using System.Text.Json;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class AgentToolExecutor(WorkspaceService workspace, StateStore store, GitProviderService gitHub, S3ObjectStorage objectStorage, AiGenerationService generation)
{
    private static readonly HashSet<string> InternalFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", ".history", ".trash", ".staging", ".undo" };

    public static object[] ToolDefinitions(AgentRuntimeSettings settings, IReadOnlySet<string>? allowedNames = null)
    {
        if (!settings.Enabled) return [];
        bool Allowed(string name) => allowedNames is null || allowedNames.Contains(name);

        var definitions = new List<object>();
        if (Allowed("workspace_guide"))
            definitions.Add(Tool("workspace_guide", "Show the permitted project workspace, file tools, top-level project entries, and connected Git destination. This never exposes the server filesystem.", new { type = "object", properties = new { } }));
        if (settings.AllowFileRead)
        {
            if (Allowed("browse_folder")) definitions.Add(Tool("browse_folder", "Browse one folder inside the project workspace through a scoped file API, not a shell command. Paths are relative to the logical project root.", new { type = "object", properties = new { path = new { type = "string", description = "Project-relative folder path; empty means the project root." } } }));
            if (Allowed("read_file")) definitions.Add(Tool("read_file", "Read any file belonging to this project. Text is returned directly; binary content is returned as Base64 in bounded chunks. Server and absolute paths are blocked.", new { type = "object", properties = new { path = new { type = "string" }, offset_bytes = new { type = "integer", minimum = 0, description = "Optional binary byte offset for reading the next chunk of a large file." } }, required = new[] { "path" } }));
        }
        if (settings.AllowFileWrite)
        {
            if (Allowed("write_file")) definitions.Add(Tool("write_file", "Create or replace one UTF-8 project file. Existing content is backed up inside the project workspace.", new { type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } }, required = new[] { "path", "content" } }));
            if (Allowed("replace_in_file")) definitions.Add(Tool("replace_in_file", "Replace exact text in one existing project file. The old text must occur exactly once.", new { type = "object", properties = new { path = new { type = "string" }, old_text = new { type = "string" }, new_text = new { type = "string" } }, required = new[] { "path", "old_text", "new_text" } }));
            if (Allowed("generate_asset")) definitions.Add(Tool("generate_asset", "Generate an image, audio, video, or other file with an enabled AI connection and save it directly inside this project. The server chooses providers by capability and falls back by priority.", new { type = "object", properties = new { capability = new { type = "string", @enum = new[] { AiCapabilities.Image, AiCapabilities.Audio, AiCapabilities.Video, AiCapabilities.Files } }, prompt = new { type = "string" }, path = new { type = "string", description = "Project-relative destination path." } }, required = new[] { "capability", "prompt", "path" } }));
        }
        if (settings.AllowGitHubPublish && Allowed("publish_project_files"))
            definitions.Add(Tool("publish_project_files", "Commit selected project files to the project's assigned Git provider. No terminal access is used.", new { type = "object", properties = new { paths = new { type = "array", items = new { type = "string" } }, message = new { type = "string" } }, required = new[] { "paths", "message" } }));
        if (Allowed("object_storage_status"))
            definitions.Add(Tool("object_storage_status", "Show whether cloud object storage is active. It exposes no credentials and remains scoped to this project.", new { type = "object", properties = new { } }));
        if (Allowed("sync_project_storage"))
            definitions.Add(Tool("sync_project_storage", "Synchronize project files to the configured object-storage provider. This never exposes credentials or another project's files.", new { type = "object", properties = new { } }));
        return [.. definitions];
    }

    public async Task<string> CreateWorkspaceGuideAsync(Guid projectId, AgentRuntimeSettings settings)
    {
        var root = await workspace.ResolveProjectPathAsync(projectId, "");
        var entries = Directory.EnumerateFileSystemEntries(root).Where(path => !InternalFolders.Contains(Path.GetFileName(path)) && !IsLink(path))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).Take(100)
            .Select(path => Directory.Exists(path) ? Path.GetFileName(path) + "/" : Path.GetFileName(path));
        var target = await ResolveGitHubTargetAsync(projectId);
        var available = new List<string> { "workspace guide" };
        if (settings.AllowFileRead) available.AddRange(["browse project folders", "read project text files"]);
        if (settings.AllowFileWrite) available.AddRange(["write project files", "replace text in project files"]);
        if (settings.AllowGitHubPublish) available.Add("queue AI-created files for background Git publishing; publish selected files immediately only when explicitly requested");
        if (objectStorage.IsEnabled) available.Add($"inspect and synchronize configured {objectStorage.ActiveProvider} project storage");
        var context = await store.ReadAsync(state => new
        {
            Project = state.Projects.First(item => item.Id == projectId),
            GenerationCapabilities = AiCapabilities.All.Where(capability => capability is AiCapabilities.Image or AiCapabilities.Audio or AiCapabilities.Video or AiCapabilities.Files)
                .Where(capability => state.Models.Any(model => model.IsEnabled && AiCapabilities.Supports(model, capability))).ToList()
        });
        if (settings.AllowFileWrite && context.GenerationCapabilities.Count > 0)
            available.Add($"generate project assets with provider fallback ({string.Join(", ", context.GenerationCapabilities)})");
        var project = context.Project;
        return $"""
               Project workspace: logical root `/` (project-relative paths only; this is not the server root).
               Top-level project entries: {string.Join(", ", entries).NullIfEmpty() ?? "(empty)"}
               Available operations: {string.Join(", ", available)}. The read tool returns text directly and binary files as Base64 chunks.
               Git destination: {(target is null ? "not assigned" : $"{target.Value.Connection.Provider} · {target.Value.Repository.FullName}")}.
               Repository project folder: {StateStore.ProjectRepositoryFolder(project)}. AI-created changes are batched and published there in the background; user-uploaded and user-edited files require explicit selection in the file manager.
               Object storage: {(objectStorage.IsEnabled ? "configured; project files synchronize automatically and can be synchronized on demand" : "not configured; local project storage is active")}.
               Unavailable operations: shell, terminal, Bash, commands, builds, tests, programs, package managers, server file listing, local Git, local commits, local pushes, secrets, and paths outside this project.
               """;
    }

    public async Task<AgentToolResult> ExecuteAsync(Guid projectId, AgentToolCall call, AgentRuntimeSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var arguments = document.RootElement;
            return call.Name.ToLowerInvariant() switch
            {
                "workspace_guide" => new AgentToolResult { Success = true, Summary = "Project workspace guide", Output = await CreateWorkspaceGuideAsync(projectId, settings) },
                "browse_folder" when settings.AllowFileRead => await BrowseFolderAsync(projectId, arguments),
                "read_file" when settings.AllowFileRead => await ReadFileAsync(projectId, arguments, settings),
                "write_file" when settings.AllowFileWrite => await WriteFileAsync(projectId, arguments),
                "replace_in_file" when settings.AllowFileWrite => await ReplaceInFileAsync(projectId, arguments),
                "generate_asset" when settings.AllowFileWrite => await generation.GenerateAsync(projectId, Required(arguments, "capability"), Required(arguments, "prompt"), Required(arguments, "path"), cancellationToken),
                "publish_project_files" when settings.AllowGitHubPublish => await PublishProjectFilesAsync(projectId, arguments, cancellationToken),
                "object_storage_status" => new AgentToolResult { Success = true, Summary = "Object storage status", Output = objectStorage.IsEnabled ? $"{objectStorage.ActiveProvider} project storage is active. This project's files are mirrored under its isolated project namespace." : "Cloud object storage is not configured; this project is using local storage." },
                "sync_project_storage" when objectStorage.IsEnabled => await SyncObjectStorageAsync(cancellationToken),
                "run_command" or "bash" => BlockedCommand(call.Name),
                _ => new AgentToolResult { Success = false, Summary = call.Name, Output = "Tool is disabled or unknown. Use workspace_guide to see the project-scoped tools." }
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new AgentToolResult { Success = false, Summary = call.Name, Output = ex.Message }; }
    }

    public async Task<bool> HasGitHubTargetAsync(Guid projectId) => await ResolveGitHubTargetAsync(projectId) is not null;

    private async Task<AgentToolResult> SyncObjectStorageAsync(CancellationToken cancellationToken)
    {
        await objectStorage.SyncAsync(cancellationToken);
        return new AgentToolResult { Success = true, Summary = "Synchronized object storage", Output = $"Project file synchronization with {objectStorage.ActiveProvider} completed." };
    }

    private static AgentToolResult BlockedCommand(string name) => new()
    {
        Success = false, Summary = $"Blocked {name}",
        Output = "Server command execution is permanently unavailable. Do not use Bash, terminal commands, builds, tests, local Git, or server file-list commands. Use workspace_guide and project-scoped file tools instead."
    };

    private async Task<AgentToolResult> BrowseFolderAsync(Guid projectId, JsonElement arguments)
    {
        var relative = Value(arguments, "path");
        EnsureSafePath(relative, allowEmpty: true);
        var folder = await ResolveSafePathAsync(projectId, relative);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Project folder '{relative}' was not found.");
        var entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().Where(entry => !InternalFolders.Contains(entry.Name) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(entry => entry is DirectoryInfo).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).Take(500)
            .Select(entry => entry is DirectoryInfo ? $"folder\t{entry.Name}/" : $"file\t{entry.Name}\t{((FileInfo)entry).Length} bytes");
        var output = string.Join('\n', entries);
        return new AgentToolResult { Success = true, Summary = $"Browsed project folder /{relative.Trim('/')}".TrimEnd('/'), Output = output.Length == 0 ? "(empty project folder)" : output };
    }

    private async Task<AgentToolResult> ReadFileAsync(Guid projectId, JsonElement arguments, AgentRuntimeSettings settings)
    {
        var relative = Required(arguments, "path"); EnsureSafePath(relative);
        var path = await ResolveSafePathAsync(projectId, relative);
        if (!File.Exists(path)) throw new FileNotFoundException("Project file not found.", relative);
        if (WorkspaceService.IsText(path))
            return new AgentToolResult { Success = true, Summary = $"Read {relative}", Output = Limit(await File.ReadAllTextAsync(path), settings.MaxToolOutputCharacters) };

        var info = new FileInfo(path);
        var offset = arguments.TryGetProperty("offset_bytes", out var offsetValue) && offsetValue.TryGetInt64(out var requestedOffset) ? requestedOffset : 0;
        if (offset < 0 || offset > info.Length) throw new InvalidOperationException("'offset_bytes' must be between 0 and the file size.");
        var maximumBytes = Math.Max(1, (Math.Max(1000, settings.MaxToolOutputCharacters) - 500) / 4 * 3);
        var byteCount = (int)Math.Min(info.Length - offset, maximumBytes);
        var bytes = new byte[byteCount];
        await using (var stream = File.OpenRead(path))
        {
            stream.Position = offset;
            if (byteCount > 0) await stream.ReadExactlyAsync(bytes);
        }
        var nextOffset = offset + byteCount;
        var continuation = nextOffset < info.Length ? $"Read the next chunk with offset_bytes={nextOffset}." : "This is the complete file.";
        var output = $"""
                     Binary project file: {relative}
                     File size: {info.Length} bytes
                     Bytes returned: {offset}-{nextOffset} ({byteCount} bytes)
                     Encoding: Base64
                     {continuation}

                     {Convert.ToBase64String(bytes)}
                     """;
        return new AgentToolResult { Success = true, Summary = $"Read binary file {relative}", Output = output };
    }

    private async Task<AgentToolResult> WriteFileAsync(Guid projectId, JsonElement arguments)
    {
        var relative = Required(arguments, "path"); var content = Required(arguments, "content", allowEmpty: true); EnsureSafePath(relative);
        var path = await ResolveSafePathAsync(projectId, relative);
        if (File.Exists(path) && await File.ReadAllTextAsync(path) == content)
            return new AgentToolResult { Success = true, Mutated = false, Summary = $"No change to {relative}", Output = "The requested content already matches the project file." };
        await BackupAsync(projectId, relative, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, content);
        await workspace.QueueAiGeneratedAsync(projectId, [relative]);
        return new AgentToolResult { Success = true, Mutated = true, Summary = $"Wrote {relative}", Output = $"Saved {content.Length} characters to the project file." };
    }

    private async Task<AgentToolResult> ReplaceInFileAsync(Guid projectId, JsonElement arguments)
    {
        var relative = Required(arguments, "path"); var oldText = Required(arguments, "old_text"); var newText = Required(arguments, "new_text", allowEmpty: true); EnsureSafePath(relative);
        var path = await ResolveSafePathAsync(projectId, relative);
        if (!File.Exists(path)) throw new FileNotFoundException("Project file not found.", relative);
        var content = await File.ReadAllTextAsync(path); var first = content.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0) throw new InvalidOperationException("The requested old text was not found.");
        if (content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0) throw new InvalidOperationException("The old text occurs more than once; provide a more specific match.");
        if (oldText == newText)
            return new AgentToolResult { Success = true, Mutated = false, Summary = $"No change to {relative}", Output = "The replacement text is identical to the existing text." };
        await BackupAsync(projectId, relative, path); await File.WriteAllTextAsync(path, content[..first] + newText + content[(first + oldText.Length)..]);
        await workspace.QueueAiGeneratedAsync(projectId, [relative]);
        return new AgentToolResult { Success = true, Mutated = true, Summary = $"Updated {relative}", Output = "Exact replacement applied to the project file." };
    }

    private async Task<AgentToolResult> PublishProjectFilesAsync(Guid projectId, JsonElement arguments, CancellationToken cancellationToken)
    {
        var message = Required(arguments, "message");
        if (!arguments.TryGetProperty("paths", out var values) || values.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("'paths' must be a non-empty list of project-relative file paths.");
        var paths = values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? "")
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(100).ToList();
        if (paths.Count == 0) throw new InvalidOperationException("Choose at least one project file to publish.");
        foreach (var path in paths) EnsureSafePath(path);
        var url = await PublishPathsAsync(projectId, paths, message, cancellationToken);
        return new AgentToolResult { Success = true, Summary = $"Published {paths.Count} project file(s)", Output = url };
    }

    public async Task<string> PublishPathsAsync(Guid projectId, IReadOnlyList<string> paths, string message, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) throw new InvalidOperationException("Choose at least one project file to publish.");
        var target = await ResolveGitHubTargetAsync(projectId) ?? throw new InvalidOperationException("Git publishing is unavailable: assign a connected repository to this project first.");
        var project = await store.ReadAsync(state => state.Projects.First(item => item.Id == projectId));
        var root = await workspace.ResolveProjectPathAsync(projectId, "");
        foreach (var path in paths) { EnsureSafePath(path); await ResolveSafePathAsync(projectId, path); }
        var folder = StateStore.ProjectRepositoryFolder(project);
        var url = await gitHub.PushProjectFilesAsync(target.Connection, target.Repository, root, paths, folder, message, cancellationToken);
        await store.UpdateAsync(state =>
        {
            var current = state.Projects.First(item => item.Id == projectId);
            current.RepositoryFolder = folder;
            current.AutoPublishPendingPaths.RemoveAll(path => paths.Contains(path, StringComparer.OrdinalIgnoreCase));
            if (current.AutoPublishPendingPaths.Count == 0) current.AutoPublishQueuedAt = null;
            current.LastAutoPublishAt = DateTimeOffset.UtcNow;
            current.NextAutoPublishAttemptAt = null;
            current.LastAutoPublishCommitUrl = url;
            current.LastAutoPublishError = "";
            foreach (var job in state.Jobs.Where(job => job.ProjectId == projectId && job.ProjectFilesChanged.Count > 0))
            {
                var published = job.PublishedProjectFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths.Where(path => job.ProjectFilesChanged.Contains(path, StringComparer.OrdinalIgnoreCase))) published.Add(path);
                job.PublishedProjectFiles = published.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
                if (job.ProjectFilesChanged.All(path => published.Contains(path) || current.UserManagedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                    job.ProjectCommitUrl = url;
            }
            return true;
        });
        return url;
    }

    private Task<(GitHubConnection Connection, GitHubRepository Repository)?> ResolveGitHubTargetAsync(Guid projectId) => store.ReadAsync<(GitHubConnection Connection, GitHubRepository Repository)?>(state =>
    {
        var project = state.Projects.FirstOrDefault(item => item.Id == projectId);
        var link = project?.ResultsRepositoryId is long repositoryId
            ? state.ProjectRepositories.FirstOrDefault(item => item.ProjectId == projectId && item.RepositoryId == repositoryId)
            : state.ProjectRepositories.FirstOrDefault(item => item.ProjectId == projectId);
        if (link is null) return ((GitHubConnection Connection, GitHubRepository Repository)?)null;
        var connection = state.GitHubConnections.FirstOrDefault(item => item.Id == link.GitHubConnectionId);
        var repository = connection?.Repositories.FirstOrDefault(item => item.Id == link.RepositoryId);
        return connection is null || repository is null ? null : (connection, repository);
    });

    private async Task BackupAsync(Guid projectId, string relative, string path)
    {
        if (!File.Exists(path)) return;
        var root = await workspace.ResolveProjectPathAsync(projectId, "");
        var backup = Path.Combine(root, ".history", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(path, backup);
    }

    private async Task<string> ResolveSafePathAsync(Guid projectId, string relative)
    {
        var root = await workspace.ResolveProjectPathAsync(projectId, "");
        var resolved = await workspace.ResolveProjectPathAsync(projectId, relative);
        var current = root;
        foreach (var segment in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if (IsLink(current)) throw new InvalidOperationException("Symbolic links are not accessible to AI tools because they may leave the project workspace.");
        }
        return resolved;
    }

    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    private static void EnsureSafePath(string relative, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(relative)) { if (allowEmpty) return; throw new InvalidOperationException("A project-relative path is required."); }
        if (Path.IsPathRooted(relative) || relative.StartsWith('~')) throw new InvalidOperationException("Absolute and server paths are blocked. Use a project-relative path.");
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == ".." || InternalFolders.Contains(part))) throw new InvalidOperationException("The path is outside the permitted project files.");
    }

    private static object Tool(string name, string description, object parameters) => new { type = "function", function = new { name, description, parameters } };
    private static string Required(JsonElement element, string name, bool allowEmpty = false) { var value = Value(element, name); if (!allowEmpty && string.IsNullOrEmpty(value)) throw new InvalidOperationException($"'{name}' is required."); return value; }
    private static string Value(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + $"\n… truncated after {maximum} characters";
}

file static class AgentToolStringExtensions
{
    public static string? NullIfEmpty(this string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
