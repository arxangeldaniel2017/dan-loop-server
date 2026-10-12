namespace DanLoopServer.Models;

public sealed class AppState
{
    public int Version { get; set; } = 11;
    public List<AppUser> Users { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<LoopDefinition> Loops { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];
    public List<RemoteModel> Models { get; set; } = [];
    public List<AiProviderCatalogItem> AiProviderCatalog { get; set; } = [];
    public List<ProjectPermission> Permissions { get; set; } = [];
    public List<GitHubConnection> GitHubConnections { get; set; } = [];
    public List<ProjectRepository> ProjectRepositories { get; set; } = [];
    public List<Conversation> Conversations { get; set; } = [];
    public List<PendingUpload> PendingUploads { get; set; } = [];
    public List<AppMessage> Messages { get; set; } = [];
    public List<ProjectExecutionPlan> ExecutionPlans { get; set; } = [];
    public AgentRuntimeState AgentRuntime { get; set; } = new();
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
    public string WorkspaceFolder { get; set; } = "";
    public string RepositoryFolder { get; set; } = "";
    public List<string> UserManagedPaths { get; set; } = [];
    public List<string> AutoPublishPendingPaths { get; set; } = [];
    public DateTimeOffset? AutoPublishQueuedAt { get; set; }
    public DateTimeOffset? LastAutoPublishAt { get; set; }
    public DateTimeOffset? NextAutoPublishAttemptAt { get; set; }
    public string LastAutoPublishCommitUrl { get; set; } = "";
    public string LastAutoPublishError { get; set; } = "";
    public long? ResultsRepositoryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LoopDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int IntervalSeconds { get; set; } = 300;
    public int? MaxExecutions { get; set; }
    public int ExecutionCount { get; set; }
    public bool IsEnabled { get; set; }
    public DateTimeOffset? LastQueuedAt { get; set; }
}

public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? LoopId { get; set; }
    public Guid? ModelId { get; set; }
    public string RequiredCapability { get; set; } = "";
    public List<Guid> FailedModelIds { get; set; } = [];
    public Guid? ConversationId { get; set; }
    public string Prompt { get; set; } = "";
    public List<string> AttachmentPaths { get; set; } = [];
    public string ResultFolder { get; set; } = "";
    public List<JobResultItem> ResultItems { get; set; } = [];
    public string ResultsCommitUrl { get; set; } = "";
    public string ResultsPushError { get; set; } = "";
    public string WorkflowOutcome { get; set; } = "";
    public List<string> ProjectFilesChanged { get; set; } = [];
    public List<string> PublishedProjectFiles { get; set; } = [];
    public string ProjectCommitUrl { get; set; } = "";
    public Guid? PlanStepId { get; set; }
    public string PlanStepTitle { get; set; } = "";
    public List<AgentResponseLog> AiResponses { get; set; } = [];
    public string Status { get; set; } = JobStatuses.Queued;
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AgentResponseLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Step { get; set; }
    public string Kind { get; set; } = "Response";
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectExecutionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string LastMarkdown { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ProjectPlanStep> Steps { get; set; } = [];
}

public sealed class ProjectPlanStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = PlanStepStatuses.Pending;
    public int? RetryLimit { get; set; }
    public List<PlanStepAttempt> Attempts { get; set; } = [];
}

public sealed class PlanStepAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int Number { get; set; }
    public string Status { get; set; } = PlanStepStatuses.Running;
    public string Summary { get; set; } = "";
    public string Error { get; set; } = "";
    public List<string> ChangedPaths { get; set; } = [];
    public string CommitUrl { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class PlanStepStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Retrying = "Retrying";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
    public const string Paused = "Paused";
    public const string Cancelled = "Cancelled";
}

public static class JobStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Planned = "Planned";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed class AppMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ProjectId { get; set; }
    public Guid? JobId { get; set; }
    public string Level { get; set; } = MessageLevels.Info;
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class MessageLevels
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Error = "Error";
}

public sealed class RemoteModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ProviderId { get; set; } = "custom";
    public string BaseUrl { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public List<string> Capabilities { get; set; } = [];
    public List<string> SuggestedCapabilities { get; set; } = [];
    public string LastTestStatus { get; set; } = "Not tested";
    public string LastTestMessage { get; set; } = "";
    public DateTimeOffset? LastTestedAt { get; set; }
}

public sealed class AiProviderCatalogItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool RequiresBaseUrl { get; set; }
    public string ApiKeyUrl { get; set; } = "";
    public string DocumentationUrl { get; set; } = "";
    public string PricingUrl { get; set; } = "";
    public List<AiModelCatalogItem> Models { get; set; } = [];
}

public sealed class AiModelCatalogItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Capabilities { get; set; } = [];
}

public static class AiCapabilities
{
    public const string Planner = "Planner";
    public const string Coder = "Coder";
    public const string Orchestrator = "Orchestrator";
    public const string ToolCalling = "Tool calling";
    public const string Generator = "Generator";
    public const string Image = "Image";
    public const string Audio = "Audio";
    public const string Video = "Video";
    public const string Files = "Files";
    public static readonly string[] All = [Planner, Coder, Orchestrator, ToolCalling, Generator, Image, Audio, Video, Files];

    public static IReadOnlyList<string> Effective(RemoteModel model) =>
        (model.Capabilities.Count > 0 ? model.Capabilities : model.SuggestedCapabilities)
        .Where(All.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool Supports(RemoteModel model, string capability) =>
        string.IsNullOrWhiteSpace(capability) || Effective(model).Contains(capability, StringComparer.OrdinalIgnoreCase);
}

public sealed class ProjectPermission
{
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
    public bool CanView { get; set; } = true;
    public bool CanManage { get; set; }
    public bool CanRun { get; set; }
}

public sealed class GitHubConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AuthenticationType { get; set; } = GitHubAuthenticationTypes.App;
    public string ProtectedToken { get; set; } = "";
    public long InstallationId { get; set; }
    public string AccountLogin { get; set; } = "";
    public string AccountType { get; set; } = "";
    public string SettingsUrl { get; set; } = "";
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RefreshedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<GitHubRepository> Repositories { get; set; } = [];
}

public static class GitHubAuthenticationTypes
{
    public const string App = "App";
    public const string Token = "Token";
}

public sealed class GitHubRepository
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public string CloneUrl { get; set; } = "";
    public string DefaultBranch { get; set; } = "";
    public bool IsPrivate { get; set; }
}

public sealed class ProjectRepository
{
    public Guid ProjectId { get; set; }
    public Guid GitHubConnectionId { get; set; }
    public long RepositoryId { get; set; }
}

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ConversationMessage> Messages { get; set; } = [];
}

public sealed class ConversationMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? JobId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public List<string> FilePaths { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JobResultItem
{
    public string RelativePath { get; set; } = "";
    public string ChangeType { get; set; } = "created";
    public long Size { get; set; }
    public bool IsUndone { get; set; }
}

public sealed class PendingUpload
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PendingUploadItem> Items { get; set; } = [];
}

public sealed class PendingUploadItem
{
    public string StagedName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public bool IsZip { get; set; }
}
