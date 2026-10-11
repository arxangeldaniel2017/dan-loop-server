using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class JobWorker(StateStore store, RemoteModelClient client, ILogger<JobWorker> logger) : BackgroundService
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
                    return new Job { Id = job.Id, ProjectId = job.ProjectId, LoopId = job.LoopId, ModelId = job.ModelId, Prompt = job.Prompt };
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
            if (loop.LastQueuedAt is not null && now - loop.LastQueuedAt < TimeSpan.FromSeconds(Math.Max(10, loop.IntervalSeconds))) continue;
            var project = state.Projects.FirstOrDefault(x => x.Id == loop.ProjectId);
            if (project?.DefaultModelId is null) continue;
            state.Jobs.Add(new Job
            {
                ProjectId = loop.ProjectId, LoopId = loop.Id, ModelId = project.DefaultModelId,
                Prompt = loop.Prompt, CreatedByUserId = Guid.Empty, CreatedAt = now
            });
            loop.LastQueuedAt = now;
        }
        return true;
    });

    private async Task RunAsync(Job job, CancellationToken cancellationToken)
    {
        var context = await store.ReadAsync(state => new
        {
            Project = state.Projects.FirstOrDefault(x => x.Id == job.ProjectId),
            Model = state.Models.FirstOrDefault(x => x.Id == job.ModelId && x.IsEnabled)
        });
        if (context.Project is null || context.Model is null)
        {
            await FailAsync(job.Id, "Project or enabled remote model was not found.");
            return;
        }
        try
        {
            var output = await client.CompleteAsync(context.Model, context.Project.SystemPrompt, job.Prompt, cancellationToken);
            await store.UpdateAsync(state =>
            {
                var current = state.Jobs.First(x => x.Id == job.Id);
                if (current.Status != JobStatuses.Cancelled) { current.Status = JobStatuses.Completed; current.Output = output; }
                current.CompletedAt = DateTimeOffset.UtcNow;
                return true;
            });
        }
        catch (Exception ex) { await FailAsync(job.Id, ex.Message); }
    }

    private Task FailAsync(Guid id, string error) => store.UpdateAsync(state =>
    {
        var job = state.Jobs.First(x => x.Id == id);
        job.Status = JobStatuses.Failed;
        job.Error = error;
        job.CompletedAt = DateTimeOffset.UtcNow;
        return true;
    });
}
