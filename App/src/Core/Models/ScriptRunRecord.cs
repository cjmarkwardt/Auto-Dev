namespace AutoDev.Core.Models;

/// <summary>One run of a .cs file via `dotnet run --file` - see IWorkspaceScriptRunner/WorkspaceScriptRunnerService for how this gets built, and IWorkspaceMetadataStore for how it's persisted.</summary>
public sealed class ScriptRunRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The .cs file's path relative to the workspace root - its identity now that runnable scripts are plain files rather than entries in a central registry. See IWorkspaceMetadataStore for how this maps to an on-disk history folder.</summary>
    public required string FilePath { get; set; }

    /// <summary>The .cs file's name without extension, captured at run time so history/Output-tab display never needs to re-derive it from FilePath (e.g. after the file has since been renamed or deleted).</summary>
    public required string FileName { get; set; }

    /// <summary>The owning .task file's own workspace-relative path, if this run happened as one of its scripts (see IWorkspaceScriptRunner.RunTaskNowAsync) - null for a standalone run. Excludes the script from IWorkspaceMetadataStore.LoadRunScriptRefsAsync's own top-level seeding, since a task's children are only ever reached through their task's own dropdown entry (see Docs/Components/RunningScripts.md), not a separate top-level one.</summary>
    public string? TaskId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>True only when `dotnet run` exited with code 0 - always false when the run was stopped.</summary>
    public bool Success { get; set; }

    /// <summary>True only for an explicit user Stop - distinct from a genuine failure, so the Script tab can show "Stopped" instead of "Failed". The underlying process itself has no notion of this (a killed process just has no exit code) - this is purely AutoDev's own policy, tracked by WorkspaceScriptRunnerService around the cancellation it already requests.</summary>
    public bool WasStopped { get; set; }

    /// <summary>The process's exit code - null if the run was stopped before it exited on its own.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Every line `dotnet run` wrote to stdout/stderr, interleaved in the order it arrived - matches exactly what was shown live in the Script tab.</summary>
    public string Output { get; set; } = "";
}
