// The Workspace panel's SELECTION — several rows at once, the Delete key, and the menu a
// right-click over a multi-row selection opens. The rules live in ProjectTreeSelection; this is
// only their wiring.
//
// ── DELETE ACTS ONLY ON A TREE THAT HAS THE KEYBOARD ───────────────────────────────────────────
//
// The handler is on the TreeView itself, bubbling — not a window KeyBinding — so the key reaches it
// only when keyboard focus is INSIDE the tree. Every editor canvas takes focus on the press that
// selects a shape, so Delete aimed at a layout shape is the canvas's and never arrives here. Three
// things stand between a stray key and a lost cell:
//   1. the selection is drawn in the accent colour only while the tree has the keyboard, and in a
//      neutral grey otherwise — the cue every file manager gives for "this is where keys go";
//   2. nothing is removed without a confirmation that names it;
//   3. that confirmation is destructive: Cancel is its default button, so Delete-then-Enter cancels.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Material.Icons;
using Material.Icons.Avalonia;

namespace CircuitRF.Ui.Views.ProjectTree;

public partial class ProjectTreeView
{
    /// <summary>The Fluent TreeViewItem's selected-row brushes — the keys the shipping theme reads
    /// (taken from <c>Avalonia.Themes.Fluent</c> 12.0.3, not guessed), two of which this view already
    /// overrides in XAML.</summary>
    internal static readonly string[] SelectionBrushKeys =
    [
        "TreeViewItemBackgroundSelected",
        "TreeViewItemBackgroundSelectedPointerOver",
        "TreeViewItemBackgroundSelectedPressed",
    ];

    /// <summary>What each key held before the cue touched it (null = nothing, the theme's own).</summary>
    private readonly Dictionary<string, object?> _focusedSelectionBrushes = new();

    /// <summary>Called once, from the constructor.</summary>
    private void WireSelection()
    {
        TheTreeView.AddHandler(KeyDownEvent, OnTreeDeleteKeyDown);

        // TUNNEL, so it runs before the row Grid's own ContextMenu decides to open — that handler
        // stands aside for an event already handled.
        TheTreeView.AddHandler(ContextRequestedEvent, OnTreeContextRequested, RoutingStrategies.Tunnel);

        foreach (var key in SelectionBrushKeys)
            _focusedSelectionBrushes[key] = TheTreeView.Resources.TryGetValue(key, out var brush) ? brush : null;
        TheTreeView.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsKeyboardFocusWithinProperty) ApplySelectionFocusCue();
        };
        ApplySelectionFocusCue();
    }

    // ── Delete / Backspace ────────────────────────────────────────────────────

    private void OnTreeDeleteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Source is TextBox) return;
        if (!ProjectTreeSelection.IsDeleteGesture(e.Key, e.KeyModifiers)) return;
        if (DataContext is not ProjectTreeTool tool) return;

        e.Handled = true;
        _ = tool.RemoveSelectionAsync();
    }

    // ── Focus cue ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Accent while the tree has the keyboard, neutral while it does not. Swapped through the
    /// theme's own resource keys rather than a style on a template part: a guessed part name fails
    /// silently, styling nothing and reporting nothing.
    /// </summary>
    private void ApplySelectionFocusCue()
    {
        bool focused  = TheTreeView.IsKeyboardFocusWithin;
        var  inactive = this.TryFindResource("CrfInactiveSelectedRowBrush", ActualThemeVariant, out var b) ? b : null;

        foreach (var key in SelectionBrushKeys)
        {
            if (!focused && inactive is not null)
                TheTreeView.Resources[key] = inactive;
            else if (_focusedSelectionBrushes[key] is { } original)
                TheTreeView.Resources[key] = original;
            else
                TheTreeView.Resources.Remove(key);
        }
    }

    // ── The multi-selection menu ──────────────────────────────────────────────

    /// <summary>
    /// A right-click on a row that is part of a selection of two or more opens THIS menu instead of
    /// the row's own: what it offers has to apply to every row chosen, and a menu built for one
    /// row's kind would act on that row alone while the others stayed highlighted. A right-click on
    /// a row outside the selection selects just that row first (Avalonia's own rule), so the row's
    /// own menu opens as before.
    /// </summary>
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not ProjectTreeTool tool || tool.SelectedNodes.Count < 2) return;
        var row = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (row?.DataContext is not ProjectTreeNodeViewModel vm || !tool.SelectedNodes.Contains(vm)) return;

        e.Handled = true;
        BuildSelectionMenu(tool).Open(row);
    }

    private static ContextMenu BuildSelectionMenu(ProjectTreeTool tool)
    {
        var selection = tool.Selection;
        int n = selection.Count;
        string items = n == 1 ? "1 Item" : $"{n} Items";

        var open = new MenuItem
        {
            Header    = $"Open {items}",
            Icon      = new MaterialIcon { Kind = MaterialIconKind.OpenInApp, Width = 14, Height = 14 },
            IsEnabled = selection.All(ProjectTreeSelection.CanOpen),
        };
        open.Click += (_, _) => tool.OpenSelection();
        if (!open.IsEnabled)
        {
            ToolTip.SetTip(open, "Some of the selected rows have nothing to open — select only cells and documents.");
            ToolTip.SetShowOnDisabled(open, true);
        }

        string? refusal = ProjectTreeSelection.BulkRemoveRefusal(selection);
        var remove = new MenuItem
        {
            Header       = $"Remove {items}",
            Icon         = new MaterialIcon { Kind = MaterialIconKind.TrashCanOutline, Width = 14, Height = 14 },
            IsEnabled    = refusal is null,
            // A Mac keyboard's "delete" is Back; Key.Delete would render as the forward-delete glyph.
            InputGesture = new KeyGesture(OperatingSystem.IsMacOS() ? Key.Back : Key.Delete),
        };
        remove.Click += (_, _) => _ = tool.RemoveSelectionAsync();
        if (refusal is not null)
        {
            ToolTip.SetTip(remove, refusal);
            ToolTip.SetShowOnDisabled(remove, true);
        }

        return new ContextMenu { ItemsSource = new List<object> { open, new Separator(), remove } };
    }
}
