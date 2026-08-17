using System.Collections.Concurrent;
using AutoDev.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoDev.Core.Services;

public sealed class WorkspaceTaskSchedulerService(
    string workspacePath,
    IWorkspaceMetadataStore metadataStore,
    IScriptTaskRunner scriptRunner,
    ILogger<WorkspaceTaskSchedulerService> logger) : IWorkspaceTaskScheduler
{
    private readonly ConcurrentDictionary<string, byte> _activeRuns = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<ScriptBlockLayout>> _scriptBlockOrder = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentQueue<string>>> _scriptOutputBuffers = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ScriptBlockRunRecord>> _scriptBlockResults = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runCancellations = new();
    private readonly ConcurrentDictionary<string, byte> _userStopped = new();
    private CancellationTokenSource? _cts;

    public event Action<TaskRef>? TaskRunStarted;
    public event Action<TaskRunRecord>? TaskRunCompleted;
    public event Action<string, string, string>? ScriptTaskProgress;
    public event Action<string, ScriptBlockRunRecord>? ScriptBlockCompleted;

    public bool IsRunning(string taskId) => _activeRuns.ContainsKey(taskId);

    public bool StopRun(string taskId)
    {
        if (!_runCancellations.TryGetValue(taskId, out var cts))
        {
            return false;
        }

        _userStopped[taskId] = 0;
        cts.Cancel();
        return true;
    }

    public IReadOnlyList<ScriptBlockLayout> GetScriptBlockLayouts(string taskId) =>
        _scriptBlockOrder.TryGetValue(taskId, out var layouts) ? layouts : [];

    public IReadOnlyList<string> GetScriptOutputSoFar(string taskId, string blockName) =>
        _scriptOutputBuffers.TryGetValue(taskId, out var buffers) && buffers.TryGetValue(blockName, out var buffer)
            ? buffer.ToArray()
            : [];

    public ScriptBlockRunRecord? GetScriptBlockResult(string taskId, string blockName) =>
        _scriptBlockResults.TryGetValue(taskId, out var results) && results.TryGetValue(blockName, out var result)
            ? result
            : null;

    /// <summary>Tasks only ever run manually (see IWorkspaceTaskScheduler) - the scheduler exists purely to track/broadcast the state of runs kicked off via RunNowAsync, so there's no background loop to start; kept only so the CancellationTokenSource every run links against exists before the first RunNowAsync call.</summary>
    public void Start() => _cts ??= new CancellationTokenSource();

    public async Task RunNowAsync(TaskRef task, CancellationToken cancellationToken = default)
    {
        if (!_activeRuns.TryAdd(task.Path, 0))
        {
            return; // already running
        }

        await RunAndTrackAsync(task, cancellationToken);
    }

    private async Task RunAndTrackAsync(TaskRef task, CancellationToken cancellationToken)
    {
        TaskRunStarted?.Invoke(task);
        var startedAt = DateTimeOffset.UtcNow;

        // Link to the scheduler's own lifetime token so disposing it (workspace tab closed, app shutting
        // down) actually kills the underlying subprocess instead of leaving it orphaned and running
        // forever with no way to ever persist a run record for it. Not a `using` here (unlike before) -
        // StopRun needs to reach this specific run's token from outside, for as long as the run is active.
        var linkedCts = _cts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCancellations[task.Path] = linkedCts;

        try
        {
            var content = await File.ReadAllTextAsync(Path.Combine(workspacePath, task.Path), cancellationToken);
            var record = await RunScriptAsync(task, content, linkedCts.Token);
            await PersistCompletedRunAsync(record, cancellationToken);
        }
        catch (OperationCanceledException) when (_userStopped.TryRemove(task.Path, out _))
        {
            // An explicit Stop (as opposed to the workspace tab closing/app shutting down, which cancels
            // every in-flight run the same way but was never marked in _userStopped) - still worth a
            // history entry and a completion notification, so the Output tab and sidebar reflect it was
            // stopped rather than just going silent forever mid-"Running…".
            await PersistCompletedRunAsync(BuildStoppedScriptRecord(task, startedAt), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Scheduler disposed (tab closed / app shutting down) - nothing to persist or notify about,
            // matching the prior behavior for this case.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task run failed for task {TaskPath} in {WorkspacePath}", task.Path, workspacePath);
        }
        finally
        {
            _runCancellations.TryRemove(task.Path, out _);
            _userStopped.TryRemove(task.Path, out _);
            linkedCts.Dispose();
            _activeRuns.TryRemove(task.Path, out _);
            _scriptOutputBuffers.TryRemove(task.Path, out _);
            _scriptBlockResults.TryRemove(task.Path, out _);

            // _scriptBlockOrder is deliberately NOT cleared here: a fast-finishing run (e.g. a plain `echo`)
            // can complete and hit this finally block before OutputTabViewModel.OnScriptProgress's UI-thread-
            // marshaled handler for one of its own already-queued progress lines even runs (Progress<T> and
            // the UI dispatcher both post/queue rather than invoke synchronously) - that handler still needs
            // GetScriptBlockLayouts to resolve the panel's Row/Column correctly. Left in place, it's simply
            // overwritten at the start of the next run; a stale entry between runs is never read by anything.
        }
    }

    /// <summary>Builds a stopped-run record from whatever's already buffered - a block that had already finished before the stop keeps its real result (WasStopped false); every other block is recorded as stopped (WasStopped true) with whatever it had streamed so far.</summary>
    private TaskRunRecord BuildStoppedScriptRecord(TaskRef task, DateTimeOffset startedAt)
    {
        var blockRecords = GetScriptBlockLayouts(task.Path)
            .Select(layout => GetScriptBlockResult(task.Path, layout.Name)
                ?? new ScriptBlockRunRecord(layout.Name, false, WasStopped: true, "Stopped by user.", string.Join('\n', GetScriptOutputSoFar(task.Path, layout.Name)), layout.Row, layout.Column))
            .ToList();

        return new TaskRunRecord
        {
            TaskPath = task.Path,
            TaskName = task.Name,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Success = false,
            ErrorMessage = "Stopped by user.",
            WasStopped = true,
            OutputSummary = FormatScriptOutputSummary(blockRecords),
            ScriptBlocks = blockRecords,
        };
    }

    private async Task PersistCompletedRunAsync(TaskRunRecord record, CancellationToken cancellationToken)
    {
        await metadataStore.AppendTaskRunAsync(workspacePath, record, cancellationToken);
        TaskRunCompleted?.Invoke(record);
    }

    private async Task<TaskRunRecord> RunScriptAsync(TaskRef task, string scriptText, CancellationToken cancellationToken)
    {
        _scriptBlockOrder[task.Path] = TryGetScriptLayouts(scriptText);
        var buffers = _scriptOutputBuffers[task.Path] = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
        var results = _scriptBlockResults[task.Path] = new ConcurrentDictionary<string, ScriptBlockRunRecord>();

        var onLine = new Progress<ScriptOutputLine>(evt =>
        {
            var buffer = buffers.GetOrAdd(evt.BlockName, _ => new ConcurrentQueue<string>());
            buffer.Enqueue(evt.Line);
            ScriptTaskProgress?.Invoke(task.Path, evt.BlockName, evt.Line);
        });

        void OnBlockCompleted(ScriptBlockResult result)
        {
            var record = new ScriptBlockRunRecord(result.Name, result.Success, WasStopped: false, result.ErrorMessage, result.Output, result.Row, result.Column);
            results[result.Name] = record;
            ScriptBlockCompleted?.Invoke(task.Path, record);
        }

        var result = await scriptRunner.RunAsync(workspacePath, scriptText, onLine, OnBlockCompleted, cancellationToken);
        var blockRecords = result.Blocks.Select(b => new ScriptBlockRunRecord(b.Name, b.Success, WasStopped: false, b.ErrorMessage, b.Output, b.Row, b.Column)).ToList();

        return new TaskRunRecord
        {
            TaskPath = task.Path,
            TaskName = task.Name,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            OutputSummary = FormatScriptOutputSummary(blockRecords),
            ScriptBlocks = blockRecords,
        };
    }

    /// <summary>Best-effort script layouts ahead of a run actually starting (used to pre-populate the ordered list a live viewer reads via GetScriptBlockLayouts) - a parse failure here just means an empty list, since scriptRunner.RunAsync will independently surface the same parse error as the run's real failure.</summary>
    private static IReadOnlyList<ScriptBlockLayout> TryGetScriptLayouts(string scriptText)
    {
        try
        {
            return TaskDocumentReader.ParseAndResolve(scriptText).Scripts.Select(s => new ScriptBlockLayout(s.Name, s.Row, s.Column)).ToList();
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static string FormatScriptOutputSummary(IReadOnlyList<ScriptBlockRunRecord> blocks) =>
        string.Join("\n\n", blocks.Select(b => $"[{b.Name}]\n{b.Output}"));

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

public sealed class TaskSchedulerServiceFactory(
    IWorkspaceMetadataStore metadataStore,
    IScriptTaskRunner scriptRunner,
    ILoggerFactory loggerFactory) : ITaskSchedulerServiceFactory
{
    public IWorkspaceTaskScheduler Create(string workspacePath) => new WorkspaceTaskSchedulerService(
        workspacePath,
        metadataStore,
        scriptRunner,
        loggerFactory.CreateLogger<WorkspaceTaskSchedulerService>());
}
