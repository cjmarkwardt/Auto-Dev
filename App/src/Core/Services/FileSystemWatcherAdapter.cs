namespace AutoDev.Core.Services;

public sealed class FileSystemWatcherAdapter : IWorkspaceFileWatcher
{
    private static readonly TimeSpan debounceWindow = TimeSpan.FromMilliseconds(250);

    private readonly FileSystemWatcher watcher;
    private readonly Lock gate = new();
    private readonly HashSet<string> pendingPaths = new(StringComparer.Ordinal);
    private readonly string[] ignoredRoots;
    private CancellationTokenSource? debounceCts;

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
        ignoredRoots =
        [
            Path.Combine(workspacePath, ".git") + Path.DirectorySeparatorChar,
            Path.Combine(workspacePath, ".autodev") + Path.DirectorySeparatorChar,
        ];

        watcher = new FileSystemWatcher(workspacePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        watcher.Changed += (_, e) => ScheduleRaise(e.FullPath);
        watcher.Created += (_, e) => ScheduleRaise(e.FullPath);
        watcher.Deleted += (_, e) => ScheduleRaise(e.FullPath);
        watcher.Renamed += (_, e) => ScheduleRaise(e.OldFullPath, e.FullPath);
        watcher.EnableRaisingEvents = true;
    }

    public event Action<IReadOnlySet<string>>? Changed;

    private bool IsIgnored(string path) => ignoredRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal));

    private void ScheduleRaise(params ReadOnlySpan<string> paths)
    {
        CancellationTokenSource? cts = null;
        lock (gate)
        {
            foreach (string path in paths)
            {
                if (!IsIgnored(path))
                {
                    pendingPaths.Add(path);
                }
            }

            if (pendingPaths.Count == 0)
            {
                return; // every path in this batch was under .git/.autodev - nothing worth debouncing/raising
            }

            debounceCts?.Cancel();
            debounceCts = new CancellationTokenSource();
            cts = debounceCts;
        }

        _ = DebounceAndRaiseAsync(cts.Token);
    }

    private async Task DebounceAndRaiseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(debounceWindow, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        HashSet<string> paths;
        lock (gate)
        {
            paths = [.. pendingPaths];
            pendingPaths.Clear();
        }

        Changed?.Invoke(paths);
    }

    public void Pause()
    {
        watcher.EnableRaisingEvents = false;
        lock (gate)
        {
            debounceCts?.Cancel();
            pendingPaths.Clear();
        }
    }

    public void Resume() => watcher.EnableRaisingEvents = true;

    public void Dispose()
    {
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        debounceCts?.Cancel();
        debounceCts?.Dispose();
    }
}

public sealed class WorkspaceFileWatcherFactory : IWorkspaceFileWatcherFactory
{
    public IWorkspaceFileWatcher Create(string workspacePath) => new FileSystemWatcherAdapter(workspacePath);
}
