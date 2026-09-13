namespace AutoDev.Tests.Core.Services;

/// <summary>Covers BinaryFileDetector's two detection paths - the no-I/O extension denylist, and the null-byte content sniff for everything else - plus its never-throws contract for unreadable paths.</summary>
public sealed class BinaryFileDetectorTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("BinaryFileDetectorTests").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>A denylisted extension is reported as binary purely from its name, even if the file doesn't exist on disk.</summary>
    [Theory]
    [InlineData("image.png")]
    [InlineData("archive.zip")]
    [InlineData("library.DLL")]
    public void IsLikelyBinary_DenylistedExtension_ReturnsTrueWithoutReadingTheFile(string fileName) =>
        Assert.True(BinaryFileDetector.IsLikelyBinary(Path.Combine(directory, fileName)));

    /// <summary>A file with a non-denylisted extension but a null byte in its first bytes is sniffed as binary.</summary>
    [Fact]
    public void IsLikelyBinary_NonListedExtensionWithNullByte_ReturnsTrue()
    {
        string path = Path.Combine(directory, "data.custom");
        File.WriteAllBytes(path, [(byte)'a', 0, (byte)'b']);

        Assert.True(BinaryFileDetector.IsLikelyBinary(path));
    }

    /// <summary>A plain-text file with no null bytes and no denylisted extension is reported as not binary.</summary>
    [Fact]
    public void IsLikelyBinary_PlainTextFile_ReturnsFalse()
    {
        string path = Path.Combine(directory, "notes.txt");
        File.WriteAllText(path, "just some ordinary text content");

        Assert.False(BinaryFileDetector.IsLikelyBinary(path));
    }

    /// <summary>A path that can't be read at all (doesn't exist) is treated as binary rather than letting the exception escape.</summary>
    [Fact]
    public void IsLikelyBinary_UnreadablePath_ReturnsTrue() =>
        Assert.True(BinaryFileDetector.IsLikelyBinary(Path.Combine(directory, "does-not-exist.custom")));

    /// <summary>An empty file (nothing to sniff) is not binary.</summary>
    [Fact]
    public void IsLikelyBinary_EmptyFile_ReturnsFalse()
    {
        string path = Path.Combine(directory, "empty.custom");
        File.WriteAllBytes(path, []);

        Assert.False(BinaryFileDetector.IsLikelyBinary(path));
    }
}
