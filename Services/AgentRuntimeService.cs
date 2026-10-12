using System.Text.Json;
using System.Text.RegularExpressions;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class AgentRuntimeService(AgentRuntimeClient client, AgentToolExecutor tools, StateStore store, WorkspaceService workspace)
{
    public async Task<AgentRuntimeResult> RunAsync(RemoteModel model, Project project, Job job, IReadOnlyList<ConversationMessage> conversation, string currentPrompt, IReadOnlyList<string> imageDataUrls, AgentRuntimeSettings settings, CancellationToken cancellationToken)
    {
        var root = await workspace.ResolveProjectPathAsync(project.Id, "");
        var planExisted = File.Exists(Path.Combine(root, "PLAN.md"));
        var planContent = planExisted ? Limit(await File.ReadAllTextAsync(Path.Combine(root, "PLAN.md"), cancellationToken), 12_000) : "";
        // AI changes are queued by the file tools and published in efficient background batches.
        // A model run must not wait for GitHub network activity in order to finish.
        var requirePublishing = false;
        var workspaceGuide = await tools.CreateWorkspaceGuideAsync(project.Id, settings);
        var messages = BuildMessages(project, job, conversation, currentPrompt, imageDataUrls, workspaceGuide, planExisted, planContent, requirePublishing);
        var toolCallCount = 0;
        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unpublishedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var publishUrl = "";
        var forceImplementationAction = false;
        var maximumSteps = Math.Clamp(settings.MaxSteps, 1, 50);
        for (var stepNumber = 1; stepNumber <= maximumSteps; stepNumber++)
        {
            var policy = ToolPolicy(planExisted, changedPaths, unpublishedPaths, requirePublishing, forceImplementationAction, stepNumber);
            var step = await client.CompleteStepAsync(model, messages, settings, cancellationToken, policy.Allowed, policy.Required, policy.RequireAny, policy.Stream, policy.MaxTokens);
            var structured = step.ToolCalls.Count > 0;
            var calls = structured ? step.ToolCalls : ParseFallbackToolCalls(step.Content);
            if (calls.Count == 0)
            {
                var final = !string.IsNullOrWhiteSpace(step.Content) ? step.Content : step.Reasoning;
                if (string.IsNullOrWhiteSpace(final)) throw new InvalidOperationException("The model returned no final content.");
                await RecordAiResponseAsync(job.Id, stepNumber, "Response", final);
                var validation = ValidateCompletion(planExisted, changedPaths, unpublishedPaths, requirePublishing);
                if (validation.Error is null)
                    return new AgentRuntimeResult
                    {
                        Output = final, Steps = stepNumber, ToolCalls = toolCallCount, Outcome = validation.Outcome!,
                        ChangedPaths = changedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(), PublishUrl = publishUrl
                    };
                if (stepNumber == maximumSteps)
                    throw new InvalidOperationException($"The model did not complete the required project action: {validation.Error}");

                messages.Add(new { role = "assistant", content = Limit(final, 2000) });
                messages.Add(new { role = "user", content = CorrectiveInstruction(validation.Error, planExisted, requirePublishing, unpublishedPaths) });
                forceImplementationAction = true;
                continue;
            }

            await RecordAiResponseAsync(job.Id, stepNumber, "Tool request", FormatToolRequest(step.Content, calls));

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
                if (result.Success) TrackToolEffect(call, result, changedPaths, unpublishedPaths, ref publishUrl);
                else if (call.Name.Equals("publish_project_files", StringComparison.OrdinalIgnoreCase))
                {
                    requirePublishing = false;
                    unpublishedPaths.Clear();
                    await RecordPublishingFallbackAsync(project.Id, job.Id, result.Output);
                }
                if (structured) messages.Add(new { role = "tool", tool_call_id = call.Id, content = result.Output });
                else fallbackResults.Add($"TOOL {call.Name} ({(result.Success ? "success" : "failed")}):\n{result.Output}");
            }
            if (!structured) messages.Add(new { role = "user", content = "Tool execution results:\n\n" + string.Join("\n\n", fallbackResults) + "\n\n" + ContinueInstruction(planExisted, requirePublishing) });
        }
        throw new InvalidOperationException($"Agent stopped after reaching the configured limit of {settings.MaxSteps} steps.");
    }

    private static List<object> BuildMessages(Project project, Job job, IReadOnlyList<ConversationMessage> conversation, string currentPrompt, IReadOnlyList<string> imageDataUrls, string workspaceGuide, bool planExisted, string planContent, bool requirePublishing)
    {
        var workflow = planExisted
            ? $"""

              WORKFLOW PHASE — IMPLEMENT ONE PLAN STEP:
              A root PLAN.md already exists and its current content is included below. The server has assigned this step: {(string.IsNullOrWhiteSpace(job.PlanStepTitle) ? "the first eligible unchecked task" : job.PlanStepTitle)}. Implement only that server-assigned step now with write_file, replace_in_file, or generate_asset when a generated asset is required. Then update PLAN.md to mark the task complete and record the files changed. Do not merely discuss, redesign, or rewrite the plan. A successful run requires at least one meaningful source, test, configuration, documentation, or generated asset file plus an updated PLAN.md. Files under results, bin, obj, temporary folders, and files named placeholder do not count as implementation.{(requirePublishing ? " Publish every changed path together with publish_project_files before finishing." : " GitHub is not connected, so keep the verified changes in the local project workspace and finish without publishing.")}

              CURRENT PLAN.md:
              {planContent}
              """
            : $"""

              WORKFLOW PHASE — CREATE THE PROJECT PLAN:
              PLAN.md does not exist. Your first action is to create a concise root PLAN.md with write_file. Include the project goal and 3–12 ordered, small, verifiable tasks using Markdown checkboxes. Do not attempt the entire project and do not emit a long design before calling the tool. This run is successfully planned only after PLAN.md is actually saved.{(requirePublishing ? " Publish PLAN.md with publish_project_files before finishing." : "")} The next loop run will implement its first unchecked task.
              """;
        var system = project.SystemPrompt + """


AGENT RUNTIME:
You can access only folders and files that belong to this project workspace. Use only the supplied structured project-file tools. Paths must be relative to the logical project root; it is not the server root.

Shells, terminals, Bash, command execution, programs, scripts, builds, tests, package managers, local Git, local commits, local pushes, server file listings, secrets, and every path outside this project are unavailable. Never request or emit a Bash or terminal tool call, including XML tool-call markup. Never try to bypass a blocked tool.

GitHub publishing is handled automatically in background batches when this project has an assigned connected repository and permission. Do not call publish_project_files unless the user explicitly requests an immediate publish. Never use Git commands. Use workspace_guide or browse_folder to understand project-relative directories, then read or edit individual project files. Describe anything you cannot verify without command execution honestly.

CURRENT PROJECT GUIDE:
""" + workspaceGuide + workflow;
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

    private static (IReadOnlySet<string>? Allowed, string? Required, bool RequireAny, bool? Stream, int? MaxTokens) ToolPolicy(bool planExisted, HashSet<string> changedPaths, HashSet<string> unpublishedPaths, bool requirePublishing, bool forceImplementationAction, int stepNumber)
    {
        var planChanged = changedPaths.Contains("PLAN.md");
        var implementationChanged = changedPaths.Any(IsImplementationPath);
        if (!planExisted && !planChanged)
            return (new HashSet<string>(["write_file"], StringComparer.OrdinalIgnoreCase), "write_file", false, true, 4096);
        // A plan is already available in the system prompt. Give the model two rounds to inspect
        // existing project files, then require implementation instead of allowing an unbounded
        // read/discuss loop over historical reports.
        if (planExisted && (forceImplementationAction || stepNumber >= 3 || changedPaths.Count > 0) && (!implementationChanged || !planChanged))
            return (new HashSet<string>(["write_file", "replace_in_file", "generate_asset"], StringComparer.OrdinalIgnoreCase), null, true, true, 4096);
        if (requirePublishing && unpublishedPaths.Count > 0)
            return (new HashSet<string>(["publish_project_files"], StringComparer.OrdinalIgnoreCase), "publish_project_files", false, true, 2048);
        return (null, null, false, true, 4096);
    }

    private static (string? Outcome, string? Error) ValidateCompletion(bool planExisted, HashSet<string> changedPaths, HashSet<string> unpublishedPaths, bool requirePublishing)
    {
        var planChanged = changedPaths.Contains("PLAN.md");
        var implementationChanged = changedPaths.Any(IsImplementationPath);
        if (!planChanged)
            return (null, planExisted ? "PLAN.md was not updated after the implementation work." : "PLAN.md was not created with write_file.");
        if (planExisted && !implementationChanged)
            return (null, "No implementation file was created or changed; only planning activity was performed.");
        if (requirePublishing && unpublishedPaths.Count > 0)
            return (null, $"The following changed files were not published through GitHub: {string.Join(", ", unpublishedPaths.OrderBy(path => path))}.");
        return (planExisted || implementationChanged ? AgentRunOutcomes.ChangesCompleted : AgentRunOutcomes.PlanCreated, null);
    }

    private static string CorrectiveInstruction(string error, bool planExisted, bool requirePublishing, HashSet<string> unpublishedPaths)
        => $"""
           Your previous response is not a valid completion: {error}
           Do not provide another plan or explanation. {(planExisted ? "Use the file tools now to implement the first unchecked PLAN.md task, then update PLAN.md." : "Use write_file now to create a concise root PLAN.md with 3–12 small checkbox tasks.")}
           {(requirePublishing && unpublishedPaths.Count > 0 ? $"Publish all still-unpublished paths together: {string.Join(", ", unpublishedPaths.OrderBy(path => path))}." : "")}
           Only provide the final report after the required tool actions succeed.
           """;

    private static string ContinueInstruction(bool planExisted, bool requirePublishing)
        => $"Continue now. {(planExisted ? "Finish one PLAN.md task, change implementation files, and update PLAN.md." : "Create PLAN.md now.")} {(requirePublishing ? "Publish every changed path before the final response." : "")} Do not stop at planning prose.";

    private static void TrackToolEffect(AgentToolCall call, AgentToolResult result, HashSet<string> changedPaths, HashSet<string> unpublishedPaths, ref string publishUrl)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var arguments = document.RootElement;
            if (result.Mutated && (call.Name.Equals("write_file", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("replace_in_file", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("generate_asset", StringComparison.OrdinalIgnoreCase)))
            {
                if (arguments.TryGetProperty("path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String)
                {
                    var path = NormalizePath(pathValue.GetString() ?? "");
                    if (path.Length > 0) { changedPaths.Add(path); unpublishedPaths.Add(path); }
                }
            }
            else if (call.Name.Equals("publish_project_files", StringComparison.OrdinalIgnoreCase) && arguments.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in paths.EnumerateArray())
                    if (value.ValueKind == JsonValueKind.String) unpublishedPaths.Remove(NormalizePath(value.GetString() ?? ""));
                publishUrl = result.Output;
            }
        }
        catch (JsonException) { /* The executor already reports malformed arguments. */ }
    }

    private static bool IsPlan(string path) => NormalizePath(path).Equals("PLAN.md", StringComparison.OrdinalIgnoreCase);
    private static bool IsImplementationPath(string path)
    {
        var normalized = NormalizePath(path);
        if (IsPlan(normalized)) return false;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part.Equals("results", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase) || part.Equals("tmp", StringComparison.OrdinalIgnoreCase) || part.Equals("temp", StringComparison.OrdinalIgnoreCase))) return false;
        return !Path.GetFileNameWithoutExtension(normalized).Equals("placeholder", StringComparison.OrdinalIgnoreCase);
    }
    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static IReadOnlyList<AgentToolCall> ParseFallbackToolCalls(string content)
    {
        var calls = new List<AgentToolCall>();
        foreach (Match match in Regex.Matches(content, "<tool_call>\\s*<function=(?<name>[^>]+)>\\s*(?<body>.*?)</tool_call>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match parameter in Regex.Matches(match.Groups["body"].Value, "<parameter=(?<key>[^>]+)>\\s*(?<value>.*?)(?=<parameter=|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                values[parameter.Groups["key"].Value.Trim()] = parameter.Groups["value"].Value.Trim();
            var name = match.Groups["name"].Value.Trim();
            var normalized = name.Equals("Bash", StringComparison.OrdinalIgnoreCase) || name.Equals("run_command", StringComparison.OrdinalIgnoreCase) ? "bash" : name;
            var arguments = normalized == "bash" && values.TryGetValue("command", out var command)
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

    private Task RecordPublishingFallbackAsync(Guid projectId, Guid jobId, string reason) => store.UpdateAsync(state =>
    {
        state.Messages.Add(new AppMessage
        {
            ProjectId = projectId,
            JobId = jobId,
            Level = MessageLevels.Warning,
            Text = $"GitHub publishing is unavailable, so this job is continuing in the local project workspace. {Limit(reason, 500)}"
        });
        return true;
    });

    private Task RecordAiResponseAsync(Guid jobId, int step, string kind, string content) => store.UpdateAsync(state =>
    {
        var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
        if (job is null) return false;
        job.AiResponses.Add(new AgentResponseLog { Step = step, Kind = kind, Content = Limit(content, 20_000) });
        if (job.AiResponses.Count > 500) job.AiResponses.RemoveRange(0, job.AiResponses.Count - 500);
        return true;
    });

    private static string FormatToolRequest(string content, IReadOnlyList<AgentToolCall> calls)
    {
        var requests = calls.Select(call => $"Requested tool: {call.Name}\n{call.ArgumentsJson}");
        var toolText = string.Join("\n\n", requests);
        return string.IsNullOrWhiteSpace(content) ? toolText : $"{content.Trim()}\n\n{toolText}";
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";
}
