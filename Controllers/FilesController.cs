using System.Security.Claims;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace DanLoopServer.Controllers;

[Authorize]
public sealed class FilesController(StateStore store, WorkspaceService workspace) : Controller
{
    public async Task<IActionResult> Index(Guid projectId, string? path = null)
    {
        var project = await GetProjectAsync(projectId, manage: false);
        if (project is null) return Forbid();
        await SetDashboardShellAsync();
        try { return View(await workspace.BuildViewAsync(project, path)); }
        catch (DirectoryNotFoundException) { return NotFound(); }
        catch (InvalidOperationException) { return BadRequest(); }
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(262_144_000)]
    public async Task<IActionResult> Upload(Guid projectId, List<IFormFile> files)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        try
        {
            var pending = await workspace.StageAsync(projectId, CurrentUserId(), files);
            var details = await workspace.GetPendingAsync(pending.Id, CurrentUserId());
            if (details.Conflicts.Count == 0 && !pending.Items.Any(x => x.IsZip))
            {
                await workspace.CommitPendingAsync(pending, "keep", false);
                return Back(projectId, "Files uploaded.");
            }
            return RedirectToAction(nameof(ConfirmUpload), new { id = pending.Id });
        }
        catch (Exception ex) { return Back(projectId, ex.Message); }
    }

    [HttpGet]
    public async Task<IActionResult> ConfirmUpload(Guid id)
    {
        try
        {
            var data = await workspace.GetPendingAsync(id, CurrentUserId());
            if (await GetProjectAsync(data.Project.Id, manage: true) is null) return Forbid();
            await SetDashboardShellAsync();
            return View(new UploadConfirmationViewModel { Project = data.Project, Upload = data.Upload, Conflicts = data.Conflicts, ContainsZip = data.Upload.Items.Any(x => x.IsZip) });
        }
        catch { return NotFound(); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmUpload(Guid id, string conflictAction, string zipAction)
    {
        try
        {
            var data = await workspace.GetPendingAsync(id, CurrentUserId());
            if (await GetProjectAsync(data.Project.Id, manage: true) is null) return Forbid();
            if (zipAction == "cancel" || conflictAction == "cancel")
            {
                await workspace.CancelPendingAsync(id);
                return Back(data.Project.Id, "Upload cancelled.");
            }
            await workspace.CommitPendingAsync(data.Upload, conflictAction, zipAction == "extract");
            return Back(data.Project.Id, zipAction == "extract" ? "Files uploaded and ZIP archives extracted." : "Files uploaded.");
        }
        catch (Exception ex) { return RedirectToAction(nameof(ConfirmUpload), new { id, error = ex.Message }); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFolder(Guid projectId, string? path, string name)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        try
        {
            var relative = WorkspaceService.NormalizeRelativePath(Path.Combine(path ?? "", name));
            Directory.CreateDirectory(await workspace.ResolveProjectPathAsync(projectId, relative));
            return Back(projectId, "Folder created.", path);
        }
        catch (Exception ex) { return Back(projectId, ex.Message, path); }
    }

    [HttpGet]
    public async Task<IActionResult> Preview(Guid projectId, string path)
    {
        if (await GetProjectAsync(projectId, manage: false) is null) return Forbid();
        try
        {
            var full = await workspace.ResolveProjectPathAsync(projectId, path);
            if (!System.IO.File.Exists(full)) return NotFound();
            if (WorkspaceService.IsText(full))
            {
                if (new FileInfo(full).Length > 2 * 1024 * 1024) return BadRequest("Text preview is limited to 2 MB.");
                var project = (await GetProjectAsync(projectId, manage: false))!;
                await SetDashboardShellAsync();
                return View("Editor", new TextEditorViewModel { Project = project, Path = WorkspaceService.NormalizeRelativePath(path), Content = await System.IO.File.ReadAllTextAsync(full), Language = Path.GetExtension(full).TrimStart('.').ToLowerInvariant() });
            }
            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(full, out var contentType)) contentType = "application/octet-stream";
            return PhysicalFile(full, contentType, enableRangeProcessing: true);
        }
        catch (InvalidOperationException) { return BadRequest(); }
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(3_000_000)]
    public async Task<IActionResult> SaveText(Guid projectId, string path, string content)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        try
        {
            var full = await workspace.ResolveProjectPathAsync(projectId, path);
            if (!System.IO.File.Exists(full) || !WorkspaceService.IsText(full)) return BadRequest();
            var root = await workspace.ResolveProjectPathAsync(projectId, "");
            var backup = Path.Combine(root, ".history", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"), WorkspaceService.NormalizeRelativePath(path));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            System.IO.File.Copy(full, backup);
            await System.IO.File.WriteAllTextAsync(full, content ?? "");
            TempData["Message"] = "File saved; the previous version was retained in project history.";
            return RedirectToAction(nameof(Preview), new { projectId, path });
        }
        catch (Exception ex) { return Back(projectId, ex.Message); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid projectId, string path)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        try
        {
            var full = await workspace.ResolveProjectPathAsync(projectId, path);
            var root = await workspace.ResolveProjectPathAsync(projectId, "");
            var trash = Path.Combine(root, ".trash", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"), WorkspaceService.NormalizeRelativePath(path));
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            if (System.IO.File.Exists(full)) System.IO.File.Move(full, trash);
            else if (Directory.Exists(full)) Directory.Move(full, trash);
            else return NotFound();
            return Back(projectId, "Item moved to recoverable project trash.");
        }
        catch (Exception ex) { return Back(projectId, ex.Message); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSelected(Guid projectId, string? path, List<string>? selectedPaths)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        if (selectedPaths is null || selectedPaths.Count == 0) return Back(projectId, "Select at least one file or folder.", path);
        try
        {
            var moved = await workspace.MoveToTrashAsync(projectId, selectedPaths);
            return Back(projectId, $"{moved} selected item{(moved == 1 ? "" : "s")} moved to recoverable project trash.", path);
        }
        catch (Exception ex) { return Back(projectId, ex.Message, path); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoResult(Guid jobId, string path)
    {
        var job = await store.ReadAsync(state => state.Jobs.FirstOrDefault(x => x.Id == jobId));
        if (job is null || await GetProjectAsync(job.ProjectId, manage: true) is null) return Forbid();
        var item = job.ResultItems.FirstOrDefault(x => x.RelativePath == path && !x.IsUndone);
        if (item is null) return NotFound();
        var full = await workspace.ResolveProjectPathAsync(job.ProjectId, path);
        if (System.IO.File.Exists(full)) System.IO.File.Delete(full); else if (Directory.Exists(full)) Directory.Delete(full, true);
        await store.UpdateAsync(state => { state.Jobs.First(x => x.Id == jobId).ResultItems.First(x => x.RelativePath == path).IsUndone = true; return true; });
        return Back(job.ProjectId, "Result change undone.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoAll(Guid jobId)
    {
        var job = await store.ReadAsync(state => state.Jobs.FirstOrDefault(x => x.Id == jobId));
        if (job is null || await GetProjectAsync(job.ProjectId, manage: true) is null) return Forbid();
        if (!string.IsNullOrWhiteSpace(job.ResultFolder))
        {
            var folder = await workspace.ResolveProjectPathAsync(job.ProjectId, job.ResultFolder);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        await store.UpdateAsync(state => { foreach (var item in state.Jobs.First(x => x.Id == jobId).ResultItems) item.IsUndone = true; return true; });
        return Back(job.ProjectId, "All result changes undone.");
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private async Task SetDashboardShellAsync()
    {
        var userId = CurrentUserId();
        var isAdmin = User.IsInRole(Roles.Admin);
        var summary = await store.ReadAsync(state =>
        {
            var projectIds = isAdmin
                ? state.Projects.Select(project => project.Id).ToHashSet()
                : state.Permissions.Where(permission => permission.UserId == userId && permission.CanView).Select(permission => permission.ProjectId).ToHashSet();
            return new
            {
                ProjectCount = state.Projects.Count(project => projectIds.Contains(project.Id)),
                LiveLoopCount = state.Loops.Count(loop => projectIds.Contains(loop.ProjectId) && loop.IsEnabled),
                ActiveJobCount = state.Jobs.Count(job => projectIds.Contains(job.ProjectId) && job.Status is JobStatuses.Running or JobStatuses.Queued)
            };
        });
        ViewBag.DashboardProjectCount = summary.ProjectCount;
        ViewBag.DashboardLiveLoopCount = summary.LiveLoopCount;
        ViewBag.DashboardActiveJobCount = summary.ActiveJobCount;
        ViewBag.DashboardIsAdmin = isAdmin;
    }
    private async Task<Project?> GetProjectAsync(Guid id, bool manage)
    {
        if (User.IsInRole(Roles.Admin)) return await store.ReadAsync(state => state.Projects.FirstOrDefault(x => x.Id == id));
        var userId = CurrentUserId();
        return await store.ReadAsync(state => state.Permissions.Any(x => x.ProjectId == id && x.UserId == userId && x.CanView && (!manage || x.CanManage)) ? state.Projects.FirstOrDefault(x => x.Id == id) : null);
    }
    private IActionResult Back(Guid projectId, string message, string? path = null) { TempData["Message"] = message; return RedirectToAction(nameof(Index), new { projectId, path }); }
}
