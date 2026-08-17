# AutoDev

AutoDev is a desktop IDE-shell for driving an AI coding CLI (Claude Code or Codex) against local
git repositories. It's a C#/.NET 10 Avalonia application (cross-platform, currently used on Linux)
that wraps a workspace's file tree, a text/markdown/hex editor, a git-branch workflow with an
opinionated naming convention, and a `.task` script runner around a headless AI CLI subprocess -
so day-to-day work (open a repo, target a branch, ask the AI to make a change, review/commit/merge
it) happens in one window without shelling out to a terminal.

## Using AutoDev

### Requirements

- The [`claude`](https://docs.claude.com/en/docs/claude-code) CLI and/or the
  [`codex`](https://github.com/openai/codex) CLI, installed and signed in - AutoDev drives
  whichever one is currently selected as a subprocess rather than talking to either service's API
  directly, so it needs that CLI's own login to already be in place.
- `git`.

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

- **Version** (top) - shows the currently targeted branch/tag/commit and exposes the
  branch-workflow actions (Branch, Commit, Merge, Rebase, Squash, Tag, Reset, Rename, Set Remote)
  that make sense for whatever's targeted. See [Version Control](Docs/VersionControl.md) for the
  branch-naming convention this is built on.
- **Files** (bottom) - the workspace's file tree. Right-click an entry for New File/New Folder,
  Open (in the OS file manager), Copy Path, Rename, Duplicate, or Delete - a `.task` file also
  gets Run/Stop/View. A toggle switches the tree into "Changes Mode", showing only files with
  pending changes against the current target.

### Working with the AI (Generate tab)

- Press **F2** (or click the Generate tab) to jump to the chat panel: type a request and press
  Enter to send it (Shift+Enter for a newline), or paste/drag in images and files to attach them.
- While a request is in flight, the status bar along the bottom of the window turns blue ("AI work
  in progress…") and the sidebar/Edit tab go read-only until it finishes; a Cancel button stops it
  early.
- The model and effort/reasoning-level dropdowns at the bottom of the tab apply starting with the
  next message sent - both lists depend on whichever AI provider is currently selected.
- Earlier requests in the same conversation stay in a short scrollback (◀/▶ at the top of the
  tab), each showing its own prompt, live status, and final reply.

### Other tabs

- **Edit** (opens automatically when you click a file in the sidebar) - text editing with syntax
  highlighting, a markdown preview/edit toggle, image preview, and a hex view for large/binary
  files. Ctrl+F opens find-in-file; Alt+Left/Alt+Right step back/forward through recently opened
  files.
- **History** (**F3**) - a per-branch commit/tag timeline; click an entry to see what it changed
  or switch the workspace to it.
- **Output** (**F4**) - results from `.task` scripts you've run - see
  [Task Automation](Docs/TaskAutomation.md).
- **Command** (**F5**) - run an ad hoc shell command against the workspace and see its output.

### Quick file search

Press **F1** to open a quick-open popup and fuzzy-search files by name; press F1 again to switch
it into full-text content search, and again to switch back. Enter opens the selected result;
Escape closes the popup.

## Start here

- **[Architecture](Docs/Architecture.md)** - process bootstrap, dependency injection, the MVVM
  view/view-model split, per-workspace-tab composition, and the top-level shell.
- **[Version Control](Docs/VersionControl.md)** - the branch-naming convention AutoDev layers on top of
  plain git (base commits, parent/child branches, public vs. private), and the Version
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
│       │   ├── Models/           Plain data records (WorkspaceInfo, GitTarget, BranchInfo, TaskDocument, ...)
│       │   ├── Services/         GitService, WorkspaceVersioningService, FileTreeService, ScriptTaskRunner, ...
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
│       │   ├── Dialogs/            Modal dialog VMs (Input, Confirm, Create Branch)
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
- **Git is the source of truth** for almost everything - branch identity/parentage, task run
  gating, read-only edit state - rather than a separate app-level database. See
  [Version Control](Docs/VersionControl.md) for the specific convention this relies on.
