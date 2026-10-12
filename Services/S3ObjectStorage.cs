using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using DanLoopServer.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DanLoopServer.Services;

/// <summary>
/// Keeps the local project workspace as a compatibility cache while mirroring durable
/// project files to the active S3-compatible, Azure Blob, or Bunny Storage connection.
/// </summary>
public sealed class S3ObjectStorage : IAsyncDisposable
{
    private const string ManifestName = ".dan-loop-storage-manifest.json";
    private static readonly string[] InternalFolders = [".downloads", ".git", ".history", ".staging", ".trash"];
    private readonly ILogger<S3ObjectStorage> _logger;
    private readonly StateStore _store;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _root;
    private string? _bucket;
    private string _prefix = "dan-loop/projects";
    private IObjectStorageClient? _client;
    private string _provider = "";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, StoredFile> _manifest = new(StringComparer.Ordinal);
    private bool _initialized;

    public S3ObjectStorage(IConfiguration configuration, StateStore store, IDataProtectionProvider protectionProvider, IHttpClientFactory httpClientFactory, ILogger<S3ObjectStorage> logger)
    {
        _logger = logger;
        _store = store;
        _protector = protectionProvider.CreateProtector("object-storage-credentials-v1");
        _httpClientFactory = httpClientFactory;
        _root = Path.GetFullPath(configuration["Storage:ProjectsPath"] ?? "/data/projects");
    }

    public bool IsEnabled => _client is not null && _bucket is not null;
    public string ActiveProvider => IsEnabled ? _provider : "Local storage";

    public string ProtectSecret(string value) => string.IsNullOrWhiteSpace(value) ? "" : _protector.Protect(value.Trim());

    public async Task InitializeAsync(CancellationToken cancellationToken = default) => await RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await _store.ReadAsync(state => state.ObjectStorageConnections.FirstOrDefault(item => item.IsEnabled));
            if (_client is not null) await _client.DisposeAsync();
            _client = null;
            _bucket = null;
            _provider = "";
            _prefix = "dan-loop/projects";
            _manifest = new(StringComparer.Ordinal);
            _initialized = false;
            if (connection is null)
            {
                _logger.LogInformation("No active saved object-storage connection; local project storage is active");
                return;
            }

            _client = CreateClient(connection);
            _bucket = connection.Bucket.Trim();
            _provider = connection.Provider;
            _prefix = (string.IsNullOrWhiteSpace(connection.Prefix) ? "dan-loop/projects" : connection.Prefix).Trim('/');
            await _client.EnsureContainerAsync(cancellationToken);
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
            _logger.LogInformation("Saved {Provider} project storage enabled for container {Bucket} with prefix {Prefix}", _provider, _bucket, _prefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not initialize the active saved object-storage connection; local storage remains available");
            if (_client is not null) await _client.DisposeAsync();
            _client = null;
            _bucket = null;
            _provider = "";
            _initialized = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> TestAsync(ObjectStorageConnection connection, CancellationToken cancellationToken)
    {
        await using var client = CreateClient(connection);
        await client.EnsureContainerAsync(cancellationToken);
        var containerLabel = ObjectStorageProviders.IsAzure(connection.Provider) ? "container" : ObjectStorageProviders.IsBunny(connection.Provider) ? "storage zone" : "bucket";
        return $"Connected to {connection.Provider} {containerLabel} '{connection.Bucket.Trim()}'.";
    }

    private IObjectStorageClient CreateClient(ObjectStorageConnection connection)
    {
        if (!ObjectStorageProviders.All.Contains(connection.Provider))
            throw new InvalidOperationException($"Unknown object-storage provider '{connection.Provider}'.");
        var accessKey = Unprotect(connection.ProtectedAccessKey);
        var secretKey = Unprotect(connection.ProtectedSecretKey);
        var sessionToken = Unprotect(connection.ProtectedSessionToken);
        var region = string.IsNullOrWhiteSpace(connection.Region) ? ObjectStorageProviders.DefaultRegion(connection.Provider) : connection.Region.Trim();
        var resolvedEndpoint = ObjectStorageProviders.ResolveEndpoint(connection.Provider, connection.Endpoint, region, accessKey);
        if (ObjectStorageProviders.EndpointIsRequired(connection.Provider) && string.IsNullOrWhiteSpace(resolvedEndpoint))
            throw new InvalidOperationException($"An endpoint is required for {connection.Provider}.");

        if (ObjectStorageProviders.IsAzure(connection.Provider))
            return AzureBlobStorageClient.Create(NormalizeEndpoint(resolvedEndpoint), accessKey, secretKey, connection.Bucket.Trim());
        if (ObjectStorageProviders.IsBunny(connection.Provider))
            return new BunnyStorageClient(_httpClientFactory.CreateClient(nameof(BunnyStorageClient)), NormalizeEndpoint(resolvedEndpoint), connection.Bucket.Trim(), accessKey);

        var endpoint = string.IsNullOrWhiteSpace(resolvedEndpoint) ? null : resolvedEndpoint;
        var config = new AmazonS3Config
        {
            AuthenticationRegion = region,
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            ForcePathStyle = connection.ForcePathStyle || ObjectStorageProviders.DefaultForcePathStyle(connection.Provider)
        };
        if (endpoint is not null)
        {
            config.ServiceURL = NormalizeEndpoint(endpoint);
            // Many S3-compatible services do not implement the SDK's optional modern
            // checksum extensions. SigV4 still protects these requests and responses.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Access key and secret key are required.");
        AWSCredentials credentials = string.IsNullOrWhiteSpace(sessionToken)
            ? new BasicAWSCredentials(accessKey, secretKey)
            : new SessionAWSCredentials(accessKey, secretKey, sessionToken);
        return new S3StorageClient(new AmazonS3Client(credentials, config), connection.Bucket.Trim());
    }

    private string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return _protector.Unprotect(value); }
        catch { throw new InvalidOperationException("The saved object-storage credentials could not be decrypted."); }
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
            await _client!.DeleteAsync(_manifest[relativePath].Key, cancellationToken);
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
            var json = await _client!.DownloadTextAsync(ObjectKey(ManifestName), cancellationToken);
            return string.IsNullOrWhiteSpace(json)
                ? new(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, StoredFile>>(json) ?? new(StringComparer.Ordinal);
        }
        catch (JsonException ex) { throw new InvalidDataException("The remote object-storage manifest is invalid.", ex); }
    }

    private async Task<Dictionary<string, StoredFile>> DiscoverRemoteFilesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, StoredFile>(StringComparer.Ordinal);
        var objects = await _client!.ListAsync(PrefixWithSlash(), cancellationToken);
        foreach (var item in objects)
        {
            var relative = item.Key[PrefixWithSlash().Length..];
            if (string.IsNullOrWhiteSpace(relative) || relative == ManifestName || !IsSafeRelative(relative)) continue;
            result[relative] = new StoredFile(item.Key, item.Size, "");
        }
        return result;
    }

    private async Task UploadAsync(string key, string filePath, CancellationToken cancellationToken)
    {
        await _client!.UploadAsync(key, filePath, cancellationToken);
    }

    private async Task DownloadAsync(string key, string destination, CancellationToken cancellationToken)
    {
        await _client!.DownloadAsync(key, destination, cancellationToken);
    }

    private async Task WriteManifestAsync(CancellationToken cancellationToken)
    {
        await _client!.UploadTextAsync(ObjectKey(ManifestName), JsonSerializer.Serialize(_manifest), cancellationToken);
    }

    private string LocalPath(string relativePath)
    {
        if (!IsSafeRelative(relativePath)) throw new InvalidDataException("Unsafe object path in remote storage.");
        var full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Remote object path escaped the projects directory.");
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
        if (_client is not null) await _client.DisposeAsync();
        _gate.Dispose();
    }

    private sealed record StoredFile(string Key, long Size, string Sha256);
    private sealed record LocalFile(string RelativePath, string FullPath, long Size);
}
