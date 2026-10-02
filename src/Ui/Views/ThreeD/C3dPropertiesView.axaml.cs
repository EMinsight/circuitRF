using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-em3d-43 R-em3d43-6b — a 3D view's selection in the Properties Inspector. Each typed field commits on Enter or
/// when it loses focus (one undo entry), and Esc puts the field back. 3D editor round 1: moved out of the editor's own
/// panel into the application's Properties Inspector, which follows the active 3D view as it follows a layout.
/// </summary>
public partial class C3dPropertiesView : UserControl
{
    private C3dPropertiesViewModel? _vm;
    private (TextBox Box, string? Text)? _focusText;

    public C3dPropertiesView()
    {
        InitializeComponent();
        Focusable = true;
        // 3D editor round 1 — Esc in a field cancels the edit and leaves the field. The window binds Esc to a command that
        // marks it handled before a focused control sees it, so this listens on the tunnel with handled events too.
        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnEscapeTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        // brief-em3d-92 — the slider previews while it moves and writes on release: the pointer let go (its thumb captures it,
        // so the release may arrive as a lost capture) or an arrow key let go.
        TransparencySlider.AddHandler(PointerReleasedEvent, (_, _) => _vm?.CommitTransparencySlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        TransparencySlider.AddHandler(PointerCaptureLostEvent, (_, _) => _vm?.CommitTransparencySlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        TransparencySlider.AddHandler(KeyUpEvent, (_, _) => _vm?.CommitTransparencySlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        // The clip plot's offset slider: the same preview-then-commit, one undo entry per drag.
        PlotOffsetSlider.AddHandler(PointerReleasedEvent, (_, _) => _vm?.CommitPlotOffsetSlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        PlotOffsetSlider.AddHandler(PointerCaptureLostEvent, (_, _) => _vm?.CommitPlotOffsetSlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        PlotOffsetSlider.AddHandler(KeyUpEvent, (_, _) => _vm?.CommitPlotOffsetSlider(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is TextBox tb) _focusText = (tb, tb.Text);
    }

    /// <summary>The field gets back the text it had when it took focus, THEN loses focus — so its lost-focus commit finds
    /// nothing changed and writes nothing.</summary>
    private void OnEscapeTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _focusText is not { } f || !f.Box.IsFocused || !this.IsVisualAncestorOf(f.Box)) return;
        f.Box.Text = f.Text;
        _focusText = null;
        Focus();
        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.RenameRequested -= OnRenameRequested;
        _vm = DataContext as C3dPropertiesViewModel;
        if (_vm is not null) _vm.RenameRequested += OnRenameRequested;
    }

    private void OnRenameRequested()
        => Dispatcher.UIThread.Post(() => { NameBox.Focus(); NameBox.SelectAll(); }, DispatcherPriority.Loaded);

    private void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitName(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitName();

    // brief-em3d-92 — the transparency box, and the slider's release: a drag is one undo entry, written when it ends.
    private void OnTransparencyKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitTransparencyText(); e.Handled = true; }
    }

    private void OnTransparencyLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitTransparencyText();

    // brief-em3d-83 — a field plot's clip-plane offset, and the Other frequency box.
    private void OnPlotOffsetKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitPlotOffset(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnPlotOffsetLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitPlotOffset();

    // brief-em3d-100 — a field plot's drive power.
    private void OnPlotDriveKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitPlotDrive(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnPlotDriveLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitPlotDrive();

    private void OnPlotOtherKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitOtherFrequency(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    // 3D editor round 4 — a bond wire's loop height and span.
    private void OnWireLoopHeightKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitWireLoopHeight(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnWireLoopHeightLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitWireLoopHeight();

    private void OnWireSpanKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitWireSpan(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnWireSpanLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitWireSpan();

    private void OnRotateKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitRotate(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnRotateLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitRotate();

    private void OnVertexKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitVertex(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnVertexLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitVertex();

    private void OnDimensionKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; return; }
        if (e.Key != Key.Enter || (sender as Control)?.DataContext is not C3dDimensionField f) return;
        _vm?.CommitField(f);
        e.Handled = true;
    }

    private void OnDimensionLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dDimensionField f) _vm?.CommitField(f);
    }

    // 3D editor round 3 — a bond wire's points.
    private void OnWirePointKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; return; }
        if (e.Key != Key.Enter || (sender as Control)?.DataContext is not C3dWirePointRow row) return;
        _vm?.CommitWirePoint(row);
        e.Handled = true;
    }

    private void OnWirePointLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dWirePointRow row) _vm?.CommitWirePoint(row);
    }

    // 3D editor round 1 — the air box's padding per axis.
    // brief-em3d-75 R-em3d75-1c — a thermal place's field: Enter or lost focus commits it, one undo entry.
    private void OnThermalFieldKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (sender as Control)?.DataContext is not C3dThermalTextField f) return;
        _vm?.CommitThermalField(f);
        e.Handled = true;
    }

    private void OnThermalFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dThermalTextField f) _vm?.CommitThermalField(f);
    }

    // brief-em3d-90 R-em3d90-4 — a thermal boundary's field: Enter or lost focus commits it, one undo entry.
    private void OnBoundaryKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        _vm?.CommitBoundary();
        e.Handled = true;
    }

    private void OnBoundaryLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitBoundary();

    private static char AxisOf(object? sender) => (sender as Control)?.Tag is string { Length: 1 } a ? a[0] : 'x';

    private void OnPadKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitAirBoxPercent(AxisOf(sender)); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnPadLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitAirBoxPercent(AxisOf(sender));
}
