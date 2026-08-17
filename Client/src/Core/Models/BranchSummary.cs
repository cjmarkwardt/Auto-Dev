namespace AutoDev.Core.Models;

/// <summary>One row in the History tab's flat branch list. See BranchInfo.IsPublic.</summary>
public sealed record BranchSummary(string Id, string Name, string? ParentId, bool IsPublic, bool IsCurrent);
