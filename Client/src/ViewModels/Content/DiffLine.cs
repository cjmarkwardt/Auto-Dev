namespace AutoDev.ViewModels.Content;

public enum DiffLineKind
{
    Unchanged,
    Added,
    Removed,
}

/// <summary>One row of the Edit tab's read-only Diff mode (see EditTabViewModel.LoadDiffAsync) - OldLineNumber/NewLineNumber are each null on whichever side a line doesn't exist on (an Added line has no old number, a Removed line no new one), for the gutter's before/after columns.</summary>
public sealed record DiffLine(string Text, DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber);
