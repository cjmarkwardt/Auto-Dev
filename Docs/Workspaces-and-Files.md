# Workspaces & Files

## Opening, cloning, and recent workspaces

`HeaderViewModel` (bound to the top-left toolbar) owns every way a workspace gets opened:

- **`BrowseForFolderAsync`** - native folder picker (`IDialogService.PickFolderAsync`), then the
  shared `OpenWorkspaceAsync(path)` tail.
- **`CloneAsync`** - prompts for a URL and a destination parent folder, derives the repo name from
  the URL, checks for a destination collision, then runs `IGitService.CloneAsync` under a
  cancelable `CancellationTokenSource` (`IsCloning` drives the toolbar's "Cloning…"/cancel-button
  state). A failed or cancelled clone deletes the partial destination folder before reporting the
  error. On success, it opens the result exactly like any other folder - see
  [Version Control](VersionControl.md#auto-initializing-a-repo) for what happens next if the
  cloned remote turned out to be empty.
- **`OpenRecentAsync`/`RemoveRecentAsync`** - the MRU dropdown, backed by `IWorkspaceService`.
- **`OpenPathAsync`** - the public entry point `MainShellViewModel` calls once per workspace when
  restoring the previous session's open tabs on startup.

All of these funnel through one private tail:

```csharp
private async Task OpenWorkspaceAsync(string path)
{
    var workspace = await _workspaceService.OpenOrCreateAsync(path);
    WorkspaceOpened?.Invoke(workspace);
    await RefreshRecentWorkspacesAsync();
}
```

`IWorkspaceService.OpenOrCreateAsync` (`Core/Services/WorkspaceService.cs`) resolves the full path,
`Directory.CreateDirectory`s it if needed, calls `IWorkspaceMetadataStore.EnsureInitialized` (creates
`.autodev/` and `.autodev/local/`), and moves the path to the front of the recent-workspaces list.
It has no knowledge of git at all - repo initialization happens later, when the resulting
`WorkspaceTabViewModel.InitializeAsync()` calls `Version.EnsureRepoAsync()`.

## The file tree

`Core/Services/FileTreeService` is a plain filesystem wrapper (`GetChildren`,
`CreateFile`/`CreateFolder`, `Rename`, `Delete`, `Duplicate`, `Move`) - no caching, always reads
disk fresh. `GetChildren` filters out `.autodev` and `.git` themselves but not other dotfiles, and
sorts directories before files, both case-insensitively.

`ViewModels/Sidebar/FileTreeNodeViewModel` wraps one entry. Directories start with a single
placeholder child so the tree shows an expander arrow without eagerly scanning the whole subtree;
`LoadChildren()`/`RefreshChildren()` replace it with real children only once expanded.
`IsIgnored` (dims the row) is resolved asynchronously right after construction via
`IGitService.IsIgnoredAsync`/`IsDirectoryIgnoredAsync`, and re-resolved across the whole
already-loaded tree whenever `.gitignore` itself changes.

`FilesSectionViewModel` is the sidebar's own view model: `Refresh()` diffs new `GetChildren()`
results against existing `FileTreeNodeViewModel`s by path (add/remove only what actually changed,
so unrelated expanded state survives a refresh) and re-applies which `.task` files are currently
"running" (see [Task Automation](TaskAutomation.md)) since a refresh recreates node instances.
Mutating commands (`NewFileAsync`, `NewFolderAsync`, `RenameAsync`, `DeleteAsync`, `Duplicate`,
drag-and-drop `MoveExternalItemsAsync`) are gated on `CanMutate`/`CanMutateNode`, which require a
branch actually being targeted and no interaction-blocking action in flight.

**File-tree change detection** is `Core/Services/FileSystemWatcherAdapter`, wrapping a single
`System.IO.FileSystemWatcher` per workspace (`IncludeSubdirectories = true`) with a 250ms debounce
that coalesces bursts of `Changed`/`Created`/`Deleted`/`Renamed` events into one `Changed(paths)`
callback, so e.g. a `git checkout` touching hundreds of files triggers one tree refresh, not
hundreds.

## Large and binary files in the tree

The Edit tab (see [Editor](Editor.md)) refuses to auto-load a file that's either over 100 KB or
detected as binary (a NUL byte anywhere in its first 8000 bytes, the same heuristic `git diff`
uses) - selecting one shows a warning with the file's size and a "Load Anyway" button instead of
reading its content immediately. A confirmed binary file opens in a memory-mapped hex viewer
rather than as text, so even a very large binary file is never read into a string.

## Search (`FileSearchViewModel`)

The `F1` overlay, with two modes:

- **Filename** - fuzzy subsequence scoring (VS Code Quick-Open style: consecutive-run bonuses,
  filename matches weighted 3x over full relative-path matches), capped at 50 results.
- **Content** - a plain `File.ReadAllLines` scan per non-ignored, non-binary file (binary check via
  `Core/Services/BinaryFileDetector`), case-insensitive substring match, capped at 200 results, run
  on a background thread with a 200ms debounce per keystroke.

Neither mode shells out to `ripgrep` or `git grep` - both are implemented directly in C# over a
file list that's first filtered through `IGitService.GetIgnoredPathsAsync` (the same git-ignore
mechanism the file tree uses for dimming).

`FileChosen(path)` opens the file via the file tree's normal selection path;
`ContentResultChosen(path, line)` goes straight to `WorkspaceContentViewModel.OpenFileAsync(path,
line)`, bypassing the file tree, so the seek-to-line request can't race a tree-driven open of the
same file.
