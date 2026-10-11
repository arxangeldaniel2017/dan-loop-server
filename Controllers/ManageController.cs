using System.Security.Claims;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DanLoopServer.Controllers;

[Authorize]
public sealed class ManageController(StateStore store, PasswordService passwords, RemoteModelClient modelClient, WorkspaceService workspace, GitHubService gitHub, ProjectPlanService plans) : Controller
{
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreateProject(string? name, string? description, string? systemPrompt, Guid? defaultModelId)
    {
        if (string.IsNullOrWhiteSpace(name)) return Back("Project name is required.");
        await store.UpdateAsync(state => { state.Projects.Add(new Project { Name = name.Trim(), Description = description?.Trim() ?? "", SystemPrompt = systemPrompt?.Trim() ?? "", DefaultModelId = defaultModelId }); return true; });
        return Back("Project created.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProject(Guid id, string? name, string? description, string? systemPrompt, Guid? defaultModelId)
    {
        if (!await CanAsync(id, manage: true)) return Forbid();
        if (string.IsNullOrWhiteSpace(name)) return Back("Project name is required.");
        await store.UpdateAsync(state => { var x = state.Projects.First(p => p.Id == id); x.Name = name.Trim(); x.Description = description?.Trim() ?? ""; x.SystemPrompt = systemPrompt?.Trim() ?? ""; x.DefaultModelId = defaultModelId; return true; });
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
        if (maxExecutions is <= 0) return Back("Execution limit must be a positive number or left blank for unlimited.");
        await store.UpdateAsync(state => { state.Loops.Add(new LoopDefinition { ProjectId = projectId, Name = name.Trim(), Prompt = prompt.Trim(), IntervalSeconds = Math.Clamp(intervalSeconds, 10, 2_592_000), MaxExecutions = maxExecutions, IsEnabled = isEnabled }); return true; });
        return Back("Loop created.");
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
        await store.UpdateAsync(s => { var x = s.Loops.First(y => y.Id == id); x.IsEnabled = !x.IsEnabled; x.LastQueuedAt = null; return true; });
        return Back("Loop status changed.");
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
    public async Task<IActionResult> RunJob(Guid projectId, string prompt, Guid? modelId, Guid? conversationId, List<string>? attachmentPaths)
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
            var job = new Job { ProjectId = projectId, Prompt = prompt.Trim(), ModelId = modelId ?? project.DefaultModelId, CreatedByUserId = userId, ConversationId = conversation.Id, AttachmentPaths = attachmentPaths?.Distinct().ToList() ?? [] };
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
        await store.UpdateAsync(s => { var x = s.Jobs.First(y => y.Id == id); if (x.Status is JobStatuses.Queued or JobStatuses.Running) x.Status = JobStatuses.Cancelled; return true; });
        return Back("Job cancelled.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPlanStepRetryLimit(Guid projectId, Guid stepId, int? retryLimit)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        if (retryLimit is <= 0) return Back("Retry limit must be a positive number or blank for unlimited.");
        await plans.SetRetryLimitAsync(projectId, stepId, retryLimit);
        return Back(retryLimit is null ? "Step retries are unlimited." : $"Step retry limit set to {retryLimit}.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryPlanStep(Guid projectId, Guid stepId)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        await plans.RetryAsync(projectId, stepId);
        return Back("Step queued for another attempt.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SkipPlanStep(Guid projectId, Guid stepId)
    {
        if (!await CanAsync(projectId, manage: true)) return Forbid();
        await plans.SkipAsync(projectId, stepId);
        return Back("Step skipped. The next eligible step can continue.");
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
        if (target is null) return Back("Select a GitHub result destination for this project first.");

        try
        {
            var localFolder = await workspace.ResolveProjectPathAsync(job.ProjectId, job.ResultFolder);
            var resultName = Path.GetFileName(job.ResultFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var commitUrl = await gitHub.PushResultFolderAsync(target.Connection, target.Repository, localFolder, StateStore.ProjectRepositoryFolder(target.Project), resultName, cancellationToken);
            await store.UpdateAsync(state => { var current = state.Jobs.First(x => x.Id == id); current.ResultsCommitUrl = commitUrl; current.ResultsPushError = ""; state.Messages.Add(new AppMessage { ProjectId = current.ProjectId, JobId = current.Id, Level = MessageLevels.Info, Text = "GitHub result push completed." }); return true; });
            return Back("GitHub push completed.");
        }
        catch (Exception ex)
        {
            await store.UpdateAsync(state => { var current = state.Jobs.First(x => x.Id == id); current.ResultsPushError = ex.Message; state.Messages.Add(new AppMessage { ProjectId = current.ProjectId, JobId = current.Id, Level = MessageLevels.Warning, Text = $"GitHub result push failed: {ex.Message}" }); return true; });
            return Back("GitHub push failed: " + ex.Message);
        }
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreateModel(string name, string baseUrl, string modelId, string apiKey, bool isEnabled = false)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return Back("A valid HTTP(S) base URL is required.");
        await store.UpdateAsync(state => { state.Models.Add(new RemoteModel { Name = name.Trim(), BaseUrl = baseUrl.TrimEnd('/'), ModelId = modelId.Trim(), ProtectedApiKey = modelClient.ProtectKey(apiKey), IsEnabled = isEnabled }); return true; });
        return Back("Remote model added.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteModel(Guid id)
    {
        await store.UpdateAsync(state => { state.Models.RemoveAll(x => x.Id == id); foreach (var p in state.Projects.Where(x => x.DefaultModelId == id)) p.DefaultModelId = null; return true; });
        return Back("Remote model deleted.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreateUser(string username, string password, string role)
    {
        username = username.Trim();
        if (username.Length < 3 || password.Length < 10) return Back("Username must be 3+ characters and password 10+ characters.");
        var created = await store.UpdateAsync(state =>
        {
            if (state.Users.Any(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase))) return false;
            state.Users.Add(new AppUser { Username = username, PasswordHash = passwords.Hash(password), Role = role == Roles.Admin ? Roles.Admin : Roles.Member });
            return true;
        });
        return Back(created ? "User created." : "Username already exists.");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ToggleUser(Guid id)
    {
        if (id == CurrentUserId()) return Back("You cannot disable your own account.");
        await store.UpdateAsync(state => { var user = state.Users.First(x => x.Id == id); user.IsEnabled = !user.IsEnabled; return true; });
        return Back("User status changed.");
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
        return Back("Permissions updated.");
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> CanAsync(Guid projectId, bool manage = false, bool run = false)
    {
        if (User.IsInRole(Roles.Admin)) return true;
        var id = CurrentUserId();
        return await store.ReadAsync(s => s.Permissions.Any(x => x.UserId == id && x.ProjectId == projectId && (!manage || x.CanManage) && (!run || x.CanRun)));
    }

    private IActionResult Back(string message) { TempData["Message"] = message; return RedirectToAction("Index", "Home"); }
}
