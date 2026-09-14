namespace AutoDev.Core.Models;

/// <summary>A .cs file's identity for run-tracking purposes - Path is workspace-relative and doubles as the key every runner/history lookup uses (a runnable script's identity now that there's no central registry issuing GUIDs); Name is the display name (filename without extension), carried alongside since callers that only have Path (e.g. history enumeration) still need something to show.</summary>
/// <param name="TaskId">The owning .task file's own workspace-relative path, if this script is running as one of a task's own scripts (see IWorkspaceScriptRunner.RunTaskNowAsync) - null for a standalone run.</param>
public sealed record ScriptRef(string Path, string Name, string? TaskId = null);

/// <summary>A .task file's identity for run-tracking purposes - mirrors ScriptRef, Path being workspace-relative and doubling as the key every runner/history lookup uses.</summary>
public sealed record TaskRef(string Path, string Name);
