using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoDev.Core.Services;

/// <summary>Pre-processes markdown text before it reaches MarkdownScrollViewer, since Markdown.Avalonia.Tight has no plugin API for a custom diagram type - see EditTabViewModel.RenderedContent/GenerateRequestViewModel.RenderedOutput.</summary>
public static partial class MermaidMarkdownProcessor
{
    [GeneratedRegex(@"```mermaid\s*\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex MermaidBlockPattern();

    /// <summary>Matches an ER relationship line whose two entity names are identical (e.g. `Folder ||--o{ Folder : "parent of"`) - see ErSelfRelationshipWorkaround.</summary>
    [GeneratedRegex(@"^[ \t]*(\S+)[ \t]+[|o{}]+--[|o{}]+[ \t]+\1[ \t]*:.*\r?\n?", RegexOptions.Multiline)]
    private static partial Regex ErSelfRelationshipPattern();

    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "AutoDev", "mermaid-cache");

    /// <summary>
    /// Mermaider 0.12.1's ER-diagram layout engine corrupts the layout of the *entire* diagram when a
    /// self-referencing relationship is present (e.g. a folder hierarchy's parent/child edge) - every
    /// entity collapses into an overlapping stack with connector lines routed straight through box
    /// interiors and PK/FK badges overlapping crossing-indicators. There's no RenderOptions knob to avoid
    /// it and Mermaider ships as a compiled NuGet package, not vendored source, so it can't be patched
    /// here. Stripping just the offending relationship line sidesteps the bug entirely; the same
    /// relationship is still visible via the FK attribute already listed in the entity's own field list,
    /// so no information is lost from the rendered diagram.
    /// </summary>
    private static string ErSelfRelationshipWorkaround(string source)
    {
        return source.StartsWith("erDiagram", StringComparison.Ordinal)
            ? ErSelfRelationshipPattern().Replace(source, string.Empty)
            : source;
    }

    /// <summary>Replaces every ```mermaid fenced block with a standard image reference to a rendered PNG, cached on disk by a hash of the diagram source (so re-rendering identical text - e.g. re-opening the same file - is just a cache hit). A block that fails to render (see MermaidRenderer.TryRender) is left untouched, so it falls back to rendering as a plain code block instead of a broken image.</summary>
    public static string Process(string markdown)
    {
        if (!markdown.Contains("```mermaid", StringComparison.Ordinal))
        {
            return markdown; // fast path - no regex work for the common case of no diagrams at all
        }

        return MermaidBlockPattern().Replace(markdown, match =>
        {
            var source = ErSelfRelationshipWorkaround(match.Groups[1].Value.Trim());
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
            var pngPath = Path.Combine(CacheDir, $"{hash}.png");

            if (!File.Exists(pngPath))
            {
                var png = MermaidRenderer.TryRender(source);
                if (png is null)
                {
                    return match.Value;
                }

                Directory.CreateDirectory(CacheDir);
                File.WriteAllBytes(pngPath, png);
            }

            return $"![Mermaid diagram]({new Uri(pngPath).AbsoluteUri})";
        });
    }
}
