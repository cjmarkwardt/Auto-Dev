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
        if (!await git.FetchAsync(workspacePath, prune: true, cancellationToken))
        {
            return; // no remote, or unreachable - nothing to sync
        }

        var current = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var candidates = await git.ListBranchesAsync(workspacePath, "", cancellationToken);

        foreach (var branch in candidates)
        {
            if (branch == current || !await git.BranchExistsAsync(workspacePath, branch, cancellationToken))
            {
                continue; // never reset the checked-out branch; skip remote-only names with no local ref
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

    public async Task<TagCreationOutcome> CreateTagAsync(string id, string fullName, string atRef, CancellationToken cancellationToken = default)
    {
        if (await git.TagExistsAsync(workspacePath, id, cancellationToken))
        {
            return TagCreationOutcome.IdAlreadyExists;
        }

        await git.CreateAnnotatedTagAsync(workspacePath, id, fullName, atRef, cancellationToken);
        await git.PushAsync(workspacePath, id, force: false, setUpstream: false, cancellationToken: cancellationToken);
        return TagCreationOutcome.Created;
    }

    public async Task DeleteBranchAsync(string name, CancellationToken cancellationToken = default) =>
        await git.DeleteBranchAsync(workspacePath, name, cancellationToken);

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

    public async Task PushCurrentBranchAsync(bool force, CancellationToken cancellationToken = default)
    {
        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (branch is not null)
        {
            await git.PushAsync(workspacePath, branch, force: force, cancellationToken: cancellationToken);
        }
    }

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

    public async Task SquashAsync(string baseBranch, string message, CancellationToken cancellationToken = default)
    {
        await SquashSinceBaseAsync(baseBranch, message, cancellationToken);
        await PushCurrentBranchAsync(force: true, cancellationToken);
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
            await git.PushAsync(workspacePath, targetBranch, cancellationToken: cancellationToken);
        }

        await git.CheckoutAsync(workspacePath, current, cancellationToken);
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
        var currentBranch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var tagsByCommit = await git.GetTagsByCommitAsync(workspacePath, cancellationToken);

        var entries = new List<BranchTimelineEntry>();
        foreach (var commit in pageCommits)
        {
            var isCurrentCommit = currentBranch == branchName && commit.Hash == head;

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
