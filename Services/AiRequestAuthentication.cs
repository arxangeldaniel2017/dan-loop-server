using System.Net.Http.Headers;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public static class AiRequestAuthentication
{
    public static void Apply(HttpRequestMessage request, RemoteModel model, string apiKey)
    {
        if (model.AuthenticationType == AiAuthenticationTypes.None || string.IsNullOrWhiteSpace(apiKey)) return;
        if (model.AuthenticationType == AiAuthenticationTypes.ApiKeyHeader)
            request.Headers.TryAddWithoutValidation(string.IsNullOrWhiteSpace(model.ApiKeyHeaderName) ? "api-key" : model.ApiKeyHeaderName,
                string.IsNullOrWhiteSpace(model.ApiKeyPrefix) ? apiKey : $"{model.ApiKeyPrefix} {apiKey}");
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }
}
