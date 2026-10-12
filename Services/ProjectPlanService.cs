using System.Text.RegularExpressions;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class ProjectPlanService(StateStore store, WorkspaceService workspace)
{
    private static readonly Regex Checklist = new(@"^\s*[-*]\s*\[(?<done>[ xX])\]\s*(?<title>.+?)\s*$", RegexOptions.Compiled);

    public async Task<ProjectPlanStep?> PrepareJobAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken)
    {
        var planPath = await workspace.ResolveProjectPathAsync(projectId, "PLAN.md");
        var plan = await store.ReadAsync(state => state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == projectId));
        if (!File.Exists(planPath) && plan is not null && !string.IsNullOrWhiteSpace(plan.LastMarkdown))
        {
            await File.WriteAllTextAsync(planPath, Render(plan), cancellationToken);
            await store.UpdateAsync(state =>
            {
                state.Messages.Add(new AppMessage { ProjectId = projectId, Level = MessageLevels.Warning, Text = "PLAN.md was deleted and has been restored from the server-managed execution plan." });
                return true;
            });
        }

        if (File.Exists(planPath))
        {
            var markdown = await File.ReadAllTextAsync(planPath, cancellationToken);
            await SynchronizeAsync(projectId, markdown);
        }

        return await store.UpdateAsync(state =>
        {
            var currentPlan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == projectId);
            if (currentPlan is null) return null;
            var step = currentPlan.Steps.OrderBy(item => item.Order).FirstOrDefault(IsEligible);
            if (step is null) return null;
            var existing = step.Attempts.FirstOrDefault(item => item.JobId == jobId);
            if (existing is null)
            {
                existing = new PlanStepAttempt { JobId = jobId, Number = step.Attempts.Count + 1 };
                step.Attempts.Add(existing);
            }
            else
            {
                existing.Status = PlanStepStatuses.Running;
                existing.CompletedAt = null;
                existing.Error = "";
            }
            step.Status = PlanStepStatuses.Running;
            currentPlan.UpdatedAt = DateTimeOffset.UtcNow;
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
            if (job is not null) { job.PlanStepId = step.Id; job.PlanStepTitle = step.Title; }
            return CloneStep(step);
        });
    }

    public async Task SynchronizeFromWorkspaceAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var path = await workspace.ResolveProjectPathAsync(projectId, "PLAN.md");
        if (File.Exists(path)) await SynchronizeAsync(projectId, await File.ReadAllTextAsync(path, cancellationToken));
    }

    public Task CompleteJobAsync(Guid jobId, AgentRuntimeResult result) => store.UpdateAsync(state =>
    {
        var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
        if (job?.PlanStepId is not Guid stepId) return false;
        var plan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == job.ProjectId);
        var step = plan?.Steps.FirstOrDefault(item => item.Id == stepId);
        var attempt = step?.Attempts.FirstOrDefault(item => item.JobId == jobId);
        if (step is null || attempt is null) return false;
        step.Status = PlanStepStatuses.Succeeded;
        attempt.Status = PlanStepStatuses.Succeeded;
        attempt.Summary = $"Completed with {result.ChangedPaths.Count(path => !path.Equals("PLAN.md", StringComparison.OrdinalIgnoreCase))} project file change(s).";
        attempt.ChangedPaths = result.ChangedPaths.ToList();
        attempt.CommitUrl = result.PublishUrl;
        attempt.CompletedAt = DateTimeOffset.UtcNow;
        plan!.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    });

    public Task FailJobAsync(Guid jobId, string error) => store.UpdateAsync(state =>
    {
        var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
        if (job?.PlanStepId is not Guid stepId) return false;
        var plan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == job.ProjectId);
        var step = plan?.Steps.FirstOrDefault(item => item.Id == stepId);
        var attempt = step?.Attempts.FirstOrDefault(item => item.JobId == jobId);
        if (step is null || attempt is null) return false;
        attempt.Status = PlanStepStatuses.Failed;
        attempt.Error = error;
        attempt.CompletedAt = DateTimeOffset.UtcNow;
        var canRetry = step.RetryLimit is null || step.Attempts.Count < step.RetryLimit;
        step.Status = canRetry ? PlanStepStatuses.Retrying : PlanStepStatuses.Failed;
        attempt.Summary = canRetry ? "Attempt failed; the step will be retried by the next project job." : "Attempt failed and the configured retry limit was reached.";
        plan!.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    });

    public Task InterruptJobAsync(Guid jobId) => store.UpdateAsync(state =>
    {
        var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
        if (job?.PlanStepId is not Guid stepId) return false;
        var step = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == job.ProjectId)?.Steps.FirstOrDefault(item => item.Id == stepId);
        var attempt = step?.Attempts.FirstOrDefault(item => item.JobId == jobId);
        if (step is null || attempt is null) return false;
        step.Status = PlanStepStatuses.Retrying;
        attempt.Status = PlanStepStatuses.Retrying;
        attempt.Summary = "Interrupted by a server restart; this attempt will resume.";
        return true;
    });

    public Task CancelJobsAsync(IEnumerable<Guid> jobIds, string reason) => store.UpdateAsync(state =>
    {
        var changed = false;
        foreach (var jobId in jobIds.Distinct())
        {
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
            if (job?.PlanStepId is not Guid stepId) continue;
            var plan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == job.ProjectId);
            var step = plan?.Steps.FirstOrDefault(item => item.Id == stepId);
            var attempt = step?.Attempts.FirstOrDefault(item => item.JobId == jobId);
            if (step is null || attempt is null) continue;
            if (attempt.Status is PlanStepStatuses.Running or PlanStepStatuses.Retrying)
            {
                attempt.Status = PlanStepStatuses.Cancelled;
                attempt.Summary = reason;
                attempt.CompletedAt = DateTimeOffset.UtcNow;
            }
            if (step.Status is PlanStepStatuses.Running or PlanStepStatuses.Retrying)
                step.Status = PlanStepStatuses.Paused;
            plan!.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        return changed;
    });

    public Task PauseProjectAsync(Guid projectId, string reason) => store.UpdateAsync(state =>
    {
        var plan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == projectId);
        if (plan is null) return false;
        var changed = false;
        foreach (var step in plan.Steps.Where(item => item.Status is PlanStepStatuses.Running or PlanStepStatuses.Retrying))
        {
            var attempt = step.Attempts.LastOrDefault(item => item.Status is PlanStepStatuses.Running or PlanStepStatuses.Retrying);
            if (attempt is not null)
            {
                attempt.Status = PlanStepStatuses.Cancelled;
                attempt.Summary = reason;
                attempt.CompletedAt ??= DateTimeOffset.UtcNow;
            }
            step.Status = PlanStepStatuses.Paused;
            changed = true;
        }
        if (changed) plan.UpdatedAt = DateTimeOffset.UtcNow;
        return changed;
    });

    public Task SetRetryLimitAsync(Guid projectId, Guid stepId, int? retryLimit) => store.UpdateAsync(state =>
    {
        var step = state.ExecutionPlans.First(item => item.ProjectId == projectId).Steps.First(item => item.Id == stepId);
        step.RetryLimit = retryLimit;
        if (step.Status == PlanStepStatuses.Failed && (retryLimit is null || step.Attempts.Count < retryLimit)) step.Status = PlanStepStatuses.Retrying;
        return true;
    });

    public Task RetryAsync(Guid projectId, Guid stepId) => store.UpdateAsync(state =>
    {
        var step = state.ExecutionPlans.First(item => item.ProjectId == projectId).Steps.First(item => item.Id == stepId);
        step.Status = PlanStepStatuses.Retrying;
        return true;
    });

    public Task SkipAsync(Guid projectId, Guid stepId) => store.UpdateAsync(state =>
    {
        var step = state.ExecutionPlans.First(item => item.ProjectId == projectId).Steps.First(item => item.Id == stepId);
        step.Status = PlanStepStatuses.Skipped;
        return true;
    });

    public Task<PlanStepMutationResult> AddStepAsync(Guid projectId, string title) => MutatePlanAsync(projectId, plan =>
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return PlanStepMutationResult.Fail("Step title is required.");
        if (trimmed.Contains('\r') || trimmed.Contains('\n')) return PlanStepMutationResult.Fail("Step title must be a single line.");
        if (trimmed.Length > 500) return PlanStepMutationResult.Fail("Step title must be 500 characters or fewer.");
        if (plan.Steps.Any(step => Normalize(step.Title) == Normalize(trimmed)))
            return PlanStepMutationResult.Fail("A step with that title already exists.");
        plan.Steps.Add(new ProjectPlanStep { Order = plan.Steps.Count + 1, Title = trimmed });
        NormalizeOrder(plan);
        return PlanStepMutationResult.Ok("Step added at the end of the plan.");
    });

    public Task<PlanStepMutationResult> ToggleStepAsync(Guid projectId, Guid stepId) => MutatePlanAsync(projectId, plan =>
    {
        var step = plan.Steps.FirstOrDefault(item => item.Id == stepId);
        if (step is null) return PlanStepMutationResult.Fail("Step was not found.");
        if (step.Status == PlanStepStatuses.Running)
            return PlanStepMutationResult.Fail("A running step cannot be deactivated. Cancel its job first.");
        step.IsActive = !step.IsActive;
        return PlanStepMutationResult.Ok(step.IsActive ? "Step activated." : "Step deactivated.");
    });

    public Task<PlanStepMutationResult> DeleteStepAsync(Guid projectId, Guid stepId) => MutatePlanAsync(projectId, plan =>
    {
        var step = plan.Steps.FirstOrDefault(item => item.Id == stepId);
        if (step is null) return PlanStepMutationResult.Fail("Step was not found.");
        if (step.Status == PlanStepStatuses.Running)
            return PlanStepMutationResult.Fail("A running step cannot be deleted. Cancel its job first.");
        plan.Steps.Remove(step);
        NormalizeOrder(plan);
        return PlanStepMutationResult.Ok("Step deleted.");
    });

    public Task<PlanStepMutationResult> MoveStepAsync(Guid projectId, Guid stepId, int direction) => MutatePlanAsync(projectId, plan =>
    {
        var ordered = plan.Steps.OrderBy(item => item.Order).ToList();
        var index = ordered.FindIndex(item => item.Id == stepId);
        if (index < 0) return PlanStepMutationResult.Fail("Step was not found.");
        var destination = index + Math.Sign(direction);
        if (destination < 0 || destination >= ordered.Count)
            return PlanStepMutationResult.Fail("Step is already at the edge of the plan.");
        (ordered[index], ordered[destination]) = (ordered[destination], ordered[index]);
        for (var item = 0; item < ordered.Count; item++) ordered[item].Order = item + 1;
        return PlanStepMutationResult.Ok(direction < 0 ? "Step moved up." : "Step moved down.");
    });

    public Task<PlanStepMutationResult> ReorderStepsAsync(Guid projectId, IReadOnlyCollection<Guid> stepIds) => MutatePlanAsync(projectId, plan =>
    {
        var requested = stepIds.ToList();
        if (requested.Count != plan.Steps.Count || requested.Distinct().Count() != requested.Count ||
            requested.Any(id => plan.Steps.All(step => step.Id != id)))
            return PlanStepMutationResult.Fail("The step order is stale. Refresh and try again.");
        var byId = plan.Steps.ToDictionary(step => step.Id);
        for (var index = 0; index < requested.Count; index++) byId[requested[index]].Order = index + 1;
        return PlanStepMutationResult.Ok("Step order updated.");
    });

    private async Task<PlanStepMutationResult> MutatePlanAsync(Guid projectId, Func<ProjectExecutionPlan, PlanStepMutationResult> mutation)
    {
        var result = await store.UpdateAsync(state =>
        {
            var plan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == projectId);
            if (plan is null) return (Result: PlanStepMutationResult.Fail("Plan was not found."), Markdown: (string?)null);
            var mutationResult = mutation(plan);
            if (!mutationResult.Success) return (Result: mutationResult, Markdown: (string?)null);
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            var markdown = Render(plan);
            plan.LastMarkdown = markdown;
            return (Result: mutationResult, Markdown: (string?)markdown);
        });
        if (result.Markdown is not null)
        {
            var path = await workspace.ResolveProjectPathAsync(projectId, "PLAN.md");
            await File.WriteAllTextAsync(path, result.Markdown);
        }
        return result.Result;
    }

    private static void NormalizeOrder(ProjectExecutionPlan plan)
    {
        var ordered = plan.Steps.OrderBy(item => item.Order).ToList();
        for (var index = 0; index < ordered.Count; index++) ordered[index].Order = index + 1;
    }

    private Task SynchronizeAsync(Guid projectId, string markdown) => store.UpdateAsync(state =>
    {
        var parsed = Parse(markdown);
        if (parsed.Count == 0) return false;
        var plan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == projectId);
        if (plan is null)
        {
            plan = new ProjectExecutionPlan { ProjectId = projectId };
            state.ExecutionPlans.Add(plan);
        }
        foreach (var item in parsed)
        {
            var step = plan.Steps.FirstOrDefault(existing => Normalize(existing.Title) == Normalize(item.Title));
            if (step is null)
            {
                step = new ProjectPlanStep { Order = item.Order, Title = item.Title, Status = item.Done ? PlanStepStatuses.Succeeded : PlanStepStatuses.Pending };
                plan.Steps.Add(step);
            }
            else
            {
                step.Order = item.Order;
                step.Title = item.Title;
                if (item.Done) step.Status = PlanStepStatuses.Succeeded;
                else if (step.Status is PlanStepStatuses.Succeeded or PlanStepStatuses.Skipped) step.Status = PlanStepStatuses.Pending;
            }
        }
        plan.LastMarkdown = markdown;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    });

    private static List<(int Order, string Title, bool Done)> Parse(string markdown) => markdown.Split('\n')
        .Select(line => Checklist.Match(line)).Where(match => match.Success)
        .Select((match, index) => (index + 1, match.Groups["title"].Value.Trim(), !string.IsNullOrWhiteSpace(match.Groups["done"].Value))).ToList();

    private static string Render(ProjectExecutionPlan plan)
    {
        var lines = plan.LastMarkdown.Split('\n').ToList();
        var checklistIndexes = lines.Select((line, index) => (line, index)).Where(item => Checklist.IsMatch(item.line)).Select(item => item.index).ToList();
        var renderedSteps = plan.Steps.OrderBy(item => item.Order).Select(step =>
        {
            var done = step.Status is PlanStepStatuses.Succeeded or PlanStepStatuses.Skipped ? "x" : " ";
            return $"- [{done}] {step.Title}";
        }).ToList();
        if (checklistIndexes.Count == 0)
        {
            var stepsHeading = lines.FindLastIndex(line => Regex.IsMatch(line, @"^\s*#{1,6}\s+Steps\s*$", RegexOptions.IgnoreCase));
            if (stepsHeading >= 0)
            {
                lines.InsertRange(stepsHeading + 1, renderedSteps);
                return string.Join('\n', lines);
            }
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add("## Steps");
            lines.AddRange(renderedSteps);
            return string.Join('\n', lines);
        }
        var checklistSet = checklistIndexes.ToHashSet();
        var output = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (index == checklistIndexes[0]) output.AddRange(renderedSteps);
            if (!checklistSet.Contains(index)) output.Add(lines[index]);
        }
        return string.Join('\n', output);
    }

    private static bool IsEligible(ProjectPlanStep step) => step.IsActive && (step.Status is PlanStepStatuses.Pending or PlanStepStatuses.Retrying or PlanStepStatuses.Running or PlanStepStatuses.Paused)
        && (step.RetryLimit is null || step.Attempts.Count < step.RetryLimit || step.Status == PlanStepStatuses.Running);
    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"^\d+[.)]\s*", "").Trim().ToLowerInvariant();
    private static ProjectPlanStep CloneStep(ProjectPlanStep step) => new() { Id = step.Id, Order = step.Order, Title = step.Title, IsActive = step.IsActive, Status = step.Status, RetryLimit = step.RetryLimit, Attempts = step.Attempts.ToList() };
}

public sealed record PlanStepMutationResult(bool Success, string Message)
{
    public static PlanStepMutationResult Ok(string message) => new(true, message);
    public static PlanStepMutationResult Fail(string message) => new(false, message);
}
