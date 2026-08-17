# Editor

`ViewModels/Content/EditTabViewModel` and `Views/Content/EditTabView` are the always-visible
left-hand content pane. A given open file is in exactly one of five mutually-exclusive display
states, each gating a different part of the view:

| State | Flag | View content |
|---|---|---|
| Plain text | `ShowTextEditor` | An `AvaloniaEdit.TextEditor` with TextMate syntax highlighting |
| Markdown preview | `ShowMarkdownPreview` | A `MarkdownScrollViewer` rendering `RenderedContent` |
| Image | `IsImage` | An `Image` control bound to a decoded `Bitmap` |
| Binary hex view | `IsHexView` | A `HexView.Avalonia.Controls.HexViewControl`, memory-mapped |
| Large/binary-file warning | `IsLargeFile` | Size + reason message and a "Load Anyway" button |

## Loading a file (`LoadCoreAsync`)

Every load goes through one method that decides which of the five states applies, in order:

1. **Size check** - `fileSize > 100 KB` (`LargeFileWarningThresholdBytes`).
2. **Binary check** - `IsBinaryContentAsync`: reads only the first 8000 bytes and looks for a NUL
   byte (the same heuristic `git diff` uses to decide a file is binary) - cheap even for a huge
   file, since it never reads more than that leading slice. Skipped for image files, which have
   their own always-safe path regardless of content.
3. If either trips (and the load wasn't explicitly forced), the file **is not read at all** -
   `IsLargeFile`/`IsBinaryFile`/`LargeFileSizeBytes` are set and `LoadCoreAsync` returns early. The
   pending path is remembered so the "Load Anyway" button (`LoadLargeFileAnywayCommand`) can
   re-invoke `LoadCoreAsync(path, forceLoad: true)` for that exact file.
4. Otherwise: an image path decodes to a `Bitmap` off the UI thread
   (`Task.Run(() => new Bitmap(path))`, swallowing decode failures into `ImageLoadFailed` instead
   of crashing); a binary path (only reachable via a forced "Load Anyway" load) sets `IsHexView`
   and deliberately **never populates `Content`** - the point of routing to the hex viewer is to
   avoid ever reading a large binary file into a C# string; anything else is read as text into
   `Content` normally.

`EditTabView.axaml.cs` mirrors `IsHexView` by memory-mapping the file directly
(`HexView.Avalonia.Services.MemoryMappedLineReader`) the moment it flips true, and releases that
mapping the instant a different file loads or the tab closes - so a hex-viewed file, however large,
never has its bytes duplicated into managed memory at all.

### Why this exists

Earlier, `RenderedContent` (the string fed to the markdown preview's `MarkdownScrollViewer`) was
set unconditionally for *every* opened file, because the control binding it stays live even while
its container is hidden for a non-markdown file. Opening a large binary file (a published
executable, in the case that surfaced this) pushed its raw bytes through Markdown.Avalonia's
regex-based parser on the UI thread, which could take long enough to look exactly like the whole
app had hung. The size/binary gate above exists specifically so that can't happen again, for any
file type, regardless of extension.

## Markdown rendering and Mermaid diagrams

`RenderedContent` is only ever populated for an actual markdown file (`IsMarkdown`, i.e. a `.md`
extension) - see `UpdateRenderedContent()`. For those files, `Core/Services/MermaidMarkdownProcessor`
replaces every ` ```mermaid ` fenced block with a `![Mermaid diagram](file://...)` reference to a
rendered PNG before the markdown reaches the viewer:

- `Core/Services/MermaidRenderer` wraps **Mermaider** (a pure-.NET Mermaid-to-SVG engine - no
  Node/browser dependency) plus **Svg.Skia** (rasterizes that SVG to PNG), themed to match
  AutoDev's own dark palette.
  - Mermaider's ER-diagram layout has a known bug where a *self-referencing* relationship (e.g. a
    folder-hierarchy "parent of" edge) corrupts the whole diagram's layout, not just that one
    edge - `MermaidMarkdownProcessor` strips self-referencing relationship lines from `erDiagram`
    blocks before rendering as a workaround, since the package itself ships as a binary NuGet
    reference with no local source to patch.
- Rendered PNGs are cached on disk (keyed by a hash of the diagram source) under
  `%TEMP%/AutoDev/mermaid-cache`, so re-opening the same file is a cache hit rather than a
  re-render.
- Rendering runs off the UI thread (`Task.Run`) and swaps in once ready; `RenderedContent` starts
  out as the raw, unrendered text the instant `Content` changes, so switching files/typing is
  never blocked waiting on a render.

The same `MermaidMarkdownProcessor` is reused by the Generate tab (see
[Claude Integration](ClaudeIntegration.md)) to render diagrams inside a Claude reply.

## Autosave, external changes, and per-path undo history

- `OnContentChanged` debounce-autosaves 750ms after the last keystroke (`ScheduleAutoSave`) - but
  only when `HasTextContent` (i.e. neither an image nor a hex view is showing); saving is a no-op
  for either.
- `CheckForExternalChangesAsync` (triggered by the workspace file watcher) re-reads the file and
  sets `HasExternalChange` if disk content no longer matches what was last loaded/saved, showing a
  "This file changed on disk" banner with a Reload action.
- `EditTabView.axaml.cs` keeps one `AvaloniaEdit.TextDocument` (with its own independent undo
  stack) per file path in a dictionary, so switching between files and back preserves each one's
  undo history rather than sharing a single editor-wide stack.
- A browser-style in-memory back/forward navigation stack (`GoBackAsync`/`GoForwardAsync`,
  `Alt+Left`/`Alt+Right`) tracks previously opened files for the lifetime of the tab.

`Edit.IsReadOnly` is driven from outside this view model entirely - see
[Architecture](Architecture.md#read-only-editing-and-per-tab-layout) for how targeting a
tag/commit, an in-progress version action, or an active Claude turn all lock editing.

## Find in file

A non-modal find bar (`IsFindBarOpen`), opened via `Ctrl+F` or the header's magnifying-glass
toggle button - both hidden/disabled unless `ShowTextEditor` is the active state, since there's
nothing to search while an image/hex view, the markdown preview, or the large-file warning is
showing instead. It's a slim bar docked above the editor (same idiom as the "file changed on
disk" banner), not a blocking dialog, so the document stays fully visible and interactive
underneath while searching.

- **Matching**: `FindAllMatches` is a plain non-overlapping substring scan over `Content` (no
  regex, no AvaloniaEdit's own built-in `AvaloniaEdit.Search.SearchPanel` engine, which has no way
  to report occurrence count) - `FindMatchCase` picks `Ordinal` vs `OrdinalIgnoreCase`, and
  `FindMatchWholeWord` additionally checks that the characters immediately before/after a match
  aren't themselves word characters (letter, digit, or `_`).
- **Count/position**: `FindMatchCountDisplay` shows `"{current} of {total}"` (or `"No results"`,
  or nothing while the query is empty). Opening the bar, editing the query, toggling an option, or
  Next/Previous (`FindNextCommand`/`FindPreviousCommand`, wrapping at either end) all raise
  `NavigateToMatch(offset, length)`, which `EditTabView.axaml.cs` turns into
  `_editor.Select(offset, length)` + `ScrollTo` + `BringCaretToView()`.
- **Live recount without stealing the viewport**: `OnContentChanged` also re-runs the same scan
  (`RecomputeFindMatches(navigate: false)`) so the count/positions stay accurate while the
  document is being edited elsewhere with the bar still open - but that path deliberately never
  raises `NavigateToMatch`, so editing the file doesn't yank the selection/scroll position around;
  only an explicit find action does.
- Ctrl+F seeds the query from the current editor selection when opening fresh (standard find-bar
  convention), but re-pressing Ctrl+F while already open just refocuses and re-selects the
  existing query instead of clobbering it. The bar (and its query/options) closes automatically on
  every file load - a find naturally scopes to one file at a time.
