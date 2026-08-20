using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using AutoDev.ViewModels.Content;
using ColorTextBlock.Avalonia;
using CommunityToolkit.Mvvm.Input;
using HexView.Avalonia.Controls;
using HexView.Avalonia.Services;
using Markdown.Avalonia;
using TextMateSharp.Grammars;

namespace AutoDev.Views.Content;

public partial class EditTabView : UserControl
{
    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private readonly TaskSyntaxColorizer _taskColorizer = new();
    private TextEditor? _editor;
    private TextMate.Installation? _textMateInstallation;
    private bool _isSyncingFromVm;
    private MarkdownScrollViewer? _markdownPreview;
    private HexViewControl? _hexViewer;
    private TextBox? _findBox;

    /// <summary>The memory-mapped file backing _hexViewer's current LineReader, and the path it maps - see UpdateHexView. Disposed as soon as a different file loads (hex or not) or this View detaches, so a hex-viewed file is never held open longer than it's actually on screen.</summary>
    private MemoryMappedLineReader? _hexReader;
    private string? _hexReaderPath;

    /// <summary>
    /// One AvaloniaEdit TextDocument per open file path, each with its own independent UndoStack (a fresh
    /// TextDocument gets a fresh one) - reused across switches so undo/redo history stays isolated per file
    /// instead of all files sharing the single TextEditor's default document. See OnFileLoaded.
    /// </summary>
    private readonly Dictionary<string, TextDocument> _documentsByPath = [];

    /// <summary>
    /// The EditTabViewModel OnDataContextChanged is currently subscribed to, if any - tracked so it can
    /// unsubscribe before subscribing to whatever replaces it. Without this, switching the top-level
    /// workspace tab strip (which Avalonia can satisfy by recycling this same EditTabView instance across
    /// different WorkspaceTabViewModels, rather than tearing it down - see WorkspaceTabView's own identical
    /// fix) left the OLD tab's ViewModel still subscribed forever. That old, now-backgrounded tab's own
    /// NavigateToMatch (e.g. from its own find bar recomputing for any reason while backgrounded) then fired
    /// straight into THIS view's OnNavigateToMatch/_editor, which by then displayed a completely different,
    /// differently-sized file - Select(offset, length) with an offset valid for the old tab's document but
    /// not the new one's crashed with an ArgumentOutOfRangeException.
    /// </summary>
    private EditTabViewModel? _subscribedVm;

    /// <summary>The markdown-preview counterpart to _findBox's plain-text search - each entry is one match's owning CTextBlock plus its (start, length) within that block's own Text. Rebuilt from scratch by RecomputePreviewMatches on every query/option change, since the preview's whole CTextBlock tree is itself rebuilt from scratch on every Markdown change (see OnMarkdownPreviewPropertyChanged) - there's no stable identity to incrementally update against.</summary>
    private readonly List<(CTextBlock Block, int Start, int Length)> _previewMatches = [];

    /// <summary>0-based index into _previewMatches of whichever match is currently selected/scrolled-to; -1 while there's no current match. The ViewModel's own FindCurrentMatchIndex (1-based, shared with plain-text mode's "N of M" display) is always kept one higher than this.</summary>
    private int _previewMatchIndex = -1;

    /// <summary>Whichever CTextBlock NavigateToPreviewMatch last selected, if any - cleared before selecting a new one (or when the bar closes/query changes to no matches) so a stale highlight never lingers on a block that's no longer the current match.</summary>
    private CTextBlock? _previewSelectedBlock;

    public EditTabView()
    {
        InitializeComponent();
        _editor = this.FindControl<TextEditor>("Editor");
        if (_editor is not null)
        {
            _textMateInstallation = _editor.InstallTextMate(_registryOptions);
            _editor.TextChanged += OnEditorTextChanged;

            // AvaloniaEdit's built-in hyperlink detection (e.g. URLs inside XML attribute values)
            // defaults TextView.LinkTextForegroundBrush to Brushes.Blue (#0000FF), which is hard to
            // read against our dark background and is unrelated to/not overridden by the TextMate theme.
            _editor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.Parse("#4FC1FF"));

            // The line number margin butts directly against the text with no gap of its own - Editor's
            // outer Padding only affects the far left/right edges, not this internal seam.
            if (_editor.TextArea.LeftMargins.OfType<LineNumberMargin>().FirstOrDefault() is { } lineNumberMargin)
            {
                lineNumberMargin.Margin = new Thickness(0, 0, 10, 0);
            }
        }

        _markdownPreview = this.FindControl<MarkdownScrollViewer>("MarkdownPreview");
        if (_markdownPreview is not null)
        {
            _markdownPreview.PropertyChanged += OnMarkdownPreviewPropertyChanged;

            // The code-block copy button (Button.CopyButton) doesn't exist yet when ApplyMarkdownCodeColors
            // runs after a Markdown change - CodePad (Markdown.Avalonia.SyntaxHigh) only adds it to its own
            // Panel.Children lazily, the moment the pointer first enters that code block (see
            // ApplyMarkdownCopyButtonFix's own doc comment) - so there's nothing to patch yet at that point.
            // PointerMoved bubbles (unlike PointerEntered/Exited, which Avalonia only routes directly to
            // whichever control's own bounds were entered, never to ancestors), so this reliably re-scans
            // every time the pointer moves anywhere over the preview, catching the button the first moment
            // it actually exists.
            _markdownPreview.PointerMoved += (_, _) => ApplyMarkdownCopyButtonFix();

            // Engine already holds a live default Markdown instance the moment MarkdownScrollViewer itself
            // is constructed (see its own ctor) - safe to configure right away, before any Markdown content
            // is ever set. See OnMarkdownLinkClicked for the three kinds of link this handles.
            if (_markdownPreview.Engine is Markdown.Avalonia.Markdown engine)
            {
                engine.HyperlinkCommand = new RelayCommand<string?>(OnMarkdownLinkClicked);
            }
        }

        _hexViewer = this.FindControl<HexViewControl>("HexViewer");

        _findBox = this.FindControl<TextBox>("FindBox");
        _findBox?.AddHandler(KeyDownEvent, OnFindBoxKeyDown, RoutingStrategies.Tunnel);

        DataContextChanged += OnDataContextChanged;

        // See GenerateTabView's constructor comment: FocusRequested alone races the TabControl on a
        // tab's first-ever activation, so AttachedToVisualTree backs it up.
        AttachedToVisualTree += (_, _) => FocusEditor();

        // Releases the memory-mapped file the moment this tab closes, rather than leaving it open for the
        // rest of the process's lifetime - see _hexReader's own doc comment.
        DetachedFromVisualTree += (_, _) => TeardownHexView();

        // Tunnel (not bubble): caught here before any descendant (the text editor, a button) gets a chance
        // to consume Left/Right itself, so Alt+Left/Alt+Right navigate file history from anywhere focus
        // happens to be within this tab.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (e.Key == Key.Left && Vm.GoBackCommand.CanExecute(null))
            {
                Vm.GoBackCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Right && Vm.GoForwardCommand.CanExecute(null))
            {
                Vm.GoForwardCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        // Caught at the Tunnel stage so it works no matter where focus already is within this tab (inside
        // the AvaloniaEdit editor itself included) - matches Alt+Left/Right's own reasoning above. Gated on
        // CanFind since there's nothing to search while an image/hex view or the still-unconfirmed large-file
        // warning is showing instead (the header's own Find button is hidden the same way - see EditTabView.axaml).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F && Vm.CanFind)
        {
            // Seeds the query from the current editor selection, standard find-bar convention - but only when
            // actually opening fresh; re-pressing Ctrl+F while already open just refocuses/reselects the
            // existing query instead of clobbering whatever the user already typed. Only meaningful in
            // ShowTextEditor mode - the rendered preview's own CTextBlocks have no equivalent notion of "the
            // AvaloniaEdit selection" to seed from.
            if (!Vm.IsFindBarOpen)
            {
                if (Vm.ShowTextEditor && _editor is { SelectionLength: > 0 })
                {
                    Vm.FindText = _editor.SelectedText;
                }

                Vm.IsFindBarOpen = true;
            }
            else
            {
                FocusFindBox();
            }

            e.Handled = true;
        }
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Vm.FindPreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                Vm.FindNextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                Vm.IsFindBarOpen = false;
                FocusEditor();
                e.Handled = true;
                break;
        }
    }

    private void FocusFindBox() => Dispatcher.UIThread.Post(() =>
    {
        _findBox?.Focus();
        _findBox?.SelectAll();
    }, DispatcherPriority.Background);

    /// <summary>
    /// EditTabViewModel.NavigateToMatch's handler - selects and scrolls the match into view, the same
    /// ScrollTo+BringCaretToView pattern OnFileLoaded's own seekToLine handling below already uses. Guards
    /// against offset/length no longer fitting the editor's CURRENT document - the offset was computed
    /// against Vm.Content at the moment the match was found, which is normally identical to
    /// _editor.Document's own text, but see _subscribedVm's own doc comment for one real way (now fixed)
    /// those two could momentarily disagree; kept here too as cheap insurance against any other such case,
    /// since silently skipping a stale navigation is far better than crashing the whole app over it.
    /// </summary>
    private void OnNavigateToMatch(int offset, int length)
    {
        if (_editor?.Document is not { } document || offset < 0 || length < 0 || offset + length > document.TextLength)
        {
            return;
        }

        _editor.Select(offset, length);
        var location = document.GetLocation(offset);
        _editor.ScrollTo(location.Line, location.Column);
        _editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>
    /// HyperlinkCommand.Execute for every link clicked in the rendered markdown preview - url is passed
    /// through exactly as written in the markdown (Markdown.Avalonia's own documented contract: a relative
    /// link stays relative, an absolute one stays absolute). A same-document "#section" anchor is handled
    /// entirely here (see ScrollToHeading) since it needs the live rendered visual/logical tree, which
    /// EditTabViewModel deliberately has no access to; everything else (a web URL, or a relative/absolute
    /// file path) goes to EditTabViewModel.HandleMarkdownLink instead.
    /// </summary>
    private void OnMarkdownLinkClicked(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (url.StartsWith('#'))
        {
            ScrollToHeading(url[1..]);
            return;
        }

        Vm?.HandleMarkdownLink(url);
    }

    /// <summary>
    /// Finds the rendered heading (Classes "Heading1".."Heading6", the exact set Markdown.Avalonia's own
    /// HeaderElement applies) whose GitHub-style slug matches anchor, and scrolls it into view - a safe
    /// no-op if nothing matches. Same GetLogicalDescendants() walk ApplyMarkdownCodeColors already uses
    /// against this same control - a fresh scan on every click rather than a cached slug-to-control map,
    /// since the preview's whole logical tree is rebuilt from scratch on every markdown change anyway (see
    /// OnMarkdownPreviewPropertyChanged), which would immediately invalidate any such cache.
    /// </summary>
    private void ScrollToHeading(string anchor)
    {
        if (_markdownPreview is null || anchor.Length == 0)
        {
            return;
        }

        var heading = _markdownPreview.GetLogicalDescendants()
            .OfType<CTextBlock>()
            .FirstOrDefault(t =>
                t.Classes.Any(c => c is "Heading1" or "Heading2" or "Heading3" or "Heading4" or "Heading5" or "Heading6") &&
                string.Equals(Slugify(t.Text), anchor, StringComparison.OrdinalIgnoreCase));

        heading?.BringIntoView();
    }

    /// <summary>
    /// EditTabViewModel.PreviewSearchInvalidated's handler - the markdown-preview counterpart to
    /// EditTabViewModel.RecomputeFindMatches, run here instead of there since matches live in the rendered
    /// CTextBlock visual tree rather than a single Content string a plain int offset could address. Scans
    /// every CTextBlock in document order (same GetLogicalDescendants() walk ScrollToHeading/
    /// ApplyMarkdownCodeColors already use against this same control), searching each one's own Text
    /// independently - see TextSearch's own doc comment for why a match can't span across two blocks (e.g.
    /// across a paragraph break). Always jumps to the first match (unlike text mode's RecomputeFindMatches,
    /// which tries to stay near the previous match's position) - there's no equivalent notion of "nearby" to
    /// preserve across a CTextBlock tree that's rebuilt from scratch on every markdown change anyway.
    /// </summary>
    private void RecomputePreviewMatches()
    {
        _previewSelectedBlock?.ClearSelection();
        _previewSelectedBlock = null;
        _previewMatches.Clear();
        _previewMatchIndex = -1;

        if (Vm is not { IsFindBarOpen: true, ShowMarkdownPreview: true } vm || _markdownPreview is null)
        {
            return;
        }

        if (vm.FindText.Length > 0)
        {
            foreach (var block in _markdownPreview.GetLogicalDescendants().OfType<CTextBlock>())
            {
                foreach (var offset in TextSearch.FindAllMatches(block.Text, vm.FindText, vm.FindMatchCase, vm.FindMatchWholeWord))
                {
                    _previewMatches.Add((block, offset, vm.FindText.Length));
                }
            }
        }

        vm.FindMatchCount = _previewMatches.Count;
        vm.FindCurrentMatchIndex = _previewMatches.Count > 0 ? 1 : 0;

        if (_previewMatches.Count > 0)
        {
            NavigateToPreviewMatch(0);
        }
    }

    /// <summary>EditTabViewModel.PreviewMatchMoveRequested's handler - the markdown-preview counterpart to EditTabViewModel.MoveToMatch, run here instead of there for the same reason RecomputePreviewMatches is (see its own doc comment).</summary>
    private void MovePreviewMatch(int direction)
    {
        if (Vm is not { } vm || _previewMatches.Count == 0)
        {
            return;
        }

        _previewMatchIndex = ((_previewMatchIndex + direction) % _previewMatches.Count + _previewMatches.Count) % _previewMatches.Count;
        vm.FindCurrentMatchIndex = _previewMatchIndex + 1;
        NavigateToPreviewMatch(_previewMatchIndex);
    }

    /// <summary>Selects (highlights) and scrolls to _previewMatches[index], clearing whichever match was previously selected first - CTextBlock.Select takes absolute begin/end character offsets into its own Text, not a (start, length) pair like AvaloniaEdit.TextEditor.Select.</summary>
    private void NavigateToPreviewMatch(int index)
    {
        _previewSelectedBlock?.ClearSelection();

        if (index < 0 || index >= _previewMatches.Count)
        {
            _previewSelectedBlock = null;
            return;
        }

        var (block, start, length) = _previewMatches[index];
        block.Select(start, start + length);
        block.BringIntoView();
        _previewSelectedBlock = block;
        _previewMatchIndex = index;
    }

    /// <summary>Mirrors GitHub's own heading-anchor algorithm closely enough for real-world use: lowercase, spaces to hyphens, anything other than a letter/digit/hyphen/underscore stripped. Doesn't handle GitHub's duplicate-heading "-1"/"-2" suffixing - the first (and in practice only) heading with a given text always wins here.</summary>
    private static string Slugify(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        Span<char> buffer = stackalloc char[text.Length];
        var length = 0;
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                buffer[length++] = ch;
            }
            else if (ch == ' ' && length > 0 && buffer[length - 1] != '-')
            {
                buffer[length++] = '-';
            }
        }

        return new string(buffer[..length]).Trim('-');
    }

    private EditTabViewModel? Vm => DataContext as EditTabViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.FocusRequested -= FocusEditor;
            _subscribedVm.FileLoaded -= OnFileLoaded;
            _subscribedVm.NavigateToMatch -= OnNavigateToMatch;
            _subscribedVm.FindBarFocusRequested -= FocusFindBox;
            _subscribedVm.PreviewSearchInvalidated -= RecomputePreviewMatches;
            _subscribedVm.PreviewMatchMoveRequested -= MovePreviewMatch;
            _subscribedVm = null;
        }

        if (Vm is null)
        {
            return;
        }

        Vm.FocusRequested += FocusEditor;
        Vm.FileLoaded += OnFileLoaded;
        Vm.NavigateToMatch += OnNavigateToMatch;
        Vm.FindBarFocusRequested += FocusFindBox;
        Vm.PreviewSearchInvalidated += RecomputePreviewMatches;
        Vm.PreviewMatchMoveRequested += MovePreviewMatch;
        _subscribedVm = Vm;
        OnFileLoaded(null);
    }

    private void FocusEditor() => Dispatcher.UIThread.Post(() => _editor?.Focus(), DispatcherPriority.Background);

    /// <summary>
    /// The sole place Vm.Content flows into the editor - fires on every completed load, including a reload
    /// of the same path. Switches to that path's own cached TextDocument (creating one if this is the first
    /// time it's been opened), so each file's undo/redo history survives switching away and back untouched
    /// by other files' edits. A reload (or any case where the cached document's text no longer matches
    /// Vm.Content - e.g. an external change) starts a fresh document instead of reusing one, since old undo
    /// entries wouldn't correspond to the content now on screen anyway.
    ///
    /// There is deliberately no separate "Content changed" handler reacting mid-load: Content's own
    /// PropertyChanged fires before this (LoadCoreAsync sets Content, then Kind/CurrentFilePath, then raises
    /// FileLoaded), and at that point _editor.Document is still the OUTGOING file's document - syncing Vm's
    /// new Content into it there would silently overwrite the wrong (still-active-for-a-moment) document
    /// with the new file's text, corrupting whatever file was open before the switch.
    ///
    /// Guards on HasTextContent rather than ShowTextEditor deliberately: a markdown file opened straight into
    /// "View" mode still needs its document prepared now, so toggling to Edit/Dual later shows the right text
    /// immediately instead of a stale or empty document (mode toggling alone never re-runs this method).
    /// </summary>
    private void OnFileLoaded(int? seekToLine)
    {
        UpdateHexView();

        if (_editor is null || Vm is null || !Vm.HasTextContent || Vm.CurrentFilePath is not { } path)
        {
            // No document to attach a grammar to (image/binary file, or editor not ready yet) - still update
            // the language so a later text file open isn't left pointing at whatever grammar the previous
            // text file used.
            UpdateLanguage();
            return;
        }

        if (!_documentsByPath.TryGetValue(path, out var document) || document.Text != Vm.Content)
        {
            document = new TextDocument(Vm.Content);
            _documentsByPath[path] = document;
        }

        _isSyncingFromVm = true;
        _editor.Document = document;
        _isSyncingFromVm = false;

        // Deliberately called AFTER the document swap above, not before: SetGrammar's own initial tokenize
        // pass runs against whatever document is CURRENTLY attached to the editor at the moment it's called,
        // so calling it first (the previous order) tokenized the OUTGOING file's now-detached document
        // instead of this one. The new document then sat untokenized - visible but uncolored - until some
        // unrelated later event (a scroll, a keystroke) forced AvaloniaEdit to rebuild its visual lines and
        // incidentally pick up the correct grammar along the way. Calling it here, once the real document is
        // already attached, tokenizes the right content immediately.
        UpdateLanguage();

        // A content-search result click (see FileSearchViewModel.ContentResultChosen) - jump the caret/scroll
        // to the matched line once the right document is actually attached.
        if (seekToLine is { } line)
        {
            var clampedLine = Math.Clamp(line, 1, document.LineCount);
            _editor.ScrollTo(clampedLine, 0);
            _editor.CaretOffset = document.GetLineByNumber(clampedLine).Offset;
            _editor.TextArea.Caret.BringCaretToView();
        }
    }

    /// <summary>Called on every OnFileLoaded, mirroring how that method drives the plain-text editor - maps CurrentFilePath into _hexViewer when Vm.IsHexView is on, or releases whatever was previously mapped when it isn't (a non-hex file loaded, or nothing loaded at all).</summary>
    private void UpdateHexView()
    {
        if (Vm is { IsHexView: true } && Vm.CurrentFilePath is { } path)
        {
            SetupHexView(path);
        }
        else
        {
            TeardownHexView();
        }
    }

    /// <summary>Memory-maps path and points _hexViewer at it - a no-op if that exact file is already mapped (e.g. an unrelated FileLoaded replay), so a hex file already on screen never gets briefly unmapped and remapped out from under the control.</summary>
    private void SetupHexView(string path)
    {
        if (_hexReader is not null && string.Equals(_hexReaderPath, path, StringComparison.Ordinal))
        {
            return;
        }

        TeardownHexView();

        _hexReader = new MemoryMappedLineReader(path);
        _hexReaderPath = path;

        if (_hexViewer is not null)
        {
            _hexViewer.LineReader = _hexReader;
            _hexViewer.HexFormatter = new HexFormatter(_hexReader.Length);
        }
    }

    private void TeardownHexView()
    {
        if (_hexReader is null)
        {
            return;
        }

        if (_hexViewer is not null)
        {
            _hexViewer.LineReader = null;
            _hexViewer.HexFormatter = null;
        }

        _hexReader.Dispose();
        _hexReader = null;
        _hexReaderPath = null;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isSyncingFromVm || Vm is null || _editor is null)
        {
            return;
        }

        Vm.Content = _editor.Text;
        UpdateTaskColorizerIfActive();
    }

    /// <summary>Re-parses the document's structure and forces a repaint - a no-op unless _taskColorizer is actually attached right now (see UpdateLanguage), i.e. the open file is a .task file.</summary>
    private void UpdateTaskColorizerIfActive()
    {
        if (_editor is null || !_editor.TextArea.TextView.LineTransformers.Contains(_taskColorizer))
        {
            return;
        }

        _taskColorizer.UpdateStructure(_editor.Text);
        _editor.TextArea.TextView.Redraw();
    }

    private void UpdateLanguage()
    {
        if (Vm?.CurrentFilePath is null || _editor is null || _textMateInstallation is null)
        {
            return;
        }

        var extension = System.IO.Path.GetExtension(Vm.CurrentFilePath);
        var lineTransformers = _editor.TextArea.TextView.LineTransformers;

        if (extension.Equals(".task", StringComparison.OrdinalIgnoreCase))
        {
            // No bundled TextMate grammar exists for this app's own custom DSL - use a live, indentation-
            // aware colorizer instead (see TaskSyntaxColorizer), re-parsed on every edit rather than a static
            // XSHD grammar. Also clear TextMate's own grammar, left over from whatever file was open before
            // this one, so it doesn't ALSO try to recolor this text using unrelated rules - both engines
            // otherwise sit on the same TextView.LineTransformers list and would fight over the same text.
            _editor.SyntaxHighlighting = null;
            _textMateInstallation.SetGrammar(null!);
            if (!lineTransformers.Contains(_taskColorizer))
            {
                lineTransformers.Add(_taskColorizer);
            }

            UpdateTaskColorizerIfActive();
            return;
        }

        lineTransformers.Remove(_taskColorizer);
        _editor.SyntaxHighlighting = null;
        var language = _registryOptions.GetLanguageByExtension(extension);
        _textMateInstallation.SetGrammar(language is not null ? _registryOptions.GetScopeByLanguageId(language.Id) : null!);
    }

    private void OnMarkdownPreviewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == MarkdownScrollViewer.MarkdownProperty)
        {
            // Posted rather than applied inline: the control reparses/rebuilds its CCode/Border descendants
            // asynchronously off this same property-changed notification, so patching immediately would run
            // before they exist. Loaded priority runs after that rebuild's own layout pass completes.
            Dispatcher.UIThread.Post(ApplyMarkdownCodeColors, DispatcherPriority.Loaded);

            // The whole CTextBlock tree _previewMatches points into was just torn down and rebuilt from
            // scratch (e.g. mermaid diagrams finishing their own async render - see
            // EditTabViewModel.UpdateRenderedContent) - re-search against the new one. A safe no-op via
            // RecomputePreviewMatches' own guard whenever Find isn't open in preview mode right now anyway.
            Dispatcher.UIThread.Post(RecomputePreviewMatches, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Directly overrides Foreground/Background on every rendered code span/block, bypassing Style
    /// entirely - the built-in MarkdownStyleName theme's own selector for these
    /// (".Markdown_Avalonia_MarkdownViewer CCode"/"...Border.CodeBlock", scoped by an ancestor class) is more
    /// specific than anything this app can add via Application.Styles (see App.axaml's surviving CCode style,
    /// which only wins the one property - MonospaceFontFamily - the built-in theme never sets at all), so a
    /// Style-based override can't reliably beat it. A local property value always wins over any Style
    /// regardless of specificity, which is the whole point of doing it this way. Re-run on every Markdown
    /// change (see OnMarkdownPreviewPropertyChanged) since CCode/Border instances are recreated from scratch
    /// each time the document reparses - there's no persistent set of elements to patch once.
    /// </summary>
    private void ApplyMarkdownCodeColors()
    {
        if (_markdownPreview is null)
        {
            return;
        }

        var codeBackground = this.TryFindResource("HeaderBackgroundBrush", out var background) ? background as IBrush : null;
        var codeBorderBrush = this.TryFindResource("BorderSubtleBrush", out var borderBrush) ? borderBrush as IBrush : null;

        var codeBlocks = _markdownPreview.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("CodeBlock")).ToList();
        var tokensInsideCodeBlocks = codeBlocks.SelectMany(b => b.GetLogicalDescendants().OfType<CCode>()).ToHashSet();

        // A fenced block with a recognized language renders as a real embedded AvaloniaEdit.TextEditor
        // (Markdown.Avalonia.SyntaxHigh's CodePad), not as CCode/TextBlock - its SyntaxHighlighting is a
        // freestanding, per-instance definition (never registered globally, so it can only be reached
        // through the editor that already has it - see MarkdownCodeHighlightTheme's own doc comment).
        // Redrawn after recoloring for the same reason the main code editor needs it (see OnFileLoaded) - a
        // freshly-created TextEditor can render its text before ever tokenizing it, leaving it plain until
        // some unrelated event forces a redraw. FontFamily is set for the same reason as CCode's own
        // MonospaceFontFamily/the no-language TextBlock.CodeBlock case below - a plain embedded TextEditor's
        // own default is a proportional UI font, not monospace, and (being neither CCode nor a Style-reachable
        // Control class this app already targets) nothing else here was setting it at all.
        foreach (var codeEditor in codeBlocks.SelectMany(b => b.GetLogicalDescendants().OfType<AvaloniaEdit.TextEditor>()))
        {
            codeEditor.FontFamily = new FontFamily("monospace");

            if (codeEditor.SyntaxHighlighting is { } highlighting)
            {
                MarkdownCodeHighlightTheme.Apply(highlighting);
            }

            codeEditor.TextArea.TextView.Redraw();
        }

        foreach (var code in _markdownPreview.GetLogicalDescendants().OfType<CCode>())
        {
            if (codeBackground is not null)
            {
                code.Background = codeBackground;
            }

            // Only an inline code span (single-backtick `code`, no syntax highlighting) needs forcing to
            // white - the built-in theme's own default for CCode renders those in a hard-to-read purple on
            // this dark background. A syntax-highlighted fenced block's per-token CCode instances
            // (descendants of a CodeBlock-classed Border, tracked above) already carry their own correct
            // per-token color from SyntaxHighlight/MarkdownCodeHighlightTheme - forcing every CCode found
            // anywhere to white would blindly erase that coloring instead of just fixing the inline-span
            // case this override actually exists for.
            if (!tokensInsideCodeBlocks.Contains(code))
            {
                code.Foreground = Brushes.White;
            }
        }

        foreach (var block in codeBlocks)
        {
            if (codeBackground is not null)
            {
                block.Background = codeBackground;
            }

            if (codeBorderBrush is not null)
            {
                block.BorderBrush = codeBorderBrush;
            }
        }

        // A fenced block with no recognized language never reaches SyntaxHighlight's own per-token Run
        // coloring, so it renders as a plain TextBlock.CodeBlock - a completely different element from CCode,
        // needing its own patch (setting the TextBlock's own Foreground doesn't disturb any Run that a
        // recognized-language block's highlighter *did* color individually - a Run's own explicit Foreground
        // still wins over its parent TextBlock's). FontFamily is set for the same reason as CCode's own
        // MonospaceFontFamily above - this element's own default is the same crash-prone composite font list.
        foreach (var text in _markdownPreview.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("CodeBlock")))
        {
            text.Foreground = Brushes.White;
            text.FontFamily = new FontFamily("monospace");
        }

        ApplyMarkdownCopyButtonFix();
    }

    /// <summary>
    /// Directly overrides sizing on every rendered code-block copy button (Button.CopyButton, see
    /// Markdown.Avalonia.SyntaxHigh's CodeBlockElement/CodePad) - same rationale as ApplyMarkdownCodeColors'
    /// own doc comment (Style-based overrides can't reliably beat this library's own, more specific built-in
    /// styling; a local property value always wins regardless). FluentTheme's default Button sizing is
    /// noticeably taller than a single line of code, and CodePad's own layout sizes the whole code block to
    /// fit whichever is taller, its content or this button - so on a short code block, the button's default
    /// size alone made the block visibly grow the instant it appeared on hover. Shrinking it well below any
    /// realistic code line's height keeps that from happening.
    ///
    /// Unlike CCode/Border (patched once per Markdown change, since those always exist by then), CopyButton
    /// doesn't exist until the pointer first enters its code block - CodePad adds it to its own children
    /// lazily on hover, not eagerly on render - so this is also called from a PointerMoved handler on
    /// _markdownPreview (registered in the constructor) to catch it the moment it actually appears. Re-setting
    /// the same values on every subsequent move is harmless - it's a handful of cheap property sets, not
    /// worth tracking "already patched" state for.
    /// </summary>
    private void ApplyMarkdownCopyButtonFix()
    {
        if (_markdownPreview is null)
        {
            return;
        }

        foreach (var button in _markdownPreview.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("CopyButton")))
        {
            button.Padding = new Thickness(4, 0);
            button.MinHeight = 0;
            button.MinWidth = 0;
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(2);
            button.FontSize = 10;
        }
    }
}
