using SkiaSharp;
using Svg.Skia;

namespace AutoDev.Core.Services;

/// <summary>Thin wrapper around Mermaider (pure .NET Mermaid-to-SVG rendering, no Node/browser needed) plus Svg.Skia (rasterizes the resulting SVG to PNG) - isolates the third-party API surface to this one file.</summary>
public static class MermaidRenderer
{
    /// <summary>
    /// Matches this app's own VS-Code-Dark+-ish palette (see Styles/VsCodeColors.axaml) rather than
    /// Mermaider's default light-page theme, which rendered as barely-visible dark-on-dark once composited
    /// onto AutoDev's own dark background. Bg is HeaderBackgroundBrush (a lighter node "card" than the page
    /// itself, same idiom other boxed chrome in this app already uses), Fg/Muted/Accent mirror
    /// TextPrimaryBrush/TextMutedBrush/AccentBrush.
    ///
    /// All seven DiagramColors roles are set explicitly, including Line/Surface/Border - Mermaider's own
    /// built-in themes deliberately leave those three blank and derive them at render time via CSS
    /// color-mix() on the SVG's Bg/Fg custom properties (see the README's Theming section), but Svg.Skia's
    /// underlying SVG renderer doesn't evaluate color-mix() (a very recent CSS Color Module 5 feature) -
    /// left blank, connector lines and ER-diagram table rows/borders rendered as unspecified/near-black
    /// instead of picking up any derived shade, which is what made them invisible against the dark
    /// background. Spelling out literal hex values for every role sidesteps needing color-mix() support at
    /// all.
    /// </summary>
    private static readonly Mermaider.Models.RenderOptions DarkOptions = new()
    {
        Bg = "#2D2D2D",
        Fg = "#CCCCCC",
        Line = "#969696",
        Accent = "#007ACC",
        Muted = "#969696",
        Surface = "#252525",
        Border = "#3C3C3C",
    };

    /// <summary>Renders Mermaid source to PNG bytes, or null if Mermaider/Svg.Skia fails to parse/render it (e.g. an unsupported diagram type) - callers should leave the original fenced block untouched on null rather than show a broken image.</summary>
    public static byte[]? TryRender(string mermaidSource)
    {
        try
        {
            var svgText = Mermaider.MermaidRenderer.RenderSvg(mermaidSource, DarkOptions);

            using var svg = new SKSvg();
            if (svg.FromSvg(svgText) is null || svg.Picture is null)
            {
                return null;
            }

            using var stream = new MemoryStream();
            svg.Picture.ToImage(stream, SKColors.Transparent, SKEncodedImageFormat.Png, 100, 1f, 1f,
                SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
