namespace AutoDev.Core.Models;

public sealed record WorkspaceInfo(string FullPath)
{
    public string Name => System.IO.Path.GetFileName(FullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                           is { Length: > 0 } name ? name : FullPath;
}
