namespace AutoDev.Core.Models;

/// <summary>The checked-out branch (if any) and HEAD's own commit hash right before a mutating git action starts - see IWorkspaceVersioningService.CaptureSnapshotAsync/RevertToSnapshotAsync, which the busy overlay's Cancel button uses to undo whatever the action had done so far.</summary>
public sealed record GitActionSnapshot(string? Branch, string CommitHash);
