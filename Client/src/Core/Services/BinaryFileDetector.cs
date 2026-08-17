namespace AutoDev.Core.Services;

/// <summary>Cheap best-effort binary/image detection for content search - an extension denylist first (no I/O), then a null-byte sniff of the first few KB for anything not on the list. Never throws; unreadable files are treated as binary (skip it rather than fail the whole search).</summary>
public static class BinaryFileDetector
{
    private const int SniffBytes = 8000;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif",
        ".pdf", ".zip", ".tar", ".gz", ".7z", ".rar",
        ".exe", ".dll", ".so", ".dylib", ".o", ".obj", ".bin", ".pdb",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp3", ".mp4", ".mov", ".avi", ".wav", ".flac",
        ".class", ".pyc", ".jar",
    };

    public static bool IsLikelyBinary(string path)
    {
        if (BinaryExtensions.Contains(Path.GetExtension(path)))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[SniffBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return true; // unreadable - treat as binary rather than let the caller's file read blow up the search
        }
    }
}
