namespace DanLoopServer.Models;

public sealed class AgentRuntimeState
{
    public AgentRuntimeSettings Settings { get; set; } = new();
    public List<AgentToolAuditEntry> AuditTrail { get; set; } = [];
}

public sealed class AgentRuntimeSettings
{
    public bool Enabled { get; set; } = true;
    public bool UseStreaming { get; set; } = true;
    public bool AllowFileRead { get; set; } = true;
    public bool AllowFileWrite { get; set; } = true;
    public bool AllowCommands { get; set; } = true;
    public bool AllowGitCommit { get; set; } = true;
    public bool AllowGitPush { get; set; } = true;
    public int MaxSteps { get; set; } = 12;
    public int MaxOutputTokens { get; set; } = 8192;
    public int CommandTimeoutSeconds { get; set; } = 120;
    public int MaxToolOutputCharacters { get; set; } = 20_000;
    public List<string> AllowedCommands { get; set; } = AgentCommandCatalog.DefaultCommands.ToList();
}

public sealed record AgentCommandOption(string Name, string Category, string Description, bool IsDefault = false, bool IsPowerTool = false);

public static class AgentCommandCatalog
{
    public static readonly IReadOnlyList<AgentCommandOption> Options =
    [
        new("pwd", "Files & text", "Show the workspace location", true), new("ls", "Files & text", "List folders and files", true),
        new("find", "Files & text", "Find workspace entries", true), new("rg", "Files & text", "Fast text search", true),
        new("grep", "Files & text", "Search text", true), new("head", "Files & text", "Read the beginning of output", true),
        new("tail", "Files & text", "Read the end of output", true), new("wc", "Files & text", "Count lines, words, and bytes", true),
        new("echo", "Files & text", "Print text", true), new("cat", "Files & text", "Read and combine text", true),
        new("sort", "Files & text", "Sort text", true), new("uniq", "Files & text", "Remove adjacent duplicates", true),
        new("cut", "Files & text", "Select text columns", true), new("tr", "Files & text", "Translate characters", true),
        new("sed", "Files & text", "Transform text", true), new("awk", "Files & text", "Process structured text", true),
        new("diff", "Files & text", "Compare files", true), new("file", "Files & text", "Identify file types", true),
        new("jq", "Files & text", "Query JSON", true),

        new("git", "Version control", "Initialize, inspect, commit, and push Git repositories", true),
        new("gh", "Version control", "GitHub CLI", false, true),

        new("dotnet", ".NET", "Restore, build, test, and format .NET projects", true),
        new("node", "JavaScript", "Run project JavaScript", false, true), new("npm", "JavaScript", "Install and run npm projects", false, true),
        new("npx", "JavaScript", "Run npm package tools", false, true), new("pnpm", "JavaScript", "Install and run pnpm projects", false, true),
        new("yarn", "JavaScript", "Install and run Yarn projects", false, true),

        new("python3", "Python", "Run project Python", false, true), new("pip3", "Python", "Install Python packages", false, true),
        new("pytest", "Python", "Run Python tests", false, true), new("ruff", "Python", "Lint and format Python", false, true),

        new("make", "Native build", "Run Make targets", true), new("cmake", "Native build", "Configure C/C++ projects", true),
        new("ninja", "Native build", "Run Ninja builds", true), new("gcc", "Native build", "Compile C projects", false, true),
        new("clang", "Native build", "Compile C/C++ projects", false, true),

        new("go", "Other languages", "Build and test Go projects", false, true), new("cargo", "Other languages", "Build and test Rust projects", false, true),
        new("rustc", "Other languages", "Compile Rust", false, true), new("java", "Other languages", "Run Java projects", false, true),
        new("javac", "Other languages", "Compile Java", false, true), new("mvn", "Other languages", "Build Maven projects", false, true),
        new("gradle", "Other languages", "Build Gradle projects", false, true), new("ruby", "Other languages", "Run project Ruby", false, true),
        new("bundle", "Other languages", "Manage Ruby dependencies", false, true), new("php", "Other languages", "Run project PHP", false, true),
        new("composer", "Other languages", "Manage PHP dependencies", false, true), new("swift", "Other languages", "Build Swift projects", false, true),
        new("xcodebuild", "Other languages", "Build Apple projects", false, true),

        new("docker", "Infrastructure", "Build and run containers", false, true), new("podman", "Infrastructure", "Build and run containers", false, true),
        new("kubectl", "Infrastructure", "Manage Kubernetes resources", false, true), new("helm", "Infrastructure", "Manage Helm deployments", false, true),
        new("terraform", "Infrastructure", "Manage infrastructure", false, true),
        new("curl", "Network", "Call HTTP endpoints", false, true), new("wget", "Network", "Download remote resources", false, true)
    ];

    public static readonly IReadOnlyList<string> DefaultCommands = Options.Where(option => option.IsDefault).Select(option => option.Name).ToList();
}

public sealed class AgentToolAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid JobId { get; set; }
    public int Step { get; set; }
    public string Tool { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "Running";
    public string Output { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AgentRuntimeResult
{
    public string Output { get; init; } = "";
    public int Steps { get; init; }
    public int ToolCalls { get; init; }
}

public sealed class AgentToolCall
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string ArgumentsJson { get; init; } = "{}";
}

public sealed class AgentModelStep
{
    public string Content { get; init; } = "";
    public string Reasoning { get; init; } = "";
    public IReadOnlyList<AgentToolCall> ToolCalls { get; init; } = [];
}

public sealed class AgentToolResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string Summary { get; init; } = "";
}
