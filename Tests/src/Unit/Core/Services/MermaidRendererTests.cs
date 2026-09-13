using SkiaSharp;

namespace Tests.Core.Services;

public sealed class MermaidRendererTests
{
    /// <summary>
    /// A wide `flowchart LR` with enough nodes produces a picture whose pixel width would otherwise exceed
    /// what GPU-backed rendering can upload as a texture (most GPUs cap out around 8192-16384) - Markdown.
    /// Avalonia shows that failure as a silent broken-image icon rather than any error, since every earlier
    /// step (SVG generation, rasterization, PNG encode) succeeds. MermaidRenderer.TryRender is expected to
    /// scale the rendered image down to fit within a safe maximum dimension instead.
    /// </summary>
    [Fact]
    public void ScalesDownADiagramWiderThanTheSafeMaximumDimension()
    {
        string nodes = string.Join('\n', Enumerable.Range(0, 80).Select(i => $"    n{i}[Node {i}]"));
        string edges = string.Join('\n', Enumerable.Range(0, 79).Select(i => $"    n{i} --> n{i + 1}"));
        string source = $"flowchart LR\n{nodes}\n{edges}";

        byte[]? png = MermaidRenderer.TryRender(source);

        Assert.NotNull(png);
        using SKCodec codec = SkiaSharp.SKCodec.Create(new MemoryStream(png!));
        Assert.True(codec.Info.Width <= 8000);
        Assert.True(codec.Info.Height <= 8000);
    }
}
