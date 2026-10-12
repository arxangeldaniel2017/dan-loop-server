using System.Net.Http.Json;
using System.Text.Json;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

internal static class AnthropicProtocol
{
    public static bool Handles(RemoteModel model) => model.ApiProtocol.Equals(AiApiProtocols.AnthropicMessages, StringComparison.OrdinalIgnoreCase)
        || model.ProviderId.Equals("anthropic", StringComparison.OrdinalIgnoreCase);

    public static HttpRequestMessage CreateRequest(RemoteModel model, string apiKey, IReadOnlyList<object> openAiMessages,
        IReadOnlyList<object>? openAiTools, object? openAiToolChoice, int maxTokens)
    {
        var source = JsonSerializer.SerializeToElement(openAiMessages);
        var messages = new List<object>();
        var system = new List<string>();
        foreach (var message in source.EnumerateArray())
        {
            var role = message.GetProperty("role").GetString() ?? "user";
            if (role == "system") { system.Add(ContentText(message)); continue; }
            if (role == "tool")
            {
                messages.Add(new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = String(message, "tool_call_id"), content = ContentText(message) } } });
                continue;
            }
            var blocks = new List<object>();
            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String) blocks.Add(new { type = "text", text = content.GetString() ?? "" });
                else if (content.ValueKind == JsonValueKind.Array)
                    foreach (var item in content.EnumerateArray()) AddContentBlock(blocks, item);
            }
            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                foreach (var call in calls.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    blocks.Add(new
                    {
                        type = "tool_use", id = String(call, "id"), name = String(function, "name"),
                        input = ParseObject(String(function, "arguments"))
                    });
                }
            messages.Add(new { role = role == "assistant" ? "assistant" : "user", content = blocks });
        }

        var tools = openAiTools is null ? null : JsonSerializer.SerializeToElement(openAiTools).EnumerateArray().Select(tool =>
        {
            var function = tool.GetProperty("function");
            return (object)new { name = String(function, "name"), description = String(function, "description"), input_schema = function.GetProperty("parameters") };
        }).ToArray();
        var choice = ConvertToolChoice(openAiToolChoice);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.ModelId, ["system"] = string.Join("\n\n", system), ["messages"] = messages,
            ["max_tokens"] = Math.Clamp(maxTokens, 32, 32_768), ["temperature"] = 0.2
        };
        if (tools is { Length: > 0 }) payload["tools"] = tools;
        if (choice is not null) payload["tool_choice"] = choice;
        var request = new HttpRequestMessage(HttpMethod.Post, model.BaseUrl.TrimEnd('/') + "/messages") { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return request;
    }

    public static AgentModelStep ParseStep(string body)
    {
        using var json = JsonDocument.Parse(body);
        var text = new List<string>();
        var calls = new List<AgentToolCall>();
        if (json.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                var type = String(block, "type");
                if (type == "text") text.Add(String(block, "text"));
                else if (type == "tool_use") calls.Add(new AgentToolCall
                {
                    Id = String(block, "id"), Name = String(block, "name"),
                    ArgumentsJson = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"
                });
            }
        return new AgentModelStep { Content = string.Join("\n", text), ToolCalls = calls };
    }

    private static object? ConvertToolChoice(object? choice)
    {
        if (choice is null) return null;
        var element = JsonSerializer.SerializeToElement(choice);
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() == "required" ? new { type = "any" } : new { type = "auto" };
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("function", out var function))
            return new { type = "tool", name = String(function, "name") };
        return new { type = "auto" };
    }

    private static void AddContentBlock(List<object> blocks, JsonElement item)
    {
        var type = String(item, "type");
        if (type == "text") { blocks.Add(new { type = "text", text = String(item, "text") }); return; }
        if (type != "image_url" || !item.TryGetProperty("image_url", out var image)) return;
        var url = String(image, "url");
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(','); var semicolon = url.IndexOf(';');
            if (comma > 0 && semicolon > 5)
                blocks.Add(new { type = "image", source = new { type = "base64", media_type = url[5..semicolon], data = url[(comma + 1)..] } });
        }
        else blocks.Add(new { type = "image", source = new { type = "url", url } });
    }

    private static string ContentText(JsonElement message) => message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : "";
    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static JsonElement ParseObject(string value)
    {
        try { using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value); return json.RootElement.Clone(); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(new { }); }
    }
}
