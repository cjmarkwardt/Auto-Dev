# Version Control

AutoDev drives plain git directly, with no naming convention or invented identity layered on top.
A branch or tag's only identity is its own literal git ref name - the repo stays a perfectly normal
git repo usable with any other tool, and everything AutoDev shows or does maps to an ordinary git
command.

## Layers

- **`Core/Services/IGitService`/`GitService`** - thin, safe wrapper around the `git` CLI (via
  `CliWrap`). `IsInstalled` (`GitCliLocator`, checking `PATH` exactly like `ClaudeCliLocator`/
  `CodexCliLocator` do for their own CLIs) is checked once at launch by `AuthGateViewModel`, which
  refuses to start the app at all without it - every other member here would otherwise throw the
  moment it tried to launch a `git` process that doesn't exist. Every call goes through one
  `RunAsync` helper that sets `GIT_EDITOR`/`GIT_SEQUENCE_EDITOR=true` (so nothing can ever block on
  an interactive editor) and three separate things aimed at the same goal - a remote operation
  needing credentials AutoDev can't supply fails fast with a real error instead of hanging or
  popping up a login flow of its own: `GIT_TERMINAL_PROMPT=0` (git's own username/password prompt),
  `-c credential.helper=` (an external GUI credential helper/keychain prompt, which
  `GIT_TERMINAL_PROMPT` has no effect on, for this invocation only), and `GIT_SSH_COMMAND` with
  `BatchMode=yes` (the SSH-transport equivalent) plus `StrictHostKeyChecking=accept-new` (so that
  doesn't also block a legitimate first-time clone from a never-before-seen host). No business
  logic lives here - just running plain git commands and parsing output.
- **`Core/Services/IWorkspaceVersioningService`/`WorkspaceVersioningService`** - the actual
  branch/tag workflow (Checkout, Branch, Tag, Delete, Reset, Rebase, Merge, Squash, Commit), built
  directly on the above.
- **`ViewModels/Sidebar/VersionSectionViewModel`** - a passive display of the current target plus
  the shared busy/lock state every action runs through, and the AI-assisted conflict-resolution
  loop for Rebase/Merge.
- **`ViewModels/Content/HistoryTabViewModel`** - a read-only branch/timeline browser over the same
  service, and the home of every action itself (see below) as a right-click context menu.

## Auto-initializing a repo

`WorkspaceTabViewModel.InitializeAsync()` calls `VersionSectionViewModel.EnsureRepoAsync()` on
every workspace open:

```csharp
public async Task EnsureRepoAsync()
{
    if (!await versioningService.IsRepoInitializedAsync())
    {
        await RunBusyAsync(() => versioningService.InitializeRepoAsync());
    }
    else
    {
        await RefreshAsync();
        await versioningService.EnsureLocalGitExcludeAsync();
    }

    periodicSyncTimer.Start();
}
```

`IsRepoInitializedAsync` is deliberately stricter than "does `.git` exist":

```csharp
public async Task<bool> IsRepoInitializedAsync(CancellationToken cancellationToken = default)
{
    if (!await git.IsRepoAsync(workspacePath, cancellationToken))
    {
        return false;
    }

    return await git.HasCommitsAsync(workspacePath, cancellationToken);
}
```

`HasCommitsAsync` checks the *exit code* of `git rev-parse --verify --quiet HEAD` rather than
inspecting its output - `git rev-parse HEAD` on an unborn branch prints the literal text `"HEAD"`
back to stdout instead of nothing, so a naive "is the output non-empty" check silently reports
"has commits" even when it doesn't. This distinction is what makes **cloning an empty remote
repository** behave identically to **opening a brand-new folder with no git repo at all**: a fresh
clone of an empty remote is a real git work tree (so `IsRepoAsync` alone would say "yes,
initialized") but has zero commits, so `EnsureRepoAsync` still routes it through
`InitializeRepoAsync`.

```csharp
public async Task InitializeRepoAsync(CancellationToken cancellationToken = default)
{
    await git.InitAsync(workspacePath, cancellationToken);           // git init - safe no-op if .git exists
    await EnsureLocalGitExcludeAsync(cancellationToken);              // .git/info/exclude += .autodev/local/

    await git.CommitEmptyAsync(workspacePath, "Initial commit", cancellationToken);
    await git.RenameCurrentBranchAsync(workspacePath, "main", cancellationToken);

    // No-op if there's no "origin" yet (a plain new folder); for a repo that got here via cloning
    // an empty remote, "origin" is already configured, so this is what actually lands the new
    // main branch on the remote instead of leaving it sitting local-only.
    await git.PushAsync(workspacePath, "main", setUpstream: true, cancellationToken: cancellationToken);
}
```

So: open an empty folder, or clone an empty remote repo, and AutoDev transparently leaves you with
a plain `main` branch whose single "Initial commit" is genuinely empty (`git commit --allow-empty`,
no `git add` first - `IGitService.CommitEmptyAsync`) - ready to use immediately, with that empty
state pushed back to the remote if one exists. Anything already sitting in the folder (a non-empty
new folder, or a clone that already had local-only files) is left as pending, uncommitted content
instead of being silently swept into that first commit - the user commits it explicitly afterward,
same as any other edit.

## The Version sidebar (`VersionSectionViewModel`)

A passive display plus one click-to-open menu. It shows whatever `GitTarget Target` currently is
(`BranchName`/`TagName` where applicable, plus HEAD's own `CommitMessage`/`CommitHash`, always
present), all centered - see `VersionSectionView`. Pending changes (`HasPendingChanges`) show as a
small asterisk in the section's top-right corner and a highlighted background over the whole
section, rather than a separate badge.

Clicking anywhere on the section opens a `MenuFlyout` with every action that targets the currently
checked-out branch directly, rather than some other row in the History tab:

| Menu item | What it does | Shown |
|---|---|---|
| Commit | Prompts for a message, commits pending changes, pushes | always |
| Reset | Confirms, then discards pending changes (`git reset --hard` + `clean -fd`) | always |
| Branch | Prompts for a name, creates a branch at the current target (`HEAD`), checks it out | always |
| Tag | Prompts for a name, creates an annotated tag (blank message) at the current target | always |
| Remote | Prompts for and configures the `origin` URL | always |
| Squash | Prompts for a base branch and message, squashes since diverging from it | targeting a branch |
| Rebase | Prompts for an onto-branch and (always-applied) squash message, rebases the current branch | targeting a branch |
| Merge | Prompts for a target branch, fast-forward merges the current branch onto it | targeting a branch |

Squash/Rebase/Merge are only offered while `Target.Kind == GitTargetKind.Branch` - all three need a
"current branch" to make sense of, so they stay hidden while HEAD is detached at a tag or arbitrary
commit. A failed action (a branch/tag name collision, an unresolvable rebase, a merge that isn't
actually fast-forwardable) shows an OK popup (`IDialogService.ShowMessageDialogAsync` -
`MessageDialogViewModel`) rather than a persistent inline label.

It also owns the shared busy/lock machinery every action (Commit/Reset/Branch/Tag/Remote/Squash/
Rebase/Merge here, everything else triggered from the History tab - see below) runs through:

- `IsBusy`/`IsAiWorking` combined into `IsInteractionBlocked`, which locks the sidebar, Edit tab,
  and History tab's action commands while true.
- `GitOutputLog` - the current action's own live git command log (each command line plus its
  output), shown in the busy overlay - see "The busy overlay" below.
- `RunBusyAsync(action)` - sets `IsBusy`, clears `GitOutputLog`, captures a pre-action
  `GitActionSnapshot` (`CaptureSnapshotAsync`), flushes any pending Edit-tab autosave via
  `FlushPendingEditBeforeMutation`, then runs `action` with a fresh `CancellationToken` (see below).
  Always refreshes (`RefreshAsync`) afterward, whether `action` succeeded, failed, or was cancelled.
- `ResolveConflictsAsync(outcome, continueAction, cancellationToken)` - the AI-assisted
  conflict-resolution loop shared by this section's own Rebase and the History tab's
  Merge/Rebase-onto-this (below).
- A background `periodicSyncTimer` (60s) posts a refresh (fetch/prune + reset any non-current local
  branch to match its remote counterpart) whenever the section isn't currently locked.

### The busy overlay: live git output log and Cancel

`WorkspaceTabView`'s busy overlay (bound to `Version.IsBusy`) shows, alongside the usual
indeterminate progress bar: a scrolling, auto-following log of every git command the current action
runs (command line plus stdout/stderr - see `GitCommandLogSink` below) and a Cancel button
(`Version.CancelBusyCommand`).

`GitCommandLogSink` is a small `AsyncLocal<Action<string>?>`-backed static class, not a persistent
event subscription on `IGitService` (a shared, app-wide singleton) - `RunBusyAsync` sets
`GitCommandLogSink.Current` right before calling `action`, and `GitService`'s own `RunAsync` helper
reports to whatever `Current` is (if anything) after every command. Being `AsyncLocal` means it
flows automatically into everything that single action call awaits, and needs no unsubscription
when a workspace tab closes - the alternative (an event on the shared `IGitService`) would leak a
handler referencing a dead `VersionSectionViewModel` every time a tab closed and reopened over the
app's lifetime.

Clicking Cancel signals `RunBusyAsync`'s own `CancellationTokenSource`, whose token every action
lambda threads into whichever `IWorkspaceVersioningService` calls it makes (all of them already
accept a trailing `CancellationToken`) - all the way down to `GitService.RunAsync`'s
`ExecuteBufferedAsync(cancellationToken)`, so cancelling can actually kill an in-flight git
subprocess, not just race to be first past a check. `RunBusyAsync` catches the resulting
`OperationCanceledException` and calls `RevertToSnapshotAsync(snapshot)` - a generic, action-agnostic
undo: abort any in-progress rebase/merge, check out the pre-action branch again if a different one
ended up checked out, then hard-reset it back to the pre-action commit hash and discard pending
changes. It doesn't know or care which specific action it's undoing; every mutating action's own
effect reduces to "the checked-out branch moved and/or its tip advanced", which this reverses. A
normal git failure (bad credentials, no permission, a rejected push, ...) never reaches this at all
- it comes back as an ordinary `false`/`GitOperationOutcome.Failed` result, which each action's own
caller turns into its own specific message (e.g. Rebase's "Rebase failed."). `RunBusyAsync` also
catches any *other* exception as a backstop - reverts the same way, logs it to `GitOutputLog`, and
shows a generic "The git action failed unexpectedly" popup - so something truly unexpected (not a
normal git failure, which the `RunAsync` overrides above mean should never throw in the first place)
fails as visibly as any other action failure rather than crashing the app.

The Cancel button only matters while `IsBusy` is actually up - during a Rebase/Merge conflict's own
AI-resolution turn (below), `IsBusy` is deliberately dropped so the user can watch/interact with the
Generate tab, which has its own Cancel for that part of the flow.

### Squash, Rebase, and Merge's branch pickers

Squash and Rebase (`SquashDialogViewModel`/`RebaseDialogViewModel`) pick from
`IWorkspaceVersioningService.GetEligibleBaseBranchesAsync()` - every local branch except the
current one and any that's already a git ancestor of it (`IGitService.IsAncestorAsync`). An
ancestor branch is excluded because both actions would be a no-op/degenerate against it: rebasing
onto a branch you're already built on top of replays nothing, and squashing back to it just
reproduces the same commits some other reachable point already represents.

Merge (`MergeDialogViewModel`) picks from the *opposite* filter -
`GetEligibleMergeTargetBranchesAsync()` returns only branches that current **is** already an
ancestor-relationship away from being fast-forwardable onto (i.e. branches current is strictly
ahead of) - the mirror image of Squash/Rebase's list, since a fast-forward is only possible in that
direction. `FastForwardMergeAsync` re-validates this itself (`merge-base(current, target) ==
target's own head`) before touching anything and fails outright - via the same OK popup as any
other failed action - if it doesn't hold; it never conflicts, since a fast-forward that isn't
possible simply doesn't happen instead of falling back to a real merge commit.

Whichever branch Squash/Rebase picks, the actual squash boundary is `git merge-base(current,
picked)` - *not* necessarily the picked branch's own tip - so history still shows the current
branch forking from the true common ancestor, just with its own commits collapsed to one
(`IGitService.SquashSinceAsync` = `git reset --soft <merge-base>` + `git commit`, which leaves the
tree/index untouched and only changes how many commits it took to get there). The default message
offered (`GetDefaultSquashMessageAsync`) is the subject of the first commit unique to the current
branch since that merge-base (`IGitService.GetCommitsSinceAsync(mergeBase, "HEAD")[0]`).

Rebase always squashes first (`RebaseWithSquashAsync`, no separate toggle - a rebase can't offer a
meaningful per-commit AI conflict-resolution loop otherwise, since each resolution attempt only
gets one shot at the whole diff, not one per original commit) against whichever branch was picked,
so only that one squashed commit ever gets replayed. Merge squashes too, but only conditionally -
`FastForwardMergeAsync` counts the commits since the merge-base and only calls `SquashSinceAsync`
if there's more than one; fast-forwarding an already-single commit needs no rewrite.

## Actions - right-click a branch, commit, or tag in the History tab

Every git action that targets some *other* row instead of the current branch itself lives on a
context menu, not a sidebar button - see `HistoryTabViewModel`/`HistoryTabView.axaml`. Each one
runs through `VersionSectionViewModel.RunBusyAsync`, so it locks the same controls the Version
section's own actions do.

**Branch row** (nothing is shown for the current branch's own row - see
`HistoryTabView.OnBranchContextRequested`, which suppresses the popup entirely for it):

| Menu item | What it does |
|---|---|
| Checkout | `git checkout <branch>` (confirms discarding pending changes first) |
| Merge Into Current | `git merge <this branch>` into whatever's checked out |
| Rebase Current Onto This | `git rebase <this branch>` |
| Delete | `git branch -D` |

**Commit row:** Checkout (detaches HEAD there).

**Tag row:** Checkout, Delete Tag (`git tag -d`).

A Commit/Tag row's *left*-click expands/collapses its changed-files view in place
(`ToggleExpandedCommand`) - a right-click only opens the context menu above, never also toggling
the changes view (see `HistoryTabView.OnEntryPointerPressed`, which checks
`PointerPressedEventArgs.GetCurrentPoint(null).Properties.IsLeftButtonPressed` before acting).

### AI-assisted conflict resolution

The History tab's Merge/Rebase-onto-this items, and the Version section's own Rebase, all share
`VersionSectionViewModel.ResolveConflictsAsync`. If the initial attempt reports
`GitOperationOutcome.Conflicts`, the loop (up to 3 attempts):

1. Lists conflicted files (`GetConflictedFilesAsync`).
2. Builds an instruction ("here are the conflicted files, resolve the markers and `git add` the
   results") and drives it through **the same Claude session the Generate tab uses**, via
   `GenerateTabViewModel.RunAutomatedTurnAsync(instruction)` - not a separate process. See
   [Claude Integration](ClaudeIntegration.md).
3. Checks `HasConflictsAsync()` again; if still conflicted, retries (budget permitting); otherwise
   calls whichever continuation the caller passed in (`ContinueRebaseAsync`/`ContinueMergeAsync`).

`IsBusy` is dropped to `false` for the duration of the actual Claude turn (only `IsAiWorking`
stays set) so the user can watch/interact with the Generate tab while it works, while the
surrounding git plumbing keeps its own busy overlay. On success the branch is force-pushed; on
exhausted attempts the operation is aborted (`AbortRebaseAsync`/`AbortMergeAsync`) with an OK popup
explaining it couldn't be resolved automatically. The Version section's own Merge action never goes
through this loop at all - a fast-forward either applies cleanly or fails outright, with no
merge-conflict state to resolve.

`VersionSectionViewModel` also listens to `GenerateTabViewModel`'s `NormalTurnStarted/Completed`
events directly - so a plain user-submitted Generate turn (nothing to do with conflict resolution)
also locks every History tab action until the user reviews and commits, avoiding a race between
Claude's tool calls and a concurrent git mutation.

## History tab (`HistoryTabViewModel`)

A read-only browser built directly on the same service, plus every action above:

- `ListAllBranchesAsync()` → `BranchSummary(Name, IsCurrent)` per local branch, current-first then
  alphabetical - shown as a flat list (`BranchRows`), no parent/child hierarchy.
- `GetBranchTimelinePageAsync(branchName, pageIndex)` → a `BranchTimelinePage` of that branch's own
  plain commit history (newest first, 100 per page), with a `Tag` entry inserted immediately above
  whichever commit it points at.

Left-clicking a branch row navigates the timeline to it (`SelectBranch`, no git action). The
timeline reloads automatically whenever `VersionSectionViewModel.TargetChanged` fires from
anywhere else in the app.
