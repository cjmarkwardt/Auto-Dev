namespace AutoDev.Core.Models;

/// <summary>One task script's own result - see TaskDocument. Every script always finishes with an explicit Success, even a stopped one (recorded as false, WasStopped true, ErrorMessage "Stopped by user."). Row/Column mirror the script's own optional Output tab grid placement, so historical replay positions its panel the same way a live run did.</summary>
public sealed record ScriptBlockRunRecord(string Name, bool Success, bool WasStopped, string? ErrorMessage, string Output, int? Row = null, int? Column = null);

public sealed class TaskRunRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The .task file's path relative to the workspace root - its identity now that tasks are plain files rather than entries in a central registry. See IWorkspaceMetadataStore for how this maps to an on-disk history folder.</summary>
    public required string TaskPath { get; set; }

    /// <summary>The .task file's name without extension, captured at run time so history/Output-tab display never needs to re-derive it from TaskPath (e.g. after the file has since been renamed or deleted).</summary>
    public required string TaskName { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>True only for an explicit user Stop - distinct from a genuine failure, so the Output tab can show "Stopped" instead of "Failed".</summary>
    public bool WasStopped { get; set; }

    public required string OutputSummary { get; set; }

    /// <summary>One entry per concurrently-run script (see TaskDocument), in declared order, so the Output tab can show each script's own output panel from history exactly as it appeared live. Null/empty only when the run never got as far as any script (e.g. a script content parse error).</summary>
    public List<ScriptBlockRunRecord>? ScriptBlocks { get; set; }
}
