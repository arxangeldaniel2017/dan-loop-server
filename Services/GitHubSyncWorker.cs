using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class GitHubSyncWorker(
    StateStore store,
    WorkspaceService workspace,
    AgentToolExecutor tools,
    ILogger<GitHubSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);
    private const int ImmediateBatchSize = 10;
    private const int MaximumBatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextReconciliation = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextReconciliation)
                {
                    await ReconcileCompletedJobsAsync();
                    nextReconciliation = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
                }
                await PublishDueProjectsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background Git sync iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
        }
    }

    private Task ReconcileCompletedJobsAsync() => store.UpdateAsync(state =>
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var project in state.Projects)
        {
            var userPaths = project.UserManagedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending = project.AutoPublishPendingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var job in state.Jobs.Where(job => job.ProjectId == project.Id && job.Status is JobStatuses.Completed or JobStatuses.Planned))
            {
                job.PublishedProjectFiles ??= [];
                var published = job.PublishedProjectFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in job.ProjectFilesChanged.Where(path => !string.IsNullOrWhiteSpace(path) && !published.Contains(path) && !userPaths.Contains(path)))
                    pending.Add(path);
            }
            if (pending.Count == project.AutoPublishPendingPaths.Count) continue;
            project.AutoPublishPendingPaths = pending.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            project.AutoPublishQueuedAt ??= now;
        }
        return true;
    });

    private async Task PublishDueProjectsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var due = await store.ReadAsync(state =>
        {
            if (!state.AgentRuntime.Settings.AllowGitHubPublish) return [];
            var linkedProjects = state.ProjectRepositories
                .Where(link => state.GitHubConnections.Any(connection =>
                    connection.Id == link.GitHubConnectionId &&
                    connection.Repositories.Any(repository => repository.Id == link.RepositoryId)))
                .Select(link => link.ProjectId).ToHashSet();
            return state.Projects
                .Where(project => linkedProjects.Contains(project.Id) && project.AutoPublishPendingPaths.Count > 0)
                .Where(project => project.NextAutoPublishAttemptAt is null || project.NextAutoPublishAttemptAt <= now)
                .Where(project => project.AutoPublishPendingPaths.Count >= ImmediateBatchSize ||
                                  project.AutoPublishQueuedAt is DateTimeOffset queued && now - queued >= QuietPeriod)
                .Select(project => new
                {
                    project.Id,
                    project.Name,
                    Paths = project.AutoPublishPendingPaths.Take(MaximumBatchSize).ToList(),
                    UserPaths = project.UserManagedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
                }).ToList();
        });

        foreach (var project in due)
        {
            var existing = new List<string>();
            foreach (var path in project.Paths.Where(path => !project.UserPaths.Contains(path)))
            {
                try
                {
                    var full = await workspace.ResolveProjectPathAsync(project.Id, path);
                    if (File.Exists(full)) existing.Add(path);
                }
                catch (InvalidOperationException) { }
            }

            if (existing.Count == 0)
            {
                await store.UpdateAsync(state =>
                {
                    var current = state.Projects.First(item => item.Id == project.Id);
                    current.AutoPublishPendingPaths.RemoveAll(path => project.Paths.Contains(path, StringComparer.OrdinalIgnoreCase));
                    if (current.AutoPublishPendingPaths.Count == 0) current.AutoPublishQueuedAt = null;
                    return true;
                });
                continue;
            }

            try
            {
                var url = await tools.PublishPathsAsync(project.Id, existing, $"Update {project.Name} ({existing.Count} files)", cancellationToken);
                await store.UpdateAsync(state =>
                {
                    state.Messages.Add(new AppMessage
                    {
                        ProjectId = project.Id,
                        Level = MessageLevels.Info,
                        Text = $"Background Git sync published {existing.Count} AI-created file{(existing.Count == 1 ? "" : "s")}. {url}"
                    });
                    return true;
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background Git sync failed for project {ProjectId}", project.Id);
                await store.UpdateAsync(state =>
                {
                    var current = state.Projects.First(item => item.Id == project.Id);
                    var shouldReport = !string.Equals(current.LastAutoPublishError, ex.Message, StringComparison.Ordinal);
                    current.LastAutoPublishError = ex.Message;
                    current.NextAutoPublishAttemptAt = DateTimeOffset.UtcNow + RetryDelay;
                    if (shouldReport)
                        state.Messages.Add(new AppMessage { ProjectId = project.Id, Level = MessageLevels.Warning, Text = $"Background Git sync will retry: {ex.Message}" });
                    return true;
                });
            }
        }
    }
}
