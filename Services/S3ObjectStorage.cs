using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using DanLoopServer.Models;
using Microsoft.AspNetCore.DataProtection;

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
    private readonly StateStore _store;
    private readonly IDataProtector _protector;
    private readonly string _root;
    private string? _bucket;
    private string _prefix = "dan-loop/projects";
    private IAmazonS3? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, StoredFile> _manifest = new(StringComparer.Ordinal);
    private bool _initialized;

    public S3ObjectStorage(IConfiguration configuration, StateStore store, IDataProtectionProvider protectionProvider, ILogger<S3ObjectStorage> logger)
    {
        _logger = logger;
        _store = store;
        _protector = protectionProvider.CreateProtector("object-storage-credentials-v1");
        _root = Path.GetFullPath(configuration["Storage:ProjectsPath"] ?? "/data/projects");
    }

    public bool IsEnabled => _client is not null && _bucket is not null;

    public string ProtectSecret(string value) => string.IsNullOrWhiteSpace(value) ? "" : _protector.Protect(value.Trim());

    public async Task InitializeAsync(CancellationToken cancellationToken = default) => await RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await _store.ReadAsync(state => state.ObjectStorageConnections.FirstOrDefault(item => item.IsEnabled));
            _client?.Dispose();
            _client = null;
            _bucket = null;
            _prefix = "dan-loop/projects";
            _manifest = new(StringComparer.Ordinal);
            _initialized = false;
            if (connection is null)
            {
                _logger.LogInformation("No active saved S3-compatible storage connection; local project storage is active");
                return;
            }

            _client = CreateClient(connection);
            _bucket = connection.Bucket.Trim();
            _prefix = (string.IsNullOrWhiteSpace(connection.Prefix) ? "dan-loop/projects" : connection.Prefix).Trim('/');
            await EnsureBucketAsync(_client, _bucket, connection.Region, cancellationToken);
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
            _logger.LogInformation("Saved S3-compatible project storage enabled for bucket {Bucket} with prefix {Prefix}", _bucket, _prefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not initialize the active saved S3-compatible storage connection; local storage remains available");
            _client?.Dispose();
            _client = null;
            _bucket = null;
            _initialized = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> TestAsync(ObjectStorageConnection connection, CancellationToken cancellationToken)
    {
        using var client = CreateClient(connection);
        await EnsureBucketAsync(client, connection.Bucket.Trim(), connection.Region, cancellationToken);
        var result = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = connection.Bucket.Trim(), MaxKeys = 1 }, cancellationToken);
        return $"Connected to bucket '{connection.Bucket.Trim()}'.";
    }

    private IAmazonS3 CreateClient(ObjectStorageConnection connection)
    {
        var endpoint = string.IsNullOrWhiteSpace(connection.Endpoint) ? null : connection.Endpoint.Trim();
        var region = string.IsNullOrWhiteSpace(connection.Region) ? InferSpacesRegion(endpoint) ?? "us-east-1" : connection.Region.Trim();
        var config = new AmazonS3Config
        {
            AuthenticationRegion = region,
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            ForcePathStyle = connection.ForcePathStyle
        };
        if (endpoint is not null)
        {
            config.ServiceURL = NormalizeEndpoint(endpoint);
            config.RegionEndpoint = null;
        }
        var accessKey = Unprotect(connection.ProtectedAccessKey);
        var secretKey = Unprotect(connection.ProtectedSecretKey);
        var sessionToken = Unprotect(connection.ProtectedSessionToken);
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Access key and secret key are required.");
        AWSCredentials credentials = string.IsNullOrWhiteSpace(sessionToken)
            ? new BasicAWSCredentials(accessKey, secretKey)
            : new SessionAWSCredentials(accessKey, secretKey, sessionToken);
        return new AmazonS3Client(credentials, config);
    }

    private string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return _protector.Unprotect(value); }
        catch { throw new InvalidOperationException("The saved object-storage credentials could not be decrypted."); }
    }

    private static async Task EnsureBucketAsync(IAmazonS3 client, string bucket, string region, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new InvalidOperationException("Bucket name is required.");
        if (await AmazonS3Util.DoesS3BucketExistV2Async(client, bucket)) return;
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = true }, cancellationToken);
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

    private string ObjectKey(string relativePath)
    {
        if (relativePath == ManifestName) return string.IsNullOrEmpty(_prefix) ? relativePath : $"{_prefix}/{relativePath}";
        var parts = relativePath.Replace('\\', '/').Split('/', 2);
        var projectNamespace = SafeObjectSegment(parts[0]);
        var objectPath = parts.Length == 1 ? projectNamespace : $"{projectNamespace}/{parts[1]}";
        return string.IsNullOrEmpty(_prefix) ? objectPath : $"{_prefix}/{objectPath}";
    }
    private string PrefixWithSlash() => string.IsNullOrEmpty(_prefix) ? "" : _prefix + "/";

    private static async Task<string> HashAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string NormalizeEndpoint(string endpoint) => endpoint.Contains("://", StringComparison.Ordinal) ? endpoint.TrimEnd('/') : "https://" + endpoint.TrimEnd('/');
    private static string? InferSpacesRegion(string? endpoint)
    {
        if (endpoint is null || !endpoint.Contains("digitaloceanspaces.com", StringComparison.OrdinalIgnoreCase)) return null;
        var host = Uri.TryCreate(NormalizeEndpoint(endpoint), UriKind.Absolute, out var uri) ? uri.Host : endpoint;
        return host.Split('.')[0];
    }

    private static string SafeObjectSegment(string value)
    {
        var ascii = new string(value.Normalize(System.Text.NormalizationForm.FormD)
            .Where(character => character <= 127 && char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')
            .ToArray());
        var slug = System.Text.RegularExpressions.Regex.Replace(ascii, "-+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
            slug = "project-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..10].ToLowerInvariant();
        return slug[..Math.Min(50, slug.Length)];
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
