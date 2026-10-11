using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class AgentToolExecutor(WorkspaceService workspace)
{
    private static readonly HashSet<string> AlwaysBlockedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "sudo", "su", "dd", "mkfs", "shutdown", "reboot", "kill", "killall"
    };
    private static readonly HashSet<string> InternalFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", ".history", ".trash", ".staging", ".undo" };
    private static readonly HashSet<string> ReadOnlyGitCommands = new(StringComparer.OrdinalIgnoreCase) { "status", "diff", "log", "show", "branch", "rev-parse", "ls-files", "grep", "remote", "tag" };

    public static object[] ToolDefinitions() =>
    [
        Tool("list_files", "List files and folders inside the project workspace.", new { type = "object", properties = new { path = new { type = "string" }, recursive = new { type = "boolean" } } }),
        Tool("read_file", "Read a UTF-8 text file from the project workspace.", new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } }),
        Tool("write_file", "Create or replace a UTF-8 text file. Existing content is backed up.", new { type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } }, required = new[] { "path", "content" } }),
        Tool("replace_in_file", "Replace exact text in an existing UTF-8 file. The old text must occur exactly once.", new { type = "object", properties = new { path = new { type = "string" }, old_text = new { type = "string" }, new_text = new { type = "string" } }, required = new[] { "path", "old_text", "new_text" } }),
        Tool("run_command", "Run an approved development command in the project workspace. Prefer structured program and arguments.", new { type = "object", properties = new { program = new { type = "string" }, arguments = new { type = "array", items = new { type = "string" } }, command = new { type = "string" } } })
    ];

    public async Task<AgentToolResult> ExecuteAsync(Guid projectId, AgentToolCall call, AgentRuntimeSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var arguments = document.RootElement;
            return call.Name.ToLowerInvariant() switch
            {
                "list_files" when settings.AllowFileRead => await ListFilesAsync(projectId, arguments),
                "read_file" when settings.AllowFileRead => await ReadFileAsync(projectId, arguments, settings),
                "write_file" when settings.AllowFileWrite => await WriteFileAsync(projectId, arguments),
                "replace_in_file" when settings.AllowFileWrite => await ReplaceInFileAsync(projectId, arguments),
                "run_command" or "bash" when settings.AllowCommands => await RunCommandAsync(projectId, arguments, settings, cancellationToken),
                _ => new AgentToolResult { Success = false, Summary = call.Name, Output = "Tool is disabled or unknown." }
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new AgentToolResult { Success = false, Summary = call.Name, Output = ex.Message };
        }
    }

    private async Task<AgentToolResult> ListFilesAsync(Guid projectId, JsonElement arguments)
    {
        var projectRoot = await workspace.ResolveProjectPathAsync(projectId, "");
        var relative = NormalizeToolPath(projectRoot, Value(arguments, "path"));
        var root = await workspace.ResolveProjectPathAsync(projectId, relative);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(relative);
        var recursive = Bool(arguments, "recursive");
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var items = Directory.EnumerateFileSystemEntries(root, "*", option)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(InternalFolders.Contains))
            .Take(500)
            .Select(path => Directory.Exists(path) ? Path.GetRelativePath(root, path).Replace('\\', '/') + "/" : Path.GetRelativePath(root, path).Replace('\\', '/'));
        var output = string.Join('\n', items);
        return new AgentToolResult { Success = true, Summary = $"Listed {relative.NullIfEmpty() ?? "workspace"}", Output = output.Length == 0 ? "(empty)" : output };
    }

    private async Task<AgentToolResult> ReadFileAsync(Guid projectId, JsonElement arguments, AgentRuntimeSettings settings)
    {
        var relative = Required(arguments, "path");
        EnsureSafePath(relative);
        var path = await workspace.ResolveProjectPathAsync(projectId, relative);
        if (!File.Exists(path)) throw new FileNotFoundException("File not found.", relative);
        if (!WorkspaceService.IsText(path)) throw new InvalidOperationException("Only recognized text files can be read.");
        var content = await File.ReadAllTextAsync(path);
        return new AgentToolResult { Success = true, Summary = $"Read {relative}", Output = Limit(content, settings.MaxToolOutputCharacters) };
    }

    private async Task<AgentToolResult> WriteFileAsync(Guid projectId, JsonElement arguments)
    {
        var relative = Required(arguments, "path");
        var content = Required(arguments, "content", allowEmpty: true);
        EnsureSafePath(relative);
        var path = await workspace.ResolveProjectPathAsync(projectId, relative);
        await BackupAsync(projectId, relative, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return new AgentToolResult { Success = true, Summary = $"Wrote {relative}", Output = $"Saved {content.Length} characters." };
    }

    private async Task<AgentToolResult> ReplaceInFileAsync(Guid projectId, JsonElement arguments)
    {
        var relative = Required(arguments, "path");
        var oldText = Required(arguments, "old_text");
        var newText = Required(arguments, "new_text", allowEmpty: true);
        EnsureSafePath(relative);
        var path = await workspace.ResolveProjectPathAsync(projectId, relative);
        if (!File.Exists(path)) throw new FileNotFoundException("File not found.", relative);
        var content = await File.ReadAllTextAsync(path);
        var first = content.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0) throw new InvalidOperationException("The requested old text was not found.");
        if (content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0) throw new InvalidOperationException("The old text occurs more than once; provide a more specific match.");
        await BackupAsync(projectId, relative, path);
        await File.WriteAllTextAsync(path, content[..first] + newText + content[(first + oldText.Length)..]);
        return new AgentToolResult { Success = true, Summary = $"Updated {relative}", Output = "Exact replacement applied." };
    }

    private async Task<AgentToolResult> RunCommandAsync(Guid projectId, JsonElement arguments, AgentRuntimeSettings settings, CancellationToken cancellationToken)
    {
        var projectRoot = await workspace.ResolveProjectPathAsync(projectId, "");
        var program = Value(arguments, "program");
        var command = Value(arguments, "command");
        var argumentList = arguments.TryGetProperty("arguments", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(item => item.GetString() ?? "").ToList() : [];
        ProcessStartInfo start;
        string summary;
        if (!string.IsNullOrWhiteSpace(program))
        {
            ValidateProgram(program, argumentList, settings);
            ValidateArguments(argumentList);
            if (Path.GetFileName(program).Equals("git", StringComparison.OrdinalIgnoreCase) && !IsGitInit(argumentList)) EnsureOwnedGitWorkspace(projectRoot);
            start = new ProcessStartInfo(program) { WorkingDirectory = projectRoot };
            foreach (var argument in argumentList) start.ArgumentList.Add(argument);
            summary = $"{program} {string.Join(' ', argumentList)}".Trim();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Provide program/arguments or command.");
            ValidateShellCommand(command, settings);
            if (ShellRequiresOwnedGit(command)) EnsureOwnedGitWorkspace(projectRoot);
            start = new ProcessStartInfo("/bin/zsh") { WorkingDirectory = projectRoot };
            start.ArgumentList.Add("-lc");
            start.ArgumentList.Add(command);
            summary = command;
        }
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.Environment.Remove("GITHUB_TOKEN");
        start.Environment.Remove("GH_TOKEN");
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("RUNPOD_API_KEY");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start command.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.CommandTimeoutSeconds, 5, 600)));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = (await stdout) + (await stderr);
            return new AgentToolResult { Success = process.ExitCode == 0, Summary = summary, Output = Limit($"Exit code: {process.ExitCode}\n{output}", settings.MaxToolOutputCharacters) };
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"Command exceeded {settings.CommandTimeoutSeconds} seconds.");
        }
    }

    private static void ValidateShellCommand(string command, AgentRuntimeSettings settings)
    {
        if (command.IndexOfAny(['\n', '\r', '`', '>', '<', '$']) >= 0 || command.Contains("$(", StringComparison.Ordinal) || command.Contains("${", StringComparison.Ordinal))
            throw new InvalidOperationException("Shell substitutions, redirections, and multiline commands are blocked.");
        foreach (var segment in Regex.Split(command, @"\s*(?:&&|\|\||;|\|)\s*"))
        {
            var tokens = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(token => token.Trim('\'', '\"')).ToList();
            if (tokens.Count == 0) continue;
            ValidateProgram(tokens[0], tokens.Skip(1).ToList(), settings);
            ValidateArguments(tokens.Skip(1).ToList());
        }
    }

    private static void ValidateProgram(string program, IReadOnlyList<string> arguments, AgentRuntimeSettings settings)
    {
        program = Path.GetFileName(program);
        if (AlwaysBlockedCommands.Contains(program)) throw new InvalidOperationException($"Command '{program}' is always blocked for safety.");
        if (!settings.AllowedCommands.Contains(program, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"Command '{program}' is not allowed.");
        if (program.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            var subcommand = arguments.FirstOrDefault(arg => !arg.StartsWith('-')) ?? "";
            var allowed = subcommand == "init" || ReadOnlyGitCommands.Contains(subcommand) || settings.AllowGitCommit && subcommand is "add" or "commit" || settings.AllowGitPush && subcommand == "push";
            if (!allowed) throw new InvalidOperationException($"Git '{subcommand}' is not permitted.");
            if (subcommand == "push" && arguments.Any(arg => arg.Contains("force", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Force push is always blocked.");
        }
        if (program.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && arguments.FirstOrDefault() is not ("build" or "test" or "restore" or "format"))
            throw new InvalidOperationException("Only dotnet build, test, restore, and format are allowed.");
        if (program.Equals("find", StringComparison.OrdinalIgnoreCase) && arguments.Any(argument => argument is "-delete" or "-exec" or "-execdir" or "-ok" or "-okdir"))
            throw new InvalidOperationException("Find actions that delete files or execute other programs are blocked.");
    }

    private static bool ShellRequiresOwnedGit(string command) => Regex.Split(command, @"\s*(?:&&|\|\||;|\|)\s*")
        .Select(segment => segment.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(token => token.Trim('\'', '\"')).ToList())
        .Any(tokens => tokens.FirstOrDefault()?.Equals("git", StringComparison.OrdinalIgnoreCase) == true && !IsGitInit(tokens.Skip(1).ToList()));

    private static bool IsGitInit(IReadOnlyList<string> arguments) => arguments.FirstOrDefault(arg => !arg.StartsWith('-')) == "init";

    private static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            var candidate = argument.Contains('=') ? argument[(argument.IndexOf('=') + 1)..] : argument;
            if (string.IsNullOrWhiteSpace(candidate) || candidate is "." or "./") continue;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme is "http" or "https") continue;
                throw new InvalidOperationException($"URI scheme '{uri.Scheme}' is not allowed.");
            }
            if (Path.IsPathRooted(candidate) || candidate.StartsWith('~') || candidate.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains(".."))
                throw new InvalidOperationException($"Argument '{argument}' points outside the project workspace.");
        }
    }

    private static string NormalizeToolPath(string projectRoot, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested is "." or "./") return "";
        if (!Path.IsPathRooted(requested)) return requested;
        var full = Path.GetFullPath(requested);
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal) && !full.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            throw new InvalidOperationException("The requested absolute path is outside this project workspace. Use a workspace-relative path such as '.' or 'src'.");
        return Path.GetRelativePath(projectRoot, full).Replace('\\', '/');
    }

    private static void EnsureOwnedGitWorkspace(string projectRoot)
    {
        var marker = Path.Combine(projectRoot, ".git");
        if (!Directory.Exists(marker) && !File.Exists(marker))
            throw new InvalidOperationException("Git is not initialized in this project workspace. Parent repositories are never used. Initialize or import a repository into the workspace first.");
    }

    private async Task BackupAsync(Guid projectId, string relative, string path)
    {
        if (!File.Exists(path)) return;
        var root = await workspace.ResolveProjectPathAsync(projectId, "");
        var backup = Path.Combine(root, ".history", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(path, backup);
    }

    private static void EnsureSafePath(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(InternalFolders.Contains)) throw new InvalidOperationException("Internal workspace folders cannot be accessed by tools.");
    }

    private static object Tool(string name, string description, object parameters) => new { type = "function", function = new { name, description, parameters } };
    private static string Required(JsonElement element, string name, bool allowEmpty = false) { var value = Value(element, name); if (!allowEmpty && string.IsNullOrEmpty(value)) throw new InvalidOperationException($"'{name}' is required."); return value; }
    private static string Value(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + $"\n… truncated after {maximum} characters";
}

file static class AgentToolStringExtensions
{
    public static string? NullIfEmpty(this string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
