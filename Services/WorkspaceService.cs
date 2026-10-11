using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class WorkspaceService(IConfiguration configuration, StateStore store)
{
    private const long MaxUploadBytes = 250L * 1024 * 1024;
    private const long MaxExtractedBytes = 500L * 1024 * 1024;
    private const int MaxArchiveEntries = 5_000;
    private readonly string _root = Path.GetFullPath(configuration["Storage:ProjectsPath"] ?? "/data/projects");

    public async Task<string> GetProjectRootAsync(Guid projectId)
    {
        var folder = await store.UpdateAsync(state =>
        {
            var project = state.Projects.First(x => x.Id == projectId);
            if (string.IsNullOrWhiteSpace(project.WorkspaceFolder))
                project.WorkspaceFolder = NextFolderName(project.Name);
            return project.WorkspaceFolder;
        });
        var path = Path.Combine(_root, folder);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<FileManagerViewModel> BuildViewAsync(Project project, string? relativePath)
    {
        var root = await GetProjectRootAsync(project.Id);
        var normalized = NormalizeRelativePath(relativePath ?? "", allowEmpty: true);
        var directory = Resolve(root, normalized);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
        var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos()
            .Where(x => !IsInternal(x.Name))
            .Select(x => ToEntry(root, x))
            .OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var stateView = await store.ReadAsync(state => new
        {
            Conversations = state.Conversations.Where(x => x.ProjectId == project.Id).OrderByDescending(x => x.UpdatedAt).ToList(),
            CanPublish = state.AgentRuntime.Settings.AllowGitHubPublish && state.ProjectRepositories.Any(link => link.ProjectId == project.Id)
        });
        return new FileManagerViewModel { Project = project, Path = normalized, Entries = entries, Conversations = stateView.Conversations, CanPublishToGitHub = stateView.CanPublish };
    }

    public async Task<PendingUpload> StageAsync(Guid projectId, Guid userId, IReadOnlyList<IFormFile> files, string? basePath = null)
    {
        if (files.Count == 0) throw new InvalidOperationException("Choose at least one file.");
        if (files.Sum(x => x.Length) > MaxUploadBytes) throw new InvalidOperationException("The upload exceeds the 250 MB limit.");
        var pending = new PendingUpload { ProjectId = projectId, UserId = userId };
        var staging = StagingPath(pending.Id);
        Directory.CreateDirectory(staging);
        foreach (var file in files)
        {
            if (file.Length <= 0) continue;
            var relative = NormalizeRelativePath(Path.Combine(basePath ?? "", file.FileName));
            var stagedName = Guid.NewGuid().ToString("N");
            await using var target = File.Create(Path.Combine(staging, stagedName));
            await file.CopyToAsync(target);
            pending.Items.Add(new PendingUploadItem
            {
                StagedName = stagedName, RelativePath = relative, Size = file.Length,
                IsZip = Path.GetExtension(relative).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            });
        }
        if (pending.Items.Count == 0) throw new InvalidOperationException("The selected files were empty.");
        await store.UpdateAsync(state => { state.PendingUploads.Add(pending); return true; });
        return pending;
    }

    public async Task<(PendingUpload Upload, Project Project, List<string> Conflicts)> GetPendingAsync(Guid id, Guid userId)
    {
        var data = await store.ReadAsync(state => new
        {
            Upload = state.PendingUploads.FirstOrDefault(x => x.Id == id && x.UserId == userId),
            Projects = state.Projects
        });
        var upload = data.Upload ?? throw new FileNotFoundException("Pending upload not found.");
        var project = data.Projects.First(x => x.Id == upload.ProjectId);
        var root = await GetProjectRootAsync(project.Id);
        var conflicts = upload.Items.Where(x => File.Exists(Resolve(root, x.RelativePath)) || Directory.Exists(Resolve(root, x.RelativePath))).Select(x => x.RelativePath).ToList();
        foreach (var zip in upload.Items.Where(x => x.IsZip))
        {
            var folder = Path.Combine(Path.GetDirectoryName(zip.RelativePath) ?? "", Path.GetFileNameWithoutExtension(zip.RelativePath));
            if (Directory.Exists(Resolve(root, folder))) conflicts.Add(folder + "/");
        }
        return (upload, project, conflicts.Distinct().ToList());
    }

    public async Task CommitPendingAsync(PendingUpload upload, string conflictAction, bool extractZips)
    {
        var root = await GetProjectRootAsync(upload.ProjectId);
        var userPaths = new List<string>();
        if (conflictAction == "cancel") { await CancelPendingAsync(upload.Id); return; }
        foreach (var item in upload.Items)
        {
            var destination = Resolve(root, item.RelativePath);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                if (conflictAction == "keep") destination = UniquePath(destination);
                else if (conflictAction == "override") DeleteExisting(destination);
                else throw new InvalidOperationException("Choose how to handle name conflicts.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(StagingPath(upload.Id), item.StagedName), destination);
            userPaths.Add(Path.GetRelativePath(root, destination).Replace('\\', '/'));
            if (extractZips && item.IsZip)
            {
                var extract = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileNameWithoutExtension(destination));
                if (File.Exists(extract) || Directory.Exists(extract))
                {
                    if (conflictAction == "keep") extract = UniquePath(extract);
                    else if (conflictAction == "override") DeleteExisting(extract);
                }
                ExtractZipSafely(destination, extract);
                userPaths.AddRange(Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')));
            }
        }
        await RemovePendingAsync(upload.Id);
        await MarkUserManagedAsync(upload.ProjectId, userPaths);
    }

    public async Task CancelPendingAsync(Guid id)
    {
        var staging = StagingPath(id);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        await RemovePendingAsync(id);
    }

    public async Task<string> CreateResultFolderAsync(Guid projectId, Guid jobId)
    {
        var root = await GetProjectRootAsync(projectId);
        var results = Path.Combine(root, "results");
        Directory.CreateDirectory(results);
        var number = 1;
        string folder;
        do folder = Path.Combine(results, $"result-{number++}"); while (Directory.Exists(folder));
        Directory.CreateDirectory(folder);
        return Path.GetRelativePath(root, folder).Replace('\\', '/');
    }

    public async Task<JobResultItem> WriteResultTextAsync(Guid projectId, string resultFolder, string fileName, string content)
    {
        var root = await GetProjectRootAsync(projectId);
        var relative = NormalizeRelativePath(Path.Combine(resultFolder, fileName));
        var path = Resolve(root, relative);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        return new JobResultItem { RelativePath = relative, Size = new FileInfo(path).Length };
    }

    public async Task<List<JobResultItem>> WriteGeneratedFilesAsync(Guid projectId, string resultFolder, string response)
    {
        var results = new List<JobResultItem>();
        var root = await GetProjectRootAsync(projectId);
        var pattern = new Regex("```(?<kind>file|base64-file):(?<path>[^\\r\\n]+)\\r?\\n(?<content>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in pattern.Matches(response))
        {
            try
            {
                var requested = NormalizeRelativePath(match.Groups["path"].Value.Trim());
                var relative = NormalizeRelativePath(Path.Combine(resultFolder, requested));
                var path = Resolve(root, relative);
                if (File.Exists(path) || Directory.Exists(path)) { path = UniquePath(path); relative = Path.GetRelativePath(root, path).Replace('\\', '/'); }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (match.Groups["kind"].Value.Equals("base64-file", StringComparison.OrdinalIgnoreCase))
                    await File.WriteAllBytesAsync(path, Convert.FromBase64String(match.Groups["content"].Value.Trim()));
                else
                    await File.WriteAllTextAsync(path, match.Groups["content"].Value, new UTF8Encoding(false));
                results.Add(new JobResultItem { RelativePath = relative, Size = new FileInfo(path).Length });
            }
            catch { /* Invalid generated file blocks remain visible in response.md but are not written. */ }
        }
        return results;
    }

    public async Task<string?> ReadImageDataUrlAsync(Guid projectId, string relativePath)
    {
        var root = await GetProjectRootAsync(projectId);
        var path = Resolve(root, NormalizeRelativePath(relativePath));
        if (!File.Exists(path) || !IsImage(path) || new FileInfo(path).Length > 10 * 1024 * 1024) return null;
        var mediaType = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", _ => "image/jpeg" };
        return $"data:{mediaType};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(path))}";
    }

    public async Task<string> ReadAttachmentAsync(Guid projectId, string relativePath)
    {
        var root = await GetProjectRootAsync(projectId);
        var path = Resolve(root, NormalizeRelativePath(relativePath));
        if (!File.Exists(path)) return $"[Missing attachment: {relativePath}]";
        var info = new FileInfo(path);
        if (info.Length > 2 * 1024 * 1024) return $"[Attachment {relativePath} is too large to inline: {info.Length} bytes]";
        if (!IsText(path)) return $"[Binary attachment available in the project workspace: {relativePath}, {info.Length} bytes]";
        return $"--- FILE: {relativePath} ---\n{await File.ReadAllTextAsync(path)}\n--- END FILE ---";
    }

    public async Task<string> ResolveProjectPathAsync(Guid projectId, string relativePath)
    {
        var root = await GetProjectRootAsync(projectId);
        return Resolve(root, NormalizeRelativePath(relativePath, allowEmpty: true));
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(Guid projectId)
    {
        var root = await GetProjectRootAsync(projectId);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(root, x).Replace('\\', '/'))
            .Where(x => !x.Split('/').Any(IsInternal))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(500).ToList();
    }

    public async Task<IReadOnlyList<string>> ExpandSelectionToFilesAsync(Guid projectId, IEnumerable<string> relativePaths)
    {
        var root = await GetProjectRootAsync(projectId);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in relativePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var normalized = NormalizeRelativePath(relative);
            var full = Resolve(root, normalized);
            if (File.Exists(full)) files.Add(normalized);
            else if (Directory.Exists(full))
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    var path = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!path.Split('/').Any(IsInternal)) files.Add(path);
                }
        }
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(500).ToList();
    }

    public async Task<PreparedDownload> PrepareDownloadAsync(Guid projectId, string projectName, IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        if (relativePaths.Count == 0) throw new InvalidOperationException("Select at least one file or folder.");
        var root = await GetProjectRootAsync(projectId);
        var selected = relativePaths.Select(path => NormalizeRelativePath(path)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new { Relative = path, Full = Resolve(root, path) })
            .Where(item => File.Exists(item.Full) || Directory.Exists(item.Full)).ToList();
        if (selected.Count == 0) throw new FileNotFoundException("The selected files or folders no longer exist.");
        foreach (var item in selected) EnsureDownloadPath(item.Full);

        if (selected.Count == 1 && File.Exists(selected[0].Full))
            return new PreparedDownload { FilePath = selected[0].Full, DownloadName = Path.GetFileName(selected[0].Full), FileCount = 1 };

        var downloadRoot = Path.Combine(_root, ".downloads");
        Directory.CreateDirectory(downloadRoot);
        var archivePath = Path.Combine(downloadRoot, Guid.NewGuid().ToString("N") + ".zip");
        var fileCount = 0;
        try
        {
            await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var item in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archiveRoot = Path.GetFileName(item.Relative.TrimEnd('/'));
                if (File.Exists(item.Full))
                {
                    await AddZipFileAsync(archive, item.Full, archiveRoot, cancellationToken);
                    fileCount++;
                    continue;
                }

                var files = EnumerateDownloadFiles(item.Full).ToList();
                archive.CreateEntry(archiveRoot.TrimEnd('/') + "/");
                foreach (var directory in EnumerateDownloadDirectories(item.Full))
                {
                    var inside = Path.GetRelativePath(item.Full, directory).Replace('\\', '/');
                    archive.CreateEntry($"{archiveRoot}/{inside}/");
                }
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var inside = Path.GetRelativePath(item.Full, file).Replace('\\', '/');
                    await AddZipFileAsync(archive, file, $"{archiveRoot}/{inside}", cancellationToken);
                    fileCount++;
                }
            }
            var baseName = selected.Count == 1 ? Path.GetFileName(selected[0].Relative.TrimEnd('/')) : SafeSegment(projectName) + "-selection";
            return new PreparedDownload { FilePath = archivePath, DownloadName = $"{baseName}.zip", IsTemporary = true, FileCount = fileCount };
        }
        catch
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            throw;
        }
    }

    public Task MarkUserManagedAsync(Guid projectId, IEnumerable<string> relativePaths) => store.UpdateAsync(state =>
    {
        var project = state.Projects.First(project => project.Id == projectId);
        var userPaths = project.UserManagedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = project.AutoPublishPendingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => NormalizeRelativePath(path)))
        {
            userPaths.Add(path);
            pending.Remove(path);
        }
        project.UserManagedPaths = userPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        project.AutoPublishPendingPaths = pending.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        if (project.AutoPublishPendingPaths.Count == 0) project.AutoPublishQueuedAt = null;
        return true;
    });

    public Task QueueAiGeneratedAsync(Guid projectId, IEnumerable<string> relativePaths, bool publishSoon = false) => store.UpdateAsync(state =>
    {
        var project = state.Projects.First(project => project.Id == projectId);
        var userPaths = project.UserManagedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = project.AutoPublishPendingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => NormalizeRelativePath(path)))
            if (!userPaths.Contains(path)) pending.Add(path);
        project.AutoPublishPendingPaths = pending.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        if (project.AutoPublishPendingPaths.Count > 0)
            project.AutoPublishQueuedAt = publishSoon ? DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5) : project.AutoPublishQueuedAt ?? DateTimeOffset.UtcNow;
        return true;
    });

    public async Task<int> MoveToTrashAsync(Guid projectId, IEnumerable<string> relativePaths)
    {
        var root = await GetProjectRootAsync(projectId);
        var trashRoot = Path.Combine(root, ".trash", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"));
        var moved = 0;
        foreach (var relativePath in relativePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = NormalizeRelativePath(relativePath);
            var source = Resolve(root, normalized);
            if (!File.Exists(source) && !Directory.Exists(source)) continue;
            var destination = Path.Combine(trashRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(source)) File.Move(source, destination);
            else Directory.Move(source, destination);
            moved++;
        }
        return moved;
    }

    public async Task<IReadOnlyList<(string Source, string Destination)>> MoveItemsAsync(Guid projectId, IEnumerable<string> relativePaths, string destinationPath)
    {
        var root = await GetProjectRootAsync(projectId);
        var destinationRelative = NormalizeRelativePath(destinationPath, allowEmpty: true);
        var destinationFolder = Resolve(root, destinationRelative);
        if (!Directory.Exists(destinationFolder)) throw new DirectoryNotFoundException("The destination folder no longer exists.");
        var planned = new List<(string SourceRelative, string Source)>();
        foreach (var relativePath in relativePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sourceRelative = NormalizeRelativePath(relativePath);
            var source = Resolve(root, sourceRelative);
            if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException($"{sourceRelative} no longer exists.");
            var sourceParent = Path.GetDirectoryName(sourceRelative)?.Replace('\\', '/') ?? "";
            if (string.Equals(sourceParent, destinationRelative, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{Path.GetFileName(sourceRelative)} is already in that folder.");
            if (Directory.Exists(source) && (string.Equals(destinationRelative, sourceRelative, StringComparison.OrdinalIgnoreCase) || destinationRelative.StartsWith(sourceRelative + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A folder cannot be moved into itself or one of its subfolders.");
            planned.Add((sourceRelative, source));
        }
        if (planned.Count == 0) throw new InvalidOperationException("Nothing was moved.");
        var moves = new List<(string Source, string Destination)>();
        foreach (var item in planned)
        {
            var destination = Path.Combine(destinationFolder, Path.GetFileName(item.Source));
            if (File.Exists(destination) || Directory.Exists(destination)) destination = UniquePath(destination);
            if (File.Exists(item.Source)) File.Move(item.Source, destination); else Directory.Move(item.Source, destination);
            moves.Add((item.SourceRelative, Path.GetRelativePath(root, destination).Replace('\\', '/')));
        }
        await store.UpdateAsync(state =>
        {
            var project = state.Projects.First(project => project.Id == projectId);
            project.UserManagedPaths = RewriteMovedPaths(project.UserManagedPaths, moves);
            project.AutoPublishPendingPaths = RewriteMovedPaths(project.AutoPublishPendingPaths, moves);
            foreach (var job in state.Jobs.Where(job => job.ProjectId == projectId))
            {
                job.ProjectFilesChanged = RewriteMovedPaths(job.ProjectFilesChanged, moves);
                job.PublishedProjectFiles = RewriteMovedPaths(job.PublishedProjectFiles, moves);
            }
            return true;
        });
        return moves;
    }

    private static List<string> RewriteMovedPaths(IEnumerable<string> paths, IReadOnlyList<(string Source, string Destination)> moves)
        => paths.Select(path =>
        {
            var move = moves.FirstOrDefault(item => string.Equals(path, item.Source, StringComparison.OrdinalIgnoreCase) || path.StartsWith(item.Source + "/", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(move.Source) ? path : move.Destination + path[move.Source.Length..];
        }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<string> ImportRepositoryAsync(Guid projectId, string fullName, byte[] archive)
    {
        var root = await GetProjectRootAsync(projectId);
        var relative = NormalizeRelativePath(Path.Combine("repositories", fullName));
        var destination = Resolve(root, relative);
        if (Directory.Exists(destination) || File.Exists(destination)) destination = UniquePath(destination);
        var temporary = Path.Combine(_root, ".staging", Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        await File.WriteAllBytesAsync(temporary, archive);
        try { ExtractZipSafely(temporary, destination); }
        finally { File.Delete(temporary); }
        return Path.GetRelativePath(root, destination).Replace('\\', '/');
    }

    public static bool IsText(string path) => new[] { ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".csv", ".tsv", ".html", ".css", ".js", ".ts", ".cs", ".py", ".sh", ".sql", ".log", ".svg" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private WorkspaceEntry ToEntry(string root, FileSystemInfo info)
    {
        var isDirectory = info is DirectoryInfo;
        var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
        var stats = isDirectory ? DirectoryStats((DirectoryInfo)info) : (Size: ((FileInfo)info).Length, Files: 0, Directories: 0);
        var size = stats.Size;
        var kind = isDirectory ? "folder" : IsImage(info.FullName) ? "image" : IsText(info.FullName) ? "text" : Path.GetExtension(info.Name).TrimStart('.').ToLowerInvariant() is { Length: > 0 } ext ? ext : "file";
        var preview = !isDirectory && IsText(info.FullName) && size <= 64 * 1024 ? ReadPreview(info.FullName) : "";
        return new WorkspaceEntry { Name = info.Name, RelativePath = relative, IsDirectory = isDirectory, Size = size, FileCount = stats.Files, DirectoryCount = stats.Directories, ModifiedAt = info.LastWriteTimeUtc, Kind = kind, Preview = preview };
    }

    private static string ReadPreview(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[240];
        var count = reader.Read(buffer, 0, buffer.Length);
        return new string(buffer, 0, count);
    }

    private string NextFolderName(string projectName)
    {
        Directory.CreateDirectory(_root);
        var name = SafeSegment(projectName);
        var number = 1;
        string candidate;
        do candidate = $"{name}-{number++}"; while (Directory.Exists(Path.Combine(_root, candidate)));
        return candidate;
    }

    private string StagingPath(Guid id) => Path.Combine(_root, ".staging", id.ToString("N"));
    private async Task RemovePendingAsync(Guid id)
    {
        var staging = StagingPath(id);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        await store.UpdateAsync(state => { state.PendingUploads.RemoveAll(x => x.Id == id); return true; });
    }
    private static bool IsInternal(string name) => name is ".staging" or ".trash" or ".history" or ".undo";

    private static void EnsureDownloadPath(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Symbolic links cannot be downloaded because they may point outside the project.");
    }

    private static IEnumerable<string> EnumerateDownloadFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsureDownloadPath(directory);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                EnsureDownloadPath(file);
                yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                EnsureDownloadPath(child);
                pending.Push(child);
            }
        }
    }

    private static IEnumerable<string> EnumerateDownloadDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                EnsureDownloadPath(child);
                yield return child;
                pending.Push(child);
            }
        }
    }

    private static async Task AddZipFileAsync(ZipArchive archive, string sourcePath, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Fastest);
        entry.LastWriteTime = File.GetLastWriteTimeUtc(sourcePath);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        await using var target = entry.Open();
        await source.CopyToAsync(target, cancellationToken);
    }

    public static string NormalizeRelativePath(string value, bool allowEmpty = false)
    {
        var parts = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(x => x is "." or "..")) throw new InvalidOperationException("Invalid path.");
        var normalized = string.Join('/', parts.Select(SafeSegment));
        if (!allowEmpty && string.IsNullOrWhiteSpace(normalized)) throw new InvalidOperationException("Invalid file name.");
        return normalized;
    }

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "item" : safe;
    }

    private static string Resolve(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (full != fullRoot.TrimEnd(Path.DirectorySeparatorChar) && !full.StartsWith(fullRoot, StringComparison.Ordinal)) throw new InvalidOperationException("Path leaves the project workspace.");
        return full;
    }

    private static string UniquePath(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var extension = File.Exists(path) ? Path.GetExtension(path) : "";
        var name = extension.Length > 0 ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
        var number = 2;
        string candidate;
        do candidate = Path.Combine(directory, $"{name} ({number++}){extension}"); while (File.Exists(candidate) || Directory.Exists(candidate));
        return candidate;
    }

    private static void DeleteExisting(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static (long Size, int Files, int Directories) DirectoryStats(DirectoryInfo directory)
    {
        try
        {
            var files = directory.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            return (files.Sum(file => file.Length), files.Count, directory.EnumerateDirectories("*", SearchOption.AllDirectories).Count());
        }
        catch { return (0, 0, 0); }
    }

    private static void ExtractZipSafely(string archivePath, string destination)
    {
        try
        {
            Directory.CreateDirectory(destination);
            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > MaxArchiveEntries) throw new InvalidOperationException("ZIP contains too many entries.");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                total += entry.Length;
                if (entry.Length > MaxExtractedBytes || total > MaxExtractedBytes) throw new InvalidOperationException("ZIP expands beyond the 500 MB limit.");
                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException("ZIP contains an unsafe path.");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, false);
            }
        }
        catch { if (Directory.Exists(destination)) Directory.Delete(destination, true); throw; }
    }
}
