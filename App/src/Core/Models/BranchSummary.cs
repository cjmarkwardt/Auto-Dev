namespace AutoDev.Core.Models;

/// <summary>One row in the History tab's flat branch list - a branch's identity is just its own literal git branch name, nothing more.</summary>
public sealed record BranchSummary(string Name, bool IsCurrent);
