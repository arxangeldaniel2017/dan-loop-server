using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace DanLoopServer.Services;

/// <summary>
/// Keeps the local project workspace as a compatibility cache while mirroring durable
/// project files to any S3-compatible service, including DigitalOcean Spaces.
/// </summary>
public sealed class S3ObjectStorage : IAsyncDisposable
{
    private const string ManifestName = ".dan-loop-storage-manifest.json";
    private static readonly string[] InternalFolders = [".downloads", ".git", ".history", ".staging", ".trash"];
    private readonly ILogger<S3ObjectStorage> _logger;
    private readonly string _root;
    private readonly string? _bucket;
    private readonly string _prefix;
    private readonly IAmazonS3? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, StoredFile> _manifest = new(StringComparer.Ordinal);
    private bool _initialized;

    public S3ObjectStorage(IConfiguration configuration, ILogger<S3ObjectStorage> logger)
    {
        _logger = logger;
        _root = Path.GetFullPath(configuration["Storage:ProjectsPath"] ?? "/data/projects");
        _bucket = First(configuration["S3:Bucket"], configuration["S3_BUCKET"], configuration["S3_BUCKET_NAME"], configuration["AWS_BUCKET_NAME"], configuration["SPACES_BUCKET"], configuration["SPACES_BUCKET_NAME"]);
        _prefix = (First(configuration["S3:Prefix"], configuration["S3_PREFIX"], configuration["SPACES_PREFIX"]) ?? "dan-loop/projects").Trim('/');
        if (_bucket is null) return;

        var endpoint = First(configuration["S3:Endpoint"], configuration["S3_ENDPOINT"], configuration["AWS_ENDPOINT_URL_S3"], configuration["SPACES_ENDPOINT"]);
        var region = First(configuration["S3:Region"], configuration["S3_REGION"], configuration["AWS_REGION"], configuration["AWS_DEFAULT_REGION"], configuration["SPACES_REGION"])
                     ?? InferSpacesRegion(endpoint) ?? "us-east-1";
        var accessKey = First(configuration["S3:AccessKey"], configuration["S3_ACCESS_KEY"], configuration["S3_ACCESS_KEY_ID"], configuration["SPACES_KEY"], configuration["SPACES_ACCESS_KEY"], configuration["SPACES_ACCESS_KEY_ID"], configuration["AWS_ACCESS_KEY_ID"]);
        var secretKey = First(configuration["S3:SecretKey"], configuration["S3_SECRET_KEY"], configuration["S3_SECRET_ACCESS_KEY"], configuration["SPACES_SECRET"], configuration["SPACES_SECRET_KEY"], configuration["SPACES_SECRET_ACCESS_KEY"], configuration["AWS_SECRET_ACCESS_KEY"]);
        var sessionToken = First(configuration["S3:SessionToken"], configuration["S3_SESSION_TOKEN"], configuration["AWS_SESSION_TOKEN"]);
        if ((accessKey is null) != (secretKey is null))
            throw new InvalidOperationException("S3 object storage requires both access-key and secret-key values.");

        var s3Config = new AmazonS3Config
        {
            AuthenticationRegion = region,
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            ForcePathStyle = ParseBool(First(configuration["S3:ForcePathStyle"], configuration["S3_FORCE_PATH_STYLE"]))
        };
        if (endpoint is not null)
        {
            s3Config.ServiceURL = NormalizeEndpoint(endpoint);
            // RegionEndpoint causes the AWS hostname to win over a custom endpoint in some SDK versions.
            s3Config.RegionEndpoint = null;
        }

        var credentials = accessKey is null
            ? null
            : sessionToken is null
                ? (AWSCredentials)new BasicAWSCredentials(accessKey, secretKey)
                : new SessionAWSCredentials(accessKey, secretKey, sessionToken);
        _client = credentials is not null
            ? new AmazonS3Client(credentials, s3Config)
            : new AmazonS3Client(s3Config);
    }

    public bool IsEnabled => _client is not null && _bucket is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return;
        Directory.CreateDirectory(_root);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _manifest = await ReadManifestAsync(cancellationToken);
            if (_manifest.Count == 0)
                _manifest = await DiscoverRemoteFilesAsync(cancellationToken);

            foreach (var (relativePath, stored) in _manifest.ToList())
            {
                var destination = LocalPath(relativePath);
                if (File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadAsync(stored.Key, destination, cancellationToken);
            }
            _initialized = true;
            await SyncUnsafeAsync(cancellationToken);
            _logger.LogInformation("S3-compatible project storage enabled for bucket {Bucket} with prefix {Prefix}", _bucket, _prefix);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Could not initialize configured S3-compatible project storage");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || !_initialized) return;
        await _gate.WaitAsync(cancellationToken);
        try { await SyncUnsafeAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task SyncUnsafeAsync(CancellationToken cancellationToken)
    {
        var localFiles = EnumerateLocalFiles().ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var changed = false;
        foreach (var (relativePath, local) in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = await HashAsync(local.FullPath, cancellationToken);
            if (_manifest.TryGetValue(relativePath, out var existing) &&
                existing.Size == local.Size && existing.Sha256 == hash) continue;

            var key = ObjectKey(relativePath);
            await UploadAsync(key, local.FullPath, cancellationToken);
            _manifest[relativePath] = new StoredFile(key, local.Size, hash);
            changed = true;
        }

        foreach (var relativePath in _manifest.Keys.Except(localFiles.Keys, StringComparer.Ordinal).ToList())
        {
            await _client!.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = _manifest[relativePath].Key }, cancellationToken);
            _manifest.Remove(relativePath);
            changed = true;
        }

        if (changed) await WriteManifestAsync(cancellationToken);
    }

    private IEnumerable<LocalFile> EnumerateLocalFiles()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
            var firstSegment = relative.Split('/', 2)[0];
            if (InternalFolders.Contains(firstSegment, StringComparer.OrdinalIgnoreCase) ||
                relative.Split('/').Any(segment => InternalFolders.Contains(segment, StringComparer.OrdinalIgnoreCase)) ||
                File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
            var info = new FileInfo(file);
            yield return new LocalFile(relative, file, info.Length);
        }
    }

    private async Task<Dictionary<string, StoredFile>> ReadManifestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client!.GetObjectAsync(_bucket, ObjectKey(ManifestName), cancellationToken);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, StoredFile>>(response.ResponseStream, cancellationToken: cancellationToken)
                   ?? new(StringComparer.Ordinal);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode is "NoSuchKey" or "NotFound")
        {
            return new(StringComparer.Ordinal);
        }
    }

    private async Task<Dictionary<string, StoredFile>> DiscoverRemoteFilesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, StoredFile>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await _client!.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = PrefixWithSlash(),
                ContinuationToken = token
            }, cancellationToken);
            foreach (var item in page.S3Objects)
            {
                var relative = item.Key[PrefixWithSlash().Length..];
                if (string.IsNullOrWhiteSpace(relative) || relative == ManifestName || !IsSafeRelative(relative)) continue;
                result[relative] = new StoredFile(item.Key, item.Size ?? 0, "");
            }
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);
        return result;
    }

    private async Task UploadAsync(string key, string filePath, CancellationToken cancellationToken)
    {
        await _client!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = filePath,
            AutoCloseStream = true
        }, cancellationToken);
    }

    private async Task DownloadAsync(string key, string destination, CancellationToken cancellationToken)
    {
        using var response = await _client!.GetObjectAsync(_bucket, key, cancellationToken);
        await response.WriteResponseStreamToFileAsync(destination, false, cancellationToken);
    }

    private async Task WriteManifestAsync(CancellationToken cancellationToken)
    {
        await _client!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = ObjectKey(ManifestName),
            ContentBody = JsonSerializer.Serialize(_manifest)
        }, cancellationToken);
    }

    private string LocalPath(string relativePath)
    {
        if (!IsSafeRelative(relativePath)) throw new InvalidDataException("Unsafe object path in S3 storage.");
        var full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("S3 object path escaped the projects directory.");
        return full;
    }

    private static bool IsSafeRelative(string value) =>
        !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) &&
        !value.Split('/', '\\').Any(segment => segment is "" or "." or "..");

    private string ObjectKey(string relativePath) => string.IsNullOrEmpty(_prefix) ? relativePath : $"{_prefix}/{relativePath}";
    private string PrefixWithSlash() => string.IsNullOrEmpty(_prefix) ? "" : _prefix + "/";

    private static async Task<string> HashAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    private static bool ParseBool(string? value) => bool.TryParse(value, out var parsed) && parsed;
    private static string NormalizeEndpoint(string endpoint) => endpoint.Contains("://", StringComparison.Ordinal) ? endpoint.TrimEnd('/') : "https://" + endpoint.TrimEnd('/');
    private static string? InferSpacesRegion(string? endpoint)
    {
        if (endpoint is null || !endpoint.Contains("digitaloceanspaces.com", StringComparison.OrdinalIgnoreCase)) return null;
        var host = Uri.TryCreate(NormalizeEndpoint(endpoint), UriKind.Absolute, out var uri) ? uri.Host : endpoint;
        return host.Split('.')[0];
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) _client.Dispose();
        _gate.Dispose();
        await ValueTask.CompletedTask;
    }

    private sealed record StoredFile(string Key, long Size, string Sha256);
    private sealed record LocalFile(string RelativePath, string FullPath, long Size);
}
