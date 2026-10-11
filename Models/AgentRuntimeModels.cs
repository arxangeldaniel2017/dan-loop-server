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
    public bool AllowGitHubPublish { get; set; } = true;
    public int MaxSteps { get; set; } = 20;
    public int MaxOutputTokens { get; set; } = 16_384;
    public int MaxToolOutputCharacters { get; set; } = 50_000;
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
    public string Outcome { get; init; } = AgentRunOutcomes.ChangesCompleted;
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];
    public string PublishUrl { get; init; } = "";
}

public static class AgentRunOutcomes
{
    public const string PlanCreated = "PlanCreated";
    public const string ChangesCompleted = "ChangesCompleted";
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
    public bool Mutated { get; init; }
    public string Output { get; init; } = "";
    public string Summary { get; init; } = "";
}
