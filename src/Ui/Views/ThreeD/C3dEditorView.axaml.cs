using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Render;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-em3d-43 — the 3D editor's shell. Wires the pane's frames to the overlay, shows a present fault in
/// the pane's place, opens the selection's context menu (the view model's, per mode), commits the Properties
/// panel's typed fields on Enter or lost focus, and follows the application theme (a switch regenerates the
/// scene, because colours are baked into the vertices).
/// </summary>
public partial class C3dEditorView : UserControl
{
    private C3dEditorViewModel? _vm;
    private readonly ContextMenu _menu = new();
    private readonly ContextMenu _drawMenu = new();

    public C3dEditorView()
    {
        InitializeComponent();
        // brief-em3d-45 — the typed field sees Tab, Enter and Esc before the TextBox (and focus navigation) does.
        FieldInput.AddHandler(KeyDownEvent, OnFieldKey, RoutingStrategies.Tunnel);
        _drawMenu.AddHandler(KeyDownEvent, OnDrawMenuKey, RoutingStrategies.Tunnel);
        Pane.FramePresented += () => Overlay.InvalidateVisual();
        Pane.ContextMenuRequested += () =>
        {
            if (_vm is null) return;
            Viewer3DContextMenu.Fill(_menu, _vm.Viewer.OpenContextMenu(), []);
            if (_menu.Items.Count > 0) _menu.Open(Pane);
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
            _vm.PropertiesRequested -= OnPropertiesRequested;
            _vm.DrawMenuRequested -= OnDrawMenuRequested;
            _vm.FieldFocusRequested -= OnFieldFocusRequested;
        }
        var doc = DataContext as C3dEditorDocument;
        _vm = doc?.ViewModel;
        if (_vm is null) return;
        _vm.PropertiesRequested += OnPropertiesRequested;
        _vm.DrawMenuRequested += OnDrawMenuRequested;
        _vm.FieldFocusRequested += OnFieldFocusRequested;
        ApplyBackground();
        if (doc!.ConsumeActivationFocus()) Dispatcher.UIThread.Post(() => Pane.Focus(), DispatcherPriority.Loaded);
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

    private void OnPropertiesRequested(bool rename)
    {
        if (!rename) return;
        Dispatcher.UIThread.Post(() => { NameBox.Focus(); NameBox.SelectAll(); }, DispatcherPriority.Loaded);
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
            case Key.Escape: _vm.FieldEscape(); Pane.Focus(); e.Handled = true; break;
        }
    }

    private void OnPlaneOffsetKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitPlaneOffset(); Pane.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { if (_vm is not null) _vm.SetPlane(_vm.Plane); Pane.Focus(); e.Handled = true; }
    }

    private void OnPlaneOffsetLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitPlaneOffset();

    // ── the Properties panel's typed fields: committed on Enter or lost focus ───────────────

    private void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.Properties.CommitName(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Properties.Reload(); e.Handled = true; }
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => _vm?.Properties.CommitName();

    private void OnOriginKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.Properties.CommitOrigin(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Properties.Reload(); e.Handled = true; }
    }

    private void OnOriginLostFocus(object? sender, RoutedEventArgs e) => _vm?.Properties.CommitOrigin();

    private void OnRotateKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.Properties.CommitRotate(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Properties.Reload(); e.Handled = true; }
    }

    private void OnRotateLostFocus(object? sender, RoutedEventArgs e) => _vm?.Properties.CommitRotate();
}
