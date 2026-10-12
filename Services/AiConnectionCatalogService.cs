using System.Text.Json;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class AiConnectionCatalogService(StateStore store, IWebHostEnvironment environment, ILogger<AiConnectionCatalogService> logger)
{
    private readonly string _path = Path.Combine(environment.ContentRootPath, "Data", "ai-connections.json");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) throw new FileNotFoundException("AI connection catalog is missing.", _path);
        await using var stream = File.OpenRead(_path);
        var catalog = await JsonSerializer.DeserializeAsync<List<AiProviderCatalogItem>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken) ?? [];
        await store.UpdateAsync(state =>
        {
            state.AiProviderCatalog = catalog;
            foreach (var model in state.Models)
            {
                if (model.ProviderId == "custom") model.ProviderId = InferProvider(catalog, model.BaseUrl);
                ApplyProviderAuthentication(catalog, model);
                var suggested = SuggestedCapabilities(catalog, model.ProviderId, model.ModelId);
                if (suggested.Count > 0) model.SuggestedCapabilities = suggested;
                model.Capabilities = NormalizeSelectedCapabilities(catalog, model.ProviderId, model.ModelId, model.Capabilities);
            }
            return true;
        });
        logger.LogInformation("Loaded {ProviderCount} AI providers and {ModelCount} predefined models from {Path}",
            catalog.Count, catalog.Sum(provider => provider.Models.Count), _path);
    }

    public static List<string> SuggestedCapabilities(IEnumerable<AiProviderCatalogItem> catalog, string providerId, string modelId)
    {
        var provider = catalog.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        var model = provider?.Models.FirstOrDefault(item => item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (model is not null) return model.Capabilities.Where(AiCapabilities.All.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var value = modelId.ToLowerInvariant();
        if (value.Contains("image") || value.Contains("flux") || value.Contains("stable-diffusion")) return [AiCapabilities.Generator, AiCapabilities.Image, AiCapabilities.Files];
        if (value.Contains("audio") || value.Contains("whisper") || value.Contains("tts")) return [AiCapabilities.Generator, AiCapabilities.Audio, AiCapabilities.Files];
        if (value.Contains("video") || value.Contains("sora")) return [AiCapabilities.Generator, AiCapabilities.Video, AiCapabilities.Files];
        if (value.Contains("coder") || value.Contains("codestral") || value.Contains("code")) return [AiCapabilities.Coder, AiCapabilities.ToolCalling, AiCapabilities.Generator, AiCapabilities.Files];
        var providerCapabilities = provider?.Models.SelectMany(item => item.Capabilities).Where(AiCapabilities.All.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return providerCapabilities is { Count: > 0 } ? providerCapabilities : [AiCapabilities.Planner, AiCapabilities.Coder, AiCapabilities.Orchestrator, AiCapabilities.ToolCalling, AiCapabilities.Generator, AiCapabilities.Files];
    }

    public static List<string> NormalizeSelectedCapabilities(IEnumerable<AiProviderCatalogItem> catalog, string providerId, string modelId, IEnumerable<string>? selected)
    {
        var values = (selected ?? []).Where(AiCapabilities.All.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var provider = catalog.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        var exact = provider?.Models.FirstOrDefault(item => item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (exact is null) return values;
        return values.Where(value => exact.Capabilities.Contains(value, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public static void ApplyProviderAuthentication(IEnumerable<AiProviderCatalogItem> catalog, RemoteModel model, string? requestedType = null, string? requestedHeader = null)
    {
        var provider = catalog.FirstOrDefault(item => item.Id.Equals(model.ProviderId, StringComparison.OrdinalIgnoreCase));
        model.AuthenticationType = provider?.AllowsAuthenticationSelection == true
            ? AiAuthenticationTypes.Normalize(requestedType ?? model.AuthenticationType)
            : AiAuthenticationTypes.Normalize(provider?.AuthenticationType ?? model.AuthenticationType);
        var header = provider?.AllowsAuthenticationSelection == true ? requestedHeader : provider?.ApiKeyHeaderName;
        model.ApiKeyHeaderName = model.AuthenticationType == AiAuthenticationTypes.ApiKeyHeader
            ? (string.IsNullOrWhiteSpace(header) ? "api-key" : header.Trim()) : "Authorization";
    }

    private static string InferProvider(IEnumerable<AiProviderCatalogItem> catalog, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var configured)) return "custom";
        return catalog.FirstOrDefault(provider => Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var known) &&
            configured.Host.Equals(known.Host, StringComparison.OrdinalIgnoreCase))?.Id
            ?? (configured.Host.Contains("runpod", StringComparison.OrdinalIgnoreCase) ? "runpod" : "custom");
    }
}
