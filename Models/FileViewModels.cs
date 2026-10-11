namespace DanLoopServer.Models;

public sealed class FileManagerViewModel
{
    public required Project Project { get; init; }
    public string Path { get; init; } = "";
    public IReadOnlyList<WorkspaceEntry> Entries { get; init; } = [];
    public IReadOnlyList<Conversation> Conversations { get; init; } = [];
    public bool CanPublishToGitHub { get; init; }
}

public sealed class WorkspaceEntry
{
    public string Name { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public string Kind { get; init; } = "file";
    public string Preview { get; init; } = "";
}

public sealed class TextEditorViewModel
{
    public required Project Project { get; init; }
    public string Path { get; init; } = "";
    public string Content { get; init; } = "";
    public string Language { get; init; } = "text";
}

public sealed class ImageEditorViewModel
{
    public required Project Project { get; init; }
    public string Path { get; init; } = "";
    public string ParentPath { get; init; } = "";
    public bool CanSave { get; init; }
}

public sealed class FilePreviewViewModel
{
    public required Project Project { get; init; }
    public string Path { get; init; } = "";
    public string ParentPath { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
}

public sealed class PreparedDownload
{
    public string FilePath { get; init; } = "";
    public string DownloadName { get; init; } = "";
    public bool IsTemporary { get; init; }
    public int FileCount { get; init; }
}

public sealed class UploadConfirmationViewModel
{
    public required Project Project { get; init; }
    public required PendingUpload Upload { get; init; }
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public bool ContainsZip { get; init; }
}
