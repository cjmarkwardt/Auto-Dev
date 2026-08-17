namespace AutoDev.Core.Models;

/// <summary>Every Version sidebar button's visibility, computed in one consolidated pass (see WorkspaceVersioningService.GetActionStateAsync) so each button doesn't re-query git on its own.</summary>
public sealed record VersionActionState(
    bool CanBranch,
    bool CanReset,
    bool CanSquash,
    bool CanRebase,
    bool CanMerge,
    bool CanRename,
    bool CanCommit)
{
    public static VersionActionState Empty { get; } = new(false, false, false, false, false, false, false);
}
