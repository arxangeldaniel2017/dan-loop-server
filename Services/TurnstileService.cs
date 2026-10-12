using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DanLoopServer.Services;

public sealed class TurnstileService(HttpClient http, IConfiguration configuration, ILogger<TurnstileService> logger)
{
    private readonly string _siteKey = configuration["Security:Turnstile:SiteKey"]?.Trim() ?? "";
    private readonly string _secretKey = configuration["Security:Turnstile:SecretKey"]?.Trim() ?? "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_siteKey) && !string.IsNullOrWhiteSpace(_secretKey);
    public string SiteKey => IsConfigured ? _siteKey : "";

    public async Task<bool> VerifyAsync(string? token, string ipAddress, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return true;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = _secretKey,
                ["response"] = token,
                ["remoteip"] = ipAddress
            });
            using var response = await http.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", content, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<TurnstileVerification>(cancellationToken: cancellationToken);
            return response.IsSuccessStatusCode && result?.Success == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Turnstile verification could not be completed");
            return false;
        }
    }

    private sealed class TurnstileVerification
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
    }
}
