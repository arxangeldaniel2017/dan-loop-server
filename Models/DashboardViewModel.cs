namespace DanLoopServer.Models;

public sealed class DashboardViewModel
{
    public required AppState State { get; init; }
    public required IReadOnlyList<Project> VisibleProjects { get; init; }
    public bool IsAdmin { get; init; }
    public Guid CurrentUserId { get; init; }
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> ProjectFiles { get; init; } = new Dictionary<Guid, IReadOnlyList<string>>();
}
