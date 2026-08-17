using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

/// <summary>One script's name plus its optional requested Output tab grid position (see TaskScript) - carried alongside the name wherever a viewer needs to build a fresh panel without a full ScriptBlockRunRecord in hand.</summary>
public sealed record ScriptBlockLayout(string Name, int? Row, int? Column);

/// <summary>A .task file's identity for scheduling purposes - Path is workspace-relative and doubles as the key every scheduler/history lookup uses (a task's identity now that there's no central registry issuing GUIDs); Name is the display name (filename without extension), carried alongside since callers that only have Path (e.g. history enumeration) still need something to show.</summary>
public sealed record TaskRef(string Path, string Name);

/// <summary>Tasks only ever run manually (via RunNowAsync) - this tracks/broadcasts the state of those runs across every viewer (sidebar rows, the Output tab) for one workspace.</summary>
public interface IWorkspaceTaskScheduler : IDisposable
{
    /// <summary>Establishes the token every run links against so disposing the scheduler (workspace tab closed, app shutting down) actually kills in-flight subprocesses - call once per workspace before the first RunNowAsync.</summary>
    void Start();

    /// <summary>Runs one task immediately (used by the sidebar's "Run" context-menu action) - a no-op if it's already running.</summary>
    Task RunNowAsync(TaskRef task, CancellationToken cancellationToken = default);

    bool IsRunning(string taskId);

    /// <summary>Ordered script layouts (see TaskDocument) for a task's current run - lets a viewer that opens the Output tab after a run has already started know which panels to show, and where to place them.</summary>
    IReadOnlyList<ScriptBlockLayout> GetScriptBlockLayouts(string taskId);

    /// <summary>Everything a specific block has reported so far in the task's current run.</summary>
    IReadOnlyList<string> GetScriptOutputSoFar(string taskId, string blockName);

    /// <summary>Non-null once that block has finished (success, failure, or stopped), even while sibling blocks in the same run are still going.</summary>
    ScriptBlockRunRecord? GetScriptBlockResult(string taskId, string blockName);

    /// <summary>
    /// Forcefully cancels the task's in-flight run (kills every block's underlying subprocess) - a no-op
    /// returning false if it isn't currently running. The run still completes normally afterwards from the
    /// caller's perspective: TaskRunCompleted fires with a record marked failed ("Stopped by user"), so
    /// history and any Output tab viewing it reflect the stop instead of just going silent.
    /// </summary>
    bool StopRun(string taskId);

    /// <summary>Raised the instant a run actually starts - before the first progress event, so a viewer knows immediately that a task neither running nor previously connected to has become live.</summary>
    event Action<TaskRef>? TaskRunStarted;

    event Action<TaskRunRecord>? TaskRunCompleted;

    /// <summary>Raised for each stdout/stderr line from any block of an actively-running task - (taskId, blockName, line).</summary>
    event Action<string, string, string>? ScriptTaskProgress;

    /// <summary>Raised the instant one block of a task's run finishes (success, failure, or stopped), independent of the overall run - see GetScriptBlockResult.</summary>
    event Action<string, ScriptBlockRunRecord>? ScriptBlockCompleted;
}

public interface ITaskSchedulerServiceFactory
{
    IWorkspaceTaskScheduler Create(string workspacePath);
}
