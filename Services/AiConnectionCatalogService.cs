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
        return model?.Capabilities.Where(AiCapabilities.All.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    }
}
