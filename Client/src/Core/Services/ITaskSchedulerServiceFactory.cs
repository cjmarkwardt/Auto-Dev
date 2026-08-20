using AutoDev.Core.Models;
using Markwardt.TaskRunner;

namespace AutoDev.Core.Services;

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

    /// <summary>
    /// The task's own live Markwardt.TaskRunner.ScriptRunner instances for its current run, in declaration
    /// order - null if the task isn't currently running. Each one keeps reporting its own Status/LogText
    /// straight from the library as the run progresses (see ScriptRunner's own PropertyChanged), so a viewer
    /// (the Output tab) can bind to them directly instead of this scheduler re-buffering their output itself.
    /// </summary>
    IReadOnlyList<ScriptRunner>? GetLiveScripts(string taskId);

    /// <summary>
    /// Forcefully cancels the task's in-flight run (kills every script's underlying subprocess) - a no-op
    /// returning false if it isn't currently running. The run still completes normally afterwards from the
    /// caller's perspective: TaskRunCompleted fires with a record marked WasStopped, so history and any
    /// Output tab viewing it reflect the stop instead of just going silent.
    /// </summary>
    bool StopRun(string taskId);

    /// <summary>Raised the instant a run actually starts, before the .task file has even been read - a viewer knows immediately that a task neither running nor previously connected to has become live, though its scripts (see GetLiveScripts) may not be available for a moment yet, until the file is parsed (see TaskScriptsAvailable).</summary>
    event Action<TaskRef>? TaskRunStarted;

    /// <summary>Raised the instant the task's file has parsed successfully and GetLiveScripts is ready to return its scripts - fires strictly after TaskRunStarted for the same run, and never at all if the file fails to parse (TaskRunCompleted still fires for that case, with TaskRunRecord.ParseError set).</summary>
    event Action<TaskRef>? TaskScriptsAvailable;

    event Action<TaskRunRecord>? TaskRunCompleted;
}

public interface ITaskSchedulerServiceFactory
{
    IWorkspaceTaskScheduler Create(string workspacePath);
}
