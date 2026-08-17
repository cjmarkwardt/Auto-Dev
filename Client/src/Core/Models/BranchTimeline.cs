namespace AutoDev.Core.Models;

public enum BranchTimelineEntryKind
{
    /// <summary>A node before the branch's own base commit, naming its parent branch - clicking it navigates the History tab's view to the parent branch (no git action).</summary>
    ParentLink,

    /// <summary>A real commit on this branch, from its base commit up to its tip. Clicking one checks it out (detaching HEAD there).</summary>
    Commit,

    /// <summary>A node for a branch that was created from this one - clicking it navigates the History tab's view to that child branch (no git action).</summary>
    ChildLink,

    /// <summary>A tag pointing at the commit immediately below it - its own node rather than riding along on the Commit entry, so it reads as a distinct marker on the timeline. Purely a label; not independently clickable (CommitHash is set only so a future feature could act on it, not currently read by anything).</summary>
    Tag,
}

/// <summary>
/// One row in the History tab's selected-branch timeline. A Commit entry with IsBase set is the exact commit
/// BranchConvention recognizes as this branch's base marker (see BranchConvention.FindBranchInfoByIdAsync) -
/// its Label reads "Base" rather than the marker's embedded branch name, since that name is already shown as
/// the timeline's own title and re-showing it here as if it were regular commit content would be redundant/
/// confusing. Any other base-commit-shaped commit reachable in this branch's own history (left behind by an
/// earlier Rename/Squash that amended a later commit instead of this same position - see
/// WorkspaceVersioningService.GetBranchTimelinePageAsync for exactly which ones those are) is dropped entirely
/// rather than shown as its own node - it's not real work, and the branch's current name already reflects the
/// latest one.
/// </summary>
public sealed record BranchTimelineEntry(
    BranchTimelineEntryKind Kind,
    string Label,
    DateTimeOffset? Date,
    string? CommitHash,
    string? LinkedBranchId,
    bool IsCurrentCommit = false,
    bool IsBase = false);

/// <summary>One page of a branch's timeline (see WorkspaceVersioningService.GetBranchTimelinePageAsync) - PageIndex/PageCount are 0-based/total, for the History tab's up/down pager.</summary>
public sealed record BranchTimelinePage(string BranchId, string BranchName, IReadOnlyList<BranchTimelineEntry> Entries, int PageIndex, int PageCount);

/// <summary>A single file's change within one commit - one row in the History tab's per-commit expanded changes tree.</summary>
public sealed record GitChange(string Path, GitChangeStatus Status);

public enum GitChangeStatus
{
    Added,
    Modified,
    Deleted,
    Renamed,
}

/// <summary>A file's content immediately before and after one commit - Before is null for a file the commit added (nothing to compare against), After is null for one it deleted. Powers the History tab's before/after read-only diff view.</summary>
public sealed record FileDiffContent(string? Before, string? After);
