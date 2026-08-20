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
/// Business logic for the git-backed branch/tag workflow, built directly on IGitService with no naming
/// convention or invented semantics layered on top - a branch or tag's only identity is its own literal git
/// ref name. Deliberately has no knowledge of Claude or any ViewModel - same separation as
/// IWorkspaceTaskScheduler knowing nothing about the Generate tab.
/// </summary>
public interface IWorkspaceVersioningService
{
    /// <summary>False both for a plain folder with no .git yet AND for an existing git work tree with no commits (e.g. a fresh `git clone` of an empty remote) - either way, EnsureRepoAsync routes to InitializeRepoAsync so both end up with the same initial "main" branch.</summary>
    Task<bool> IsRepoInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>Silent, no prompts: git init (a safe no-op if .git already exists, e.g. a cloned repo), ensures AutoDev's own local-only bookkeeping (.autodev/local/) is excluded via .git/info/exclude, makes a genuinely empty "Initial commit" (nothing staged, even if the folder already has content - see IGitService.CommitEmptyAsync), renames the resulting branch to "main", then pushes it upstream if "origin" is already configured (a no-op otherwise).</summary>
    Task InitializeRepoAsync(CancellationToken cancellationToken = default);

    /// <summary>Derived fresh from git state every call - see GitTarget. Null if the repo isn't initialized yet.</summary>
    Task<GitTarget?> GetCurrentTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>Configures (or repoints) the "origin" remote - callable any time, not just at repo creation.</summary>
    Task ConfigureRemoteAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>The current "origin" remote URL, or null if none is configured.</summary>
    Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>Idempotently ensures ".autodev/local/" is listed in .git/info/exclude - see the old doc comment this carries forward: a per-clone, local-only ignore rule that keeps AutoDev's own bookkeeping out of git status without the user ever seeing it.</summary>
    Task EnsureLocalGitExcludeAsync(CancellationToken cancellationToken = default);

    Task<bool> HasUncommittedChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches (+prunes deleted remote branches), then hard-resets every local branch OTHER than the currently checked-out one to match its remote-tracking counterpart wherever they differ. The checked-out branch is never touched by this reset, so local work in progress is never silently overwritten. Best-effort - a missing/unreachable remote is silently ignored, same tolerance as every other remote call here.</summary>
    Task SyncWithRemoteAsync(CancellationToken cancellationToken = default);

    /// <summary>The checked-out branch (if any) and HEAD's own commit hash, right before a mutating busy action starts - see RevertToSnapshotAsync, which the busy overlay's Cancel button uses to undo whatever the action had done so far.</summary>
    Task<GitActionSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Best-effort undo back to `snapshot`: aborts an in-progress rebase/merge if there is one, checks out `snapshot.Branch` again if a different branch ended up checked out, then hard-resets it to `snapshot.CommitHash` and discards any pending changes. Doesn't know or care which specific action it's undoing - every mutating action's own effects reduce to "the checked-out branch moved and/or its tip advanced", which this reverses generically.</summary>
    Task RevertToSnapshotAsync(GitActionSnapshot snapshot, CancellationToken cancellationToken = default);

    // --- History tab actions ---

    /// <summary>Creates a new branch named `name` starting at `fromRef` (a branch/tag/commit ref) and checks it out.</summary>
    Task<BranchCreationOutcome> CreateBranchAsync(string name, string fromRef, CancellationToken cancellationToken = default);

    /// <summary>Creates an annotated tag at `atRef` and pushes it - `id` is the actual git ref name, `fullName` becomes the tag's own message and is what the History tab's timeline shows instead of `id` (see IGitService.CreateAnnotatedTagAsync/GetTagsByCommitAsync).</summary>
    Task<TagCreationOutcome> CreateTagAsync(string id, string fullName, string atRef, CancellationToken cancellationToken = default);

    /// <summary>`git branch -D` - callers confirm with the user first.</summary>
    Task DeleteBranchAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>`git tag -d` (plus removing it from the remote, if any) - callers confirm with the user first.</summary>
    Task DeleteTagAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Discards all pending changes (`git reset --hard` + `git clean -fd`). Destructive; callers confirm with the user first.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>Rebases the currently checked-out branch onto `ontoRef`.</summary>
    Task<GitOperationOutcome> RebaseAsync(string ontoRef, CancellationToken cancellationToken = default);

    Task<GitOperationOutcome> ContinueRebaseAsync(CancellationToken cancellationToken = default);

    Task AbortRebaseAsync(CancellationToken cancellationToken = default);

    /// <summary>Merges `sourceBranch` into the currently checked-out branch - a real merge commit if it can't fast-forward.</summary>
    Task<GitOperationOutcome> MergeAsync(string sourceBranch, CancellationToken cancellationToken = default);

    Task<GitOperationOutcome> ContinueMergeAsync(CancellationToken cancellationToken = default);

    Task AbortMergeAsync(CancellationToken cancellationToken = default);

    Task<bool> HasConflictsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetConflictedFilesAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>Pushes the currently checked-out branch, if any - used once a Rebase/Merge's AI-assisted conflict-resolution loop finally lands on a Succeeded outcome (CreateBranchAsync/CreateTagAsync/CommitAsync push inline themselves, since they're single synchronous operations with no external continuation step in between).</summary>
    Task PushCurrentBranchAsync(bool force, CancellationToken cancellationToken = default);

    /// <summary>Checks out a branch, tag, or arbitrary commit - attaches HEAD to it if it's a branch name, otherwise detaches at that exact spot. Caller is responsible for confirming discard of pending changes first.</summary>
    Task CheckoutRefAsync(string refName, CancellationToken cancellationToken = default);

    /// <summary>Local branches that make sense as a Squash/Rebase base for the currently checked-out branch - every other local branch except the current one and any that's already a git ancestor of it (squashing back to, or rebasing onto, an ancestor would be a no-op/degenerate). Empty while HEAD is detached.</summary>
    Task<IReadOnlyList<string>> GetEligibleBaseBranchesAsync(CancellationToken cancellationToken = default);

    /// <summary>The subject of the first commit unique to the current branch since diverging from `baseBranch` (i.e. right after their merge-base) - the Squash/Rebase dialogs' own default commit message. Empty if the current branch has no commits of its own since that point.</summary>
    Task<string> GetDefaultSquashMessageAsync(string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>Collapses every commit unique to the current branch since diverging from `baseBranch` into one (see IGitService.SquashSinceAsync), then force-pushes.</summary>
    Task SquashAsync(string baseBranch, string message, CancellationToken cancellationToken = default);

    /// <summary>Rebases the current branch onto `ontoBranch`, always squashing the current branch's own commits since diverging from `ontoBranch` first (see SquashAsync, minus the intermediate push) so only that single commit ever gets replayed - a rebase can't offer a meaningful per-commit conflict-resolution loop otherwise, since AI conflict resolution (see VersionSectionViewModel.ResolveConflictsAsync) only gets one shot at the whole diff, not one per original commit.</summary>
    Task<GitOperationOutcome> RebaseWithSquashAsync(string ontoBranch, string squashMessage, CancellationToken cancellationToken = default);

    /// <summary>Local branches current can be fast-forward merged onto - every other local branch that's already a git ancestor of current (the opposite filter from GetEligibleBaseBranchesAsync: only a branch current is strictly ahead of can be fast-forwarded to match it). Empty while HEAD is detached.</summary>
    Task<IReadOnlyList<string>> GetEligibleMergeTargetBranchesAsync(CancellationToken cancellationToken = default);

    /// <summary>Fast-forwards `targetBranch` to match the current branch - false if current isn't actually based on `targetBranch`'s own head (the fast-forward precondition). Squashes the current branch's own commits since diverging from `targetBranch` into one first, but only if there's more than one - a single commit needs no squashing before being fast-forwarded onto.</summary>
    Task<bool> FastForwardMergeAsync(string targetBranch, string? squashMessage, CancellationToken cancellationToken = default);

    // --- History tab ---

    /// <summary>Every local branch, current branch first then alphabetical.</summary>
    Task<IReadOnlyList<BranchSummary>> ListAllBranchesAsync(CancellationToken cancellationToken = default);

    /// <summary>One 100-entries-at-a-time page of `branchName`'s own commit/tag history (newest first), for the History tab's up/down pager - see BranchTimelinePage. Null if `branchName` doesn't exist.</summary>
    Task<BranchTimelinePage?> GetBranchTimelinePageAsync(string branchName, int pageIndex, int pageSize = 100, CancellationToken cancellationToken = default);

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
