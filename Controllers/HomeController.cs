using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authorization;

namespace DanLoopServer.Controllers;

[Authorize]
public sealed class HomeController(StateStore store) : Controller
{
    public async Task<IActionResult> Index()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = User.IsInRole(Roles.Admin);
        var state = await store.ReadAsync(x => x);
        var visible = isAdmin
            ? state.Projects
            : state.Projects.Where(p => state.Permissions.Any(x => x.UserId == userId && x.ProjectId == p.Id && x.CanView)).ToList();
        return View(new DashboardViewModel { State = state, VisibleProjects = visible, IsAdmin = isAdmin, CurrentUserId = userId });
    }

    [AllowAnonymous, HttpGet("/health")]
    public IActionResult Health() => Ok(new { status = "ok" });

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [AllowAnonymous]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
