using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CircuitRF.Ui.Recognition;

/// <summary>
/// The picture dialog's body (R-im5-2, R-im5-3). The window owns only what needs a window; this places the measure
/// tools' inline boxes at the point clicked, flashes a hovered layer row's colour, and takes a dropped file.
/// </summary>
public partial class ImageReadingPanel : UserControl
{
    private ImageSourceViewModel? _vm;

    public ImageReadingPanel()
    {
        InitializeComponent();
        BuildRemoveMenu();
        Canvas.ViewChanged += PlaceInline;
        // A box asked for before the canvas has a size is clamped into its corner; it moves beside its point once it does.
        Canvas.SizeChanged += (_, _) => PlaceInline();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Scale.PropertyChanged -= OnScaleChanged;
            _vm.Plots.PropertyChanged -= OnPlotsChanged;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as ImageSourceViewModel;
        if (_vm is not null)
        {
            _vm.Scale.PropertyChanged += OnScaleChanged;
            _vm.Plots.PropertyChanged += OnPlotsChanged;
            _vm.PropertyChanged += OnVmChanged;
        }
    }

    /// <summary>A hole or a copper region selected: its picker beside it (brief-dfr29-1 R-dfr29-1-2).</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageSourceViewModel.HolePickerShown) or nameof(ImageSourceViewModel.CopperPickerShown)
            or nameof(ImageSourceViewModel.SelectedItem))
            PlaceInline();
    }

    private void OnPlotsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlotSessionViewModel.AskingLabel) || _vm?.Plots.AskingLabel != true) return;
        PlaceInline();
        Dispatcher.UIThread.Post(() => { LabelInput.Focus(); LabelInput.SelectAll(); });
    }

    private void OnScaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageScaleViewModel.PromptAt) or nameof(ImageScaleViewModel.CornerPoints))
            PlaceInline();
        else if (e.PropertyName is nameof(ImageScaleViewModel.AskingDistance) or nameof(ImageScaleViewModel.AskingZ0)
                 or nameof(ImageScaleViewModel.AskingSize))
        {
            PlaceInline();
            // The box takes the keyboard as it appears, so the distance is typed straight after the second click.
            if (_vm?.Scale.AskingDistance == true) Dispatcher.UIThread.Post(() => DistanceInput.Focus());
            else if (_vm?.Scale.AskingZ0 == true) Dispatcher.UIThread.Post(() => { Z0Input.Focus(); Z0Input.SelectAll(); });
            else if (_vm?.Scale.AskingSize == true) Dispatcher.UIThread.Post(() => SizeInput.Focus());
        }
    }

    /// <summary>The inline box beside the point it asks about, never over it. Board corners' size box sits in the middle
    /// of the four handles: beside one of them it covered the corner being dragged (designer round 29).</summary>
    private void PlaceInline()
    {
        var scale = _vm?.Scale;
        if (scale is { Tool: ImageMeasureTool.Corners, CornerPoints: { Count: 4 } c })
        {
            var mid = Canvas.ToView(c.Average(p => p.X), c.Average(p => p.Y));
            var size = SizeOf(SizeBox);
            SetAt(SizeBox, mid.X - size.Width / 2, mid.Y - size.Height / 2);
        }
        if (scale?.PromptAt is { } at)
        {
            // Keep the other point of a two-point pick in view as well as the one asked about.
            Point[] keep = scale.PointA is { } a && scale.PointB is { } b
                ? [Canvas.ToView(a.X, a.Y), Canvas.ToView(b.X, b.Y)] : [Canvas.ToView(at.X, at.Y)];
            foreach (var box in new Control[] { DistanceBox, Z0Box }) Place(box, Canvas.ToView(at.X, at.Y), keep);
        }
        // A hole's or a copper region's picker beside it, off the piece itself.
        if (_vm?.SelectedItem is { } item && (_vm.HolePickerShown || _vm.CopperPickerShown))
        {
            var b = item.Bounds;
            Point[] keep = [Canvas.ToView(b.Left, b.Top), Canvas.ToView(b.Right, b.Bottom), Canvas.ToView(b.CentreX, b.CentreY)];
            Place(_vm.HolePickerShown ? HoleBox : CopperBox, Canvas.ToView(b.Right, b.Bottom), keep);
        }
        // The label editor beside the label's own box (PTU-4).
        if (_vm?.Plots.Label is { } label) { var p = Canvas.ToView(label.X, label.Y); Place(LabelBox, p, [p]); }
    }

    /// <summary>Below-right of the point, else whichever other side fits on the canvas and covers none of
    /// <paramref name="keep"/>; clamped inside the canvas when none does.</summary>
    private void Place(Control box, Point at, Point[] keep)
    {
        var size = SizeOf(box);
        const double gap = 12;
        (double X, double Y)[] sides =
        [
            (at.X + gap, at.Y + gap), (at.X - gap - size.Width, at.Y + gap),
            (at.X + gap, at.Y - gap - size.Height), (at.X - gap - size.Width, at.Y - gap - size.Height),
        ];
        foreach (var (x, y) in sides)
        {
            var r = new Rect(x, y, size.Width, size.Height);
            bool fits = x >= 4 && y >= 4 && r.Right <= Canvas.Bounds.Width - 4 && r.Bottom <= Canvas.Bounds.Height - 4;
            if (fits && !keep.Any(k => r.Inflate(6).Contains(k))) { SetAt(box, x, y); return; }
        }
        SetAt(box, sides[0].X, sides[0].Y);
    }

    private void SetAt(Control box, double x, double y)
    {
        var size = SizeOf(box);
        Avalonia.Controls.Canvas.SetLeft(box, Math.Clamp(x, 4, Math.Max(4, Canvas.Bounds.Width - size.Width - 4)));
        Avalonia.Controls.Canvas.SetTop(box, Math.Clamp(y, 4, Math.Max(4, Canvas.Bounds.Height - size.Height - 4)));
    }

    /// <summary>A box's size: measured when it has none yet (it is placed as it appears), else a generous guess.</summary>
    private static Size SizeOf(Control box)
    {
        if (box.Bounds.Width > 0) return box.Bounds.Size;
        box.Measure(Size.Infinity);
        return box.DesiredSize.Width > 0 ? box.DesiredSize : new Size(320, 80);
    }

    /// <summary>Other…'s size, Enter: given to the hand via selected (brief-dfr30-1 R-dfr30-1-2).</summary>
    private void OnOtherDrillKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm is null) return;
        _vm.ApplyOtherDrillCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Delete on the Corrections list removes the row selected.</summary>
    private void OnCorrectionsKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back) || _vm is null) return;
        _vm.DeleteSelectedCorrectionCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Remove ▾ (R-dfr30-1-4): its items made once, so the menu has them as it opens, and each one's count and
    /// whether it is enabled — never hidden — brought up to date as it opens.</summary>
    private void OnRemoveOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || _vm is not { } vm) return;
        var items = menu.Items.OfType<MenuItem>().ToList();
        void Say(MenuItem item, string what, int n) { item.Header = $"{what} ({n})"; item.IsEnabled = n > 0; }
        Say(items[0], "Vias read from the picture", vm.AutoViaCount);
        Say(items[1], "Vias placed by hand", vm.HandViaCount);
        Say(items[2], "Copper fills drawn", vm.CopperFillCount);
        Say(items[3], "Paint fills", vm.PaintedCount);
        Say(items[4], "Clears drawn", vm.ClearCount);
        Say(items[5], "The outline drawn", vm.OutlineCount);
        var read = items[6];
        read.Items.Clear();
        foreach (string layer in vm.ConductorChoices)
        {
            var one = new MenuItem { Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _vm?.RemoveCopperRead(layer)) };
            Say(one, layer, vm.CopperReadCount(layer));
            read.Items.Add(one);
        }
        read.IsEnabled = read.Items.OfType<MenuItem>().Any(m => m.IsEnabled);
    }

    /// <summary>Remove ▾'s items, made with the panel: a menu whose items arrive as it opens is laid out empty.</summary>
    private void BuildRemoveMenu()
    {
        if (RemoveButton.Flyout is not MenuFlyout menu) return;
        MenuItem Item(Action<ImageSourceViewModel> act) =>
            new() { Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { if (_vm is { } v) act(v); }) };
        menu.Items.Add(Item(v => v.RemoveAutoVias()));
        menu.Items.Add(Item(v => v.RemoveHandVias()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(v => v.RemoveCopperFills()));
        menu.Items.Add(Item(v => v.RemovePainted()));
        menu.Items.Add(Item(v => v.RemoveClears()));
        menu.Items.Add(Item(v => v.RemoveOutline()));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Copper read on" });
    }

    private void OnLabelKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.Plots.SetLabelCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Plots.CancelLabelCommand.Execute(null); e.Handled = true; }
    }

    /// <summary>Zoom to Fit hands the keyboard back to the canvas, as the editors' button does, so F and Space work next.</summary>
    private void OnZoomToFit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { Canvas.Fit(); Canvas.Focus(); }

    /// <summary>A box tool armed or disarmed: the canvas takes the keyboard back, so Escape cancels it.</summary>
    private void OnBoxTool(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.Focus();

    private void OnDistanceKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.Scale.ApplyDistanceCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Scale.CancelToolCommand.Execute(null); e.Handled = true; }
    }

    private void OnSizeKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.Scale.ApplyCornersCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Scale.CancelToolCommand.Execute(null); e.Handled = true; }
    }

    private void OnZ0Key(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.Scale.ApplyZ0Command.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Scale.CancelToolCommand.Execute(null); e.Handled = true; }
    }

    private void OnLayerEnter(object? sender, PointerEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is ImageLayerRowViewModel row) _vm.HoveredLayer = row;
    }

    private void OnLayerExit(object? sender, PointerEventArgs e)
    {
        if (_vm is not null && ReferenceEquals((sender as Control)?.DataContext, _vm.HoveredLayer)) _vm.HoveredLayer = null;
    }

    // ── a dropped picture (R-im5-2) ─────────────────────────────────────────────────────────────────

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedPath(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is not null && DroppedPath(e) is { } path) _vm.LoadFile(path);
        e.Handled = true;
    }

    /// <summary>The first file a drop carries — whatever it is: a file that is not a picture is refused by the read, in
    /// the status line, not silently here. The payload's type varies by platform (one item on macOS, a list elsewhere).</summary>
    internal static string? DroppedPath(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            string? path = item.TryGetRaw(DataFormat.File) switch
            {
                IStorageItem single => single.Path?.LocalPath,
                IEnumerable<IStorageItem> files => files.FirstOrDefault()?.Path?.LocalPath,
                string s => s,
                _ => null,
            };
            if (path is not null) return path;
        }
        return null;
    }
}
