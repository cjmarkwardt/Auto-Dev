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
        // "no repo yet" routes it through InitializeRepoAsync below just like a brand new plain folder, so a
        // clone of an empty remote ends up with the same main-branch-plus-base-commit convention instead of
        // being left as a bare, convention-less checkout.
        return await git.HasCommitsAsync(workspacePath, cancellationToken);
    }

    public async Task InitializeRepoAsync(CancellationToken cancellationToken = default)
    {
        await git.InitAsync(workspacePath, cancellationToken);
        await EnsureLocalGitExcludeAsync(cancellationToken);

        // Staging everything already in the folder is correct here (unlike CommitEmptyAsync's base-commit
        // markers elsewhere) - there's no prior state to preserve as "still pending", this commit IS the
        // repo's starting content. isPublic: true - main is the repo's permanent, shared root branch, not a
        // private one-user branch meant to be squashed/renamed away (see BranchConvention/GetActionStateAsync's
        // CanSquash/CanRename, both gated on !IsPublic).
        await git.CommitAsync(workspacePath, BranchConvention.BuildBaseCommitMessage("Main", parentId: null, isPublic: true, "main"), allowEmpty: true, cancellationToken);
        await git.RenameCurrentBranchAsync(workspacePath, "main", cancellationToken);

        // A no-op for a plain new folder (no "origin" yet - PushAsync itself checks and just returns false).
        // For a folder that reached here via a clone of an empty remote, "origin" is already configured from
        // the clone, so this is what actually lands the new main branch/base commit on the remote instead of
        // leaving it sitting local-only next to an otherwise-still-empty remote.
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

        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (branch is not null)
        {
            var info = await BranchConvention.FindBranchInfoByIdAsync(git, workspacePath, branch, cancellationToken);
            return new GitTarget(GitTargetKind.Branch, branch, info);
        }

        var tag = await git.GetExactTagAsync(workspacePath, cancellationToken);
        if (tag is not null)
        {
            return new GitTarget(GitTargetKind.Tag, tag);
        }

        var hash = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        return new GitTarget(GitTargetKind.Commit, hash.Length > 7 ? hash[..7] : hash);
    }

    public async Task<VersionActionState> GetActionStateAsync(CancellationToken cancellationToken = default)
    {
        var target = await GetCurrentTargetAsync(cancellationToken);
        if (target is null)
        {
            return VersionActionState.Empty;
        }

        var hasPending = await git.HasUncommittedChangesAsync(workspacePath, cancellationToken);

        if (target is not { Kind: GitTargetKind.Branch, Branch: { } info })
        {
            // Detached (tag/commit), or a branch whose base commit can't be resolved (predates this
            // convention) - only Branch/Reset are meaningful without a known lineage.
            return new VersionActionState(CanBranch: true, CanReset: hasPending, false, false, false, false, false);
        }

        var head = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        var hasCommitsAfterBase = head != info.BaseCommitHash;

        var parentHasNewCommits = false;
        if (info.ParentId is not null && await git.BranchExistsAsync(workspacePath, info.ParentId, cancellationToken))
        {
            var baseParent = await git.RevParseAsync(workspacePath, $"{info.BaseCommitHash}^", cancellationToken);
            var parentTip = await git.RevParseAsync(workspacePath, info.ParentId, cancellationToken);
            parentHasNewCommits = baseParent.Length > 0 && baseParent != parentTip && await git.IsAncestorAsync(workspacePath, baseParent, parentTip, cancellationToken);
        }

        return new VersionActionState(
            CanBranch: true,
            CanReset: hasPending,
            CanSquash: (hasPending || hasCommitsAfterBase) && !info.IsPublic,
            CanRebase: parentHasNewCommits,
            CanMerge: info.ParentId is not null && !parentHasNewCommits && !hasCommitsAfterBase && !hasPending,
            CanRename: !info.IsPublic,
            CanCommit: hasPending);
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

    public async Task<BranchCreationOutcome> CreateBranchAsync(string name, string id, bool isPublic, CancellationToken cancellationToken = default)
    {
        if (await git.BranchExistsAsync(workspacePath, id, cancellationToken))
        {
            return BranchCreationOutcome.IdAlreadyExists;
        }

        // Attached: the current branch IS its own id, no history walk needed. Detached (tag/commit): fall
        // back to the "conceptual" containing branch found by walking HEAD's history for the nearest marker.
        var currentBranch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var parentId = currentBranch ?? (await BranchConvention.FindContainingBranchInfoAsync(git, workspacePath, "HEAD", cancellationToken))?.Id;

        await git.CreateBranchAsync(workspacePath, id, "HEAD", cancellationToken);
        await git.CheckoutAsync(workspacePath, id, cancellationToken);
        await git.CommitEmptyAsync(workspacePath, BranchConvention.BuildBaseCommitMessage(name, parentId, isPublic, id), cancellationToken);
        await git.PushAsync(workspacePath, id, force: false, setUpstream: true, cancellationToken: cancellationToken);
        return BranchCreationOutcome.Created;
    }

    public async Task<TagCreationOutcome> CreateTagAsync(string id, string fullName, CancellationToken cancellationToken = default)
    {
        if (await git.TagExistsAsync(workspacePath, id, cancellationToken))
        {
            return TagCreationOutcome.IdAlreadyExists;
        }

        await git.CreateAnnotatedTagAsync(workspacePath, id, fullName, cancellationToken);
        await git.PushAsync(workspacePath, id, force: false, setUpstream: false, cancellationToken: cancellationToken);
        return TagCreationOutcome.Created;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default) =>
        await git.DiscardChangesAsync(workspacePath, cancellationToken);

    public async Task SquashAsync(CancellationToken cancellationToken = default)
    {
        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var info = branch is null ? null : await BranchConvention.FindBranchInfoByIdAsync(git, workspacePath, branch, cancellationToken);
        if (branch is null || info is null)
        {
            return;
        }

        await git.ResetSoftAsync(workspacePath, info.BaseCommitHash, cancellationToken);
        var message = BranchConvention.BuildBaseCommitMessage(info.Name, info.ParentId, info.IsPublic, info.Id);
        await git.AmendCommitAsync(workspacePath, message, allowEmpty: true, cancellationToken);
        await git.PushAsync(workspacePath, branch, force: true, cancellationToken: cancellationToken);
    }

    public async Task<RebaseOutcome> RebaseAsync(CancellationToken cancellationToken = default)
    {
        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var info = branch is null ? null : await BranchConvention.FindBranchInfoByIdAsync(git, workspacePath, branch, cancellationToken);
        if (info?.ParentId is not { } parentId)
        {
            return RebaseOutcome.Failed;
        }

        if (!info.IsPublic)
        {
            await SquashAsync(cancellationToken);
        }

        return await git.RebaseOntoAsync(workspacePath, parentId, cancellationToken);
    }

    public async Task<RebaseOutcome> ContinueRebaseAsync(CancellationToken cancellationToken = default) =>
        await git.RebaseContinueAsync(workspacePath, cancellationToken);

    public async Task AbortRebaseAsync(CancellationToken cancellationToken = default) =>
        await git.RebaseAbortAsync(workspacePath, cancellationToken);

    public async Task<bool> HasConflictsAsync(CancellationToken cancellationToken = default) =>
        await git.HasConflictsAsync(workspacePath, cancellationToken);

    public async Task<IReadOnlyList<string>> GetConflictedFilesAsync(CancellationToken cancellationToken = default) =>
        await git.GetConflictedFilesAsync(workspacePath, cancellationToken);

    public async Task FinishMergeAsync(CancellationToken cancellationToken = default)
    {
        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var info = branch is null ? null : await BranchConvention.FindBranchInfoByIdAsync(git, workspacePath, branch, cancellationToken);
        if (info?.ParentId is not { } parentId)
        {
            return;
        }

        await git.CheckoutAsync(workspacePath, parentId, cancellationToken);
        await git.FastForwardMergeAsync(workspacePath, info.Id, cancellationToken);
        await git.DeleteBranchAsync(workspacePath, info.Id, cancellationToken);
        await git.PushAsync(workspacePath, parentId, cancellationToken: cancellationToken);
    }

    public async Task RenameAsync(string newName, CancellationToken cancellationToken = default)
    {
        var branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var info = branch is null ? null : await BranchConvention.FindBranchInfoByIdAsync(git, workspacePath, branch, cancellationToken);
        if (branch is null || info is null)
        {
            return;
        }

        await git.ResetSoftAsync(workspacePath, info.BaseCommitHash, cancellationToken);
        var message = BranchConvention.BuildBaseCommitMessage(newName, info.ParentId, info.IsPublic, info.Id);
        await git.AmendCommitAsync(workspacePath, message, allowEmpty: true, cancellationToken);
        await git.PushAsync(workspacePath, branch, force: true, cancellationToken: cancellationToken);
    }

    public async Task CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        await git.CommitAsync(workspacePath, message, allowEmpty: false, cancellationToken);
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

            var info = await BranchConvention.FindBranchInfoByIdAsync(git, workspacePath, name, cancellationToken);
            results.Add(new BranchSummary(info?.Id ?? name, info?.Name ?? name, info?.ParentId, info?.IsPublic ?? false, IsCurrent: name == current));
        }

        return [.. results.OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<BranchTimelinePage?> GetBranchTimelinePageAsync(string branchId, int pageIndex, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        if (!await git.BranchExistsAsync(workspacePath, branchId, cancellationToken))
        {
            return null;
        }

        var info = await BranchConvention.FindBranchInfoByIdAsync(git, workspacePath, branchId, cancellationToken);
        var effectiveId = info?.Id ?? branchId;
        var effectiveName = info?.Name ?? branchId;

        var allCommits = await git.LogAsync(workspacePath, branchId, cancellationToken); // oldest-first
        IReadOnlyList<GitCommit> commits = allCommits;
        if (info is not null)
        {
            var startIndex = allCommits.ToList().FindIndex(c => c.Hash == info.BaseCommitHash);
            if (startIndex >= 0)
            {
                commits = [.. allCommits.Skip(startIndex)];
            }
        }

        // The oldest commit in this slice (position 0, if it's this branch's own recognized base marker) is
        // kept and relabeled "Base" below; any OTHER base-commit-shaped commit further along was left behind
        // by an earlier Rename/Squash that amended a later position instead of this one - not real work, so it
        // isn't shown as its own node at all (see BranchTimelineEntry's doc comment).
        var workCommits = commits
            .Where((c, i) => i == 0 || !BranchConvention.TryParseBaseCommitMessage(c.Subject, out _, out _, out _, out _))
            .Reverse() // newest first
            .ToList();

        var pageCount = Math.Max(1, (int)Math.Ceiling(workCommits.Count / (double)pageSize));
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);
        var pageCommits = workCommits.Skip(pageIndex * pageSize).Take(pageSize).ToList();

        var head = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        var currentBranch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        var allBranches = await ListAllBranchesAsync(cancellationToken);
        var tagsByCommit = await git.GetTagsByCommitAsync(workspacePath, cancellationToken);

        var entries = new List<BranchTimelineEntry>();

        if (pageIndex == 0)
        {
            foreach (var child in allBranches.Where(b => b.ParentId == effectiveId))
            {
                entries.Add(new BranchTimelineEntry(BranchTimelineEntryKind.ChildLink, child.Name, null, null, child.Id));
            }
        }

        foreach (var commit in pageCommits)
        {
            var isCurrentCommit = currentBranch == branchId && commit.Hash == head;

            // Each tag gets its own node immediately above the commit it points at, rather than riding along
            // on the commit's own row - see BranchTimelineEntryKind.Tag. IsCurrentCommit carries over too, so
            // TimelineEntryViewModel.CanSwitch doesn't offer a no-op "Switch" from a tag pointing at HEAD.
            foreach (var tag in tagsByCommit.GetValueOrDefault(commit.Hash, []))
            {
                entries.Add(new BranchTimelineEntry(BranchTimelineEntryKind.Tag, tag, commit.Date, commit.Hash, null, isCurrentCommit));
            }

            var isBase = info is not null && commit.Hash == info.BaseCommitHash;
            var label = isBase ? "Base" : commit.Subject;
            entries.Add(new BranchTimelineEntry(BranchTimelineEntryKind.Commit, label, commit.Date, commit.Hash, null, isCurrentCommit, isBase));
        }

        if (pageIndex == pageCount - 1 && info?.ParentId is { } parentId)
        {
            var parentInfo = await BranchConvention.FindBranchInfoByIdAsync(git, workspacePath, parentId, cancellationToken);
            entries.Add(new BranchTimelineEntry(BranchTimelineEntryKind.ParentLink, parentInfo?.Name ?? parentId, null, null, parentId));
        }

        return new BranchTimelinePage(effectiveId, effectiveName, entries, pageIndex, pageCount);
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
