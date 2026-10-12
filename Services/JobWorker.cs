using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class JobWorker(StateStore store, RemoteModelClient client, AgentRuntimeService runtime, WorkspaceService workspace, GitProviderService gitHub, ProjectPlanService plans, JobCancellationRegistry cancellations, ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScheduleLoopsAsync();
                var next = await store.UpdateAsync(state =>
                {
                    var job = state.Jobs.OrderByDescending(x => x.RunImmediately).ThenBy(x => x.CreatedAt).FirstOrDefault(x => x.Status == JobStatuses.Queued);
                    if (job is null) return null;
                    job.Status = JobStatuses.Running;
                    job.StartedAt = DateTimeOffset.UtcNow;
                    return new Job { Id = job.Id, ProjectId = job.ProjectId, LoopId = job.LoopId, ModelId = job.ModelId, RequiredCapability = job.RequiredCapability, FailedModelIds = job.FailedModelIds.ToList(), Prompt = job.Prompt, ConversationId = job.ConversationId, AttachmentPaths = job.AttachmentPaths, PlanStepId = job.PlanStepId, PlanStepTitle = job.PlanStepTitle, RunImmediately = job.RunImmediately };
                });
                if (next is not null)
                {
                    using var execution = cancellations.Register(next.Id, stoppingToken);
                    var timeoutMinutes = await store.ReadAsync(state => Math.Clamp(state.Projects.FirstOrDefault(project => project.Id == next.ProjectId)?.JobTimeoutMinutes ?? 15, 1, 15));
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
                    var stillRunning = await store.ReadAsync(state => state.Jobs.FirstOrDefault(item => item.Id == next.Id)?.Status == JobStatuses.Running);
                    if (stillRunning) await RunAsync(next, timeout.Token, stoppingToken, timeoutMinutes);
                }
                else await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job worker iteration failed");
                await Task.Delay(3000, stoppingToken);
            }
        }
    }

    private Task ScheduleLoopsAsync() => store.UpdateAsync(state =>
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var loop in state.Loops.Where(x => x.IsEnabled))
        {
            var executionCount = Math.Max(loop.ExecutionCount, state.Jobs.Count(x => x.LoopId == loop.Id));
            loop.ExecutionCount = executionCount;
            // A recurring loop owns one execution at a time. Slow work must not build an
            // ever-growing queue behind the active execution.
            if (state.Jobs.Any(job => job.LoopId == loop.Id && job.Status is JobStatuses.Queued or JobStatuses.Running)) continue;
            var plan = state.ExecutionPlans.FirstOrDefault(item => item.LoopId == loop.Id);
            if (plan is { Steps.Count: > 0 } && plan.Steps.All(step => step.Status is PlanStepStatuses.Succeeded or PlanStepStatuses.Skipped or PlanStepStatuses.Failed))
            {
                loop.IsEnabled = false;
                var failed = plan.Steps.Count(step => step.Status == PlanStepStatuses.Failed);
                var skipped = plan.Steps.Count(step => step.Status == PlanStepStatuses.Skipped);
                var detail = failed > 0
                    ? $"stopped after {failed} fixed plan step{(failed == 1 ? "" : "s")} exhausted the configured retries"
                    : $"finished all {plan.Steps.Count} fixed plan steps{(skipped > 0 ? $" ({skipped} skipped)" : "")}";
                state.Messages.Add(new AppMessage { ProjectId = loop.ProjectId, Level = failed > 0 ? MessageLevels.Warning : MessageLevels.Info, Text = $"Loop '{loop.Name}' {detail}. Create a new loop to define additional steps." });
                continue;
            }
            var project = state.Projects.FirstOrDefault(x => x.Id == loop.ProjectId);
            if (project is null || !state.Models.Any(model => model.IsEnabled)) continue;
            var nextAt = loop.LastQueuedAt?.AddSeconds(Math.Max(10, loop.IntervalSeconds)) ?? DateTimeOffset.MinValue;
            var lastCompletedAt = state.Jobs.Where(job => job.ProjectId == loop.ProjectId && job.CompletedAt is not null).Max(job => job.CompletedAt);
            if (lastCompletedAt is DateTimeOffset completedAt)
            {
                var afterCooldown = completedAt.AddSeconds(Math.Clamp(project.TaskDelaySeconds, 0, 86_400));
                if (afterCooldown > nextAt) nextAt = afterCooldown;
            }
            if (now < nextAt) continue;
            if (loop.MaxExecutions is int maximum && executionCount >= maximum)
            {
                loop.IsEnabled = false;
                state.Messages.Add(new AppMessage { ProjectId = loop.ProjectId, Level = MessageLevels.Info, Text = $"Loop '{loop.Name}' reached its execution limit of {maximum} and was paused." });
                continue;
            }
            var conversation = new Conversation { ProjectId = loop.ProjectId, Title = loop.Name };
            state.Conversations.Add(conversation);
            var job = new Job
            {
                ProjectId = loop.ProjectId, LoopId = loop.Id, ModelId = project.DefaultModelId, RequiredCapability = AiCapabilities.Orchestrator,
                Prompt = loop.Prompt, CreatedByUserId = Guid.Empty, CreatedAt = now, ConversationId = conversation.Id
            };
            state.Jobs.Add(job);
            loop.ExecutionCount++;
            conversation.Messages.Add(new ConversationMessage { JobId = job.Id, Role = "user", Content = job.Prompt });
            loop.LastQueuedAt = now;
        }
        return true;
    });

    private async Task RunAsync(Job job, CancellationToken cancellationToken, CancellationToken applicationStoppingToken, int timeoutMinutes)
    {
        var requiredCapability = string.IsNullOrWhiteSpace(job.RequiredCapability)
            ? AiCapabilities.Orchestrator
            : job.RequiredCapability;
        var context = await store.ReadAsync(state =>
        {
            var project = state.Projects.FirstOrDefault(x => x.Id == job.ProjectId);
            var excluded = job.FailedModelIds.ToHashSet();
            var models = state.Models.Where(model => model.IsEnabled && !excluded.Contains(model.Id) && AiCapabilities.Supports(model, requiredCapability))
                .OrderBy(model => model.Id == job.ModelId ? 0 : model.Id == project?.DefaultModelId ? 1 : 2)
                .ThenBy(model => model.Priority).ThenBy(model => model.Name).ToList();
            return new
            {
                Project = project,
                Models = models,
                Conversation = state.Conversations.FirstOrDefault(x => x.Id == job.ConversationId),
                RuntimeSettings = state.AgentRuntime.Settings,
                ResultsTarget = project?.ResultsRepositoryId is long repositoryId
                    ? state.GitHubConnections.SelectMany(connection => connection.Repositories.Select(repository => new { Connection = connection, Repository = repository })).FirstOrDefault(x => x.Repository.Id == repositoryId)
                    : null
            };
        });
        var selectedModel = context.Models.FirstOrDefault();
        if (context.Project is null || selectedModel is null)
        {
            await FailAsync(job.Id, $"No enabled AI connection supports the {requiredCapability} capability.");
            return;
        }
        await store.UpdateAsync(state => { var current = state.Jobs.First(item => item.Id == job.Id); current.ModelId = selectedModel.Id; current.RequiredCapability = requiredCapability; return true; });
        try
        {
            var assignedStep = await plans.PrepareJobAsync(job.Id);
            if (assignedStep is not null) { job.PlanStepId = assignedStep.Id; job.PlanStepTitle = assignedStep.Title; }
            var prompt = job.Prompt;
            var images = new List<string>();
            if (job.AttachmentPaths.Count > 0)
            {
                var attached = new List<string>();
                foreach (var path in job.AttachmentPaths)
                {
                    var image = await workspace.ReadImageDataUrlAsync(job.ProjectId, path);
                    if (image is not null) images.Add(image); else attached.Add(await workspace.ReadAttachmentAsync(job.ProjectId, path));
                }
                prompt += "\n\nPROJECT FILES PROVIDED FOR THIS REQUEST:\n" + string.Join("\n\n", attached);
            }
            var messages = context.Conversation?.Messages ?? [new ConversationMessage { JobId = job.Id, Role = "user", Content = job.Prompt }];
            foreach (var message in messages.Where(x => x.Role == "user" && x.FilePaths.Count > 0))
            {
                var fileContext = new List<string>();
                foreach (var path in message.FilePaths) fileContext.Add(await workspace.ReadAttachmentAsync(job.ProjectId, path));
                message.Content += "\n\nPROJECT FILES PROVIDED FOR THIS MESSAGE:\n" + string.Join("\n\n", fileContext);
            }
            AgentRuntimeResult? runtimeResult = null;
            string output;
            var hasPlan = await plans.HasPlanAsync(job.Id);
            if (!hasPlan)
            {
                var planningPrompt = context.Project.SystemPrompt + """

                    Create the complete fixed execution-step list for this job. Do not use tools and do not create or modify any project file. Return only strict JSON in this exact shape: {"steps":["First small verifiable implementation step","Second small verifiable implementation step"]}. Define 3–12 ordered steps that fully cover the requested work. These database-backed steps become the loop's immutable source of truth; later runs cannot add or restructure them.
                    """;
                output = await client.CompleteAsync(selectedModel, planningPrompt, messages, prompt, images, cancellationToken);
                await plans.CreatePlanAsync(job.Id, output);
                runtimeResult = new AgentRuntimeResult { Output = output, Outcome = AgentRunOutcomes.PlanCreated };
            }
            else if (assignedStep is null)
            {
                throw new InvalidOperationException("The loop has no eligible fixed step to execute.");
            }
            else if (context.RuntimeSettings.Enabled)
            {
                runtimeResult = await runtime.RunAsync(selectedModel, context.Project, job, messages, prompt, images, context.RuntimeSettings, cancellationToken);
                output = runtimeResult.Output;
                if (runtimeResult.Outcome == AgentRunOutcomes.ChangesCompleted) await plans.CompleteJobAsync(job.Id, runtimeResult);
            }
            else output = await client.CompleteAsync(selectedModel, context.Project.SystemPrompt + "\n\nYou may create result files by returning fenced blocks in the form ```file:relative/path.ext followed by text content, or ```base64-file:relative/path.ext followed by base64 data. Source project files are read-only; all generated files are saved in a new result folder.", messages, prompt, images, cancellationToken);
            var resultFolder = await workspace.CreateResultFolderAsync(job.ProjectId, job.Id);
            var resultItem = await workspace.WriteResultTextAsync(job.ProjectId, resultFolder, "response.md", output);
            var generatedItems = await workspace.WriteGeneratedFilesAsync(job.ProjectId, resultFolder, output);
            var commitUrl = "";
            var pushError = "";
            if (context.ResultsTarget is not null)
            {
                try
                {
                    var localFolder = await workspace.ResolveProjectPathAsync(job.ProjectId, resultFolder);
                    commitUrl = await gitHub.PushResultFolderAsync(context.ResultsTarget.Connection, context.ResultsTarget.Repository, localFolder, StateStore.ProjectRepositoryFolder(context.Project), Path.GetFileName(resultFolder), cancellationToken);
                }
                catch (Exception ex) { pushError = ex.Message; logger.LogWarning(ex, "Could not push results for job {JobId}", job.Id); }
            }
            var backgroundPublishPaths = new HashSet<string>(runtimeResult?.ChangedPaths ?? [], StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(commitUrl))
            {
                backgroundPublishPaths.Add(resultItem.RelativePath);
                foreach (var item in generatedItems) backgroundPublishPaths.Add(item.RelativePath);
            }
            await store.UpdateAsync(state =>
            {
                var current = state.Jobs.First(x => x.Id == job.Id);
                if (current.Status != JobStatuses.Cancelled)
                {
                    var planned = runtimeResult?.Outcome == AgentRunOutcomes.PlanCreated;
                    current.Status = planned ? JobStatuses.Planned : JobStatuses.Completed;
                    current.WorkflowOutcome = runtimeResult?.Outcome ?? AgentRunOutcomes.ChangesCompleted;
                    current.ProjectFilesChanged = runtimeResult?.ChangedPaths.ToList() ?? [];
                    current.ProjectCommitUrl = runtimeResult?.PublishUrl ?? "";
                    current.Output = output; current.ResultFolder = resultFolder; current.ResultItems.Add(resultItem); current.ResultItems.AddRange(generatedItems); current.ResultsCommitUrl = commitUrl; current.ResultsPushError = pushError;
                    var conversation = state.Conversations.FirstOrDefault(x => x.Id == current.ConversationId);
                    if (conversation is not null) { conversation.Messages.Add(new ConversationMessage { JobId = current.Id, Role = "assistant", Content = output, FilePaths = current.ResultItems.Select(x => x.RelativePath).ToList() }); conversation.UpdatedAt = DateTimeOffset.UtcNow; }
                    var success = planned
                        ? "Fixed loop steps created in server state. The next loop run will implement the first step."
                        : $"Implementation step completed with {current.ProjectFilesChanged.Count} project file change(s).";
                    if (backgroundPublishPaths.Count > 0) success += $" {backgroundPublishPaths.Count} generated file{(backgroundPublishPaths.Count == 1 ? " is" : "s are")} queued for background Git synchronization.";
                    state.Messages.Add(new AppMessage { ProjectId = current.ProjectId, JobId = current.Id, Level = string.IsNullOrWhiteSpace(pushError) ? MessageLevels.Info : MessageLevels.Warning, Text = string.IsNullOrWhiteSpace(pushError) ? success : $"{success} The run-report Git push failed: {pushError}" });
                }
                current.CompletedAt = DateTimeOffset.UtcNow;
                return true;
            });
            if (backgroundPublishPaths.Count > 0)
                await workspace.QueueAiGeneratedAsync(job.ProjectId, backgroundPublishPaths, publishSoon: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var wasCancelledByUser = await store.ReadAsync(state => state.Jobs.FirstOrDefault(item => item.Id == job.Id)?.Status == JobStatuses.Cancelled);
            if (wasCancelledByUser)
            {
                await plans.CancelJobsAsync([job.Id], "Execution stopped because its job or recurring loop was stopped.");
                return;
            }
            if (!applicationStoppingToken.IsCancellationRequested)
            {
                var error = $"Job timed out after {timeoutMinutes} minute{(timeoutMinutes == 1 ? "" : "s")}.";
                await plans.FailJobAsync(job.Id, error);
                await FailAsync(job.Id, error);
                return;
            }
            await plans.InterruptJobAsync(job.Id);
            await store.UpdateAsync(state =>
            {
                var current = state.Jobs.First(x => x.Id == job.Id);
                if (current.Status == JobStatuses.Running)
                {
                    current.Status = JobStatuses.Queued;
                    current.StartedAt = null;
                    state.Messages.Add(new AppMessage { ProjectId = current.ProjectId, JobId = current.Id, Level = MessageLevels.Warning, Text = "Job was interrupted by a server restart and returned to the queue." });
                }
                return true;
            });
            if (applicationStoppingToken.IsCancellationRequested) throw;
        }
        catch (Exception ex)
        {
            await plans.FailJobAsync(job.Id, ex.Message);
            if (!await RetryWithNextModelAsync(job.Id, selectedModel, requiredCapability, ex.Message))
                await FailAsync(job.Id, ex.Message);
        }
    }

    private Task<bool> RetryWithNextModelAsync(Guid jobId, RemoteModel failedModel, string capability, string error) => store.UpdateAsync(state =>
    {
        var job = state.Jobs.First(item => item.Id == jobId);
        if (!job.FailedModelIds.Contains(failedModel.Id)) job.FailedModelIds.Add(failedModel.Id);
        var hasFallback = state.Models.Any(model => model.IsEnabled && !job.FailedModelIds.Contains(model.Id) && AiCapabilities.Supports(model, capability));
        if (!hasFallback) return false;
        job.Status = JobStatuses.Queued; job.StartedAt = null; job.CompletedAt = null; job.Error = ""; job.ModelId = null;
        state.Messages.Add(new AppMessage { ProjectId = job.ProjectId, JobId = job.Id, Level = MessageLevels.Warning, Text = $"{failedModel.Name} failed: {error}. Retrying with the next {capability} connection." });
        return true;
    });

    private Task FailAsync(Guid id, string error) => store.UpdateAsync(state =>
    {
        var job = state.Jobs.First(x => x.Id == id);
        job.Status = JobStatuses.Failed;
        job.Error = error;
        job.CompletedAt = DateTimeOffset.UtcNow;
        state.Messages.Add(new AppMessage { ProjectId = job.ProjectId, JobId = job.Id, Level = MessageLevels.Error, Text = $"Job failed: {error}" });
        return true;
    });
}
