namespace AutoDev.Tests.ViewModels.Content;

/// <summary>Covers TextSearch's non-overlapping substring scan, including case sensitivity, whole-word matching, and the non-overlap rule itself.</summary>
public sealed class TextSearchTests
{
    /// <summary>Every occurrence of the pattern is found when it doesn't overlap itself.</summary>
    [Fact]
    public void FindAllMatches_MultipleNonOverlappingOccurrences_FindsAll()
    {
        List<int> matches = TextSearch.FindAllMatches("the cat sat on the mat", "at", matchCase: false, matchWholeWord: false);

        Assert.Equal([5, 9, 20], matches);
    }

    /// <summary>A pattern that overlaps itself only matches non-overlapping occurrences - searching "aa" in "aaaa" finds 2 matches (at 0 and 2), not 3.</summary>
    [Fact]
    public void FindAllMatches_SelfOverlappingPattern_AdvancesPastEachMatchByItsOwnLength()
    {
        List<int> matches = TextSearch.FindAllMatches("aaaa", "aa", matchCase: false, matchWholeWord: false);

        Assert.Equal([0, 2], matches);
    }

    /// <summary>With MatchCase off (the default), differently-cased occurrences are still found.</summary>
    [Fact]
    public void FindAllMatches_CaseInsensitiveByDefault_MatchesDifferentCasing()
    {
        List<int> matches = TextSearch.FindAllMatches("Apple apple APPLE", "apple", matchCase: false, matchWholeWord: false);

        Assert.Equal([0, 6, 12], matches);
    }

    /// <summary>With MatchCase on, only exact-case occurrences are found.</summary>
    [Fact]
    public void FindAllMatches_CaseSensitive_OnlyMatchesExactCasing()
    {
        List<int> matches = TextSearch.FindAllMatches("Apple apple APPLE", "apple", matchCase: true, matchWholeWord: false);

        Assert.Equal([6], matches);
    }

    /// <summary>With whole-word matching, a pattern embedded inside a longer word (e.g. "cat" inside "concatenate") is skipped.</summary>
    [Fact]
    public void FindAllMatches_WholeWord_SkipsPatternEmbeddedInLongerWord()
    {
        List<int> matches = TextSearch.FindAllMatches("cat concatenate cat", "cat", matchCase: false, matchWholeWord: true);

        Assert.Equal([0, 16], matches);
    }

    /// <summary>Whole-word matching treats underscore as a word character, so a pattern adjacent to "_" is not a whole-word boundary.</summary>
    [Fact]
    public void FindAllMatches_WholeWord_TreatsUnderscoreAsWordCharacter()
    {
        List<int> matches = TextSearch.FindAllMatches("cat cat_dog", "cat", matchCase: false, matchWholeWord: true);

        Assert.Equal([0], matches);
    }

    /// <summary>An empty search pattern never matches anything.</summary>
    [Fact]
    public void FindAllMatches_EmptyPattern_ReturnsNoMatches() =>
        Assert.Empty(TextSearch.FindAllMatches("some content", "", matchCase: false, matchWholeWord: false));

    /// <summary>A pattern longer than the content it's searched against never matches.</summary>
    [Fact]
    public void FindAllMatches_PatternLongerThanContent_ReturnsNoMatches() =>
        Assert.Empty(TextSearch.FindAllMatches("hi", "hello", matchCase: false, matchWholeWord: false));

    /// <summary>A pattern that occupies the entire content is still found and is treated as whole-word (nothing on either side).</summary>
    [Fact]
    public void FindAllMatches_PatternEqualsWholeContent_MatchesAsWholeWord()
    {
        List<int> matches = TextSearch.FindAllMatches("needle", "needle", matchCase: false, matchWholeWord: true);

        Assert.Equal([0], matches);
    }
}
