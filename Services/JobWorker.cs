using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class JobWorker(StateStore store, RemoteModelClient client, AgentRuntimeService runtime, WorkspaceService workspace, GitHubService gitHub, ProjectPlanService plans, ILogger<JobWorker> logger) : BackgroundService
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
                    var job = state.Jobs.OrderBy(x => x.CreatedAt).FirstOrDefault(x => x.Status == JobStatuses.Queued);
                    if (job is null) return null;
                    job.Status = JobStatuses.Running;
                    job.StartedAt = DateTimeOffset.UtcNow;
                    return new Job { Id = job.Id, ProjectId = job.ProjectId, LoopId = job.LoopId, ModelId = job.ModelId, Prompt = job.Prompt, ConversationId = job.ConversationId, AttachmentPaths = job.AttachmentPaths, PlanStepId = job.PlanStepId, PlanStepTitle = job.PlanStepTitle };
                });
                if (next is not null) await RunAsync(next, stoppingToken);
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
            if (loop.LastQueuedAt is not null && now - loop.LastQueuedAt < TimeSpan.FromSeconds(Math.Max(10, loop.IntervalSeconds))) continue;
            if (loop.MaxExecutions is int maximum && executionCount >= maximum)
            {
                loop.IsEnabled = false;
                state.Messages.Add(new AppMessage { ProjectId = loop.ProjectId, Level = MessageLevels.Info, Text = $"Loop '{loop.Name}' reached its execution limit of {maximum} and was paused." });
                continue;
            }
            var project = state.Projects.FirstOrDefault(x => x.Id == loop.ProjectId);
            if (project?.DefaultModelId is null) continue;
            var conversation = new Conversation { ProjectId = loop.ProjectId, Title = loop.Name };
            state.Conversations.Add(conversation);
            var job = new Job
            {
                ProjectId = loop.ProjectId, LoopId = loop.Id, ModelId = project.DefaultModelId,
                Prompt = loop.Prompt, CreatedByUserId = Guid.Empty, CreatedAt = now, ConversationId = conversation.Id
            };
            state.Jobs.Add(job);
            loop.ExecutionCount++;
            conversation.Messages.Add(new ConversationMessage { JobId = job.Id, Role = "user", Content = job.Prompt });
            loop.LastQueuedAt = now;
        }
        return true;
    });

    private async Task RunAsync(Job job, CancellationToken cancellationToken)
    {
        var context = await store.ReadAsync(state =>
        {
            var project = state.Projects.FirstOrDefault(x => x.Id == job.ProjectId);
            return new
            {
                Project = project,
                Model = state.Models.FirstOrDefault(x => x.Id == job.ModelId && x.IsEnabled),
                Conversation = state.Conversations.FirstOrDefault(x => x.Id == job.ConversationId),
                RuntimeSettings = state.AgentRuntime.Settings,
                ResultsTarget = project?.ResultsRepositoryId is long repositoryId
                    ? state.GitHubConnections.SelectMany(connection => connection.Repositories.Select(repository => new { Connection = connection, Repository = repository })).FirstOrDefault(x => x.Repository.Id == repositoryId)
                    : null
            };
        });
        if (context.Project is null || context.Model is null)
        {
            await FailAsync(job.Id, "Project or enabled remote model was not found.");
            return;
        }
        try
        {
            var assignedStep = await plans.PrepareJobAsync(job.ProjectId, job.Id, cancellationToken);
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
            if (context.RuntimeSettings.Enabled)
            {
                runtimeResult = await runtime.RunAsync(context.Model, context.Project, job, messages, prompt, images, context.RuntimeSettings, cancellationToken);
                output = runtimeResult.Output;
                await plans.SynchronizeFromWorkspaceAsync(job.ProjectId, cancellationToken);
                if (runtimeResult.Outcome == AgentRunOutcomes.ChangesCompleted) await plans.CompleteJobAsync(job.Id, runtimeResult);
            }
            else output = await client.CompleteAsync(context.Model, context.Project.SystemPrompt + "\n\nYou may create result files by returning fenced blocks in the form ```file:relative/path.ext followed by text content, or ```base64-file:relative/path.ext followed by base64 data. Source project files are read-only; all generated files are saved in a new result folder.", messages, prompt, images, cancellationToken);
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
                        ? "Project plan created. The next loop run will implement its first unchecked task."
                        : $"Implementation step completed with {current.ProjectFilesChanged.Count(path => !path.Equals("PLAN.md", StringComparison.OrdinalIgnoreCase))} project file change(s), and PLAN.md was updated.";
                    if (backgroundPublishPaths.Count > 0) success += $" {backgroundPublishPaths.Count} generated file{(backgroundPublishPaths.Count == 1 ? " is" : "s are")} queued for background GitHub synchronization.";
                    state.Messages.Add(new AppMessage { ProjectId = current.ProjectId, JobId = current.Id, Level = string.IsNullOrWhiteSpace(pushError) ? MessageLevels.Info : MessageLevels.Warning, Text = string.IsNullOrWhiteSpace(pushError) ? success : $"{success} The run-report GitHub push failed: {pushError}" });
                }
                current.CompletedAt = DateTimeOffset.UtcNow;
                return true;
            });
            if (backgroundPublishPaths.Count > 0)
                await workspace.QueueAiGeneratedAsync(job.ProjectId, backgroundPublishPaths, publishSoon: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
            throw;
        }
        catch (Exception ex)
        {
            await plans.FailJobAsync(job.Id, ex.Message);
            await FailAsync(job.Id, ex.Message);
        }
    }

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
