using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DanLoopServer.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DanLoopServer.Services;

public sealed class AgentRuntimeClient(HttpClient http, IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("remote-model-api-keys-v1");

    public async Task<AgentModelStep> CompleteStepAsync(RemoteModel model, IReadOnlyList<object> messages, AgentRuntimeSettings settings, CancellationToken cancellationToken, IReadOnlySet<string>? allowedTools = null, string? requiredTool = null, bool requireAnyTool = false, bool? streamOverride = null, bool useFileGenerationBudget = false)
    {
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestDeadline.CancelAfter(TimeSpan.FromMinutes(15));
        var requestCancellationToken = requestDeadline.Token;
        var useStreaming = streamOverride ?? settings.UseStreaming;
        var maxOutputTokens = useFileGenerationBudget
            ? Math.Clamp(settings.MaxFileOutputTokens, 32, 131_072)
            : Math.Clamp(settings.MaxOutputTokens, 32, 16_384);
        object toolChoice = requiredTool is not null
            ? new { type = "function", function = new { name = requiredTool } }
            : requireAnyTool ? "required" : "auto";
        var toolDefinitions = AgentToolExecutor.ToolDefinitions(settings, allowedTools);
        if (AnthropicProtocol.Handles(model))
        {
            using var anthropicRequest = AnthropicProtocol.CreateRequest(model, RevealKey(model), messages, toolDefinitions, toolChoice, maxOutputTokens);
            using var anthropicResponse = await http.SendAsync(anthropicRequest, requestCancellationToken);
            var anthropicBody = await anthropicResponse.Content.ReadAsStringAsync(requestCancellationToken);
            if (!anthropicResponse.IsSuccessStatusCode) throw RemoteError(anthropicResponse, anthropicBody);
            return AnthropicProtocol.ParseStep(anthropicBody);
        }
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.ModelId,
            ["messages"] = messages,
            ["tools"] = toolDefinitions,
            ["tool_choice"] = toolChoice,
            ["stream"] = useStreaming,
            ["max_tokens"] = maxOutputTokens,
            ["temperature"] = 0.2
        };
        if (model.ModelId.Contains("qwen", StringComparison.OrdinalIgnoreCase))
            payload["chat_template_kwargs"] = new { enable_thinking = false, preserve_thinking = false };
        using var request = new HttpRequestMessage(HttpMethod.Post, model.BaseUrl.TrimEnd('/') + "/chat/completions") { Content = JsonContent.Create(payload) };
        AiRequestAuthentication.Apply(request, model, string.IsNullOrEmpty(model.ProtectedApiKey) ? "" : _protector.Unprotect(model.ProtectedApiKey));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(requestCancellationToken);
            throw RemoteError(response, body);
        }
        return useStreaming
            ? await ReadStreamingAsync(response, requestCancellationToken)
            : ParseComplete(await response.Content.ReadAsStringAsync(requestCancellationToken));
    }

    private string RevealKey(RemoteModel model) => string.IsNullOrEmpty(model.ProtectedApiKey) ? "" : _protector.Unprotect(model.ProtectedApiKey);
    private static InvalidOperationException RemoteError(HttpResponseMessage response, string body) => new($"Remote model returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1000)]}");

    private static async Task<AgentModelStep> ReadStreamingAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var tools = new Dictionary<int, ToolBuilder>();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            using var json = JsonDocument.Parse(data);
            var delta = json.RootElement.GetProperty("choices")[0].GetProperty("delta");
            AppendString(delta, "content", content);
            AppendString(delta, "reasoning", reasoning);
            AppendString(delta, "reasoning_content", reasoning);
            if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) continue;
            foreach (var call in calls.EnumerateArray())
            {
                var index = call.TryGetProperty("index", out var indexValue) ? indexValue.GetInt32() : tools.Count;
                if (!tools.TryGetValue(index, out var builder)) tools[index] = builder = new ToolBuilder();
                if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) builder.Id = id.GetString() ?? builder.Id;
                if (!call.TryGetProperty("function", out var function)) continue;
                if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) builder.Name.Append(name.GetString());
                if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String) builder.Arguments.Append(arguments.GetString());
            }
        }
        return new AgentModelStep
        {
            Content = content.ToString(), Reasoning = reasoning.ToString(),
            ToolCalls = tools.OrderBy(pair => pair.Key).Select(pair => pair.Value.Build()).ToList()
        };
    }

    private static AgentModelStep ParseComplete(string body)
    {
        using var json = JsonDocument.Parse(body);
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var calls = new List<AgentToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                calls.Add(new AgentToolCall
                {
                    Id = call.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                    Name = function.GetProperty("name").GetString() ?? "",
                    ArgumentsJson = function.GetProperty("arguments").GetString() ?? "{}"
                });
            }
        return new AgentModelStep
        {
            Content = GetString(message, "content"),
            Reasoning = GetString(message, "reasoning") + GetString(message, "reasoning_content"),
            ToolCalls = calls
        };
    }

    private static void AppendString(JsonElement element, string property, StringBuilder target)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String) target.Append(value.GetString());
    }
    private static string GetString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private sealed class ToolBuilder
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
        public AgentToolCall Build() => new() { Id = Id, Name = Name.ToString(), ArgumentsJson = Arguments.Length == 0 ? "{}" : Arguments.ToString() };
    }
}
