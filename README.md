# AutoDev

AutoDev is a desktop IDE-shell for driving an AI coding CLI (Claude Code or Codex) against local
git repositories. It's a C#/.NET 10 Avalonia application (cross-platform, currently used on Linux)
that wraps a workspace's file tree, a text/markdown/hex editor, a git-branch workflow with an
opinionated naming convention, and a `.task` script runner around a headless AI CLI subprocess -
so day-to-day work (open a repo, target a branch, ask the AI to make a change, review/commit/merge
it) happens in one window without shelling out to a terminal.

## Using AutoDev

### Requirements

- `git` on `PATH` - checked on launch before anything else; AutoDev refuses to start at all without
  it, since there's no part of the app that doesn't eventually need to run a git command.
- The [`claude`](https://docs.claude.com/en/docs/claude-code) CLI and/or the
  [`codex`](https://github.com/openai/codex) CLI - AutoDev drives whichever one is currently
  selected as a subprocess rather than talking to either service's API directly. On first launch it
  checks both: if one is already installed and signed in it's used automatically; otherwise it
  offers a sign-in button for whichever CLI(s) it finds installed, or asks you to install one if
  neither is present.

### Opening a workspace

- The folder icon at the top-left of the title bar ("Open Folder…") opens an existing local git
  repository; the icon next to it ("Clone Repository…") clones a remote one into a folder you pick
  first. The chevron opens a dropdown of recently-opened workspaces ("Open Recent…").
- Each opened workspace becomes its own tab along the top of the window - open as many as you
  like side by side. Right-click a tab for **Move Left**/**Move Right** (reorder the strip) or
  **Close**.

### Choosing an AI provider

- The account/usage section at the top-right of the title bar is also a button - click it to
  switch which AI CLI (Claude or Codex) AutoDev drives for every workspace tab. What's shown there
  depends on the provider: Claude shows session/week rate-limit percentages; a provider with no
  such API (Codex) shows a cumulative token count instead.
- Switching providers starts a fresh conversation on the next message in every tab - a session id
  from one provider means nothing to the other.

### Sidebar

Each workspace tab has its own sidebar, split into two sections:

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
  right-click menus instead - see [Version Control](Docs/VersionControl.md).
- **Files** (bottom) - the workspace's file tree. Right-click an entry for New File/New Folder,
  Open (in the OS file manager), Copy Path, Rename, Duplicate, or Delete - a `.task` file also
  gets Run/Stop/View. A toggle switches the tree into "Changes Mode", showing only files with
  pending changes against the current target. Ignored/dimmed entries normally follow `.gitignore`;
  adding a `.fileignore` file at the workspace root (same pattern syntax - `#comments`, `!negation`,
  a trailing `/` for directories only, `*`/`?`/`**` wildcards) takes over from it entirely for this
  purpose, so you can hide things from the tree without touching what git itself tracks. A line
  reading just `$gitignore` pulls in `.gitignore`'s own patterns too. Only one `.task` file runs at
  a time per workspace - starting one while another is already running (or while a version action or
  the AI is working) is disabled - and a run in turn locks manual editing, tree mutations, every
  version action, and the AI, until it finishes; see [Task Automation](Docs/TaskAutomation.md).

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
  same way a Merge/Rebase conflict is resolved (see below) - that turn can only ever be paused and
  resumed, never stopped or cancelled, to avoid ever forcibly leaving the repository mid-conflict.
  The fetch (with prune) button above the timeline does the fetch part on demand without switching
  away and back, and never also pulls.
- **Output** (**F4**) - results from `.task` scripts you've run - see
  [Task Automation](Docs/TaskAutomation.md).
- **Command** (**F5**) - run an ad hoc shell command against the workspace and see its output; the
  input box keeps focus after each run, so you can keep typing the next one without reclicking it.

### Quick file search

Press **F1** to open a quick-open popup and fuzzy-search files by name; press F1 again to switch
it into full-text content search, and again to switch back. Enter opens the selected result;
Escape closes the popup.

## Start here

- **[Architecture](Docs/Architecture.md)** - process bootstrap, dependency injection, the MVVM
  view/view-model split, per-workspace-tab composition, and the top-level shell.
- **[Version Control](Docs/VersionControl.md)** - AutoDev's own thin `IGitService` wrapper around
  plain git, with no naming convention or invented identity on top, and the Version
  sidebar/History tab built on it.
- **[Workspaces & Files](Docs/Workspaces-and-Files.md)** - opening/cloning a workspace, recent-workspace
  and settings persistence, the file tree, and in-workspace file/content search.
- **[Editor](Docs/Editor.md)** - the Edit tab's five content modes (text, markdown preview, image, hex
  viewer, large-file warning) and Mermaid diagram rendering inside markdown.
- **[Claude Integration](Docs/ClaudeIntegration.md)** - how AutoDev talks to the `claude` CLI, the
  Generate tab's turn lifecycle, and the AI-assisted rebase/merge conflict-resolution loop.
- **[Task Automation](Docs/TaskAutomation.md)** - the `.task` scripting DSL, its runner, and the
  Output/Command tabs.
- **[UI & Theming](Docs/UI-and-Theming.md)** - dialogs, the VS-Code-Dark+-style theme, icons, and shared
  Avalonia conventions.

## Repository layout

```
AutoDev/
├── AutoDev.slnx                             Solution file, references Client/ and Tests/
├── Docs/                                    This repo's own architecture/feature docs
├── Example.task                             Sample `.task` file
├── Client/                                  The AutoDev app itself
│   ├── AutoDev.csproj, app.manifest           Project file & Windows PE manifest
│   ├── Assets/                                 App icon
│   └── src/
│       ├── Program.cs, App.axaml(.cs), MainWindow.axaml(.cs), ViewLocator.cs   Composition root & shell
│       ├── Core/                Platform-agnostic domain/service layer (no Avalonia dependency)
│       │   ├── Models/           Plain data records (WorkspaceInfo, GitTarget, BranchSummary, TaskRunRecord, ...)
│       │   ├── Services/         GitService, WorkspaceVersioningService, FileTreeService, WorkspaceTaskSchedulerService, ...
│       │   └── Serialization/    System.Text.Json source-gen context
│       ├── AiCli/                Provider-agnostic AI session/usage/auth abstractions (IAiSessionClient, ...)
│       │   └── Models/            Shared stream-event/data types both providers below translate into
│       ├── ClaudeCli/            Subprocess bridge to the `claude` CLI (auth, usage, session streaming)
│       │   ├── Models/             Claude's own stream-json event/data types
│       │   └── Serialization/      Hand-written JSON converters for the stream-json protocol
│       ├── CodexCli/             Subprocess bridge to the `codex` CLI (one process per turn, unlike Claude's)
│       ├── ViewModels/           CommunityToolkit.Mvvm view models, mirrors Views/ 1:1
│       │   ├── Content/            Edit/Generate/History/Output/Command tab VMs + WorkspaceContentViewModel
│       │   ├── Sidebar/            Files/Version sidebar sections, file search
│       │   ├── Dialogs/            Modal dialog VMs (Input, Confirm, Create Tag)
│       │   └── Infrastructure/     IDialogService/IUiDispatcher abstractions (kept Avalonia-free)
│       ├── Views/                 Avalonia .axaml views, one per ViewModel, same folder layout
│       ├── Infrastructure/        Avalonia-specific implementations of the above abstractions
│       ├── Converters/             XAML value converters
│       └── Styles/                 Theme resource dictionaries (colors, icons, control styles)
└── Tests/                                   Tests.csproj - xUnit tests for Client's components
```

## Conventions worth knowing before reading the rest

- **MVVM throughout**, via `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`).
  Views are resolved from view models purely by name (`AutoDev.ViewModels.Foo.BarViewModel` →
  `AutoDev.Views.Foo.BarView`) - see [Architecture](Docs/Architecture.md).
- **Explicit, manual DI registration** in `App.axaml.cs` (`Microsoft.Extensions.DependencyInjection`,
  all singletons) - there is no reflection-based auto-registration, despite the interface/
  implementation naming always lining up (`IThing` → `Thing`).
- **Every workspace tab is fully isolated**: its own file watcher, task scheduler, AI session
  client, and git-versioning service instance, composed fresh per tab by
  `WorkspaceTabFactory`. Nothing about one open workspace leaks into another.
- **Git is the source of truth** for almost everything - branch/tag identity, task run gating,
  read-only edit state - rather than a separate app-level database. AutoDev invents no naming
  convention of its own on top; see [Version Control](Docs/VersionControl.md).
