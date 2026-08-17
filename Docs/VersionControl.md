# Version Control

AutoDev layers an opinionated branch workflow on top of plain git, rather than inventing its own
storage. Everything - a branch's display name, its parent, whether it's public or private - is
derived from the git history itself, so the repo stays a perfectly normal git repo usable with any
other tool; AutoDev just reads/writes it through one specific convention.

## Layers

- **`Core/Services/IGitService`/`GitService`** - thin, safe wrapper around the `git` CLI (via
  `CliWrap`). Every call goes through one `RunAsync` helper that sets `GIT_EDITOR`/
  `GIT_SEQUENCE_EDITOR=true` (so nothing can ever block on an interactive editor) and
  `GIT_TERMINAL_PROMPT=0` (so a remote operation needing credentials fails fast instead of
  hanging). No business logic lives here - just running commands and parsing output.
- **`Core/Services/BranchConvention`** - the static naming/parsing convention (below).
- **`Core/Services/IWorkspaceVersioningService`/`WorkspaceVersioningService`** - the actual
  branch/release/feature workflow (Branch, Reset, Squash, Rebase, Merge, Rename, Commit), built on
  the two above.
- **`ViewModels/Sidebar/VersionSectionViewModel`** - the sidebar UI, plus the AI-assisted
  conflict-resolution loop for Rebase/Merge.
- **`ViewModels/Content/HistoryTabViewModel`** - a read-only branch/timeline browser over the same
  service.

## The branch convention

Right after a branch is created, an **empty commit** is made whose message alone is the branch's
entire identity record:

```
{name} ~[{parentId}>{star}{id}]
```

`star` is `*` iff the branch is **private**, else empty (public). For example, the auto-created
root branch's base commit is `Main ~[>main]` (id `main`, no parent, public); a private feature
branch named "Add dark mode" with id `add-dark-mode` branched from `main` would get
`Add dark mode ~*[main>add-dark-mode]`.

```csharp
// Core/Services/BranchConvention.cs
public static string BuildBaseCommitMessage(string name, string? parentId, bool isPublic, string id) =>
    $"{name} ~{(isPublic ? "" : "*")}[{parentId}>{id}]";
```

`TryParseBaseCommitMessage` is the inverse. A branch's id is literally its git branch name - no
`version/`/`feature/` prefixing - so `id` is directly usable as a git ref anywhere.

**Finding a branch's own base commit** (`FindBranchInfoByIdAsync`) walks that branch's history for
the newest commit whose message ends with `>{id}]` - searching by the *exact* id, not "the nearest
marker of any shape" (the public/private star sits right after `~`, well before `>{id}]`, so it
doesn't affect this search either way). This matters once a branch has been merged: after a
fast-forward Merge, the parent's tip commit *is* the just-merged child's own base-commit marker, so
a generic "nearest marker" search from the parent's tip would misattribute identity to the child.
Searching for the specific id skips past that straight to the parent's own marker further back.

`FindContainingBranchInfoAsync` is the looser sibling used only when there's no known id to search
for directly (a detached tag/commit) - it walks for *any* base-commit-shaped marker and resolves
whichever branch that position conceptually belongs to.

## Public vs. private branches (`isPublic`)

This is a collaboration model, not a naming convention. A **public** branch (e.g. `main`, or any
other long-lived branch meant for many users to work on at once) is never squashed or renamed away,
so its full history stays intact for everyone sharing it. A **private** branch is meant for exactly
one user at a time and is expected to be squashed and merged/deleted once done - its history is
disposable, not something other users depend on.

A branch created with `isPublic: false` (private - the default from the Create Branch dialog) is
treated as disposable/squashable: its whole history between the base commit and its tip is meant
to collapse into one commit before merging. `isPublic: true` opts a branch out of that -
`VersionActionState.CanSquash` and `CanRename` are both gated on `!IsPublic`:

```csharp
CanSquash: (hasPending || hasCommitsAfterBase) && !info.IsPublic,
CanRename: !info.IsPublic,
```

The auto-initialized `main` branch (below) is created with `isPublic: true` - it's the repo's
permanent, shared root, not a private branch meant to be squashed away.

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

    await git.CommitAsync(
        workspacePath,
        BranchConvention.BuildBaseCommitMessage("Main", parentId: null, isPublic: true, "main"),
        allowEmpty: true, cancellationToken);
    await git.RenameCurrentBranchAsync(workspacePath, "main", cancellationToken);

    // No-op if there's no "origin" yet (a plain new folder); for a repo that got here via cloning
    // an empty remote, "origin" is already configured, so this is what actually lands the new
    // main branch/base commit on the remote instead of leaving it sitting local-only.
    await git.PushAsync(workspacePath, "main", setUpstream: true, cancellationToken: cancellationToken);
}
```

So: open an empty folder, or clone an empty remote repo, and AutoDev transparently leaves you with
a `main` branch whose single base commit stages whatever was already in the folder (nothing, for a
truly empty clone) - ready to use with the rest of the branch workflow immediately, with the
initial state pushed back to the remote if one exists.

## The Version sidebar (`VersionSectionViewModel`)

State: `GitTarget? Target` (current branch/tag/commit + resolved `BranchInfo`),
`VersionActionState ActionState` (which buttons are enabled - one consolidated
`GetActionStateAsync()` call per refresh rather than each button querying git independently),
`IsBusy`/`IsAiWorking` combined into `IsInteractionBlocked` (locks the sidebar, Edit tab, and
History tab while true).

Commands, each wrapped in `RunBusyAsync` (sets `IsBusy`, flushes any pending Edit-tab autosave via
`FlushPendingEditBeforeMutation`, runs the action, always refreshes afterward):

| Command | What it does |
|---|---|
| `SetRemoteAsync` | Prompts for and configures the `origin` URL |
| `BranchAsync` | Opens the Create Branch dialog, creates the branch + its base commit |
| `ResetAsync` | Confirms, then discards pending changes (`git reset --hard` + `clean -fd`) |
| `SquashAsync` | Confirms, then collapses pending changes + commits since the base commit into one |
| `RebaseAsync` | Rebases onto the parent branch's tip, with AI-assisted conflict resolution (below) |
| `MergeAsync` | Same rebase path, then fast-forward-merges into the parent and deletes this branch |
| `RenameAsync` | Prompts for a new name, squashes, amends the base commit with the new name |
| `CommitAsync` | Prompts for a message, commits pending changes, pushes |

A background `periodicSyncTimer` (60s) posts a refresh (fetch/prune + reset any non-current local
branch to match its remote counterpart) whenever the section isn't currently locked.

### AI-assisted conflict resolution

Both `RebaseAsync` and `MergeAsync` share `ResolveRebaseConflictsAsync`. If a rebase reports
`RebaseOutcome.Conflicts`, the loop (up to 3 attempts):

1. Lists conflicted files (`GetConflictedFilesAsync`).
2. Builds an instruction ("here are the conflicted files, resolve the markers and `git add` the
   results") and drives it through **the same Claude session the Generate tab uses**, via
   `GenerateTabViewModel.RunAutomatedTurnAsync(instruction)` - not a separate process. See
   [Claude Integration](ClaudeIntegration.md).
3. Checks `HasConflictsAsync()` again; if still conflicted, retries (budget permitting); otherwise
   calls `ContinueRebaseAsync()`.

`IsBusy` is dropped to `false` for the duration of the actual Claude turn (only `IsAiWorking`
stays set) so the user can watch/interact with the Generate tab while it works, while the
surrounding git plumbing keeps its own busy overlay. On success the branch is force-pushed; on
exhausted attempts the rebase is aborted with a status message.

`VersionSectionViewModel` also listens to `GenerateTabViewModel`'s `NormalTurnStarted/Completed`
events directly - so a plain user-submitted Generate turn (nothing to do with conflict resolution)
also locks the Version section until the user reviews and commits, avoiding a race between Claude's
tool calls and a concurrent git mutation.

## History tab (`HistoryTabViewModel`)

A read-only browser built directly on the same service:

- `ListAllBranchesAsync()` → `BranchSummary(Id, Name, ParentId, IsPublic, IsCurrent)` per
  local branch, current-first then alphabetical.
- `GetBranchTimelineAsync(branchId)` → a `BranchTimeline` whose entries are: one `ChildLink` per
  branch forked from this one, one `Commit` entry per commit from the base commit to the tip
  (newest first), and a trailing `ParentLink` if this branch has a parent.

Clicking a `ParentLink`/`ChildLink` just navigates (`NavigateTo`, reassigns the selected branch, no
git action). Clicking a plain commit row (`CheckoutCommitAsync`) confirms discarding any pending
changes, then detaches HEAD there via `CheckoutRefAsync` - run through
`VersionSectionViewModel.RunBusyAsync`, so it locks the tab the same as any other Version action.
The timeline reloads automatically whenever `VersionSectionViewModel.TargetChanged` fires from
anywhere else in the app.
