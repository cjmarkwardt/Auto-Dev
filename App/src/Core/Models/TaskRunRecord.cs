namespace AutoDev.Core.Models;

/// <summary>
/// One run of a .task file via IWorkspaceScriptRunner.RunTaskNowAsync - a group of .cs scripts run together,
/// batched by wait markers (see TaskFileParser). Each script still gets its own persisted ScriptRunRecord
/// (tagged with this run's FilePath via ScriptRunRecord.TaskId) for its individual output/exit code history;
/// this record just tracks the group's own identity, overall outcome, and which scripts it ran (in file order,
/// flattened across batches), so the Script tab can rebuild the task's sub-tabs from history after a restart.
/// </summary>
public sealed class TaskRunRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The .task file's path relative to the workspace root - its identity, mirroring ScriptRunRecord.FilePath.</summary>
    public required string FilePath { get; set; }

    /// <summary>The .task file's name without extension, captured at run time so history/Script-tab display never needs to re-derive it from FilePath.</summary>
    public required string FileName { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>True only when every batch ran to completion with every one of its scripts succeeding - false the moment any script fails or the task is stopped, which also skips every batch still to come.</summary>
    public bool Success { get; set; }

    /// <summary>True only for an explicit user Stop of the task - distinct from a genuine script failure, mirroring ScriptRunRecord.WasStopped.</summary>
    public bool WasStopped { get; set; }

    /// <summary>Every script this task ran, in file order (flattened across batches) - each one's own output/exit code lives in its own ScriptRunRecord history instead (see IWorkspaceMetadataStore.LoadScriptRunsAsync).</summary>
    public required List<ScriptRef> Scripts { get; set; }
}
