namespace AutoDev.ViewModels.Sidebar;

public sealed class FileSearchResultViewModel(string fullPath, string relativePath)
{
    public string FullPath { get; } = fullPath;
    public string RelativePath { get; } = relativePath;
    public string FileName { get; } = System.IO.Path.GetFileName(fullPath);
}
