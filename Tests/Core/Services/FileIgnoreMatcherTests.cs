using AutoDev.Core.Services;

namespace AutoDev.Tests.Core.Services;

/// <summary>Covers FileIgnoreMatcher's own subset of the .gitignore pattern language - unanchored/anchored patterns, directory-only patterns, wildcards, negation ordering, and whole-subtree hiding via an ancestor match.</summary>
public sealed class FileIgnoreMatcherTests
{
    [Fact]
    public void IsMatch_UnanchoredPattern_MatchesAtAnyDepth()
    {
        FileIgnoreMatcher matcher = FileIgnoreMatcher.Parse(["node_modules"]);

        Assert.True(matcher.IsMatch("node_modules", isDirectory: true));
        Assert.True(matcher.IsMatch("src/node_modules", isDirectory: true));
        Assert.False(matcher.IsMatch("node_modules_backup", isDirectory: true));
    }

    [Fact]
    public void IsMatch_LeadingSlash_OnlyMatchesAtRoot()
    {
        FileIgnoreMatcher matcher = FileIgnoreMatcher.Parse(["/build"]);

        Assert.True(matcher.IsMatch("build", isDirectory: true));
        Assert.False(matcher.IsMatch("src/build", isDirectory: true));
    }

    [Fact]
    public void IsMatch_TrailingSlash_OnlyMatchesDirectories()
    {
        FileIgnoreMatcher matcher = FileIgnoreMatcher.Parse(["logs/"]);

        Assert.True(matcher.IsMatch("logs", isDirectory: true));
        Assert.False(matcher.IsMatch("logs", isDirectory: false));
    }

    [Fact]
    public void IsMatch_Wildcard_MatchesWithinOneSegment()
    {
        FileIgnoreMatcher matcher = FileIgnoreMatcher.Parse(["*.log"]);

        Assert.True(matcher.IsMatch("debug.log", isDirectory: false));
        Assert.True(matcher.IsMatch("logs/debug.log", isDirectory: false));
        Assert.False(matcher.IsMatch("debug.log.bak", isDirectory: false));
    }

    [Fact]
    public void IsMatch_DoubleWildcard_MatchesAcrossSegments()
    {
        FileIgnoreMatcher matcher = FileIgnoreMatcher.Parse(["a/**/b"]);

        Assert.True(matcher.IsMatch("a/b", isDirectory: false));
        Assert.True(matcher.IsMatch("a/x/y/b", isDirectory: false));
        Assert.False(matcher.IsMatch("a/c", isDirectory: false));
    }

    [Fact]
    public void IsMatch_DescendantOfMatchedDirectory_IsAlsoIgnored()
    {
        FileIgnoreMatcher matcher = FileIgnoreMatcher.Parse(["build/"]);

        Assert.True(matcher.IsMatch("build/output/app.exe", isDirectory: false));
    }

    [Fact]
    public void IsMatch_LaterNegationWins()
    {
        FileIgnoreMatcher matcher = FileIgnoreMatcher.Parse(["*.log", "!important.log"]);

        Assert.True(matcher.IsMatch("debug.log", isDirectory: false));
        Assert.False(matcher.IsMatch("important.log", isDirectory: false));
    }

    [Fact]
    public void IsMatch_CommentsAndBlankLines_AreIgnored()
    {
        FileIgnoreMatcher matcher = FileIgnoreMatcher.Parse(["# a comment", "", "*.tmp"]);

        Assert.True(matcher.IsMatch("scratch.tmp", isDirectory: false));
        Assert.False(matcher.IsMatch("# a comment", isDirectory: false));
    }

    [Fact]
    public void IsMatch_NoRulesMatch_ReturnsFalse()
    {
        FileIgnoreMatcher matcher = FileIgnoreMatcher.Parse(["*.log"]);

        Assert.False(matcher.IsMatch("README.md", isDirectory: false));
    }
}
