namespace AutoDev.Tests.Core.Services;

/// <summary>
/// Covers BranchConvention's pure name/message helpers (Slugify, BuildBaseCommitMessage,
/// TryParseBaseCommitMessage) and the two IGitService-driven lookups, exercised against a mocked IGitService
/// rather than a real git repository.
/// </summary>
public sealed class BranchConventionTests
{
    /// <summary>Slugify lowercases, collapses any run of non-alphanumeric characters to a single "-", and trims leading/trailing "-".</summary>
    [Theory]
    [InlineData("My Feature", "my-feature")]
    [InlineData("  Leading and Trailing  ", "leading-and-trailing")]
    [InlineData("Multiple---Dashes", "multiple-dashes")]
    [InlineData("Already-Slugged", "already-slugged")]
    [InlineData("Über Cool!!", "ber-cool")]
    public void Slugify_NormalizesToLowercaseDashCase(string input, string expected) =>
        Assert.Equal(expected, BranchConvention.Slugify(input));

    /// <summary>BuildBaseCommitMessage's "*" marker sits right after "~" and is present only for a private branch.</summary>
    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "*")]
    public void BuildBaseCommitMessage_MarksPrivacyWithStarAfterTilde(bool isPublic, string expectedStar) =>
        Assert.Equal($"Feature ~{expectedStar}[main>feature]", BranchConvention.BuildBaseCommitMessage("Feature", "main", isPublic, "feature"));

    /// <summary>A root branch (no parent) has an empty parentId segment between "[" and ">".</summary>
    [Fact]
    public void BuildBaseCommitMessage_RootBranch_HasEmptyParentSegment() =>
        Assert.Equal("Main ~[>main]", BranchConvention.BuildBaseCommitMessage("Main", null, true, "main"));

    /// <summary>TryParseBaseCommitMessage is the exact inverse of BuildBaseCommitMessage across public/private and root/child combinations.</summary>
    [Theory]
    [InlineData("Main", null, true, "main")]
    [InlineData("Feature", "main", false, "feature")]
    [InlineData("Nested Name With Spaces", "parent-id", true, "child-id")]
    public void TryParseBaseCommitMessage_RoundTripsWithBuildBaseCommitMessage(string name, string? parentId, bool isPublic, string id)
    {
        string message = BranchConvention.BuildBaseCommitMessage(name, parentId, isPublic, id);

        bool success = BranchConvention.TryParseBaseCommitMessage(message, out string parsedName, out string? parsedParentId, out bool parsedIsPublic, out string parsedId);

        Assert.True(success);
        Assert.Equal(name, parsedName);
        Assert.Equal(parentId, parsedParentId);
        Assert.Equal(isPublic, parsedIsPublic);
        Assert.Equal(id, parsedId);
    }

    /// <summary>A commit subject that isn't shaped like a base-commit marker fails to parse and produces the documented empty/default out values.</summary>
    [Theory]
    [InlineData("just a regular commit message")]
    [InlineData("Feature ~[missing-closing-bracket")]
    [InlineData("")]
    public void TryParseBaseCommitMessage_NonMatchingSubject_ReturnsFalseWithDefaults(string subject)
    {
        bool success = BranchConvention.TryParseBaseCommitMessage(subject, out string name, out string? parentId, out bool isPublic, out string id);

        Assert.False(success);
        Assert.Equal("", name);
        Assert.Null(parentId);
        Assert.False(isPublic);
        Assert.Equal("", id);
    }

    /// <summary>FindBranchInfoByIdAsync turns the matching commit's parsed subject into a BranchInfo carrying that commit's own hash.</summary>
    [Fact]
    public async Task FindBranchInfoByIdAsync_MatchingCommit_ReturnsParsedBranchInfo()
    {
        GitCommit commit = new("abc123", "Feature ~[main>feature]", DateTimeOffset.UtcNow);
        Mock<IGitService> git = new();
        git.Setup(g => g.FindFirstCommitMatchingAsync("/repo", "feature", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commit);

        BranchInfo? result = await BranchConvention.FindBranchInfoByIdAsync(git.Object, "/repo", "feature");

        Assert.NotNull(result);
        Assert.Equal("feature", result.Id);
        Assert.Equal("Feature", result.Name);
        Assert.Equal("main", result.ParentId);
        Assert.True(result.IsPublic);
        Assert.Equal("abc123", result.BaseCommitHash);
    }

    /// <summary>No matching commit at all (a repo/branch predating this convention) resolves to null rather than throwing.</summary>
    [Fact]
    public async Task FindBranchInfoByIdAsync_NoMatchingCommit_ReturnsNull()
    {
        Mock<IGitService> git = new();
        git.Setup(g => g.FindFirstCommitMatchingAsync("/repo", "feature", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GitCommit?)null);

        BranchInfo? result = await BranchConvention.FindBranchInfoByIdAsync(git.Object, "/repo", "feature");

        Assert.Null(result);
    }

    /// <summary>A found commit whose subject nonetheless fails to parse as a base-commit marker also resolves to null.</summary>
    [Fact]
    public async Task FindBranchInfoByIdAsync_CommitWithUnparsableSubject_ReturnsNull()
    {
        GitCommit commit = new("abc123", "not a base commit", DateTimeOffset.UtcNow);
        Mock<IGitService> git = new();
        git.Setup(g => g.FindFirstCommitMatchingAsync("/repo", "feature", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commit);

        BranchInfo? result = await BranchConvention.FindBranchInfoByIdAsync(git.Object, "/repo", "feature");

        Assert.Null(result);
    }

    /// <summary>FindContainingBranchInfoAsync searches by the given ref name directly (a tag or detached commit) rather than by a branch id, using the looser "any base-commit marker" grep pattern.</summary>
    [Fact]
    public async Task FindContainingBranchInfoAsync_MatchingCommit_ReturnsParsedBranchInfo()
    {
        GitCommit commit = new("def456", "Release ~*[main>release]", DateTimeOffset.UtcNow);
        Mock<IGitService> git = new();
        git.Setup(g => g.FindFirstCommitMatchingAsync("/repo", "v1.0", BranchConvention.BaseCommitGrepPattern, It.IsAny<CancellationToken>()))
            .ReturnsAsync(commit);

        BranchInfo? result = await BranchConvention.FindContainingBranchInfoAsync(git.Object, "/repo", "v1.0");

        Assert.NotNull(result);
        Assert.Equal("release", result.Id);
        Assert.Equal("Release", result.Name);
        Assert.False(result.IsPublic);
    }
}
