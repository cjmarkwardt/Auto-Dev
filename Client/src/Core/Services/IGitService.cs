using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public enum RebaseOutcome
{
    Succeeded,
    Conflicts,
    Failed,
}

/// <summary>A file tree entry's git status, coarsest-grained first to last (see GitService.GetStatusAsync for exactly how a path is classified) - drives the Files section's status colors.</summary>
public enum GitFileStatus
{
    /// <summary>Tracked with no pending changes - git status has nothing at all to report for this path.</summary>
    Unmodified,

    /// <summary>Untracked (new, not yet added) or staged-new - not part of any commit yet.</summary>
    Added,

    /// <summary>Tracked with some kind of pending change (modified, deleted, renamed, ...).</summary>
    Modified,

    /// <summary>Excluded by .gitignore (or any other rule git would apply), with no tracked content underneath - see GetStatusAsync for why a directory needs more than just "does status report it" to tell this apart from Unmodified.</summary>
    Ignored,
}

/// <summary>One commit as needed for building the History tab's timeline - Hash, single-line subject (%s), and commit date.</summary>
public sealed record GitCommit(string Hash, string Subject, DateTimeOffset Date);

/// <summary>
/// Thin, safe git wrapper backing the version/release/feature workflow (see IWorkspaceVersioningService,
/// which owns all the actual branch/tag naming conventions and business logic - this interface just runs
/// git commands and parses their output). Grows incrementally as later phases of that workflow need more
/// git primitives, rather than declaring the whole eventual surface up front.
/// </summary>
public interface IGitService
{
    Task<bool> IsRepoAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Whether HEAD currently resolves to a real commit - false for a freshly `git init`'d repo, or one cloned from an empty remote, whose branch still exists only as an "unborn" ref name with nothing committed to it yet. Checks the exit code of `git rev-parse --verify --quiet HEAD` rather than RevParseAsync's output, since plain `rev-parse HEAD` prints the literal text "HEAD" back to stdout (not empty) when it can't resolve it.</summary>
    Task<bool> HasCommitsAsync(string workspacePath, CancellationToken cancellationToken = default);

    Task InitAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Clones `url` into a new `destinationName` subfolder of `parentDirectory` (there's no existing workspace to run "in" yet, unlike every other method here). False on failure (bad URL, auth, network).</summary>
    Task<bool> CloneAsync(string parentDirectory, string url, string destinationName, CancellationToken cancellationToken = default);

    /// <summary>Stages every change in the working tree and commits it.</summary>
    Task CommitAsync(string workspacePath, string message, bool allowEmpty = false, CancellationToken cancellationToken = default);

    /// <summary>Renames the currently checked-out branch (used once, right after the initial commit, to become the first version branch).</summary>
    Task RenameCurrentBranchAsync(string workspacePath, string newName, CancellationToken cancellationToken = default);

    Task CheckoutAsync(string workspacePath, string refName, CancellationToken cancellationToken = default);

    /// <summary>The current branch name, or null if HEAD is detached (e.g. a release tag is checked out).</summary>
    Task<string?> GetCurrentBranchAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>The tag exactly at HEAD, or null if none (only meaningful/checked while detached).</summary>
    Task<string?> GetExactTagAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// A single file or directory's git status, for the Files section's per-row status color. Not part of the
    /// version/release/feature workflow itself. Unmodified (not an error) if the workspace isn't a git repo
    /// at all.
    /// </summary>
    Task<GitFileStatus> GetStatusAsync(string workspacePath, string path, CancellationToken cancellationToken = default);

    /// <summary>Bulk equivalent of IsIgnoredAsync - one `git check-ignore --stdin` call checks every path in `paths` at once instead of one subprocess per file, returning just the ignored subset. Used to filter file search results rather than merely dim them. Empty if the workspace isn't a git repo, or `paths` is empty.</summary>
    Task<IReadOnlySet<string>> GetIgnoredPathsAsync(string workspacePath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListTagsAsync(string workspacePath, string prefix, CancellationToken cancellationToken = default);

    /// <summary>Local branches plus remote-tracking ones (deduped to plain names) - so branches pushed from elsewhere show up even before this clone has a local copy. See EnsureLocalBranchAsync.</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(string workspacePath, string prefix, CancellationToken cancellationToken = default);

    /// <summary>Creates a local branch tracking `origin/{branchName}` if no local branch of that name exists yet - lets code that discovered a branch only via ListBranchesAsync's remote-tracking half operate on it (log, checkout, etc.) exactly like any other local branch. No-op if the local branch already exists.</summary>
    Task EnsureLocalBranchAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default);

    Task CreateBranchAsync(string workspacePath, string branchName, string fromRef, CancellationToken cancellationToken = default);

    Task DeleteBranchAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default);

    /// <summary>Whether a local branch named `branchName` exists.</summary>
    Task<bool> BranchExistsAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default);

    /// <summary>Whether a tag named `tagName` exists.</summary>
    Task<bool> TagExistsAsync(string workspacePath, string tagName, CancellationToken cancellationToken = default);

    /// <summary>`git reset --soft <ref>` - moves HEAD/index to `ref` without touching the working tree, so everything between `ref` and the old HEAD (plus real pending changes) ends up staged. Foundation for Squash/Rename.</summary>
    Task ResetSoftAsync(string workspacePath, string refName, CancellationToken cancellationToken = default);

    /// <summary>Stages everything (`git add -A`, so new untracked files are swept in too) then `git commit --amend`, replacing the commit currently at HEAD with one containing `message` and whatever is now staged. Used after ResetSoftAsync for Squash/Rename - amending (rather than resetting to the base commit's parent and recommitting) works even when the base commit is the repo root, with no parent to resolve.</summary>
    Task AmendCommitAsync(string workspacePath, string message, bool allowEmpty, CancellationToken cancellationToken = default);

    /// <summary>`git commit --allow-empty -m message`, deliberately with no `git add -A` first. Used only for a branch's base-commit marker (see BranchConvention) - must not sweep pending changes on the source ref into the marker, since `git branch`/`checkout` never touch the working tree and a change valid on the source ref is still valid on the new branch's identical tree.</summary>
    Task CommitEmptyAsync(string workspacePath, string message, CancellationToken cancellationToken = default);

    /// <summary>`git merge --ff-only branchName` into the current HEAD. False if it can't fast-forward.</summary>
    Task<bool> FastForwardMergeAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default);

    /// <summary>Moves a local branch's ref directly (`git branch -f branchName targetRef`) without checking it out or touching the working tree - used by background remote sync to keep a non-checked-out branch mirroring its remote counterpart.</summary>
    Task ForceUpdateBranchRefAsync(string workspacePath, string branchName, string targetRef, CancellationToken cancellationToken = default);

    /// <summary>The single newest commit reachable from `refName` whose message matches `extendedRegexPattern` (POSIX extended regex, as accepted by `git log --extended-regexp --grep`), or null if none matches - lets a caller find "the nearest commit of a given shape" (e.g. a BranchConvention base-commit marker) via git's own commit walk with early exit, instead of pulling full history into this process to scan it.</summary>
    Task<GitCommit?> FindFirstCommitMatchingAsync(string workspacePath, string refName, string extendedRegexPattern, CancellationToken cancellationToken = default);

    /// <summary>Full commit message body (used to recover a feature's verbatim summary from its root commit, and a merged feature's summary from its squash-merge commit).</summary>
    Task<string> GetCommitMessageAsync(string workspacePath, string commitRef, CancellationToken cancellationToken = default);

    /// <summary>True if `ancestorRef` is reachable from `descendantRef` - i.e. descendantRef already contains everything on ancestorRef.</summary>
    Task<bool> IsAncestorAsync(string workspacePath, string ancestorRef, string descendantRef, CancellationToken cancellationToken = default);

    Task<RebaseOutcome> RebaseOntoAsync(string workspacePath, string ontoRef, CancellationToken cancellationToken = default);

    Task<RebaseOutcome> RebaseContinueAsync(string workspacePath, CancellationToken cancellationToken = default);

    Task RebaseAbortAsync(string workspacePath, CancellationToken cancellationToken = default);

    Task<bool> HasConflictsAsync(string workspacePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetConflictedFilesAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Stages featureBranch's changes (squashed) into the working tree without committing - caller commits with its own message.</summary>
    Task SquashMergeAsync(string workspacePath, string featureBranch, CancellationToken cancellationToken = default);

    Task<string?> GetRemoteUrlAsync(string workspacePath, CancellationToken cancellationToken = default);

    Task<string> RevParseAsync(string workspacePath, string refName, CancellationToken cancellationToken = default);

    Task<DateTimeOffset> GetCommitDateAsync(string workspacePath, string refName, CancellationToken cancellationToken = default);

    /// <summary>Full history of `refName`, oldest first - used to build the History tab's timeline (releases + merged features, in commit order).</summary>
    Task<IReadOnlyList<GitCommit>> LogAsync(string workspacePath, string refName, CancellationToken cancellationToken = default);

    /// <summary>Every tag in the repo, grouped by the commit it ultimately points at (an annotated tag is dereferenced to the commit it tags, not left as its own tag object) - used to show tag badges on the History tab's timeline without one subprocess call per commit. Each entry is the tag's display name: an annotated tag's own message (its "full name" - see CreateAnnotatedTagAsync) if it has one, otherwise its short ref name.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetTagsByCommitAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Creates an annotated tag (`git tag -a`) at HEAD - `id` is the actual git ref name (short, unique, ref-safe), `fullName` becomes the tag's own annotation message and is what GetTagsByCommitAsync shows in the History tab's timeline instead of `id`.</summary>
    Task CreateAnnotatedTagAsync(string workspacePath, string id, string fullName, CancellationToken cancellationToken = default);

    /// <summary>Every file `commitHash` changed relative to its first parent (`--root` makes this also work for a parentless root commit, diffing against the empty tree) - populates the History tab's per-commit expanded changes tree.</summary>
    Task<IReadOnlyList<GitChange>> GetCommitChangesAsync(string workspacePath, string commitHash, CancellationToken cancellationToken = default);

    /// <summary>`relativePath`'s exact content as of `commitHash` (`git show {commitHash}:{relativePath}`) - null if that path didn't exist in the tree at that commit (a file just added, or already deleted, there). Powers the History tab's before/after file diff view.</summary>
    Task<string?> GetFileContentAtCommitAsync(string workspacePath, string commitHash, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Every path with a pending change right now - staged or not, tracked or brand new (`git status --porcelain -z`) - powers the Files section's Changes Mode. Like GetCommitChangesAsync, a rename is reported only by its new path.</summary>
    Task<IReadOnlyList<GitChange>> GetWorkingTreeChangesAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Commits unique to branchRef relative to baseRef (`git log baseRef..branchRef`), oldest first - a feature's own commit history, distinct from the shared history it branched from.</summary>
    Task<IReadOnlyList<GitCommit>> GetCommitsSinceAsync(string workspacePath, string baseRef, string branchRef, CancellationToken cancellationToken = default);

    /// <summary>True if the working tree has any staged or unstaged changes (`git status --porcelain` is non-empty).</summary>
    Task<bool> HasUncommittedChangesAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Discards every uncommitted change - tracked modifications (`reset --hard`) and untracked new files/folders (`clean -fd`) alike. Destructive and irreversible; callers confirm with the user first.</summary>
    Task DiscardChangesAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Adds an "origin" remote, or repoints it if one already exists.</summary>
    Task SetRemoteAsync(string workspacePath, string url, CancellationToken cancellationToken = default);

    /// <summary>False if there's no remote, or the fetch failed (network down, auth failure - GIT_TERMINAL_PROMPT=0 means this fails fast rather than hanging). `prune` also removes remote-tracking refs for branches deleted on the remote (`git fetch --prune`).</summary>
    Task<bool> FetchAsync(string workspacePath, bool prune = false, CancellationToken cancellationToken = default);

    /// <summary>Best-effort - false on failure, never throws (a push failure shouldn't roll back the local git action that already completed). `force` rewrites the remote branch's history (needed after Squash/Rebase/Rename locally rewrote it); `setUpstream` records the pushed branch as tracking `origin/refName` (used once, right after CreateBranchAsync).</summary>
    Task<bool> PushAsync(string workspacePath, string refName, bool force = false, bool setUpstream = false, CancellationToken cancellationToken = default);

    /// <summary>The commit `origin/{branchName}` currently points to, or null if there's no remote or that branch was never pushed.</summary>
    Task<string?> GetRemoteTrackingCommitAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default);

    /// <summary>Fetches then fast-forwards the current branch onto `origin/{branchName}`. False if it can't fast-forward (real divergence) or there's no remote.</summary>
    Task<bool> FastForwardPullAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default);
}
