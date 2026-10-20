using System.Text.Json;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class AiConnectionCatalogService(StateStore store, IWebHostEnvironment environment, ILogger<AiConnectionCatalogService> logger)
{
    private readonly string _path = Path.Combine(environment.ContentRootPath, "Data", "ai-connections.json");
    private readonly string _expandedModelsPath = Path.Combine(environment.ContentRootPath, "Data", "ai-models-expanded.json");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            await store.UpdateAsync(state => { state.AiProviderCatalog = []; return true; });
            logger.LogInformation("AI provider catalog was not found at {Path}; starting without AI provider defaults. Optional integrations will be used only when they are configured", _path);
            return;
        }
        await using var stream = File.OpenRead(_path);
        var catalog = await JsonSerializer.DeserializeAsync<List<AiProviderCatalogItem>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken) ?? [];
        await MergeExpandedModelsAsync(catalog, cancellationToken);
        foreach (var provider in catalog)
        {
            provider.ModelDiscovery = AiModelDiscoveryService.DiscoveryModeFor(provider.Id);
            provider.ModelDiscoveryRequiresCredential = AiModelDiscoveryService.DiscoveryRequiresCredential(provider.Id);
        }
        ValidateCatalog(catalog);
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

    private async Task MergeExpandedModelsAsync(List<AiProviderCatalogItem> catalog, CancellationToken cancellationToken)
    {
        if (!File.Exists(_expandedModelsPath)) return;
        await using var stream = File.OpenRead(_expandedModelsPath);
        var additions = await JsonSerializer.DeserializeAsync<Dictionary<string, List<AiModelCatalogItem>>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken) ?? [];
        foreach (var (providerId, models) in additions)
        {
            var provider = catalog.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null) throw new InvalidDataException($"Expanded model catalog references unknown provider '{providerId}'.");
            provider.Models = provider.Models.Concat(models)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        }
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
        model.ApiKeyPrefix = provider?.ApiKeyPrefix?.Trim() ?? "";
        model.ApiProtocol = provider?.ApiProtocol is string protocol && AiApiProtocols.All.Contains(protocol)
            ? protocol : AiApiProtocols.OpenAiChat;
    }

    private static void ValidateCatalog(IReadOnlyList<AiProviderCatalogItem> catalog)
    {
        if (catalog.Count == 0) throw new InvalidDataException("The AI provider catalog is empty.");
        var duplicate = catalog.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate AI provider id: {duplicate.Key}.");
        foreach (var provider in catalog)
        {
            if (string.IsNullOrWhiteSpace(provider.Id) || string.IsNullOrWhiteSpace(provider.Name)) throw new InvalidDataException("Every AI provider requires an id and name.");
            if (!AiApiProtocols.All.Contains(provider.ApiProtocol)) throw new InvalidDataException($"Provider {provider.Id} has unsupported protocol '{provider.ApiProtocol}'.");
            if (!AiModelDiscoveryModes.All.Contains(provider.ModelDiscovery)) throw new InvalidDataException($"Provider {provider.Id} has unsupported model discovery mode '{provider.ModelDiscovery}'.");
            if (provider.Models.Count == 0) throw new InvalidDataException($"Provider {provider.Id} has no model choices.");
            if (!provider.RequiresBaseUrl && string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new InvalidDataException($"Provider {provider.Id} requires a catalog base URL or requiresBaseUrl=true.");
        }
    }

    private static string InferProvider(IEnumerable<AiProviderCatalogItem> catalog, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var configured)) return "custom";
        return catalog.FirstOrDefault(provider => Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var known) &&
            configured.Host.Equals(known.Host, StringComparison.OrdinalIgnoreCase))?.Id
            ?? (configured.Host.Contains("runpod", StringComparison.OrdinalIgnoreCase) ? "runpod" : "custom");
    }
}
