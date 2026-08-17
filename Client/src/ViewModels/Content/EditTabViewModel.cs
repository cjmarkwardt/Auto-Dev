using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace AutoDev.ViewModels.Content;

public enum EditorKind
{
    File,

    /// <summary>A read-only before/after view of one file as of a specific historical commit - see LoadDiffAsync, opened from the History tab's expanded changes tree. Never backed by a path on disk (CurrentFilePath stays null), so none of the plain-file machinery (auto-save, external-change detection, undo history) applies.</summary>
    Diff,
}

public sealed partial class EditTabViewModel(IFileTreeService fileTreeService, IExternalOpenService externalOpenService) : ViewModelBase
{
    private static readonly TimeSpan AutoSaveDebounce = TimeSpan.FromMilliseconds(750);

    /// <summary>Files at or below this size load straight away; anything larger stops at the IsLargeFile warning instead (see LoadCoreAsync) until the user explicitly confirms via LoadLargeFileAnywayCommand.</summary>
    private const long LargeFileWarningThresholdBytes = 100 * 1024;

    private CancellationTokenSource? _debounceCts;
    private string _lastSavedContent = "";
    private bool _isLoading;

    /// <summary>The path/seek-line a pending IsLargeFile warning is for - consumed by LoadLargeFileAnywayCommand, cleared as soon as a load actually proceeds (large-file or not).</summary>
    private string? _pendingLargeFilePath;
    private int? _pendingLargeFileSeekToLine;

    /// <summary>Browser-style in-memory navigation history of previously opened files, scoped to this workspace tab's lifetime (never persisted) - see LoadCoreAsync's push logic and GoBackAsync/GoForwardAsync.</summary>
    private readonly List<string> _backStack = [];
    private readonly List<string> _forwardStack = [];
    private bool _isNavigatingHistory;

    [ObservableProperty]
    private string? _currentFilePath;

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>Set when the open file changed on disk outside our own auto-save (detected via content comparison, not a raw watcher echo).</summary>
    [ObservableProperty]
    private bool _hasExternalChange;

    [ObservableProperty]
    private EditorKind _kind = EditorKind.File;

    /// <summary>Filename for a plain file, or the task's name for a task's content file.</summary>
    [ObservableProperty]
    private string? _displayTitle;

    /// <summary>Set by WorkspaceContentViewModel.UpdateEditReadOnly - editing is only allowed while targeting a feature, or a version with direct mode on.</summary>
    [ObservableProperty]
    private bool _isReadOnly;

    /// <summary>Why editing is currently blocked, specific to whatever's actually true right now (AI working, wrong target mode, etc.) - see WorkspaceContentViewModel.ComputeReadOnlyReason. Empty while editable.</summary>
    [ObservableProperty]
    private string _readOnlyReason = "";

    /// <summary>True when the open file is an image (see ImageFileTypes) - the View shows ImageSource in a viewer instead of the text editor, and Content/auto-save/external-change-detection are all skipped (see LoadCoreAsync, OnContentChanged, SaveAsync, CheckForExternalChangesAsync).</summary>
    [ObservableProperty]
    private bool _isImage;

    [ObservableProperty]
    private Bitmap? _imageSource;

    /// <summary>
    /// True while a file is being held back from loading - either it's over LargeFileWarningThresholdBytes,
    /// or IsBinaryFile detected non-text content, regardless of size (see LoadCoreAsync/IsBinaryContentAsync).
    /// Cleared as soon as the user confirms via LoadLargeFileAnywayCommand. The View shows a warning (see
    /// LargeFileWarningTitle/LargeFileWarningDetail) instead of the text editor/markdown preview/image viewer,
    /// all three of which stay hidden via ShowTextEditor/ShowMarkdownPreview/the Image binding's own IsImage
    /// check (LoadCoreAsync forces IsImage false in this state).
    /// </summary>
    [ObservableProperty]
    private bool _isLargeFile;

    /// <summary>Whether the pending IsLargeFile warning was triggered by binary content (as opposed to, or in addition to, sheer size) - see IsBinaryContentAsync. Confirming via LoadLargeFileAnywayCommand opens a binary file in the hex viewer (IsHexView) rather than the plain text editor.</summary>
    [ObservableProperty]
    private bool _isBinaryFile;

    [ObservableProperty]
    private long _largeFileSizeBytes;

    /// <summary>Human-readable form of LargeFileSizeBytes (e.g. "92.0 MB") for the IsLargeFile warning.</summary>
    public string LargeFileSizeDisplay => FormatFileSize(LargeFileSizeBytes);

    public string LargeFileWarningTitle => IsBinaryFile ? "Binary file" : "File is very large";

    public string LargeFileWarningDetail => IsBinaryFile
        ? $"{LargeFileSizeDisplay} - this looks like a binary file and won't display correctly as text."
        : $"{LargeFileSizeDisplay} - loading it here may be slow.";

    private static string FormatFileSize(long bytes)
    {
        ReadOnlySpan<string> units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }

    /// <summary>True once a binary file has been confirmed via LoadLargeFileAnywayCommand - the View shows a HexViewControl memory-mapped straight to CurrentFilePath instead of the plain text editor, so even a huge binary file never gets read into a Content string (see EditTabView's OnFileLoaded/SetupHexView).</summary>
    [ObservableProperty]
    private bool _isHexView;

    /// <summary>
    /// The Edit tab's markdown-only edit toggle - false (default) renders the file as a MarkdownScrollViewer
    /// preview; true edits it as plain text like any other file. Deliberately never reset by LoadCoreAsync, so
    /// switching between .md files keeps whichever state was last chosen. Meaningless (and hidden - see
    /// IsMarkdown) for any other file type.
    /// </summary>
    [ObservableProperty]
    private bool _isEditingMarkdown;

    /// <summary>
    /// Only meaningful while CanFind is true (see EditTabView.axaml's Find button/Ctrl+F handler, both gated
    /// the same way) - closed on every LoadCoreAsync, since a find naturally scopes to one file at a time.
    /// Works against either of two different underlying searches depending on which of ShowTextEditor/
    /// ShowMarkdownPreview is currently active - see PreviewSearchInvalidated's own doc comment. Two-way
    /// bound straight to the header's ToggleButton (see OnIsFindBarOpenChanged for the actual open/close side
    /// effects) rather than routed through a command, so toggling it either way - the button, Ctrl+F, Escape,
    /// or the bar's own close button - all funnel through the exact same path.
    /// </summary>
    [ObservableProperty]
    private bool _isFindBarOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindMatchCountDisplay))]
    private string _findText = "";

    [ObservableProperty]
    private bool _findMatchCase;

    [ObservableProperty]
    private bool _findMatchWholeWord;

    /// <summary>The current search's total match count, in whichever domain applies (see PreviewSearchInvalidated's own doc comment for why there are two) - RecomputeFindMatches keeps this mirroring _findMatches.Count in text mode; the View sets it directly after a preview-mode search. NotifyPropertyChangedFor(FindMatchCountDisplay) is load-bearing, not decorative - a repeat search that lands on the same FindCurrentMatchIndex (e.g. every keystroke while typing "apple" keeps re-landing on match 1) still needs FindMatchCountDisplay to refresh even though FindCurrentMatchIndex itself didn't change value.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindPreviousCommand))]
    [NotifyPropertyChangedFor(nameof(FindMatchCountDisplay))]
    private int _findMatchCount;

    /// <summary>1-based position of the current match (within _findMatches in text mode, or the View's own tracked preview match index) - see FindMatchCountDisplay; 0 while there's no current match.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindMatchCountDisplay))]
    private int _findCurrentMatchIndex;

    private List<int> _findMatches = [];

    public string FindMatchCountDisplay => FindText.Length == 0 ? "" : FindMatchCount == 0 ? "No results" : $"{FindCurrentMatchIndex} of {FindMatchCount}";

    /// <summary>Raised whenever the current match changes to a real one (opening the bar, typing, toggling an option, Next/Previous) while ShowTextEditor is true - the View selects and scrolls to (offset, length) in the AvaloniaEdit editor. Never raised for a mid-edit recount (see OnContentChanged) - only an explicit find action should ever move the viewport/selection.</summary>
    public event Action<int, int>? NavigateToMatch;

    /// <summary>
    /// The ShowMarkdownPreview counterpart to RecomputeFindMatches/NavigateToMatch - raised whenever the find
    /// query/options change (or the bar opens) while the rendered preview, not the plain-text editor, is
    /// showing. Matches there live in the rendered CTextBlock visual tree, not in a plain Content string a
    /// single int offset could address, so unlike the text-editor path this ViewModel can't compute them
    /// itself - the View owns the whole search (see EditTabView.RecomputePreviewMatches) and reports the
    /// count/current-match back through FindMatchCount/FindCurrentMatchIndex, the same properties either mode
    /// drives the "N of M" label from.
    /// </summary>
    public event Action? PreviewSearchInvalidated;

    /// <summary>The ShowMarkdownPreview counterpart to MoveToMatch's own NavigateToMatch call - raised by FindNext/FindPrevious with the same +1/-1 direction, letting the View advance whichever match it's tracking and highlight/scroll to it (see EditTabView.MovePreviewMatch).</summary>
    public event Action<int>? PreviewMatchMoveRequested;

    /// <summary>Raised right after IsFindBarOpen turns on - the View focuses and selects-all in the find text box in response, mirroring FocusRequested's own pattern for the main editor.</summary>
    public event Action? FindBarFocusRequested;

    /// <summary>Bound to the header's Find ToggleButton and everything else that opens/closes the bar - see IsFindBarOpen's own doc comment for why this is a property hook rather than a pair of RelayCommands.</summary>
    partial void OnIsFindBarOpenChanged(bool value)
    {
        if (value)
        {
            InvalidateFind();
            FindBarFocusRequested?.Invoke();
        }
        else
        {
            _findMatches = [];
            FindMatchCount = 0;
            FindCurrentMatchIndex = 0;

            // RecomputePreviewMatches clears its own tracked selection/highlight before checking IsFindBarOpen,
            // so this also runs (and is a no-op) in text mode - cheaper than adding a second, preview-only event
            // just for "the bar closed" when this one already fires at exactly the right time.
            PreviewSearchInvalidated?.Invoke();
        }
    }

    /// <summary>Thin wrapper so the find bar's own close ("×") button has a Command to bind, consistent with every other button in this codebase - Escape (see EditTabView.axaml.cs) just sets the property directly instead, same as the header ToggleButton's two-way binding does.</summary>
    [RelayCommand]
    private void CloseFindBar() => IsFindBarOpen = false;

    /// <summary>FindMatchCount is the shared source of truth for both modes - RecomputeFindMatches keeps it mirroring _findMatches.Count in text mode, and the View sets it directly after a preview-mode search (see PreviewSearchInvalidated) - so this one check covers Find Next/Previous's enabled state regardless of which is active.</summary>
    private bool CanCycleFindMatches() => FindMatchCount > 0;

    [RelayCommand(CanExecute = nameof(CanCycleFindMatches))]
    private void FindNext() => MoveToMatch(1);

    [RelayCommand(CanExecute = nameof(CanCycleFindMatches))]
    private void FindPrevious() => MoveToMatch(-1);

    partial void OnFindTextChanged(string value) => InvalidateFind();

    partial void OnFindMatchCaseChanged(bool value) => InvalidateFind();

    partial void OnFindMatchWholeWordChanged(bool value) => InvalidateFind();

    private void InvalidateFind()
    {
        if (!IsFindBarOpen)
        {
            return;
        }

        if (ShowMarkdownPreview)
        {
            PreviewSearchInvalidated?.Invoke();
        }
        else
        {
            RecomputeFindMatches(navigate: true);
        }
    }

    private void MoveToMatch(int direction)
    {
        if (ShowMarkdownPreview)
        {
            PreviewMatchMoveRequested?.Invoke(direction);
            return;
        }

        if (_findMatches.Count == 0)
        {
            return;
        }

        var zeroBased = ((FindCurrentMatchIndex - 1 + direction) % _findMatches.Count + _findMatches.Count) % _findMatches.Count;
        FindCurrentMatchIndex = zeroBased + 1;
        NavigateToMatch?.Invoke(_findMatches[zeroBased], FindText.Length);
    }

    /// <summary>
    /// Re-scans Content for every occurrence of FindText under the current MatchCase/MatchWholeWord options.
    /// `navigate` is false only for OnContentChanged's mid-edit recount (below) - the count/positions need to
    /// stay accurate while the user keeps typing elsewhere in the document with the bar still open, but that
    /// alone shouldn't yank the viewport/selection around; every other caller (opening the bar, editing the
    /// query itself, toggling an option) does want the current match to jump into view.
    /// </summary>
    private void RecomputeFindMatches(bool navigate)
    {
        if (!IsFindBarOpen)
        {
            return;
        }

        var previousOffset = FindCurrentMatchIndex > 0 && FindCurrentMatchIndex <= _findMatches.Count
            ? _findMatches[FindCurrentMatchIndex - 1]
            : 0;

        _findMatches = TextSearch.FindAllMatches(Content, FindText, FindMatchCase, FindMatchWholeWord);
        FindMatchCount = _findMatches.Count;
        FindNextCommand.NotifyCanExecuteChanged();
        FindPreviousCommand.NotifyCanExecuteChanged();

        if (_findMatches.Count == 0)
        {
            FindCurrentMatchIndex = 0;
            return;
        }

        // Keep whichever match is at/after wherever the previous current match was, rather than always
        // snapping back to the very first match on every keystroke of the query.
        var newIndex = _findMatches.FindIndex(offset => offset >= previousOffset);
        if (newIndex < 0)
        {
            newIndex = _findMatches.Count - 1;
        }

        FindCurrentMatchIndex = newIndex + 1;

        if (navigate)
        {
            NavigateToMatch?.Invoke(_findMatches[newIndex], FindText.Length);
        }
    }

    public string KindLabel => Kind == EditorKind.Diff ? "Diff" : "File";

    public bool IsDiff => Kind == EditorKind.Diff;

    /// <summary>Whether there's real text content at all (as opposed to an image, a hex-viewed binary file, or a Diff-mode view) - independent of the markdown toggle, so the View's code-behind can keep the AvaloniaEdit document in sync even while it's hidden behind the markdown preview (see EditTabView's OnFileLoaded).</summary>
    public bool HasTextContent => !IsImage && !IsHexView && Kind != EditorKind.Diff;

    /// <summary>Populated once by LoadDiffAsync, never mutated afterward - a fresh Diff-mode load Clears and refills rather than reusing stale rows.</summary>
    public ObservableCollection<DiffLine> DiffLines { get; } = [];

    public bool IsMarkdown => Kind == EditorKind.File && CurrentFilePath is { } path &&
                               Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>The plain AvaloniaEdit surface shows for any regular text file, and for a markdown file with the edit toggle on - never while IsLargeFile is still awaiting confirmation.</summary>
    public bool ShowTextEditor => HasTextContent && !IsLargeFile && (!IsMarkdown || IsEditingMarkdown);

    /// <summary>The rendered markdown preview shows only for a markdown file with the edit toggle off, and never while IsLargeFile is still awaiting confirmation.</summary>
    public bool ShowMarkdownPreview => IsMarkdown && !IsLargeFile && !IsEditingMarkdown;

    /// <summary>Whether the Find bar/button and Ctrl+F make sense right now - either a plain-text editable surface or the rendered markdown preview (see IsFindBarOpen's own doc comment for how the two are searched differently), but not an image/hex view or the still-unconfirmed large-file warning.</summary>
    public bool CanFind => ShowTextEditor || ShowMarkdownPreview;

    /// <summary>What ShowMarkdownPreview's MarkdownScrollViewer actually binds to - Content with any ```mermaid fenced blocks replaced by rendered diagram images (see MermaidMarkdownProcessor). The saved file content is never touched - only this computed view of it. A stored (not computed) property: mermaid rendering runs off the UI thread (see UpdateRenderedContent) since it can take a visible moment, so this starts out as the raw, unrendered Content the instant it changes and is swapped in once rendering finishes, rather than blocking the UI thread synchronously on every markdown change.</summary>
    [ObservableProperty]
    private string _renderedContent = "";

    /// <summary>Bumped on every call - lets a slower-finishing render from an earlier Content/mode change (e.g. rapid file switching) detect it's stale and not overwrite a newer one's result.</summary>
    private int _renderGeneration;

    /// <summary>True when the open file is an image but decoding it failed (corrupt/truncated file) - the View shows a fallback message instead of a blank viewer.</summary>
    public bool ImageLoadFailed => IsImage && ImageSource is null;

    public bool CanGoBack => _backStack.Count > 0;

    public bool CanGoForward => _forwardStack.Count > 0;

    partial void OnKindChanged(EditorKind value)
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IsDiff));
        OnPropertyChanged(nameof(HasTextContent));
        RaiseMarkdownLayoutChanged();
    }

    partial void OnCurrentFilePathChanged(string? value) => RaiseMarkdownLayoutChanged();

    partial void OnIsEditingMarkdownChanged(bool value) => RaiseMarkdownLayoutChanged();

    partial void OnIsLargeFileChanged(bool value) => RaiseMarkdownLayoutChanged();

    partial void OnIsBinaryFileChanged(bool value)
    {
        OnPropertyChanged(nameof(LargeFileWarningTitle));
        OnPropertyChanged(nameof(LargeFileWarningDetail));
    }

    partial void OnLargeFileSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(LargeFileSizeDisplay));
        OnPropertyChanged(nameof(LargeFileWarningDetail));
    }

    partial void OnIsHexViewChanged(bool value)
    {
        OnPropertyChanged(nameof(HasTextContent));
        RaiseMarkdownLayoutChanged();
    }

    private void RaiseMarkdownLayoutChanged()
    {
        OnPropertyChanged(nameof(IsMarkdown));
        OnPropertyChanged(nameof(ShowTextEditor));
        OnPropertyChanged(nameof(ShowMarkdownPreview));
        OnPropertyChanged(nameof(CanFind));
        UpdateRenderedContent();

        // Toggling the markdown edit/preview switch while Find is already open (e.g. IsEditingMarkdown flipping)
        // changes which of the two searches below applies - re-run under whichever one now applies. A harmless
        // no-op when this fires from an actual file load instead (LoadCoreAsync already closed the bar by then).
        InvalidateFind();
    }

    /// <summary>Immediately shows Content as-is (so switching files/modes is never blocked), then swaps in the mermaid-rendered version once ready - see RenderedContent's doc comment. Mermaid rendering runs via Task.Run purely to get it off the UI thread; the await afterward resumes back on it automatically (this method is always invoked from the UI thread, same as every other async method in this codebase that relies on the default SynchronizationContext capture - no explicit dispatcher needed).</summary>
    private void UpdateRenderedContent()
    {
        var generation = ++_renderGeneration;

        if (!IsMarkdown)
        {
            // EditTabView.axaml's MarkdownScrollViewer is bound to RenderedContent unconditionally - only
            // its containing Border's IsVisible toggles on ShowMarkdownPreview, so the control itself is
            // always realized and its Markdown binding stays live even while hidden. Setting RenderedContent
            // for a non-markdown file used to still push the raw content through Markdown.Avalonia's
            // regex-based parser for a control nobody can see; for a huge/binary file (e.g. a multi-hundred-
            // MB published executable opened in the Edit tab) that parse ran long enough on the UI thread to
            // look like the whole app had locked up. Skipping it entirely for non-markdown files is safe -
            // nothing reads RenderedContent while ShowMarkdownPreview is false.
            RenderedContent = "";
            return;
        }

        RenderedContent = Content;

        if (!Content.Contains("```mermaid", StringComparison.Ordinal))
        {
            return; // fast path - nothing to render, skip the background hop entirely
        }

        _ = RenderMermaidAsync(Content, generation);
    }

    private async Task RenderMermaidAsync(string content, int generation)
    {
        var rendered = await Task.Run(() => MermaidMarkdownProcessor.Process(content));
        if (generation == _renderGeneration)
        {
            RenderedContent = rendered;
        }
    }

    partial void OnIsImageChanged(bool value)
    {
        OnPropertyChanged(nameof(ImageLoadFailed));
        OnPropertyChanged(nameof(HasTextContent));
        RaiseMarkdownLayoutChanged();
    }

    partial void OnImageSourceChanged(Bitmap? value) => OnPropertyChanged(nameof(ImageLoadFailed));

    /// <summary>Raised when this tab becomes active - the view focuses its editor in response.</summary>
    public event Action? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke();

    /// <summary>Raised once LoadCoreAsync fully finishes (see there) - every genuine load or reload, including reloading the same path. The View uses this to switch AvaloniaEdit to that path's own cached TextDocument, so each file keeps its own independent undo/redo history across switches instead of sharing one editor-wide undo stack. Carries a line to seek to (from a content-search result click), or null for a normal open.</summary>
    public event Action<int?>? FileLoaded;

    /// <summary>Raised by HandleMarkdownLink for a relative-path link that resolved to a real file - WorkspaceTabViewModel subscribes this to Content.OpenFileAsync, the same way Files.FileSelected already does, since only WorkspaceContentViewModel (which owns both this Edit tab and OpenFileAsync) can actually navigate there.</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>
    /// Handles every markdown link click except a same-document "#section" anchor - EditTabView's own
    /// HyperlinkCommand handles that one directly (see there), since scrolling the rendered preview to a
    /// heading needs the live Avalonia visual tree, which this ViewModel deliberately has no access to.
    /// A web URL (http/https/mailto/ftp) opens in the OS default browser; anything else is resolved as a
    /// filesystem path relative to the current markdown file's own directory (Path.Combine already treats
    /// an already-absolute target as itself, ignoring baseDir) - a trailing "#fragment" on a link to
    /// *another* file is stripped before resolving, since jumping into that file's own section isn't
    /// supported (only same-document anchors are). Silently does nothing for a link that resolves to no
    /// real file - there's nothing else sensible to do with e.g. a custom URL scheme or a typo'd path.
    /// </summary>
    public void HandleMarkdownLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "ftp")
        {
            externalOpenService.OpenUrl(url);
            return;
        }

        if (CurrentFilePath is not { } currentPath || Path.GetDirectoryName(currentPath) is not { } baseDir)
        {
            return;
        }

        var target = url.Split('#')[0];
        if (target.Length == 0)
        {
            return;
        }

        var resolved = Path.GetFullPath(Path.Combine(baseDir, Uri.UnescapeDataString(target)));
        if (File.Exists(resolved))
        {
            OpenFileRequested?.Invoke(resolved);
        }
    }

    public async Task LoadFileAsync(string path, int? seekToLine = null)
    {
        await LoadCoreAsync(path, seekToLine);
        DisplayTitle = Path.GetFileName(path);
    }

    /// <summary>
    /// Opens a read-only before/after view of one file as of a specific historical commit - see EditorKind.Diff.
    /// Unlike LoadFileAsync, this never touches CurrentFilePath/Content/disk at all, so none of the plain-file
    /// machinery (auto-save, external-change detection, undo history, find-in-file) applies; DiffLines is the
    /// only thing the View's Diff-mode panel actually renders.
    /// </summary>
    public async Task LoadDiffAsync(string displayTitle, FileDiffContent diff)
    {
        await FlushPendingSaveAsync();

        ImageSource?.Dispose();
        IsFindBarOpen = false;

        Kind = EditorKind.Diff;
        CurrentFilePath = null;
        Content = "";
        _lastSavedContent = "";
        HasUnsavedChanges = false;
        HasExternalChange = false;
        IsImage = false;
        ImageSource = null;
        IsHexView = false;
        IsLargeFile = false;
        DisplayTitle = displayTitle;

        DiffLines.Clear();
        foreach (var line in await Task.Run(() => BuildDiffLines(diff.Before, diff.After)))
        {
            DiffLines.Add(line);
        }
    }

    /// <summary>Line-level diff via DiffPlex's InlineDiffBuilder (a single merged before/after sequence, not side-by-side panes) - OldLineNumber/NewLineNumber are tracked by hand here since InlineDiffBuilder only exposes one Position per piece, not both gutters at once. A missing Before/After (an added/deleted file - see FileDiffContent) is treated as empty, so the whole other side's content reads as fully added/removed.</summary>
    private static IReadOnlyList<DiffLine> BuildDiffLines(string? before, string? after)
    {
        var diff = InlineDiffBuilder.Diff(before ?? "", after ?? "");
        var lines = new List<DiffLine>();
        var oldLineNumber = 0;
        var newLineNumber = 0;

        foreach (var piece in diff.Lines)
        {
            switch (piece.Type)
            {
                case ChangeType.Inserted:
                    newLineNumber++;
                    lines.Add(new DiffLine(piece.Text, DiffLineKind.Added, null, newLineNumber));
                    break;
                case ChangeType.Deleted:
                    oldLineNumber++;
                    lines.Add(new DiffLine(piece.Text, DiffLineKind.Removed, oldLineNumber, null));
                    break;
                default:
                    oldLineNumber++;
                    newLineNumber++;
                    lines.Add(new DiffLine(piece.Text, DiffLineKind.Unchanged, oldLineNumber, newLineNumber));
                    break;
            }
        }

        return lines;
    }

    /// <summary>Loads the file an IsLargeFile warning is currently showing for, bypassing the size gate - the reverse of LoadCoreAsync's forceLoad = false default.</summary>
    [RelayCommand]
    private async Task LoadLargeFileAnywayAsync()
    {
        if (_pendingLargeFilePath is not { } path)
        {
            return;
        }

        await LoadCoreAsync(path, _pendingLargeFileSeekToLine, forceLoad: true);
    }

    private async Task LoadCoreAsync(string path, int? seekToLine = null, bool forceLoad = false)
    {
        await FlushPendingSaveAsync();

        _isLoading = true;
        ImageSource?.Dispose();
        IsFindBarOpen = false; // a find naturally scopes to one file - never carry a stale match list into the next

        long fileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        bool isImagePath = ImageFileTypes.IsImage(path);

        // Images get their own dedicated (already-safe, off-thread) decode path below regardless of content,
        // so there's no need to sniff one for binary-ness too - only non-image files can end up routed to the
        // hex viewer.
        bool isBinary = !isImagePath && await IsBinaryContentAsync(path);

        if (!forceLoad && (fileSize > LargeFileWarningThresholdBytes || isBinary))
        {
            _pendingLargeFilePath = path;
            _pendingLargeFileSeekToLine = seekToLine;
            IsLargeFile = true;
            IsBinaryFile = isBinary;
            LargeFileSizeBytes = fileSize;
            IsImage = false;
            ImageSource = null;
            IsHexView = false;
            Content = "";
            _lastSavedContent = "";
        }
        else
        {
            _pendingLargeFilePath = null;
            _pendingLargeFileSeekToLine = null;
            IsLargeFile = false;
            IsBinaryFile = false;

            if (isImagePath)
            {
                IsImage = true;
                IsHexView = false;
                ImageSource = await DecodeImageAsync(path);
                Content = "";
                _lastSavedContent = "";
            }
            else if (isBinary)
            {
                // Confirmed via LoadLargeFileAnywayCommand (forceLoad) - the View's HexViewControl memory-maps
                // CurrentFilePath directly once IsHexView flips true (see OnFileLoaded/SetupHexView), so unlike
                // the text branch below, Content is deliberately never populated here: reading a huge binary
                // file into a string is exactly the wasted work/risk IsLargeFile exists to avoid in the first
                // place, and the hex viewer has no use for it anyway.
                IsImage = false;
                ImageSource = null;
                IsHexView = true;
                Content = "";
                _lastSavedContent = "";
            }
            else
            {
                IsImage = false;
                ImageSource = null;
                IsHexView = false;
                var text = await fileTreeService.ReadFileAsync(path);
                Content = text;
                _lastSavedContent = text;
            }
        }

        // Recorded before CurrentFilePath is overwritten below - a navigation not driven by GoBackAsync/
        // GoForwardAsync themselves pushes the file being left onto the back stack and clears any forward
        // stack (standard browser semantics: opening a new location after having gone back discards the
        // path not taken). Skipped for a same-path reload (ReloadFromDiskAsync) and while replaying history.
        if (!_isNavigatingHistory && CurrentFilePath is { } previousPath && previousPath != path)
        {
            _backStack.Add(previousPath);
            _forwardStack.Clear();
            NotifyHistoryChanged();
        }

        // Set last, after Content/ImageSource are all final - the View's FileLoaded handler (below) reads
        // Content to seed or reuse a per-path AvaloniaEdit TextDocument, so it needs the new file's actual
        // content already in place, not whatever the previously-open file left behind.
        Kind = EditorKind.File;
        CurrentFilePath = path;

        if (seekToLine.HasValue && IsMarkdown && !IsLargeFile)
        {
            IsEditingMarkdown = true; // force plain-text view so the seeked line is actually visible
        }

        HasUnsavedChanges = false;
        HasExternalChange = false;
        _isLoading = false;

        // Fired unconditionally (unlike CurrentFilePath's own PropertyChanged, which no-ops when reloading
        // the same path) - ReloadFromDiskAsync needs the View to still react even though the path didn't
        // change, so it can replace that path's cached TextDocument with one reflecting the fresh disk
        // content instead of reusing stale undo history that no longer matches what's on screen.
        FileLoaded?.Invoke(seekToLine);
    }

    /// <summary>Decodes off the UI thread since Bitmap's constructor is synchronous disk+decode work - returns null (rather than throwing) for a corrupt/truncated file, so opening a bad image can't crash the app; ImageLoadFailed drives the View's fallback message in that case.</summary>
    private static async Task<Bitmap?> DecodeImageAsync(string path)
    {
        try
        {
            return await Task.Run(() => new Bitmap(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Same heuristic `git diff` uses to decide a file is binary: a NUL byte anywhere in the first 8000 bytes
    /// means it isn't meaningfully text, regardless of size - a 2 KB binary file is just as unreadable/
    /// unsafe to feed through the plain-text editor or markdown parser as a 2 GB one (see LoadCoreAsync). Only
    /// reads that leading slice rather than the whole file, so this stays cheap even for a huge file.
    /// </summary>
    private static async Task<bool> IsBinaryContentAsync(string path)
    {
        const int SampleSize = 8000;

        try
        {
            await using FileStream stream = File.OpenRead(path);
            byte[] buffer = new byte[SampleSize];
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    partial void OnContentChanged(string value)
    {
        UpdateRenderedContent();
        RecomputeFindMatches(navigate: false); // keep the count/positions accurate as the document itself changes, without yanking the viewport around - see its own doc comment

        if (_isLoading || !HasTextContent)
        {
            return;
        }

        HasUnsavedChanges = value != _lastSavedContent;
        HasExternalChange = false; // a further edit is treated as the user choosing to keep their version
        ScheduleAutoSave();
    }

    private void ScheduleAutoSave()
    {
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = DebounceSaveAsync(cts.Token);
    }

    private async Task DebounceSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutoSaveDebounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveAsync();
    }

    public async Task FlushPendingSaveAsync()
    {
        _debounceCts?.Cancel();
        if (HasUnsavedChanges)
        {
            await SaveAsync();
        }
    }

    private async Task SaveAsync()
    {
        if (CurrentFilePath is null || !HasTextContent)
        {
            return;
        }

        await fileTreeService.WriteFileAsync(CurrentFilePath, Content);
        _lastSavedContent = Content;
        HasUnsavedChanges = false;
    }

    /// <summary>Called when the workspace file watcher reports a change - re-reads the file and flags a genuine external edit (i.e. content that doesn't match what we last saved/loaded).</summary>
    public async Task CheckForExternalChangesAsync()
    {
        if (CurrentFilePath is null || !File.Exists(CurrentFilePath) || _isLoading || !HasTextContent)
        {
            return;
        }

        var onDisk = await fileTreeService.ReadFileAsync(CurrentFilePath);
        if (onDisk != _lastSavedContent)
        {
            HasExternalChange = true;
        }
    }

    [RelayCommand]
    private async Task ReloadFromDiskAsync()
    {
        if (CurrentFilePath is null)
        {
            return;
        }

        await LoadFileAsync(CurrentFilePath);
    }

    private bool CanGoBackExecute() => CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoBackExecute))]
    private async Task GoBackAsync()
    {
        if (_backStack.Count == 0 || CurrentFilePath is not { } current)
        {
            return;
        }

        var target = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        _forwardStack.Add(current);

        _isNavigatingHistory = true;
        try
        {
            await LoadFileAsync(target);
        }
        finally
        {
            _isNavigatingHistory = false;
        }

        NotifyHistoryChanged();
    }

    private bool CanGoForwardExecute() => CanGoForward;

    [RelayCommand(CanExecute = nameof(CanGoForwardExecute))]
    private async Task GoForwardAsync()
    {
        if (_forwardStack.Count == 0 || CurrentFilePath is not { } current)
        {
            return;
        }

        var target = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);
        _backStack.Add(current);

        _isNavigatingHistory = true;
        try
        {
            await LoadFileAsync(target);
        }
        finally
        {
            _isNavigatingHistory = false;
        }

        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }
}
