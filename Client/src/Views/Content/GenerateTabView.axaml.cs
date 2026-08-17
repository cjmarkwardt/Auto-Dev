using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AutoDev.ViewModels.Content;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;

namespace AutoDev.Views.Content;

public partial class GenerateTabView : UserControl
{
    private ScrollViewer? _scroller;
    private TextBox? _inputBox;
    private MarkdownScrollViewer? _outputMarkdown;

    public GenerateTabView()
    {
        InitializeComponent();
        _scroller = this.FindControl<ScrollViewer>("Scroller");
        _inputBox = this.FindControl<TextBox>("InputBox");
        if (_inputBox is not null)
        {
            // Tunnel (not bubble): TextBox's own AcceptsReturn handling consumes Enter during the
            // bubble phase to insert a newline, so we must intercept during tunneling to get first look.
            _inputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        }

        // Same code-span/code-block/copy-button fixes as EditTabView.axaml.cs's MarkdownScrollViewer (see
        // ApplyMarkdownCodeColors/ApplyMarkdownCopyButtonFix's doc comments there for the full rationale) -
        // this is a second, independent MarkdownScrollViewer instance, so it needs its own patching.
        _outputMarkdown = this.FindControl<MarkdownScrollViewer>("OutputMarkdown");
        if (_outputMarkdown is not null)
        {
            _outputMarkdown.PropertyChanged += OnOutputMarkdownPropertyChanged;
            _outputMarkdown.PointerMoved += (_, _) => ApplyMarkdownCopyButtonFix();
        }

        DataContextChanged += OnDataContextChanged;

        // FocusRequested alone races the TabControl: switching SelectedTabIndex fires it synchronously,
        // but if this is the tab's first-ever activation the view (and its subscription) may not exist
        // yet at that instant. AttachedToVisualTree fires exactly when we're actually realized/visible,
        // which reliably covers that first-activation case regardless of the VM-side event's timing.
        AttachedToVisualTree += (_, _) => FocusInput();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is GenerateTabViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.FocusRequested += FocusInput;
        }
    }

    private void FocusInput() => Dispatcher.UIThread.Post(() => _inputBox?.Focus(), DispatcherPriority.Background);

    /// <summary>Switching to a different displayed request should show its input from the top, not wherever the scroll position happened to be left from the previously-displayed one.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GenerateTabViewModel.DisplayedIndex))
        {
            ScrollToTop();
        }
    }

    private void ScrollToTop() => Dispatcher.UIThread.Post(() => _scroller?.ScrollToHome(), DispatcherPriority.Background);

    private void OnOutputMarkdownPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == MarkdownScrollViewer.MarkdownProperty)
        {
            // Posted rather than applied inline - see EditTabView.axaml.cs's identical handler for why
            // (the control rebuilds its CCode/Border descendants asynchronously off this same notification).
            Dispatcher.UIThread.Post(ApplyMarkdownCodeColors, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Same fix as EditTabView.axaml.cs's ApplyMarkdownCodeColors - see that method's doc comment for the full rationale (local property values beat the library's own more-specific built-in styles).</summary>
    private void ApplyMarkdownCodeColors()
    {
        if (_outputMarkdown is null)
        {
            return;
        }

        var codeBackground = this.TryFindResource("HeaderBackgroundBrush", out var background) ? background as IBrush : null;
        var codeBorderBrush = this.TryFindResource("BorderSubtleBrush", out var borderBrush) ? borderBrush as IBrush : null;

        var codeBlocks = _outputMarkdown.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("CodeBlock")).ToList();
        var tokensInsideCodeBlocks = codeBlocks.SelectMany(b => b.GetLogicalDescendants().OfType<CCode>()).ToHashSet();

        // A fenced block with a recognized language (e.g. ```csharp) renders as a real embedded
        // AvaloniaEdit.TextEditor (Markdown.Avalonia.SyntaxHigh's CodePad), not as CCode/TextBlock at all -
        // its SyntaxHighlighting is a freestanding, per-instance definition (never registered globally, so
        // it can only be reached through the editor that already has it - see MarkdownCodeHighlightTheme's
        // own doc comment). Redrawn after recoloring for the same reason the main code editor needs it (see
        // EditTabView.axaml.cs's OnFileLoaded) - a freshly-created TextEditor can render its text before ever
        // tokenizing it, leaving it plain until some unrelated event forces a redraw.
        foreach (var editor in codeBlocks.SelectMany(b => b.GetLogicalDescendants().OfType<AvaloniaEdit.TextEditor>()))
        {
            if (editor.SyntaxHighlighting is { } highlighting)
            {
                MarkdownCodeHighlightTheme.Apply(highlighting);
            }

            editor.TextArea.TextView.Redraw();
        }

        foreach (var code in _outputMarkdown.GetLogicalDescendants().OfType<CCode>())
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

        foreach (var text in _outputMarkdown.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("CodeBlock")))
        {
            text.Foreground = Brushes.White;
            text.FontFamily = new FontFamily("monospace");
        }

        ApplyMarkdownCopyButtonFix();
    }

    /// <summary>Same fix as EditTabView.axaml.cs's ApplyMarkdownCopyButtonFix - see that method's doc comment for the full rationale (shrinks the lazily-added copy button so it can't grow a single-line code block on hover).</summary>
    private void ApplyMarkdownCopyButtonFix()
    {
        if (_outputMarkdown is null)
        {
            return;
        }

        foreach (var button in _outputMarkdown.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("CopyButton")))
        {
            button.Padding = new Thickness(4, 0);
            button.MinHeight = 0;
            button.MinWidth = 0;
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(2);
            button.FontSize = 10;
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (DataContext is GenerateTabViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        // Intercepted here (tunnel, before TextBox's own bubble-phase Paste()) so an image or file paste
        // never also falls through to the TextBox's default behavior (which would either do nothing for an
        // image-only clipboard, or paste a stray file:// URI as plain text). Handled is set unconditionally;
        // HandlePasteAsync falls back to _inputBox.Paste() itself for a plain-text clipboard, so normal
        // paste-as-text still works exactly as before.
        if (TextBox.PasteGesture?.Matches(e) == true)
        {
            e.Handled = true;
            _ = HandlePasteAsync();
        }
    }

    private static string MediaTypeForExtension(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// A pasted image can arrive as either raw pixel data (e.g. a screenshot tool's "copy image", or an
    /// in-browser "copy image") or a file reference (e.g. copied in a file manager) - tries bitmap data
    /// first, then falls back to files (attaching image files as images too, everything else by path
    /// reference), and only falls back to a normal text paste if the clipboard has neither.
    /// </summary>
    private async Task HandlePasteAsync()
    {
        if (DataContext is not GenerateTabViewModel vm)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is not null)
        {
            vm.AddImageAttachment(bitmap);
            return;
        }

        var files = await clipboard.TryGetFilesAsync();
        if (files is { Length: > 0 })
        {
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is null)
                {
                    continue;
                }

                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (ImageFileTypes.Extensions.Contains(extension))
                {
                    try
                    {
                        var bytes = await File.ReadAllBytesAsync(path);
                        vm.AddImageAttachment(bytes, MediaTypeForExtension(extension));
                        continue;
                    }
                    catch (IOException)
                    {
                        // fall through to a plain path reference
                    }
                }

                vm.AddFileReference(path);
            }

            return;
        }

        _inputBox?.Paste();
    }
}
