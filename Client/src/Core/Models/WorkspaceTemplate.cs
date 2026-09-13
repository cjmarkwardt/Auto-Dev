namespace AutoDev.Core.Models;

/// <summary>A registered scaffolding template - just a reference to a `.md` file somewhere on disk (see ITemplateService). Content is deliberately never carried on this record; it's always re-read from Path at the moment a template is actually applied, so editing the file takes effect immediately with no stale in-memory copy.</summary>
public sealed record WorkspaceTemplate
{
    public required string Path { get; init; }

    /// <summary>Display name - just the file name without its `.md` extension, since nothing else about a template is ever registered separately from its file.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>A template the user just applied from the Templates dialog - its name (for the Generate request card's own display text) alongside the content actually read at that moment (see WorkspaceTemplate's own doc comment on why content is never cached).</summary>
public sealed record AppliedTemplate
{
    public required string Name { get; init; }

    public required string Content { get; init; }
}
