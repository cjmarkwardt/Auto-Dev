namespace AutoDev.Core.Services;

/// <summary>Debounced notification that something changed under a workspace's file tree, so the Files sidebar can refresh - carries every full path that changed within the debounce window (so callers can react to a specific file, e.g. .gitignore, without a second watcher).</summary>
public interface IWorkspaceFileWatcher : IDisposable
{
    event Action<IReadOnlySet<string>>? Changed;

    /// <summary>Stops watching (and drops anything already pending in the current debounce window) without disposing - see FilesSectionViewModel.Deactivate, which pauses the watcher entirely while its workspace tab isn't the selected one, rather than reacting to changes nothing is currently displaying.</summary>
    void Pause();

    /// <summary>Resumes watching after Pause - misses whatever changed while paused (there's nothing pending to catch up on), so callers that care about that (see FilesSectionViewModel.Activate) do their own full re-resolve immediately after calling this instead of relying on a Changed event for it.</summary>
    void Resume();
}

public interface IWorkspaceFileWatcherFactory
{
    IWorkspaceFileWatcher Create(string workspacePath);
}
