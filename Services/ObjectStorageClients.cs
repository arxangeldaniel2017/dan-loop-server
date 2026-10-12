using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace DanLoopServer.Services;

internal sealed record RemoteObject(string Key, long Size);

internal interface IObjectStorageClient : IAsyncDisposable
{
    Task EnsureContainerAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteObject>> ListAsync(string prefix, CancellationToken cancellationToken);
    Task<string?> DownloadTextAsync(string key, CancellationToken cancellationToken);
    Task DownloadAsync(string key, string destination, CancellationToken cancellationToken);
    Task UploadAsync(string key, string filePath, CancellationToken cancellationToken);
    Task UploadTextAsync(string key, string content, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

internal sealed class S3StorageClient(IAmazonS3 client, string bucket) : IObjectStorageClient
{
    public async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new InvalidOperationException("Bucket name is required.");
        try
        {
            await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode is "NoSuchBucket" or "NotFound")
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = true }, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RemoteObject>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var result = new List<RemoteObject>();
        string? token = null;
        do
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = token
            }, cancellationToken);
            result.AddRange((page.S3Objects ?? []).Select(item => new RemoteObject(item.Key, item.Size ?? 0)));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);
        return result;
    }

    public async Task DownloadAsync(string key, string destination, CancellationToken cancellationToken)
    {
        using var response = await client.GetObjectAsync(bucket, key, cancellationToken);
        await response.WriteResponseStreamToFileAsync(destination, false, cancellationToken);
    }

    public async Task<string?> DownloadTextAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, key, cancellationToken);
            using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode is "NoSuchKey" or "NotFound")
        {
            return null;
        }
    }

    public async Task UploadAsync(string key, string filePath, CancellationToken cancellationToken) =>
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, FilePath = filePath, AutoCloseStream = true }, cancellationToken);

    public async Task UploadTextAsync(string key, string content, CancellationToken cancellationToken) =>
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = content, ContentType = "application/json" }, cancellationToken);

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class AzureBlobStorageClient(BlobServiceClient service, string containerName) : IObjectStorageClient
{
    private BlobContainerClient Container => service.GetBlobContainerClient(containerName);

    public async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(containerName)) throw new InvalidOperationException("Container name is required.");
        await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteObject>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var result = new List<RemoteObject>();
        await foreach (var item in Container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
            result.Add(new RemoteObject(item.Name, item.Properties.ContentLength ?? 0));
        return result;
    }

    public async Task DownloadAsync(string key, string destination, CancellationToken cancellationToken) =>
        await Container.GetBlobClient(key).DownloadToAsync(destination, cancellationToken);

    public async Task<string?> DownloadTextAsync(string key, CancellationToken cancellationToken)
    {
        var blob = Container.GetBlobClient(key);
        if (!await blob.ExistsAsync(cancellationToken)) return null;
        var result = await blob.DownloadContentAsync(cancellationToken);
        return result.Value.Content.ToString();
    }

    public async Task UploadAsync(string key, string filePath, CancellationToken cancellationToken) =>
        await Container.GetBlobClient(key).UploadAsync(filePath, overwrite: true, cancellationToken);

    public async Task UploadTextAsync(string key, string content, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await Container.GetBlobClient(key).UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
        }, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await Container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static AzureBlobStorageClient Create(string endpoint, string accountName, string accountKey, string containerName)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(accountKey))
            throw new InvalidOperationException("Azure storage account name and account key are required.");
        var credential = new StorageSharedKeyCredential(accountName, accountKey);
        return new AzureBlobStorageClient(new BlobServiceClient(new Uri(endpoint), credential), containerName);
    }
}

internal sealed class BunnyStorageClient(HttpClient http, string endpoint, string storageZone, string apiKey) : IObjectStorageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageZone)) throw new InvalidOperationException("Bunny storage zone name is required.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Bunny storage-zone API password is required.");
        using var response = await SendAsync(HttpMethod.Get, "", null, cancellationToken);
        await EnsureSuccessAsync(response, "open the Bunny storage zone", cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteObject>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var results = new List<RemoteObject>();
        var pending = new Queue<string>();
        pending.Enqueue(NormalizeDirectory(prefix));
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            using var response = await SendAsync(HttpMethod.Get, directory, null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            await EnsureSuccessAsync(response, "list Bunny Storage objects", cancellationToken);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var items = await JsonSerializer.DeserializeAsync<List<BunnyObject>>(body, JsonOptions, cancellationToken) ?? [];
            foreach (var item in items)
            {
                var key = string.IsNullOrEmpty(directory) ? item.ObjectName : directory + item.ObjectName;
                if (item.IsDirectory) pending.Enqueue(NormalizeDirectory(key));
                else results.Add(new RemoteObject(key, item.Length));
            }
        }
        return results;
    }

    public async Task DownloadAsync(string key, string destination, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, key, null, cancellationToken);
        await EnsureSuccessAsync(response, "download a Bunny Storage object", cancellationToken);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await source.CopyToAsync(target, cancellationToken);
    }

    public async Task<string?> DownloadTextAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, key, null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, "download the Bunny Storage manifest", cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task UploadAsync(string key, string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, true);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(HttpMethod.Put, key, content, cancellationToken);
        await EnsureSuccessAsync(response, "upload a Bunny Storage object", cancellationToken);
    }

    public async Task UploadTextAsync(string key, string content, CancellationToken cancellationToken)
    {
        using var body = new StringContent(content, Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Put, key, body, cancellationToken);
        await EnsureSuccessAsync(response, "upload the Bunny Storage manifest", cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, "delete a Bunny Storage object", cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string key, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildUrl(key)) { Content = content };
        request.Headers.TryAddWithoutValidation("AccessKey", apiKey);
        request.Headers.UserAgent.ParseAdd("dan-loop-server/1.0");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private string BuildUrl(string key)
    {
        var encoded = string.Join('/', key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var suffix = string.IsNullOrEmpty(encoded) ? "/" : "/" + encoded + (key.EndsWith('/') ? "/" : "");
        return $"{endpoint.TrimEnd('/')}/{Uri.EscapeDataString(storageZone)}{suffix}";
    }

    private static string NormalizeDirectory(string value)
    {
        value = value.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(value) ? "" : value + "/";
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await response.Content.ReadAsStringAsync(cancellationToken);
        if (message.Length > 500) message = message[..500];
        throw new InvalidOperationException($"Could not {operation}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {message}".Trim());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class BunnyObject
    {
        public string ObjectName { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Length { get; set; }
    }
}
