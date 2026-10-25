// ================================================================
//  LegendBoxView.axaml.cs
//
//  A plot's floating legend on the Data Display canvas. Drawn by
//  LegendRenderer.DrawFloating — the code path an export draws it with —
//  and moved exactly as a MarkerInfoBoxView is: a press selects it (Ctrl/
//  Cmd toggles) and drags every selected box together.
// ================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class LegendBoxView : UserControl
{
    private LegendBoxViewModel? Vm => DataContext as LegendBoxViewModel;

    public LegendBoxView()
    {
        InitializeComponent();
        Focusable = true;

        PointerPressed  += OnPointerPressed;
        PointerMoved    += OnPointerMoved;
        PointerReleased += OnPointerReleased;

        ((IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LegendBoxViewModel vm)
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(LegendBoxViewModel.NeedsRedraw)
                                      or nameof(LegendBoxViewModel.IsSelected)
                                      or nameof(LegendBoxViewModel.BoxWidth)
                                      or nameof(LegendBoxViewModel.BoxHeight))
                    InvalidateVisual();
            };
    }

    public override void Render(DrawingContext context)
    {
        if (Vm is not { } vm) return;
        // Everything the draw needs is captured HERE, on the UI thread: the operation runs on the compositor thread,
        // where reading the live plot or resolving the accent colour is not safe.
        context.Custom(new LegendDrawOperation(
            new Rect(Bounds.Size),
            vm.Plot.RenderSnapshot(),
            vm.Lw,
            vm.Theme,
            vm.AliasFor,
            vm.AlwaysShowSource,
            AppSettingsViewModel.Instance.MarkerBoxTransparentBackground,
            vm.IsSelected,
            vm.IsSelected ? PlotAccentColor.GetTransparentAccent(RenderTheme.SelectionAlpha) : default));
    }

    private sealed class LegendDrawOperation(
        Rect bounds, Plot plot, double lw, RenderTheme theme, Func<Trace, string?>? aliasFor, bool alwaysShowSource,
        bool transparentBackground, bool isSelected, SKColor selectionColor) : ICustomDrawOperation
    {
        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var l = lease.Lease();
            LegendRenderer.DrawFloating(l.SkCanvas, (bounds.Width, bounds.Height), plot, lw, theme,
                aliasFor, alwaysShowSource, transparentBackground, isSelected, selectionColor);
        }

        public void Dispose() { }
    }

    // ---- Drag: move the legend (with every other selected box) ---------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        bool isCtrlOrMeta =
            e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (isCtrlOrMeta) Vm?.ToggleSelect();
        else              Vm?.SelectOnly();

        Focus();

        var root = TopLevel.GetTopLevel(this);
        Vm?.StartGroupDrag(root is not null ? e.GetPosition(root) : e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Captured != this) return;
        var root = TopLevel.GetTopLevel(this);
        Vm?.UpdateGroupDrag(root is not null ? e.GetPosition(root) : e.GetPosition(this));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured != this) return;
        Vm?.EndGroupDrag();
        e.Pointer.Capture(null);
    }
}
