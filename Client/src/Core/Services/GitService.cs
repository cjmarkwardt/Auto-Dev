using AutoDev.Core.Models;
using CliWrap;
using CliWrap.Buffered;

namespace AutoDev.Core.Services;

public sealed class GitService : IGitService
{
    public bool IsInstalled => GitCliLocator.IsInstalled;

    public async Task<bool> IsRepoAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        return result.ExitCode == 0 && result.StandardOutput.Trim() == "true";
    }

    public async Task<bool> HasUserIdentityConfiguredAsync(string workspacePath, CancellationToken cancellationToken = default) =>
        (await RunAsync(workspacePath, ["var", "GIT_AUTHOR_IDENT"], cancellationToken)).ExitCode == 0;

    public async Task SetGlobalUserIdentityAsync(string workspacePath, string name, string email, CancellationToken cancellationToken = default)
    {
        await RunAsync(workspacePath, ["config", "--global", "user.name", name], cancellationToken);
        await RunAsync(workspacePath, ["config", "--global", "user.email", email], cancellationToken);
    }

    public async Task<bool> HasCommitsAsync(string workspacePath, CancellationToken cancellationToken = default) =>
        (await RunAsync(workspacePath, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken)).ExitCode == 0;

    public async Task InitAsync(string workspacePath, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["init"], cancellationToken);

    public async Task<GitCloneResult> CloneAsync(string parentDirectory, string url, string destinationName, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(parentDirectory, ["clone", url, destinationName], cancellationToken);
        if (result.ExitCode == 0)
        {
            return new GitCloneResult(true, null);
        }

        var errorMessage = result.StandardError.Trim();
        return new GitCloneResult(false, errorMessage.Length > 0 ? errorMessage : null);
    }

    public async Task CommitAsync(string workspacePath, string message, CancellationToken cancellationToken = default)
    {
        await RunAsync(workspacePath, ["add", "-A"], cancellationToken);
        await RunAsync(workspacePath, ["commit", "-m", message], cancellationToken);
    }

    public async Task CommitEmptyAsync(string workspacePath, string message, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["commit", "--allow-empty", "-m", message], cancellationToken);

    public async Task RenameCurrentBranchAsync(string workspacePath, string newName, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["branch", "-m", newName], cancellationToken);

    public async Task CheckoutAsync(string workspacePath, string refName, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["checkout", refName], cancellationToken);

    public async Task<string?> GetCurrentBranchAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["symbolic-ref", "--short", "-q", "HEAD"], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public async Task<string?> GetExactTagAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["describe", "--tags", "--exact-match", "HEAD"], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public async Task<IReadOnlySet<string>> GetIgnoredPathsAsync(string workspacePath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return new HashSet<string>();
        }

        // -z NUL-delimits both the stdin paths and the ignored-paths output, so a match is always an exact
        // round-trip of one of our own input strings regardless of any unusual characters in a filename.
        var stdin = string.Join('\0', paths) + '\0';
        var result = await RunAsync(workspacePath, ["check-ignore", "--stdin", "-z"], cancellationToken, PipeSource.FromString(stdin));

        // 0 = at least one path matched, 1 = none did, 128 = fatal (e.g. not a git repo) - only the last
        // needs an explicit guard; 0/1 both just mean "parse whatever stdout has" (empty for exit code 1).
        return result.ExitCode == 128
            ? new HashSet<string>()
            : new HashSet<string>(result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// `git status --porcelain --ignored` never reports a clean tracked file/directory at all - there's
    /// nothing to say about it. That's exactly what makes classifying a *directory* ambiguous: one that's
    /// genuinely fully ignored, and one that's mostly clean tracked content plus one incidental ignored
    /// subitem (e.g. a "target/" build folder sitting next to committed source files), both produce the exact
    /// same single "!! path" status line - the clean tracked files on either side are invisible to status
    /// either way. `git ls-files` is the only way to tell those apart, so it's only ever run as a fallback,
    /// once status alone can't already tell "Added"/"Modified" apart from "maybe-Ignored, maybe-Unmodified".
    /// </summary>
    public async Task<GitFileStatus> GetStatusAsync(string workspacePath, string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["status", "--porcelain", "--ignored", "--", path], cancellationToken);
        var lines = SplitLines(result.StandardOutput);
        if (lines.Count == 0)
        {
            return GitFileStatus.Unmodified;
        }

        var sawIgnored = false;
        foreach (var line in lines)
        {
            if (line.Length < 2)
            {
                continue;
            }

            var indexStatus = line[0];
            var worktreeStatus = line[1];

            if (indexStatus == '!' && worktreeStatus == '!')
            {
                sawIgnored = true;
                continue;
            }

            if ((indexStatus == '?' && worktreeStatus == '?') || indexStatus == 'A')
            {
                return GitFileStatus.Added; // untracked, or staged as a brand new file - highest priority
            }

            return GitFileStatus.Modified; // any other non-ignored status (M/D/R/C/U/...) is some kind of change
        }

        if (!sawIgnored)
        {
            return GitFileStatus.Unmodified; // shouldn't happen (every line was too short to classify), but safe default
        }

        var tracked = await RunAsync(workspacePath, ["ls-files", "--", path], cancellationToken);
        return SplitLines(tracked.StandardOutput).Count > 0 ? GitFileStatus.Unmodified : GitFileStatus.Ignored;
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(string workspacePath, string prefix, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["tag", "--list", $"{prefix}*"], cancellationToken);
        return SplitLines(result.StandardOutput);
    }

    /// <summary>
    /// Local branches plus remote-tracking ones (deduped down to their plain name), so a version/feature
    /// someone else pushed shows up here even if this clone never checked it out locally - see
    /// EnsureLocalBranchAsync, which callers use to materialize a real local branch for anything found only
    /// via the remote-tracking half of this list before operating on it further (log, checkout, etc.).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(string workspacePath, string prefix, CancellationToken cancellationToken = default)
    {
        var local = await RunAsync(workspacePath, ["branch", "--list", $"{prefix}*", "--format=%(refname:short)"], cancellationToken);
        var remote = await RunAsync(workspacePath, ["branch", "-r", "--list", $"origin/{prefix}*", "--format=%(refname:short)"], cancellationToken);

        var names = new HashSet<string>(SplitLines(local.StandardOutput));
        foreach (var name in SplitLines(remote.StandardOutput))
        {
            names.Add(name.StartsWith("origin/", StringComparison.Ordinal) ? name["origin/".Length..] : name);
        }

        return [.. names];
    }

    public async Task EnsureLocalBranchAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default)
    {
        var verify = await RunAsync(workspacePath, ["show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"], cancellationToken);
        if (verify.ExitCode == 0)
        {
            return; // already exists locally
        }

        await RunAsync(workspacePath, ["branch", branchName, $"origin/{branchName}"], cancellationToken);
    }

    public async Task CreateBranchAsync(string workspacePath, string branchName, string fromRef, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["branch", branchName, fromRef], cancellationToken);

    public async Task DeleteBranchAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["branch", "-D", branchName], cancellationToken);

    public async Task<bool> DeleteRemoteBranchAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default)
    {
        if (await GetRemoteUrlAsync(workspacePath, cancellationToken) is null)
        {
            return false;
        }

        var result = await RunAsync(workspacePath, ["push", "origin", "--delete", branchName], cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task DeleteTagAsync(string workspacePath, string tagName, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["tag", "-d", tagName], cancellationToken);

    public async Task<bool> BranchExistsAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default) =>
        (await RunAsync(workspacePath, ["show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"], cancellationToken)).ExitCode == 0;

    public async Task<bool> TagExistsAsync(string workspacePath, string tagName, CancellationToken cancellationToken = default) =>
        (await RunAsync(workspacePath, ["show-ref", "--verify", "--quiet", $"refs/tags/{tagName}"], cancellationToken)).ExitCode == 0;

    public async Task ForceUpdateBranchRefAsync(string workspacePath, string branchName, string targetRef, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["branch", "-f", branchName, targetRef], cancellationToken);

    public async Task CreateAnnotatedTagAsync(string workspacePath, string name, string atRef, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["tag", "-a", name, "-m", "", atRef], cancellationToken);

    public async Task<GitOperationOutcome> RebaseOntoAsync(string workspacePath, string ontoRef, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["rebase", ontoRef], cancellationToken);
        return await ClassifyOperationResultAsync(workspacePath, result, cancellationToken);
    }

    public async Task<GitOperationOutcome> RebaseContinueAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["rebase", "--continue"], cancellationToken);
        return await ClassifyOperationResultAsync(workspacePath, result, cancellationToken);
    }

    public async Task RebaseAbortAsync(string workspacePath, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["rebase", "--abort"], cancellationToken);

    public async Task<GitOperationOutcome> MergeAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["merge", branchName], cancellationToken);
        return await ClassifyOperationResultAsync(workspacePath, result, cancellationToken);
    }

    public async Task<GitOperationOutcome> MergeContinueAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["merge", "--continue"], cancellationToken);
        return await ClassifyOperationResultAsync(workspacePath, result, cancellationToken);
    }

    public async Task MergeAbortAsync(string workspacePath, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["merge", "--abort"], cancellationToken);

    public async Task<bool> HasConflictsAsync(string workspacePath, CancellationToken cancellationToken = default) =>
        (await GetConflictedFilesAsync(workspacePath, cancellationToken)).Count > 0;

    public async Task<IReadOnlyList<string>> GetConflictedFilesAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["diff", "--name-only", "--diff-filter=U"], cancellationToken);
        return SplitLines(result.StandardOutput);
    }

    public async Task<string?> GetRemoteUrlAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["remote", "get-url", "origin"], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public async Task SetRemoteAsync(string workspacePath, string url, CancellationToken cancellationToken = default)
    {
        var existing = await GetRemoteUrlAsync(workspacePath, cancellationToken);
        string[] args = existing is null ? ["remote", "add", "origin", url] : ["remote", "set-url", "origin", url];
        await RunAsync(workspacePath, args, cancellationToken);
    }

    public async Task<bool> FetchAsync(string workspacePath, bool prune = false, CancellationToken cancellationToken = default)
    {
        if (await GetRemoteUrlAsync(workspacePath, cancellationToken) is null)
        {
            return false;
        }

        // --tags: releases are just tags, and a plain fetch only auto-follows ones reachable from fetched
        // branches - explicit --tags guarantees every release is known locally regardless of reachability.
        List<string> args = ["fetch", "origin", "--tags"];
        if (prune)
        {
            args.Add("--prune");
        }

        var result = await RunAsync(workspacePath, args, cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<bool> PushAsync(string workspacePath, string refName, bool force = false, bool setUpstream = false, CancellationToken cancellationToken = default)
    {
        if (await GetRemoteUrlAsync(workspacePath, cancellationToken) is null)
        {
            return false;
        }

        List<string> args = ["push", "origin", refName];
        if (force)
        {
            args.Add("--force");
        }

        if (setUpstream)
        {
            args.Add("--set-upstream");
        }

        var result = await RunAsync(workspacePath, args, cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<string?> GetRemoteTrackingCommitAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["rev-parse", $"origin/{branchName}"], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public async Task<bool> FastForwardPullAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default)
    {
        if (!await FetchAsync(workspacePath, prune: false, cancellationToken))
        {
            return false;
        }

        var result = await RunAsync(workspacePath, ["merge", "--ff-only", $"origin/{branchName}"], cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<bool> FastForwardMergeAsync(string workspacePath, string refName, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["merge", "--ff-only", refName], cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<bool> IsAncestorAsync(string workspacePath, string ancestorRef, string descendantRef, CancellationToken cancellationToken = default) =>
        (await RunAsync(workspacePath, ["merge-base", "--is-ancestor", ancestorRef, descendantRef], cancellationToken)).ExitCode == 0;

    public async Task<string> MergeBaseAsync(string workspacePath, string refA, string refB, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["merge-base", refA, refB], cancellationToken);
        return result.StandardOutput.Trim();
    }

    public async Task SquashSinceAsync(string workspacePath, string sinceRef, string message, CancellationToken cancellationToken = default)
    {
        await RunAsync(workspacePath, ["reset", "--soft", sinceRef], cancellationToken);
        await RunAsync(workspacePath, ["commit", "-m", message], cancellationToken);
    }

    public async Task<bool> StashPushAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["stash", "push", "-u"], cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<GitOperationOutcome> StashPopAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["stash", "pop"], cancellationToken);
        return await ClassifyOperationResultAsync(workspacePath, result, cancellationToken);
    }

    public async Task StashDropAsync(string workspacePath, CancellationToken cancellationToken = default) =>
        await RunAsync(workspacePath, ["stash", "drop"], cancellationToken);

    public async Task<string> RevParseAsync(string workspacePath, string refName, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["rev-parse", refName], cancellationToken);
        return result.StandardOutput.Trim();
    }

    public async Task<string> GetCommitSubjectAsync(string workspacePath, string refName, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["log", "-1", "--format=%s", refName], cancellationToken);
        return result.StandardOutput.Trim();
    }

    public async Task<DateTimeOffset> GetCommitDateAsync(string workspacePath, string refName, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["log", "-1", "--format=%cI", refName], cancellationToken);
        return DateTimeOffset.TryParse(result.StandardOutput.Trim(), out var date) ? date : DateTimeOffset.MinValue;
    }

    public async Task<IReadOnlyList<GitCommit>> LogAsync(string workspacePath, string refName, CancellationToken cancellationToken = default) =>
        await LogCommitsAsync(workspacePath, refName, cancellationToken);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<GitTag>>> GetTagsByCommitAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        const char sep = '\x1f';
        var result = await RunAsync(workspacePath,
            ["for-each-ref", $"--format=%(objectname){sep}%(*objectname){sep}%(refname:short){sep}%(objecttype){sep}%(contents:subject)", "refs/tags"],
            cancellationToken);

        var map = new Dictionary<string, List<GitTag>>();
        foreach (var line in SplitLines(result.StandardOutput))
        {
            var parts = line.Split(sep);
            if (parts.Length != 5)
            {
                continue;
            }

            // An annotated tag's own object hash (%(objectname)) is the tag object, not the commit it names -
            // %(*objectname) is git's own dereferenced-to-commit hash for that case, and empty for a plain
            // lightweight tag (whose %(objectname) already IS the commit hash).
            var commitHash = parts[1].Length > 0 ? parts[1] : parts[0];
            var name = parts[2];

            // %(contents:subject) for a lightweight tag (objecttype "commit") is actually the pointed-at
            // commit's own subject line, not anything belonging to the tag - only an annotated tag
            // (objecttype "tag") could have a real message of its own to show here, and even then only if it's
            // not one of AutoDev's own (CreateAnnotatedTagAsync always leaves the message blank); anything
            // else falls back to the tag's own short ref name.
            var isAnnotated = parts[3] == "tag";
            var displayName = isAnnotated && parts[4].Length > 0 ? parts[4] : name;

            if (!map.TryGetValue(commitHash, out var tags))
            {
                tags = [];
                map[commitHash] = tags;
            }

            tags.Add(new GitTag(name, displayName));
        }

        return map.ToDictionary(kv => kv.Key, IReadOnlyList<GitTag> (kv) => kv.Value);
    }

    public async Task<IReadOnlyList<GitChange>> GetCommitChangesAsync(string workspacePath, string commitHash, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["diff-tree", "--no-commit-id", "--name-status", "-r", "--root", commitHash], cancellationToken);

        var changes = new List<GitChange>();
        foreach (var line in SplitLines(result.StandardOutput))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            // A rename's status code carries a trailing similarity score ("R100") and two paths (old, new) -
            // every other status is a single letter with a single path. The new path is what the History tab's
            // changes tree should show either way.
            var status = parts[0][0] switch
            {
                'A' => GitChangeStatus.Added,
                'D' => GitChangeStatus.Deleted,
                'R' => GitChangeStatus.Renamed,
                _ => GitChangeStatus.Modified,
            };
            var path = parts[^1];
            changes.Add(new GitChange(path, status));
        }

        return changes;
    }

    public async Task<string?> GetFileContentAtCommitAsync(string workspacePath, string commitHash, string relativePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["show", $"{commitHash}:{relativePath}"], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    /// <summary>
    /// `-z` (NUL-terminated, unquoted paths) rather than plain `--porcelain` specifically so a path with a
    /// space or other special character can't be misread - plain porcelain output quotes/escapes those,
    /// which this method doesn't attempt to un-escape. A rename/copy status carries an extra NUL-terminated
    /// "original path" field right after its entry (consumed and discarded here, same simplification
    /// GetCommitChangesAsync's diff-tree parsing already makes) - without skipping it, every entry after the
    /// first rename would desync by one field.
    /// </summary>
    public async Task<IReadOnlyList<GitChange>> GetWorkingTreeChangesAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        // --untracked-files=all: without it, git collapses a wholly-new untracked directory into a single
        // "?? NewFolder/" entry instead of listing the files inside it - ChangeTreeNode.Build would then treat
        // that trailing-slash path as a single-segment leaf (a "file" named NewFolder with nothing to expand)
        // rather than a folder with children, exactly the bug this is guarding against.
        var result = await RunAsync(workspacePath, ["status", "--porcelain", "-z", "--untracked-files=all"], cancellationToken);
        var fields = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        var changes = new List<GitChange>();
        for (var i = 0; i < fields.Length; i++)
        {
            var entry = fields[i];
            if (entry.Length < 4)
            {
                continue;
            }

            var indexStatus = entry[0];
            var worktreeStatus = entry[1];
            var path = entry[3..];
            var isRenameOrCopy = indexStatus is 'R' or 'C' || worktreeStatus is 'R' or 'C';
            if (isRenameOrCopy)
            {
                i++; // the next field is the original path - unused, see doc comment
            }

            var status = (indexStatus, worktreeStatus) switch
            {
                ('?', '?') or ('A', _) => GitChangeStatus.Added,
                ('D', _) or (_, 'D') => GitChangeStatus.Deleted,
                _ when isRenameOrCopy => GitChangeStatus.Renamed,
                _ => GitChangeStatus.Modified,
            };
            changes.Add(new GitChange(path, status));
        }

        return changes;
    }

    public async Task<IReadOnlyList<GitCommit>> GetCommitsSinceAsync(string workspacePath, string baseRef, string branchRef, CancellationToken cancellationToken = default) =>
        await LogCommitsAsync(workspacePath, $"{baseRef}..{branchRef}", cancellationToken);

    public async Task<bool> HasUncommittedChangesAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workspacePath, ["status", "--porcelain"], cancellationToken);
        return result.StandardOutput.Trim().Length > 0;
    }

    public async Task DiscardChangesAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        await RunAsync(workspacePath, ["reset", "--hard"], cancellationToken);
        await RunAsync(workspacePath, ["clean", "-fd"], cancellationToken);
    }

    public async Task ResetHardAsync(string workspacePath, string commitHash, CancellationToken cancellationToken = default)
    {
        await RunAsync(workspacePath, ["reset", "--hard", commitHash], cancellationToken);
        await RunAsync(workspacePath, ["clean", "-fd"], cancellationToken);
    }

    private static async Task<IReadOnlyList<GitCommit>> LogCommitsAsync(string workspacePath, string revisionRange, CancellationToken cancellationToken)
    {
        const char sep = '\x1f';
        var result = await RunAsync(workspacePath, ["log", revisionRange, $"--format=%H{sep}%cI{sep}%s", "--reverse"], cancellationToken);

        var commits = new List<GitCommit>();
        foreach (var line in SplitLines(result.StandardOutput))
        {
            var parts = line.Split(sep);
            if (parts.Length == 3 && DateTimeOffset.TryParse(parts[1], out var date))
            {
                commits.Add(new GitCommit(parts[0], parts[2], date));
            }
        }

        return commits;
    }

    private async Task<GitOperationOutcome> ClassifyOperationResultAsync(string workspacePath, BufferedCommandResult result, CancellationToken cancellationToken)
    {
        if (result.ExitCode == 0)
        {
            return GitOperationOutcome.Succeeded;
        }

        return await HasConflictsAsync(workspacePath, cancellationToken) ? GitOperationOutcome.Conflicts : GitOperationOutcome.Failed;
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Every git invocation goes through here with the same safety env vars/flags, all aimed at the same goal:
    /// nothing this app runs may ever block waiting on interactive input that will never come. GIT_EDITOR/
    /// GIT_SEQUENCE_EDITOR prevent any command from ever blocking on an interactive editor (e.g. a rebase
    /// --continue that would otherwise want to open one). GIT_TERMINAL_PROMPT=0 stops git's own username/
    /// password prompt from hanging on a terminal that isn't there - deliberately *not* paired with a
    /// `-c credential.helper=` override to also disable the user's own configured credential helper: for the
    /// common case (a cached token, a system keychain, a GUI login flow the user already went through in a
    /// terminal) that helper answers non-interactively or via its own already-working UI, exactly as it would
    /// running the same command in a terminal - stripping it away here left every one of those setups unable
    /// to push/fetch/clone at all ("could not read Username ... terminal prompts disabled") despite working
    /// fine outside AutoDev, since GIT_TERMINAL_PROMPT=0 alone then had nothing left to fall back to. Only a
    /// helper that itself blocks on truly interactive terminal input would still hang here, same as it would
    /// in any other non-interactive context (CI, a script, ...) - not something AutoDev's own env can prevent
    /// or should try to paper over. GIT_SSH_COMMAND's BatchMode=yes is the SSH-transport equivalent of
    /// GIT_TERMINAL_PROMPT=0 (no password/passphrase prompt); StrictHostKeyChecking=accept-new keeps that from
    /// also blocking a legitimate first-time clone from a never-before-seen host on a host-key confirmation
    /// prompt, while still failing (rather than silently trusting) if a known host's key ever changes. Uses
    /// CliWrap's array-form arguments (never a shell string) so free-form user text - commit messages, feature
    /// summaries - is passed through literally and can never be shell-interpreted. Reports the command line
    /// and its output to GitCommandLogSink.Current, if set, for the busy overlay's live log.
    /// </summary>
    private static async Task<BufferedCommandResult> RunAsync(string workspacePath, IReadOnlyList<string> args, CancellationToken cancellationToken, PipeSource? standardInput = null)
    {
        var command = Cli.Wrap("git")
            .WithArguments(args)
            .WithWorkingDirectory(workspacePath)
            .WithEnvironmentVariables(env => env
                .Set("GIT_EDITOR", "true")
                .Set("GIT_SEQUENCE_EDITOR", "true")
                .Set("GIT_TERMINAL_PROMPT", "0")
                .Set("GIT_SSH_COMMAND", "ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new"))
            .WithValidation(CommandResultValidation.None);

        if (standardInput is not null)
        {
            command = command.WithStandardInputPipe(standardInput);
        }

        var sink = GitCommandLogSink.Current;
        sink?.Invoke($"$ git {string.Join(' ', args)}");

        var result = await command.ExecuteBufferedAsync(cancellationToken);

        if (sink is not null)
        {
            if (result.StandardOutput.Trim().Length > 0)
            {
                sink(result.StandardOutput.TrimEnd());
            }

            if (result.StandardError.Trim().Length > 0)
            {
                sink(result.StandardError.TrimEnd());
            }
        }

        return result;
    }
}
