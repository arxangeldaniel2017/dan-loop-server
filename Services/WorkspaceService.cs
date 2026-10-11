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
        Directory.CreateDirectory(Path.Combine(path, "results"));
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
        var conversations = await store.ReadAsync(state => state.Conversations.Where(x => x.ProjectId == project.Id).OrderByDescending(x => x.UpdatedAt).ToList());
        return new FileManagerViewModel { Project = project, Path = normalized, Entries = entries, Conversations = conversations };
    }

    public async Task<PendingUpload> StageAsync(Guid projectId, Guid userId, IReadOnlyList<IFormFile> files)
    {
        if (files.Count == 0) throw new InvalidOperationException("Choose at least one file.");
        if (files.Sum(x => x.Length) > MaxUploadBytes) throw new InvalidOperationException("The upload exceeds the 250 MB limit.");
        var pending = new PendingUpload { ProjectId = projectId, UserId = userId };
        var staging = StagingPath(pending.Id);
        Directory.CreateDirectory(staging);
        foreach (var file in files)
        {
            if (file.Length <= 0) continue;
            var relative = NormalizeRelativePath(file.FileName);
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
            if (extractZips && item.IsZip)
            {
                var extract = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileNameWithoutExtension(destination));
                if (File.Exists(extract) || Directory.Exists(extract))
                {
                    if (conflictAction == "keep") extract = UniquePath(extract);
                    else if (conflictAction == "override") DeleteExisting(extract);
                }
                ExtractZipSafely(destination, extract);
            }
        }
        await RemovePendingAsync(upload.Id);
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
    private Task RemovePendingAsync(Guid id) => store.UpdateAsync(state => { state.PendingUploads.RemoveAll(x => x.Id == id); return true; });
    private static bool IsInternal(string name) => name is ".staging" or ".trash" or ".history" or ".undo";

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
        var safe = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
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
