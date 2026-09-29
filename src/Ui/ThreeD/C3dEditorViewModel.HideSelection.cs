// brief-em3d-91 — H hides the selection, and the toolbar's eye button shows and sets its visibility.
//
// ONE COMMAND, NO VISIBILITY CODE OF ITS OWN. The key (the pane's and the tree's), the 3D menu item, both context menus and the
// toolbar button all call HideOrShowSelection, and it writes through brief 90's SetRowsVisible — so an object's Hidden is saved
// and undoable, a record's is the view's, and a setup's view-only view hides for the session, exactly as each row's tick does.
//
// THE SELECTION is the tree's rows when it has any, else the view's objects mapped to their rows; a group selected whole in the
// view is its group's row. Its STATE is read over the leaves: a group's row stands for its members and an instance's for its
// parts, so a group with one member hidden on its own is Mixed, not AllVisible. A press shows Mixed (the owner's cycle: all
// visible first, then hidden), hides AllVisible and shows AllHidden.
//
// THE SELECTION SURVIVES THE HIDE, so a second H can show it again. Two things cleared it: SetGroupVisible's own
// SetSelection([]) (held off here by _keepSelectionOnHide), and a multi-row tree selection that held a row the scene no longer
// draws (a hidden thermal boundary's tint) being replaced by the rows of what the view still selects (TreeRowStillSelected now
// compares every selected row's objects, not only the first's). The right-click ▸ Hide path (SetHidden) keeps clearing it.

using Avalonia.Input;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>brief-em3d-91 — what the selection's visibility is, read over its leaves.</summary>
public enum C3dSelectionVisibility { None, AllVisible, AllHidden, Mixed }

public sealed partial class C3dEditorViewModel
{
    /// <summary>Set while H hides: a group's hide leaves the selection alone, so the next H shows it again.</summary>
    private bool _keepSelectionOnHide;

    /// <summary>The selection's visibility: <see cref="C3dSelectionVisibility.None"/> with nothing selected.</summary>
    public C3dSelectionVisibility SelectionVisibility => StateOf(SelectionVisibilityRows());

    /// <summary>The toolbar button's backdrop: the accent, full.</summary>
    public bool IsSelectionAllVisible => SelectionVisibility == C3dSelectionVisibility.AllVisible;

    /// <summary>The toolbar button's backdrop: the accent, dimmed.</summary>
    public bool IsSelectionMixed => SelectionVisibility == C3dSelectionVisibility.Mixed;

    /// <summary>The toolbar button's tooltip: what a press would do, and the key.</summary>
    public string SelectionVisibilityTip => SelectionVisibility switch
    {
        C3dSelectionVisibility.AllVisible => "Hide the selection (H)",
        C3dSelectionVisibility.AllHidden => "Show the selection (H)",
        C3dSelectionVisibility.Mixed => "Some of the selection is hidden: show all of it (H)",
        _ => "Select something to hide or show it",
    };

    /// <summary>The menu item's header, for the menus that cannot bind (the context menus are built per open).</summary>
    public const string HideShowSelectionHeader = "Hide / Show Selection";

    /// <summary>The toolbar button, the 3D menu item and both context menus.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleSelectionVisibility))]
    private void ToggleSelectionVisibility() => HideOrShowSelection();

    private bool CanToggleSelectionVisibility() => SelectionVisibility != C3dSelectionVisibility.None;

    /// <summary>
    /// brief-em3d-91 R-em3d91-1 — H: AllVisible hides the selection, AllHidden and Mixed show it, through SetRowsVisible as one
    /// undo entry. The selection is kept. False when nothing is selected (the key then falls through).
    /// </summary>
    public bool HideOrShowSelection()
    {
        var rows = SelectionVisibilityRows();
        var state = StateOf(rows);
        if (state == C3dSelectionVisibility.None) return false;
        bool visible = state != C3dSelectionVisibility.AllVisible;
        string what = rows.Count == 1 ? (rows[0].IsGroup ? C3dGroups.NameOf(rows[0].GroupPath!) : rows[0].Name) : $"{rows.Count} items";
        _keepSelectionOnHide = true;
        try { SetRowsVisible(rows, visible, $"{(visible ? "Show" : "Hide")} {what}"); }
        finally { _keepSelectionOnHide = false; }
        RaiseMenuStateChanged();
        return true;
    }

    /// <summary>IViewer3DEditHost — the canvas's context menu (and the tree's, on several rows) offers H by name.</summary>
    public Viewer3DMenuItem? HideShowSelectionItem()
        => new(HideShowSelectionHeader, () => HideOrShowSelection(), Enabled: CanToggleSelectionVisibility(), Tip: SelectionVisibilityTip,
               Gesture: Viewer3DMenuItem.Plain(Key.H));

    /// <summary>A tree row's menu, with Hide / Show Selection before its Properties (or at its end).</summary>
    private IReadOnlyList<Viewer3DMenuItem> WithHideShowSelection(IReadOnlyList<Viewer3DMenuItem> items)
    {
        if (HideShowSelectionItem() is not { } item) return items;
        var list = items.ToList();
        int at = list.FindLastIndex(i => i.Header == "Properties");
        if (at < 0) list.Add(item);
        else list.Insert(at > 0 && list[at - 1].IsSeparator ? at - 1 : at, item);
        return list;
    }

    /// <summary>The key H, bare, and not typed into a text field: the tree's tunnel asks this.</summary>
    public static bool IsHideKey(Key key, KeyModifiers modifiers, bool textHasFocus)
        => key == Key.H && modifiers == KeyModifiers.None && !textHasFocus;

    /// <summary>The rows H acts on: the tree's selected rows, else the view's objects' rows; a group selected whole is its row. A
    /// feature row (a fillet) has no visibility of its own and is left out.</summary>
    private List<C3dTreeItem> SelectionVisibilityRows()
    {
        List<C3dTreeItem> rows = SelectedTreeItems.Count > 0
            ? [.. SelectedTreeItems]
            : [.. Viewer.SelectedObjects().Select(o => IsViewOnly ? ViewTreeItemOf(o) : RowOf(o)).OfType<C3dTreeItem>().Distinct()];
        return [.. CollapseToGroups(rows).Where(r => !r.IsFeature)];
    }

    private static C3dSelectionVisibility StateOf(IReadOnlyList<C3dTreeItem> rows)
    {
        var leaves = rows.SelectMany(VisibilityLeaves).Distinct().ToList();
        // One field plot is drawn at a time (showing several shows one), so the selected plots are ONE leaf, shown when any is:
        // counted apart, two of them read Mixed after every show and H never reached the hide.
        var plots = leaves.Where(r => r.Kind == FieldPlotKind).ToList();
        var states = leaves.Where(r => r.Kind != FieldPlotKind).Select(r => r.IsVisible).ToList();
        if (plots.Count > 0) states.Add(plots.Any(p => p.IsVisible));
        if (states.Count == 0) return C3dSelectionVisibility.None;
        int shown = states.Count(v => v);
        return shown == states.Count ? C3dSelectionVisibility.AllVisible : shown == 0 ? C3dSelectionVisibility.AllHidden : C3dSelectionVisibility.Mixed;
    }

    /// <summary>The rows whose ticks a row's visibility is: a group's members (at any depth) and an instance's parts; any other
    /// row is its own.</summary>
    private static IEnumerable<C3dTreeItem> VisibilityLeaves(C3dTreeItem r)
        => r.IsGroup || (r.InstanceIndex >= 0 && r.Children.Count > 0) ? r.Children.Where(c => !c.IsFeature).SelectMany(VisibilityLeaves) : [r];

    /// <summary>The toolbar button follows the selection, a tick and undo: every one of them raises the menu state.</summary>
    private void RaiseSelectionVisibilityChanged()
    {
        OnPropertyChanged(nameof(SelectionVisibility));
        OnPropertyChanged(nameof(IsSelectionAllVisible));
        OnPropertyChanged(nameof(IsSelectionMixed));
        OnPropertyChanged(nameof(SelectionVisibilityTip));
        ToggleSelectionVisibilityCommand.NotifyCanExecuteChanged();
    }
}
