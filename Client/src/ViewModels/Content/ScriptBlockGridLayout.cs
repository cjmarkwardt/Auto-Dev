namespace AutoDev.ViewModels.Content;

/// <summary>
/// Resolves each visible script panel's concrete grid cell from its optionally-requested Row/Column (see
/// TaskScript) - if none of the panels request a position, reproduces the old square-ish auto layout
/// exactly (zero behavior change in the common case); otherwise the grid is sized to fit every requested
/// position, and panels missing either coordinate fill whatever cells are left over in row-major order,
/// growing extra rows (same column count) if there are more of them than free cells.
/// </summary>
public static class ScriptBlockGridLayout
{
    public static (int Rows, int Columns) Apply(IReadOnlyList<ScriptBlockPanelViewModel> panels)
    {
        if (panels.Count == 0)
        {
            return (1, 1);
        }

        var positioned = panels.Where(p => p.RequestedRow is not null && p.RequestedColumn is not null).ToList();
        var unpositioned = panels.Except(positioned).ToList();

        if (positioned.Count == 0)
        {
            var autoColumns = (int)Math.Ceiling(Math.Sqrt(panels.Count));
            var autoRows = (int)Math.Ceiling(panels.Count / (double)autoColumns);
            for (var i = 0; i < panels.Count; i++)
            {
                panels[i].ResolvedRow = i / autoColumns;
                panels[i].ResolvedColumn = i % autoColumns;
            }

            return (autoRows, autoColumns);
        }

        var columns = positioned.Max(p => p.RequestedColumn!.Value) + 1;
        var rows = positioned.Max(p => p.RequestedRow!.Value) + 1;
        var occupied = new HashSet<(int Row, int Column)>();
        foreach (var panel in positioned)
        {
            panel.ResolvedRow = panel.RequestedRow!.Value;
            panel.ResolvedColumn = panel.RequestedColumn!.Value;
            occupied.Add((panel.ResolvedRow, panel.ResolvedColumn));
        }

        var cursor = 0;
        foreach (var panel in unpositioned)
        {
            while (occupied.Contains((cursor / columns, cursor % columns)))
            {
                cursor++;
            }

            var row = cursor / columns;
            var column = cursor % columns;
            occupied.Add((row, column));
            panel.ResolvedRow = row;
            panel.ResolvedColumn = column;
            rows = Math.Max(rows, row + 1);
            cursor++;
        }

        return (rows, columns);
    }
}
