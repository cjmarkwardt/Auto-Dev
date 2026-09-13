using System.Diagnostics;

namespace AutoDev.Core.Services;

public sealed class ExternalOpenService : IExternalOpenService
{
    public void OpenFolder(string directoryPath) => Launch(directoryPath);

    public void OpenUrl(string url) => Launch(url);

    /// <summary>The same OS hand-off (Explorer on Windows, whatever xdg-open resolves to on Linux) opens either a folder in the file manager or a URL in the default browser - the OS itself decides which based on the target, not this app.</summary>
    private static void Launch(string target)
    {
        try
        {
            ProcessStartInfo startInfo;
            if (OperatingSystem.IsWindows())
            {
                startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            }
            else if (OperatingSystem.IsLinux())
            {
                startInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            }
            else
            {
                return;
            }

            startInfo.ArgumentList.Add(target);
            Process.Start(startInfo);
        }
        catch
        {
            // Best-effort - a missing file manager/browser binary should never crash the app.
        }
    }
}
