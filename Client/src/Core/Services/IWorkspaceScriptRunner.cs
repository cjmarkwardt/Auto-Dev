using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

/// <summary>A .cs file's identity for run-tracking purposes - Path is workspace-relative and doubles as the key every runner/history lookup uses (a runnable script's identity now that there's no central registry issuing GUIDs); Name is the display name (filename without extension), carried alongside since callers that only have Path (e.g. history enumeration) still need something to show.</summary>
public sealed record ScriptRef(string Path, string Name);

/// <summary>Scripts only ever run manually (via RunNowAsync) - this tracks/broadcasts the state of those runs across every viewer (sidebar rows, the Script tab) for one workspace. Only one script total ever runs at a time per workspace - see RunNowAsync.</summary>
public interface IWorkspaceScriptRunner : IDisposable
{
    /// <summary>Establishes the token every run links against so disposing the runner (workspace tab closed, app shutting down) actually kills an in-flight `dotnet run` subprocess - call once per workspace before the first RunNowAsync.</summary>
    void Start();

    /// <summary>Runs one .cs file immediately via `dotnet run --file` (used by the sidebar's "Run" context-menu action, or double-clicking the file) - a no-op if this same file is already running, or if any other .cs file in this workspace is: only one script total runs at a time per workspace.</summary>
    Task RunNowAsync(ScriptRef script, CancellationToken cancellationToken = default);

    bool IsRunning(string scriptId);

    /// <summary>The script's own live output as `dotnet run` streams it, if it's currently running - null once the run finishes (see ScriptRunCompleted for the final record instead). A viewer (the Script tab) binds to this directly rather than the runner re-buffering it separately.</summary>
    LiveScriptRun? GetLiveRun(string scriptId);

    /// <summary>
    /// Forcefully cancels the script's in-flight run (kills the underlying `dotnet run` subprocess) - a
    /// no-op returning false if it isn't currently running. The run still completes normally afterwards from
    /// the caller's perspective: ScriptRunCompleted fires with a record marked WasStopped, so history and
    /// any Script tab viewing it reflect the stop instead of just going silent.
    /// </summary>
    bool StopRun(string scriptId);

    /// <summary>Raised the instant a run actually starts, before `dotnet run` has even been launched - a viewer knows immediately that a script neither running nor previously connected to has become live.</summary>
    event Action<ScriptRef>? ScriptRunStarted;

    event Action<ScriptRunRecord>? ScriptRunCompleted;
}

public interface IScriptRunnerServiceFactory
{
    IWorkspaceScriptRunner Create(string workspacePath);
}
