// The picture, and what was read drawn over it — brief-img-5-dialog.md R-im5-3, R-im5-4, R-im5-8.
//
// DRAWS; DECIDES NOTHING. Everything drawn over the picture is an ImageOverlayItem the reading produced (ImageOverlay);
// a click asks the overlay which item is under it, and a drag hands a rectangle to the view model as the scope. The
// measure tools hand their clicks to ImageScaleViewModel, which states them to the trace.
//
// Gestures: wheel or pinch zooms about the pointer; right- or middle-drag, or Space held with a left-drag, pans; a
// left-drag sets the scope rectangle; a left-click selects; a double-click, the Fit button or F fits the picture. A
// picture stays fitted as the canvas changes size — the dialog opening, the window resized — until it is zoomed or panned. With a tool armed a
// left-click is the tool's; with Board corners… armed (IM-13) a left-drag on a handle moves it, and the overlay — read
// on the rectified picture — is not drawn over the photo as taken. On a plot picture with Read only here or Ignore here
// armed (PTU-6) a left-drag is the tool's box, handed to the view model on release; an Ignore here box is drawn hatched,
// selected by a click and removed by Delete. On a plot picture the pointer near a traced curve shows a crosshair and a
// readout at the snapped point (brief-pdg-3 R-pdg3-2) without changing what a click does; with the Point tool armed a
// click pins a point there, drawn as a numbered dot, selected by a click and removed by Delete.
//
// A plot read by hand (brief-pdg-4-digitise-by-hand.md R-pdg4-5) draws none of the overlay: it draws the box with its
// handles (corners and edges resize it, the round one above the top edge turns it) and the value typed at each edge, every
// curve's fixpoints in its colour, the sampled curve dashed over the picture, and a 4× loupe at the pointer while points
// are placed or dragged. A left-drag on empty picture with the box tool draws the box; a double-click places a point on the
// selected curve, a drag moves one, a right-click (without a drag) or Delete removes one; with Find curve armed a click
// follows the curve under it.
//
// On a layout picture with a scale (brief-dfr29-1 R-dfr29-1-3), the Via tool takes a click, and the Copper and Outline
// tools take a dragged rectangle or clicked corners that a double-click closes. A click on a hole or a copper region
// selects it for its picker. Cmd/Ctrl+Z undoes the last correction. Corrections are drawn dashed.
//
// A correction is an object (brief-dfr30-1 R-dfr30-1-3): a click on one selects it and its row. A selected fill, clear or
// outline shows its corners as handles — a drag moves one, a double-click on an edge adds one, Delete removes the one
// clicked (never below three) — and a selected hand via is dragged to a new place; Delete with no corner chosen deletes
// the correction. Each drag is one undo step, made on release. The board outline is drawn above the copper, 2 px in the
// accent with a halo, the picture outside it dimmed, whether it was drawn, set by the corners or read (R-dfr30-1-6).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using PixelPoint = CircuitRF.Design.Layout.Recognition.Image.PixelPoint;
using PixelRect = CircuitRF.Design.Layout.Recognition.Image.PixelRect;
using PixelBox = CircuitRF.Design.Schematic.Recognition.PixelBox;

namespace CircuitRF.Ui.Recognition;

public sealed class ImageOverlayCanvas : Control
{
    // The classes' colours are ImageOverlay.Colours — the flattened --overlay picture reads the same constants.
    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static readonly Color Amber = Rgb(ImageOverlay.Colours.Amber);
    private static readonly Color Accent = Rgb(ImageOverlay.Colours.Accent);
    private static readonly Color PortColour = Rgb(ImageOverlay.Colours.Port);
    // A read trace not selected (R-ptu3-3): thin, dashed and neutral, so the picture's own curve — whose colour says
    // which trace it is — shows through and beside it.
    private static readonly Color TraceNeutral = Color.FromArgb(0xD0, 0x40, 0x40, 0x40);

    private ImageSourceViewModel? _vm;
    private RasterImage? _rasterShown;
    private WriteableBitmap? _picture;
    private ImageOverlay? _overlayShown;
    private WriteableBitmap? _hatch;
    private (ImageOverlay? Overlay, int Cluster) _flashKey = (null, -1);
    private WriteableBitmap? _flash;
    private readonly Dictionary<string, Geometry> _geometry = new(StringComparer.Ordinal);

    private double _zoom = 1;
    private Vector _pan;
    private bool _autoFit = true;

    private Point? _pressAt;
    private Point _lastPointer;
    private ImageOverlayItem? _dragEnd;
    private bool _panning, _scoping, _spaceHeld;
    private PixelPoint? _hover;
    private CircuitRF.Design.Imaging.Plots.TraceProbeHit? _probe;
    private int _dragCorner = -1;
    private int _manualHandle = -1;
    private (ManualCurveViewModel Curve, int Index)? _manualPoint, _rightPress;
    private int _dragVertex = -1, _selectedVertex = -1, _vertexOf = -1;
    private bool _dragVia;
    private PixelPoint? _dragTo;
    private string? _manualReadout;
    /// <summary>What the item under the pointer on a layout picture is, one line (brief-dfr30-3 R-dfr30-3-3).</summary>
    private string? _hoverLine;
    private Point _pressPosition;

    /// <summary>How far above the box's top edge its rotation handle stands, screen pixels.</summary>
    private const double RotationLift = 26;

    public ImageOverlayCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        AddHandler(PinchEvent, OnPinch);
    }

    /// <summary>Raised when the view moves — the panel re-places its inline boxes.</summary>
    public event Action? ViewChanged;

    /// <summary>A picture pixel on the control.</summary>
    public Point ToView(double x, double y) => new(x * _zoom + _pan.X, y * _zoom + _pan.Y);

    /// <summary>A control point in picture pixels.</summary>
    public PixelPoint ToPicture(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    // ── the view model ──────────────────────────────────────────────────────────────────────────────

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Scale.PropertyChanged -= OnVmChanged;
            _vm.Plots.PropertyChanged -= OnPointsChanged;
            _vm.Plots.ProbePoints.CollectionChanged -= OnPointsChanged;
            _vm.FitRequested -= Fit;
        }
        _vm = DataContext as ImageSourceViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.Scale.PropertyChanged += OnVmChanged;
            _vm.Plots.PropertyChanged += OnPointsChanged;
            _vm.Plots.ProbePoints.CollectionChanged += OnPointsChanged;
            _vm.FitRequested += Fit;
        }
        _autoFit = true;
        InvalidateArrange();
        InvalidateVisual();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A new picture, or a photo swapping between as taken and rectified: fit the one now shown.
        // The raster usually arrives after the canvas was arranged, so arrange again to fit it.
        if (e.PropertyName is nameof(ImageSourceViewModel.Source) or nameof(ImageSourceViewModel.Raster)
            && !ReferenceEquals(_vm?.Raster, _rasterShown))
        {
            _autoFit = true;
            InvalidateArrange();
        }
        if (e.PropertyName == nameof(ImageSourceViewModel.SelectedItem) && _vm?.SelectedItem is { } item) BringIntoView(item);
        if (e.PropertyName is nameof(ImageSourceViewModel.Overlay) or nameof(ImageSourceViewModel.Raster)) _probe = null;
        if (e.PropertyName == nameof(ImageSourceViewModel.SelectedCorrection) && _vm?.SelectedCorrection != _vertexOf) _selectedVertex = -1;
        _vertexOf = _vm?.SelectedCorrection ?? -1;
        InvalidateVisual();
    }

    /// <summary>A point pinned, removed from the list or selected there: the dots are redrawn.</summary>
    private void OnPointsChanged(object? sender, EventArgs e)
    {
        if (e is PropertyChangedEventArgs p && p.PropertyName != nameof(PlotSessionViewModel.SelectedProbePoint)) return;
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (_autoFit) Fit(size);
        return size;
    }

    /// <summary>The whole picture in the control, with a margin — and kept so as the control resizes.</summary>
    public void Fit()
    {
        _autoFit = true;
        Fit(Bounds.Size);
    }

    private void Fit(Size size)
    {
        if (_vm?.Raster is not { } r || size.Width <= 0 || size.Height <= 0) return;
        // The legend floats over the top edge and the prompt over the bottom: the picture is fitted between them.
        const double margin = 16, top = 44, bottom = 44;
        _zoom = Math.Max(1e-3, Math.Min((size.Width - 2 * margin) / r.Width, (size.Height - top - bottom) / r.Height));
        _pan = new Vector((size.Width - r.Width * _zoom) / 2, top + (size.Height - top - bottom - r.Height * _zoom) / 2);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Pans just enough to bring an item on screen.</summary>
    private void BringIntoView(ImageOverlayItem item)
    {
        var b = item.Bounds;
        var tl = ToView(b.Left, b.Top);
        var br = ToView(b.Right, b.Bottom);
        var view = new Rect(Bounds.Size).Deflate(24);
        double dx = 0, dy = 0;
        if (br.X - tl.X > view.Width || tl.X < view.Left || br.X > view.Right) dx = view.Center.X - (tl.X + br.X) / 2;
        if (br.Y - tl.Y > view.Height || tl.Y < view.Top || br.Y > view.Bottom) dy = view.Center.Y - (tl.Y + br.Y) / 2;
        if (dx == 0 && dy == 0) return;
        _autoFit = false;
        _pan += new Vector(dx, dy);
        ViewChanged?.Invoke();
    }

    // ── drawing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The picture under a failure: faded enough to say nothing is drawn over it, not so far that it cannot be
    /// read — the failure note is small and sits at its foot.</summary>
    private const double FailureOpacity = 0.7;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new ImmutableSolidColorBrush(Color.Parse("#00000000")), new Rect(Bounds.Size));
        if (_vm?.Raster is not { } raster) return;
        EnsurePicture(raster);

        var pictureRect = new Rect(0, 0, raster.Width, raster.Height);
        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y)))
        {
            context.FillRectangle(Brushes.White, pictureRect);
            using (context.PushRenderOptions(new RenderOptions
                   { BitmapInterpolationMode = _zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
            using (context.PushOpacity(_vm.HasFailure ? FailureOpacity : 1.0))
                context.DrawImage(_picture!, pictureRect);

            if (!_vm.HasFailure && _vm.Scale.Tool != ImageMeasureTool.Corners && !_vm.IsManualPlotPicture) DrawOverlay(context, pictureRect);
        }
        context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(Color.Parse("#808080")), 1),
                              new Rect(ToView(0, 0), ToView(raster.Width, raster.Height)));
        DrawTools(context);
    }

    private void DrawOverlay(DrawingContext context, Rect pictureRect)
    {
        var vm = _vm!;
        var overlay = vm.Overlay;
        if (!ReferenceEquals(overlay, _overlayShown))
        {
            _overlayShown = overlay;
            _geometry.Clear();
            _hatch = BuildHatch(overlay);
        }
        if (_hatch is not null && (vm.ShowIgnored || vm.ShowUnknowns)) context.DrawImage(_hatch, pictureRect);

        if (vm.HoveredLayer is { } hovered && overlay.Clusters is { } clusters)
        {
            if (_flashKey != (overlay, hovered.Cluster))
            {
                _flashKey = (overlay, hovered.Cluster);
                _flash = BuildMask(clusters, [hovered.Cluster], Accent, 150, stripes: false);
            }
            if (_flash is not null) context.DrawImage(_flash, pictureRect);
        }

        double px = 1 / _zoom;
        var litTraces = vm.Plots.Lit(PlotTraceLink.TraceName(vm.SelectedItem));
        foreach (var item in overlay.Items)
        {
            if (item.Class == ImageOverlayClass.Trace && item.Id.StartsWith(ImageOverlay.FitPrefix, StringComparison.Ordinal))
            {
                // A trace's fitted curve (brief-pdg-2 R-pdg2-4): thin and dashed in its own contrasting colour, over the trace.
                context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Rgb(item.Rgb ?? ImageOverlay.FitColour(0))), px,
                                                   new ImmutableDashStyle([4, 3], 0)), GeometryOf(item));
                continue;
            }
            if (item.Class == ImageOverlayClass.Trace)
            {
                DrawTrace(context, item, PlotTraceLink.TraceName(item) is { } traceName && litTraces.Contains(traceName), px);
                continue;
            }
            // The outline drawn is drawn below as THE outline, above everything else (R-dfr30-1-6).
            if (item.Class == ImageOverlayClass.Line && !item.Unknown && item.Id.StartsWith(ImageOverlay.CorrectionPrefix, StringComparison.Ordinal)) continue;
            bool unknownShown = item.Unknown && vm.ShowUnknowns;
            if (!vm.Shows(item.Class) && !unknownShown) continue;
            bool selected = ReferenceEquals(item, vm.SelectedItem);
            var colour = item.Unknown ? Amber : ColourOf(item);
            var pen = new Pen(new ImmutableSolidColorBrush(selected ? Accent : colour), (selected ? 3 : item.Class == ImageOverlayClass.Copper ? 1.25 : 1.75) * px,
                              // A correction drawn over the reading in a dash of its own (brief-dfr29-1 R-dfr29-1-3).
                              item.Id.StartsWith(ImageOverlay.CorrectionPrefix, StringComparison.Ordinal) ? new ImmutableDashStyle([4, 2], 0) : null);

            switch (item.Class)
            {
                // A via is the circle of its drill; a hole kept open is its own outline (the default case).
                case ImageOverlayClass.Drill when item.Circle is { } c && item.Paths.Count == 0:
                    context.DrawEllipse(null, pen, new Point(c.X, c.Y), c.R, c.R);
                    break;
                case ImageOverlayClass.Port when item.Circle is { } c:
                    DrawPort(context, c, item.Label, selected ? Accent : PortColour, px);
                    break;
                case ImageOverlayClass.Wire when item.Circle is { } c:
                    // A junction dot filled; a dangling end a ring — the handle that is dragged onto a pin.
                    bool dot = item.Id.StartsWith("dot:", StringComparison.Ordinal);
                    context.DrawEllipse(dot ? new ImmutableSolidColorBrush(colour) : new ImmutableSolidColorBrush(Colors.White, 0.6), pen,
                                        new Point(c.X, c.Y), c.R, c.R);
                    break;
                case ImageOverlayClass.Plot when item.Circle is { } c:
                    // A chart's frame (IM-14): its outer circle.
                    context.DrawEllipse(null, pen, new Point(c.X, c.Y), c.R, c.R);
                    break;
                case ImageOverlayClass.Marker when item.Circle is { } c:
                    context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White, 0.6), pen, new Point(c.X, c.Y), c.R * px * 1.2, c.R * px * 1.2);
                    break;
                case ImageOverlayClass.Annotation:
                    // An annotation stroke (IMF-3): dashed, so what was set aside is told from what was read as line work.
                    context.DrawGeometry(null, new Pen(pen.Brush, pen.Thickness, new ImmutableDashStyle([3, 3], 0)), GeometryOf(item));
                    break;
                case ImageOverlayClass.Crossing when item.Circle is { } c:
                    // Read as connected: a filled disc; not connected: a ring with a gap — what a click flips.
                    var ring = new Pen(new ImmutableSolidColorBrush(selected ? Accent : colour), 2 * px);
                    context.DrawEllipse(item.Rgb == ImageOverlay.Colours.Connected ? new ImmutableSolidColorBrush(colour, 0.35) : null, ring, new Point(c.X, c.Y), c.R, c.R);
                    if (selected && item.Label is { } l) DrawLabel(context, item, l, Accent, px);
                    break;
                default:
                    var g = GeometryOf(item);
                    // A halo under the outline: copper is outlined in its layer's colour, which is very often the
                    // colour the picture drew it in, and an outline the colour of what it outlines cannot be seen.
                    if (!selected && item.Class is ImageOverlayClass.Copper or ImageOverlayClass.Part)
                        context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Colors.White, 0.85), pen.Thickness + 2 * px), g);
                    IBrush? fill = item.Class == ImageOverlayClass.Copper
                        ? new ImmutableSolidColorBrush(colour, 0.18)
                        : selected && item.Class == ImageOverlayClass.Part ? new ImmutableSolidColorBrush(Accent, 0.12) : null;
                    context.DrawGeometry(fill, pen, g);
                    break;
            }
            if (item.Label is { Length: > 0 } label
                && item.Class is ImageOverlayClass.Part or ImageOverlayClass.Line or ImageOverlayClass.Text
                              or ImageOverlayClass.Plot or ImageOverlayClass.Marker or ImageOverlayClass.Annotation)
                DrawLabel(context, item, label, selected ? Accent : colour, px);
        }

        if (vm.IsLayoutPicture && vm.OutlinePx is { Count: >= 3 } outline) DrawOutline(context, pictureRect, outline, px);

        if (vm.IsPlotPicture)
            foreach (var b in vm.Plots.IgnoreBoxes)
                if (b.Picture == vm.Plots.ShownPicture) DrawIgnoreBox(context, b.Box, Equals(b, vm.Plots.SelectedIgnoreBox), px);

        if (vm.ScopeRect is { } s)
        {
            // What the crop leaves out is dimmed, as outside the board outline is, so it reads as not read.
            var kept = new Rect(s.Left, s.Top, s.Right - s.Left, s.Bottom - s.Top);
            context.DrawGeometry(new ImmutableSolidColorBrush(Colors.Black, 0.3), null,
                                 new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(pictureRect), new RectangleGeometry(kept)));
            context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(Accent), 1.5 * px, new ImmutableDashStyle([4, 3], 0)), kept);
        }
    }

    /// <summary>The board outline (R-dfr30-1-6): the picture outside it dimmed, then a 2 px line in the accent over a halo,
    /// above the copper — seen after the tool is put down, on a picture of any colour.</summary>
    private static void DrawOutline(DrawingContext context, Rect pictureRect, IReadOnlyList<PixelPoint> outline, double px)
    {
        var ring = new StreamGeometry();
        using (var g = ring.Open())
        {
            g.BeginFigure(new Point(outline[0].X, outline[0].Y), true);
            for (int k = 1; k < outline.Count; k++) g.LineTo(new Point(outline[k].X, outline[k].Y));
            g.EndFigure(true);
        }
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(pictureRect), ring);
        context.DrawGeometry(new ImmutableSolidColorBrush(Colors.Black, 0.3), null, outside);
        context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Colors.White, 0.85), 5 * px), ring);
        context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Accent), 2 * px), ring);
    }

    /// <summary>
    /// A read trace, or one of its bridged gaps (R-ptu3-3). Unselected: 1 px, dashed, neutral — never amber and never
    /// the trace's own colour, so the curve under it can be seen and its colour judged; whether it is assigned is the
    /// table's to say. Selected: the accent at 2 px, with its label, so the user sees which pixels the row means.
    /// </summary>
    private void DrawTrace(DrawingContext context, ImageOverlayItem item, bool selected, double px)
    {
        bool gap = item.Id.StartsWith(ImageOverlay.GapPrefix, StringComparison.Ordinal);
        var pen = selected
            ? new Pen(new ImmutableSolidColorBrush(Accent), 2 * px, gap ? new ImmutableDashStyle([2, 2], 0) : null)
            : new Pen(new ImmutableSolidColorBrush(TraceNeutral), px, new ImmutableDashStyle(gap ? [1, 4] : [3, 3], 0));
        context.DrawGeometry(null, pen, GeometryOf(item));
        if (selected && !gap && item.Label is { Length: > 0 } label)
            DrawLabel(context, item, string.Join(" · ", label.Split(' ', StringSplitOptions.RemoveEmptyEntries)), Accent, px);
    }

    /// <summary>An Ignore here box (R-ptu6-3): hatched in the Ignored class's grey, the accent when selected.</summary>
    private static void DrawIgnoreBox(DrawingContext context, PixelBox box, bool selected, double px)
    {
        var rect = new Rect(box.Left, box.Top, box.Width, box.Height);
        var grey = Rgb(ImageOverlay.Colours.Ignored);
        context.FillRectangle(new ImmutableSolidColorBrush(Colors.White, 0.35), rect);
        using (context.PushClip(rect))
        {
            var hatch = new Pen(new ImmutableSolidColorBrush(grey, 0.67), 1.5 * px);
            double step = 8 * px;
            for (double d = -rect.Height; d < rect.Width; d += step)
                context.DrawLine(hatch, new Point(rect.Left + d, rect.Bottom), new Point(rect.Left + d + rect.Height, rect.Top));
        }
        context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(selected ? Accent : grey), (selected ? 2.5 : 1.25) * px), rect);
    }

    private static Color ColourOf(ImageOverlayItem item) => Rgb(ImageOverlay.ColourOf(item with { Unknown = false }));

    private Geometry GeometryOf(ImageOverlayItem item)
    {
        if (_geometry.TryGetValue(item.Id, out var g)) return g;
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            if (item.Closed) ctx.SetFillRule(FillRule.EvenOdd);
            foreach (var path in item.Paths)
            {
                if (path.Count == 0) continue;
                ctx.BeginFigure(new Point(path[0].X, path[0].Y), item.Closed);
                for (int i = 1; i < path.Count; i++) ctx.LineTo(new Point(path[i].X, path[i].Y));
                if (path.Count == 1) ctx.LineTo(new Point(path[0].X + 0.01, path[0].Y));
                ctx.EndFigure(item.Closed);
            }
        }
        _geometry[item.Id] = sg;
        return sg;
    }

    private static void DrawPort(DrawingContext context, (double X, double Y, double R) c, string? label, Color colour, double px)
    {
        // The schematic's port glyph: a circle with the port's number beside it.
        double r = 7 * px;
        var pen = new Pen(new ImmutableSolidColorBrush(colour), 1.75 * px);
        context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White, 0.85), pen, new Point(c.X, c.Y), r, r);
        context.DrawLine(pen, new Point(c.X - r * 0.6, c.Y), new Point(c.X + r * 0.6, c.Y));
        if (label is null) return;
        var text = Text(label, colour, px);
        context.DrawText(text, new Point(c.X + r + 2 * px, c.Y - text.Height / 2));
    }

    private static void DrawLabel(DrawingContext context, ImageOverlayItem item, string label, Color colour, double px)
    {
        var text = Text(label, colour, px);
        Point at = item.Class == ImageOverlayClass.Line && item.Paths.Count > 0 && item.Paths[0].Count > 0
            ? Midpoint(item.Paths[0])
            : new Point(item.Bounds.Left, item.Bounds.Top - text.Height - 1 * px);
        if (item.Class == ImageOverlayClass.Line) at = new Point(at.X - text.Width / 2, at.Y - text.Height - 3 * px);
        context.FillRectangle(new ImmutableSolidColorBrush(Colors.White, 0.8),
                              new Rect(at.X - 2 * px, at.Y, text.Width + 4 * px, text.Height));
        context.DrawText(text, at);
    }

    private static Point Midpoint(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count == 1) return new Point(path[0].X, path[0].Y);
        int i = path.Count / 2;
        return new Point((path[i - 1].X + path[i].X) / 2, (path[i - 1].Y + path[i].Y) / 2);
    }

    private static FormattedText Text(string s, Color colour, double px) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11 * px,
            new ImmutableSolidColorBrush(colour));

    private void DrawTools(DrawingContext context)
    {
        var scale = _vm!.Scale;
        var pen = new Pen(new ImmutableSolidColorBrush(Accent), 2);
        void Mark(PixelPoint p)
        {
            var v = ToView(p.X, p.Y);
            context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White), pen, v, 4, 4);
        }
        if (scale.Tool == ImageMeasureTool.TwoPoints)
        {
            if (scale.PointA is { } a)
            {
                var end = scale.PointB ?? _hover;
                if (end is { } b) context.DrawLine(new Pen(pen.Brush, 1.5, new ImmutableDashStyle([5, 3], 0)), ToView(a.X, a.Y), ToView(b.X, b.Y));
                Mark(a);
            }
            if (scale.PointB is { } pb) Mark(pb);
        }
        else if (scale.Tool == ImageMeasureTool.Impedance && scale.PointA is { } p) Mark(p);
        else if (scale.Tool == ImageMeasureTool.Corners && scale.CornerPoints is { Count: 4 } corners)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(ToView(corners[0].X, corners[0].Y), true);
                for (int k = 1; k < 4; k++) g.LineTo(ToView(corners[k].X, corners[k].Y));
                g.EndFigure(true);
            }
            context.DrawGeometry(new ImmutableSolidColorBrush(Accent, 0.08), new Pen(pen.Brush, 1.5), geometry);
            for (int k = 0; k < 4; k++)
            {
                var v = ToView(corners[k].X, corners[k].Y);
                context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White), pen, v, 6, 6);
                context.DrawText(Text((k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Accent, 1), new Point(v.X + 8, v.Y - 16));
            }
        }

        if (_scoping && _pressAt is { } from)
            context.DrawRectangle(new ImmutableSolidColorBrush(Accent, 0.08), new Pen(pen.Brush, 1, new ImmutableDashStyle([4, 3], 0)),
                                  new Rect(from, _lastPointer).Normalize());
        if (_dragEnd is { Circle: { } c } && _pressAt is not null)
            context.DrawLine(new Pen(pen.Brush, 1.5, new ImmutableDashStyle([5, 3], 0)), ToView(c.X, c.Y), _lastPointer);

        // A polygon being drawn (R-dfr29-1-3): its corners so far, and the edge to the pointer.
        if (_vm.DrawPoints.Count > 0)
        {
            var dash = new Pen(pen.Brush, 1.5, new ImmutableDashStyle([4, 2], 0));
            for (int k = 1; k < _vm.DrawPoints.Count; k++)
                context.DrawLine(dash, ToView(_vm.DrawPoints[k - 1].X, _vm.DrawPoints[k - 1].Y), ToView(_vm.DrawPoints[k].X, _vm.DrawPoints[k].Y));
            context.DrawLine(dash, ToView(_vm.DrawPoints[^1].X, _vm.DrawPoints[^1].Y), _lastPointer);
            foreach (var corner in _vm.DrawPoints) Mark(corner);
        }

        DrawSelectedCorrection(context);
        if (_vm.IsManualPlotPicture) DrawManual(context);
        else if (_vm.IsPlotPicture && !_vm.HasFailure) DrawProbes(context);
        if (_hoverLine is { } line && _hover is { } over && !_panning)
        {
            var v = ToView(over.X, over.Y);
            Readout(context, line, new Point(v.X + 14, v.Y + 12), Colors.Black);
        }
    }

    /// <summary>The selected correction (R-dfr30-1-3): a shape's corners as handles — the one clicked filled — and its edges
    /// as they will be while a corner is dragged; a hand via ringed, at the pointer while it is dragged. Screen pixels.</summary>
    private void DrawSelectedCorrection(DrawingContext context)
    {
        if (_vm is not { IsLayoutPicture: true, SelectedCorrectionRecord: { } fix } vm) return;
        var accent = new ImmutableSolidColorBrush(Accent);
        if (fix.IsPoint || fix.Kind == ImageCorrectionKind.PaintFill)
        {
            var at = _dragVia && _dragTo is { } to ? to : fix.At;
            double r = fix.IsPoint ? Math.Max(6, vm.HoleRadiusAt(fix.At) * _zoom + 3) : 8;
            context.DrawEllipse(null, new Pen(new ImmutableSolidColorBrush(Colors.White, 0.85), 4), ToView(at.X, at.Y), r, r);
            context.DrawEllipse(null, new Pen(accent, 2), ToView(at.X, at.Y), r, r);
            return;
        }
        if (fix.Points.Count < 3 || fix is { Kind: ImageCorrectionKind.Clear, Layer: not null }) return;
        var pts = fix.Points.ToList();
        if (_dragVertex >= 0 && _dragVertex < pts.Count && _dragTo is { } moved) pts[_dragVertex] = moved;
        var edge = new Pen(accent, 1.5, new ImmutableDashStyle([4, 2], 0));
        for (int k = 0; k < pts.Count; k++)
            context.DrawLine(edge, ToView(pts[k].X, pts[k].Y), ToView(pts[(k + 1) % pts.Count].X, pts[(k + 1) % pts.Count].Y));
        for (int k = 0; k < pts.Count; k++)
        {
            var v = ToView(pts[k].X, pts[k].Y);
            context.DrawRectangle(k == _selectedVertex ? accent : Brushes.White, new Pen(accent, 1.5), new Rect(v.X - 4, v.Y - 4, 8, 8));
        }
    }

    // ── a plot read by hand (R-pdg4-5) ─────────────────────────────────────────────────────────────────

    private ManualPlotViewModel? Manual => _vm is { IsManualPlotPicture: true } vm ? vm.Plots.ShownManual : null;

    /// <summary>The box, its handles and edge values, the curves' fixpoints, the sampled preview, the readout and the loupe —
    /// in screen pixels, so they keep their size at any zoom.</summary>
    private void DrawManual(DrawingContext context)
    {
        var plots = _vm!.Plots;
        if (Manual is not { } m) return;

        // The sampled curves, dashed in their own colours, carried back from the box's space onto the picture.
        if (plots.Session is { } session)
            foreach (var t in session.Traces.Where(t => t.Manual && t.Samples.Count > 1))
            {
                var plot = session.Plots[t.Plot - 1];
                if (plot.Picture != plots.ShownPicture) continue;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    for (int i = 0; i < t.Samples.Count; i++)
                    {
                        var (x, y) = plot.ToPicture(t.Samples[i].Px, t.Samples[i].Py);
                        if (i == 0) ctx.BeginFigure(ToView(x, y), false); else ctx.LineTo(ToView(x, y));
                    }
                    ctx.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Rgb(t.Curve.Rgb)), 1.75, new ImmutableDashStyle([4, 3], 0)), g);
            }

        if (m.Box is { } box)
        {
            var corners = box.Quad.Corners.Select(c => ToView(c.X, c.Y)).ToList();
            var outline = new StreamGeometry();
            using (var g = outline.Open())
            {
                g.BeginFigure(corners[0], true);
                for (int k = 1; k < 4; k++) g.LineTo(corners[k]);
                g.EndFigure(true);
            }
            var accent = new ImmutableSolidColorBrush(Accent);
            context.DrawGeometry(null, new Pen(accent, 1.5, m.Suggested ? new ImmutableDashStyle([5, 3], 0) : null), outline);
            var handles = m.Handles(RotationLift / _zoom).Select(h => ToView(h.X, h.Y)).ToList();
            context.DrawLine(new Pen(accent, 1), handles[6], handles[8]);
            for (int k = 0; k < 8; k++)
                context.DrawRectangle(Brushes.White, new Pen(accent, 1.5), new Rect(handles[k].X - 3.5, handles[k].Y - 3.5, 7, 7));
            context.DrawEllipse(Brushes.White, new Pen(accent, 1.5), handles[8], 5, 5);

            // Each typed value at its edge, so a wrong corner is seen at once.
            var (xl, xh, yl, yh, y2l, y2h) = m.EdgeTexts;
            void Edge(string text, Point at, bool right, bool below)
            {
                if (text.Length == 0) return;
                var t = Text(text, Accent, 1);
                double x = right ? at.X + 8 : at.X - t.Width - 8, y = below ? at.Y + 6 : at.Y - t.Height / 2;
                if (below) x = right ? at.X - t.Width : at.X;
                Readout(context, text, new Point(x, y), Accent);
            }
            Edge(xl, corners[0], right: false, below: true);
            Edge(xh, corners[1], right: true, below: true);
            Edge(yl, corners[0], right: false, below: false);
            Edge(yh, corners[3], right: false, below: false);
            Edge(y2l, corners[1], right: true, below: false);
            Edge(y2h, corners[2], right: true, below: false);
        }

        // The fixpoints: every curve's in its colour, the selected curve's larger, the selected point ringed.
        foreach (var c in m.Curves)
        {
            bool mine = ReferenceEquals(c, m.SelectedCurve);
            var fill = new ImmutableSolidColorBrush(Rgb(c.Rgb));
            for (int i = 0; i < c.Fixpoints.Count; i++)
            {
                var at = ToView(c.Fixpoints[i].X, c.Fixpoints[i].Y);
                context.DrawEllipse(fill, new Pen(Brushes.White, 1.25), at, mine ? 4.5 : 3.5, mine ? 4.5 : 3.5);
                if (m.SelectedPoint is { } sp && ReferenceEquals(sp.Curve, c) && sp.Index == i)
                    context.DrawEllipse(null, new Pen(new ImmutableSolidColorBrush(Accent), 2), at, 7.5, 7.5);
            }
        }

        if (_hover is not { } hover || _panning) return;
        bool placing = plots.ManualTool is ManualTool.Points or ManualTool.Find && m.SelectedCurve is not null;
        if (placing || _manualPoint is not null) DrawLoupe(context, hover);
        if (_manualReadout is { } readout && _manualHandle < 0)
        {
            var v = ToView(hover.X, hover.Y);
            Readout(context, readout, new Point(v.X + 12, v.Y + 10), Accent);
        }
    }

    /// <summary>A 4× magnifier of the picture around the pointer, beside it: the precision a hand-placed point needs.</summary>
    private void DrawLoupe(DrawingContext context, PixelPoint at)
    {
        if (_picture is null) return;
        const double r = 46, magnify = 4;
        var v = ToView(at.X, at.Y);
        double cx = v.X + r + 22, cy = v.Y - r - 22;
        if (cx + r > Bounds.Width - 4) cx = v.X - r - 22;
        if (cy - r < 4) cy = v.Y + r + 22;
        var dest = new Rect(cx - r, cy - r, 2 * r, 2 * r);
        double half = r / (magnify * _zoom);
        var source = new Rect(at.X - half, at.Y - half, 2 * half, 2 * half);
        using (context.PushClip(new RoundedRect(dest, r)))
        {
            context.FillRectangle(Brushes.White, dest);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                context.DrawImage(_picture, source, dest);
            var cross = new Pen(new ImmutableSolidColorBrush(Accent), 1);
            context.DrawLine(cross, new Point(cx - r, cy), new Point(cx - 3, cy));
            context.DrawLine(cross, new Point(cx + 3, cy), new Point(cx + r, cy));
            context.DrawLine(cross, new Point(cx, cy - r), new Point(cx, cy - 3));
            context.DrawLine(cross, new Point(cx, cy + 3), new Point(cx, cy + r));
        }
        context.DrawEllipse(null, new Pen(new ImmutableSolidColorBrush(Accent), 1.5), new Point(cx, cy), r, r);
    }

    /// <summary>The box handle within reach of a control point, or −1.</summary>
    private int ManualHandleAt(Point at)
    {
        if (Manual is not { HasBox: true } m) return -1;
        var handles = m.Handles(RotationLift / _zoom);
        int best = -1;
        double reach = 9 * 9;
        for (int k = handles.Count - 1; k >= 0; k--)
        {
            var v = ToView(handles[k].X, handles[k].Y);
            double d = (v.X - at.X) * (v.X - at.X) + (v.Y - at.Y) * (v.Y - at.Y);
            if (d <= reach) { reach = d; best = k; }
        }
        return best;
    }

    /// <summary>The pinned points as numbered dots, and the hover's crosshair and readout (R-pdg3-2), in screen pixels so
    /// they keep their size at any zoom.</summary>
    private void DrawProbes(DrawingContext context)
    {
        var plots = _vm!.Plots;
        foreach (var p in plots.ProbePoints)
        {
            if (p.Picture != plots.ShownPicture || p.Hit is null) continue;
            bool selected = ReferenceEquals(p, plots.SelectedProbePoint);
            var at = ToView(p.Px, p.Py);
            var colour = selected ? Accent : PortColour;
            context.DrawEllipse(new ImmutableSolidColorBrush(colour), new Pen(Brushes.White, 1.5), at, 4.5, 4.5);
            Readout(context, p.Name, new Point(at.X + 6, at.Y - 18), colour);
        }
        if (_probe is not { } h || _panning || _scoping) return;
        var v = ToView(h.Px, h.Py);
        var cross = new Pen(new ImmutableSolidColorBrush(Accent), 1.25);
        context.DrawLine(cross, new Point(v.X - 7, v.Y), new Point(v.X - 2, v.Y));
        context.DrawLine(cross, new Point(v.X + 2, v.Y), new Point(v.X + 7, v.Y));
        context.DrawLine(cross, new Point(v.X, v.Y - 7), new Point(v.X, v.Y - 2));
        context.DrawLine(cross, new Point(v.X, v.Y + 2), new Point(v.X, v.Y + 7));
        Readout(context, plots.ProbeText(h), new Point(v.X + 10, v.Y + 8), Accent);
    }

    /// <summary>A line of text on a white backing at <paramref name="at"/>, kept inside the canvas.</summary>
    private void Readout(DrawingContext context, string text, Point at, Color colour)
    {
        var t = Text(text, colour, 1);
        double x = Math.Min(at.X, Bounds.Width - t.Width - 6), y = Math.Min(at.Y, Bounds.Height - t.Height - 4);
        context.FillRectangle(new ImmutableSolidColorBrush(Colors.White, 0.88), new Rect(x - 3, y - 1, t.Width + 6, t.Height + 2), 2);
        context.DrawText(t, new Point(x, y));
    }

    // ── bitmaps ─────────────────────────────────────────────────────────────────────────────────────

    private void EnsurePicture(RasterImage raster)
    {
        if (ReferenceEquals(raster, _rasterShown) && _picture is not null) return;
        _rasterShown = raster;
        _picture?.Dispose();
        _picture = FromRgba(raster.Width, raster.Height, raster.Rgba);
    }

    private static WriteableBitmap FromRgba(int w, int h, byte[] rgba)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        int row = w * 4;
        for (int y = 0; y < h; y++) Marshal.Copy(rgba, y * row, fb.Address + y * fb.RowBytes, row);
        return bmp;
    }

    /// <summary>Ignored colours hatched grey, colours mapped to nothing hatched amber.</summary>
    private static WriteableBitmap? BuildHatch(ImageOverlay overlay)
    {
        if (overlay.Clusters is not { } clusters || (overlay.IgnoredClusters.Count == 0 && overlay.UnmappedClusters.Count == 0)) return null;
        int w = clusters.Width, h = clusters.Height;
        var px = new byte[w * h * 4];
        var ignored = overlay.IgnoredClusters.ToHashSet();
        var unmapped = overlay.UnmappedClusters.ToHashSet();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (((x + y) & 7) >= 2) continue;
                int l = clusters.Label(x, y);
                Color? c = unmapped.Contains(l) ? Amber : ignored.Contains(l) ? Rgb(ImageOverlay.Colours.Ignored) : null;
                if (c is not { } col) continue;
                int i = (y * w + x) * 4;
                px[i] = col.R; px[i + 1] = col.G; px[i + 2] = col.B; px[i + 3] = 170;
            }
        return FromRgba(w, h, px);
    }

    private static WriteableBitmap BuildMask(ColourClusterSet clusters, IReadOnlyCollection<int> which, Color colour, byte alpha, bool stripes)
    {
        int w = clusters.Width, h = clusters.Height;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (stripes && ((x + y) & 7) >= 2) continue;
                if (!which.Contains(clusters.Label(x, y))) continue;
                int i = (y * w + x) * 4;
                px[i] = colour.R; px[i + 1] = colour.G; px[i + 2] = colour.B; px[i + 3] = alpha;
            }
        return FromRgba(w, h, px);
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The corner handle within reach of a control point, or −1.</summary>
    private int CornerAt(Point at)
    {
        if (_vm?.Scale is not { Tool: ImageMeasureTool.Corners, CornerPoints: { Count: 4 } corners }) return -1;
        int best = -1;
        double reach = 10 * 10;
        for (int k = 0; k < 4; k++)
        {
            var v = ToView(corners[k].X, corners[k].Y);
            double d = (v.X - at.X) * (v.X - at.X) + (v.Y - at.Y) * (v.Y - at.Y);
            if (d <= reach) { reach = d; best = k; }
        }
        return best;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAbout(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        e.Handled = true;
    }

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        ZoomAbout(e.ScaleOrigin, e.Scale > 0 ? Math.Pow(e.Scale, 0.2) : 1);
        e.Handled = true;
    }

    private void ZoomAbout(Point at, double factor)
    {
        double z = Math.Clamp(_zoom * factor, 0.01, 64);
        factor = z / _zoom;
        _autoFit = false;
        _pan = new Vector(at.X - (at.X - _pan.X) * factor, at.Y - (at.Y - _pan.Y) * factor);
        _zoom = z;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space) { _spaceHeld = true; Cursor = new Cursor(StandardCursorType.Hand); e.Handled = true; }
        else if (e.Key == Key.Escape && _vm is not null)
        {
            _vm.CancelTools();
            _scoping = false;
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && Manual is { } manual && manual.RemoveSelectedPoint())
        {
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && Manual is not null)
        {
            _vm!.Plots.UndoCommand.Execute(null);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && _vm is { IsLayoutPicture: true } corrected)
        {
            // A correction made by hand undone, one choice at a time (R-dfr29-1-2).
            corrected.UndoCorrection();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _vm is { IsLayoutPicture: true, SelectedCorrection: >= 0 } fixing)
        {
            // The corner clicked, else the correction selected (R-dfr30-1-3).
            if (_selectedVertex >= 0) fixing.RemoveCorrectionVertex(fixing.SelectedCorrection, _selectedVertex);
            else fixing.DeleteCorrection(fixing.SelectedCorrection);
            _selectedVertex = -1;
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _vm is { IsPlotPicture: true } && _vm.Plots.SelectedProbePoint is { } point)
        {
            _vm.Plots.RemoveProbePoint(point);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _vm?.Plots.SelectedIgnoreBox is { } box)
        {
            _vm.Plots.RemoveIgnoreBox(box);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _vm is { HasScope: true } scoped)
        {
            // The rectangle dragged on the picture is deleted like any other drawn box: the whole picture is read again.
            scoped.WholePictureCommand.Execute(null);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        _hoverLine = null;
        InvalidateVisual();
        if (_probe is null) return;
        _probe = null;
        InvalidateVisual();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) { _spaceHeld = false; Cursor = new Cursor(StandardCursorType.Cross); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _lastPointer = point.Position;
        _pressPosition = point.Position;
        // By hand: a double-click places a point on the selected curve (R-pdg4-5) rather than fitting the picture.
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed && Manual is { SelectedCurve: not null } placing
            && _vm!.Plots.ManualTool == ManualTool.Points)
        {
            var placed = ToPicture(point.Position);
            if (placing.PointAt(placed.X, placed.Y, 6 / _zoom) is null) placing.AddPoint(placed.X, placed.Y);
            _manualPoint = null;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        // A polygon being drawn with the Copper or Outline tool (R-dfr29-1-3): a double-click closes it.
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed && _vm is { DrawTool: PictureDrawTool.Copper or PictureDrawTool.Outline } drawing)
        {
            drawing.ClosePolygon();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        // A double-click on the selected shape's edge adds a corner there (R-dfr30-1-3).
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed && _vm is { DrawTool: PictureDrawTool.None, SelectedCorrection: >= 0 } shaping
            && ToPicture(point.Position) is var onEdge && shaping.EdgeAt(shaping.SelectedCorrection, onEdge, 5 / _zoom) is >= 0 and var edgeAt)
        {
            shaping.InsertCorrectionVertex(shaping.SelectedCorrection, edgeAt, onEdge);
            _selectedVertex = edgeAt + 1;
            _dragVertex = -1;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed && _vm?.Scale.Tool == ImageMeasureTool.None)
        {
            Fit();
            e.Handled = true;
            return;
        }
        _panning = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed
                   || (point.Properties.IsLeftButtonPressed && _spaceHeld);
        _pressAt = point.Properties.IsLeftButtonPressed && !_panning ? point.Position : null;
        _scoping = false;
        _dragCorner = _pressAt is { } press ? CornerAt(press) : -1;
        _manualHandle = -1;
        _manualPoint = null;
        _rightPress = null;
        _dragVertex = -1;
        _dragVia = false;
        _dragTo = null;
        if (_pressAt is { } held && _vm is { IsLayoutPicture: true, DrawTool: PictureDrawTool.None, SelectedCorrectionRecord: { } chosen } editing
            && editing.Scale.Tool == ImageMeasureTool.None)
        {
            var on = ToPicture(held);
            if (!chosen.IsPoint && chosen.Kind != ImageCorrectionKind.PaintFill) _dragVertex = editing.VertexAt(editing.SelectedCorrection, on, 6 / _zoom);
            else if (chosen.Kind is ImageCorrectionKind.Via or ImageCorrectionKind.PaintFill && editing.CorrectionAt(on, 4 / _zoom) == editing.SelectedCorrection) _dragVia = true;
            if (_dragVertex >= 0) _selectedVertex = _dragVertex;
        }
        if (Manual is { } m)
        {
            var pressed = ToPicture(point.Position);
            if (point.Properties.IsRightButtonPressed) _rightPress = m.PointAt(pressed.X, pressed.Y, 6 / _zoom);
            else if (_pressAt is { } p0 && _vm!.Plots.ManualTool != ManualTool.Find)
            {
                _manualHandle = ManualHandleAt(p0);
                if (_manualHandle < 0 && m.PointAt(pressed.X, pressed.Y, 6 / _zoom) is { } onPoint)
                {
                    _manualPoint = onPoint;
                    m.SelectedCurve = onPoint.Curve;
                    m.SelectedPoint = onPoint;
                }
                if (_manualHandle >= 0 || _manualPoint is not null) m.BeginEdit();
            }
        }
        // A dangling wire end pressed on a schematic picture is dragged, not scoped: dropped on a pin it joins it (R-im10-8).
        _dragEnd = _pressAt is { } at && _vm is { IsSchematicPicture: true } vm0
            ? vm0.Overlay.HitTest(ToPicture(at).X, ToPicture(at).Y, 4 / _zoom, vm0.Shows) is { } hit
              && hit.Id.StartsWith(ImageOverlay.EndPrefix, StringComparison.Ordinal) ? hit : null
            : null;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _hover = ToPicture(p);
        _probe = _vm is { IsAutomaticPlotPicture: true, HasFailure: false } && !_panning && _dragCorner < 0
            ? _vm.Plots.ProbeAt(_hover.Value.X, _hover.Value.Y, PlotSessionViewModel.SnapScreenPx / _zoom)
            : null;
        _manualReadout = Manual is not null && !_panning ? _vm!.Plots.ManualReadout(_hover.Value.X, _hover.Value.Y) : null;
        _hoverLine = _vm is { IsLayoutPicture: true } layout && !_panning && _pressAt is null
                     && layout.Overlay.HitTest(_hover.Value.X, _hover.Value.Y, 4 / _zoom,
                                                c => layout.Shows(c) || (c == ImageOverlayClass.Line && !layout.MakeSchematic), corrections: true) is { } under
            ? layout.HoverLine(under) : null;
        if (_panning)
        {
            _autoFit = false;
            _pan += p - _lastPointer;
            ViewChanged?.Invoke();
        }
        else if (_dragCorner >= 0 && _vm is not null)
        {
            _vm.Scale.MoveCorner(_dragCorner, ToPicture(p));
            ViewChanged?.Invoke();
        }
        else if ((_dragVertex >= 0 || _dragVia) && _pressAt is { } grabbed)
        {
            if (Math.Abs(p.X - grabbed.X) > 3 || Math.Abs(p.Y - grabbed.Y) > 3) _dragTo = _hover;
        }
        else if (_manualHandle >= 0 && Manual is { } boxed) boxed.DragHandle(_manualHandle, _hover.Value.X, _hover.Value.Y);
        else if (_manualPoint is { } dragged && Manual is { } placed) placed.DragPoint(dragged.Curve, dragged.Index, _hover.Value.X, _hover.Value.Y);
        else if (_pressAt is { } from && _dragEnd is null
                 && (_vm is { BoxTool: PlotBoxTool.ReadOnlyHere or PlotBoxTool.IgnoreHere }
                          or { Scale.Tool: ImageMeasureTool.None, CanvasPick: false, IsLayoutPicture: true }
                     || (Manual is { } drawing && (_vm!.Plots.ManualTool == ManualTool.Box || !drawing.HasBox)))
                 && (Math.Abs(p.X - from.X) > 4 || Math.Abs(p.Y - from.Y) > 4))
            _scoping = true;
        _lastPointer = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        e.Pointer.Capture(null);
        if (_panning)
        {
            _panning = false;
            // A right-click on a fixpoint, not a pan, removes it (R-pdg4-5).
            if (_rightPress is { } doomed && Manual is { } m0 && Math.Abs(p.X - _pressPosition.X) <= 4 && Math.Abs(p.Y - _pressPosition.Y) <= 4)
            {
                m0.RemovePoint(doomed.Curve, doomed.Index);
                InvalidateVisual();
            }
            _rightPress = null;
            return;
        }
        if (_pressAt is not { } from || _vm is not { } vm) return;
        _pressAt = null;
        if (_dragCorner >= 0) { _dragCorner = -1; return; }
        // A corner or a hand via dragged: one undo step, made on release (R-dfr30-1-3). A corner only clicked stays chosen.
        if (_dragVertex >= 0 || _dragVia)
        {
            int index = vm.SelectedCorrection, vertex = _dragVertex;
            bool via = _dragVia;
            var to = _dragTo;
            (_dragVertex, _dragVia, _dragTo) = (-1, false, null);
            if (to is { } dropped)
            {
                if (via) vm.MoveCorrection(index, dropped);
                else vm.MoveCorrectionVertex(index, vertex, dropped);
                InvalidateVisual();
                return;
            }
            if (!via) { InvalidateVisual(); return; }
        }
        if (Manual is { } manual)
        {
            if (_manualHandle >= 0 || _manualPoint is not null)
            {
                _manualHandle = -1;
                _manualPoint = null;
                manual.EndEdit();
                InvalidateVisual();
                return;
            }
            var a = ToPicture(from);
            var b = ToPicture(p);
            if (_scoping)
            {
                _scoping = false;
                manual.DrawBox(a.X, a.Y, b.X, b.Y);
                if (vm.Plots.ManualTool == ManualTool.Box) vm.Plots.ManualTool = manual.SelectedCurve is null ? ManualTool.None : ManualTool.Points;
            }
            else if (vm.Plots.ManualTool == ManualTool.Find) vm.Plots.FindAt(b.X, b.Y);
            else manual.SelectedPoint = null;
            InvalidateVisual();
            return;
        }
        if (_dragEnd is { } end)
        {
            _dragEnd = null;
            if (Math.Abs(p.X - from.X) > 4 || Math.Abs(p.Y - from.Y) > 4) { vm.JoinEnd(end, ToPicture(p)); InvalidateVisual(); return; }
        }

        if (_scoping)
        {
            _scoping = false;
            var a = ToPicture(from);
            var b = ToPicture(p);
            // The Copper and Outline tools take a dragged rectangle; the Via tool a click only (R-dfr29-1-3).
            if (vm.DrawTool is PictureDrawTool.Copper or PictureDrawTool.Outline)
            {
                vm.DrawPoints.Clear();
                vm.DrawRectangle(a, b);
                InvalidateVisual();
                return;
            }
            if (vm.DrawTool == PictureDrawTool.Crop) { vm.CropTo(a, b); InvalidateVisual(); return; }
            // On a layout picture only the Crop tool crops: a drag with no tool armed is no edit (owner, 2026-10-28).
            if (vm.DrawTool is PictureDrawTool.Via or PictureDrawTool.PaintFill || vm.IsLayoutPicture) { InvalidateVisual(); return; }
            if (vm.BoxTool != PlotBoxTool.None)
            {
                // Read only here crops and Ignore here excludes (R-ptu6-2, R-ptu6-3); either way the tool is done.
                vm.CommitBox(new PixelRect(a.X, a.Y, b.X, b.Y));
                InvalidateVisual();
                return;
            }
            if (vm.Raster is { } r)
                vm.ScopeRect = new PixelRect(Math.Clamp(Math.Min(a.X, b.X), 0, r.Width), Math.Clamp(Math.Min(a.Y, b.Y), 0, r.Height),
                                             Math.Clamp(Math.Max(a.X, b.X), 0, r.Width), Math.Clamp(Math.Max(a.Y, b.Y), 0, r.Height));
            InvalidateVisual();
            return;
        }

        var at = ToPicture(p);
        // A pinned point clicked is selected; with the Point tool armed any other click pins one (R-pdg3-2).
        if (vm.IsPlotPicture && vm.Plots.ProbePointAt(at.X, at.Y, 6 / _zoom) is { } pin)
        {
            vm.Plots.SelectedProbePoint = pin;
            vm.Plots.SelectedIgnoreBox = null;
            InvalidateVisual();
            return;
        }
        if (vm.BoxTool == PlotBoxTool.Point)
        {
            vm.PinAt(at, PlotSessionViewModel.SnapScreenPx / _zoom);
            InvalidateVisual();
            return;
        }
        if (vm.BoxTool != PlotBoxTool.None) return;   // a click is not a box: the tool stays armed
        if (vm.IsPlotPicture) vm.Plots.SelectedProbePoint = null;
        if (vm.CanvasPick) { vm.PickAt(at); return; }
        if (vm.IsPlotPicture)
        {
            vm.Plots.SelectedIgnoreBox = vm.Plots.IgnoreBoxAt(at.X, at.Y);
            InvalidateVisual();
            if (vm.Plots.SelectedIgnoreBox is not null) { vm.SelectedItem = null; return; }
        }
        if (vm.Scale.Click(at)) { ViewChanged?.Invoke(); InvalidateVisual(); return; }
        if (vm.DrawTool == PictureDrawTool.Via) { vm.PlaceVia(at); InvalidateVisual(); return; }
        if (vm.DrawTool == PictureDrawTool.PaintFill) { vm.PaintAt(at); InvalidateVisual(); return; }
        if (vm.DrawTool is PictureDrawTool.Copper or PictureDrawTool.Outline) { vm.AddDrawPoint(at); InvalidateVisual(); return; }
        var picked = vm.Overlay.HitTest(at.X, at.Y, 4 / _zoom, vm.Shows);
        if (picked is { Class: ImageOverlayClass.Crossing }) { vm.ToggleCrossing(picked); return; }
        // A correction clicked is selected with its row (R-dfr30-1-3); a via on a hole keeps the hole's picker too.
        _selectedVertex = -1;
        if (vm.IsLayoutPicture && vm.CorrectionAt(at, 4 / _zoom) is >= 0 and var fix)
        {
            vm.SelectedCorrection = fix;
            vm.SelectedItem = vm.Corrections[fix].IsPoint ? picked : null;
            InvalidateVisual();
            return;
        }
        if (vm.IsLayoutPicture) vm.SelectedCorrection = -1;
        vm.SelectedItem = picked;
    }
}
