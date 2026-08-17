using Markdown.Avalonia.Plugins;

namespace AutoDev.Core.Services;

/// <summary>Markdown.Avalonia plugin that registers SvgImageResolver - see that class for why this exists instead of just using the Markdown.Avalonia.Svg package directly. Registered on MarkdownScrollViewer.Plugins in EditTabView.axaml/GenerateTabView.axaml.</summary>
public sealed class SvgMarkdownPlugin : IMdAvPlugin
{
    /// <inheritdoc />
    public void Setup(SetupInfo info) => info.Register(new SvgImageResolver());
}
