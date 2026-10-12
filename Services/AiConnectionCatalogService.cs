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
                var suggested = SuggestedCapabilities(catalog, model.ProviderId, model.ModelId);
                if (suggested.Count > 0) model.SuggestedCapabilities = suggested;
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
        model ??= provider?.Models.FirstOrDefault(item => item.Id.Equals("custom", StringComparison.OrdinalIgnoreCase));
        if (model is not null) return model.Capabilities.Where(AiCapabilities.All.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var value = modelId.ToLowerInvariant();
        if (value.Contains("image") || value.Contains("flux") || value.Contains("stable-diffusion")) return [AiCapabilities.Generator, AiCapabilities.Image, AiCapabilities.Files];
        if (value.Contains("audio") || value.Contains("whisper") || value.Contains("tts")) return [AiCapabilities.Generator, AiCapabilities.Audio, AiCapabilities.Files];
        if (value.Contains("video") || value.Contains("sora")) return [AiCapabilities.Generator, AiCapabilities.Video, AiCapabilities.Files];
        return [AiCapabilities.Planner, AiCapabilities.Coder, AiCapabilities.Orchestrator, AiCapabilities.ToolCalling, AiCapabilities.Generator, AiCapabilities.Files];
    }

    private static string InferProvider(IEnumerable<AiProviderCatalogItem> catalog, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var configured)) return "custom";
        return catalog.FirstOrDefault(provider => Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var known) &&
            configured.Host.Equals(known.Host, StringComparison.OrdinalIgnoreCase))?.Id
            ?? (configured.Host.Contains("runpod", StringComparison.OrdinalIgnoreCase) ? "runpod" : "custom");
    }
}
