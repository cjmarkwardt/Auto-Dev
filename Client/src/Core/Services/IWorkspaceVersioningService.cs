using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public enum BranchCreationOutcome
{
    Created,
    IdAlreadyExists,
}

public enum TagCreationOutcome
{
    Created,
    IdAlreadyExists,
}

/// <summary>
/// Business logic for the git-backed branch workflow, built on IGitService and BranchConvention. Deliberately
/// has no knowledge of Claude or any ViewModel - same separation as IWorkspaceTaskScheduler knowing nothing
/// about the Generate tab.
/// </summary>
public interface IWorkspaceVersioningService
{
    /// <summary>False both for a plain folder with no .git yet AND for an existing git work tree with no commits (e.g. a fresh `git clone` of an empty remote) - either way, EnsureRepoAsync routes to InitializeRepoAsync so both end up with the same main-branch-plus-base-commit convention.</summary>
    Task<bool> IsRepoInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>Silent, no prompts: git init (a safe no-op if .git already exists, e.g. a cloned repo), ensures AutoDev's own local-only bookkeeping (.autodev/local/) is excluded via .git/info/exclude, makes the initial commit (staging anything already in the folder) with message "Main ~[>main]" (public - see BranchConvention), renames the resulting branch to "main", then pushes it upstream if "origin" is already configured (a no-op otherwise).</summary>
    Task InitializeRepoAsync(CancellationToken cancellationToken = default);

    /// <summary>Derived fresh from git state every call - see GitTarget. Null if the repo isn't initialized yet.</summary>
    Task<GitTarget?> GetCurrentTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>Every Version sidebar button's visibility, computed in one consolidated pass.</summary>
    Task<VersionActionState> GetActionStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Configures (or repoints) the "origin" remote - callable any time from the Version section, not just at repo creation.</summary>
    Task ConfigureRemoteAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>The current "origin" remote URL, or null if none is configured.</summary>
    Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>Idempotently ensures ".autodev/local/" is listed in .git/info/exclude - see the old doc comment this carries forward: a per-clone, local-only ignore rule that keeps AutoDev's own bookkeeping out of git status without the user ever seeing it.</summary>
    Task EnsureLocalGitExcludeAsync(CancellationToken cancellationToken = default);

    Task<bool> HasUncommittedChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches (+prunes deleted remote branches), then hard-resets every local branch OTHER than the currently checked-out one to match its remote-tracking counterpart wherever they differ. The checked-out branch is never touched by this reset, so local work in progress is never silently overwritten. Best-effort - a missing/unreachable remote is silently ignored, same tolerance as every other remote call here.</summary>
    Task SyncWithRemoteAsync(CancellationToken cancellationToken = default);

    // --- Buttons ---

    Task<BranchCreationOutcome> CreateBranchAsync(string name, string id, bool isPublic, CancellationToken cancellationToken = default);

    /// <summary>Creates an annotated tag at HEAD (the currently checked-out spot) and pushes it - `id` is the actual git ref name, `fullName` becomes the tag's own message and is what the History tab's timeline shows instead of `id` (see IGitService.CreateAnnotatedTagAsync/GetTagsByCommitAsync).</summary>
    Task<TagCreationOutcome> CreateTagAsync(string id, string fullName, CancellationToken cancellationToken = default);

    /// <summary>Discards all pending changes (`git reset --hard` + `git clean -fd`). Destructive; callers confirm with the user first.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>Combines all pending changes and commits after the base commit into a new base commit that replaces the old one, keeping the same message.</summary>
    Task SquashAsync(CancellationToken cancellationToken = default);

    /// <summary>Squashes first (unless the branch is public), then rebases onto the parent branch's tip.</summary>
    Task<RebaseOutcome> RebaseAsync(CancellationToken cancellationToken = default);

    Task<RebaseOutcome> ContinueRebaseAsync(CancellationToken cancellationToken = default);

    Task AbortRebaseAsync(CancellationToken cancellationToken = default);

    Task<bool> HasConflictsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetConflictedFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Second half of Merge, called once a preceding RebaseAsync's outcome is Succeeded: checks out the parent branch, fast-forward merges this branch into it, then deletes this branch.</summary>
    Task FinishMergeAsync(CancellationToken cancellationToken = default);

    /// <summary>Same mechanics as SquashAsync (unconditional, not gated on dirty state), but the new base commit carries `newName` instead of the branch's current name.</summary>
    Task RenameAsync(string newName, CancellationToken cancellationToken = default);

    Task CommitAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>Pushes the currently checked-out branch, if any - used by VersionSectionViewModel once a Rebase/Merge's AI-assisted conflict-resolution loop finally lands on RebaseOutcome.Succeeded (SquashAsync/RenameAsync/CommitAsync/CreateBranchAsync push inline themselves, since they're single synchronous operations with no external continuation step in between).</summary>
    Task PushCurrentBranchAsync(bool force, CancellationToken cancellationToken = default);

    /// <summary>Detaches HEAD at an arbitrary commit - used by the History tab's "click a commit to check it out" action. Caller is responsible for confirming discard of pending changes first.</summary>
    Task CheckoutRefAsync(string refName, CancellationToken cancellationToken = default);

    // --- History tab ---

    /// <summary>All local branches, current branch first.</summary>
    Task<IReadOnlyList<BranchSummary>> ListAllBranchesAsync(CancellationToken cancellationToken = default);

    /// <summary>One 100-entries-at-a-time page of `branchId`'s own timeline (newest first), for the History tab's up/down pager - see BranchTimelinePage. Null if `branchId` doesn't exist.</summary>
    Task<BranchTimelinePage?> GetBranchTimelinePageAsync(string branchId, int pageIndex, int pageSize = 100, CancellationToken cancellationToken = default);

    /// <summary>Every file `commitHash` changed - populates a timeline commit's expanded changes tree (see IGitService.GetCommitChangesAsync).</summary>
    Task<IReadOnlyList<GitChange>> GetCommitChangesAsync(string commitHash, CancellationToken cancellationToken = default);

    /// <summary>`relativePath`'s content immediately before and after `commitHash` - powers the History tab's "open this change" read-only diff view (see FileDiffContent).</summary>
    Task<FileDiffContent> GetFileDiffAsync(string commitHash, string relativePath, CancellationToken cancellationToken = default);

    // --- Files section Changes Mode ---

    /// <summary>Every path with a pending change right now, workspace-wide (see IGitService.GetWorkingTreeChangesAsync) - populates the Files section's Changes Mode tree.</summary>
    Task<IReadOnlyList<GitChange>> GetWorkingTreeChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>`relativePath`'s content at HEAD versus right now on disk - the working-tree equivalent of GetFileDiffAsync, for a change that hasn't been committed yet. Before is null for a file that's new (untracked or freshly staged); After is null for one that's been deleted from disk.</summary>
    Task<FileDiffContent> GetWorkingTreeFileDiffAsync(string relativePath, CancellationToken cancellationToken = default);
}

public interface IVersioningServiceFactory
{
    IWorkspaceVersioningService Create(string workspacePath);
}
