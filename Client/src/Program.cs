using Avalonia;
using Avalonia.Logging;
using Avalonia.X11;
using System;

namespace AutoDev;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // We never show a native menu or tray icon, and our own file/folder picker
            // (AvaloniaDialogService) is the only StorageProvider consumer - disabling the DBus-backed
            // variants avoids the xdg-desktop-portal round-trip entirely (falls back to Avalonia's
            // built-in GTK/managed picker) instead of just hiding its failures when no portal is reachable.
            // OverlayPopups renders popups inside their owning window's own surface instead of as
            // separate X11 windows - avoids a class of Avalonia-on-X11 bugs where a popup's GPU-backed
            // surface gets stuck and stops repainting until the process (or the machine) is restarted.
            .With(new X11PlatformOptions { UseDBusFilePicker = false, UseDBusMenu = false, OverlayPopups = true })
            .WithInterFont()
            // Avalonia itself (not this app) probes DBus at startup for OS theme/accent-color, IME, and
            // AT-SPI accessibility services - benign no-ops when no session/portal bus answers, but logged
            // at Warning by default. There's no per-feature toggle for those probes, so raise the floor to
            // Fatal to silence that noise while still surfacing anything that's actually a real problem.
            .LogToTrace(LogEventLevel.Fatal);
}
