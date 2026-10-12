using System.Net.Http.Json;
using System.Text.Json;
using DanLoopServer.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DanLoopServer.Services;

public sealed class RemoteModelClient(HttpClient http, IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("remote-model-api-keys-v1");

    public string ProtectKey(string apiKey) => string.IsNullOrWhiteSpace(apiKey) ? "" : _protector.Protect(apiKey.Trim());

    public string KeyHint(string protectedKey)
    {
        if (string.IsNullOrEmpty(protectedKey)) return "none";
        try
        {
            var key = _protector.Unprotect(protectedKey);
            return key.Length <= 4 ? "configured" : $"••••{key[^4..]}";
        }
        catch { return "unavailable"; }
    }

    public async Task<string> TestAsync(RemoteModel model, CancellationToken cancellationToken)
    {
        if (AnthropicProtocol.Handles(model))
        {
            var messages = new object[] { new { role = "user", content = "Reply with OK." } };
            using var anthropicRequest = AnthropicProtocol.CreateRequest(model, RevealKey(model), messages, null, null, 12);
            using var anthropicResponse = await http.SendAsync(anthropicRequest, cancellationToken);
            var anthropicBody = await anthropicResponse.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(anthropicResponse, anthropicBody);
            _ = AnthropicProtocol.ParseStep(anthropicBody);
            return "Connection and native Anthropic model request succeeded.";
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, model.BaseUrl.TrimEnd('/') + "/chat/completions");
        AiRequestAuthentication.Apply(request, model, string.IsNullOrEmpty(model.ProtectedApiKey) ? "" : _protector.Unprotect(model.ProtectedApiKey));
        request.Content = JsonContent.Create(new
        {
            model = model.ModelId,
            messages = new[] { new { role = "user", content = "Reply with OK." } },
            max_tokens = 12,
            stream = false
        });
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}");
        return "Connection and model request succeeded.";
    }

    public async Task<string> CompleteAsync(RemoteModel model, string systemPrompt, IReadOnlyList<ConversationMessage> conversation, string currentPrompt, IReadOnlyList<string> imageDataUrls, CancellationToken cancellationToken)
    {
        var url = model.BaseUrl.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AiRequestAuthentication.Apply(request, model, string.IsNullOrEmpty(model.ProtectedApiKey) ? "" : _protector.Unprotect(model.ProtectedApiKey));
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var message in conversation)
        {
            var isCurrent = message.JobId == conversation.LastOrDefault()?.JobId && message.Role == "user";
            if (isCurrent && imageDataUrls.Count > 0)
            {
                var content = new List<object> { new { type = "text", text = currentPrompt } };
                content.AddRange(imageDataUrls.Select(url => (object)new { type = "image_url", image_url = new { url } }));
                messages.Add(new { role = message.Role, content = (object)content });
            }
            else messages.Add(new { role = message.Role, content = (object)(isCurrent ? currentPrompt : message.Content) });
        }
        var payload = new
        {
            model = model.ModelId,
            messages
        };
        if (AnthropicProtocol.Handles(model))
        {
            using var anthropicRequest = AnthropicProtocol.CreateRequest(model, RevealKey(model), messages, null, null, 16_384);
            using var anthropicResponse = await http.SendAsync(anthropicRequest, cancellationToken);
            var anthropicBody = await anthropicResponse.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(anthropicResponse, anthropicBody);
            return AnthropicProtocol.ParseStep(anthropicBody).Content;
        }
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Remote model returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1000)]}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private string RevealKey(RemoteModel model) => string.IsNullOrEmpty(model.ProtectedApiKey) ? "" : _protector.Unprotect(model.ProtectedApiKey);
    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Remote model returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1000)]}");
    }
}
