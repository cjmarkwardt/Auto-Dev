# Architecture

## Process bootstrap

**`Program.cs`** is a standard Avalonia entry point: `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`.
`BuildAvaloniaApp()` configures `.UsePlatformDetect()`, `.WithInterFont()`, and:

```csharp
.With(new X11PlatformOptions
{
    UseDBusFilePicker = false,
    UseDBusMenu = false,
    OverlayPopups = true,
})
```

`UseDBusFilePicker`/`UseDBusMenu` are off because AutoDev has its own `AvaloniaDialogService` folder
picker and no native menu/tray to integrate with. `OverlayPopups = true` renders popups (context
menus, combo box drop-downs, etc.) inside their owning window's own surface instead of as separate
X11 windows - without it, popups get their own GPU surface, and rapid open/close cycles or a
workspace/compositor switch can leave that surface stuck and un-repainting until the process (or
machine) restarts. This is a load-bearing setting, not cosmetic - keep it set on any new
`X11PlatformOptions` block rather than replacing it.

**`App.axaml.cs`** is the composition root. `OnFrameworkInitializationCompleted()`:

1. Builds the DI container (see [Dependency injection](#dependency-injection) below).
2. Resolves `MainWindowViewModel`, creates `new MainWindow { DataContext = ... }`.
3. Wires `desktop.ShutdownRequested`: the first request sets `e.Cancel = true` and awaits
   `MainWindowViewModel.ShutdownAsync()` (persists the open-workspace list, disposes every tab);
   the resulting second request actually shuts down.
4. Registers `AppDomain.CurrentDomain.ProcessExit` as a backstop that disposes the DI container
   directly, for exit paths (SIGTERM, a crash) that never reach `ShutdownRequested`.
5. Kicks off `MainWindowViewModel.InitializeAsync()` (auth check, then workspace restore)
   fire-and-forget once the window is showing.

**`ViewLocator.cs`** implements Avalonia's `IDataTemplate`. It matches any `ViewModelBase` and
resolves its view by pure string substitution on the full type name:
`AutoDev.ViewModels.Content.EditTabViewModel` → `AutoDev.Views.Content.EditTabView`, then
`Type.GetType(...)` + `Activator.CreateInstance`. Views are parameterless and never DI-constructed;
if a matching type can't be found, a "View not found" `TextBlock` is shown instead of throwing.

## Dependency injection

Package: `Microsoft.Extensions.DependencyInjection`. Registration is **entirely explicit and
manual** - there is no assembly-scanning/convention-based auto-registration. Every line in
`App.BuildServiceProvider()` looks like:

```csharp
services.AddSingleton<IGitService, GitService>();
```

grouped under comment banners (`// Core`, `// Claude CLI bridge`, `// App infrastructure`,
`// ViewModels`). Every registration is `AddSingleton` - there are no scoped or transient
lifetimes anywhere in this container. Top-level view models (`HeaderViewModel`,
`AuthGateViewModel`, `MainShellViewModel`, `MainWindowViewModel`) are registered as concrete
types, since they're resolved directly rather than through an abstraction.

The one place the flat singleton list doesn't apply is **per-workspace-tab state**, which by
definition needs a fresh instance every time a workspace is opened. That's `IWorkspaceTabFactory`
(`ViewModels/IWorkspaceTabFactory.cs`):

```csharp
public sealed class WorkspaceTabFactory(/* ~14 shared singletons */) : IWorkspaceTabFactory
{
    public WorkspaceTabViewModel Create(WorkspaceInfo workspace)
    {
        var versioningService = versioningServiceFactory.Create(workspace.FullPath);
        var scheduler = schedulerFactory.Create(workspace.FullPath);
        var files = new FilesSectionViewModel(workspace.FullPath, fileTreeService, watcherFactory, ...);
        var edit = new EditTabViewModel(fileTreeService);
        var generate = new GenerateTabViewModel(workspace.FullPath, sessionClientFactory, ...);
        var version = new VersionSectionViewModel(versioningService, dialogService, generate, dispatcher);
        var history = new HistoryTabViewModel(versioningService, version, dialogService);
        var output = new OutputTabViewModel(workspace.FullPath, metadataStore, scheduler, dispatcher);
        var command = new CommandTabViewModel(workspace.FullPath, commandExecutor, dispatcher);
        var content = new WorkspaceContentViewModel(edit, generate, history, output, command);
        var fileSearch = new FileSearchViewModel(workspace.FullPath, gitService);

        return new WorkspaceTabViewModel(workspace, version, files, content, fileSearch);
    }
}
```

Every argument the factory itself takes is a stateless, process-wide singleton (or a `*Factory`
that produces per-workspace instances, like `IVersioningServiceFactory`/`ITaskSchedulerServiceFactory`/
`IClaudeSessionClientFactory`); all the statefulness lives in what `Create` builds. This is what
gives each open workspace tab its own isolated file watcher, task scheduler, Claude session
subprocess, and git-versioning service - closing one tab and its subprocess/watcher can't affect
another tab's.

## The shell: `MainShellViewModel` and workspace tabs

`MainShellViewModel` owns a `HeaderViewModel` and an `ObservableCollection<WorkspaceTabViewModel>`.
It subscribes to `Header.WorkspaceOpened`: if the opened path matches an already-open tab (by
`Workspace.FullPath`), that tab is just re-selected; otherwise `IWorkspaceTabFactory.Create` builds
a new one, it's added and selected, and `tab.InitializeAsync()` runs.

Startup restore (`MainShellViewModel.InitializeAsync`, called after auth succeeds) re-opens every
workspace from `IWorkspaceService.GetOpenWorkspacesAsync()` - **sequentially**, not
`Task.WhenAll`, because the underlying settings read-modify-write
(`JsonSettingsService`/`AppSettings.RecentWorkspacePaths`) isn't safe under concurrent callers and
would drop entries if opened in parallel.

Closing a tab computes the next `SelectedTab` **before** removing the closed one from `Tabs`
(removing the currently-selected item would otherwise null `SelectedTab` as a side effect of the
binding), then disposes it inside a try/catch so a stuck subprocess or locked file during disposal
can't crash the whole app.

**`WorkspaceTabViewModel`** owns four sub-view-models - `Version` (git sidebar), `Files` (file
tree sidebar), `Content` (the right-hand tabs), `FileSearch` (the F1 overlay) - and wires them
together with plain C# events in its constructor, not a shared mediator/message bus:

| Source | Event | Effect |
|---|---|---|
| `Files` | `FileSelected` | `Content.OpenFileAsync(path)` |
| `Files` | `WorkspaceFilesChanged` | `Content.Edit.CheckForExternalChangesAsync()` |
| `Files` | `TaskOutputRequested` | selects the task in `Content.Output`, switches to the Output tab |
| `FileSearch` | `FileChosen` | `Files.SelectPath(path)` (→ itself raises `FileSelected`) |
| `FileSearch` | `ContentResultChosen` | `Content.OpenFileAsync(path, line)` directly (bypasses `Files`, to avoid a race on the seek position) |
| `Version` | `TargetChanged` / `IsInteractionBlocked` change | recomputes editable state, pushed into `Files`/`Content` |

`Version.FlushPendingEditBeforeMutation` and `Files.FlushPendingEditBeforeRun` are callback
delegates (not events) pointed at `Content.Edit.FlushPendingSaveAsync`, so a git action or a task
run can force-save a dirty editor buffer first rather than racing the editor's own debounced
autosave.

`WorkspaceTabViewModel.InitializeAsync()` calls `Version.EnsureRepoAsync()` (which silently
`git init`s an un-versioned folder into AutoDev's branch convention - see
[Version Control](VersionControl.md)) then `Content.Output.LoadAsync()`. `DisposeAsync()` flushes
pending edits and disposes `Content`, `Files`, `Version` in that order.

## Read-only editing and per-tab layout

**`WorkspaceContentViewModel`** is the right-hand pane: `Edit` (always visible, not a switchable
tab) plus four indexed tabs - `GenerateTabIndex = 0`, `HistoryTabIndex = 1`, `OutputTabIndex = 2`,
`CommandTabIndex = 3` (these are also what `MainWindow.axaml.cs`'s global `F2`-`F5` shortcuts
target).

Editability is computed from the currently targeted `GitTarget`:

```csharp
public bool IsEditableTarget => _lastTarget?.Kind == GitTargetKind.Branch;
```

Only a branch is editable - a tag or a detached commit is a read-only historical snapshot.
`UpdateEditReadOnly()` combines that with busy/AI-working state:

```csharp
Edit.IsReadOnly = _isBusy || _isAiWorking || !IsEditableTarget;
```

`ComputeReadOnlyReason()` picks the user-facing explanation in priority order: AI currently
working → a version action in progress → the target-kind reason (tag/commit/no branch). Changing
target also re-keys the Generate tab's conversation: `ApplyTargetStateAsync` calls
`Generate.SwitchSessionAsync(branchId)`, so each branch has its own independent chat history.

## Global keyboard shortcuts

`MainWindow.axaml.cs` installs a tunnel-priority `KeyDown` handler (so it fires before any
descendant control, e.g. a text box, can consume the key first):

- `F1` - toggle the active tab's file/content search overlay.
- `F2`-`F5` - switch to Generate/History/Output/Command.
- All of the above are swallowed while `Version.IsBusy` (a full-screen loading overlay is up for
  a git action).

`MainWindow.axaml` itself has no native window chrome (`WindowDecorations="None"`) and switches
its whole content between an `AuthGate` view and the `Shell` view based on
`MainWindowViewModel.IsAuthenticated`.

## Settings and workspace metadata persistence

Two separate, deliberately-scoped persistence layers:

- **Global app settings** - `ISettingsService`/`JsonSettingsService`
  (`Core/Services/JsonSettingsService.cs`), one JSON file at
  `~/.config/AutoDev/settings.json` (`Environment.SpecialFolder.ApplicationData`), holding
  `AppSettings { RecentWorkspacePaths, OpenWorkspacePaths }` - the app-wide MRU list and the exact
  set of tabs to restore on next launch. Serialized via a `System.Text.Json` source-generated
  context (`Core/Serialization/AppJson.cs`).
- **Per-workspace metadata** - `IWorkspaceMetadataStore`/`WorkspaceMetadataStore`
  (`Core/Services/WorkspaceMetadataStore.cs`), rooted at `<workspace>/.autodev/local/` inside each
  repo itself: Generate session ids, unsent drafts, request history, and `.task` run history (see
  [Claude Integration](ClaudeIntegration.md) and [Task Automation](TaskAutomation.md)). This
  folder is excluded from git via `.git/info/exclude` (not a tracked `.gitignore`), so it never
  shows up as a change to commit - see `EnsureLocalGitExcludeAsync` in
  [Version Control](VersionControl.md).

Both stores tolerate corruption/missing files by falling back to empty defaults rather than
throwing, and both filter stale entries against `Directory.Exists` on load.
