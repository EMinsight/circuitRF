// brief-em3d-43 R-em3d43-4e — a 3D pane's context menu, made from the view model's menu model. The view
// model decides WHAT is offered (per mode, per selection, editor or viewer); this only turns the records
// into Avalonia items, so the read-only viewer and the editor build their menus the same way.

using Avalonia.Controls;

namespace CircuitRF.Ui.Viewer3D;

public static class Viewer3DContextMenu
{
    /// <summary>Replaces <paramref name="menu"/>'s items with <paramref name="items"/>, then
    /// <paramref name="extras"/> after a separator.</summary>
    public static void Fill(ContextMenu menu, IReadOnlyList<Viewer3DMenuItem> items, IReadOnlyList<MenuItem> extras)
    {
        menu.Items.Clear();
        foreach (var i in items) menu.Items.Add(Make(i));
        if (extras.Count == 0) return;
        if (items.Count > 0) menu.Items.Add(new Separator());
        foreach (var e in extras) menu.Items.Add(e);
    }

    private static Control Make(Viewer3DMenuItem i)
    {
        if (i.IsSeparator) return new Separator();
        var m = new MenuItem { Header = i.Header, IsEnabled = i.Enabled && (i.Run is not null || i.Children is { Count: > 0 }) };
        if (i.Tip is { } tip)
        {
            ToolTip.SetTip(m, tip);
            ToolTip.SetShowOnDisabled(m, true);
        }
        if (i.Children is { } children)
            foreach (var c in children) m.Items.Add(Make(c));
        else if (i.Run is { } run)
            m.Click += (_, _) => run();
        return m;
    }
}
