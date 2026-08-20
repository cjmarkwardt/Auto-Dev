using Markwardt.TaskRunner;

namespace AutoDev.Core.Models;

/// <summary>One script's own result from a task run, captured straight from the Markwardt.TaskRunner library's own <see cref="ScriptRunner"/> once it finishes - Log is exactly its LogText, including the automatic "&gt; instruction" announcements and any "Error: ..." line the library itself writes on failure, so historical replay renders identically to how it looked live.</summary>
public sealed record ScriptRunRecord(string Name, ScriptStatus Status, string Log);

/// <summary>One run of a .task file - see IWorkspaceTaskScheduler/WorkspaceTaskSchedulerService for how this gets built from a Markwardt.TaskRunner.TaskEngine, and IWorkspaceMetadataStore for how it's persisted.</summary>
public sealed class TaskRunRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The .task file's path relative to the workspace root - its identity now that tasks are plain files rather than entries in a central registry. See IWorkspaceMetadataStore for how this maps to an on-disk history folder.</summary>
    public required string TaskPath { get; set; }

    /// <summary>The .task file's name without extension, captured at run time so history/Output-tab display never needs to re-derive it from TaskPath (e.g. after the file has since been renamed or deleted).</summary>
    public required string TaskName { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>True only when every script in the run reached Completed - false for any script left Failed, and always false when the run was stopped or the file failed to parse.</summary>
    public bool Success { get; set; }

    /// <summary>True only for an explicit user Stop - distinct from a genuine failure, so the Output tab can show "Stopped" instead of "Failed". A Markwardt.TaskRunner.ScriptRunner itself has no notion of this (a cancelled script just ends up Failed, same as any other failure) - this is purely AutoDev's own policy, tracked by WorkspaceTaskSchedulerService around the cancellation it already requests.</summary>
    public bool WasStopped { get; set; }

    /// <summary>Set only when the .task file itself failed to parse (see Markwardt.TaskRunner.TaskParseException) - Scripts is empty in that case, since the document never loaded far enough to know what scripts it declares.</summary>
    public string? ParseError { get; set; }

    /// <summary>One entry per script the document declared, in declaration order, so the Output tab can show each script's own output panel from history exactly as it appeared live. Empty only when ParseError is set.</summary>
    public List<ScriptRunRecord> Scripts { get; set; } = [];
}
