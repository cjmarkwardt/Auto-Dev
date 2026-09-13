using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public interface IWorkspaceService
{
    Task<IReadOnlyList<WorkspaceInfo>> GetRecentWorkspacesAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens (or creates, if it doesn't exist) a workspace folder, ensures its `.autodev/` metadata dir exists, and records it as recent.</summary>
    Task<WorkspaceInfo> OpenOrCreateAsync(string folderPath, CancellationToken cancellationToken = default);

    Task ForgetRecentAsync(string folderPath, CancellationToken cancellationToken = default);

    /// <summary>The parent directory the folder picker was last browsed into (see AppSettings.LastParentFolderPath) - null if never set or the saved directory no longer exists.</summary>
    Task<string?> GetLastParentFolderAsync(CancellationToken cancellationToken = default);

    Task SaveLastParentFolderAsync(string directoryPath, CancellationToken cancellationToken = default);
}
