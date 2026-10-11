namespace DanLoopServer.Models;

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public List<AppUser> Users { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<LoopDefinition> Loops { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];
    public List<RemoteModel> Models { get; set; } = [];
    public List<ProjectPermission> Permissions { get; set; } = [];
}

public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Member;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Member = "Member";
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public Guid? DefaultModelId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LoopDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int IntervalSeconds { get; set; } = 300;
    public bool IsEnabled { get; set; }
    public DateTimeOffset? LastQueuedAt { get; set; }
}

public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? LoopId { get; set; }
    public Guid? ModelId { get; set; }
    public string Prompt { get; set; } = "";
    public string Status { get; set; } = JobStatuses.Queued;
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class JobStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed class RemoteModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
}

public sealed class ProjectPermission
{
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
    public bool CanView { get; set; } = true;
    public bool CanManage { get; set; }
    public bool CanRun { get; set; }
}
