namespace AutoDev.ViewModels.Sidebar;

public sealed class ContentSearchResultViewModel(string fullPath, string relativePath, int lineNumber, string snippet)
{
    public string FullPath { get; } = fullPath;
    public string RelativePath { get; } = relativePath;
    public int LineNumber { get; } = lineNumber;
    public string Snippet { get; } = snippet;
    public string FileName { get; } = System.IO.Path.GetFileName(fullPath);
}
