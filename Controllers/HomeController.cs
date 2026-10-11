using System.Diagnostics;
using System.Security.Claims;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DanLoopServer.Controllers;

[Authorize]
public sealed class HomeController(StateStore store, WorkspaceService workspace) : Controller
{
    public async Task<IActionResult> Index()
        => View(await BuildDashboardAsync(includeProjectFiles: true));

    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> JobsPartial()
        => PartialView("_JobsList", await BuildDashboardAsync(includeProjectFiles: false));

    private async Task<DashboardViewModel> BuildDashboardAsync(bool includeProjectFiles)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = User.IsInRole(Roles.Admin);
        var state = await store.ReadAsync(x => x);
        var visible = isAdmin
            ? state.Projects
            : state.Projects.Where(p => state.Permissions.Any(x => x.UserId == userId && x.ProjectId == p.Id && x.CanView)).ToList();
        var files = new Dictionary<Guid, IReadOnlyList<string>>();
        if (includeProjectFiles)
            foreach (var project in visible) files[project.Id] = await workspace.ListFilesAsync(project.Id);
        return new DashboardViewModel { State = state, VisibleProjects = visible, IsAdmin = isAdmin, CurrentUserId = userId, ProjectFiles = files };
    }

    [AllowAnonymous, HttpGet("/health")]
    public IActionResult Health() => Ok(new { status = "ok" });

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [AllowAnonymous]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
