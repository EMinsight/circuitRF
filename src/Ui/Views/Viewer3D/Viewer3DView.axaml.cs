using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CircuitRF.Render;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.Views.Viewer3D;

/// <summary>
/// brief-em3d-28 — the 3D view's shell. Wires the pane's frames to the overlay's redraw, shows a
/// present fault in the pane's place, scrolls the object tree to what a click selected, and follows the
/// application theme: a light/dark switch or a new theme regenerates the scene, because colours are
/// baked into the vertices (a regeneration, not a per-frame cost).
/// </summary>
public partial class Viewer3DView : UserControl
{
    private Viewer3DViewModel? _vm;
    private readonly ContextMenu _menu = new();

    public Viewer3DView()
    {
        InitializeComponent();
        Pane.FramePresented += () => Overlay.InvalidateVisual();
        // brief-em3d-43 R-em3d43-4e — the menu is the selection's (per mode), rebuilt at each right-click,
        // with the picture commands after it.
        Pane.ContextMenuRequested += () =>
        {
            if (_vm is null) return;
            var copy = Viewer3DPictureCopy.Item(Pane, () => _vm, text => { if (_vm is not null) _vm.PictureText = text; });
            var export = new MenuItem { Header = "Export Picture…" };
            export.Click += OnExportPicture;
            Viewer3DContextMenu.Fill(_menu, _vm.OpenContextMenu(), [copy, export]);
            _menu.Open(Pane);
        };
        Pane.FaultChanged += why =>
        {
            FaultText.Text = why is null ? "" : "The 3D view cannot draw here: " + why;
            FaultText.IsVisible = why is not null;
        };
        // 3D round 1 — the workspace window's Escape key binding marks the key handled before the pane sees it, so the
        // view claims Esc first (handled events too) and hands it to the pane's ladder: a measurement, Measure, the
        // selection. A text box or an open drop-down keeps its own Esc.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape || e.Source is TextBox or ComboBox { IsDropDownOpen: true }) return;
            Pane.Escape();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.RevealRequested -= Reveal;
        _vm = (DataContext as Viewer3DDocument)?.ViewModel;
        if (_vm is not null)
        {
            _vm.RevealRequested += Reveal;
            ApplyBackground();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeChanged;
        ActualThemeVariantChanged += OnVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeService.ThemeChanged -= OnThemeChanged;
        ActualThemeVariantChanged -= OnVariantChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _vm?.Invalidate();

    private void OnVariantChanged(object? sender, EventArgs e)
    {
        ApplyBackground();
        _vm?.Invalidate();
    }

    private void ApplyBackground()
    {
        if (_vm is null) return;
        _vm.View.Background = ThemeService.CurrentVariant == ColorVariant.Dark ? (0.12f, 0.13f, 0.15f) : (0.93f, 0.94f, 0.96f);
    }

    /// <summary>
    /// brief-em3d-29 R-em3d29-5 — Export picture…: the view drawn offscreen by the GPU at the chosen multiple
    /// of the pane's DEVICE-pixel size, read back, and written as PNG where the user says.
    /// </summary>
    private async void OnExportPicture(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var (w, h) = Viewer3DPictureCopy.PanePixels(Pane);
        var png = _vm.ExportPng(w, h, out string? error);
        if (png is null) { _vm.PictureText = "The picture could not be made: " + error; return; }
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export Picture",
            SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_vm.CemPath) + "-3d.png",
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(png);
        _vm.PictureText = $"Exported the view as {file.Name}.";
    }

    /// <summary>ItemsControl.ScrollIntoView resolves an item among the TOP-level items only, and a leaf
    /// sits under a group whose children are not realized until it expands — so the group is expanded
    /// first and the leaf's container brought into view once layout has made it.</summary>
    private void Reveal(Viewer3DTreeItem item)
    {
        foreach (var c in ObjectTree.GetRealizedContainers())
            if (c is TreeViewItem group && group.DataContext is Viewer3DTreeGroup g && g.Items.Contains(item))
            {
                group.IsExpanded = true;
                Dispatcher.UIThread.Post(() => group.ContainerFromItem(item)?.BringIntoView(), DispatcherPriority.Loaded);
                return;
            }
    }
}
