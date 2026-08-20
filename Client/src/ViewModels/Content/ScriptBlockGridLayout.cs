namespace AutoDev.ViewModels.Content;

/// <summary>
/// Arranges a run's visible script panels into a square-ish auto grid - the task language has no notion of a
/// script requesting a specific Output tab panel position (unlike AutoDev's own now-removed pre-Markwardt.
/// TaskRunner .task format), so every run always uses this same automatic layout.
/// </summary>
public static class ScriptBlockGridLayout
{
    public static (int Rows, int Columns) Apply(IReadOnlyList<ScriptPanelViewModel> panels)
    {
        if (panels.Count == 0)
        {
            return (1, 1);
        }

        var columns = (int)Math.Ceiling(Math.Sqrt(panels.Count));
        var rows = (int)Math.Ceiling(panels.Count / (double)columns);
        for (var i = 0; i < panels.Count; i++)
        {
            panels[i].ResolvedRow = i / columns;
            panels[i].ResolvedColumn = i % columns;
        }

        return (rows, columns);
    }
}
