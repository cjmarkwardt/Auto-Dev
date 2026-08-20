using System.Collections.Concurrent;
using AutoDev.Core.Models;
using Markwardt.TaskRunner;
using Microsoft.Extensions.Logging;

namespace AutoDev.Core.Services;

public sealed class WorkspaceTaskSchedulerService(
    string workspacePath,
    IWorkspaceMetadataStore metadataStore,
    ILogger<WorkspaceTaskSchedulerService> logger) : IWorkspaceTaskScheduler
{
    private readonly ConcurrentDictionary<string, byte> _activeRuns = new();
    private readonly ConcurrentDictionary<string, TaskEngine> _activeEngines = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runCancellations = new();
    private readonly ConcurrentDictionary<string, byte> _userStopped = new();
    private CancellationTokenSource? _cts;

    public event Action<TaskRef>? TaskRunStarted;
    public event Action<TaskRef>? TaskScriptsAvailable;
    public event Action<TaskRunRecord>? TaskRunCompleted;

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

    public IReadOnlyList<ScriptRunner>? GetLiveScripts(string taskId) =>
        _activeEngines.TryGetValue(taskId, out var engine) ? engine.Scripts : null;

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
        // down) actually kills the underlying subprocesses instead of leaving them orphaned and running
        // forever with no way to ever persist a run record for them. Not a `using` here - StopRun needs to
        // reach this specific run's token from outside, for as long as the run is active.
        var linkedCts = _cts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCancellations[task.Path] = linkedCts;

        try
        {
            var filePath = Path.Combine(workspacePath, task.Path);
            var source = await File.ReadAllTextAsync(filePath, cancellationToken);

            TaskRunRecord record;
            try
            {
                var document = TaskDocumentParser.Parse(source);
                var engine = new TaskEngine(document, Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? workspacePath);
                _activeEngines[task.Path] = engine;
                TaskScriptsAvailable?.Invoke(task);

                await engine.RunAsync(linkedCts.Token);
                record = BuildRunRecord(task, engine, startedAt, wasStopped: _userStopped.ContainsKey(task.Path));
            }
            catch (TaskParseException ex)
            {
                record = BuildParseFailureRecord(task, startedAt, ex.Message);
            }

            await PersistCompletedRunAsync(record, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Scheduler disposed (tab closed / app shutting down) before the run's own engine even got a
            // chance to observe the cancellation itself (e.g. cancelled mid file-read) - nothing to persist
            // or notify about. A cancellation requested once the engine is running instead surfaces as an
            // ordinary Failed status on whichever scripts were still active (see ScriptRunner.RunAsync,
            // which never lets that propagate out of TaskEngine.RunAsync), so BuildRunRecord above already
            // covers that far more common case without ever reaching this catch.
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
            _activeEngines.TryRemove(task.Path, out _);
            _activeRuns.TryRemove(task.Path, out _);
        }
    }

    private static TaskRunRecord BuildRunRecord(TaskRef task, TaskEngine engine, DateTimeOffset startedAt, bool wasStopped)
    {
        var scripts = engine.Scripts.Select(script => new ScriptRunRecord(script.Name, script.Status, script.LogText)).ToList();

        return new TaskRunRecord
        {
            TaskPath = task.Path,
            TaskName = task.Name,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Success = !wasStopped && scripts.All(script => script.Status == ScriptStatus.Completed),
            WasStopped = wasStopped,
            Scripts = scripts,
        };
    }

    private static TaskRunRecord BuildParseFailureRecord(TaskRef task, DateTimeOffset startedAt, string parseError) => new()
    {
        TaskPath = task.Path,
        TaskName = task.Name,
        StartedAt = startedAt,
        CompletedAt = DateTimeOffset.UtcNow,
        Success = false,
        ParseError = parseError,
    };

    private async Task PersistCompletedRunAsync(TaskRunRecord record, CancellationToken cancellationToken)
    {
        await metadataStore.AppendTaskRunAsync(workspacePath, record, cancellationToken);
        TaskRunCompleted?.Invoke(record);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

public sealed class TaskSchedulerServiceFactory(
    IWorkspaceMetadataStore metadataStore,
    ILoggerFactory loggerFactory) : ITaskSchedulerServiceFactory
{
    public IWorkspaceTaskScheduler Create(string workspacePath) => new WorkspaceTaskSchedulerService(
        workspacePath,
        metadataStore,
        loggerFactory.CreateLogger<WorkspaceTaskSchedulerService>());
}
