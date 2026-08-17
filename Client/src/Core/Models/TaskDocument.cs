namespace AutoDev.Core.Models;

public enum ScriptInstruction
{
    Wait,
    Run,
    Move,
    Rename,
    Create,
    Delete,
    Purge,
    SetContext,

    /// <summary>Outputs Command's text as-is (see TaskFileParser's "print") - unlike Run, never shells out; ScriptTaskRunner's Describe returns the text itself with no "$ ..." prefix, so it reads exactly like an echoed line, and there's nothing further for ExecuteAsync to actually do (falls to its default no-op case).</summary>
    Print,
}

public enum CreateEntryKind
{
    File,
    Folder,
}

public sealed class TaskVariable
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// One step in a script - Instruction selects which fields are meaningful (see TaskDocumentReader for
/// %VAR% substitution and TaskScriptRunner for execution); every field is present on every command
/// regardless of instruction so the format needs no JSON polymorphism, unused ones just sit at their default.
/// </summary>
public sealed class TaskCommand
{
    public ScriptInstruction Instruction { get; set; } = ScriptInstruction.Run;

    /// <summary>Wait.</summary>
    public double Seconds { get; set; }

    /// <summary>Run/Print.</summary>
    public string Command { get; set; } = "";

    /// <summary>Move/Rename/Create/Delete/Purge.</summary>
    public string Target { get; set; } = "";

    /// <summary>Move.</summary>
    public string Destination { get; set; } = "";
    public bool Copy { get; set; }

    /// <summary>Move (delete an existing entry at the new path first) and Create (delete an existing Target first) - same field, same meaning, reused rather than duplicated.</summary>
    public bool Overwrite { get; set; }

    /// <summary>Rename's new leaf name (same parent directory as Target).</summary>
    public string Name { get; set; } = "";

    /// <summary>Create.</summary>
    public CreateEntryKind EntryKind { get; set; } = CreateEntryKind.File;
    public bool Conditional { get; set; }
    public string Content { get; set; } = "";

    /// <summary>SetContext - like a shell "cd", resolved relative to whatever the current directory already is (see ScriptTaskRunner's currentDirectory), so a later "../" walks back up from there rather than from the workspace root.</summary>
    public string Path { get; set; } = "";
}

/// <summary>Replaces the old free-text "block" - a named, independently-run set of sequential instructions. Row/Column (0-based; set via the script's own "output" command - see TaskFileParser) optionally place its live output panel at a specific Output tab grid cell (see ScriptBlockGridLayout) instead of auto-arranging.</summary>
public sealed class TaskScript
{
    public string Name { get; set; } = "";
    public int? Row { get; set; }
    public int? Column { get; set; }
    public List<TaskCommand> Commands { get; set; } = [];
}

/// <summary>A task's structured content - see TaskFileParser for how a .task file's own text turns into this, TaskDocumentReader for %VAR% resolution, and ScriptTaskRunner for execution. The .task file itself (wherever it lives in the workspace) is the sole source of truth - this is just its parsed, in-memory form.</summary>
public sealed class TaskDocument
{
    public List<TaskVariable> Variables { get; set; } = [];
    public List<TaskScript> Scripts { get; set; } = [];
}
