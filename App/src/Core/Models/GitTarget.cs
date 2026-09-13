namespace AutoDev.Core.Models;

public enum GitTargetKind
{
    Branch,
    Tag,
    Commit,
}

/// <summary>
/// What the Version section displays and Generate/Edit key off of - derived fresh from git state every call,
/// never cached, so it can never drift from what's actually checked out (see IWorkspaceVersioningService).
/// BranchName/TagName are set only for their respective Kind (both null while detached at a plain commit);
/// CommitHash/CommitMessage - HEAD's own short hash and one-line subject - are always set, since they're
/// meaningful regardless of what's checked out.
/// </summary>
public sealed record GitTarget(GitTargetKind Kind, string? BranchName, string? TagName, string CommitHash, string CommitMessage);
