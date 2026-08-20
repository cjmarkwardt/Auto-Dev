namespace AutoDev.Tests.ViewModels.Content;

/// <summary>Covers ScriptBlockGridLayout's square-ish auto-arrangement - the task language has no notion of a script requesting a specific panel position, so this is the only layout the Output tab ever uses.</summary>
public sealed class ScriptBlockGridLayoutTests
{
    private static List<ScriptPanelViewModel> Panels(int count) =>
        Enumerable.Range(1, count).Select(i => new ScriptPanelViewModel($"Script {i}")).ToList();

    [Fact]
    public void Apply_NoPanels_ReturnsSingleCellGrid()
    {
        (int rows, int columns) = ScriptBlockGridLayout.Apply([]);

        Assert.Equal(1, rows);
        Assert.Equal(1, columns);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 3)]
    [InlineData(9, 3, 3)]
    public void Apply_SizesGridAsCloseToSquareAsPossible(int count, int expectedRows, int expectedColumns)
    {
        List<ScriptPanelViewModel> panels = Panels(count);

        (int rows, int columns) = ScriptBlockGridLayout.Apply(panels);

        Assert.Equal(expectedRows, rows);
        Assert.Equal(expectedColumns, columns);
    }

    [Fact]
    public void Apply_AssignsEveryPanelAUniqueRowMajorCell()
    {
        List<ScriptPanelViewModel> panels = Panels(5);

        (int rows, int columns) = ScriptBlockGridLayout.Apply(panels);

        for (int i = 0; i < panels.Count; i++)
        {
            Assert.Equal(i / columns, panels[i].ResolvedRow);
            Assert.Equal(i % columns, panels[i].ResolvedColumn);
        }

        var cells = panels.Select(p => (p.ResolvedRow, p.ResolvedColumn)).ToHashSet();
        Assert.Equal(panels.Count, cells.Count);
        Assert.All(panels, p => Assert.True(p.ResolvedRow < rows && p.ResolvedColumn < columns));
    }
}
