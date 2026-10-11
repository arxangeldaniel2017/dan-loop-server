using System.Security.Claims;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace DanLoopServer.Controllers;

[Authorize]
public sealed class FilesController(StateStore store, WorkspaceService workspace, AgentToolExecutor tools) : Controller
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

    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> BrowsePartial(Guid projectId, string? path = null)
    {
        var project = await GetProjectAsync(projectId, manage: false);
        if (project is null) return Forbid();
        try { return PartialView("_FileBrowser", await workspace.BuildViewAsync(project, path)); }
        catch (DirectoryNotFoundException) { return NotFound(); }
        catch (InvalidOperationException) { return BadRequest(); }
    }

    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> WorkspacePartial(Guid projectId, string? path = null)
    {
        var project = await GetProjectAsync(projectId, manage: false);
        if (project is null) return Forbid();
        try { return PartialView("_InlineWorkspace", await workspace.BuildViewAsync(project, path)); }
        catch (DirectoryNotFoundException) { return NotFound(); }
        catch (InvalidOperationException) { return BadRequest(); }
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(262_144_000)]
    public async Task<IActionResult> Upload(Guid projectId, string? path, List<IFormFile> files)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        PendingUpload? pending = null;
        try
        {
            pending = await workspace.StageAsync(projectId, CurrentUserId(), files, path);
            var details = await workspace.GetPendingAsync(pending.Id, CurrentUserId());
            if (IsAjaxRequest())
            {
                await workspace.CommitPendingAsync(pending, "keep", false);
                var note = details.Conflicts.Count > 0
                    ? "Files uploaded. Existing names were preserved and new files were saved with unique names."
                    : "Files uploaded.";
                return await FileBrowserResultAsync(projectId, path, note);
            }
            if (details.Conflicts.Count == 0 && !pending.Items.Any(x => x.IsZip))
            {
                await workspace.CommitPendingAsync(pending, "keep", false);
                return await FileBrowserResultAsync(projectId, path, "Files uploaded.");
            }
            return RedirectToAction(nameof(ConfirmUpload), new { id = pending.Id });
        }
        catch (Exception ex)
        {
            if (pending is not null) await workspace.CancelPendingAsync(pending.Id);
            return await FileBrowserResultAsync(projectId, path, $"Upload failed: {ex.Message}", true);
        }
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
                return await FileBrowserResultAsync(data.Project.Id, null, "Upload cancelled.", true);
            }
            await workspace.CommitPendingAsync(data.Upload, conflictAction, zipAction == "extract");
            return await FileBrowserResultAsync(data.Project.Id, null, zipAction == "extract" ? "Files uploaded and ZIP archives extracted." : "Files uploaded.");
        }
        catch (Exception ex)
        {
            var projectId = await store.ReadAsync(state => state.PendingUploads.FirstOrDefault(upload => upload.Id == id)?.ProjectId);
            if (projectId is Guid value) await RecordFileMessageAsync(value, $"Upload confirmation failed: {ex.Message}", true);
            return projectId is Guid failedProjectId ? Back(failedProjectId) : NotFound();
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFolder(Guid projectId, string? path, string name)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        try
        {
            var relative = WorkspaceService.NormalizeRelativePath(Path.Combine(path ?? "", name));
            Directory.CreateDirectory(await workspace.ResolveProjectPathAsync(projectId, relative));
            return await FileBrowserResultAsync(projectId, path, $"Folder created: {relative}.");
        }
        catch (Exception ex) { return await FileBrowserResultAsync(projectId, path, $"Create folder failed: {ex.Message}", true); }
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

    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> PreviewPartial(Guid projectId, string path)
    {
        var project = await GetProjectAsync(projectId, manage: false);
        if (project is null) return Forbid();
        try
        {
            var normalized = WorkspaceService.NormalizeRelativePath(path);
            var full = await workspace.ResolveProjectPathAsync(projectId, normalized);
            if (!System.IO.File.Exists(full)) return NotFound();
            var parent = string.Join('/', normalized.Split('/').SkipLast(1));
            if (WorkspaceService.IsText(full))
            {
                if (new FileInfo(full).Length > 2 * 1024 * 1024) return BadRequest("Text preview is limited to 2 MB.");
                return PartialView("_TextEditor", new TextEditorViewModel { Project = project, Path = normalized, Content = await System.IO.File.ReadAllTextAsync(full), Language = EditorLanguage(full) });
            }
            if (WorkspaceService.IsImage(full))
            {
                var extension = Path.GetExtension(full).ToLowerInvariant();
                return PartialView("_ImageEditor", new ImageEditorViewModel { Project = project, Path = normalized, ParentPath = parent, CanSave = extension is ".png" or ".jpg" or ".jpeg" or ".webp" });
            }
            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(full, out var contentType)) contentType = "application/octet-stream";
            return PartialView("_FilePreview", new FilePreviewViewModel { Project = project, Path = normalized, ParentPath = parent, ContentType = contentType });
        }
        catch (InvalidOperationException) { return BadRequest(); }
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(3_000_000)]
    public async Task<IActionResult> SaveText(Guid projectId, string path, string content, bool autosave = false)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        try
        {
            var full = await workspace.ResolveProjectPathAsync(projectId, path);
            if (!System.IO.File.Exists(full) || !WorkspaceService.IsText(full)) throw new InvalidOperationException("The file is missing or is not an editable text file.");
            var root = await workspace.ResolveProjectPathAsync(projectId, "");
            var backup = Path.Combine(root, ".history", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"), WorkspaceService.NormalizeRelativePath(path));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            System.IO.File.Copy(full, backup);
            await System.IO.File.WriteAllTextAsync(full, content ?? "");
            await workspace.MarkUserManagedAsync(projectId, [WorkspaceService.NormalizeRelativePath(path)]);
            await RecordFileMessageAsync(projectId, $"{(autosave ? "File autosaved" : "File saved")}: {WorkspaceService.NormalizeRelativePath(path)}. The previous version was retained in project history.");
            if (IsAjaxRequest())
            {
                var project = (await GetProjectAsync(projectId, manage: false))!;
                return PartialView("_TextEditor", new TextEditorViewModel { Project = project, Path = WorkspaceService.NormalizeRelativePath(path), Content = content ?? "", Language = EditorLanguage(full) });
            }
            return RedirectToAction(nameof(Preview), new { projectId, path });
        }
        catch (Exception ex) { await RecordFileMessageAsync(projectId, $"Save file failed for {path}: {ex.Message}", true); return IsAjaxRequest() ? BadRequest() : Back(projectId); }
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> SaveImage(Guid projectId, string path, string imageData)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        try
        {
            var normalized = WorkspaceService.NormalizeRelativePath(path);
            var full = await workspace.ResolveProjectPathAsync(projectId, normalized);
            var extension = Path.GetExtension(full).ToLowerInvariant();
            if (!System.IO.File.Exists(full) || extension is not (".png" or ".jpg" or ".jpeg" or ".webp")) throw new InvalidOperationException("The image is missing or its format cannot be saved.");
            var comma = imageData.IndexOf(',');
            if (comma < 0) throw new InvalidOperationException("The edited image data is invalid.");
            var bytes = Convert.FromBase64String(imageData[(comma + 1)..]);
            if (bytes.Length > 15 * 1024 * 1024) throw new InvalidOperationException("The edited image is too large.");
            var root = await workspace.ResolveProjectPathAsync(projectId, "");
            var backup = Path.Combine(root, ".history", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"), normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            System.IO.File.Copy(full, backup);
            await System.IO.File.WriteAllBytesAsync(full, bytes);
            await workspace.MarkUserManagedAsync(projectId, [normalized]);
            await RecordFileMessageAsync(projectId, $"Image saved: {normalized}. The original was retained in project history.");
            var project = (await GetProjectAsync(projectId, manage: false))!;
            return PartialView("_ImageEditor", new ImageEditorViewModel { Project = project, Path = normalized, ParentPath = string.Join('/', normalized.Split('/').SkipLast(1)), CanSave = true });
        }
        catch (Exception ex) { await RecordFileMessageAsync(projectId, $"Save image failed for {path}: {ex.Message}", true); return BadRequest(); }
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
            await RecordFileMessageAsync(projectId, $"Moved to recoverable project trash: {WorkspaceService.NormalizeRelativePath(path)}.");
            return Back(projectId);
        }
        catch (Exception ex) { await RecordFileMessageAsync(projectId, $"Delete failed for {path}: {ex.Message}", true); return Back(projectId); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSelected(Guid projectId, string? path, List<string>? selectedPaths)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        if (selectedPaths is null || selectedPaths.Count == 0)
            return await FileBrowserResultAsync(projectId, path, "Delete failed: select at least one file or folder.", true);
        try
        {
            var currentFolder = WorkspaceService.NormalizeRelativePath(path ?? "", allowEmpty: true);
            var scopedPaths = selectedPaths
                .Select(selected => WorkspaceService.NormalizeRelativePath(selected))
                .Where(selected =>
                {
                    var separator = selected.LastIndexOf('/');
                    var parent = separator < 0 ? "" : selected[..separator];
                    return string.Equals(parent, currentFolder, StringComparison.Ordinal);
                })
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (scopedPaths.Count == 0)
                return await FileBrowserResultAsync(projectId, path, "Delete failed: no valid items were selected in this folder.", true);
            var moved = await workspace.MoveToTrashAsync(projectId, scopedPaths);
            var note = $"{moved} selected item{(moved == 1 ? "" : "s")} moved to recoverable project trash.";
            return await FileBrowserResultAsync(projectId, path, note);
        }
        catch (Exception ex) { return await FileBrowserResultAsync(projectId, path, $"Delete failed: {ex.Message}", true); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveSelected(Guid projectId, string? path, string? destinationPath, List<string>? sourcePaths)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        if (sourcePaths is null || sourcePaths.Count == 0)
            return await FileBrowserResultAsync(projectId, path, "Move failed: choose at least one file or folder.", true);
        try
        {
            var currentFolder = WorkspaceService.NormalizeRelativePath(path ?? "", allowEmpty: true);
            var destination = WorkspaceService.NormalizeRelativePath(destinationPath ?? "", allowEmpty: true);
            var scopedPaths = sourcePaths.Select(source => WorkspaceService.NormalizeRelativePath(source)).Where(source =>
            {
                var separator = source.LastIndexOf('/');
                var parentPath = separator < 0 ? "" : source[..separator];
                return string.Equals(parentPath, currentFolder, StringComparison.Ordinal);
            }).Distinct(StringComparer.Ordinal).ToList();
            if (scopedPaths.Count == 0) throw new InvalidOperationException("No valid items were selected in this folder.");
            var moved = await workspace.MoveItemsAsync(projectId, scopedPaths, destination);
            return await FileBrowserResultAsync(projectId, path, $"Moved {moved.Count} item{(moved.Count == 1 ? "" : "s")} to /{destination}." );
        }
        catch (Exception ex) { return await FileBrowserResultAsync(projectId, path, $"Move failed: {ex.Message}", true); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PublishSelected(Guid projectId, string? path, List<string>? selectedPaths, CancellationToken cancellationToken)
    {
        if (await GetProjectAsync(projectId, manage: true) is null) return Forbid();
        if (selectedPaths is null || selectedPaths.Count == 0)
            return await FileBrowserResultAsync(projectId, path, "GitHub publish failed: select at least one file or folder.", true);
        var allowed = await store.ReadAsync(state => state.AgentRuntime.Settings.AllowGitHubPublish);
        if (!allowed) return await FileBrowserResultAsync(projectId, path, "GitHub publishing is disabled under AI workspace access.", true);
        try
        {
            var files = await workspace.ExpandSelectionToFilesAsync(projectId, selectedPaths);
            if (files.Count == 0) throw new InvalidOperationException("The selection contains no publishable files.");
            var project = (await GetProjectAsync(projectId, manage: false))!;
            var url = await tools.PublishPathsAsync(projectId, files, $"Publish selected files from {project.Name}", cancellationToken);
            return await FileBrowserResultAsync(projectId, path, $"Published {files.Count} selected file{(files.Count == 1 ? "" : "s")} to GitHub. {url}");
        }
        catch (Exception ex) { return await FileBrowserResultAsync(projectId, path, $"GitHub publish failed: {ex.Message}", true); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadSelected(Guid projectId, string? path, List<string>? selectedPaths, CancellationToken cancellationToken)
    {
        var project = await GetProjectAsync(projectId, manage: false);
        if (project is null) return Forbid();
        if (selectedPaths is null || selectedPaths.Count == 0)
        {
            await RecordFileMessageAsync(projectId, "Download failed: select at least one file or folder.", true);
            return Back(projectId, path);
        }
        try
        {
            var currentFolder = WorkspaceService.NormalizeRelativePath(path ?? "", allowEmpty: true);
            var scopedPaths = selectedPaths.Select(selected => WorkspaceService.NormalizeRelativePath(selected)).Where(selected =>
            {
                var separator = selected.LastIndexOf('/');
                var parent = separator < 0 ? "" : selected[..separator];
                return string.Equals(parent, currentFolder, StringComparison.Ordinal);
            }).Distinct(StringComparer.Ordinal).ToList();
            if (scopedPaths.Count == 0) throw new InvalidOperationException("No valid items were selected in this folder.");
            var prepared = await workspace.PrepareDownloadAsync(projectId, project.Name, scopedPaths, cancellationToken);
            await RecordFileMessageAsync(projectId, prepared.IsTemporary
                ? $"Downloaded {scopedPaths.Count} selected item{(scopedPaths.Count == 1 ? "" : "s")} as {prepared.DownloadName} containing {prepared.FileCount} file{(prepared.FileCount == 1 ? "" : "s")}."
                : $"Downloaded file: {scopedPaths[0]}.");
            if (prepared.IsTemporary)
                Response.OnCompleted(() => { try { if (System.IO.File.Exists(prepared.FilePath)) System.IO.File.Delete(prepared.FilePath); } catch { } return Task.CompletedTask; });
            var contentType = "application/zip";
            if (!prepared.IsTemporary)
            {
                var provider = new FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(prepared.FilePath, out contentType)) contentType = "application/octet-stream";
            }
            return PhysicalFile(prepared.FilePath, contentType, prepared.DownloadName, enableRangeProcessing: !prepared.IsTemporary);
        }
        catch (Exception ex)
        {
            await RecordFileMessageAsync(projectId, $"Download failed: {ex.Message}", true);
            return Back(projectId, path);
        }
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
        await RecordFileMessageAsync(job.ProjectId, $"Result change undone: {path}.");
        return Back(job.ProjectId);
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
        await RecordFileMessageAsync(job.ProjectId, "All result changes were undone.");
        return Back(job.ProjectId);
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAjaxRequest() => string.Equals(Request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    private async Task<IActionResult> BrowserPartialAsync(Guid projectId, string? path)
    {
        var project = await GetProjectAsync(projectId, manage: false);
        if (project is null) return Forbid();
        return PartialView("_FileBrowser", await workspace.BuildViewAsync(project, path));
    }
    private async Task<IActionResult> FileBrowserResultAsync(Guid projectId, string? path, string message, bool error = false)
    {
        await RecordFileMessageAsync(projectId, message, error);
        return IsAjaxRequest() ? await BrowserPartialAsync(projectId, path) : Back(projectId, path);
    }
    private Task RecordFileMessageAsync(Guid projectId, string text, bool error = false)
        => store.UpdateAsync(state => { state.Messages.Add(new AppMessage { ProjectId = projectId, Level = error ? MessageLevels.Error : MessageLevels.Info, Text = text }); return true; });
    private static string EditorLanguage(string fullPath) => Path.GetExtension(fullPath).ToLowerInvariant() switch
    {
        ".cs" => "csharp", ".js" => "javascript", ".ts" => "typescript", ".py" => "python", ".html" => "html",
        ".css" => "css", ".json" => "json", ".xml" or ".svg" => "xml", ".yaml" or ".yml" => "yaml", ".sql" => "sql", ".sh" => "shell", _ => "text"
    };
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
    private IActionResult Back(Guid projectId, string? path = null) => RedirectToAction(nameof(Index), new { projectId, path });
}
