using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Palette;

public partial class PaletteToolView : UserControl
{
    public PaletteToolView()
    {
        InitializeComponent();

        // Page Up / Page Down / Home / End over the tile grid — the same rule the Project Tree and
        // the .ctech editor use, from the one place that owns it. Tunnelled so nothing inside the
        // grid can claim a key first.
        AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel);

        // A click anywhere in the tile area gives that area keyboard focus, which is what makes the
        // keys above arrive at all. Bubbling and never Handled, so a tile's own press (arm toggle /
        // drag start) runs exactly as before.
        TileScroll.AddHandler(PointerPressedEvent, OnTilePressed, handledEventsToo: true);

        // Activating the panel (clicking its tab) must also put focus in here — see below.
        DataContextChanged += OnDataContextChangedForActivation;

        // The Type menu is built when it opens, from the tool's menu tree as it stands then.
        TypeFlyout.Opening += OnTypeMenuOpening;

        // The breadcrumb is fitted to the button's width whenever either changes.
        TypeButton.SizeChanged += (_, _) => FitBreadcrumb();
        DataContextChanged     += OnDataContextChangedForBreadcrumb;
    }

    // ── Breadcrumb (R-vl2-1a) ─────────────────────────────────────────────────

    private PaletteTool? _breadcrumbTool;

    private void OnDataContextChangedForBreadcrumb(object? sender, EventArgs e)
    {
        if (_breadcrumbTool is not null) _breadcrumbTool.PropertyChanged -= OnToolPropertyChanged;
        _breadcrumbTool = DataContext as PaletteTool;
        if (_breadcrumbTool is not null) _breadcrumbTool.PropertyChanged += OnToolPropertyChanged;
        FitBreadcrumb();
    }

    private void OnToolPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaletteTool.SelectedCategory)) FitBreadcrumb();
    }

    /// <summary>
    /// The selection's breadcrumb, trimmed from the LEFT a whole level at a time so the narrowest panel
    /// still names the level chosen: <c>… ▸ Capacitors ▸ Series</c>. Done here rather than by
    /// <c>TextTrimming.LeadingCharacterEllipsis</c>, which throws in Avalonia 12.0.3 when the text
    /// mixes fonts — and a <c>▸</c> does.
    /// </summary>
    private void FitBreadcrumb()
    {
        string full = (DataContext as PaletteTool)?.SelectedCategory?.Breadcrumb ?? "";
        // The button's content width: its own, less padding, border and the chevron's 18 px.
        double room = TypeButton.Bounds.Width - TypeButton.Padding.Left - TypeButton.Padding.Right - 2 - 18;
        TypeText.Text = room <= 0 ? full : FitFromLeft(full, room);
    }

    private string FitFromLeft(string full, double room)
    {
        var levels = full.Split(VendorMenuTree.Step);
        for (int skip = 0; skip < levels.Length; skip++)
        {
            string text = skip == 0 ? full : "…" + VendorMenuTree.Step + string.Join(VendorMenuTree.Step, levels[skip..]);
            if (skip == levels.Length - 1 || WidthOf(text) <= room) return text;
        }
        return full;
    }

    private double WidthOf(string text)
    {
        using var layout = new Avalonia.Media.TextFormatting.TextLayout(
            text, new Avalonia.Media.Typeface(TypeText.FontFamily), TypeText.FontSize, null);
        return layout.WidthIncludingTrailingWhitespace;
    }

    /// <summary>The Type button's menu. A flyout is not in the view's name scope, so it is reached
    /// through the button rather than by a generated field.</summary>
    private MenuFlyout TypeFlyout => (MenuFlyout)TypeButton.Flyout!;

    // ── Type menu (brief vendorlib-2 R-vl2-1) ─────────────────────────────────

    private void OnTypeMenuOpening(object? sender, EventArgs e)
    {
        TypeFlyout.Items.Clear();
        if (DataContext is not PaletteTool tool) return;
        foreach (var node in tool.TypeMenu)
            TypeFlyout.Items.Add(MenuItemFor(tool, node));
    }

    /// <summary>One menu item per node, nested as the tree is. A sub-menu is the menu's own: its
    /// arrow-key navigation is Avalonia's, with nothing added here (R-vl2-1d).</summary>
    private static Control MenuItemFor(PaletteTool tool, PaletteMenuNode node)
    {
        if (node.IsSeparator) return new Separator();

        var item = new MenuItem { Header = node.Header, IsEnabled = node.IsEnabled };
        if (node.Command is { } command)
            item.Command = command;
        else if (node.Entry is { } entry)
        {
            item.Click += (_, _) => tool.SelectedCategory = entry;
            // The current selection is ticked, so the menu says where the palette is.
            if (entry.SameSelection(tool.SelectedCategory))
                item.Icon = new Material.Icons.Avalonia.MaterialIcon
                    { Kind = Material.Icons.MaterialIconKind.Check, Width = 14, Height = 14 };
        }

        foreach (var child in node.Children)
            item.Items.Add(MenuItemFor(tool, child));
        return item;
    }

    private void OnTilePressed(object? sender, PointerPressedEventArgs e) => TileScroll.Focus();

    // ── Column width (PaletteColumnPin) ───────────────────────────────────────

    /// <summary>
    /// The width the tile grid actually gets — what decides how many glyph columns reflow into the
    /// panel, and the one measurement <see cref="PaletteColumnPin"/> cannot make from outside.
    ///
    /// <para>The viewport, not this control's own width: they are the same today (the scroll bar
    /// overlays rather than taking a strip), and reading the viewport is what keeps that from being
    /// an assumption. It falls back when the tile area is hidden, which it is while a search matches
    /// nothing — a panel with no tiles in it still has a width.</para>
    /// </summary>
    public double TileAreaWidth
    {
        get
        {
            double viewport = TileScroll.Viewport.Width;
            if (viewport > 0.0) return viewport;
            return TileScroll.Bounds.Width > 0.0 ? TileScroll.Bounds.Width : Bounds.Width;
        }
    }

    /// <summary>
    /// Takes the panel's pending "open at this glyph count" request, if any — the default count from
    /// Reset Layout, a Window Layout or a new workspace, or the count a saved arrangement recorded.
    /// Forwarded from the tool so <see cref="PaletteColumnPin"/> needs no dock view-model type.
    /// </summary>
    internal int ConsumeGlyphColumnsRequest()
        => DataContext is PaletteTool tool ? tool.ConsumeGlyphColumnsRequest() : 0;

    /// <summary>Tells the tool the count being held, so a save records it.</summary>
    internal void ReportHeldGlyphColumns(int columns)
    {
        if (DataContext is PaletteTool tool) tool.HeldGlyphColumns = columns;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PaletteColumnPin.NotifyPaletteAttached(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PaletteColumnPin.NotifyPaletteDetached(this);
        base.OnDetachedFromVisualTree(e);
    }

    // ── Activation focus (owner, 2026-08-25) ──────────────────────────────────
    //
    //  Clicking this panel's TAB leaves focus on the tab — Dock chrome, outside this control — so a
    //  key event's route never passes through here and OnScrollKeyDown is never called. The user had
    //  to click a tile first. Same fix, and the same mechanism, as the Project Tree beside it.

    private IActivatableTool? _activationTool;

    private void OnDataContextChangedForActivation(object? sender, EventArgs e)
    {
        if (_activationTool is not null) _activationTool.ActivationFocusRequested -= OnActivationFocusRequested;
        _activationTool = DataContext as IActivatableTool;
        if (_activationTool is null) return;

        _activationTool.ActivationFocusRequested += OnActivationFocusRequested;

        // The panel can be activated before this view exists (first layout, restored arrangement).
        if (_activationTool.ConsumeActivationFocus()) OnActivationFocusRequested();
    }

    private void OnActivationFocusRequested() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Activation fires for a click ANYWHERE in the panel, the Search box included — and this
            // grab is posted, so without the guard it lands after the click gave the box the caret
            // and takes it straight back (owner, 2026-08-26). See PanelActivationFocus.
            if (PanelActivationFocus.AlreadyInside(this)) return;
            TileScroll.Focus();
        }, Avalonia.Threading.DispatcherPriority.Input);

    private void OnScrollKeyDown(object? sender, KeyEventArgs e)
    {
        // The Type menu is a popup with its own root, so its keys never pass through here. The part
        // list is a ListBox, which scrolls itself — and the tile grid behind it is hidden.
        if (DataContext is PaletteTool { IsVendorSelection: true }) return;

        var action = PanelScrollKeys.ActionFor(e.Key, e.Source is TextBox);
        if (action is null) return;

        PanelScrollKeys.Apply(action.Value, TileScroll);
        e.Handled = true;
    }
}
