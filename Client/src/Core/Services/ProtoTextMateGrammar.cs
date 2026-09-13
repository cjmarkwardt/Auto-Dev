using Avalonia.Platform;
using TextMateSharp.Grammars;

namespace AutoDev.Core.Services;

/// <summary>
/// Registers a Protocol Buffers (`.proto`) TextMate grammar with a TextMateSharp RegistryOptions -
/// TextMateSharp.Grammars only bundles the same language set VS Code itself ships with, which doesn't
/// include Protocol Buffers (that's a separate VS Code extension, not part of the core product). The
/// grammar/manifest pair (in the same VS Code extension `package.json` + `*.tmLanguage.json` shape every
/// bundled grammar already uses) is embedded as an Avalonia resource, then written out to a temp directory
/// once per process - RegistryOptions.LoadFromLocalFile only reads from real files on disk, not streams.
/// </summary>
public static class ProtoTextMateGrammar
{
    private static readonly string ExtractedDirectory = Path.Combine(Path.GetTempPath(), "AutoDev", "grammars", "proto");
    private static readonly Lock ExtractLock = new();
    private static bool isExtracted;

    /// <summary>Registers the "PROTO" grammar on the given RegistryOptions, extracting its files to disk first if this is the first call in this process - safe to call once per RegistryOptions instance (EditTabView creates its own per view instance).</summary>
    public static void Register(RegistryOptions registryOptions)
    {
        EnsureExtracted();
        registryOptions.LoadFromLocalFile("PROTO", Path.Combine(ExtractedDirectory, "package.json"));
    }

    private static void EnsureExtracted()
    {
        if (isExtracted)
        {
            return;
        }

        lock (ExtractLock)
        {
            if (isExtracted)
            {
                return;
            }

            Directory.CreateDirectory(ExtractedDirectory);
            ExtractResource("package.json");
            ExtractResource("proto.tmLanguage.json");
            isExtracted = true;
        }
    }

    private static void ExtractResource(string fileName)
    {
        using var resourceStream = AssetLoader.Open(new Uri($"avares://AutoDev/Assets/Grammars/proto/{fileName}"));
        using var fileStream = File.Create(Path.Combine(ExtractedDirectory, fileName));
        resourceStream.CopyTo(fileStream);
    }
}
