using System.Text.Json;
using System.Text.RegularExpressions;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class AgentRuntimeService(AgentRuntimeClient client, AgentToolExecutor tools, StateStore store)
{
    public async Task<AgentRuntimeResult> RunAsync(RemoteModel model, Project project, Job job, IReadOnlyList<ConversationMessage> conversation, string currentPrompt, IReadOnlyList<string> imageDataUrls, AgentRuntimeSettings settings, CancellationToken cancellationToken)
    {
        var messages = BuildMessages(project, job, conversation, currentPrompt, imageDataUrls);
        var toolCallCount = 0;
        for (var stepNumber = 1; stepNumber <= Math.Clamp(settings.MaxSteps, 1, 50); stepNumber++)
        {
            var step = await client.CompleteStepAsync(model, messages, settings, cancellationToken);
            var structured = step.ToolCalls.Count > 0;
            var calls = structured ? step.ToolCalls : ParseFallbackToolCalls(step.Content);
            if (calls.Count == 0)
            {
                var final = !string.IsNullOrWhiteSpace(step.Content) ? step.Content : step.Reasoning;
                if (string.IsNullOrWhiteSpace(final)) throw new InvalidOperationException("The model returned no final content.");
                return new AgentRuntimeResult { Output = final, Steps = stepNumber, ToolCalls = toolCallCount };
            }

            toolCallCount += calls.Count;
            if (structured)
            {
                messages.Add(new
                {
                    role = "assistant",
                    content = string.IsNullOrWhiteSpace(step.Content) ? null : step.Content,
                    tool_calls = calls.Select(call => new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.ArgumentsJson } }).ToArray()
                });
            }
            else messages.Add(new { role = "assistant", content = step.Content });

            var fallbackResults = new List<string>();
            foreach (var call in calls)
            {
                var auditId = await StartAuditAsync(project.Id, job.Id, stepNumber, call);
                var result = await tools.ExecuteAsync(project.Id, call, settings, cancellationToken);
                await CompleteAuditAsync(auditId, result);
                if (structured) messages.Add(new { role = "tool", tool_call_id = call.Id, content = result.Output });
                else fallbackResults.Add($"TOOL {call.Name} ({(result.Success ? "success" : "failed")}):\n{result.Output}");
            }
            if (!structured) messages.Add(new { role = "user", content = "Tool execution results:\n\n" + string.Join("\n\n", fallbackResults) + "\n\nContinue the task. Use tools again if needed, otherwise provide the final verified result." });
        }
        throw new InvalidOperationException($"Agent stopped after reaching the configured limit of {settings.MaxSteps} steps.");
    }

    private static List<object> BuildMessages(Project project, Job job, IReadOnlyList<ConversationMessage> conversation, string currentPrompt, IReadOnlyList<string> imageDataUrls)
    {
        var system = project.SystemPrompt + """


AGENT RUNTIME:
You are operating inside the project's isolated workspace. Use the supplied structured tools to inspect, edit, build, and test the project. Do not print XML tool-call markup and do not merely describe commands. Call tools and use their returned results. Keep changes scoped to the workspace. Never attempt to read secrets, escape the workspace, use destructive Git operations, or bypass a blocked tool. Verify completed work before your final response.
""";
        var result = new List<object> { new { role = "system", content = system } };
        foreach (var message in conversation)
        {
            var isCurrent = message.JobId == job.Id && message.Role == "user";
            if (isCurrent && imageDataUrls.Count > 0)
            {
                var content = new List<object> { new { type = "text", text = currentPrompt } };
                content.AddRange(imageDataUrls.Select(url => (object)new { type = "image_url", image_url = new { url } }));
                result.Add(new { role = message.Role, content = (object)content });
            }
            else result.Add(new { role = message.Role, content = (object)(isCurrent ? currentPrompt : message.Content) });
        }
        if (!conversation.Any(message => message.JobId == job.Id && message.Role == "user")) result.Add(new { role = "user", content = currentPrompt });
        return result;
    }

    private static IReadOnlyList<AgentToolCall> ParseFallbackToolCalls(string content)
    {
        var calls = new List<AgentToolCall>();
        foreach (Match match in Regex.Matches(content, "<tool_call>\\s*<function=(?<name>[^>]+)>\\s*(?<body>.*?)</tool_call>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match parameter in Regex.Matches(match.Groups["body"].Value, "<parameter=(?<key>[^>]+)>\\s*(?<value>.*?)(?=<parameter=|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                values[parameter.Groups["key"].Value.Trim()] = parameter.Groups["value"].Value.Trim();
            var name = match.Groups["name"].Value.Trim();
            var normalized = name.Equals("Bash", StringComparison.OrdinalIgnoreCase) ? "run_command" : name;
            var arguments = normalized == "run_command" && values.TryGetValue("command", out var command)
                ? JsonSerializer.Serialize(new { command }) : JsonSerializer.Serialize(values);
            calls.Add(new AgentToolCall { Name = normalized, ArgumentsJson = arguments });
        }
        return calls;
    }

    private Task<Guid> StartAuditAsync(Guid projectId, Guid jobId, int step, AgentToolCall call) => store.UpdateAsync(state =>
    {
        var entry = new AgentToolAuditEntry { ProjectId = projectId, JobId = jobId, Step = step, Tool = call.Name, Summary = Limit(call.ArgumentsJson, 500) };
        state.AgentRuntime.AuditTrail.Insert(0, entry);
        if (state.AgentRuntime.AuditTrail.Count > 1000) state.AgentRuntime.AuditTrail.RemoveRange(1000, state.AgentRuntime.AuditTrail.Count - 1000);
        return entry.Id;
    });

    private Task CompleteAuditAsync(Guid id, AgentToolResult result) => store.UpdateAsync(state =>
    {
        var entry = state.AgentRuntime.AuditTrail.First(item => item.Id == id);
        entry.Status = result.Success ? "Completed" : "Failed";
        entry.Summary = result.Summary;
        entry.Output = Limit(result.Output, 4000);
        entry.CompletedAt = DateTimeOffset.UtcNow;
        return true;
    });

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";
}
