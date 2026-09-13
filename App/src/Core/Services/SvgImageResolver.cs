using Avalonia.Media;
using Avalonia.Media.Imaging;
using Markdown.Avalonia.Utils;
using SkiaSharp;
using Svg.Skia;

namespace AutoDev.Core.Services;

/// <summary>
/// Markdown.Avalonia image resolver for SVG sources (e.g. a shields.io badge referenced from a README) -
/// rasterizes via Svg.Skia, the same library MermaidRenderer already uses for Mermaid diagrams, rather than
/// relying on the Markdown.Avalonia.Svg package's own native-Avalonia SVG control. That control renders
/// shapes/paths fine but doesn't render &lt;text&gt; elements at all, which left every shields.io badge showing
/// only its colored background with no label. Registered by SvgMarkdownPlugin.
/// </summary>
public sealed class SvgImageResolver : IImageResolver
{
    /// <summary>
    /// Markdown.Avalonia tries every registered <see cref="IImageResolver"/> against EVERY image reference
    /// regardless of format, in turn against the same shared, rewound-before-each-attempt stream - but only
    /// rewinds it before handing it to the NEXT resolver, not before its own final fallback attempt to load
    /// the stream as a plain bitmap once every resolver has passed on it. Returning null here without also
    /// leaving `stream` back at position 0 left that fallback attempt reading from EOF - i.e. reading
    /// nothing - for every non-SVG image (a rendered Mermaid diagram, or any ordinary PNG/JPEG in a
    /// README), permanently showing Markdown.Avalonia's broken-image placeholder instead. Parsing PNG/JPEG
    /// bytes as SVG (XML) also throws rather than failing gracefully, which this catches for the same
    /// reason - Markdown.Avalonia has no try/catch of its own around a resolver's own Load call.
    /// </summary>
    /// <inheritdoc />
    public async Task<IImage?> Load(Stream stream)
    {
        using MemoryStream buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Position = 0;

        using SKSvg svg = new SKSvg();
        try
        {
            if (svg.Load(buffer) is null || svg.Picture is null)
            {
                stream.Position = 0;
                return null;
            }
        }
        catch
        {
            stream.Position = 0;
            return null;
        }

        using MemoryStream png = new MemoryStream();
        svg.Picture.ToImage(png, SKColors.Transparent, SKEncodedImageFormat.Png, 100, 1f, 1f,
            SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        png.Position = 0;
        return new Bitmap(png);
    }
}
