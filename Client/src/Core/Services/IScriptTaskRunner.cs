namespace AutoDev.Core.Services;

public sealed record ScriptBlockResult(
    string Name,
    bool Success,
    string? ErrorMessage,
    string Output,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int? Row = null,
    int? Column = null);

public sealed record ScriptRunResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<ScriptBlockResult> Blocks,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record ScriptOutputLine(string BlockName, string Line);

/// <summary>Runs a task's scripts (see TaskDocument) locally in the workspace folder, all concurrently, no AI involved.</summary>
public interface IScriptTaskRunner
{
    /// <summary>
    /// onBlockCompleted fires the instant an individual block's process exits (success or failure) -
    /// independent of the overall run, which only completes once every block has (a long-running block like
    /// a dev server can still be going while a short one-shot block has already finished).
    /// </summary>
    Task<ScriptRunResult> RunAsync(
        string workspacePath,
        string scriptText,
        IProgress<ScriptOutputLine>? onLine = null,
        Action<ScriptBlockResult>? onBlockCompleted = null,
        CancellationToken cancellationToken = default);
}
