using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Render;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.Ui.Views.Viewer3D;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-em3d-43 — the 3D editor's shell. Wires the pane's frames to the overlay, shows a present fault in
/// the pane's place, opens the selection's context menu (the view model's, per mode) and the tree's,
/// and follows the application theme (a switch regenerates the
/// scene, because colours are baked into the vertices).
/// </summary>
public partial class C3dEditorView : UserControl
{
    private C3dEditorViewModel? _vm;
    private readonly ContextMenu _menu = new();
    private readonly ContextMenu _drawMenu = new();
    private readonly ContextMenu _treeMenu = new();

    public C3dEditorView()
    {
        InitializeComponent();
        // brief-em3d-45 — the typed field sees Tab and Enter before the TextBox (and focus navigation) does; its Esc is
        // the view's (OnViewKeyTunnel).
        FieldInput.AddHandler(KeyDownEvent, OnFieldKey, RoutingStrategies.Tunnel);
        _drawMenu.AddHandler(KeyDownEvent, OnDrawMenuKey, RoutingStrategies.Tunnel);
        // 3D round 1 — the workspace window binds Escape to a command, and a window key binding marks the key handled
        // before routing reaches the focused control: no bubble handler in this view (the pane's, a text box's) ever
        // saw Esc while the view was docked. The view claims it first, with handled events too — the fix the layout,
        // schematic and symbol editors already carry.
        AddHandler(KeyDownEvent, OnViewKeyTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnViewGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        Pane.FramePresented += () => Overlay.InvalidateVisual();
        Pane.ContextMenuRequested += () =>
        {
            if (_vm is null) return;
            // 3D editor bugs round 4 — Copy (the picture, to the clipboard) on anything or nothing, as the read-only
            // viewer has had it; this menu had been filled with no picture commands at all.
            var copy = Viewer3DPictureCopy.Item(Pane, () => _vm?.Viewer, text => { if (_vm is not null) _vm.StatusMessage = text; });
            // 3D editor bugs round 5 — Export Picture…, the read-only viewer's, for both.
            var export = new MenuItem { Header = "Export Picture…" };
            export.Click += OnExportPicture;
            Viewer3DContextMenu.Fill(_menu, _vm.Viewer.OpenContextMenu(), [copy, export]);
            _menu.Open(Pane);
        };
        Pane.FaultChanged += why =>
        {
            FaultText.Text = why is null ? "" : "The 3D view cannot draw here: " + why;
            FaultText.IsVisible = why is not null;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.DrawMenuRequested -= OnDrawMenuRequested;
            _vm.FieldFocusRequested -= OnFieldFocusRequested;
            _vm.TextRequested -= OnTextRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.TreeRevealRequested -= OnTreeReveal;
        }
        var doc = DataContext as C3dEditorDocument;
        _vm = doc?.ViewModel;
        if (_vm is null) return;
        _vm.DrawMenuRequested += OnDrawMenuRequested;
        _vm.FieldFocusRequested += OnFieldFocusRequested;
        _vm.TextRequested += OnTextRequested;
        _vm.PropertyChanged += OnVmPropertyChanged;
        MirrorTreeSelection();
        _vm.TreeRevealRequested += OnTreeReveal;
        ApplyBackground();
        if (doc!.ConsumeActivationFocus()) Dispatcher.UIThread.Post(() => Pane.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>brief-em3d-49 — a value the view model asks for (a port's Z0, a box face's padding): asked, then committed;
    /// a refusal goes to the status line.</summary>
    private async void OnTextRequested(string title, string prompt, string current, Func<string, string?> commit)
    {
        if (TopLevel.GetTopLevel(this) is not Window window || _vm is null) return;
        var text = await new Dialogs.InputNameDialog(title, prompt, current).ShowDialog<string?>(window);
        if (text is null) return;
        if (commit(text) is { } why) _vm.StatusMessage = why;
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

    private void OnThemeChanged(object? sender, EventArgs e) => _vm?.Viewer.Invalidate();

    private void OnVariantChanged(object? sender, EventArgs e)
    {
        ApplyBackground();
        _vm?.Viewer.Invalidate();
    }

    private void ApplyBackground()
    {
        if (_vm is null) return;
        _vm.Viewer.View.Background = ThemeService.CurrentVariant == ColorVariant.Dark ? (0.12f, 0.13f, 0.15f) : (0.93f, 0.94f, 0.96f);
    }

    /// <summary>
    /// 3D editor bugs round 5 — the read-only viewer's reveal, carried over: a node the scene selected is brought into view,
    /// its group (and, for an instance's part, its instance) opened first. A container is realized only once its parent
    /// is expanded and laid out, so the scroll waits for layout.
    /// </summary>
    private void OnTreeReveal(C3dTreeItem item)
    {
        if (_vm is null) return;
        foreach (var g in _vm.Tree)
        {
            if (g.Items.Contains(item)) g.IsExpanded = true;
            else if (g.Items.FirstOrDefault(i => i.Children.Contains(item)) is { } parent) { g.IsExpanded = true; parent.IsExpanded = true; }
            else continue;
            break;
        }
        Dispatcher.UIThread.Post(() => ObjectTree.TreeContainerFromItem(item)?.BringIntoView(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// brief-em3d-29 R-em3d29-5 — Export picture…: the view drawn offscreen by the GPU at the chosen multiple of the pane's
    /// DEVICE-pixel size, read back, and written as PNG where the user says (3D editor bugs round 5: the read-only viewer's).
    /// </summary>
    private async void OnExportPicture(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var viewer = _vm.Viewer;
        var (w, h) = Viewer3DPictureCopy.PanePixels(Pane);
        var png = viewer.ExportPng(w, h, out string? error);
        if (png is null) { _vm.StatusMessage = "The picture could not be made: " + error; return; }
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export Picture",
            SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_vm.FilePath) + "-3d.png",
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(png);
        _vm.StatusMessage = $"Exported the view as {file.Name}.";
    }

    /// <summary>3D editor round 1 — a right-click on a tree node selects it, then opens its menu (the canvas's commands).
    /// Round 5: on one of several selected rows it keeps them all, and the menu is the canvas's for that selection — the
    /// one builder, so a boolean is offered exactly as it is there.</summary>
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null || (e.Source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is not C3dTreeItem item) return;
        if (_vm.SelectedTreeItems.Count > 1 && _vm.SelectedTreeItems.Contains(item))
            Viewer3DContextMenu.Fill(_treeMenu, _vm.Viewer.ContextMenuItems(), []);
        else
        {
            _vm.SelectedTreeItem = item;
            Viewer3DContextMenu.Fill(_treeMenu, _vm.TreeMenuItems(item), []);
        }
        if (_treeMenu.Items.Count > 0) _treeMenu.Open(ObjectTree);
        e.Handled = true;
    }

    // ── 3D editor round 5: the tree's multiple selection ─────────────────────────────────────

    private bool _mirroringTree;

    /// <summary>The user changed the tree's selection: the view model keeps the order and selects in the scene.</summary>
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_mirroringTree || _vm is null) return;
        _vm.TreeSelectionChanged(e.RemovedItems.OfType<C3dTreeItem>(), e.AddedItems.OfType<C3dTreeItem>());
        // The view model may have settled on something else (a row it cannot select alongside others): show that.
        MirrorTreeSelection();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(C3dEditorViewModel.SelectedTreeItems)) MirrorTreeSelection();
    }

    /// <summary>The tree shows the view model's selection — every row of a canvas multi-selection highlighted.</summary>
    private void MirrorTreeSelection()
    {
        if (_vm is null || ObjectTree.SelectedItems is not { } selected) return;
        var want = _vm.SelectedTreeItems;
        if (selected.Count == want.Count && want.All(selected.Contains)) return;
        _mirroringTree = true;
        try
        {
            selected.Clear();
            foreach (var row in want) selected.Add(row);
        }
        finally { _mirroringTree = false; }
    }

    // ── 3D round 1: Esc ─────────────────────────────────────────────────────────────────────

    private TextBox? _editBox;
    private string? _editText;
    private bool _cancellingEdit;

    /// <summary>A text box took focus: what it said then is what Esc puts back.</summary>
    private void OnViewGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is not TextBox box) return;
        _editBox = box;
        _editText = box.Text;
    }

    /// <summary>
    /// Esc anywhere in the view. In a text box it CANCELS the edit — the text it had when it took focus comes back
    /// (through its binding, so the view model's copy too), nothing is committed on the way out, and focus returns
    /// to the canvas; the typed field closes, and a Define row goes back to the field, as their own keys said. An open
    /// drop-down just closes. Anywhere else it is one step of the pane's ladder.
    /// </summary>
    private void OnViewKeyTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is null) return;
        e.Handled = true;
        if (e.Source is ComboBox { IsDropDownOpen: true } combo) { combo.IsDropDownOpen = false; return; }
        if (e.Source is not TextBox box) { Pane.Escape(); return; }
        if (ReferenceEquals(box, FieldInput)) _vm.FieldEscape();
        else if (box.DataContext is C3dDefineRow) { _vm.DefineEscape(); return; }
        else
        {
            if (ReferenceEquals(box, _editBox)) box.Text = _editText;
            if (ReferenceEquals(box, PlaneOffsetBox)) _vm.SetPlane(_vm.Plane);
            else if (box.DataContext is C3dPropertiesViewModel or C3dDimensionField) _vm.Properties.Reload();
        }
        _cancellingEdit = true;
        try { Pane.Focus(); }
        finally { _cancellingEdit = false; }
        _editBox = null;
    }

    // ── brief-em3d-45: the drawing ──────────────────────────────────────────────────────────

    /// <summary>Shift+A — the Draw popup at the cursor; one letter (underlined) arms each tool.</summary>
    private void OnDrawMenuRequested()
    {
        if (_vm is null) return;
        var vm = _vm;
        _drawMenu.Items.Clear();
        foreach (var (kind, letter, icon) in C3dEditorViewModel.DrawTools)
        {
            string name = kind.ToString();
            int at = name.IndexOf(letter.ToString(), StringComparison.OrdinalIgnoreCase);
            string header = at >= 0 ? name.Insert(at, "_") : name;
            var item = new MenuItem
            {
                Header = header,
                InputGesture = new KeyGesture(Enum.Parse<Key>(letter.ToString())),
                Icon = new Material.Icons.Avalonia.MaterialIcon { Kind = Enum.Parse<Material.Icons.MaterialIconKind>(icon), Width = 16, Height = 16 },
            };
            item.Click += (_, _) => { vm.Arm(kind); Pane.Focus(); };
            _drawMenu.Items.Add(item);
        }
        _drawMenu.Open(Pane);
    }

    private void OnDrawMenuKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.None) return;
        string k = e.Key.ToString();
        if (k.Length == 1 && _vm.ArmByLetter(k[0]))
        {
            _drawMenu.Close();
            Pane.Focus();
            e.Handled = true;
        }
    }

    private void OnFieldFocusRequested()
        => Dispatcher.UIThread.Post(() =>
        {
            FieldInput.Focus();
            FieldInput.CaretIndex = FieldInput.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);

    private void OnFieldKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Tab: _vm.FieldTab(); FieldInput.CaretIndex = FieldInput.Text?.Length ?? 0; e.Handled = true; break;
            case Key.Enter:
                _vm.FieldEnter();
                if (!_vm.FieldOpen) Pane.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnPlaneOffsetKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitPlaneOffset(); Pane.Focus(); e.Handled = true; }
    }

    private void OnPlaneOffsetLostFocus(object? sender, RoutedEventArgs e) { if (!_cancellingEdit) _vm?.CommitPlaneOffset(); }

    // 3D editor bugs round 2 — the Snap combobox is the layout editor's: a ladder pick commits at once, typed text on
    // Enter or when focus leaves.
    private void OnSnapDistanceKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is ComboBox cb) { _vm?.CommitSnapDistanceText(cb.Text ?? ""); Pane.Focus(); e.Handled = true; }
    }

    private void OnSnapDistanceLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_cancellingEdit && sender is ComboBox cb) _vm?.CommitSnapDistanceText(cb.Text ?? "");
    }

    private void OnSnapDistanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string text }) _vm?.CommitSnapDistanceText(text);
    }

    private void OnDefineKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.DefineEnter(); e.Handled = true; }
    }

    private C3dVariableRow? RowOf(object? sender) => (sender as Control)?.DataContext as C3dVariableRow;

    private void OnVariableKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RowOf(sender) is not { } row) return;
        _vm?.Variables?.Edit(row);
        e.Handled = true;
    }

    private void OnVariableSet(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Edit(r); }
    private void OnVariableRename(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Rename(r); }
    private void OnVariableDelete(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Delete(r); }
    private void OnVariableInline(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.InlineAndDelete(r); }
    private void OnVariableLink(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Link(r, true); }
    private void OnVariableUnlink(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Link(r, false); }
    private void OnVariablePromote(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm?.Variables?.Promote(r); }
    private void OnVariableAdd(object? sender, RoutedEventArgs e) => _vm?.Variables?.Add();

    // brief-em3d-48 — the breadcrumb and the Pop Out button: the view model asks about a dirty child.
    private void OnPopOutClick(object? sender, RoutedEventArgs e) => _ = _vm?.PopOutAsync();

    private void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index }) _ = _vm?.PopToAsync(index);
    }
}
