namespace DanLoopServer.Models;

public sealed class DashboardViewModel
{
    public required AppState State { get; init; }
    public required IReadOnlyList<Project> VisibleProjects { get; init; }
    public bool IsAdmin { get; init; }
    public Guid CurrentUserId { get; init; }
}
