using AutoDev.Core.Services;

namespace AutoDev.Tests.Core.Services;

/// <summary>Covers TaskFileParser's own .task file format: one script path per line, a lone "-" line marking a wait between batches, blank lines ignored, and a stray/doubled/leading/trailing marker producing no empty batch.</summary>
public sealed class TaskFileParserTests
{
    private readonly TaskFileParser parser = new();

    [Fact]
    public void ParseBatches_NoMarkers_ReturnsSingleBatchWithEveryLine()
    {
        IReadOnlyList<IReadOnlyList<string>> batches = parser.ParseBatches("Scripts/A.cs\nScripts/B.cs");

        Assert.Equal([["Scripts/A.cs", "Scripts/B.cs"]], batches);
    }

    [Fact]
    public void ParseBatches_WithMarker_SplitsIntoOrderedBatches()
    {
        IReadOnlyList<IReadOnlyList<string>> batches = parser.ParseBatches("Scripts/A.cs\nScripts/B.cs\n-\nScripts/C.cs");

        Assert.Equal(2, batches.Count);
        Assert.Equal(["Scripts/A.cs", "Scripts/B.cs"], batches[0]);
        Assert.Equal(["Scripts/C.cs"], batches[1]);
    }

    [Fact]
    public void ParseBatches_BlankLines_AreIgnored()
    {
        IReadOnlyList<IReadOnlyList<string>> batches = parser.ParseBatches("\nScripts/A.cs\n\n  \n-\n\nScripts/B.cs\n");

        Assert.Equal(2, batches.Count);
        Assert.Equal(["Scripts/A.cs"], batches[0]);
        Assert.Equal(["Scripts/B.cs"], batches[1]);
    }

    [Fact]
    public void ParseBatches_LeadingTrailingAndDoubledMarkers_ProduceNoEmptyBatch()
    {
        IReadOnlyList<IReadOnlyList<string>> batches = parser.ParseBatches("-\nScripts/A.cs\n-\n-\nScripts/B.cs\n-");

        Assert.Equal(2, batches.Count);
        Assert.Equal(["Scripts/A.cs"], batches[0]);
        Assert.Equal(["Scripts/B.cs"], batches[1]);
    }

    [Fact]
    public void ParseBatches_EmptyContent_ReturnsNoBatches()
    {
        IReadOnlyList<IReadOnlyList<string>> batches = parser.ParseBatches("");

        Assert.Empty(batches);
    }

    [Fact]
    public void ParseBatches_SurroundingWhitespaceOnLines_IsTrimmed()
    {
        IReadOnlyList<IReadOnlyList<string>> batches = parser.ParseBatches("  Scripts/A.cs  \n  -  \n  Scripts/B.cs  ");

        Assert.Equal(2, batches.Count);
        Assert.Equal(["Scripts/A.cs"], batches[0]);
        Assert.Equal(["Scripts/B.cs"], batches[1]);
    }
}
