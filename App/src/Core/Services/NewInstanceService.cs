using System.Diagnostics;

namespace AutoDev.Core.Services;

public sealed class NewInstanceService : INewInstanceService
{
    public void OpenNewInstance()
    {
        try
        {
            if (Environment.ProcessPath is not { Length: > 0 } processPath)
            {
                return;
            }

            Process.Start(new ProcessStartInfo(processPath) { UseShellExecute = false });
        }
        catch
        {
            // Best-effort - a launch failure here should never crash the current instance.
        }
    }
}
