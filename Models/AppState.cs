namespace DanLoopServer.Models;

public sealed class AppState
{
    public int Version { get; set; } = 16;
    public List<AppUser> Users { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<LoopDefinition> Loops { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];
    public List<RemoteModel> Models { get; set; } = [];
    public List<AiProviderCatalogItem> AiProviderCatalog { get; set; } = [];
    public List<ProjectPermission> Permissions { get; set; } = [];
    public List<GitHubConnection> GitHubConnections { get; set; } = [];
    public List<ObjectStorageConnection> ObjectStorageConnections { get; set; } = [];
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
    public int AccessFailedCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
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
    public string AuthenticationType { get; set; } = AiAuthenticationTypes.BearerToken;
    public string ApiKeyHeaderName { get; set; } = "Authorization";
    public string ApiKeyPrefix { get; set; } = "";
    public string ApiProtocol { get; set; } = AiApiProtocols.OpenAiChat;
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
    public string AuthenticationType { get; set; } = AiAuthenticationTypes.BearerToken;
    public string ApiKeyHeaderName { get; set; } = "Authorization";
    public string ApiKeyPrefix { get; set; } = "";
    public string ApiProtocol { get; set; } = AiApiProtocols.OpenAiChat;
    public bool ApiKeyRequired { get; set; } = true;
    public bool AllowsAuthenticationSelection { get; set; }
    public string ApiKeyUrl { get; set; } = "";
    public string DocumentationUrl { get; set; } = "";
    public string PricingUrl { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string Category { get; set; } = "AI provider";
    public string ModelDiscovery { get; set; } = AiModelDiscoveryModes.None;
    public bool ModelDiscoveryRequiresCredential { get; set; }
    public List<AiModelCatalogItem> Models { get; set; } = [];
}

public static class AiModelDiscoveryModes
{
    public const string None = "none";
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";
    public const string Anthropic = "anthropic";
    public const string Replicate = "replicate";
    public const string ElevenLabs = "elevenlabs";
    public const string Stability = "stability";
    public const string Cloudflare = "cloudflare";
    public const string HuggingFace = "huggingface";
    public const string Databricks = "databricks";
    public const string Cohere = "cohere";
    public static readonly string[] All = [None, OpenAi, Gemini, Anthropic, Replicate, ElevenLabs, Stability, Cloudflare, HuggingFace, Databricks, Cohere];
}

public static class AiApiProtocols
{
    public const string OpenAiChat = "OpenAI chat";
    public const string AnthropicMessages = "Anthropic messages";
    public const string NativeGeneration = "Native generation";
    public static readonly string[] All = [OpenAiChat, AnthropicMessages, NativeGeneration];
}

public static class AiAuthenticationTypes
{
    public const string BearerToken = "Bearer token";
    public const string ApiKeyHeader = "API key header";
    public const string None = "No authentication";
    public static readonly string[] All = [BearerToken, ApiKeyHeader, None];

    public static string Normalize(string? value) => All.Contains(value, StringComparer.OrdinalIgnoreCase)
        ? All.First(item => item.Equals(value, StringComparison.OrdinalIgnoreCase))
        : BearerToken;
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
    public string Provider { get; set; } = GitProviders.GitHub;
    public string BaseUrl { get; set; } = "https://github.com";
    public string Namespace { get; set; } = "";
    public string Region { get; set; } = "";
    public string Username { get; set; } = "";
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

public static class GitProviders
{
    public const string GitHub = "GitHub";
    public const string GitLab = "GitLab";
    public const string Bitbucket = "Bitbucket";
    public const string Codeberg = "Codeberg";
    public const string Gitea = "Gitea Cloud";
    public const string SourceHut = "SourceHut";
    public const string AwsCodeCommit = "AWS CodeCommit";
    public const string AzureDevOps = "Azure DevOps Repos";
    public const string GoogleSecureSourceManager = "Google Cloud Secure Source Manager";
    public const string Gitee = "Gitee";
    public static readonly string[] All = [GitHub, GitLab, Bitbucket, Codeberg, Gitea, SourceHut, AwsCodeCommit, AzureDevOps, GoogleSecureSourceManager, Gitee];

    public static string DefaultBaseUrl(string provider) => provider switch
    {
        GitLab => "https://gitlab.com",
        Bitbucket => "https://bitbucket.org",
        Codeberg => "https://codeberg.org",
        Gitea => "https://gitea.com",
        SourceHut => "https://git.sr.ht",
        AwsCodeCommit => "https://codecommit.amazonaws.com",
        AzureDevOps => "https://dev.azure.com",
        GoogleSecureSourceManager => "https://securesourcemanager.googleapis.com",
        Gitee => "https://gitee.com",
        _ => "https://github.com"
    };
}

public static class GitHubAuthenticationTypes
{
    public const string App = "App";
    public const string Token = "Token";
}

public sealed class ObjectStorageConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Object storage";
    public string Provider { get; set; } = ObjectStorageProviders.AmazonS3;
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "dan-loop/projects";
    public string ProtectedAccessKey { get; set; } = "";
    public string ProtectedSecretKey { get; set; } = "";
    public string ProtectedSessionToken { get; set; } = "";
    public bool ForcePathStyle { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string LastTestStatus { get; set; } = "Not tested";
    public string LastTestMessage { get; set; } = "";
    public DateTimeOffset? LastTestedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class ObjectStorageProviders
{
    public const string AmazonS3 = "Amazon S3";
    public const string DigitalOceanSpaces = "DigitalOcean Spaces";
    public const string CloudflareR2 = "Cloudflare R2";
    public const string BackblazeB2 = "Backblaze B2";
    public const string Wasabi = "Wasabi";
    public const string GoogleCloudStorage = "Google Cloud Storage";
    public const string AzureBlobStorage = "Azure Blob Storage";
    public const string OracleObjectStorage = "Oracle Object Storage";
    public const string IbmCloudObjectStorage = "IBM Cloud Object Storage";
    public const string Storj = "Storj";
    public const string IDriveE2 = "IDrive e2";
    public const string VultrObjectStorage = "Vultr Object Storage";
    public const string LinodeObjectStorage = "Linode Object Storage";
    public const string ScalewayObjectStorage = "Scaleway Object Storage";
    public const string OvhCloudObjectStorage = "OVHcloud Object Storage";
    public const string HetznerObjectStorage = "Hetzner Object Storage";
    public const string TigrisData = "Tigris Data";
    public const string BunnyStorage = "Bunny Storage";
    public const string S3Compatible = "S3 compatible";
    public static readonly string[] All =
    [
        AmazonS3, DigitalOceanSpaces, CloudflareR2, BackblazeB2, Wasabi,
        GoogleCloudStorage, AzureBlobStorage, OracleObjectStorage, IbmCloudObjectStorage,
        Storj, IDriveE2, VultrObjectStorage, LinodeObjectStorage, ScalewayObjectStorage,
        OvhCloudObjectStorage, HetznerObjectStorage, TigrisData, BunnyStorage, S3Compatible
    ];

    public static bool IsAzure(string provider) => provider == AzureBlobStorage;
    public static bool IsBunny(string provider) => provider == BunnyStorage;
    public static bool UsesS3Api(string provider) => !IsAzure(provider) && !IsBunny(provider);
    public static bool SupportsSessionToken(string provider) => provider is AmazonS3 or CloudflareR2 or S3Compatible;
    public static bool EndpointIsRequired(string provider) => provider is CloudflareR2 or OracleObjectStorage or IDriveE2 or S3Compatible;
    public static bool DefaultForcePathStyle(string provider) => provider is GoogleCloudStorage or S3Compatible;

    public static string DefaultRegion(string provider) => provider switch
    {
        CloudflareR2 or GoogleCloudStorage or TigrisData => "auto",
        DigitalOceanSpaces => "nyc3",
        BackblazeB2 => "us-west-004",
        OracleObjectStorage => "us-ashburn-1",
        IbmCloudObjectStorage => "us-south",
        VultrObjectStorage => "ewr1",
        ScalewayObjectStorage => "fr-par",
        OvhCloudObjectStorage => "gra",
        HetznerObjectStorage => "fsn1",
        BunnyStorage => "de",
        AzureBlobStorage => "",
        _ => "us-east-1"
    };

    public static string ResolveEndpoint(string provider, string? endpoint, string region, string? accountName = null)
    {
        if (!string.IsNullOrWhiteSpace(endpoint)) return endpoint.Trim().TrimEnd('/');
        return provider switch
        {
            AmazonS3 => "",
            DigitalOceanSpaces => $"https://{region}.digitaloceanspaces.com",
            BackblazeB2 => $"https://s3.{region}.backblazeb2.com",
            Wasabi => $"https://s3.{region}.wasabisys.com",
            GoogleCloudStorage => "https://storage.googleapis.com",
            AzureBlobStorage when !string.IsNullOrWhiteSpace(accountName) => $"https://{accountName.Trim()}.blob.core.windows.net",
            IbmCloudObjectStorage => $"https://s3.{region}.cloud-object-storage.appdomain.cloud",
            Storj => "https://gateway.storjshare.io",
            VultrObjectStorage => $"https://{region}.vultrobjects.com",
            LinodeObjectStorage => $"https://{region}.linodeobjects.com",
            ScalewayObjectStorage => $"https://s3.{region}.scw.cloud",
            OvhCloudObjectStorage => $"https://s3.{region}.io.cloud.ovh.net",
            HetznerObjectStorage => $"https://{region}.your-objectstorage.com",
            TigrisData => "https://fly.storage.tigris.dev",
            BunnyStorage => region.Equals("de", StringComparison.OrdinalIgnoreCase)
                ? "https://storage.bunnycdn.com"
                : $"https://{region.ToLowerInvariant()}.storage.bunnycdn.com",
            _ => ""
        };
    }

    public static string DocumentationUrl(string provider) => provider switch
    {
        CloudflareR2 => "https://developers.cloudflare.com/r2/api/s3/",
        BackblazeB2 => "https://www.backblaze.com/docs/cloud-storage-s3-compatible-api",
        Wasabi => "https://docs.wasabi.com/docs/how-do-i-use-aws-sdk-for-net-with-wasabi",
        GoogleCloudStorage => "https://cloud.google.com/storage/docs/authentication/hmackeys",
        AzureBlobStorage => "https://learn.microsoft.com/azure/storage/blobs/storage-quickstart-blobs-dotnet",
        OracleObjectStorage => "https://docs.oracle.com/iaas/Content/Object/Tasks/s3compatibleapi.htm",
        IbmCloudObjectStorage => "https://cloud.ibm.com/docs/cloud-object-storage?topic=cloud-object-storage-compatibility-api",
        Storj => "https://storj.dev/dcs/api/s3/s3-compatible-gateway",
        IDriveE2 => "https://www.idrive.com/s3-storage-e2/developer-guide",
        VultrObjectStorage => "https://docs.vultr.com/products/cloud-storage/object-storage/",
        LinodeObjectStorage => "https://techdocs.akamai.com/cloud-computing/docs/object-storage",
        ScalewayObjectStorage => "https://www.scaleway.com/en/docs/object-storage/api-cli/object-storage-aws-cli/",
        OvhCloudObjectStorage => "https://help.ovhcloud.com/csm/en-public-cloud-storage-s3-api?id=kb_article_view&sysparm_article=KB0047364",
        HetznerObjectStorage => "https://docs.hetzner.com/storage/object-storage/",
        TigrisData => "https://www.tigrisdata.com/docs/sdks/s3/",
        BunnyStorage => "https://docs.bunny.net/reference/storage-api",
        DigitalOceanSpaces => "https://docs.digitalocean.com/products/spaces/how-to/manage-access/",
        AmazonS3 => "https://docs.aws.amazon.com/AmazonS3/latest/userguide/GetStartedWithS3.html",
        _ => "https://docs.aws.amazon.com/AmazonS3/latest/API/Welcome.html"
    };
}

public sealed class GitHubRepository
{
    public long Id { get; set; }
    public string ExternalId { get; set; } = "";
    public string ApiUrl { get; set; } = "";
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
