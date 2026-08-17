namespace AutoDev.Core.Services;

/// <summary>Debounced notification that something changed under a workspace's file tree, so the Files sidebar can refresh - carries every full path that changed within the debounce window (so callers can react to a specific file, e.g. .gitignore, without a second watcher).</summary>
public interface IWorkspaceFileWatcher : IDisposable
{
    event Action<IReadOnlySet<string>>? Changed;
}

public interface IWorkspaceFileWatcherFactory
{
    IWorkspaceFileWatcher Create(string workspacePath);
}
