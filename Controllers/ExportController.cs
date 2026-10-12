using System.Globalization;
using System.Security.Claims;
using System.Text;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DanLoopServer.Controllers;

[Authorize]
public sealed class ExportController(StateStore store, WorkspaceService workspace) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Jobs()
    {
        var view = await VisibleStateAsync();
        var projects = view.State.Projects.ToDictionary(item => item.Id);
        var loops = view.State.Loops.ToDictionary(item => item.Id);
        var rows = view.State.Jobs.Where(job => view.ProjectIds.Contains(job.ProjectId)).OrderByDescending(job => job.CreatedAt).Select(job => new object?[]
        {
            job.Id, projects.GetValueOrDefault(job.ProjectId)?.Name, job.LoopId is Guid loopId ? loops.GetValueOrDefault(loopId)?.Name : "",
            job.Status, Iso(job.CreatedAt), Iso(job.StartedAt), Iso(job.CompletedAt), DurationSeconds(job.StartedAt, job.CompletedAt),
            job.Prompt, job.PlanStepTitle, job.WorkflowOutcome, string.Join("; ", job.ProjectFilesChanged), job.ProjectCommitUrl,
            string.Join("; ", job.ResultItems.Select(item => item.RelativePath)), job.ResultsCommitUrl, job.ResultsPushError, job.Error, job.Output
        });
        return Csv("jobs", ["Job ID", "Project", "Loop", "Status", "Created UTC", "Started UTC", "Ended UTC", "Duration seconds", "Prompt", "Plan step", "Outcome", "Changed project files", "Project commit", "Result files", "Results commit", "Results push error", "Error", "AI output"], rows);
    }

    [HttpGet]
    public async Task<IActionResult> Chats()
    {
        var view = await VisibleStateAsync();
        var projects = view.State.Projects.ToDictionary(item => item.Id);
        var conversations = view.State.Conversations.Where(item => view.ProjectIds.Contains(item.ProjectId)).ToList();
        var conversationIds = conversations.Select(item => item.Id).ToHashSet();
        var rows = new List<object?[]>();
        foreach (var conversation in conversations)
        {
            var projectName = projects.GetValueOrDefault(conversation.ProjectId)?.Name;
            rows.AddRange(conversation.Messages.Select(message => new object?[] { conversation.Id, conversation.Title, projectName, Iso(message.CreatedAt), message.Role, "Message", message.JobId, string.Join("; ", message.FilePaths), message.Content }));
            foreach (var job in view.State.Jobs.Where(job => job.ConversationId == conversation.Id))
                rows.AddRange(job.AiResponses.Select(response => new object?[] { conversation.Id, conversation.Title, projectName, Iso(response.CreatedAt), "assistant", response.Kind, job.Id, "", response.Content }));
        }
        return Csv("chats", ["Conversation ID", "Conversation", "Project", "Created UTC", "Role", "Kind", "Job ID", "Files", "Content"], rows.OrderByDescending(row => row[3]?.ToString()));
    }

    [HttpGet]
    public async Task<IActionResult> Messages()
    {
        var view = await VisibleStateAsync();
        var projects = view.State.Projects.ToDictionary(item => item.Id);
        var rows = view.State.Messages.Where(message => message.ProjectId is null || view.ProjectIds.Contains(message.ProjectId.Value)).OrderByDescending(message => message.CreatedAt).Select(message => new object?[]
        {
            message.Id, message.ProjectId is Guid projectId ? projects.GetValueOrDefault(projectId)?.Name : "System", message.JobId,
            message.Level, Iso(message.CreatedAt), message.Text
        });
        return Csv("messages", ["Message ID", "Project", "Job ID", "Level", "Created UTC", "Message"], rows);
    }

    [HttpGet]
    public async Task<IActionResult> Projects()
    {
        var view = await VisibleStateAsync();
        var rows = view.State.Projects.Where(project => view.ProjectIds.Contains(project.Id)).OrderBy(project => project.Name).Select(project => new object?[]
        {
            project.Id, project.Name, project.Description, Iso(project.CreatedAt), project.WorkspaceFolder, StateStore.ProjectRepositoryFolder(project),
            view.State.Loops.Count(loop => loop.ProjectId == project.Id), view.State.Jobs.Count(job => job.ProjectId == project.Id),
            project.AutoPublishPendingPaths.Count, Iso(project.LastAutoPublishAt), project.LastAutoPublishCommitUrl, project.LastAutoPublishError
        });
        return Csv("projects", ["Project ID", "Name", "Description", "Created UTC", "Workspace", "Repository folder", "Loops", "Jobs", "Pending GitHub files", "Last GitHub sync UTC", "Last GitHub commit", "Last GitHub error"], rows);
    }

    [HttpGet]
    public async Task<IActionResult> Loops()
    {
        var view = await VisibleStateAsync();
        var projects = view.State.Projects.ToDictionary(item => item.Id);
        var rows = view.State.Loops.Where(loop => view.ProjectIds.Contains(loop.ProjectId)).OrderBy(loop => projects.GetValueOrDefault(loop.ProjectId)?.Name).ThenBy(loop => loop.Name).Select(loop => new object?[]
        {
            loop.Id, projects.GetValueOrDefault(loop.ProjectId)?.Name, loop.Name, loop.IsEnabled, loop.IntervalSeconds,
            loop.MaxExecutions?.ToString(CultureInfo.InvariantCulture) ?? "Unlimited", loop.ExecutionCount, Iso(loop.LastQueuedAt), loop.Prompt
        });
        return Csv("loops", ["Loop ID", "Project", "Name", "Enabled", "Interval seconds", "Execution limit", "Execution count", "Last queued UTC", "Prompt"], rows);
    }

    [HttpGet]
    public async Task<IActionResult> Plans()
    {
        var view = await VisibleStateAsync();
        var projects = view.State.Projects.ToDictionary(item => item.Id);
        var rows = view.State.ExecutionPlans.Where(plan => view.ProjectIds.Contains(plan.ProjectId)).SelectMany(plan => plan.Steps.SelectMany(step =>
        {
            IEnumerable<PlanStepAttempt?> attempts = step.Attempts.Count == 0
                ? new PlanStepAttempt?[] { null }
                : step.Attempts.Select(item => (PlanStepAttempt?)item);
            return attempts.Select(attempt => new object?[]
            {
                projects.GetValueOrDefault(plan.ProjectId)?.Name, step.Id, step.Order, step.Title, step.Status,
                step.RetryLimit?.ToString(CultureInfo.InvariantCulture) ?? "Unlimited", attempt?.Number, attempt?.Status,
                Iso(attempt?.StartedAt), Iso(attempt?.CompletedAt), attempt?.Summary, attempt?.Error,
                attempt is null ? "" : string.Join("; ", attempt.ChangedPaths), attempt?.CommitUrl
            });
        })).ToList();
        return Csv("execution-plan", ["Project", "Step ID", "Order", "Step", "Step status", "Retry limit", "Attempt", "Attempt status", "Started UTC", "Ended UTC", "Summary", "Error", "Changed paths", "Commit"], rows);
    }

    [HttpGet]
    public async Task<IActionResult> Files()
    {
        var view = await VisibleStateAsync();
        var rows = new List<object?[]>();
        foreach (var project in view.State.Projects.Where(project => view.ProjectIds.Contains(project.Id)).OrderBy(project => project.Name))
        {
            foreach (var path in await workspace.ListFilesAsync(project.Id))
            {
                var full = await workspace.ResolveProjectPathAsync(project.Id, path);
                var info = new FileInfo(full);
                rows.Add([project.Name, path, info.Length, Iso(info.LastWriteTimeUtc), Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), project.UserManagedPaths.Contains(path, StringComparer.OrdinalIgnoreCase), project.AutoPublishPendingPaths.Contains(path, StringComparer.OrdinalIgnoreCase)]);
            }
        }
        return Csv("files", ["Project", "Path", "Size bytes", "Modified UTC", "Type", "User managed", "Pending GitHub sync"], rows);
    }

    [HttpGet, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Tools()
    {
        var state = await store.ReadAsync(value => value);
        var projects = state.Projects.ToDictionary(item => item.Id);
        var rows = state.AgentRuntime.AuditTrail.OrderByDescending(item => item.StartedAt).Select(item => new object?[]
        {
            item.Id, projects.GetValueOrDefault(item.ProjectId)?.Name, item.JobId, item.Step, item.Tool, item.Status,
            Iso(item.StartedAt), Iso(item.CompletedAt), item.Summary, item.Output
        });
        return Csv("tool-activity", ["Activity ID", "Project", "Job ID", "Step", "Tool", "Status", "Started UTC", "Ended UTC", "Summary", "Output"], rows);
    }

    [HttpGet, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Models()
    {
        var state = await store.ReadAsync(value => value);
        return Csv("models", ["Connection ID", "Provider", "Name", "Model identifier", "Base URL", "Priority", "Capabilities", "Enabled", "Last test"], state.Models.Select(item => new object?[] { item.Id, item.ProviderId, item.Name, item.ModelId, item.BaseUrl, item.Priority, string.Join(", ", AiCapabilities.Effective(item)), item.IsEnabled, item.LastTestStatus }));
    }

    [HttpGet, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> GitHub()
    {
        var state = await store.ReadAsync(value => value);
        var rows = state.GitHubConnections.SelectMany(connection => connection.Repositories.Select(repository => new object?[]
        {
            connection.AccountLogin, connection.AuthenticationType, Iso(connection.ConnectedAt), Iso(connection.RefreshedAt), repository.Id,
            repository.FullName, repository.DefaultBranch, repository.IsPrivate, repository.HtmlUrl,
            string.Join("; ", state.ProjectRepositories.Where(link => link.GitHubConnectionId == connection.Id && link.RepositoryId == repository.Id).Select(link => state.Projects.FirstOrDefault(project => project.Id == link.ProjectId)?.Name))
        }));
        return Csv("github", ["Account", "Authentication", "Connected UTC", "Refreshed UTC", "Repository ID", "Repository", "Default branch", "Private", "URL", "Projects"], rows);
    }

    [HttpGet, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Access()
    {
        var state = await store.ReadAsync(value => value);
        var rows = state.Users.SelectMany(user =>
        {
            if (user.Role == Roles.Admin) return new[] { new object?[] { user.Id, user.Username, user.Role, user.IsEnabled, "All projects", true, true, true, Iso(user.CreatedAt) } };
            var permissions = state.Permissions.Where(item => item.UserId == user.Id).ToList();
            if (permissions.Count == 0) return new[] { new object?[] { user.Id, user.Username, user.Role, user.IsEnabled, "", false, false, false, Iso(user.CreatedAt) } };
            return permissions.Select(permission => new object?[] { user.Id, user.Username, user.Role, user.IsEnabled, state.Projects.FirstOrDefault(project => project.Id == permission.ProjectId)?.Name, permission.CanView, permission.CanManage, permission.CanRun, Iso(user.CreatedAt) });
        });
        return Csv("access", ["User ID", "Username", "Role", "Enabled", "Project", "Can view", "Can manage", "Can run", "Created UTC"], rows);
    }

    private async Task<(AppState State, HashSet<Guid> ProjectIds)> VisibleStateAsync()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = User.IsInRole(Roles.Admin);
        var state = await store.ReadAsync(value => value);
        var projectIds = isAdmin
            ? state.Projects.Select(project => project.Id).ToHashSet()
            : state.Permissions.Where(permission => permission.UserId == userId && permission.CanView).Select(permission => permission.ProjectId).ToHashSet();
        return (state, projectIds);
    }

    private FileContentResult Csv(string name, IReadOnlyList<string> headers, IEnumerable<object?[]> rows)
    {
        var output = new StringBuilder("\uFEFF");
        output.AppendLine(string.Join(',', headers.Select(Cell)));
        foreach (var row in rows) output.AppendLine(string.Join(',', row.Select(value => Cell(value?.ToString() ?? ""))));
        return File(Encoding.UTF8.GetBytes(output.ToString()), "text/csv; charset=utf-8", $"dan-loop-{name}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    private static string Cell(string value)
    {
        var safe = value.Length > 0 && value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' ? "'" + value : value;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }

    private static string Iso(DateTimeOffset? value) => value?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "";
    private static string Iso(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static double? DurationSeconds(DateTimeOffset? start, DateTimeOffset? end) => start is null ? null : Math.Round(((end ?? DateTimeOffset.UtcNow) - start.Value).TotalSeconds, 3);
}
