using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DanLoopServer.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DanLoopServer.Services;

public sealed class AiGenerationService(HttpClient http, IDataProtectionProvider protectionProvider, StateStore store, WorkspaceService workspace)
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("remote-model-api-keys-v1");

    public async Task<AgentToolResult> GenerateAsync(Guid projectId, string capability, string prompt, string outputPath, CancellationToken cancellationToken)
    {
        if (capability is not (AiCapabilities.Image or AiCapabilities.Audio or AiCapabilities.Video or AiCapabilities.Files))
            throw new InvalidOperationException("Generation capability must be Image, Audio, Video, or Files.");
        var models = await store.ReadAsync(state => state.Models.Where(model => model.IsEnabled && AiCapabilities.Supports(model, capability))
            .OrderBy(model => model.Priority).ThenBy(model => model.Name).ToList());
        if (models.Count == 0) throw new InvalidOperationException($"No enabled AI connection supports {capability} generation.");
        var failures = new List<string>();
        foreach (var model in models)
        {
            try
            {
                var generated = await GenerateWithModelAsync(model, capability, prompt, cancellationToken);
                var relative = SafeRelative(outputPath, generated.Extension);
                var path = await workspace.ResolveProjectPathAsync(projectId, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, generated.Bytes, cancellationToken);
                await workspace.QueueAiGeneratedAsync(projectId, [relative]);
                return new AgentToolResult { Success = true, Mutated = true, Summary = $"Generated {relative}", Output = $"Saved {generated.Bytes.Length} bytes to {relative} with {model.Name}." };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{model.Name}: {ex.Message}");
            }
        }
        throw new InvalidOperationException("Every compatible generation connection failed. " + string.Join(" | ", failures));
    }

    public async Task<string> TestAsync(RemoteModel model, CancellationToken cancellationToken)
    {
        var key = RevealKey(model);
        var (url, method) = model.ProviderId.ToLowerInvariant() switch
        {
            "replicate" => (model.BaseUrl.TrimEnd('/') + "/account", HttpMethod.Get),
            "stability" => (model.BaseUrl.TrimEnd('/') + "/v1/user/account", HttpMethod.Get),
            "elevenlabs" => (model.BaseUrl.TrimEnd('/') + "/user", HttpMethod.Get),
            _ => (model.BaseUrl.TrimEnd('/'), HttpMethod.Get)
        };
        using var request = new HttpRequestMessage(method, url);
        AiRequestAuthentication.Apply(request, model, key);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw Error(response, body);
        return "Connection authentication succeeded. Generation requests will use this provider's native workflow.";
    }

    private async Task<GeneratedAsset> GenerateWithModelAsync(RemoteModel model, string capability, string prompt, CancellationToken cancellationToken)
        => model.ProviderId.ToLowerInvariant() switch
        {
            "replicate" => await GenerateReplicateAsync(model, prompt, cancellationToken),
            "fal" => await GenerateFalAsync(model, prompt, cancellationToken),
            "stability" => await GenerateStabilityAsync(model, prompt, cancellationToken),
            "elevenlabs" => await GenerateElevenLabsAsync(model, prompt, cancellationToken),
            _ when capability == AiCapabilities.Image => await GenerateOpenAiImageAsync(model, prompt, cancellationToken),
            _ => throw new InvalidOperationException($"{model.ProviderId} does not expose a configured native {capability} route.")
        };

    private async Task<GeneratedAsset> GenerateReplicateAsync(RemoteModel model, string prompt, CancellationToken cancellationToken)
    {
        if (!model.ModelId.Contains('/')) throw new InvalidOperationException("Replicate model ID must be owner/model or a version identifier.");
        var url = model.ModelId.Contains(':') ? model.BaseUrl.TrimEnd('/') + "/predictions" : model.BaseUrl.TrimEnd('/') + "/models/" + model.ModelId.Trim('/') + "/predictions";
        var payload = model.ModelId.Contains(':') ? new { version = model.ModelId, input = new { prompt } } : (object)new { input = new { prompt } };
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("Prefer", "wait=60"); Apply(model, request);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken); Ensure(response, body);
        return await ResolvePredictionAsync(model, body, cancellationToken);
    }

    private async Task<GeneratedAsset> GenerateFalAsync(RemoteModel model, string prompt, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, model.BaseUrl.TrimEnd('/') + "/" + model.ModelId.TrimStart('/')) { Content = JsonContent.Create(new { prompt }) }; Apply(model, request);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken); Ensure(response, body);
        using var initial = JsonDocument.Parse(body);
        if (TryAssetUrl(initial.RootElement, out var immediate)) return await DownloadAsync(immediate, cancellationToken);
        var responseUrl = String(initial.RootElement, "response_url");
        if (string.IsNullOrWhiteSpace(responseUrl)) throw new InvalidOperationException("fal.ai did not return a result or response URL.");
        for (var attempt = 0; attempt < 150; attempt++)
        {
            await Task.Delay(2000, cancellationToken);
            using var poll = new HttpRequestMessage(HttpMethod.Get, responseUrl); Apply(model, poll);
            using var pollResponse = await http.SendAsync(poll, cancellationToken);
            var pollBody = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
            if ((int)pollResponse.StatusCode == 202) continue;
            Ensure(pollResponse, pollBody);
            using var result = JsonDocument.Parse(pollBody);
            if (TryAssetUrl(result.RootElement, out var url)) return await DownloadAsync(url, cancellationToken);
        }
        throw new TimeoutException("fal.ai generation did not finish within five minutes.");
    }

    private async Task<GeneratedAsset> GenerateStabilityAsync(RemoteModel model, string prompt, CancellationToken cancellationToken)
    {
        var route = model.ModelId.Contains("ultra", StringComparison.OrdinalIgnoreCase) ? "ultra" : model.ModelId.Contains("core", StringComparison.OrdinalIgnoreCase) ? "core" : "ultra";
        using var content = new MultipartFormDataContent(); content.Add(new StringContent(prompt), "prompt"); content.Add(new StringContent("png"), "output_format");
        using var request = new HttpRequestMessage(HttpMethod.Post, model.BaseUrl.TrimEnd('/') + "/v2beta/stable-image/generate/" + route) { Content = content }; request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*")); Apply(model, request);
        using var response = await http.SendAsync(request, cancellationToken); var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw Error(response, System.Text.Encoding.UTF8.GetString(bytes));
        return new GeneratedAsset(bytes, Extension(response.Content.Headers.ContentType?.MediaType));
    }

    private async Task<GeneratedAsset> GenerateElevenLabsAsync(RemoteModel model, string prompt, CancellationToken cancellationToken)
    {
        var values = model.ModelId.Split('|', 2); var voice = values.Length == 2 ? values[0] : "21m00Tcm4TlvDq8ikWAM"; var modelId = values.Length == 2 ? values[1] : values[0];
        using var request = new HttpRequestMessage(HttpMethod.Post, model.BaseUrl.TrimEnd('/') + "/text-to-speech/" + Uri.EscapeDataString(voice)) { Content = JsonContent.Create(new { text = prompt, model_id = modelId }) }; request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg")); Apply(model, request);
        using var response = await http.SendAsync(request, cancellationToken); var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw Error(response, System.Text.Encoding.UTF8.GetString(bytes));
        return new GeneratedAsset(bytes, ".mp3");
    }

    private async Task<GeneratedAsset> GenerateOpenAiImageAsync(RemoteModel model, string prompt, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, model.BaseUrl.TrimEnd('/') + "/images/generations") { Content = JsonContent.Create(new { model = model.ModelId, prompt, size = "1024x1024" }) }; Apply(model, request);
        using var response = await http.SendAsync(request, cancellationToken); var body = await response.Content.ReadAsStringAsync(cancellationToken); Ensure(response, body);
        using var json = JsonDocument.Parse(body); var first = json.RootElement.GetProperty("data")[0];
        if (first.TryGetProperty("b64_json", out var encoded)) return new GeneratedAsset(Convert.FromBase64String(encoded.GetString() ?? ""), ".png");
        return await DownloadAsync(String(first, "url"), cancellationToken);
    }

    private async Task<GeneratedAsset> ResolvePredictionAsync(RemoteModel model, string body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            using var json = JsonDocument.Parse(body); var root = json.RootElement; var status = String(root, "status");
            if (status == "succeeded" && TryAssetUrl(root, out var asset)) return await DownloadAsync(asset, cancellationToken);
            if (status is "failed" or "canceled") { var error = String(root, "error"); throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Prediction {status}." : error); }
            if (!root.TryGetProperty("urls", out var urls) || string.IsNullOrWhiteSpace(String(urls, "get"))) throw new InvalidOperationException("Prediction did not return a polling URL.");
            await Task.Delay(2000, cancellationToken); using var poll = new HttpRequestMessage(HttpMethod.Get, String(urls, "get")); Apply(model, poll);
            using var response = await http.SendAsync(poll, cancellationToken); body = await response.Content.ReadAsStringAsync(cancellationToken); Ensure(response, body);
        }
        throw new TimeoutException("Generation did not finish within five minutes.");
    }

    private async Task<GeneratedAsset> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Provider returned an invalid asset URL.");
        using var response = await http.GetAsync(uri, cancellationToken); var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw Error(response, System.Text.Encoding.UTF8.GetString(bytes));
        return new GeneratedAsset(bytes, Extension(response.Content.Headers.ContentType?.MediaType));
    }

    private void Apply(RemoteModel model, HttpRequestMessage request) => AiRequestAuthentication.Apply(request, model, RevealKey(model));
    private string RevealKey(RemoteModel model) => string.IsNullOrEmpty(model.ProtectedApiKey) ? "" : _protector.Unprotect(model.ProtectedApiKey);
    private static void Ensure(HttpResponseMessage response, string body) { if (!response.IsSuccessStatusCode) throw Error(response, body); }
    private static InvalidOperationException Error(HttpResponseMessage response, string body) => new($"Provider returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 700)]}");
    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool TryAssetUrl(JsonElement root, out string url)
    {
        url = ""; if (root.TryGetProperty("output", out var output)) { if (output.ValueKind == JsonValueKind.String) url = output.GetString() ?? ""; else if (output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0) url = output[0].GetString() ?? ""; }
        foreach (var name in new[] { "images", "videos", "audios", "files" }) if (string.IsNullOrWhiteSpace(url) && root.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0) url = String(items[0], "url");
        if (string.IsNullOrWhiteSpace(url) && root.TryGetProperty("image", out var image)) url = image.ValueKind == JsonValueKind.String ? image.GetString() ?? "" : String(image, "url");
        if (string.IsNullOrWhiteSpace(url) && root.TryGetProperty("video", out var video)) url = video.ValueKind == JsonValueKind.String ? video.GetString() ?? "" : String(video, "url");
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }
    private static string SafeRelative(string requested, string extension)
    {
        var relative = requested.Replace('\\', '/').TrimStart('/'); if (relative.Split('/').Any(part => part is ".." or ".git" or ".history" or ".trash")) throw new InvalidOperationException("Generated asset path must stay inside the project.");
        if (string.IsNullOrWhiteSpace(Path.GetExtension(relative))) relative += extension; return relative;
    }
    private static string Extension(string? mediaType) => mediaType?.ToLowerInvariant() switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", "video/mp4" => ".mp4", "audio/mpeg" => ".mp3", "audio/wav" => ".wav", "application/pdf" => ".pdf", _ => ".png" };
    private sealed record GeneratedAsset(byte[] Bytes, string Extension);
}
