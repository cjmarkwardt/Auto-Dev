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
    /// <inheritdoc />
    public async Task<IImage?> Load(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Position = 0;

        using var svg = new SKSvg();
        if (svg.Load(buffer) is null || svg.Picture is null)
        {
            return null;
        }

        using var png = new MemoryStream();
        svg.Picture.ToImage(png, SKColors.Transparent, SKEncodedImageFormat.Png, 100, 1f, 1f,
            SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        png.Position = 0;
        return new Bitmap(png);
    }
}
