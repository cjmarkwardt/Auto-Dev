namespace AutoDev.ViewModels.Content;

/// <summary>Extensions treated as images across the app - both Generate's paste-image attachment handling and the Edit tab's image viewer.</summary>
public static class ImageFileTypes
{
    public static readonly HashSet<string> Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
