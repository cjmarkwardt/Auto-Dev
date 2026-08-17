namespace AutoDev.Core.Models;

/// <summary>
/// A branch's identity/lineage as recovered from its base commit - see BranchConvention. ParentId is null for
/// a root branch (e.g. "main"). IsPublic marks a branch meant for collaboration between many users - it's
/// never squashed/renamed away (see VersionActionState's CanSquash/CanRename), unlike a private branch, which
/// one user works on alone and eventually squashes and merges/deletes.
/// </summary>
public sealed record BranchInfo(string Id, string Name, string? ParentId, bool IsPublic, string BaseCommitHash);
