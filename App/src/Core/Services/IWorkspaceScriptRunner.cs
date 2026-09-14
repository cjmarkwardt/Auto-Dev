using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

/// <summary>
/// Scripts only ever run manually (via RunNowAsync/RunTaskNowAsync) - this tracks/broadcasts the state of those
/// runs across every viewer (sidebar rows, the Script tab) for one workspace. Only one script or task total ever
/// runs at a time per workspace - see RunNowAsync/RunTaskNowAsync. A running task's own scripts are the one
/// exception to "one at a time": every script in a batch runs concurrently against each other, tracked through
/// the exact same per-script members below (IsRunning/GetLiveRun/StopRun, ScriptRunStarted/ScriptRunCompleted)
/// as a standalone script would be - see RunTaskNowAsync's own doc comment.
/// </summary>
public interface IWorkspaceScriptRunner : IDisposable
{
    /// <summary>Establishes the token every run links against so disposing the runner (workspace tab closed, app shutting down) actually kills every in-flight `dotnet run` subprocess - call once per workspace before the first RunNowAsync/RunTaskNowAsync.</summary>
    void Start();

    /// <summary>Runs one .cs file immediately via `dotnet run --file` (used by the sidebar's "Run" context-menu action, or double-clicking the file) - a no-op if this same file is already running, or if any other .cs file or .task file in this workspace is: only one script or task total runs at a time per workspace.</summary>
    Task RunNowAsync(ScriptRef script, CancellationToken cancellationToken = default);

    bool IsRunning(string scriptId);

    /// <summary>The script's own live output as `dotnet run` streams it, if it's currently running - null once the run finishes (see ScriptRunCompleted for the final record instead). A viewer (the Script tab) binds to this directly rather than the runner re-buffering it separately. Works identically for a task's own child script, keyed the same way (by its own workspace-relative path).</summary>
    LiveScriptRun? GetLiveRun(string scriptId);

    /// <summary>
    /// Forcefully cancels the script's in-flight run (kills the underlying `dotnet run` subprocess) - a
    /// no-op returning false if it isn't currently running. The run still completes normally afterwards from
    /// the caller's perspective: ScriptRunCompleted fires with a record marked WasStopped, so history and
    /// any Script tab viewing it reflect the stop instead of just going silent. Stopping a task's own child
    /// script this way only stops that one script - see StopTask to stop every script in a running task at once.
    /// </summary>
    bool StopRun(string scriptId);

    /// <summary>
    /// Runs every script in a .task file's batches (see TaskFileParser) - all scripts within one batch run
    /// concurrently against each other via the same RunNowAsync machinery each script would otherwise use
    /// standalone (so IsRunning/GetLiveRun/StopRun and the ScriptRunStarted/ScriptRunCompleted events below all
    /// work identically for one of the task's own scripts, tagged via ScriptRef.TaskId/ScriptRunRecord.TaskId);
    /// the next batch only starts once every script in the batches before it has exited successfully. The task
    /// stops - skipping every batch still to come - the moment any script in the current batch fails or the
    /// whole task is cancelled via StopTask. A no-op if this task (or any other script/task in this workspace)
    /// is already running - only one script or task total runs at a time per workspace.
    /// </summary>
    Task RunTaskNowAsync(TaskRef task, IReadOnlyList<IReadOnlyList<ScriptRef>> batches, CancellationToken cancellationToken = default);

    bool IsTaskRunning(string taskId);

    /// <summary>Forcefully cancels every currently-running script belonging to this task (killing each one's underlying `dotnet run` subprocess) and skips every batch still to come - a no-op returning false if the task isn't currently running. Each cancelled child script still completes normally from the caller's perspective exactly like an individual StopRun would, and the task's own TaskRunCompleted fires with a record marked WasStopped.</summary>
    bool StopTask(string taskId);

    /// <summary>Raised the instant a run actually starts, before `dotnet run` has even been launched - a viewer knows immediately that a script neither running nor previously connected to has become live. Fires for a task's own child scripts too (see ScriptRef.TaskId), alongside TaskRunStarted for the task itself.</summary>
    event Action<ScriptRef>? ScriptRunStarted;

    event Action<ScriptRunRecord>? ScriptRunCompleted;

    /// <summary>Raised the instant a task run actually starts, before its first batch has been launched.</summary>
    event Action<TaskRef>? TaskRunStarted;

    event Action<TaskRunRecord>? TaskRunCompleted;
}

public interface IScriptRunnerServiceFactory
{
    IWorkspaceScriptRunner Create(string workspacePath);
}
