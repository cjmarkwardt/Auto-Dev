namespace AutoDev.Core.Services;

/// <summary>Seam for launching the OS's own file manager/browser - kept separate from IFileTreeService since it shells out to another process rather than touching the filesystem directly.</summary>
public interface IExternalOpenService
{
    /// <summary>Opens directoryPath in the system's file manager (Explorer on Windows, whatever xdg-open resolves to on Linux) - best-effort, never throws.</summary>
    void OpenFolder(string directoryPath);

    /// <summary>Opens url in the system's default web browser (see EditTabViewModel.HandleMarkdownLink) - best-effort, never throws.</summary>
    void OpenUrl(string url);
}
