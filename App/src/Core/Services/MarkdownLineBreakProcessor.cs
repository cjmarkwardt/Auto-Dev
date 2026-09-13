using System.Text.RegularExpressions;

namespace AutoDev.Core.Services;

/// <summary>
/// Pre-processes markdown text before it reaches MarkdownScrollViewer, since Markdown.Avalonia.Tight has no
/// support for raw inline HTML - a literal `&lt;br&gt;` (or `&lt;br/&gt;`/`&lt;br /&gt;`) just passes through
/// as literal text instead of forcing a line break, unlike the CommonMark-standard "two trailing spaces then
/// a newline" hard break it does understand. Rewriting one into the other here is the same "the renderer
/// doesn't support this syntax natively, so translate it into syntax it does" idea as
/// MermaidMarkdownProcessor's own fenced-block rewriting.
/// </summary>
public static partial class MarkdownLineBreakProcessor
{
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagPattern();

    /// <summary>Splits out fenced code blocks (kept, via the capturing group, at every odd index of the result) so a `&lt;br&gt;` meant to be shown as literal example text inside one isn't rewritten into an actual line break.</summary>
    [GeneratedRegex(@"(```.*?```)", RegexOptions.Singleline)]
    private static partial Regex FencedCodeBlockPattern();

    /// <summary>Replaces every `&lt;br&gt;`-family tag outside a fenced code block with a CommonMark hard line break. Run after MermaidMarkdownProcessor.Process, not before - a raw Mermaid diagram source's own `&lt;br/&gt;` node-label syntax (already consumed into a rendered image by then) would otherwise be corrupted before Mermaid ever saw it.</summary>
    public static string Process(string markdown)
    {
        if (!markdown.Contains('<'))
        {
            return markdown; // fast path - no regex work for the common case of no <br> tags at all
        }

        string[] segments = FencedCodeBlockPattern().Split(markdown);
        for (int i = 0; i < segments.Length; i += 2)
        {
            segments[i] = BrTagPattern().Replace(segments[i], "  \n");
        }

        return string.Concat(segments);
    }
}
