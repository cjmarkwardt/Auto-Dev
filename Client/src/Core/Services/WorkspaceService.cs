using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public sealed class WorkspaceService(ISettingsService settingsService, IWorkspaceMetadataStore metadataStore) : IWorkspaceService
{
    public async Task<IReadOnlyList<WorkspaceInfo>> GetRecentWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        return [.. settings.RecentWorkspacePaths
            .Where(Directory.Exists)
            .Select(p => new WorkspaceInfo(p))];
    }

    public async Task<WorkspaceInfo> OpenOrCreateAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(folderPath);
        Directory.CreateDirectory(fullPath);
        metadataStore.EnsureInitialized(fullPath);

        var settings = await settingsService.LoadAsync(cancellationToken);
        settings.RecentWorkspacePaths.RemoveAll(p => string.Equals(Path.GetFullPath(p), fullPath, StringComparison.Ordinal));
        settings.RecentWorkspacePaths.Insert(0, fullPath);
        await settingsService.SaveAsync(settings, cancellationToken);

        return new WorkspaceInfo(fullPath);
    }

    public async Task ForgetRecentAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(folderPath);
        var settings = await settingsService.LoadAsync(cancellationToken);
        settings.RecentWorkspacePaths.RemoveAll(p => string.Equals(Path.GetFullPath(p), fullPath, StringComparison.Ordinal));
        await settingsService.SaveAsync(settings, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceInfo>> GetOpenWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        return [.. settings.OpenWorkspacePaths
            .Where(Directory.Exists)
            .Select(p => new WorkspaceInfo(p))];
    }

    public async Task SaveOpenWorkspacesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        settings.OpenWorkspacePaths = [.. paths];
        await settingsService.SaveAsync(settings, cancellationToken);
    }

    public async Task<string?> GetLastParentFolderAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        return settings.LastParentFolderPath is { } path && Directory.Exists(path) ? path : null;
    }

    public async Task SaveLastParentFolderAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        settings.LastParentFolderPath = Path.GetFullPath(directoryPath);
        await settingsService.SaveAsync(settings, cancellationToken);
    }
}
