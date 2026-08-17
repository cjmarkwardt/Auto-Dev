namespace AutoDev.Core.Models;

public enum GitTargetKind
{
    Branch,
    Tag,
    Commit,
}

/// <summary>What the Version sidebar section displays - derived fresh from git state every call, never cached, so it can never drift from what's actually checked out (see IWorkspaceVersioningService).</summary>
public sealed record GitTarget(GitTargetKind Kind, string Ref, BranchInfo? Branch = null);
