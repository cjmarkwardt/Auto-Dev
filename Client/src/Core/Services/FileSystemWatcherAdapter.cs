namespace AutoDev.Core.Services;

public sealed class FileSystemWatcherAdapter : IWorkspaceFileWatcher
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

    private readonly FileSystemWatcher _watcher;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.Ordinal);
    private readonly string[] _ignoredRoots;
    private CancellationTokenSource? _debounceCts;

    public FileSystemWatcherAdapter(string workspacePath)
    {
        // ".git" and ".autodev" are both pure internal bookkeeping, never something a user-facing "the
        // workspace changed" refresh should react to - and for ".git" specifically, NOT excluding it used to
        // cause a genuine feedback loop: a plain read-only `git status` still touches ".git/index"'s own
        // mtime (refreshing its stat cache) even when nothing real changed, which this watcher would then see
        // as a change, triggering another status check (e.g. Files section's Changes Mode reloading), which
        // touches the index again, forever - visible as the whole Files tree (and any hover highlight in it)
        // continuously rebuilding/flickering every debounce window for as long as anything kept re-querying
        // git status in response.
        _ignoredRoots =
        [
            Path.Combine(workspacePath, ".git") + Path.DirectorySeparatorChar,
            Path.Combine(workspacePath, ".autodev") + Path.DirectorySeparatorChar,
        ];

        _watcher = new FileSystemWatcher(workspacePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        _watcher.Changed += (_, e) => ScheduleRaise(e.FullPath);
        _watcher.Created += (_, e) => ScheduleRaise(e.FullPath);
        _watcher.Deleted += (_, e) => ScheduleRaise(e.FullPath);
        _watcher.Renamed += (_, e) => ScheduleRaise(e.OldFullPath, e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    public event Action<IReadOnlySet<string>>? Changed;

    private bool IsIgnored(string path) => _ignoredRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal));

    private void ScheduleRaise(params ReadOnlySpan<string> paths)
    {
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            foreach (var path in paths)
            {
                if (!IsIgnored(path))
                {
                    _pendingPaths.Add(path);
                }
            }

            if (_pendingPaths.Count == 0)
            {
                return; // every path in this batch was under .git/.autodev - nothing worth debouncing/raising
            }

            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            cts = _debounceCts;
        }

        _ = DebounceAndRaiseAsync(cts.Token);
    }

    private async Task DebounceAndRaiseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceWindow, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        HashSet<string> paths;
        lock (_gate)
        {
            paths = [.. _pendingPaths];
            _pendingPaths.Clear();
        }

        Changed?.Invoke(paths);
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}

public sealed class WorkspaceFileWatcherFactory : IWorkspaceFileWatcherFactory
{
    public IWorkspaceFileWatcher Create(string workspacePath) => new FileSystemWatcherAdapter(workspacePath);
}
