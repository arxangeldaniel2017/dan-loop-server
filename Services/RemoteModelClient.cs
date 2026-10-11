using System.Net.Http.Headers;
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

    public async Task<string> CompleteAsync(RemoteModel model, string systemPrompt, string prompt, CancellationToken cancellationToken)
    {
        var url = model.BaseUrl.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(model.ProtectedApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _protector.Unprotect(model.ProtectedApiKey));
        request.Content = JsonContent.Create(new
        {
            model = model.ModelId,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            }
        });
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Remote model returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1000)]}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
