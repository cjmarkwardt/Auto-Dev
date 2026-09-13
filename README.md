# AutoDev

[![Latest release](https://img.shields.io/github/v/release/Markwardt-Labs/Auto-Dev?label=Release)](https://github.com/Markwardt-Labs/Auto-Dev/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Build](https://github.com/Markwardt-Labs/Auto-Dev/actions/workflows/build.yml/badge.svg)](https://github.com/Markwardt-Labs/Auto-Dev/actions/workflows/build.yml)
[![Coverage](.github/badges/badge_linecoverage.svg)](.github/badges/badge_linecoverage.svg)

AutoDev is a desktop IDE-shell for driving an AI coding CLI (Claude Code or Codex) against local
git repositories. It's a C#/.NET 10 Avalonia application (cross-platform, currently used on Linux)
that wraps a workspace's file tree, a text/markdown/hex editor, a git-branch workflow with an
opinionated naming convention, and a way to run single-file C# scripts, around a headless AI CLI subprocess -
so day-to-day work (open a repo, target a branch, ask the AI to make a change, review/commit/merge
it) happens in one window without shelling out to a terminal.

## Requirements

- Linux, x86-64, or Windows 10/11, 64-bit. No separate .NET runtime install needed - the executable
  is self-contained.
- `git` on `PATH` - checked on launch before anything else; AutoDev refuses to start at all without
  it, since there's no part of the app that doesn't eventually need to run a git command.
- The [.NET SDK](https://dotnet.microsoft.com/download) on `PATH` - only needed to use the Files
  section's Run/Stop/View actions on a `.cs` file; not checked on launch, since nothing else in the
  app depends on it.
- The [`claude`](https://docs.claude.com/en/docs/claude-code) CLI and/or the
  [`codex`](https://github.com/openai/codex) CLI - AutoDev drives whichever one is currently
  selected as a subprocess rather than talking to either service's API directly. On first launch it
  checks both: if one is already installed and signed in it's used automatically; otherwise it
  offers a sign-in button for whichever CLI(s) it finds installed, or asks you to install one if
  neither is present.

## Installing

Download the executable for your platform from the latest
[Release](https://github.com/Markwardt-Labs/Auto-Dev/releases/latest):

- **Windows** - download `AutoDev.exe` and run it.
- **Linux** - download `AutoDev`, mark it executable (`chmod +x AutoDev`), and run it. A raw Linux
  executable carries no icon of its own (unlike a Windows `.exe`'s embedded PE resource) - app
  launchers, taskbars, and file managers only show one via a `.desktop` entry pointing at an icon
  installed into the user's icon theme. Run `App/Linux/install-desktop-entry.sh
  [path-to-AutoDev-executable]` (defaults to `~/Tools/AutoDev`) once after deploying the binary to
  install both; re-run it any time the binary moves to a new path.

## Using AutoDev

### Opening a workspace

- Each AutoDev window opens a single workspace at a time, and always starts empty rather than
  restoring whatever was open last. The empty state shows **Open** and **Clone** buttons (on the
  left of the content area) and a list of recently-opened workspaces (on the right) - click either
  button, or a recent entry, to open a workspace in this window.
- The icon button at the top-left of the title bar opens a brand-new, fully independent AutoDev
  window (also starting empty) - this is how you work in more than one workspace at once.

### Choosing an AI provider

- The account/usage section at the top-right of the title bar is also a button - click it to
  switch which AI CLI (Claude or Codex) AutoDev drives for the open workspace. What's shown there
  depends on the provider: Claude shows session/week rate-limit percentages; a provider with no
  such API (Codex) shows a cumulative token count instead.
- Switching providers starts a fresh conversation on the next message - a session id from one
  provider means nothing to the other.

### Sidebar

The open workspace has its own sidebar, split into two sections:

- **Version** (top) - a centered, passive display of the currently targeted branch/tag name and
  HEAD's own commit message/hash; pending changes show as an asterisk in the corner plus a
  highlighted background. Click it for Commit, Reset, Branch, Tag, Remote, and (while targeting a
  branch) Squash/Rebase/Merge - a busy action shows its own live git output log with a Cancel
  button that reverts it; a failed one keeps that log on screen (Cancel replaced by Confirm) instead
  of auto-closing, so it's never dismissed unread - only a successful action closes on its own. The
  very first action in a workspace also prompts for a git name/email if neither is configured yet
  (`git config --global`), rather than failing outright. Merging deletes the now-merged branch both
  locally and on the remote automatically. If a branch you're on gets deleted on the remote by
  someone else (or by this same cleanup, from another clone) and that reaches you via a fetch, you're
  detached at the commit it was on (pending changes untouched) and the stale local branch is deleted
  too, rather than left pointing nowhere. Checkout, Merge Into Current, Rebase
  Current Onto This, and Delete for any *other* branch/commit/tag live on the History tab's own
  right-click menus instead.
- **Files** (bottom) - the workspace's file tree. Right-click an entry for New File/New Folder,
  Open (in the OS file manager), Copy Path, Rename, Duplicate, or Delete - a `.cs` file also
  gets Run/Stop/View (double-clicking one runs it too). A toggle switches the tree into "Changes
  Mode", showing only files with pending changes against the current target. Ignored/dimmed entries
  normally follow `.gitignore`; adding a `.fileignore` file at the workspace root (same pattern
  syntax - `#comments`, `!negation`, a trailing `/` for directories only, `*`/`?`/`**` wildcards)
  takes over from it entirely for this purpose, so you can hide things from the tree - and from F1
  quick-open's own search, both filename and content - without touching what git itself tracks. A
  line reading just `$gitignore` pulls in `.gitignore`'s own patterns too. Only one `.cs` script runs
  at a time per workspace - starting one while another is already running (or while a version action
  or the AI is working) is disabled - and a run in turn locks manual editing, tree mutations, every
  version action, and the AI, until it finishes.

### Working with the AI (Generate tab)

- Press **F2** (or click the Generate tab) to jump to the chat panel: type a request and press
  Enter to send it (Shift+Enter for a newline), or paste/drag in images and files to attach them.
- While a request is in flight, the status bar along the bottom of the window turns blue ("AI work
  in progress…") and the sidebar/Edit tab go read-only until it finishes. Three buttons cover
  stopping it: **Cancel** asks the AI to stop and revert what it's changed this turn (in the
  background - the turn keeps running until it actually finishes doing that); **Stop** kills it
  immediately with no cleanup; **Pause** also stops it immediately, but keeps it resumable - the
  status bar switches to "AI is paused" (same blue highlight, workspace still locked) and Pause
  itself is replaced by **Resume**, which continues the exact same turn from where it left off,
  even across restarting AutoDev entirely. Closing the app or workspace while a request is still in
  flight is treated the same way - reopening it shows "AI is paused" rather than losing the turn.
  A merge-conflict-resolution turn (Merge/Rebase, or a stash-pop conflict from opening History -
  see below) shows its own panel here instead of a normal request, with only **Pause**/**Resume**
  offered - Cancel/Stop never are, so it can never be forcibly interrupted mid-resolution.
- The title bar's Templates popup registers/applies scaffolding templates - applying one submits a
  request here too, visible and progressing exactly like a typed message.
- The model and effort/reasoning-level dropdowns at the bottom of the tab apply starting with the
  next message sent - both lists depend on whichever AI provider is currently selected.
- Earlier requests in the same conversation stay in a short scrollback (◀/▶ at the top of the
  tab), each showing its own prompt, live status, and latest output (replaced as newer output
  arrives, not just the final reply once everything's done).

### Other tabs

- **Edit** (opens automatically when you click a file in the sidebar) - text editing with syntax
  highlighting, a markdown preview/edit toggle, image preview, and a hex view for large/binary
  files. Ctrl+F opens find-in-file; Alt+Left/Alt+Right step back/forward through recently opened
  files.
- **History** (**F3**) - a flat branch list (the checked-out branch shown in bold) plus the selected
  branch's own commit/tag timeline, where the current commit - and any tag pointing at it - is
  always marked with a blue dot, even while HEAD is detached at a tag or commit instead of a branch;
  left-click a commit/tag entry to see what it changed, or right-click a branch/commit/tag for
  Checkout, Merge Into Current, Rebase Current Onto This, or Delete. Opening the tab (including the
  very first time, when it opens with the workspace) always fetches with prune, and transparently
  pulls the current branch the moment its remote moves ahead - even with pending changes in the way,
  which are stashed first and popped back on top once the pull lands (untracked files included). If
  that pop conflicts with what was just pulled in, AutoDev switches to the Generate tab and starts
  an AI turn to resolve it, reconciling the stashed changes against the newly-pulled commits the
  same way a Merge/Rebase conflict is resolved (see above) - that turn can only ever be paused and
  resumed, never stopped or cancelled, to avoid ever forcibly leaving the repository mid-conflict.
  The fetch (with prune) button above the timeline does the fetch part on demand without switching
  away and back, and never also pulls.
- **Script** (**F4**) - results from `.cs` scripts you've run, and an input box to answer one that's
  currently blocked reading from stdin (e.g. `Console.ReadLine()`).
- **Command** (**F5**) - run an ad hoc shell command against the workspace and see its output; the
  input box keeps focus after each run, so you can keep typing the next one without reclicking it.

### Quick file search

Press **F1** to open a quick-open popup and fuzzy-search files by name; press F1 again to switch
it into full-text content search, and again to switch back. Enter opens the selected result;
Escape closes the popup.

## Documentation

| File | Covers |
|---|---|
| `Docs/Api.md` | The application's primary surface and end-to-end flow, from opening a workspace through committing a change. |
| `Docs/Usage.md` | Walkthroughs: a first AI-driven change, running a script, resolving a conflict, applying a template. |
| `Docs/Architecture.md` | Process bootstrap, dependency injection, the MVVM view/view-model split, per-workspace-tab composition, and the top-level shell. |
| `Docs/Project.md` | This repo's own tooling/workflow: `Scripts/`, publishing, CI. |
| `Docs/Components/` | One file per subsystem - version control, workspaces/files, the editor, AI integration, running scripts, UI/theming. |

## Conventions worth knowing before reading the rest

See `AGENTS.md` for the full coding-convention rulebook. A few points specific to how this app is
put together:

- **MVVM throughout**, via `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`).
  Views are resolved from view models purely by name (`AutoDev.ViewModels.Foo.BarViewModel` →
  `AutoDev.Views.Foo.BarView`).
- **Explicit, manual DI registration** in `App.axaml.cs` (`Microsoft.Extensions.DependencyInjection`,
  all singletons) - there is no reflection-based auto-registration, despite the interface/
  implementation naming always lining up (`IThing` → `Thing`).
- **The open workspace is fully isolated**: its own file watcher, script runner, AI session
  client, and git-versioning service instance, composed fresh by `WorkspaceFactory` whenever a
  workspace is opened.
- **Git is the source of truth** for almost everything - branch/tag identity, task run gating,
  read-only edit state - rather than a separate app-level database. AutoDev invents no naming
  convention of its own on top.
