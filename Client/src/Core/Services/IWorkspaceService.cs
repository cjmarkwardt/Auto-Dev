using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public interface IWorkspaceService
{
    Task<IReadOnlyList<WorkspaceInfo>> GetRecentWorkspacesAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens (or creates, if it doesn't exist) a workspace folder, ensures its `.autodev/` metadata dir exists, and records it as recent.</summary>
    Task<WorkspaceInfo> OpenOrCreateAsync(string folderPath, CancellationToken cancellationToken = default);

    Task ForgetRecentAsync(string folderPath, CancellationToken cancellationToken = default);

    /// <summary>The exact set of workspace tabs open when the app last closed (see AppSettings.OpenWorkspacePaths), filtered to folders that still exist - same Directory.Exists filtering GetRecentWorkspacesAsync already applies.</summary>
    Task<IReadOnlyList<WorkspaceInfo>> GetOpenWorkspacesAsync(CancellationToken cancellationToken = default);

    Task SaveOpenWorkspacesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>The parent directory the folder picker was last browsed into (see AppSettings.LastParentFolderPath) - null if never set or the saved directory no longer exists.</summary>
    Task<string?> GetLastParentFolderAsync(CancellationToken cancellationToken = default);

    Task SaveLastParentFolderAsync(string directoryPath, CancellationToken cancellationToken = default);
}
