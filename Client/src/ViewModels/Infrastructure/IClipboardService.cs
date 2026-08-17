namespace AutoDev.ViewModels.Infrastructure;

/// <summary>Seam for clipboard access so ViewModels stay Avalonia-free - see IDialogService for the same rationale.</summary>
public interface IClipboardService
{
    /// <summary>Copies text to the system clipboard - best-effort, never throws (a missing/unavailable clipboard should never crash the app).</summary>
    /// <param name="text">The text to place on the clipboard.</param>
    Task SetTextAsync(string text);
}
