using System.Text.Json;
using System.Text.RegularExpressions;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class ProjectPlanService(StateStore store)
{
    private static readonly Regex Checklist = new(@"^\s*[-*]\s*\[(?<done>[ xX])\]\s*(?<title>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex OrderedOrBullet = new(@"^\s*(?:\d+[.)]|[-*])\s+(?<title>.+?)\s*$", RegexOptions.Compiled);

    public Task<bool> HasPlanAsync(Guid jobId) => store.ReadAsync(state =>
    {
        var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
        return job is not null && FindPlan(state, job.ProjectId, job.LoopId) is not null;
    });

    public async Task<ProjectPlanStep?> PrepareJobAsync(Guid jobId)
    {
        var jobContext = await store.ReadAsync(state => state.Jobs.Where(item => item.Id == jobId)
            .Select(item => new { item.ProjectId, item.LoopId }).FirstOrDefault());
        if (jobContext is null) return null;
        var projectId = jobContext.ProjectId;

        return await store.UpdateAsync(state =>
        {
            var currentPlan = FindPlan(state, projectId, jobContext.LoopId);
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

    public Task CreatePlanAsync(Guid jobId, string planningOutput) => store.UpdateAsync(state =>
    {
        var job = state.Jobs.FirstOrDefault(item => item.Id == jobId)
            ?? throw new InvalidOperationException("Planning job was not found.");
        var existing = FindPlan(state, job.ProjectId, job.LoopId);
        if (existing is not null) return false;
        var titles = ParseStepTitles(planningOutput);
        if (titles.Count is < 1 or > 50)
            throw new InvalidOperationException("The planning response must define between 1 and 50 steps.");
        var plan = new ProjectExecutionPlan { ProjectId = job.ProjectId, LoopId = job.LoopId, PlanningOutput = planningOutput };
        plan.Steps.AddRange(titles.Select((title, index) => new ProjectPlanStep { Order = index + 1, Title = title }));
        state.ExecutionPlans.Add(plan);
        return true;
    });

    public async Task CompleteJobAsync(Guid jobId, AgentRuntimeResult result)
    {
        await store.UpdateAsync(state =>
        {
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
            if (job?.PlanStepId is not Guid stepId) return false;
            var plan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == job.ProjectId && item.Steps.Any(step => step.Id == stepId));
            var step = plan?.Steps.FirstOrDefault(item => item.Id == stepId);
            var attempt = step?.Attempts.FirstOrDefault(item => item.JobId == jobId);
            if (step is null || attempt is null) return false;
            step.Status = PlanStepStatuses.Succeeded;
            attempt.Status = PlanStepStatuses.Succeeded;
            attempt.Summary = $"Completed with {result.ChangedPaths.Count} project file change(s).";
            attempt.ChangedPaths = result.ChangedPaths.ToList();
            attempt.CommitUrl = result.PublishUrl;
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            plan!.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    public Task FailJobAsync(Guid jobId, string error) => store.UpdateAsync(state =>
    {
        var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
        if (job?.PlanStepId is not Guid stepId) return false;
        var plan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == job.ProjectId && item.Steps.Any(step => step.Id == stepId));
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
        var step = state.ExecutionPlans.Where(item => item.ProjectId == job.ProjectId).SelectMany(item => item.Steps).FirstOrDefault(item => item.Id == stepId);
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
            var plan = state.ExecutionPlans.FirstOrDefault(item => item.ProjectId == job.ProjectId && item.Steps.Any(step => step.Id == stepId));
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
        var changed = false;
        foreach (var plan in state.ExecutionPlans.Where(item => item.ProjectId == projectId))
        {
            var planChanged = false;
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
                planChanged = true;
            }
            if (planChanged) plan.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return changed;
    });

    public Task SetRetryLimitAsync(Guid projectId, Guid stepId, int? retryLimit) => store.UpdateAsync(state =>
    {
        var step = FindStep(state, projectId, stepId);
        step.RetryLimit = retryLimit;
        if (step.Status == PlanStepStatuses.Failed && (retryLimit is null || step.Attempts.Count < retryLimit)) step.Status = PlanStepStatuses.Retrying;
        return true;
    });

    public Task RetryAsync(Guid projectId, Guid stepId) => store.UpdateAsync(state =>
    {
        var plan = state.ExecutionPlans.First(item => item.ProjectId == projectId && item.Steps.Any(step => step.Id == stepId));
        var step = plan.Steps.First(item => item.Id == stepId);
        step.Status = PlanStepStatuses.Retrying;
        if (plan.LoopId is Guid loopId)
        {
            var loop = state.Loops.FirstOrDefault(item => item.Id == loopId);
            if (loop is not null) { loop.IsEnabled = true; loop.LastQueuedAt = null; }
        }
        return true;
    });

    public Task SkipAsync(Guid projectId, Guid stepId) => store.UpdateAsync(state =>
    {
        var step = FindStep(state, projectId, stepId);
        step.Status = PlanStepStatuses.Skipped;
        return true;
    });

    public Task<PlanStepMutationResult> AddStepAsync(Guid projectId, string title)
        => Task.FromResult(PlanStepMutationResult.Fail("Loop steps are fixed when the loop plan is created. Create a new loop to define more steps."));

    public Task<PlanStepMutationResult> ToggleStepAsync(Guid projectId, Guid stepId)
        => Task.FromResult(PlanStepMutationResult.Fail("Loop steps cannot be activated or deactivated after the plan is created."));

    public Task<PlanStepMutationResult> DeleteStepAsync(Guid projectId, Guid stepId)
        => Task.FromResult(PlanStepMutationResult.Fail("Loop steps cannot be deleted after the plan is created."));

    public Task<PlanStepMutationResult> MoveStepAsync(Guid projectId, Guid stepId, int direction)
        => Task.FromResult(PlanStepMutationResult.Fail("Loop step order is fixed when the plan is created."));

    public Task<PlanStepMutationResult> ReorderStepsAsync(Guid projectId, IReadOnlyCollection<Guid> stepIds)
        => Task.FromResult(PlanStepMutationResult.Fail("Loop step order is fixed when the plan is created."));

    private static List<string> ParseStepTitles(string output)
    {
        var trimmed = output.Trim();
        var jsonCandidate = Regex.Replace(trimmed, @"^```(?:json)?\s*|\s*```$", "", RegexOptions.IgnoreCase).Trim();
        var candidates = new List<string> { jsonCandidate };
        var objectStart = trimmed.IndexOf('{');
        var objectEnd = trimmed.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart) candidates.Add(trimmed[objectStart..(objectEnd + 1)]);
        var arrayStart = trimmed.IndexOf('[');
        var arrayEnd = trimmed.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart) candidates.Add(trimmed[arrayStart..(arrayEnd + 1)]);
        foreach (var candidate in candidates.Distinct())
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                JsonElement steps;
                if (document.RootElement.ValueKind == JsonValueKind.Array) steps = document.RootElement;
                else if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("steps", out var property)) steps = property;
                else steps = default;
                if (steps.ValueKind == JsonValueKind.Array)
                {
                    var jsonSteps = steps.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()?.Trim() ?? "").Where(title => title.Length > 0).ToList();
                    if (jsonSteps.Count > 0) return NormalizeTitles(jsonSteps);
                }
            }
            catch (JsonException) { }
        }

        var lineSteps = output.Split('\n').Select(line =>
        {
            var checklist = Checklist.Match(line);
            if (checklist.Success) return checklist.Groups["title"].Value.Trim();
            var listItem = OrderedOrBullet.Match(line);
            return listItem.Success ? listItem.Groups["title"].Value.Trim() : "";
        }).Where(title => title.Length > 0).ToList();
        return NormalizeTitles(lineSteps);
    }

    private static List<string> NormalizeTitles(IEnumerable<string> titles)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in titles)
        {
            var title = value.Trim();
            if (title.Length is 0 or > 500 || !seen.Add(title)) continue;
            result.Add(title);
        }
        return result;
    }

    private static bool IsEligible(ProjectPlanStep step) => step.IsActive && (step.Status is PlanStepStatuses.Pending or PlanStepStatuses.Retrying or PlanStepStatuses.Running or PlanStepStatuses.Paused)
        && (step.RetryLimit is null || step.Attempts.Count < step.RetryLimit || step.Status == PlanStepStatuses.Running);
    private static ProjectExecutionPlan? FindPlan(AppState state, Guid projectId, Guid? loopId) => state.ExecutionPlans
        .Where(item => item.ProjectId == projectId && item.LoopId == loopId).OrderByDescending(item => item.CreatedAt).FirstOrDefault();
    private static ProjectPlanStep FindStep(AppState state, Guid projectId, Guid stepId) => state.ExecutionPlans
        .Where(item => item.ProjectId == projectId).SelectMany(item => item.Steps).First(item => item.Id == stepId);
    private static ProjectPlanStep CloneStep(ProjectPlanStep step) => new() { Id = step.Id, Order = step.Order, Title = step.Title, IsActive = step.IsActive, Status = step.Status, RetryLimit = step.RetryLimit, Attempts = step.Attempts.ToList() };
}

public sealed record PlanStepMutationResult(bool Success, string Message)
{
    public static PlanStepMutationResult Ok(string message) => new(true, message);
    public static PlanStepMutationResult Fail(string message) => new(false, message);
}
