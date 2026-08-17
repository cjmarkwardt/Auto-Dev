namespace AutoDev.Core.Services;

public sealed class FileTreeService(IGitService gitService) : IFileTreeService
{
    private const string MetadataDirName = ".autodev";

    public IReadOnlyList<FileSystemEntry> GetChildren(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return [];
        }

        var directories = Directory.EnumerateDirectories(directoryPath)
            .Select(Path.GetFileName)
            .Where(n => n is { Length: > 0 } && n != MetadataDirName && n != ".git")
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new FileSystemEntry(n!, Path.Combine(directoryPath, n!), IsDirectory: true));

        var files = Directory.EnumerateFiles(directoryPath)
            .Select(Path.GetFileName)
            .Where(n => n is { Length: > 0 })
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new FileSystemEntry(n!, Path.Combine(directoryPath, n!), IsDirectory: false));

        return [.. directories, .. files];
    }

    public Task<string> ReadFileAsync(string filePath, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(filePath, cancellationToken);

    public Task WriteFileAsync(string filePath, string content, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(filePath, content, cancellationToken);

    public void CreateFile(string directoryPath, string fileName)
    {
        var path = Path.Combine(directoryPath, fileName);
        if (!File.Exists(path))
        {
            File.Create(path).Dispose();
        }
    }

    public void CreateFolder(string directoryPath, string folderName) =>
        Directory.CreateDirectory(Path.Combine(directoryPath, folderName));

    public void Rename(string path, string newName)
    {
        var isDirectory = Directory.Exists(path);
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Path has no parent directory.");
        var destination = Path.Combine(parent, newName);
        if (isDirectory)
        {
            Directory.Move(path, destination);
        }
        else
        {
            File.Move(path, destination);
        }
    }

    public void Delete(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }

    public void Duplicate(string path, bool isDirectory)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Path has no parent directory.");
        var destination = GenerateDuplicateName(parent, path, isDirectory);

        if (isDirectory)
        {
            CopyDirectoryRecursive(path, destination);
        }
        else
        {
            File.Copy(path, destination);
        }
    }

    private static string GenerateDuplicateName(string parent, string sourcePath, bool isDirectory)
    {
        string baseName;
        string extension;
        if (isDirectory)
        {
            baseName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            extension = "";
        }
        else
        {
            baseName = Path.GetFileNameWithoutExtension(sourcePath);
            extension = Path.GetExtension(sourcePath);
        }

        var candidate = Path.Combine(parent, $"{baseName} copy{extension}");
        var suffix = 2;
        while (PathExists(candidate))
        {
            candidate = Path.Combine(parent, $"{baseName} copy {suffix}{extension}");
            suffix++;
        }

        return candidate;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
        }
    }

    public void Move(string sourcePath, string destinationDirectory, bool overwrite = false)
    {
        var isDirectory = Directory.Exists(sourcePath);
        var baseName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destinationPath = Path.Combine(destinationDirectory, baseName);

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.Ordinal))
        {
            return; // already directly inside destinationDirectory (e.g. dropped onto its own parent) - not an error
        }

        if (PathExists(destinationPath))
        {
            if (!overwrite)
            {
                throw new IOException($"'{destinationPath}' already exists.");
            }

            DeletePathRecursive(destinationPath);
        }

        if (isDirectory)
        {
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath);
        }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void DeletePathRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>git resolves the repo root itself by walking up from wherever it's run, so path's own parent directory is always a safe (and simplest) working directory to run it from - it's guaranteed to be inside the same repo as path itself, whether path is a file or a directory.</summary>
    public async Task<GitFileStatus> GetStatusAsync(string path, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));
        return parent is null ? GitFileStatus.Unmodified : await gitService.GetStatusAsync(parent, path, cancellationToken);
    }
}
