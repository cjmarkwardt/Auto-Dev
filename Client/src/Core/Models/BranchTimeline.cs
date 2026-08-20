namespace AutoDev.Core.Models;

public enum BranchTimelineEntryKind
{
    /// <summary>A real commit on this branch. Clicking one expands its changed-files view in place; right-clicking offers Checkout/New Branch/New Tag.</summary>
    Commit,

    /// <summary>A tag pointing at the commit immediately below it - its own node rather than riding along on the Commit entry, so it reads as a distinct marker on the timeline. TagName is the tag's actual git ref name (used for Delete Tag); Label is its display text (an annotated tag's own message, if it has one, otherwise TagName itself - see IGitService.GetTagsByCommitAsync).</summary>
    Tag,
}

/// <summary>One row in the History tab's selected-branch timeline.</summary>
public sealed record BranchTimelineEntry(
    BranchTimelineEntryKind Kind,
    string Label,
    DateTimeOffset? Date,
    string? CommitHash,
    bool IsCurrentCommit = false,
    string? TagName = null);

/// <summary>One page of a branch's timeline (see WorkspaceVersioningService.GetBranchTimelinePageAsync) - PageIndex/PageCount are 0-based/total, for the History tab's up/down pager.</summary>
public sealed record BranchTimelinePage(string BranchName, IReadOnlyList<BranchTimelineEntry> Entries, int PageIndex, int PageCount);

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
