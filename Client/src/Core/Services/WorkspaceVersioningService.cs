using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public sealed class WorkspaceVersioningService(string workspacePath, IGitService git) : IWorkspaceVersioningService
{
    private const string LocalExcludePattern = ".autodev/local/";

    public async Task<bool> IsRepoInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!await git.IsRepoAsync(workspacePath, cancellationToken))
        {
            return false;
        }

        // A freshly cloned-from-empty-remote repo is still a real git work tree (IsRepoAsync above already
        // returns true for it) but has no commits yet - HEAD is an "unborn" branch. Treating that the same as
        // "no repo yet" routes it through InitializeRepoAsync below just like a brand new plain folder.
        return await git.HasCommitsAsync(workspacePath, cancellationToken);
    }

    public async Task<bool> HasUserIdentityConfiguredAsync(CancellationToken cancellationToken = default) =>
        await git.HasUserIdentityConfiguredAsync(workspacePath, cancellationToken);

    public async Task SetGlobalUserIdentityAsync(string name, string email, CancellationToken cancellationToken = default) =>
        await git.SetGlobalUserIdentityAsync(workspacePath, name, email, cancellationToken);

    public async Task InitializeRepoAsync(CancellationToken cancellationToken = default)
    {
        await git.InitAsync(workspacePath, cancellationToken);
        await EnsureLocalGitExcludeAsync(cancellationToken);

        // Deliberately empty - anything already in the folder is left as pending, uncommitted content the
        // user commits explicitly afterward, same as any other edit, rather than silently folded into a
        // commit they never asked for.
        await git.CommitEmptyAsync(workspacePath, "Initial commit", cancellationToken);
        await git.RenameCurrentBranchAsync(workspacePath, "main", cancellationToken);

        // A no-op for a plain new folder (no "origin" yet - PushAsync itself checks and just returns false).
        // For a folder that reached here via a clone of an empty remote, "origin" is already configured from
        // the clone, so this is what actually lands the new main branch on the remote instead of leaving it
        // sitting local-only next to an otherwise-still-empty remote.
        await git.PushAsync(workspacePath, "main", setUpstream: true, cancellationToken: cancellationToken);
    }

    public async Task EnsureLocalGitExcludeAsync(CancellationToken cancellationToken = default)
    {
        var excludePath = Path.Combine(workspacePath, ".git", "info", "exclude");
        if (!Directory.Exists(Path.GetDirectoryName(excludePath)))
        {
            return; // not a git repo (.git/info missing) - nothing to do
        }

        var existing = File.Exists(excludePath) ? await File.ReadAllTextAsync(excludePath, cancellationToken) : "";
        if (existing.Split('\n').Any(line => line.Trim() == LocalExcludePattern))
        {
            return;
        }

        var separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
        await File.AppendAllTextAsync(excludePath, $"{separator}{LocalExcludePattern}\n", cancellationToken);
    }

    public async Task<GitTarget?> GetCurrentTargetAsync(CancellationToken cancellationToken = default)
    {
        if (!await git.IsRepoAsync(workspacePath, cancellationToken))
        {
            return null;
        }

        var hash = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        var shortHash = hash.Length > 7 ? hash[..7] : hash;
        var message = await git.GetCommitSubjectAsync(workspacePath, "HEAD", cancellationToken);

        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (branch is not null)
        {
            return new GitTarget(GitTargetKind.Branch, branch, null, shortHash, message);
        }

        var tag = await git.GetExactTagAsync(workspacePath, cancellationToken);
        return new GitTarget(tag is not null ? GitTargetKind.Tag : GitTargetKind.Commit, null, tag, shortHash, message);
    }

    public async Task ConfigureRemoteAsync(string url, CancellationToken cancellationToken = default) =>
        await git.SetRemoteAsync(workspacePath, url, cancellationToken);

    public async Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) =>
        await git.GetRemoteUrlAsync(workspacePath, cancellationToken);

    public async Task<bool> HasUncommittedChangesAsync(CancellationToken cancellationToken = default) =>
        await git.HasUncommittedChangesAsync(workspacePath, cancellationToken);

    public async Task SyncWithRemoteAsync(CancellationToken cancellationToken = default)
    {
        var current = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);

        // Captured before the fetch/prune below, which is exactly what could remove it - the only way to
        // tell "current never tracked a remote branch at all" (leave it alone) apart from "it did, and that
        // remote branch is now gone" (see the current-specific handling at the bottom) is to know whether it
        // was there beforehand.
        var currentHadRemoteTrackingBranch = current is not null
            && await git.GetRemoteTrackingCommitAsync(workspacePath, current, cancellationToken) is not null;

        if (!await git.FetchAsync(workspacePath, prune: true, cancellationToken))
        {
            return; // no remote, or unreachable - nothing to sync
        }

        var candidates = await git.ListBranchesAsync(workspacePath, "", cancellationToken);

        foreach (var branch in candidates)
        {
            if (branch == current || !await git.BranchExistsAsync(workspacePath, branch, cancellationToken))
            {
                continue; // the checked-out branch is handled separately below; skip remote-only names with no local ref
            }

            var remoteTip = await git.GetRemoteTrackingCommitAsync(workspacePath, branch, cancellationToken);
            if (remoteTip is null)
            {
                continue;
            }

            var localTip = await git.RevParseAsync(workspacePath, branch, cancellationToken);
            if (remoteTip != localTip)
            {
                await git.ForceUpdateBranchRefAsync(workspacePath, branch, remoteTip, cancellationToken);
            }
        }

        if (current is not null && currentHadRemoteTrackingBranch
            && await git.GetRemoteTrackingCommitAsync(workspacePath, current, cancellationToken) is null)
        {
            // The branch actually checked out just got pruned - its own remote counterpart is gone (e.g.
            // deleted by someone else's own post-merge cleanup elsewhere - see
            // VersionSectionViewModel.MergeAsync/HistoryTabViewModel.MergeIntoCurrentAsync, which do the same
            // thing this app itself just did). Detach HEAD at exactly the commit it was already on - a no-op
            // checkout content-wise (same commit, so it can never conflict), leaving any pending changes in
            // the working tree completely untouched - before deleting the now-orphaned local branch, since
            // git refuses to delete whichever branch is currently checked out.
            var currentTip = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
            await git.CheckoutAsync(workspacePath, currentTip, cancellationToken);
            await git.DeleteBranchAsync(workspacePath, current, cancellationToken);
        }
    }

    public async Task<GitActionSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var hash = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        return new GitActionSnapshot(branch, hash);
    }

    public async Task RevertToSnapshotAsync(GitActionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (await git.HasConflictsAsync(workspacePath, cancellationToken))
        {
            await git.RebaseAbortAsync(workspacePath, cancellationToken);
            await git.MergeAbortAsync(workspacePath, cancellationToken);
        }

        if (snapshot.Branch is not null)
        {
            var currentBranch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
            if (currentBranch != snapshot.Branch)
            {
                await git.DiscardChangesAsync(workspacePath, cancellationToken);
                await git.CheckoutAsync(workspacePath, snapshot.Branch, cancellationToken);
            }
        }

        await git.ResetHardAsync(workspacePath, snapshot.CommitHash, cancellationToken);
    }

    public async Task<BranchCreationOutcome> CreateBranchAsync(string name, string fromRef, CancellationToken cancellationToken = default)
    {
        if (await git.BranchExistsAsync(workspacePath, name, cancellationToken))
        {
            return BranchCreationOutcome.IdAlreadyExists;
        }

        await git.CreateBranchAsync(workspacePath, name, fromRef, cancellationToken);
        await git.CheckoutAsync(workspacePath, name, cancellationToken);
        await git.PushAsync(workspacePath, name, force: false, setUpstream: true, cancellationToken: cancellationToken);
        return BranchCreationOutcome.Created;
    }

    public async Task<TagCreationOutcome> CreateTagAsync(string name, string atRef, CancellationToken cancellationToken = default)
    {
        if (await git.TagExistsAsync(workspacePath, name, cancellationToken))
        {
            return TagCreationOutcome.IdAlreadyExists;
        }

        await git.CreateAnnotatedTagAsync(workspacePath, name, atRef, cancellationToken);
        await git.PushAsync(workspacePath, name, force: false, setUpstream: false, cancellationToken: cancellationToken);
        return TagCreationOutcome.Created;
    }

    public async Task DeleteBranchAsync(string name, CancellationToken cancellationToken = default) =>
        await git.DeleteBranchAsync(workspacePath, name, cancellationToken);

    public async Task<bool> DeleteBranchEverywhereAsync(string name, CancellationToken cancellationToken = default)
    {
        await git.DeleteBranchAsync(workspacePath, name, cancellationToken);
        return await git.GetRemoteUrlAsync(workspacePath, cancellationToken) is null
            || await git.DeleteRemoteBranchAsync(workspacePath, name, cancellationToken);
    }

    public async Task DeleteTagAsync(string name, CancellationToken cancellationToken = default) =>
        await git.DeleteTagAsync(workspacePath, name, cancellationToken);

    public async Task ResetAsync(CancellationToken cancellationToken = default) =>
        await git.DiscardChangesAsync(workspacePath, cancellationToken);

    public async Task<GitOperationOutcome> RebaseAsync(string ontoRef, CancellationToken cancellationToken = default) =>
        await git.RebaseOntoAsync(workspacePath, ontoRef, cancellationToken);

    public async Task<GitOperationOutcome> ContinueRebaseAsync(CancellationToken cancellationToken = default) =>
        await git.RebaseContinueAsync(workspacePath, cancellationToken);

    public async Task AbortRebaseAsync(CancellationToken cancellationToken = default) =>
        await git.RebaseAbortAsync(workspacePath, cancellationToken);

    public async Task<GitOperationOutcome> MergeAsync(string sourceBranch, CancellationToken cancellationToken = default) =>
        await git.MergeAsync(workspacePath, sourceBranch, cancellationToken);

    public async Task<GitOperationOutcome> ContinueMergeAsync(CancellationToken cancellationToken = default) =>
        await git.MergeContinueAsync(workspacePath, cancellationToken);

    public async Task AbortMergeAsync(CancellationToken cancellationToken = default) =>
        await git.MergeAbortAsync(workspacePath, cancellationToken);

    public async Task<bool> HasConflictsAsync(CancellationToken cancellationToken = default) =>
        await git.HasConflictsAsync(workspacePath, cancellationToken);

    public async Task<IReadOnlyList<string>> GetConflictedFilesAsync(CancellationToken cancellationToken = default) =>
        await git.GetConflictedFilesAsync(workspacePath, cancellationToken);

    public async Task CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        await git.CommitAsync(workspacePath, message, cancellationToken);
        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (branch is not null)
        {
            await git.PushAsync(workspacePath, branch, cancellationToken: cancellationToken);
        }
    }

    public async Task<bool> PushCurrentBranchAsync(bool force, CancellationToken cancellationToken = default)
    {
        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        return branch is null || await git.PushAsync(workspacePath, branch, force: force, cancellationToken: cancellationToken);
    }

    public async Task<PullWithStashResult> PullCurrentBranchWithStashAsync(CancellationToken cancellationToken = default)
    {
        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (branch is null)
        {
            return new PullWithStashResult(PullWithStashOutcome.NothingToDo, null);
        }

        // Saved before touching anything - not just the pull's own precondition check below, but also what a
        // caller building an AI conflict-resolution instruction needs to describe "every commit pulled in
        // since" (see HistoryTabViewModel/VersionSectionViewModel).
        var originalCommitHash = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);

        var remoteTip = await git.GetRemoteTrackingCommitAsync(workspacePath, branch, cancellationToken);
        if (remoteTip is null || remoteTip == originalCommitHash
            || !await git.IsAncestorAsync(workspacePath, originalCommitHash, remoteTip, cancellationToken))
        {
            // No remote-tracking branch, already up to date, or the current branch has diverged from it (its
            // own local commits aren't all on the remote yet either) - not a simple fast-forward, so nothing
            // this flow silently auto-pulls; a real divergence needs an explicit Rebase/Merge from the user.
            return new PullWithStashResult(PullWithStashOutcome.NothingToDo, originalCommitHash);
        }

        var hadPendingChanges = await git.HasUncommittedChangesAsync(workspacePath, cancellationToken);
        if (hadPendingChanges && !await git.StashPushAsync(workspacePath, cancellationToken))
        {
            return new PullWithStashResult(PullWithStashOutcome.Failed, originalCommitHash);
        }

        if (!await git.FastForwardMergeAsync(workspacePath, $"origin/{branch}", cancellationToken))
        {
            if (hadPendingChanges)
            {
                // Best-effort restore of exactly what was pending before this call - the pull itself never
                // touched the working tree (fast-forward failed outright), so the pop it's undone by should
                // always be clean.
                await git.StashPopAsync(workspacePath, cancellationToken);
            }

            return new PullWithStashResult(PullWithStashOutcome.Failed, originalCommitHash);
        }

        if (!hadPendingChanges)
        {
            return new PullWithStashResult(PullWithStashOutcome.Succeeded, originalCommitHash);
        }

        var popOutcome = await git.StashPopAsync(workspacePath, cancellationToken);
        return popOutcome switch
        {
            GitOperationOutcome.Succeeded => new PullWithStashResult(PullWithStashOutcome.Succeeded, originalCommitHash),
            GitOperationOutcome.Conflicts => new PullWithStashResult(PullWithStashOutcome.Conflicts, originalCommitHash),
            _ => new PullWithStashResult(PullWithStashOutcome.Failed, originalCommitHash),
        };
    }

    public async Task DropStashAsync(CancellationToken cancellationToken = default) =>
        await git.StashDropAsync(workspacePath, cancellationToken);

    public async Task CheckoutRefAsync(string refName, CancellationToken cancellationToken = default) =>
        await git.CheckoutAsync(workspacePath, refName, cancellationToken);

    public async Task<IReadOnlyList<string>> GetEligibleBaseBranchesAsync(CancellationToken cancellationToken = default)
    {
        var current = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (current is null)
        {
            return []; // detached HEAD - Squash/Rebase are only ever offered while targeting a branch
        }

        var results = new List<string>();
        foreach (var branch in await git.ListBranchesAsync(workspacePath, "", cancellationToken))
        {
            if (branch == current || !await git.BranchExistsAsync(workspacePath, branch, cancellationToken))
            {
                continue;
            }

            if (await git.IsAncestorAsync(workspacePath, branch, current, cancellationToken))
            {
                continue; // current is already built on top of this branch - nothing to squash/rebase against
            }

            results.Add(branch);
        }

        return [.. results.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<string> GetDefaultSquashMessageAsync(string baseBranch, CancellationToken cancellationToken = default)
    {
        var mergeBase = await git.MergeBaseAsync(workspacePath, baseBranch, "HEAD", cancellationToken);
        var commits = await git.GetCommitsSinceAsync(workspacePath, mergeBase, "HEAD", cancellationToken);
        return commits.Count > 0 ? commits[0].Subject : "";
    }

    public async Task<bool> SquashAsync(string baseBranch, string message, CancellationToken cancellationToken = default)
    {
        await SquashSinceBaseAsync(baseBranch, message, cancellationToken);
        return await PushCurrentBranchAsync(force: true, cancellationToken);
    }

    public async Task<GitOperationOutcome> RebaseWithSquashAsync(string ontoBranch, string squashMessage, CancellationToken cancellationToken = default)
    {
        await SquashSinceBaseAsync(ontoBranch, squashMessage, cancellationToken);
        return await git.RebaseOntoAsync(workspacePath, ontoBranch, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetEligibleMergeTargetBranchesAsync(CancellationToken cancellationToken = default)
    {
        var current = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (current is null)
        {
            return []; // detached HEAD - Merge is only ever offered while targeting a branch
        }

        var results = new List<string>();
        foreach (var branch in await git.ListBranchesAsync(workspacePath, "", cancellationToken))
        {
            if (branch == current || !await git.BranchExistsAsync(workspacePath, branch, cancellationToken))
            {
                continue;
            }

            if (await git.IsAncestorAsync(workspacePath, branch, current, cancellationToken))
            {
                results.Add(branch); // current is ahead of this branch - a valid fast-forward target
            }
        }

        return [.. results.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<bool> FastForwardMergeAsync(string targetBranch, string? squashMessage, CancellationToken cancellationToken = default)
    {
        var current = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (current is null)
        {
            return false;
        }

        var mergeBase = await git.MergeBaseAsync(workspacePath, targetBranch, "HEAD", cancellationToken);
        var targetTip = await git.RevParseAsync(workspacePath, targetBranch, cancellationToken);
        if (mergeBase != targetTip)
        {
            return false; // current isn't based on targetBranch's own head - can't fast-forward
        }

        if (squashMessage is not null)
        {
            var commitsSinceBase = await git.GetCommitsSinceAsync(workspacePath, mergeBase, "HEAD", cancellationToken);
            if (commitsSinceBase.Count > 1)
            {
                await git.SquashSinceAsync(workspacePath, mergeBase, squashMessage, cancellationToken);
            }
        }

        var currentTip = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        await git.CheckoutAsync(workspacePath, targetBranch, cancellationToken);
        var fastForwarded = await git.FastForwardMergeAsync(workspacePath, currentTip, cancellationToken);
        if (fastForwarded)
        {
            // Stays on targetBranch rather than returning to current (unlike a failed attempt, reverted
            // below) - current's own work is now fully absorbed into targetBranch, and the caller deletes
            // current next (see VersionSectionViewModel.MergeAsync), which isn't even possible while it's
            // still the checked-out branch.
            await git.PushAsync(workspacePath, targetBranch, cancellationToken: cancellationToken);
        }
        else
        {
            await git.CheckoutAsync(workspacePath, current, cancellationToken);
        }

        return fastForwarded;
    }

    private async Task SquashSinceBaseAsync(string baseBranch, string message, CancellationToken cancellationToken)
    {
        var mergeBase = await git.MergeBaseAsync(workspacePath, baseBranch, "HEAD", cancellationToken);
        await git.SquashSinceAsync(workspacePath, mergeBase, message, cancellationToken);
    }

    public async Task<IReadOnlyList<BranchSummary>> ListAllBranchesAsync(CancellationToken cancellationToken = default)
    {
        var current = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var names = await git.ListBranchesAsync(workspacePath, "", cancellationToken);

        var results = new List<BranchSummary>();
        foreach (var name in names)
        {
            if (!await git.BranchExistsAsync(workspacePath, name, cancellationToken))
            {
                continue; // remote-only, no local branch to show/select
            }

            results.Add(new BranchSummary(name, IsCurrent: name == current));
        }

        return [.. results.OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<BranchTimelinePage?> GetBranchTimelinePageAsync(string branchName, int pageIndex, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        if (!await git.BranchExistsAsync(workspacePath, branchName, cancellationToken))
        {
            return null;
        }

        var allCommits = await git.LogAsync(workspacePath, branchName, cancellationToken); // oldest-first
        var workCommits = allCommits.Reverse().ToList(); // newest first

        var pageCount = Math.Max(1, (int)Math.Ceiling(workCommits.Count / (double)pageSize));
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);
        var pageCommits = workCommits.Skip(pageIndex * pageSize).Take(pageSize).ToList();

        var head = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        var tagsByCommit = await git.GetTagsByCommitAsync(workspacePath, cancellationToken);

        var entries = new List<BranchTimelineEntry>();
        foreach (var commit in pageCommits)
        {
            // Just the commit hash, regardless of whether HEAD is attached to a branch or detached at a tag/
            // commit (previously also required the *viewed* branch to be the checked-out one, which meant a
            // detached HEAD - browsing a tag or an arbitrary commit - never showed a current-commit indicator
            // anywhere at all) - a commit matching HEAD's hash genuinely is the current commit no matter what
            // ref got you there, or which branch's own timeline happens to be on screen.
            var isCurrentCommit = commit.Hash == head;

            // Each tag gets its own node immediately above the commit it points at, rather than riding along
            // on the commit's own row - see BranchTimelineEntryKind.Tag. IsCurrentCommit carries over too, so
            // the row's own "Checkout" menu item isn't offered as a no-op from a tag pointing at HEAD.
            foreach (var tag in tagsByCommit.GetValueOrDefault(commit.Hash, []))
            {
                entries.Add(new BranchTimelineEntry(BranchTimelineEntryKind.Tag, tag.DisplayName, commit.Date, commit.Hash, isCurrentCommit, tag.Name));
            }

            entries.Add(new BranchTimelineEntry(BranchTimelineEntryKind.Commit, commit.Subject, commit.Date, commit.Hash, isCurrentCommit));
        }

        return new BranchTimelinePage(branchName, entries, pageIndex, pageCount);
    }

    public async Task<IReadOnlyList<GitChange>> GetCommitChangesAsync(string commitHash, CancellationToken cancellationToken = default) =>
        await git.GetCommitChangesAsync(workspacePath, commitHash, cancellationToken);

    public async Task<FileDiffContent> GetFileDiffAsync(string commitHash, string relativePath, CancellationToken cancellationToken = default)
    {
        // Empty (rather than a resolved hash) is git's own way of saying `{commitHash}^` doesn't exist -
        // commitHash is this branch's root commit, with nothing before it to compare against.
        var parentHash = await git.RevParseAsync(workspacePath, $"{commitHash}^", cancellationToken);
        var before = parentHash.Length > 0
            ? await git.GetFileContentAtCommitAsync(workspacePath, parentHash, relativePath, cancellationToken)
            : null;
        var after = await git.GetFileContentAtCommitAsync(workspacePath, commitHash, relativePath, cancellationToken);
        return new FileDiffContent(before, after);
    }

    public async Task<IReadOnlyList<GitChange>> GetWorkingTreeChangesAsync(CancellationToken cancellationToken = default) =>
        await git.GetWorkingTreeChangesAsync(workspacePath, cancellationToken);

    public async Task<FileDiffContent> GetWorkingTreeFileDiffAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var before = await git.GetFileContentAtCommitAsync(workspacePath, "HEAD", relativePath, cancellationToken);

        var fullPath = Path.Combine(workspacePath, relativePath);
        var after = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, cancellationToken) : null;

        return new FileDiffContent(before, after);
    }
}

public sealed class VersioningServiceFactory(IGitService git) : IVersioningServiceFactory
{
    public IWorkspaceVersioningService Create(string workspacePath) => new WorkspaceVersioningService(workspacePath, git);
}
