namespace AutoDev.Core.Services;

public sealed record FileSystemEntry(string Name, string FullPath, bool IsDirectory);

/// <summary>Directory listing + basic file CRUD for the Files sidebar and Edit tab. The `.autodev/` metadata folder is always hidden from listings.</summary>
public interface IFileTreeService
{
    IReadOnlyList<FileSystemEntry> GetChildren(string directoryPath);

    Task<string> ReadFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task WriteFileAsync(string filePath, string content, CancellationToken cancellationToken = default);

    void CreateFile(string directoryPath, string fileName);
    void CreateFolder(string directoryPath, string folderName);
    void Rename(string path, string newName);
    void Delete(string path, bool isDirectory);

    /// <summary>Copies path to a non-colliding sibling in the same parent folder ("name copy", "name copy 2", ... - "name copy.ext" for a file) - recursive for a directory.</summary>
    void Duplicate(string path, bool isDirectory);

    /// <summary>Moves sourcePath into destinationDirectory under its own leaf name (like `mv source dest/`) - used for dragging a file/folder from outside the app into the Files sidebar. A no-op if sourcePath is already directly inside destinationDirectory. Throws IOException if something already exists at the resulting path, unless overwrite is set (which deletes it first).</summary>
    void Move(string sourcePath, string destinationDirectory, bool overwrite = false);

    /// <summary>A file or folder's git status - drives the Files section's per-row status color (see GitFileStatus). Unmodified if the workspace isn't a git repo.</summary>
    Task<GitFileStatus> GetStatusAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Bulk equivalent of GetStatusAsync - one git status/ls-files call for every path in `paths` at once instead of a subprocess per path, keyed by each input path exactly as given. `rootPath` is the workspace root to run git from (paths don't need to share a single parent the way GetStatusAsync derives one from a lone path).</summary>
    Task<IReadOnlyDictionary<string, GitFileStatus>> GetStatusesAsync(string rootPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}
