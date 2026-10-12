using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DanLoopServer.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DanLoopServer.Services;

public sealed class AiModelDiscoveryService(HttpClient http, IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("remote-model-api-keys-v1");
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    public async Task<AiModelDiscoveryResult> DiscoverAsync(AiProviderCatalogItem provider, string baseUrl, string apiKey,
        string? authenticationType, string? apiKeyHeaderName, bool forceRefresh, CancellationToken cancellationToken)
    {
        baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? provider.BaseUrl : baseUrl).TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Enter a valid provider endpoint before loading models.");
        var credentialFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..16];
        var cacheKey = $"{provider.Id}|{baseUrl}|{credentialFingerprint}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CreatedAt < CacheDuration)
            return Merge(provider, cached.Models, true);
        var model = new RemoteModel { ProviderId = provider.Id, BaseUrl = baseUrl };
        AiConnectionCatalogService.ApplyProviderAuthentication([provider], model, authenticationType, apiKeyHeaderName);
        var discovered = await RequestModelsAsync(provider, model, apiKey, cancellationToken);
        _cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow, discovered);
        return Merge(provider, discovered, false);
    }

    public Task<AiModelDiscoveryResult> DiscoverSavedAsync(AiProviderCatalogItem provider, RemoteModel saved,
        bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(saved.ProtectedApiKey) ? "" : _protector.Unprotect(saved.ProtectedApiKey);
        return DiscoverAsync(provider, saved.BaseUrl, key, saved.AuthenticationType, saved.ApiKeyHeaderName, forceRefresh, cancellationToken);
    }

    private async Task<List<AiModelCatalogItem>> RequestModelsAsync(AiProviderCatalogItem provider, RemoteModel model, string apiKey, CancellationToken cancellationToken)
    {
        var urls = CandidateUrls(provider, model.BaseUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var failures = new List<string>();
        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AiRequestAuthentication.Apply(request, model, apiKey);
                if (provider.Id.Equals("anthropic", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                using var response = await http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode) { failures.Add($"HTTP {(int)response.StatusCode} from {new Uri(url).AbsolutePath}"); continue; }
                var models = ParseModels(body, provider);
                if (models.Count > 0) return models;
                failures.Add($"No model identifiers were returned by {new Uri(url).AbsolutePath}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(ex.Message); }
        }
        var detail = failures.Count == 0 ? "This provider does not expose a model-list endpoint." : failures[0];
        throw new InvalidOperationException($"Live model discovery is unavailable for {provider.Name}: {detail} You can still enter a model ID manually.");
    }

    private static IEnumerable<string> CandidateUrls(AiProviderCatalogItem provider, string baseUrl)
    {
        if (provider.Id.Equals("stability", StringComparison.OrdinalIgnoreCase)) { yield return baseUrl.TrimEnd('/') + "/v1/engines/list"; yield break; }
        yield return baseUrl.TrimEnd('/') + "/models";
    }

    private static List<AiModelCatalogItem> ParseModels(string body, AiProviderCatalogItem provider)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var candidates = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array) candidates.AddRange(root.EnumerateArray());
        else if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in new[] { "data", "models", "results", "items" })
                if (root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array) { candidates.AddRange(list.EnumerateArray()); break; }
        var models = new List<AiModelCatalogItem>();
        foreach (var item in candidates)
        {
            if (item.ValueKind == JsonValueKind.String) { Add(item.GetString(), null); continue; }
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(item, "id", "model_id", "modelId", "model", "slug");
            if (string.IsNullOrWhiteSpace(id))
            {
                var owner = ReadString(item, "owner"); var name = ReadString(item, "name");
                id = string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name) ? name : $"{owner}/{name}";
            }
            Add(id, ReadString(item, "display_name", "displayName", "name"));
        }
        return models.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        void Add(string? id, string? name)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            id = id.Trim(); if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) id = id[7..];
            models.Add(new AiModelCatalogItem { Id = id, Name = string.IsNullOrWhiteSpace(name) ? FriendlyName(id) : name.Trim(), Capabilities = AiConnectionCatalogService.SuggestedCapabilities([provider], provider.Id, id) });
        }
    }

    private static string? ReadString(JsonElement item, params string[] names)
    {
        foreach (var name in names) if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static string FriendlyName(string id)
    {
        var value = id.Split('/').Last().Replace('-', ' ').Replace('_', ' ');
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word.Length <= 3 ? word.ToUpperInvariant() : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static AiModelDiscoveryResult Merge(AiProviderCatalogItem provider, IReadOnlyList<AiModelCatalogItem> discovered, bool cached)
    {
        var builtInCount = provider.Models.Count(item => !item.Id.Equals("custom", StringComparison.OrdinalIgnoreCase));
        var merged = provider.Models.Where(item => !item.Id.Equals("custom", StringComparison.OrdinalIgnoreCase)).Concat(discovered)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var catalog = group.First(); var live = group.Last();
                return new AiModelCatalogItem { Id = live.Id, Name = string.IsNullOrWhiteSpace(live.Name) ? catalog.Name : live.Name, Capabilities = catalog.Capabilities.Count > 0 ? catalog.Capabilities : live.Capabilities };
            }).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new AiModelDiscoveryResult(merged, discovered.Count, $"Loaded {discovered.Count} live model{(discovered.Count == 1 ? "" : "s")} and merged them with {builtInCount} built-in choice{(builtInCount == 1 ? "" : "s")}.", cached);
    }

    private sealed record CacheEntry(DateTimeOffset CreatedAt, List<AiModelCatalogItem> Models);
}

public sealed record AiModelDiscoveryResult(List<AiModelCatalogItem> Models, int DiscoveredCount, string Message, bool Cached);
