using System.Security.Claims;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DanLoopServer.Controllers;

[Authorize]
public sealed class ManageController(StateStore store, PasswordService passwords, RemoteModelClient modelClient, AiModelDiscoveryService modelDiscovery, WorkspaceService workspace, GitProviderService gitHub, ProjectPlanService plans, JobCancellationRegistry cancellations, S3ObjectStorage objectStorage, LoginSecurityService loginSecurity) : Controller
{
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreateProject(string? name, string? description, string? systemPrompt, Guid? defaultModelId, int taskDelaySeconds = 10, int jobTimeoutMinutes = 15)
    {
        if (string.IsNullOrWhiteSpace(name)) return Back("Project name is required.");
        await store.UpdateAsync(state => { state.Projects.Add(new Project { Name = name.Trim(), Description = description?.Trim() ?? "", SystemPrompt = systemPrompt?.Trim() ?? "", DefaultModelId = defaultModelId, TaskDelaySeconds = Math.Clamp(taskDelaySeconds, 0, 86_400), JobTimeoutMinutes = Math.Clamp(jobTimeoutMinutes, 1, 15) }); return true; });
        return Back("Project created.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProject(Guid id, string? name, string? description, string? systemPrompt, Guid? defaultModelId, int taskDelaySeconds = 10, int jobTimeoutMinutes = 15)
    {
        if (!await CanAsync(id, manage: true)) return Forbid();
        if (string.IsNullOrWhiteSpace(name)) return Back("Project name is required.");
        await store.UpdateAsync(state => { var x = state.Projects.First(p => p.Id == id); x.Name = name.Trim(); x.Description = description?.Trim() ?? ""; x.SystemPrompt = systemPrompt?.Trim() ?? ""; x.DefaultModelId = defaultModelId; x.TaskDelaySeconds = Math.Clamp(taskDelaySeconds, 0, 86_400); x.JobTimeoutMinutes = Math.Clamp(jobTimeoutMinutes, 1, 15); return true; });
        return Back("Project updated.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteProject(Guid id)
    {
        await store.UpdateAsync(state => { state.Projects.RemoveAll(x => x.Id == id); state.Loops.RemoveAll(x => x.ProjectId == id); state.Jobs.RemoveAll(x => x.ProjectId == id); state.Permissions.RemoveAll(x => x.ProjectId == id); state.ProjectRepositories.RemoveAll(x => x.ProjectId == id); state.ExecutionPlans.RemoveAll(x => x.ProjectId == id); return true; });
        return Back("Project and its loops/jobs deleted.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLoop(Guid projectId, string name, string prompt, int intervalSeconds, int? maxExecutions, bool isEnabled = false)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        if (string.IsNullOrWhiteSpace(name)) return Back("Loop name is required.");
        if (string.IsNullOrWhiteSpace(prompt)) return Back("Loop prompt is required.");
        if (intervalSeconds is < 10 or > 2_592_000) return Back("Loop interval must be between 10 and 2,592,000 seconds.");
        if (maxExecutions is <= 0) return Back("Execution limit must be a positive number or left blank for unlimited.");
        await store.UpdateAsync(state => { state.Loops.Add(new LoopDefinition { ProjectId = projectId, Name = name.Trim(), Prompt = prompt.Trim(), IntervalSeconds = intervalSeconds, MaxExecutions = maxExecutions, IsEnabled = isEnabled }); return true; });
        return Back("Loop created.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLoop(Guid id, string? name, string? prompt, int intervalSeconds, int? maxExecutions, bool isEnabled = false)
    {
        var loop = await store.ReadAsync(state => state.Loops.FirstOrDefault(item => item.Id == id));
        if (loop is null) return NotFound();
        if (!await CanAsync(loop.ProjectId, manage: true)) return Forbid();
        if (string.IsNullOrWhiteSpace(name)) return Back("Loop name is required.");
        if (string.IsNullOrWhiteSpace(prompt)) return Back("Loop prompt is required.");
        if (intervalSeconds is < 10 or > 2_592_000) return Back("Loop interval must be between 10 and 2,592,000 seconds.");
        if (maxExecutions is <= 0) return Back("Execution limit must be a positive number or left blank for unlimited.");

        var result = await store.UpdateAsync(state =>
        {
            var current = state.Loops.First(item => item.Id == id);
            var wasEnabled = current.IsEnabled;
            current.Name = name.Trim();
            current.Prompt = prompt.Trim();
            current.IntervalSeconds = intervalSeconds;
            current.MaxExecutions = maxExecutions;
            current.IsEnabled = isEnabled;
            if (!wasEnabled && current.IsEnabled) current.LastQueuedAt = null;

            var affectedJobIds = new List<Guid>();
            foreach (var job in state.Jobs.Where(item => item.LoopId == id && item.Status == JobStatuses.Queued))
            {
                job.Prompt = current.Prompt;
                var conversation = state.Conversations.FirstOrDefault(item => item.Id == job.ConversationId);
                if (conversation is not null)
                {
                    conversation.Title = current.Name;
                    var message = conversation.Messages.FirstOrDefault(item => item.JobId == job.Id && item.Role == "user");
                    if (message is not null) message.Content = current.Prompt;
                }
            }
            if (!current.IsEnabled)
            {
                foreach (var job in state.Jobs.Where(item => item.LoopId == id && item.Status is JobStatuses.Queued or JobStatuses.Running))
                {
                    job.Status = JobStatuses.Cancelled;
                    job.Error = "Cancelled because the recurring loop was stopped.";
                    job.CompletedAt = DateTimeOffset.UtcNow;
                    affectedJobIds.Add(job.Id);
                }
            }
            var projectStillRunning = state.Loops.Any(item => item.ProjectId == current.ProjectId && item.IsEnabled);
            return new { current.IsEnabled, current.ProjectId, ProjectStillRunning = projectStillRunning, JobIds = affectedJobIds };
        });
        foreach (var jobId in result.JobIds) cancellations.Cancel(jobId);
        if (!result.IsEnabled)
        {
            await plans.CancelJobsAsync(result.JobIds, "Execution stopped because the recurring loop was disabled while editing it.");
            if (!result.ProjectStillRunning) await plans.PauseProjectAsync(result.ProjectId, "Execution plan paused because all recurring loops for this project are stopped.");
        }
        return Back(result.IsEnabled ? "Loop changes saved." : $"Loop changes saved and loop stopped. {result.JobIds.Count} unfinished execution(s) cancelled.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLoopExecutionLimit(Guid id, int? maxExecutions)
    {
        var loop = await store.ReadAsync(s => s.Loops.FirstOrDefault(x => x.Id == id));
        if (loop is null) return NotFound();
        if (!await CanAsync(loop.ProjectId, manage: true)) return Forbid();
        if (maxExecutions is <= 0) return Back("Execution limit must be a positive number or left blank for unlimited.");
        await store.UpdateAsync(state => { state.Loops.First(x => x.Id == id).MaxExecutions = maxExecutions; return true; });
        return Back(maxExecutions is null ? "Loop execution limit removed; retries are unlimited." : $"Loop execution limit set to {maxExecutions}.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleLoop(Guid id)
    {
        var loop = await store.ReadAsync(s => s.Loops.FirstOrDefault(x => x.Id == id));
        if (loop is null) return NotFound();
        if (!await CanAsync(loop.ProjectId, manage: true)) return Forbid();
        var result = await store.UpdateAsync(state =>
        {
            var current = state.Loops.First(item => item.Id == id);
            current.IsEnabled = !current.IsEnabled;
            current.LastQueuedAt = null;
            var stoppedJobIds = new List<Guid>();
            if (!current.IsEnabled)
            {
                foreach (var job in state.Jobs.Where(item => item.LoopId == id && item.Status is JobStatuses.Queued or JobStatuses.Running))
                {
                    job.Status = JobStatuses.Cancelled;
                    job.Error = "Cancelled because the recurring loop was stopped.";
                    job.CompletedAt = DateTimeOffset.UtcNow;
                    stoppedJobIds.Add(job.Id);
                }
                state.Messages.Add(new AppMessage { ProjectId = current.ProjectId, Level = MessageLevels.Info, Text = $"Loop '{current.Name}' stopped. {stoppedJobIds.Count} unfinished execution(s) were cancelled." });
            }
            var projectStillRunning = state.Loops.Any(item => item.ProjectId == current.ProjectId && item.IsEnabled);
            return new { current.IsEnabled, current.ProjectId, ProjectStillRunning = projectStillRunning, JobIds = stoppedJobIds };
        });
        foreach (var jobId in result.JobIds) cancellations.Cancel(jobId);
        if (!result.IsEnabled)
        {
            await plans.CancelJobsAsync(result.JobIds, "Execution stopped because the recurring loop was stopped.");
            if (!result.ProjectStillRunning) await plans.PauseProjectAsync(result.ProjectId, "Execution plan paused because all recurring loops for this project are stopped.");
        }
        return Back(result.IsEnabled ? "Loop enabled." : $"Loop stopped. {result.JobIds.Count} unfinished execution(s) cancelled.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLoop(Guid id)
    {
        var loop = await store.ReadAsync(s => s.Loops.FirstOrDefault(x => x.Id == id));
        if (loop is null) return NotFound();
        if (!await CanAsync(loop.ProjectId, manage: true)) return Forbid();
        await store.UpdateAsync(s => { s.Loops.RemoveAll(x => x.Id == id); return true; });
        return Back("Loop deleted.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunLoopNow(Guid id)
    {
        var loop = await store.ReadAsync(state => state.Loops.FirstOrDefault(item => item.Id == id));
        if (loop is null) return NotFound();
        if (!await CanAsync(loop.ProjectId, run: true)) return Forbid();
        var userId = CurrentUserId();
        var message = await store.UpdateAsync(state =>
        {
            var current = state.Loops.First(item => item.Id == id);
            if (!current.IsEnabled) return "Start the loop before running it now.";
            var active = state.Jobs.FirstOrDefault(job => job.LoopId == id && job.Status == JobStatuses.Running);
            if (active is not null) return $"Loop '{current.Name}' is already running.";
            var queued = state.Jobs.FirstOrDefault(job => job.LoopId == id && job.Status == JobStatuses.Queued);
            var now = DateTimeOffset.UtcNow;
            if (queued is not null)
            {
                queued.RunImmediately = true;
                current.LastQueuedAt = now;
                return $"Loop '{current.Name}' was promoted to run next; its following schedule was reset.";
            }
            var executionCount = Math.Max(current.ExecutionCount, state.Jobs.Count(job => job.LoopId == id));
            current.ExecutionCount = executionCount;
            if (current.MaxExecutions is int maximum && executionCount >= maximum)
                return $"Loop '{current.Name}' has reached its execution limit of {maximum}.";
            var project = state.Projects.FirstOrDefault(item => item.Id == current.ProjectId);
            if (project is null || !state.Models.Any(model => model.IsEnabled)) return "No enabled AI connection is available.";
            var conversation = new Conversation { ProjectId = current.ProjectId, Title = current.Name };
            var job = new Job
            {
                ProjectId = current.ProjectId, LoopId = current.Id, ModelId = project.DefaultModelId,
                RequiredCapability = AiCapabilities.Orchestrator, Prompt = current.Prompt, CreatedByUserId = userId,
                CreatedAt = now, ConversationId = conversation.Id, RunImmediately = true
            };
            state.Conversations.Add(conversation);
            state.Jobs.Add(job);
            conversation.Messages.Add(new ConversationMessage { JobId = job.Id, Role = "user", Content = job.Prompt });
            current.ExecutionCount++;
            current.LastQueuedAt = now;
            state.Messages.Add(new AppMessage { ProjectId = current.ProjectId, JobId = job.Id, Level = MessageLevels.Info, Text = $"Loop '{current.Name}' was started immediately. Its pending scheduled occurrence was replaced." });
            return $"Loop '{current.Name}' queued to run now; its following schedule was reset.";
        });
        return Back(message);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunJob(Guid projectId, string prompt, Guid? modelId, string? requiredCapability, Guid? conversationId, List<string>? attachmentPaths)
    {
        if (!await CanAsync(projectId, run: true)) return Forbid();
        if (string.IsNullOrWhiteSpace(prompt)) return Back("Job prompt is required.");
        var userId = CurrentUserId();
        await store.UpdateAsync(state =>
        {
            var project = state.Projects.First(x => x.Id == projectId);
            var conversation = conversationId is not null ? state.Conversations.FirstOrDefault(x => x.Id == conversationId && x.ProjectId == projectId) : null;
            if (conversation is null)
            {
                conversation = new Conversation { ProjectId = projectId, Title = prompt.Trim()[..Math.Min(prompt.Trim().Length, 80)] };
                state.Conversations.Add(conversation);
            }
            var capability = AiCapabilities.All.Contains(requiredCapability ?? "") ? requiredCapability! : AiCapabilities.Orchestrator;
            var job = new Job { ProjectId = projectId, Prompt = prompt.Trim(), ModelId = modelId ?? project.DefaultModelId, RequiredCapability = capability, CreatedByUserId = userId, ConversationId = conversation.Id, AttachmentPaths = attachmentPaths?.Distinct().ToList() ?? [], RunImmediately = true };
            state.Jobs.Add(job);
            conversation.Messages.Add(new ConversationMessage { JobId = job.Id, Role = "user", Content = job.Prompt, FilePaths = job.AttachmentPaths });
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        });
        return Back("Job queued.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelJob(Guid id)
    {
        var job = await store.ReadAsync(s => s.Jobs.FirstOrDefault(x => x.Id == id));
        if (job is null) return NotFound();
        if (!await CanAsync(job.ProjectId, run: true)) return Forbid();
        await store.UpdateAsync(state =>
        {
            var current = state.Jobs.First(item => item.Id == id);
            if (current.Status is JobStatuses.Queued or JobStatuses.Running)
            {
                current.Status = JobStatuses.Cancelled;
                current.Error = "Cancelled by user.";
                current.CompletedAt = DateTimeOffset.UtcNow;
            }
            return true;
        });
        cancellations.Cancel(id);
        await plans.CancelJobsAsync([id], "Execution cancelled by user.");
        return Back("Job cancelled.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPlanStepRetryLimit(Guid projectId, Guid stepId, int? retryLimit)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        if (retryLimit is <= 0) return Back("Retry limit must be a positive number or blank for unlimited.");
        await plans.SetRetryLimitAsync(projectId, stepId, retryLimit);
        return Steps(PlanStepMutationResult.Ok(retryLimit is null ? "Step retries are unlimited." : $"Step retry limit set to {retryLimit}."));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryPlanStep(Guid projectId, Guid stepId)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        await plans.RetryAsync(projectId, stepId);
        return Steps(PlanStepMutationResult.Ok("Step queued for another attempt."));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SkipPlanStep(Guid projectId, Guid stepId)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        await plans.SkipAsync(projectId, stepId);
        return Steps(PlanStepMutationResult.Ok("Step skipped. The next eligible step can continue."));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPlanStep(Guid projectId, string? title)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        return Steps(await plans.AddStepAsync(projectId, title ?? ""));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePlanStep(Guid projectId, Guid stepId)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        return Steps(await plans.ToggleStepAsync(projectId, stepId));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePlanStep(Guid projectId, Guid stepId)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        return Steps(await plans.DeleteStepAsync(projectId, stepId));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MovePlanStep(Guid projectId, Guid stepId, int direction)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        if (direction is not (-1 or 1)) return Steps(PlanStepMutationResult.Fail("Move direction is invalid."));
        return Steps(await plans.MoveStepAsync(projectId, stepId, direction));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReorderPlanSteps(Guid projectId, List<Guid>? stepIds)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        return Steps(await plans.ReorderStepsAsync(projectId, stepIds ?? []));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteJob(Guid id)
    {
        await store.UpdateAsync(s => { s.Jobs.RemoveAll(x => x.Id == id && x.Status != JobStatuses.Running); return true; });
        return Back("Job removed.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearProjectJobs(Guid projectId)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        var removed = await store.UpdateAsync(state => state.Jobs.RemoveAll(x => x.ProjectId == projectId && x.Status != JobStatuses.Running));
        return Back($"Cleared {removed} finished project jobs.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ClearAllJobs()
    {
        var removed = await store.UpdateAsync(state => state.Jobs.RemoveAll(x => x.Status != JobStatuses.Running));
        return Back($"Cleared {removed} finished jobs.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearMessages(Guid? projectId)
    {
        if (projectId is not null && !await CanAsync(projectId.Value, manage: true)) return Forbid();
        if (projectId is null && !User.IsInRole(Roles.Admin)) return Forbid();
        var removed = await store.UpdateAsync(state => state.Messages.RemoveAll(x => projectId is null || x.ProjectId == projectId));
        return Back($"Cleared {removed} messages.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearChats(Guid? projectId)
    {
        if (projectId is not null && !await CanAsync(projectId.Value, manage: true)) return Forbid();
        if (projectId is null && !User.IsInRole(Roles.Admin)) return Forbid();
        var removed = await store.UpdateAsync(state => state.Conversations.RemoveAll(item => projectId is null || item.ProjectId == projectId));
        return Back($"Cleared {removed} chats.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryResultPush(Guid id, CancellationToken cancellationToken)
    {
        var job = await store.ReadAsync(s => s.Jobs.FirstOrDefault(x => x.Id == id));
        if (job is null) return NotFound();
        if (!await CanAsync(job.ProjectId, run: true)) return Forbid();
        if (string.IsNullOrWhiteSpace(job.ResultFolder)) return Back("This job has no result folder to push.");

        var target = await store.ReadAsync(state =>
        {
            var project = state.Projects.FirstOrDefault(x => x.Id == job.ProjectId);
            return project?.ResultsRepositoryId is long repositoryId
                ? state.GitHubConnections.SelectMany(connection => connection.Repositories.Select(repository => new { Connection = connection, Repository = repository, Project = project })).FirstOrDefault(x => x.Repository.Id == repositoryId)
                : null;
        });
        if (target is null) return Back("Select a Git result destination for this project first.");

        try
        {
            var localFolder = await workspace.ResolveProjectPathAsync(job.ProjectId, job.ResultFolder);
            var resultName = Path.GetFileName(job.ResultFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var commitUrl = await gitHub.PushResultFolderAsync(target.Connection, target.Repository, localFolder, StateStore.ProjectRepositoryFolder(target.Project), resultName, cancellationToken);
            await store.UpdateAsync(state => { var current = state.Jobs.First(x => x.Id == id); current.ResultsCommitUrl = commitUrl; current.ResultsPushError = ""; state.Messages.Add(new AppMessage { ProjectId = current.ProjectId, JobId = current.Id, Level = MessageLevels.Info, Text = "Git result push completed." }); return true; });
            return Back("Git push completed.");
        }
        catch (Exception ex)
        {
            await store.UpdateAsync(state => { var current = state.Jobs.First(x => x.Id == id); current.ResultsPushError = ex.Message; state.Messages.Add(new AppMessage { ProjectId = current.ProjectId, JobId = current.Id, Level = MessageLevels.Warning, Text = $"Git result push failed: {ex.Message}" }); return true; });
            return Back("Git push failed: " + ex.Message);
        }
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DiscoverModels(string providerId, string baseUrl, string? apiKey, string? authenticationType,
        string? apiKeyHeaderName, Guid? connectionId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var input = await store.ReadAsync(state => new
        {
            Provider = state.AiProviderCatalog.FirstOrDefault(item => item.Id.Equals(providerId ?? "", StringComparison.OrdinalIgnoreCase)),
            Connection = connectionId is Guid id ? state.Models.FirstOrDefault(item => item.Id == id) : null
        });
        if (input.Provider is null) return BadRequest(new { message = "Unknown AI provider." });
        try
        {
            var result = input.Connection is null
                ? await modelDiscovery.DiscoverAsync(input.Provider, baseUrl ?? "", apiKey ?? "", authenticationType, apiKeyHeaderName, forceRefresh, cancellationToken)
                : await modelDiscovery.DiscoverSavedAsync(input.Provider, input.Connection, forceRefresh, cancellationToken);
            return Ok(new { result.Models, result.DiscoveredCount, result.Message, result.Cached });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreateModel(string name, string baseUrl, string modelId, string apiKey, string? providerId, List<string>? capabilities, string? authenticationType, string? apiKeyHeaderName, int priority = 100, bool isEnabled = false)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return Back("A valid HTTP(S) base URL is required.");
        await store.UpdateAsync(state =>
        {
            var provider = string.IsNullOrWhiteSpace(providerId) ? "custom" : providerId.Trim();
            var suggested = AiConnectionCatalogService.SuggestedCapabilities(state.AiProviderCatalog, provider, modelId.Trim());
            var model = new RemoteModel
            {
                Name = name.Trim(), ProviderId = provider, BaseUrl = baseUrl.TrimEnd('/'), ModelId = modelId.Trim(),
                ProtectedApiKey = modelClient.ProtectKey(apiKey), IsEnabled = isEnabled, Priority = Math.Clamp(priority, 1, 999),
                Capabilities = AiConnectionCatalogService.NormalizeSelectedCapabilities(state.AiProviderCatalog, provider, modelId.Trim(), capabilities), SuggestedCapabilities = suggested
            };
            AiConnectionCatalogService.ApplyProviderAuthentication(state.AiProviderCatalog, model, authenticationType, apiKeyHeaderName);
            state.Models.Add(model);
            return true;
        });
        return Connections("AI connection added.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> UpdateModel(Guid id, string name, string baseUrl, string modelId, string? apiKey, string? providerId, string? authenticationType, string? apiKeyHeaderName, int priority = 100)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return Connections("A valid HTTP(S) base URL is required.");
        await store.UpdateAsync(state =>
        {
            var model = state.Models.First(item => item.Id == id);
            model.Name = name.Trim(); model.ProviderId = providerId?.Trim() ?? "custom"; model.BaseUrl = baseUrl.TrimEnd('/');
            model.ModelId = modelId.Trim(); model.Priority = Math.Clamp(priority, 1, 999);
            if (!string.IsNullOrWhiteSpace(apiKey)) model.ProtectedApiKey = modelClient.ProtectKey(apiKey);
            model.SuggestedCapabilities = AiConnectionCatalogService.SuggestedCapabilities(state.AiProviderCatalog, model.ProviderId, model.ModelId);
            model.Capabilities = AiConnectionCatalogService.NormalizeSelectedCapabilities(state.AiProviderCatalog, model.ProviderId, model.ModelId, model.Capabilities);
            AiConnectionCatalogService.ApplyProviderAuthentication(state.AiProviderCatalog, model, authenticationType, apiKeyHeaderName);
            return true;
        });
        return Connections("AI connection updated.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> SetModelCapability(Guid id, string capability, bool enabled)
    {
        if (!AiCapabilities.All.Contains(capability)) return BadRequest(new { message = "Unknown capability." });
        await store.UpdateAsync(state =>
        {
            var model = state.Models.First(item => item.Id == id);
            var allowed = AiConnectionCatalogService.SuggestedCapabilities(state.AiProviderCatalog, model.ProviderId, model.ModelId);
            var exactModel = state.AiProviderCatalog.FirstOrDefault(item => item.Id.Equals(model.ProviderId, StringComparison.OrdinalIgnoreCase))?.Models.Any(item => item.Id.Equals(model.ModelId, StringComparison.OrdinalIgnoreCase)) == true;
            if (exactModel && !allowed.Contains(capability, StringComparer.OrdinalIgnoreCase)) return false;
            var selected = model.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (enabled) selected.Add(capability); else selected.Remove(capability);
            model.Capabilities = selected.Where(AiCapabilities.All.Contains).ToList();
            return true;
        });
        return Request.Headers.XRequestedWith == "XMLHttpRequest" ? Ok(new { saved = true }) : Connections("Capability updated.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ToggleModel(Guid id)
    {
        await store.UpdateAsync(state => { var model = state.Models.First(item => item.Id == id); model.IsEnabled = !model.IsEnabled; return true; });
        return Connections("AI connection status updated.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> TestModel(Guid id, CancellationToken cancellationToken)
    {
        var model = await store.ReadAsync(state => state.Models.First(item => item.Id == id));
        try
        {
            var message = await modelClient.TestAsync(model, cancellationToken);
            await store.UpdateAsync(state => { var current = state.Models.First(item => item.Id == id); current.LastTestStatus = "Succeeded"; current.LastTestMessage = message; current.LastTestedAt = DateTimeOffset.UtcNow; return true; });
            return Connections(message);
        }
        catch (Exception ex)
        {
            var message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await store.UpdateAsync(state => { var current = state.Models.First(item => item.Id == id); current.LastTestStatus = "Failed"; current.LastTestMessage = message; current.LastTestedAt = DateTimeOffset.UtcNow; return true; });
            return Connections("Test failed: " + message);
        }
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> BulkModels(List<Guid>? selectedIds, string action)
    {
        var ids = (selectedIds ?? []).ToHashSet();
        await store.UpdateAsync(state =>
        {
            if (action == "delete")
            {
                state.Models.RemoveAll(model => ids.Contains(model.Id));
                foreach (var project in state.Projects.Where(project => project.DefaultModelId is Guid modelId && ids.Contains(modelId))) project.DefaultModelId = null;
            }
            else foreach (var model in state.Models.Where(model => ids.Contains(model.Id))) model.IsEnabled = action == "activate";
            return true;
        });
        return Connections($"Updated {ids.Count} AI connection(s).");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteModel(Guid id)
    {
        await store.UpdateAsync(state => { state.Models.RemoveAll(x => x.Id == id); foreach (var p in state.Projects.Where(x => x.DefaultModelId == id)) p.DefaultModelId = null; return true; });
        return Connections("AI connection deleted.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreateStorageConnection(string name, string provider, string? endpoint, string? region, string bucket, string? prefix, string accessKey, string secretKey, string? sessionToken, bool forcePathStyle = false, bool isEnabled = false, string? returnUrl = null)
    {
        if (!ObjectStorageProviders.All.Contains(provider)) return Connections("Unknown object-storage provider.", returnUrl);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(bucket)) return Connections("Connection name and bucket are required.", returnUrl);
        if (string.IsNullOrWhiteSpace(accessKey) || (!ObjectStorageProviders.IsBunny(provider) && string.IsNullOrWhiteSpace(secretKey)))
            return Connections(ObjectStorageProviders.IsBunny(provider) ? "The storage-zone API password is required." : "Access key and secret key are required.", returnUrl);
        region = string.IsNullOrWhiteSpace(region) ? ObjectStorageProviders.DefaultRegion(provider) : region.Trim();
        endpoint = ObjectStorageProviders.ResolveEndpoint(provider, endpoint, region, accessKey);
        if ((!string.IsNullOrWhiteSpace(endpoint) && !ValidEndpoint(endpoint)) || (ObjectStorageProviders.EndpointIsRequired(provider) && !ValidEndpoint(endpoint)))
            return Connections($"A valid HTTP(S) endpoint is required for {provider}.", returnUrl);
        await store.UpdateAsync(state =>
        {
            if (isEnabled) foreach (var existing in state.ObjectStorageConnections) existing.IsEnabled = false;
            state.ObjectStorageConnections.Add(new ObjectStorageConnection
            {
                Name = name.Trim(), Provider = provider, Endpoint = endpoint, Region = region, Bucket = bucket.Trim(),
                Prefix = string.IsNullOrWhiteSpace(prefix) ? "dan-loop/projects" : prefix.Trim().Trim('/'),
                ProtectedAccessKey = objectStorage.ProtectSecret(accessKey), ProtectedSecretKey = objectStorage.ProtectSecret(secretKey),
                ProtectedSessionToken = objectStorage.ProtectSecret(sessionToken ?? ""), ForcePathStyle = forcePathStyle || ObjectStorageProviders.DefaultForcePathStyle(provider), IsEnabled = isEnabled
            });
            return true;
        });
        await objectStorage.RefreshAsync();
        return Connections("Object-storage connection added.", returnUrl);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> UpdateStorageConnection(Guid id, string name, string provider, string? endpoint, string? region, string bucket, string? prefix, string? accessKey, string? secretKey, string? sessionToken, bool forcePathStyle = false)
    {
        if (!ObjectStorageProviders.All.Contains(provider)) return Connections("Unknown object-storage provider.");
        region = string.IsNullOrWhiteSpace(region) ? ObjectStorageProviders.DefaultRegion(provider) : region.Trim();
        endpoint = ObjectStorageProviders.ResolveEndpoint(provider, endpoint, region, accessKey);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(bucket)) return Connections("Connection name and bucket are required.");
        if ((!string.IsNullOrWhiteSpace(endpoint) && !ValidEndpoint(endpoint)) || (ObjectStorageProviders.EndpointIsRequired(provider) && !ValidEndpoint(endpoint)))
            return Connections($"A valid HTTP(S) endpoint is required for {provider}.");
        await store.UpdateAsync(state =>
        {
            var connection = state.ObjectStorageConnections.First(item => item.Id == id);
            connection.Name = name.Trim(); connection.Provider = provider; connection.Endpoint = endpoint; connection.Region = region;
            connection.Bucket = bucket.Trim(); connection.Prefix = string.IsNullOrWhiteSpace(prefix) ? "dan-loop/projects" : prefix.Trim().Trim('/');
            connection.ForcePathStyle = forcePathStyle || ObjectStorageProviders.DefaultForcePathStyle(provider);
            if (!string.IsNullOrWhiteSpace(accessKey)) connection.ProtectedAccessKey = objectStorage.ProtectSecret(accessKey);
            if (!string.IsNullOrWhiteSpace(secretKey)) connection.ProtectedSecretKey = objectStorage.ProtectSecret(secretKey);
            if (!string.IsNullOrWhiteSpace(sessionToken)) connection.ProtectedSessionToken = objectStorage.ProtectSecret(sessionToken);
            return true;
        });
        await objectStorage.RefreshAsync();
        return Connections("Object-storage connection updated.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> TestStorageConnection(Guid id, CancellationToken cancellationToken)
    {
        var connection = await store.ReadAsync(state => state.ObjectStorageConnections.First(item => item.Id == id));
        try
        {
            var message = await objectStorage.TestAsync(connection, cancellationToken);
            await store.UpdateAsync(state => { var current = state.ObjectStorageConnections.First(item => item.Id == id); current.LastTestStatus = "Succeeded"; current.LastTestMessage = message; current.LastTestedAt = DateTimeOffset.UtcNow; return true; });
            return Connections(message);
        }
        catch (Exception ex)
        {
            var message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await store.UpdateAsync(state => { var current = state.ObjectStorageConnections.First(item => item.Id == id); current.LastTestStatus = "Failed"; current.LastTestMessage = message; current.LastTestedAt = DateTimeOffset.UtcNow; return true; });
            return Connections("Storage test failed: " + message);
        }
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ToggleStorageConnection(Guid id)
    {
        await store.UpdateAsync(state =>
        {
            var selected = state.ObjectStorageConnections.First(item => item.Id == id);
            var enable = !selected.IsEnabled;
            foreach (var connection in state.ObjectStorageConnections) connection.IsEnabled = enable && connection.Id == id;
            return true;
        });
        await objectStorage.RefreshAsync();
        return Connections("Object-storage connection status updated.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteStorageConnection(Guid id)
    {
        await store.UpdateAsync(state => state.ObjectStorageConnections.RemoveAll(item => item.Id == id));
        await objectStorage.RefreshAsync();
        return Connections("Object-storage connection deleted.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> BulkStorageConnections(List<Guid>? selectedIds, string action)
    {
        var ids = (selectedIds ?? []).ToHashSet();
        await store.UpdateAsync(state =>
        {
            if (action == "delete") state.ObjectStorageConnections.RemoveAll(item => ids.Contains(item.Id));
            else if (action == "deactivate") foreach (var item in state.ObjectStorageConnections.Where(item => ids.Contains(item.Id))) item.IsEnabled = false;
            else if (action == "activate")
            {
                var selected = state.ObjectStorageConnections.FirstOrDefault(item => ids.Contains(item.Id));
                foreach (var item in state.ObjectStorageConnections) item.IsEnabled = item.Id == selected?.Id;
            }
            return true;
        });
        await objectStorage.RefreshAsync();
        return Connections($"Updated {ids.Count} object-storage connection(s). Only one storage connection can be active at a time.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreateUser(string username, string password, string role)
    {
        username = username.Trim();
        if (username.Length < 3 || password.Length < 10) return Users("Username must be 3+ characters and password 10+ characters.");
        var created = await store.UpdateAsync(state =>
        {
            if (state.Users.Any(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase))) return false;
            state.Users.Add(new AppUser { Username = username, PasswordHash = passwords.Hash(password), Role = role == Roles.Admin ? Roles.Admin : Roles.Member });
            return true;
        });
        return Users(created ? "User created." : "Username already exists.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> UpdateUser(Guid id, string role, string? password)
    {
        if (!string.IsNullOrEmpty(password) && password.Length < 10) return Users("A new password must be at least 10 characters.");
        var result = await store.UpdateAsync(state =>
        {
            var user = state.Users.FirstOrDefault(item => item.Id == id);
            if (user is null) return "User was not found.";
            var nextRole = role == Roles.Admin ? Roles.Admin : Roles.Member;
            if (id == CurrentUserId() && user.Role != nextRole) return "You cannot change your own role while signed in.";
            if (user.Role == Roles.Admin && nextRole != Roles.Admin && state.Users.Count(item => item.Role == Roles.Admin) <= 1)
                return "The final administrator cannot be changed to a member.";
            user.Role = nextRole;
            if (!string.IsNullOrEmpty(password))
            {
                user.PasswordHash = passwords.Hash(password);
                user.AccessFailedCount = 0;
                user.LockoutEnd = null;
            }
            return "User updated.";
        });
        return Users(result);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteUser(Guid id)
    {
        if (id == CurrentUserId()) return Users("You cannot delete your own account.");
        var result = await store.UpdateAsync(state =>
        {
            var user = state.Users.FirstOrDefault(item => item.Id == id);
            if (user is null) return "User was not found.";
            if (user.Role == Roles.Admin && state.Users.Count(item => item.Role == Roles.Admin) <= 1)
                return "The final administrator cannot be deleted.";
            state.Users.Remove(user);
            state.Permissions.RemoveAll(item => item.UserId == id);
            state.PendingUploads.RemoveAll(item => item.UserId == id);
            return "User and their project permissions were deleted.";
        });
        return Users(result);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ToggleUser(Guid id)
    {
        if (id == CurrentUserId()) return Users("You cannot disable your own account.");
        await store.UpdateAsync(state => { var user = state.Users.First(x => x.Id == id); user.IsEnabled = !user.IsEnabled; if (user.IsEnabled) { user.AccessFailedCount = 0; user.LockoutEnd = null; } return true; });
        return Users("User status changed.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> UnlockUser(Guid id)
    {
        var username = await store.UpdateAsync(state =>
        {
            var user = state.Users.FirstOrDefault(item => item.Id == id);
            if (user is null) return null;
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            return user.Username;
        });
        if (username is null) return Users("User was not found.");
        loginSecurity.ClearUser(username);
        return Users("User sign-in lock was cleared.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> SetPermission(Guid userId, Guid projectId, bool canView, bool canManage, bool canRun)
    {
        await store.UpdateAsync(state =>
        {
            state.Permissions.RemoveAll(x => x.UserId == userId && x.ProjectId == projectId);
            if (canView || canManage || canRun) state.Permissions.Add(new ProjectPermission { UserId = userId, ProjectId = projectId, CanView = canView || canManage || canRun, CanManage = canManage, CanRun = canRun });
            return true;
        });
        return Users("Permissions updated.");
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> CanAsync(Guid projectId, bool manage = false, bool run = false)
    {
        if (User.IsInRole(Roles.Admin)) return true;
        var id = CurrentUserId();
        return await store.ReadAsync(s => s.Permissions.Any(x => x.UserId == id && x.ProjectId == projectId && (!manage || x.CanManage) && (!run || x.CanRun)));
    }

    private static List<string> NormalizeCapabilities(IEnumerable<string>? capabilities) =>
        (capabilities ?? []).Where(AiCapabilities.All.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool ValidEndpoint(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private IActionResult Connections(string message, string? returnUrl = null) { TempData["Message"] = message; return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : Redirect("/#connections"); }
    private IActionResult Users(string message) { TempData["Message"] = message; return Redirect("/#access"); }
    private IActionResult Steps(PlanStepMutationResult result)
    {
        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return result.Success ? Ok(new { result.Message }) : BadRequest(new { result.Message });
        TempData["Message"] = result.Message;
        return Redirect("/#steps");
    }
    private IActionResult Back(string message) { TempData["Message"] = message; return RedirectToAction("Index", "Home"); }
}
