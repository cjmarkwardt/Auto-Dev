using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using AutoDev.ViewModels.Infrastructure;

namespace AutoDev.Infrastructure;

/// <inheritdoc cref="IClipboardService" />
public sealed class AvaloniaClipboardService : IClipboardService
{
    private static Window OwnerWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        ?? throw new InvalidOperationException("Main window is not available yet.");

    /// <inheritdoc />
    public async Task SetTextAsync(string text)
    {
        if (OwnerWindow.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
