// The picture dialog's plot mode, read by hand — brief-pdg-4-digitise-by-hand.md R-pdg4-5.
//
// IT DECIDES NOTHING EITHER. Every control is a field of the records the CLI's by-hand flags fill — ManualPlot (the box,
// the scales, the grid) and ManualCurve (a curve's name, fixpoints, axis and what it is) — and Plot() / Curves() build them
// for PlotReading.Read through the same runner as an automatic reading. Find curve is ManualCurves.Follow, the CLI's
// --follow.
//
// ONE PLOT A PICTURE. The box is held as a centre, a size and an angle (the dialog only makes turned rectangles; the record
// takes any quad), in picture pixels, and every fixpoint in picture pixels as placed — the reading carries them into the
// box's own space.
//
// THE STEPS ENABLE IN ORDER: a box, then scales that calibrate it, then a curve, then its points; sampling and Create follow
// the points. Nobody has to find calibration in the help.
//
// UNDO is per picture: the box, the curves and every curve's fixpoints as they were before the last change — a curve added or
// removed is one step, and a drag is one step however far it went.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Layout.Recognition;
using AxisScale = CircuitRF.Design.Imaging.Plots.AxisScale;

namespace CircuitRF.Ui.Recognition;

/// <summary>What the canvas does with a press in Manual mode.</summary>
public enum ManualTool { None, Box, Points, Find }

/// <summary>The box as the dialog holds it: a centre, a size and an angle, picture pixels and degrees (counter-clockwise as
/// seen).</summary>
public readonly record struct ManualBoxGeometry(double Cx, double Cy, double W, double H, double AngleDeg)
{
    public ManualQuad Quad => ManualQuad.Rectangle(Cx - W / 2, Cy - H / 2, Cx + W / 2, Cy + H / 2, AngleDeg);

    /// <summary>The box's right and down directions on the picture.</summary>
    public (double Rx, double Ry, double Dx, double Dy) Axes
    {
        get
        {
            double a = AngleDeg * Math.PI / 180;
            return (Math.Cos(a), -Math.Sin(a), Math.Sin(a), Math.Cos(a));
        }
    }

    /// <summary>A picture point in the box's own frame: along its right and its down direction from its centre.</summary>
    public (double U, double V) Local(double x, double y)
    {
        var (rx, ry, dx, dy) = Axes;
        double px = x - Cx, py = y - Cy;
        return (px * rx + py * ry, px * dx + py * dy);
    }

    public (double X, double Y) Picture(double u, double v)
    {
        var (rx, ry, dx, dy) = Axes;
        return (Cx + u * rx + v * dx, Cy + u * ry + v * dy);
    }
}

/// <summary>One curve of the Curves step: its row and its fixpoints.</summary>
public sealed partial class ManualCurveViewModel : ObservableObject
{
    private readonly Action<ManualCurveViewModel> _changed;
    private readonly Action<ManualCurveViewModel> _remove;

    public ManualCurveViewModel(string name, int rgb, Action<ManualCurveViewModel> changed, Action<ManualCurveViewModel> remove)
    {
        _name = name;
        _rgb = rgb;
        _changed = changed;
        _remove = remove;
    }

    [ObservableProperty] private string _name;

    /// <summary>Its colour, 0xRRGGBB: the swatch, its points and its preview — a curve found takes the curve's own.</summary>
    [ObservableProperty] private int _rgb;

    public IBrush Swatch => new ImmutableSolidColorBrush(Color.FromRgb((byte)(Rgb >> 16), (byte)(Rgb >> 8), (byte)Rgb));

    /// <summary>The fixpoints, picture pixels, in the order placed. Changed through <see cref="ManualPlotViewModel"/>, which
    /// remembers the undo step.</summary>
    public List<(double X, double Y)> Fixpoints { get; } = [];

    public string PointCount => $"{Fixpoints.Count.ToString(CultureInfo.InvariantCulture)} point{(Fixpoints.Count == 1 ? "" : "s")}";

    /// <summary>The Is combo: Data, then every entry (R-pdg4-4) — a two-port's four first, in the order a datasheet plots them.</summary>
    public static readonly IReadOnlyList<string> IsChoices =
        ["Data", "S11", "S21", "S12", "S22", .. PlotTraceRowViewModel.EntriesFor(TouchstoneAssembly.MaxPorts).Skip(2)
                                                  .Except(["S11", "S21", "S12", "S22"])];

    [ObservableProperty] private int _isIndex;

    [ObservableProperty] private int _quantityIndex;

    /// <summary>0 Left, 1 Right — shown with two y scales only.</summary>
    [ObservableProperty] private int _axisIndex;

    public bool IsData => IsIndex <= 0;
    public bool IsEntry => !IsData;
    public PlotSide Side => AxisIndex == 1 ? PlotSide.Right : PlotSide.Left;

    /// <summary>What it is, as <c>--curve …,is=</c> spells it: none for Data.</summary>
    public PlotAssignment Assignment => IsData ? PlotAssignment.None
        : PlotQuantity.ParseAssignment($"{IsChoices[IsIndex]}:{PlotTraceRowViewModel.Quantities[Math.Clamp(QuantityIndex, 0, PlotTraceRowViewModel.Quantities.Count - 1)]}")
          ?? PlotAssignment.None;

    partial void OnNameChanged(string value) => _changed(this);
    partial void OnRgbChanged(int value) => OnPropertyChanged(nameof(Swatch));
    partial void OnIsIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsData));
        OnPropertyChanged(nameof(IsEntry));
        _changed(this);
    }
    partial void OnQuantityIndexChanged(int value) => _changed(this);
    partial void OnAxisIndexChanged(int value) => _changed(this);

    /// <summary>The fixpoints changed: the count is said again.</summary>
    public void Touch() => OnPropertyChanged(nameof(PointCount));

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One picture's plot read by hand: the box, its scales, its curves and how they are sampled.</summary>
public sealed partial class ManualPlotViewModel : ObservableObject
{
    /// <summary>The colours new curves take, in turn.</summary>
    private static readonly int[] Palette = [0x1F77B4, 0xD62728, 0x2CA02C, 0x9467BD, 0xFF7F0E, 0x17BECF];

    private readonly Action _changed;
    private bool _quiet;

    public ManualPlotViewModel(int picture, Action changed)
    {
        Picture = picture;
        _changed = changed;
    }

    /// <summary>The session picture it is on, 0-based.</summary>
    public int Picture { get; set; }

    // ── 1. the plot area ──

    [ObservableProperty] private ManualBoxGeometry? _box;

    public bool HasBox => Box is not null;

    /// <summary>The Angle field, degrees.</summary>
    public string AngleText
    {
        get => Box is { } b ? b.AngleDeg.ToString("0.##", CultureInfo.InvariantCulture) : "";
        set
        {
            if (Box is not { } b || !double.TryParse((value ?? "").Trim().TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                || !double.IsFinite(a) || Math.Abs(a - b.AngleDeg) < 1e-9) return;
            Remember();
            Box = b with { AngleDeg = Math.Clamp(a, -45, 45) };
            Edited();
        }
    }

    /// <summary>A box dragged out on the picture (two opposite corners), level.</summary>
    public void DrawBox(double x0, double y0, double x1, double y1)
    {
        if (Math.Abs(x1 - x0) < 4 || Math.Abs(y1 - y0) < 4) return;
        Remember();
        Box = new ManualBoxGeometry((x0 + x1) / 2, (y0 + y1) / 2, Math.Abs(x1 - x0), Math.Abs(y1 - y0), Box?.AngleDeg ?? 0);
        Suggested = false;
        Edited();
    }

    /// <summary>The box placed where something else said a plot is — a starting suggestion, never remembered as an edit.</summary>
    public void SuggestBox(double left, double top, double right, double bottom)
    {
        if (Box is not null) return;
        Box = new ManualBoxGeometry((left + right) / 2, (top + bottom) / 2, right - left, bottom - top, 0);
        Suggested = true;
    }

    /// <summary>The box was pre-placed on a frame the automatic finder found — a suggestion only.</summary>
    [ObservableProperty] private bool _suggested;

    /// <summary>The box's handles, picture pixels: corners 0–3 (bottom-left, bottom-right, top-right, top-left), edge
    /// midpoints 4–7 (bottom, right, top, left), and the rotation handle 8 — <paramref name="lift"/> above the top edge.</summary>
    public IReadOnlyList<(double X, double Y)> Handles(double lift)
    {
        if (Box is not { } b) return [];
        double u = b.W / 2, v = b.H / 2;
        return [b.Picture(-u, v), b.Picture(u, v), b.Picture(u, -v), b.Picture(-u, -v),
                b.Picture(0, v), b.Picture(u, 0), b.Picture(0, -v), b.Picture(-u, 0), b.Picture(0, -v - lift)];
    }

    /// <summary>A handle dragged to a picture point: a corner moves with its opposite corner fixed, an edge with its
    /// opposite edge fixed, the rotation handle turns the box about its centre. Live — <see cref="EndEdit"/> commits it.</summary>
    public void DragHandle(int handle, double x, double y)
    {
        if (Box is not { } b) return;
        if (handle == 8)
        {
            double a = Math.Atan2(-(x - b.Cx), -(y - b.Cy)) * 180 / Math.PI;
            Box = b with { AngleDeg = Math.Clamp(Math.Round(a, 2), -45, 45) };
            OnPropertyChanged(nameof(AngleText));
            return;
        }
        var (lu, lv) = b.Local(x, y);
        double u0 = -b.W / 2, u1 = b.W / 2, v0 = -b.H / 2, v1 = b.H / 2;
        switch (handle)
        {
            case 0: u0 = lu; v1 = lv; break;
            case 1: u1 = lu; v1 = lv; break;
            case 2: u1 = lu; v0 = lv; break;
            case 3: u0 = lu; v0 = lv; break;
            case 4: v1 = lv; break;
            case 5: u1 = lu; break;
            case 6: v0 = lv; break;
            case 7: u0 = lu; break;
        }
        if (Math.Abs(u1 - u0) < 8 || Math.Abs(v1 - v0) < 8) return;
        var (cx, cy) = b.Picture((u0 + u1) / 2, (v0 + v1) / 2);
        Box = b with { Cx = cx, Cy = cy, W = Math.Abs(u1 - u0), H = Math.Abs(v1 - v0) };
    }

    // ── 2. the scales ──

    /// <summary>The plot has a second y scale on the right (Two y scales).</summary>
    [ObservableProperty] private bool _twoScales;

    public bool OneScale { get => !TwoScales; set => TwoScales = !value; }

    [ObservableProperty] private string _xLowText = "";
    [ObservableProperty] private string _xHighText = "";
    [ObservableProperty] private bool _xLog;
    [ObservableProperty] private string _yLowText = "";
    [ObservableProperty] private string _yHighText = "";
    [ObservableProperty] private bool _yLog;
    [ObservableProperty] private string _y2LowText = "";
    [ObservableProperty] private string _y2HighText = "";
    [ObservableProperty] private bool _y2Log;

    /// <summary>An x value: a frequency with its unit (hertz, and that unit's multiplier), or a plain number.</summary>
    internal static (double Value, double Unit, bool Frequency)? XValue(string text)
    {
        string t = (text ?? "").Trim().Replace(" ", "", StringComparison.Ordinal).Replace('−', '-');
        if (t.Length == 0) return null;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return double.IsFinite(d) ? (d, 1, false) : null;
        if (RecognitionSweep.Parse(t) is not { } f) return null;
        return (f.Hz, f.Unit switch { "kHz" => 1e3, "MHz" => 1e6, "GHz" => 1e9, "THz" => 1e12, _ => 1 }, true);
    }

    /// <summary>A y value: a number, and whatever follows it as its unit label (<c>-40 dB</c>).</summary>
    internal static (double Value, string Unit)? YValue(string text)
    {
        string t = (text ?? "").Trim().Replace('−', '-');
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d{1,3})?");
        if (!m.Success || !double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)) return null;
        return (v, t[m.Length..].Trim());
    }

    public bool XLowInvalid => XLowText.Trim().Length > 0 && XValue(XLowText) is null;
    public bool XHighInvalid => XHighText.Trim().Length > 0 && XValue(XHighText) is null;
    public bool YLowInvalid => YLowText.Trim().Length > 0 && YValue(YLowText) is null;
    public bool YHighInvalid => YHighText.Trim().Length > 0 && YValue(YHighText) is null;
    public bool Y2LowInvalid => Y2LowText.Trim().Length > 0 && YValue(Y2LowText) is null;
    public bool Y2HighInvalid => Y2HighText.Trim().Length > 0 && YValue(Y2HighText) is null;

    /// <summary>The plot these controls spell, or null without a box. Scales not yet typed are NaN: the plot is read
    /// uncalibrated, and <see cref="ScalesProblem"/> says why.</summary>
    public ManualPlot? Plot()
    {
        if (Box is not { } b) return null;
        var xl = XValue(XLowText);
        var xh = XValue(XHighText);
        bool frequency = (xh ?? xl)?.Frequency ?? false;
        double unit = (xh ?? xl)?.Unit ?? 1;
        ManualAxis Y(string lo, string hi, bool log) =>
            new(YValue(lo)?.Value ?? double.NaN, YValue(hi)?.Value ?? double.NaN, log ? AxisScale.Log : AxisScale.Linear);
        string Unit(string lo, string hi) => YValue(hi) is { Unit.Length: > 0 } h ? h.Unit : YValue(lo)?.Unit ?? "";
        return new ManualPlot(Picture, b.Quad,
                              new ManualAxis(xl?.Value ?? double.NaN, xh?.Value ?? double.NaN, XLog ? AxisScale.Log : AxisScale.Linear),
                              Y(YLowText, YHighText, YLog), TwoScales ? Y(Y2LowText, Y2HighText, Y2Log) : null, frequency ? unit : 1, frequency)
        {
            Sampling = Sampling() ?? ManualSampleSpec.Default,
            YUnit = Unit(YLowText, YHighText),
            Y2Unit = TwoScales ? Unit(Y2LowText, Y2HighText) : "",
        };
    }

    /// <summary>Why the scales do not calibrate the box yet, or null when they do.</summary>
    public string? ScalesProblem
    {
        get
        {
            if (!HasBox) return "Draw the plot area first.";
            var xl = XValue(XLowText);
            var xh = XValue(XHighText);
            if (xl is null || xh is null) return "Type x low and x high: the values at the box's left and right edges (1 GHz, 5 GHz).";
            if (xl.Value.Frequency != xh.Value.Frequency) return "x low and x high must both be frequencies with units, or both plain numbers.";
            if (YValue(YLowText) is null || YValue(YHighText) is null) return "Type y low and y high: the values at the box's bottom and top edges.";
            if (TwoScales && (YValue(Y2LowText) is null || YValue(Y2HighText) is null))
                return "Type the right y low and high: the right-hand scale's values at the bottom and top edges.";
            return Plot()?.Refusal is { } why ? char.ToUpperInvariant(why[0]) + why[1..] + "." : null;
        }
    }

    // ── 3. the curves ──

    public ObservableCollection<ManualCurveViewModel> Curves { get; } = [];

    [ObservableProperty] private ManualCurveViewModel? _selectedCurve;

    /// <summary>The fixpoint clicked on the canvas: Delete removes it.</summary>
    [ObservableProperty] private (ManualCurveViewModel Curve, int Index)? _selectedPoint;

    public string CurveCount => $"{Curves.Count.ToString(CultureInfo.InvariantCulture)} curve{(Curves.Count == 1 ? "" : "s")}";

    [RelayCommand]
    private void AddCurve()
    {
        int n = Curves.Count + 1;
        string name;
        do name = $"Curve {n++.ToString(CultureInfo.InvariantCulture)}"; while (Curves.Any(c => c.Name == name));
        // The first colour of the palette no curve here is drawn in already — a curve found takes the picture's own.
        int rgb = Palette.FirstOrDefault(p => Curves.All(c => PlotInk.Distance(c.Rgb, p) >= 72), Palette[Curves.Count % Palette.Length]);
        Remember();
        var curve = new ManualCurveViewModel(name, rgb, _ => Edited(), RemoveCurve);
        Curves.Add(curve);
        SelectedCurve = curve;
        OnPropertyChanged(nameof(CurveCount));
        Refresh();
    }

    private void RemoveCurve(ManualCurveViewModel curve)
    {
        int i = Curves.IndexOf(curve);
        if (i < 0) return;
        Remember();   // a curve removed with its points is one undo step
        Curves.RemoveAt(i);
        if (ReferenceEquals(SelectedCurve, curve)) SelectedCurve = Curves.Count == 0 ? null : Curves[Math.Min(i, Curves.Count - 1)];
        if (SelectedPoint?.Curve == curve) SelectedPoint = null;
        OnPropertyChanged(nameof(CurveCount));
        Edited();
    }

    /// <summary>The curves these rows spell, on session plot <paramref name="plot"/>.</summary>
    public IEnumerable<ManualCurve> CurvesOn(int plot) =>
        Curves.Select(c => new ManualCurve(c.Name.Trim().Length == 0 ? "curve" : c.Name.Trim(), plot, [.. c.Fixpoints],
                                           TwoScales ? c.Side : PlotSide.Left, c.Assignment, c.Rgb));

    // ── 4. the points ──

    /// <summary>A fixpoint placed on the selected curve (a double-click).</summary>
    public void AddPoint(double x, double y)
    {
        if (SelectedCurve is not { } c) return;
        Remember();
        c.Fixpoints.Add((x, y));
        SelectedPoint = (c, c.Fixpoints.Count - 1);
        c.Touch();
        Edited();
    }

    /// <summary>A fixpoint dragged — live; <see cref="EndEdit"/> commits it.</summary>
    public void DragPoint(ManualCurveViewModel curve, int index, double x, double y)
    {
        if (index < 0 || index >= curve.Fixpoints.Count) return;
        curve.Fixpoints[index] = (x, y);
    }

    public void RemovePoint(ManualCurveViewModel curve, int index)
    {
        if (index < 0 || index >= curve.Fixpoints.Count) return;
        Remember();
        curve.Fixpoints.RemoveAt(index);
        if (SelectedPoint is { } p && p.Curve == curve) SelectedPoint = null;
        curve.Touch();
        Edited();
    }

    /// <summary>Delete: the selected fixpoint removed. False when none is selected.</summary>
    public bool RemoveSelectedPoint()
    {
        if (SelectedPoint is not { } p) return false;
        RemovePoint(p.Curve, p.Index);
        return true;
    }

    /// <summary>The fixpoint nearest a picture point within <paramref name="reach"/> picture pixels — the selected curve's
    /// first — or null.</summary>
    public (ManualCurveViewModel Curve, int Index)? PointAt(double x, double y, double reach)
    {
        (ManualCurveViewModel, int)? best = null;
        double bd = reach * reach;
        foreach (var c in SelectedCurve is { } s ? Curves.OrderBy(k => k == s ? 0 : 1) : Curves.AsEnumerable())
            for (int i = 0; i < c.Fixpoints.Count; i++)
            {
                double d = (c.Fixpoints[i].X - x) * (c.Fixpoints[i].X - x) + (c.Fixpoints[i].Y - y) * (c.Fixpoints[i].Y - y);
                if (d < bd - 1e-9) { bd = d; best = (c, i); }
            }
        return best;
    }

    /// <summary>Find curve: the selected curve's fixpoints replaced by what <see cref="ManualCurves.Follow(RasterImage, ManualPlot, ValueTuple{double, double}, int?, double)"/>
    /// followed from a click; null when done, else why nothing was.</summary>
    public string? FindCurve(RasterImage picture, double x, double y)
    {
        if (SelectedCurve is not { } c) return "Add a curve first: Find curve fills the selected curve's points.";
        if (Plot() is not { Refusal: null } plot) return ScalesProblem ?? "Set the scales first.";
        var followed = ManualCurves.Follow(picture, plot, (x, y));
        if (followed.Refusal is { } why) return char.ToUpperInvariant(why[0]) + why[1..] + ".";
        Remember();
        c.Fixpoints.Clear();
        c.Fixpoints.AddRange(followed.Fixpoints);
        c.Rgb = followed.Rgb;
        SelectedPoint = null;
        c.Touch();
        Edited();
        return null;
    }

    // ── 5. sampling ──

    [ObservableProperty] private string _sampleStartText = "";
    [ObservableProperty] private string _sampleStopText = "";
    [ObservableProperty] private string _samplePointsText = "";
    [ObservableProperty] private string _sampleStepText = "";

    /// <summary>Log-spaced — always so on a log x axis.</summary>
    [ObservableProperty] private bool _sampleLog;

    /// <summary>0 Linear, 1 Smooth.</summary>
    [ObservableProperty] private int _methodIndex;

    public static IReadOnlyList<string> Methods { get; } = ["Linear", "Smooth"];

    /// <summary>The grid these fields spell; null while one of them is not legible.</summary>
    public ManualSampleSpec? Sampling()
    {
        double? X(string t) => t.Trim().Length == 0 ? null : XValue(t)?.Value;
        if ((SampleStartText.Trim().Length > 0 && X(SampleStartText) is null) || (SampleStopText.Trim().Length > 0 && X(SampleStopText) is null))
            return null;
        int points = ManualSampleSpec.DefaultPoints;
        if (SamplePointsText.Trim().Length > 0
            && !(int.TryParse(SamplePointsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out points) && points >= 2)) return null;
        double? step = null;
        if (SampleStepText.Trim().Length > 0)
        {
            if (X(SampleStepText) is not { } s || !(s > 0)) return null;
            step = s;
        }
        return new ManualSampleSpec(X(SampleStartText), X(SampleStopText), points, step, SampleLog || XLog,
                                    MethodIndex == 1 ? ManualSampleMethod.Smooth : ManualSampleMethod.Linear);
    }

    public bool SamplingInvalid => Sampling() is null;

    // ── the steps ──

    public bool ScalesDone => HasBox && ScalesProblem is null;
    public bool HasCurves => Curves.Count > 0;
    public bool PointsDone => ScalesDone && Curves.Any(c => c.Fixpoints.Count >= 2);

    public bool StepScalesEnabled => HasBox;
    public bool StepCurvesEnabled => ScalesDone;
    public bool StepPointsEnabled => ScalesDone && HasCurves;
    public bool StepSampleEnabled => PointsDone;
    public bool StepCreateEnabled => PointsDone;

    /// <summary>The step to do next, 1–6: what the column highlights.</summary>
    public int NextStep => !HasBox ? 1 : !ScalesDone ? 2 : !HasCurves ? 3 : !PointsDone ? 4 : 6;

    // ── undo ──

    private sealed record Snapshot(ManualBoxGeometry? Box, List<(ManualCurveViewModel Curve, List<(double, double)> Points, int Rgb)> Curves,
                                   List<ManualCurveViewModel> Order);

    private readonly Stack<Snapshot> _undo = new();
    private Snapshot? _editStart;

    public bool CanUndo => _undo.Count > 0;

    private Snapshot Take() => new(Box, [.. Curves.Select(c => (c, new List<(double, double)>(c.Fixpoints), c.Rgb))], [.. Curves]);

    private void Remember()
    {
        if (_quiet) return;
        _undo.Push(Take());
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>A drag begins: what it changes is one undo step, committed by <see cref="EndEdit"/>.</summary>
    public void BeginEdit() => _editStart = Take();

    public void EndEdit()
    {
        if (_editStart is not { } start) return;
        _editStart = null;
        bool same = Equals(start.Box, Box) && start.Curves.All(s => s.Curve.Fixpoints.SequenceEqual(s.Points));
        if (same) return;
        _undo.Push(start);
        OnPropertyChanged(nameof(CanUndo));
        Suggested = false;
        foreach (var c in Curves) c.Touch();
        Edited();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var back = _undo.Pop();
        _quiet = true;
        try
        {
            Box = back.Box;
            Curves.Clear();
            foreach (var c in back.Order) Curves.Add(c);
            foreach (var (curve, points, rgb) in back.Curves)
            {
                curve.Fixpoints.Clear();
                curve.Fixpoints.AddRange(points);
                curve.Rgb = rgb;
                curve.Touch();
            }
            if (SelectedCurve is { } s && !Curves.Contains(s)) SelectedCurve = Curves.FirstOrDefault();
            SelectedPoint = null;
        }
        finally { _quiet = false; }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CurveCount));
        OnPropertyChanged(nameof(AngleText));
        Edited();
    }

    // ── notifications ──

    private void Edited()
    {
        if (_quiet) return;
        Refresh();
        _changed();
    }

    /// <summary>Every derived flag said again.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(HasBox));
        OnPropertyChanged(nameof(ScalesDone));
        OnPropertyChanged(nameof(ScalesProblem));
        OnPropertyChanged(nameof(HasCurves));
        OnPropertyChanged(nameof(PointsDone));
        OnPropertyChanged(nameof(StepScalesEnabled));
        OnPropertyChanged(nameof(StepCurvesEnabled));
        OnPropertyChanged(nameof(StepPointsEnabled));
        OnPropertyChanged(nameof(StepSampleEnabled));
        OnPropertyChanged(nameof(StepCreateEnabled));
        OnPropertyChanged(nameof(NextStep));
        OnPropertyChanged(nameof(SamplingInvalid));
    }

    partial void OnBoxChanged(ManualBoxGeometry? value)
    {
        OnPropertyChanged(nameof(HasBox));
        OnPropertyChanged(nameof(AngleText));
    }

    partial void OnTwoScalesChanged(bool value) { OnPropertyChanged(nameof(OneScale)); Edited(); }
    partial void OnXLowTextChanged(string value) { OnPropertyChanged(nameof(XLowInvalid)); Edited(); }
    partial void OnXHighTextChanged(string value) { OnPropertyChanged(nameof(XHighInvalid)); Edited(); }
    partial void OnXLogChanged(bool value) => Edited();
    partial void OnYLowTextChanged(string value) { OnPropertyChanged(nameof(YLowInvalid)); Edited(); }
    partial void OnYHighTextChanged(string value) { OnPropertyChanged(nameof(YHighInvalid)); Edited(); }
    partial void OnYLogChanged(bool value) => Edited();
    partial void OnY2LowTextChanged(string value) { OnPropertyChanged(nameof(Y2LowInvalid)); Edited(); }
    partial void OnY2HighTextChanged(string value) { OnPropertyChanged(nameof(Y2HighInvalid)); Edited(); }
    partial void OnY2LogChanged(bool value) => Edited();
    partial void OnSampleStartTextChanged(string value) => Edited();
    partial void OnSampleStopTextChanged(string value) => Edited();
    partial void OnSamplePointsTextChanged(string value) => Edited();
    partial void OnSampleStepTextChanged(string value) => Edited();
    partial void OnSampleLogChanged(bool value) => Edited();
    partial void OnMethodIndexChanged(int value) => Edited();

    /// <summary>The value typed at each edge, for the canvas to write beside it: x low, x high, y low, y high, right y low,
    /// right y high (empty where nothing is typed).</summary>
    public (string XLow, string XHigh, string YLow, string YHigh, string Y2Low, string Y2High) EdgeTexts =>
        (XLowText.Trim(), XHighText.Trim(), YLowText.Trim(), YHighText.Trim(), TwoScales ? Y2LowText.Trim() : "", TwoScales ? Y2HighText.Trim() : "");
}
