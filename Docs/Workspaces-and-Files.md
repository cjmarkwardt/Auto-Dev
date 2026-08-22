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
`LoadChildren()`/`RefreshChildren()` replace it with real children only once expanded. `IsIgnored`
(dims the row, and is what the "Show/Hide Ignored Files" toggle actually hides) is `FileIgnoreOverride
?? Status == GitFileStatus.Ignored` - `Status` (see below) is always real git status, but
`FileIgnoreOverride` overrides it entirely, independent of git, whenever a `.fileignore` file exists
at the workspace root:

- **No `.fileignore`** - `FileIgnoreOverride` stays `null` on every node, so `IsIgnored` falls back
  to plain `.gitignore`-driven git status, exactly as before.
- **`.fileignore` present** - it replaces `.gitignore` for this purpose entirely (a file `.gitignore`
  excludes but `.fileignore` doesn't is no longer dimmed; `.gitignore` itself keeps controlling real
  git status/staging regardless, since none of this touches git). `Core/Services/FileIgnoreMatcher`
  parses it with the same syntax subset .gitignore itself uses (`#comments`, `!negation`, a trailing
  `/` for directory-only, `*`/`?`/`**` wildcards, last-match-wins) - a bare line reading exactly
  `$gitignore` is expanded into `.gitignore`'s own lines at that point in the file, letting one
  `.fileignore` combine "everything `.gitignore` already excludes" with its own additional patterns
  in whichever order the two should apply. `FilesSectionViewModel.ResolveFileIgnore` is the closure
  every node resolves this through (supplied once at construction, so a folder expanded long after
  the workspace opened still resolves against whatever ruleset is current then, not whatever was
  current when the app started); edits to either file (`OnWatcherChanged`) re-parse and re-push it
  across the whole already-loaded tree via `FileTreeNodeViewModel.RefreshFileIgnoreState`. The same
  ruleset also governs F1 quick-open (`FileIgnoreMatcher.LoadForWorkspace` - see "Search" below) - a
  file `.fileignore` hides is excluded from both search modes entirely, not just dimmed in the tree.

Git status (`Status`, driving the added/modified color and the `.gitignore`-only fallback above) is
resolved asynchronously right after construction via `IFileTreeService.GetStatusAsync`
(`IGitService.GetStatusAsync` underneath), and re-resolved across the whole already-loaded tree
(`FilesSectionViewModel.RefreshGitStatusAsync`) on any on-disk change at all
(`OnWatcherChanged` - a file autosaved from this app's own Edit tab, one written externally, a git
command run outside this app, ...), and separately by `WorkspaceTabViewModel` after any
version-control action or target switch (commit, squash, merge, checkout, ...), none of which
necessarily touch the working tree's own files, so the file watcher alone would never notice a
status that's now stale from one of those.

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

### Selection follows every file open

Whatever file ends up open in the Edit tab, the tree selects (and expands ancestor folders down to)
that same file - not just for a direct tree click, but for every way a file can be opened: either F1
quick-open mode, a markdown link, Edit's own Alt+Left/Alt+Right history navigation, and any future
caller. Rather than have each of those individually call `FilesSectionViewModel.SelectPath`, there's
one shared choke point every one of them already passes through regardless of how it got there:
`EditTabViewModel.CurrentFilePath` changing. `WorkspaceTabViewModel` subscribes to that and calls
`Files.HighlightPath(path)` - the same tree lookup/expand as `SelectPath`, but never re-raises
`FileSelected` (which would otherwise re-open the file it's merely following, redundantly at best
and racing a still-in-flight seek-to-line open at worst - see `FileSearchViewModel`'s own content
search). `CurrentFilePath` stays `null` while Edit is showing a read-only diff instead of a plain
file (`LoadDiffAsync`), so nothing in the tree gets selected for those.

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
file list built once per `Open()` (`LoadFilesAsync`) and filtered the same way the file tree itself
would hide something: `FileIgnoreMatcher.LoadForWorkspace` (shared with
`FilesSectionViewModel.ReloadFileIgnore`) if a `.fileignore` exists at the workspace root - taking
over from `.gitignore` entirely, same replacement semantics as the tree's own dimming (see "The file
tree" above) - or plain `IGitService.GetIgnoredPathsAsync` (`.gitignore` only) otherwise.

`FileChosen(path)` opens the file via the file tree's normal selection path (`Files.SelectPath`).
`ContentResultChosen(path, line)` goes straight to `WorkspaceContentViewModel.OpenFileAsync(path,
line)` instead - `SelectPath` would also open the file itself (with no seek line), racing the
line-seeking open - but still ends up selected in the tree too, via
`WorkspaceTabViewModel`'s subscription to `EditTabViewModel.CurrentFilePath` (see "The file tree"
above) rather than through `SelectPath` directly.
