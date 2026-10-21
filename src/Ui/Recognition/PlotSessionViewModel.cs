// The picture dialog's plot mode — brief-img-14-sparameter-plots-to-touchstone.md R-im14-2, R-im14-3, R-im14-8,
// R-im14-9, R-im14-12.
//
// IT DECIDES NOTHING, as the rest of the dialog decides nothing (R-im14-15). Every row is a field of PlotReadingOptions
// or TouchstoneAssemblyOptions — the records the CLI's flags fill — and the dialog hands them to PlotReading.Read and
// TouchstoneAssembly.Build through IImageRecognitionRunner. A combo here is the --trace, --fill or --phase a command line
// would have typed; a typed axis row is --x-axis in the same spelling.
//
// SEVERAL PICTURES ARE ONE SESSION. The pictures list holds every picture read; the first is the dialog's own source. A
// picture dropped, pasted or added while the dialog is in plot mode joins the session rather than replacing it.
//
// THE USER'S CHOICES OUTLIVE A RE-READ: an assignment is held by trace name (T3, the CLI's selector), a fill or phase rule
// by entry, an exclusion and an Ignore here box by plot number. A re-read rebuilds every row from what came back and lays the choices over it.
//
// A FIT (brief-pdg-2-curve-fit.md R-pdg2-4) is held by trace name too, as the --fit a command line would type, and set on
// every piece fitted together with the one edited — the pieces TraceJoin joins share one fit. What a fit made comes back
// with the Touchstone (TouchstoneAssemblyResult.Fits) and is shown under its row and drawn over its trace.
//
// READ: AUTOMATIC | MANUAL (brief-pdg-4-digitise-by-hand.md R-pdg4-5). Manual reads every picture by hand: the session's
// options carry each picture's ManualPlotViewModel as a ManualPlot and its curves as ManualCurves, and the automatic rows
// above are not shown. Make: CSV hides every Touchstone control in either mode (R-pdg4-6); each axis row in Automatic gains a
// Log toggle, the CLI's --x-log (R-pdg4-7).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic.Recognition;
using RfCore;
using AxisScale = CircuitRF.Design.Imaging.Plots.AxisScale;
using FillRule = CircuitRF.Design.Imaging.Plots.FillRule;
using PlotType = CircuitRF.Design.Imaging.Plots.PlotType;

namespace CircuitRF.Ui.Recognition;

/// <summary>One picture of the session (R-im14-12's source list).</summary>
public sealed partial class PlotPictureRowViewModel(ImageSource source, Action<PlotPictureRowViewModel> remove, Action<PlotPictureRowViewModel> show)
    : ObservableObject
{
    public ImageSource Source { get; } = source;

    /// <summary>The picture as given, when this one is it cropped to a box (R-ptu6-2): what Restore whole picture brings
    /// back. Null for a picture not cropped.</summary>
    public ImageSource? Original { get; init; }

    [ObservableProperty] private int _number;

    /// <summary>The first picture is the dialog's own source; it is replaced, never removed.</summary>
    public bool Removable => Number > 1;

    public string Name => Source.Origin == ImageOrigin.Bytes && Source.OriginalName == ImageSource.PastedName ? "Pasted picture" : Source.OriginalName;

    public string Label => $"{Number.ToString(CultureInfo.InvariantCulture)}. {Name}";

    partial void OnNumberChanged(int value)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Removable));
    }

    [RelayCommand] private void Remove() => remove(this);

    [RelayCommand] private void Show() => show(this);
}

/// <summary>One plot of the session: whether it is read, and the axis rows it waits on (R-im14-2, R-im14-3).</summary>
public sealed partial class PlotFrameRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _loading = true;

    public PlotFrameRowViewModel(PlotRead plot, bool include, string xText, string yText, Action changed, string y2Text = "",
                                 bool y2Wanted = false, Func<PlotAxis, bool?>? log = null, Action<PlotAxis, bool>? logChanged = null)
    {
        _changed = changed;
        _logChanged = logChanged;
        // A Log toggle shows what the user said, else the scale the labels fit (R-pdg4-7).
        bool Log(PlotAxis a, AxisCalibration? c) => log?.Invoke(a) ?? c?.Scale == AxisScale.Log;
        _xLog = Log(PlotAxis.X, plot.Rect?.X);
        _yLog = Log(PlotAxis.Y, plot.Rect?.Y);
        _y2Log = Log(PlotAxis.Y2, plot.Rect?.Y2);
        Number = plot.Number;
        Picture = plot.Picture + 1;
        Kind = PlotKind.Name(plot.Type);
        Calibration = plot.Excluded ? "not read" : TouchstoneAssemblyText.Calibration(plot);
        NeedsX = plot.Rect is { X: null };
        // A plot whose only y axis is calibrated on the right does not wait on a left one (R-pdg1-1).
        NeedsY = plot.Rect is { Y: null, Y2: null };
        // The right axis waits: labels were read there but do not fit, or a trace is read against it (R-pdg1-3).
        NeedsY2 = plot.Rect is { YSide: PlotSide.Left, Y2: null } r && (r.Y2Labels.Count > 0 || y2Wanted);
        ShowsX = NeedsX || xText.Length > 0;
        ShowsY = NeedsY || yText.Length > 0;
        ShowsY2 = NeedsY2 || y2Text.Length > 0;
        YLabel = plot.Rect?.YSide == PlotSide.Right ? "y axis (right)" : "y axis";
        _include = include;
        _xAxisText = xText;
        _yAxisText = yText;
        _y2AxisText = y2Text;
        _loading = false;
    }

    public int Number { get; }
    public int Picture { get; }
    public string Kind { get; }
    public string Calibration { get; }
    public string Title => $"Plot {Number.ToString(CultureInfo.InvariantCulture)} · {Kind} · picture {Picture.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The x (y, y2) axis waits for two values: the reading left it uncalibrated.</summary>
    public bool NeedsX { get; }
    public bool NeedsY { get; }
    public bool NeedsY2 { get; }

    /// <summary>The x (y, y2) row is shown: the axis waits, or has values typed or clicked (R-ptu4-2, R-pdg1-3).</summary>
    public bool ShowsX { get; }
    public bool ShowsY { get; }
    public bool ShowsY2 { get; }

    /// <summary>The y row's caption: <c>y axis (right)</c> on a plot whose only y axis is on the right.</summary>
    public string YLabel { get; }

    [ObservableProperty] private bool _include;

    /// <summary><c>120px=1GHz,900px=5GHz</c>: two pixel positions along the axis and their values (<c>--x-axis</c>).</summary>
    [ObservableProperty] private string _xAxisText;
    [ObservableProperty] private string _yAxisText;

    /// <summary>The right-hand second y axis (<c>--y2-axis</c>), in the y row's spelling.</summary>
    [ObservableProperty] private string _y2AxisText;

    /// <summary>The row's text for <paramref name="axis"/>.</summary>
    public string TextOf(PlotAxis axis) => axis switch { PlotAxis.X => XAxisText, PlotAxis.Y => YAxisText, _ => Y2AxisText };

    /// <summary>Sets the row's text for <paramref name="axis"/>; the row tells its plot, which reads again.</summary>
    public void SetText(PlotAxis axis, string text)
    {
        switch (axis)
        {
            case PlotAxis.X: XAxisText = text; break;
            case PlotAxis.Y: YAxisText = text; break;
            default: Y2AxisText = text; break;
        }
    }

    private readonly Action<PlotAxis, bool>? _logChanged;

    /// <summary>The axis is logarithmic (<c>--x-log</c>, <c>--y-log</c>, <c>--y2-log</c>) — R-pdg4-7.</summary>
    [ObservableProperty] private bool _xLog;
    [ObservableProperty] private bool _yLog;
    [ObservableProperty] private bool _y2Log;

    partial void OnXLogChanged(bool value) { if (!_loading) _logChanged?.Invoke(PlotAxis.X, value); }
    partial void OnYLogChanged(bool value) { if (!_loading) _logChanged?.Invoke(PlotAxis.Y, value); }
    partial void OnY2LogChanged(bool value) { if (!_loading) _logChanged?.Invoke(PlotAxis.Y2, value); }

    partial void OnIncludeChanged(bool value) { if (!_loading) _changed(); }
    partial void OnXAxisTextChanged(string value) { if (!_loading) _changed(); }
    partial void OnYAxisTextChanged(string value) { if (!_loading) _changed(); }
    partial void OnY2AxisTextChanged(string value) { if (!_loading) _changed(); }
}

/// <summary>One trace of the traces table (R-im14-12): swatch, plot, entry, quantity, range, points, resolution, evidence.</summary>
public sealed partial class PlotTraceRowViewModel : ObservableObject
{
    /// <summary>Every entry a trace can be: unassigned, ignore, then S11 … S44. <see cref="EntryIndex"/> indexes this.</summary>
    public static readonly IReadOnlyList<string> Entries = EntriesFor(TouchstoneAssembly.MaxPorts);

    /// <summary>The entry combo for an <paramref name="ports"/>-port file: unassigned, ignore, then S11 … Snn (PTU-7).</summary>
    public static IReadOnlyList<string> EntriesFor(int ports) =>
        ["unassigned", "ignore", .. Enumerable.Range(1, ports).SelectMany(i => Enumerable.Range(1, ports).Select(j => $"S{i}{j}"))];

    /// <summary>The quantity combo, in <c>--trace</c>'s spelling.</summary>
    public static readonly IReadOnlyList<string> Quantities = ["db", "mag", "phase", "phase-rad", "re", "im", "complex", "vswr", "rl", "il"];

    /// <summary>The Axis combo (R-pdg1-3): the y axis a trace is read against.</summary>
    public static readonly IReadOnlyList<string> Sides = ["Left", "Right"];

    /// <summary>The Fit combo (R-pdg2-4); its index is <see cref="TraceFitModel"/>'s.</summary>
    public static readonly IReadOnlyList<string> FitModels = ["None", "Linear", "Exponential", "Polynomial", "Spline"];

    /// <summary>A fit as the row's controls hold it: the model's index, the degree and smoothing typed (empty for the default).</summary>
    public readonly record struct FitChoice(int Model, string Degree, string Smoothing)
    {
        /// <summary>The fit it says, or null while the degree or smoothing typed is not one.</summary>
        public TraceFitSpec? Spec
        {
            get
            {
                var model = (TraceFitModel)Math.Clamp(Model, 0, FitModels.Count - 1);
                string degreeText = Degree ?? "", smoothingText = Smoothing ?? "";
                int degree = 3;
                if (model == TraceFitModel.Polynomial && degreeText.Trim().Length > 0
                    && !(int.TryParse(degreeText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out degree) && degree >= 1 && degree <= TraceFitSpec.MaxDegree))
                    return null;
                double? smoothing = null;
                if (model == TraceFitModel.Spline && smoothingText.Trim().Length > 0)
                {
                    if (!double.TryParse(smoothingText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0) || !double.IsFinite(v))
                        return null;
                    smoothing = v;
                }
                return new TraceFitSpec(model, degree, smoothing);
            }
        }
    }

    private readonly Action<PlotTraceRowViewModel> _changed;
    private bool _loading = true;

    public PlotTraceRowViewModel(PlotTraceRead t, PlotRead plot, string startText, string stopText, Action<PlotTraceRowViewModel> changed,
                                 IReadOnlyList<string>? entryChoices = null, FitChoice fitChoice = default, TraceFitResult? fit = null,
                                 bool csv = false)
    {
        _changed = changed;
        EntryChoices = entryChoices ?? Entries;
        Name = t.Name;
        Plot = t.Plot;
        Swatch = new ImmutableSolidColorBrush(Color.FromRgb((byte)(t.Curve.Rgb >> 16), (byte)(t.Curve.Rgb >> 8), (byte)t.Curve.Rgb));
        Style = t.Curve.StyleName;
        IsChart = t.Parametric;
        Points = t.Samples.Count.ToString(CultureInfo.InvariantCulture);
        ResolutionText = $"±{(t.Resolution / 2).ToString("0.####", CultureInfo.InvariantCulture)} {t.ResolutionUnit}".TrimEnd();
        var xs = t.Samples.Select(s => s.X).Where(x => !double.IsNaN(x)).ToList();
        RangeText = xs.Count == 0 ? "no frequency"
            : plot.Rect is { XIsFrequency: false } ? $"{Fmt(xs.Min())} … {Fmt(xs.Max())}"
            : $"{AssemblyGrid.Hz(xs.Min())} … {AssemblyGrid.Hz(xs.Max())}";
        Evidence = t.Assignment.Assigned ? $"from the {t.Assignment.Evidence}" : t.Evidence;
        FrequencyEvidence = t.Frequency is { } f ? $"frequency from {f.Source}; direction by {f.Direction}" : IsChart ? "no frequency yet" : "";
        Confidence = new ImmutableSolidColorBrush(Color.Parse(
            !t.Assignment.Assigned && !t.Assignment.Ignored ? "#C8463A"
            : t.Assignment.Evidence is "user" or "legend" ? "#2E9D4A"
            : "#D9A21B"));
        Unassigned = !t.Assignment.Assigned && !t.Assignment.Ignored;
        NeedsFrequency = IsChart && t.Frequency is null;
        _entryIndex = t.Assignment.Ignored ? 1 : t.Assignment.Assigned ? 2 + (t.Assignment.I - 1) * 4 + (t.Assignment.J - 1) : 0;
        _quantityIndex = Math.Max(0, Quantities.ToList().IndexOf(PlotQuantity.Name(t.Assignment.Quantity)));
        if (!t.Assignment.Assigned) _quantityIndex = IsChart ? Quantities.ToList().IndexOf("complex") : 0;
        TwoY = !IsChart && plot.Rect is { TwoY: true };
        _axisIndex = t.Axis == PlotSide.Right ? 1 : 0;
        AxisAmbiguous = TwoY && t.AxisAmbiguous;
        AxisEvidence = !TwoY ? "" : t.AxisAmbiguous
            ? "Nothing says which y axis this trace is read against: the left is assumed. Choose the axis."
            : $"Read against the {PlotReading.Side(t.Axis)} y axis, from {PlotReading.AxisEvidenceText(t.AxisEvidence)}.";
        _startText = startText;
        _stopText = stopText;
        // A chart trace is never fitted: its combo offers None alone, and says why (R-pdg2-1).
        FitEnabled = !IsChart;
        _fitIndex = IsChart ? 0 : fitChoice.Model;
        _degreeText = fitChoice.Degree ?? "";
        _smoothingText = fitChoice.Smoothing ?? "";
        _csv = csv;
        SmoothingWatermark = $"{TraceFit.Num(t.Resolution)}{TraceFit.UnitAfter(t.ResolutionUnit)}";
        Fit = fit;
        _loading = false;
    }

    // ── the fit (R-pdg2-4) ──

    [ObservableProperty] private int _fitIndex;

    /// <summary>A polynomial's degree, 1–8; empty is 3.</summary>
    [ObservableProperty] private string _degreeText;

    /// <summary>A spline's RMS target in the trace's units; empty is one pixel's worth.</summary>
    [ObservableProperty] private string _smoothingText;

    public bool FitEnabled { get; }

    private bool _csv;

    /// <summary>Make: CSV — the fit's tooltip says nothing of a Touchstone (R-pdg4-6).</summary>
    public void SetCsv(bool csv)
    {
        if (_csv == csv) return;
        _csv = csv;
        OnPropertyChanged(nameof(FitTip));
    }

    public string FitTip => IsChart
        ? "A chart trace is not fitted: only a trace on rectangular axes has an x to fit against, so its only fit is None."
        : _csv ? "Fit a curve to the trace: the fit is the CSV's y_fit at each point; the y column stays what was read."
        : "Fit a curve to the trace: the fit replaces the raw reading in the Touchstone and is the CSV's y_fit; the y column stays what was read.";

    /// <summary>The smoothing box's watermark: the resolution default.</summary>
    public string SmoothingWatermark { get; }

    public bool IsPolynomial => FitIndex == (int)TraceFitModel.Polynomial;
    public bool IsSpline => FitIndex == (int)TraceFitModel.Spline;

    public FitChoice Choice => new(FitIndex, DegreeText, SmoothingText);

    /// <summary>The user has chosen the fit controls.</summary>
    public bool FitEdited { get; private set; }

    /// <summary>What the fit made — the joined curve's when the trace is one piece of it; null when none is set.</summary>
    public TraceFitResult? Fit { get; }

    public bool HasFit => Fit is not null;
    public bool FitRefused => Fit?.Refusal is not null;

    /// <summary>The expression with its variable's unit beside it, <c>spline · N knots</c>, or why it was refused.</summary>
    public string FitReadout => Fit?.Readout ?? "";

    /// <summary><c>RMS 0.012 dB (0.3 px)</c>.</summary>
    public string FitResidual => Fit?.ResidualText ?? "";

    /// <summary>What Copy copies: the expression text alone; null for a spline or no fit.</summary>
    public string? FitExpression => Fit is { Fitted: true } f ? f.Expression : null;

    public bool CanCopyFit => FitExpression is not null;

    partial void OnFitIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPolynomial));
        OnPropertyChanged(nameof(IsSpline));
        if (!_loading) { FitEdited = true; _changed(this); }
    }
    partial void OnDegreeTextChanged(string value) { if (!_loading) { FitEdited = true; _changed(this); } }
    partial void OnSmoothingTextChanged(string value) { if (!_loading) { FitEdited = true; _changed(this); } }

    public string Name { get; }
    public int Plot { get; }
    public IBrush Swatch { get; }
    public string Style { get; }
    public bool IsChart { get; }
    public string Points { get; }
    public string ResolutionText { get; }
    public string RangeText { get; }
    public string Evidence { get; }
    public string FrequencyEvidence { get; }
    public IBrush Confidence { get; }
    public bool Unassigned { get; }
    public bool NeedsFrequency { get; }

    /// <summary>The trace's plot has a y axis on both sides: the Axis column is shown (R-pdg1-3).</summary>
    public bool TwoY { get; }

    /// <summary>Nothing said which y axis it is read against: its Axis swatch is amber (R-pdg1-2 (6)).</summary>
    public bool AxisAmbiguous { get; }

    /// <summary>The Axis column's tooltip: what said which axis.</summary>
    public string AxisEvidence { get; }

    [ObservableProperty] private int _entryIndex;
    [ObservableProperty] private int _quantityIndex;

    /// <summary>0 Left, 1 Right — the Axis combo.</summary>
    [ObservableProperty] private int _axisIndex;

    public PlotSide Side => AxisIndex == 1 ? PlotSide.Right : PlotSide.Left;

    /// <summary>The user has chosen the Axis combo.</summary>
    public bool AxisEdited { get; private set; }

    partial void OnAxisIndexChanged(int value) { if (!_loading) { AxisEdited = true; _changed(this); } }

    /// <summary>What the entry combo lists: the session's, up to its port count — one list every row shares (PTU-7).</summary>
    public IReadOnlyList<string> EntryChoices { get; }

    /// <summary>The entry combo's choice; null while the entry is one the port count leaves out. Null written back is the
    /// combo letting go of an item taken out of its list, never a choice.</summary>
    public string? EntryChoice
    {
        get => EntryChoices.Contains(Entries[EntryIndex]) ? Entries[EntryIndex] : null;
        set { if (value is not null && Entries.ToList().IndexOf(value) is var k and >= 0) EntryIndex = k; }
    }

    /// <summary>An entry beyond the port count, said where the combo cannot show it: <c>S41 · beyond 2 ports</c>.</summary>
    public string EntryPlaceholder => PlotSessionViewModel.Beyond(Entries[EntryIndex], EntryChoices);

    /// <summary>The port count changed under the combo.</summary>
    public void RefreshChoice()
    {
        OnPropertyChanged(nameof(EntryChoice));
        OnPropertyChanged(nameof(EntryPlaceholder));
    }

    /// <summary>A chart trace's typed start and stop (the frequency row, R-im14-8 (3)); never pre-filled.</summary>
    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _stopText;

    /// <summary>What the combos say, as <c>--trace</c> would spell it; null for <i>unassigned</i>.</summary>
    public PlotAssignment? Assignment => EntryIndex switch
    {
        <= 0 => null,
        1 => new PlotAssignment(0, 0, PlotQuantityKind.Ignored, "user"),
        _ => PlotQuantity.ParseAssignment($"{Entries[EntryIndex]}:{Quantities[Math.Clamp(QuantityIndex, 0, Quantities.Count - 1)]}"),
    };

    /// <summary>The user has chosen the combos (rather than the reading).</summary>
    public bool Edited { get; private set; }

    partial void OnEntryIndexChanged(int value)
    {
        RefreshChoice();
        if (!_loading) { Edited = true; _changed(this); }
    }
    partial void OnQuantityIndexChanged(int value) { if (!_loading) { Edited = true; _changed(this); } }
    partial void OnStartTextChanged(string value) { if (!_loading) _changed(this); }
    partial void OnStopTextChanged(string value) { if (!_loading) _changed(this); }

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>One colour group of the traces table in Simple mode (R-ptu5-2): the pieces of one curve, assigned together.
/// It holds no assignment of its own — an assignment is per piece, and the group shows what its pieces say.</summary>
public sealed partial class PlotTraceGroupViewModel : ObservableObject
{
    private readonly Action<Action> _batch;

    public PlotTraceGroupViewModel(IReadOnlyList<PlotTraceRowViewModel> members, string title, bool debris, Action<Action> batch)
    {
        Members = members;
        Title = title;
        Debris = debris;
        _batch = batch;
        foreach (var m in members)
            m.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlotTraceRowViewModel.FitTip)) { OnPropertyChanged(nameof(FitTip)); return; }
                if (e.PropertyName is not (nameof(PlotTraceRowViewModel.EntryIndex) or nameof(PlotTraceRowViewModel.QuantityIndex)
                                           or nameof(PlotTraceRowViewModel.AxisIndex) or nameof(PlotTraceRowViewModel.FitIndex)
                                           or nameof(PlotTraceRowViewModel.DegreeText) or nameof(PlotTraceRowViewModel.SmoothingText))) return;
                OnPropertyChanged(nameof(EntryIndex));
                OnPropertyChanged(nameof(QuantityIndex));
                OnPropertyChanged(nameof(AxisIndex));
                OnPropertyChanged(nameof(FitIndex));
                OnPropertyChanged(nameof(IsPolynomial));
                OnPropertyChanged(nameof(IsSpline));
                OnPropertyChanged(nameof(DegreeText));
                OnPropertyChanged(nameof(SmoothingText));
                RefreshChoice();
            };
    }

    /// <summary>The pieces, in x order.</summary>
    public IReadOnlyList<PlotTraceRowViewModel> Members { get; }

    public PlotTraceRowViewModel First => Members[0];

    /// <summary><c>Red · 4 pieces · 2.3–2.6 GHz</c>.</summary>
    public string Title { get; }

    /// <summary>Shorter in total than the reader's own debris length: a legend sample or a glyph. Listed last, dimmed.</summary>
    public bool Debris { get; }

    public double RowOpacity => Debris ? 0.5 : 1;

    public IBrush Swatch => First.Swatch;

    /// <summary>The worst of its pieces: red while any is unassigned.</summary>
    public IBrush Confidence => (Members.FirstOrDefault(m => m.Unassigned) ?? First).Confidence;

    public string Names => string.Join(" + ", Members.Select(m => m.Name));

    /// <summary>A chart trace is never grouped, so its one piece's frequency row is the group's.</summary>
    public bool IsChart => Members.Count == 1 && First.IsChart;

    /// <summary>The pieces' entry, or −1 (<i>mixed</i>) when they differ. Choosing one assigns every piece.</summary>
    public int EntryIndex
    {
        get => Same(m => m.EntryIndex);
        set { if (value >= 0 && value != EntryIndex) _batch(() => { foreach (var m in Members) m.EntryIndex = value; }); }
    }

    public IReadOnlyList<string> EntryChoices => First.EntryChoices;

    /// <summary>The pieces' entry when they agree and the port count includes it; else null — <i>mixed</i>, or the entry
    /// said in <see cref="EntryPlaceholder"/>.</summary>
    public string? EntryChoice
    {
        get => EntryIndex >= 0 && EntryChoices.Contains(PlotTraceRowViewModel.Entries[EntryIndex]) ? PlotTraceRowViewModel.Entries[EntryIndex] : null;
        set { if (value is not null && PlotTraceRowViewModel.Entries.ToList().IndexOf(value) is var k and >= 0) EntryIndex = k; }
    }

    public string EntryPlaceholder => EntryIndex < 0 ? "mixed" : PlotSessionViewModel.Beyond(PlotTraceRowViewModel.Entries[EntryIndex], EntryChoices);

    public void RefreshChoice()
    {
        OnPropertyChanged(nameof(EntryChoice));
        OnPropertyChanged(nameof(EntryPlaceholder));
    }

    public int QuantityIndex
    {
        get => Same(m => m.QuantityIndex);
        set { if (value >= 0 && value != QuantityIndex) _batch(() => { foreach (var m in Members) m.QuantityIndex = value; }); }
    }

    /// <summary>Its pieces lie on a plot with a y axis on both sides.</summary>
    public bool TwoY => First.TwoY;

    /// <summary>Any piece's axis is assumed.</summary>
    public bool AxisAmbiguous => Members.Any(m => m.AxisAmbiguous);

    public string AxisEvidence => First.AxisEvidence;

    /// <summary>The pieces' axis, or −1 (<i>mixed</i>). Choosing one moves every piece (R-pdg1-3).</summary>
    public int AxisIndex
    {
        get => Same(m => m.AxisIndex);
        set { if (value >= 0 && value != AxisIndex) _batch(() => { foreach (var m in Members) m.AxisIndex = value; }); }
    }

    // ── the fit, on the colour row (R-pdg2-3) ──

    /// <summary>The pieces' fit, or −1 (<i>mixed</i>). Choosing one fits every piece.</summary>
    public int FitIndex
    {
        get => Same(m => m.FitIndex);
        set { if (value >= 0 && value != FitIndex) _batch(() => { foreach (var m in Members) m.FitIndex = value; }); }
    }

    public string DegreeText
    {
        get => First.DegreeText;
        set { if (value != DegreeText) _batch(() => { foreach (var m in Members) m.DegreeText = value; }); }
    }

    public string SmoothingText
    {
        get => First.SmoothingText;
        set { if (value != SmoothingText) _batch(() => { foreach (var m in Members) m.SmoothingText = value; }); }
    }

    public bool FitEnabled => !IsChart && Members.All(m => m.FitEnabled);
    public string FitTip => First.FitTip;
    public string SmoothingWatermark => First.SmoothingWatermark;
    public bool IsPolynomial => FitIndex == (int)TraceFitModel.Polynomial;
    public bool IsSpline => FitIndex == (int)TraceFitModel.Spline;

    /// <summary>The fitted piece's readout: pieces fitted together show one fit.</summary>
    private PlotTraceRowViewModel Fitted => Members.FirstOrDefault(m => m.HasFit) ?? First;

    public bool HasFit => Fitted.HasFit;
    public bool FitRefused => Fitted.FitRefused;
    public string FitReadout => Fitted.FitReadout;
    public string FitResidual => Fitted.FitResidual;
    public string? FitExpression => Fitted.FitExpression;
    public bool CanCopyFit => Fitted.CanCopyFit;

    private int Same(Func<PlotTraceRowViewModel, int> f) => Members.Select(f).Distinct().Count() == 1 ? f(First) : -1;
}

/// <summary>One entry of the matrix grid (R-im14-12): where its data came from, and the rule that answers it.</summary>
public sealed partial class PlotMatrixCellViewModel : ObservableObject
{
    private readonly Action<PlotMatrixCellViewModel> _changed;
    private bool _loading = true;

    public PlotMatrixCellViewModel(MatrixCell cell, int ports, bool missingPhase, int selected, Action<PlotMatrixCellViewModel> changed)
    {
        _changed = changed;
        I = cell.I;
        J = cell.J;
        Source = cell.Source;
        Rule = cell.Rule.Length > 0 ? cell.Rule : cell.Phase.Length > 0 ? cell.Phase : "";
        Estimated = cell.Estimated;
        IsPhase = cell.Plotted && missingPhase;
        Choices = IsPhase ? ["min phase (estimate)", "zero phase", "delay"]
            : cell.Plotted ? []
            : FillChoices(I, J, ports);
        _selectedIndex = Choices.Count == 0 ? -1 : Math.Clamp(selected, 0, Choices.Count - 1);
        _loading = false;
    }

    public int I { get; }
    public int J { get; }
    public string Entry => $"S{I.ToString(CultureInfo.InvariantCulture)}{J.ToString(CultureInfo.InvariantCulture)}";
    public string Source { get; }
    public string Rule { get; }
    public bool Estimated { get; }

    /// <summary>A plotted magnitude with no phase: the combo is its missing-phase rule.</summary>
    public bool IsPhase { get; }

    public IReadOnlyList<string> Choices { get; }
    public bool HasChoices => Choices.Count > 0;

    [ObservableProperty] private int _selectedIndex;

    /// <summary>The delay, when the phase rule is <i>delay</i> (<c>250 ps</c>).</summary>
    [ObservableProperty] private string _delayText = "";

    public bool AsksDelay => IsPhase && SelectedIndex == 2;

    partial void OnSelectedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(AsksDelay));
        if (!_loading) _changed(this);
    }

    partial void OnDelayTextChanged(string value) { if (!_loading) _changed(this); }

    /// <summary>The fill choices of an unplotted entry, the stated default first.</summary>
    public static IReadOnlyList<string> FillChoices(int i, int j, int ports)
    {
        var c = new List<string> { "default" };
        if (i != j) c.Add($"= S{j}{i}");
        if (ports == 2 && i == j) c.Add($"= S{3 - i}{3 - i}");
        c.Add("zero");
        c.Add("unassigned");
        return c;
    }

    public FillRule? Fill => IsPhase || SelectedIndex < 0 ? null : Choices[SelectedIndex] switch
    {
        "default" => FillRule.Default,
        "zero" => FillRule.Zero,
        "unassigned" => FillRule.Unassigned,
        var s when s.StartsWith('=') && I != J => FillRule.Reciprocal,
        _ => FillRule.Symmetric,
    };
}

/// <summary>The plot mode's state, and the options it spells.</summary>
/// <summary>A box drawn with Ignore here (R-ptu6-3): nothing in it is read as a trace on plot <paramref name="Plot"/>, which
/// is on picture <paramref name="Picture"/> (0-based). Picture pixels.</summary>
public sealed record PlotIgnoreBox(int Plot, int Picture, PixelBox Box);

/// <summary>A point pinned on a traced curve (brief-pdg-3-snap-to-curve.md R-pdg3-2): session state only, never written.</summary>
/// <param name="picture">The 0-based picture it was pinned on.</param>
public sealed partial class PlotProbePointViewModel(int number, int picture, Action<PlotProbePointViewModel> remove) : ObservableObject
{
    public int Number { get; } = number;
    public int Picture { get; set; } = picture;
    public string Name => $"P{Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The x typed for it, in the axis's own units; null for a point clicked.</summary>
    public double? TypedX { get; init; }

    /// <summary>Where it sits on the picture, pixels — what a re-read snaps it by.</summary>
    public double Px { get; set; }
    public double Py { get; set; }

    /// <summary>What the curve reads there; null when a re-read left no curve under it.</summary>
    [ObservableProperty] private TraceProbeHit? _hit;

    /// <summary><c>P1 · T3 · 2.437 GHz · −12.31 dB</c>.</summary>
    [ObservableProperty] private string _text = "";

    [RelayCommand] private void Remove() => remove(this);
}

public sealed partial class PlotSessionViewModel : ObservableObject
{
    private readonly Dictionary<string, PlotAssignment> _assign = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Start, string Stop)> _freq = new(StringComparer.Ordinal);
    private readonly HashSet<int> _excluded = [];
    private readonly Dictionary<(int Plot, PlotAxis Axis), string> _axis = [];

    /// <summary>The y axis the user chose for a trace, by name (R-pdg1-3).</summary>
    private readonly Dictionary<string, PlotSide> _side = new(StringComparer.Ordinal);

    /// <summary>The fit the user chose for a trace, by name, as its controls hold it (R-pdg2-4).</summary>
    private readonly Dictionary<string, PlotTraceRowViewModel.FitChoice> _fit = new(StringComparer.Ordinal);
    private readonly Dictionary<(int I, int J), FillRule> _fill = [];
    private readonly Dictionary<(int I, int J), (int Index, string Delay)> _phase = [];
    private readonly List<PlotIgnoreBox> _ignore = [];

    /// <summary>The axes the user said are (or are not) logarithmic, by plot (R-pdg4-7).</summary>
    private readonly Dictionary<(int Plot, PlotAxis Axis), bool> _log = [];

    // ── the port count and the assignment undo (PTU-7) ───────────────────────────────────────────────

    /// <summary>The typed port count; empty is <see cref="AutoPorts"/>.</summary>
    [ObservableProperty] private string _portsText = "";

    /// <summary>The entry combos' list, shared by every row: unassigned, ignore, S11 … Snn for <see cref="EntryPorts"/>.
    /// Changed in place, never replaced, so a combo keeps the choice it has when another entry comes or goes.</summary>
    public ObservableCollection<string> EntryChoices { get; } = new(PlotTraceRowViewModel.EntriesFor(2));

    /// <summary>The port count when none is typed: the highest port a trace names, at least 2.</summary>
    public int AutoPorts => Math.Clamp(
        Session?.Traces.Where(t => t.Assignment.Assigned && !t.Assignment.Ignored)
                       .Select(t => Math.Max(t.Assignment.I, t.Assignment.J)).DefaultIfEmpty(0).Max() ?? 0,
        2, TouchstoneAssembly.MaxPorts);

    public string AutoPortsText => AutoPorts.ToString(CultureInfo.InvariantCulture);

    /// <summary>The typed port count, or null when none (or none legible) is typed.</summary>
    public int? TypedPorts() =>
        int.TryParse(PortsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= TouchstoneAssembly.MaxPorts
            ? n : null;

    public bool PortsInvalid => PortsText.Trim().Length > 0 && TypedPorts() is null;

    /// <summary>The ports the entry combos offer.</summary>
    public int EntryPorts => TypedPorts() ?? AutoPorts;

    /// <summary><c>S41 · beyond 2 ports</c> for an entry the list leaves out; empty otherwise.</summary>
    public static string Beyond(string entry, IReadOnlyList<string> choices)
    {
        if (choices.Contains(entry)) return "";
        int ports = choices.Count(c => c.Length == 3 && c[0] == 'S' && c[1] == c[2]);
        return $"{entry} · beyond {ports.ToString(CultureInfo.InvariantCulture)} port{(ports == 1 ? "" : "s")}";
    }

    partial void OnPortsTextChanged(string? oldValue, string newValue)
    {
        if (!_undoing) Remember(State() with { Ports = oldValue ?? "" });
        OnPropertyChanged(nameof(PortsInvalid));
        SyncChoices();
        if (!_undoing) Changed?.Invoke();
    }

    /// <summary>Brings the shared entry list to <see cref="EntryPorts"/>: what leaves goes, what joins is put in its
    /// place; every combo is told after.</summary>
    private void SyncChoices()
    {
        var want = PlotTraceRowViewModel.EntriesFor(EntryPorts);
        for (int k = EntryChoices.Count - 1; k >= 0; k--)
            if (!want.Contains(EntryChoices[k])) EntryChoices.RemoveAt(k);
        for (int k = 0; k < want.Count; k++)
            if (k >= EntryChoices.Count || EntryChoices[k] != want[k]) EntryChoices.Insert(k, want[k]);
        foreach (var t in Traces) t.RefreshChoice();
        foreach (var g in Groups) g.RefreshChoice();
        OnPropertyChanged(nameof(AutoPortsText));
    }

    /// <summary>The assignments the user made, the port count, the axes and the fits chosen, as one undo step restores them.</summary>
    private sealed record AssignmentState(Dictionary<string, PlotAssignment> Assign, string Ports, Dictionary<string, PlotSide> Sides,
                                          Dictionary<string, PlotTraceRowViewModel.FitChoice> Fits);

    private readonly Stack<AssignmentState> _undo = new();
    private bool _undoing;

    private AssignmentState State() => new(new(_assign, StringComparer.Ordinal), PortsText, new(_side, StringComparer.Ordinal),
                                           new(_fit, StringComparer.Ordinal));

    private void Remember(AssignmentState before)
    {
        _undo.Push(before);
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void ForgetUndo()
    {
        _undo.Clear();
        UndoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => Manual ? ShownManual?.CanUndo == true : _undo.Count > 0;

    /// <summary>Puts back the assignments and the port count as they were before the last change to either — a group's
    /// combo assigning every piece is one step. In Manual, the shown picture's box and fixpoints as they were.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Manual)
        {
            ShownManual?.Undo();
            UndoCommand.NotifyCanExecuteChanged();
            return;
        }
        if (_undo.Count == 0) return;
        var back = _undo.Pop();
        _undoing = true;
        try
        {
            _assign.Clear();
            foreach (var (name, a) in back.Assign) _assign[name] = a;
            _side.Clear();
            foreach (var (name, side) in back.Sides) _side[name] = side;
            _fit.Clear();
            foreach (var (name, fit) in back.Fits) _fit[name] = fit;
            PortsText = back.Ports;
        }
        finally { _undoing = false; }
        UndoCommand.NotifyCanExecuteChanged();
        Changed?.Invoke();
    }

    private static bool Same<T>(Dictionary<string, T> a, Dictionary<string, T> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && Equals(v, kv.Value));

    private bool Unchanged(AssignmentState before) => Same(before.Assign, _assign) && Same(before.Sides, _side) && Same(before.Fits, _fit);

    /// <summary>Raised when anything the reading or the file depends on changed: the dialog reads again.</summary>
    public event Action? Changed;

    /// <summary>Raised when the canvas should show another picture.</summary>
    public event Action? ShownChanged;

    /// <summary>Raised when an axis row's values changed: the canvas shows a typed label's value at once, before (and
    /// even without) a reading — one value alone blocks the reading until the second (PTU-7).</summary>
    public event Action? AxisTextChanged;

    public ObservableCollection<PlotPictureRowViewModel> Pictures { get; } = [];
    public ObservableCollection<PlotFrameRowViewModel> Frames { get; } = [];
    public ObservableCollection<PlotTraceRowViewModel> Traces { get; } = [];

    /// <summary>The traces by colour (R-ptu5-2): what the table lists in Simple mode.</summary>
    public ObservableCollection<PlotTraceGroupViewModel> Groups { get; } = [];

    /// <summary>The traces (groups) of the selected plot — what the traces table lists (PTU-7). Every plot's when none is
    /// selected.</summary>
    public ObservableCollection<PlotTraceRowViewModel> ShownTraces { get; } = [];
    public ObservableCollection<PlotTraceGroupViewModel> ShownGroups { get; } = [];

    /// <summary>The plot chosen in the plots list: the traces table lists its traces, and the canvas shows its picture.</summary>
    [ObservableProperty] private PlotFrameRowViewModel? _selectedFrame;

    public ObservableCollection<PlotMatrixCellViewModel> Matrix { get; } = [];
    public ObservableCollection<string> ReportLines { get; } = [];

    public PlotSession? Session { get; private set; }
    public TouchstoneAssemblyResult? Assembly { get; private set; }

    /// <summary>The picture the canvas draws, 0-based.</summary>
    [ObservableProperty] private int _shownPicture;

    /// <summary>The trace chosen in the table or on the canvas — one selection, shown on both (R-ptu3-1, R-ptu3-2).</summary>
    [ObservableProperty] private PlotTraceRowViewModel? _selectedTrace;

    /// <summary>The selected trace's colour group — the row Simple mode shows selected (R-ptu5-4). Choosing a group selects
    /// its first piece unless one of its pieces is selected already.</summary>
    [ObservableProperty] private PlotTraceGroupViewModel? _selectedGroup;

    /// <summary>The table lists one row per piece (Detailed) rather than one per colour group (Simple) — R-ptu5-3.</summary>
    [ObservableProperty] private bool _detailed;

    public bool Simple { get => !Detailed; set => Detailed = !value; }

    partial void OnDetailedChanged(bool value) => OnPropertyChanged(nameof(Simple));

    partial void OnSelectedTraceChanged(PlotTraceRowViewModel? value)
    {
        // A trace chosen on the canvas on another plot brings its plot's traces into the table.
        if (value is not null && value.Plot != SelectedFrame?.Number) SelectedFrame = FrameRow(value.Plot);
        SelectedGroup = GroupOf(value);
    }

    partial void OnSelectedFrameChanged(PlotFrameRowViewModel? value)
    {
        // Null is the list letting go while its rows are rebuilt; Apply restores the selection.
        if (value is null) return;
        ListShown();
        if (SelectedTrace is { } t && t.Plot != value.Number) SelectedTrace = null;
        if (value.Picture - 1 != ShownPicture) ShownPicture = value.Picture - 1;
    }

    private PlotFrameRowViewModel? FrameRow(int plot) => Frames.FirstOrDefault(f => f.Number == plot);

    /// <summary>Fills the traces table with the selected plot's traces and groups.</summary>
    private void ListShown()
    {
        int? plot = SelectedFrame?.Number;
        ShownTraces.Clear();
        foreach (var t in Traces.Where(t => plot is null || t.Plot == plot)) ShownTraces.Add(t);
        ShownGroups.Clear();
        foreach (var g in Groups.Where(g => plot is null || g.First.Plot == plot)) ShownGroups.Add(g);
    }

    partial void OnSelectedGroupChanged(PlotTraceGroupViewModel? value)
    {
        // Null is the list letting go while its rows are rebuilt; the trace selection is the one that is kept.
        if (value is not null && (SelectedTrace is null || !value.Members.Contains(SelectedTrace))) SelectedTrace = value.First;
    }

    public PlotTraceGroupViewModel? GroupOf(PlotTraceRowViewModel? row) => row is null ? null : Groups.FirstOrDefault(g => g.Members.Contains(row));

    /// <summary>The traces the canvas draws selected when <paramref name="name"/> is: its whole group in Simple mode.</summary>
    public IReadOnlySet<string> Lit(string? name)
    {
        if (name is null) return new HashSet<string>();
        if (!Detailed && GroupOf(TraceRow(name)) is { } g) return g.Members.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        return new HashSet<string>(StringComparer.Ordinal) { name };
    }

    /// <summary>The row of the trace called <paramref name="name"/>, or null.</summary>
    public PlotTraceRowViewModel? TraceRow(string? name) =>
        name is null ? null : Traces.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>The 0-based picture a trace was read on, or −1.</summary>
    public int PictureOf(PlotTraceRowViewModel row) =>
        Session is { } s && row.Plot >= 1 && row.Plot <= s.Plots.Count ? s.Plots[row.Plot - 1].Picture : -1;

    [ObservableProperty] private string _smithZ0Text = "";
    [ObservableProperty] private string _gridStartText = "";
    [ObservableProperty] private string _gridStopText = "";
    [ObservableProperty] private string _gridPointsText = "";
    [ObservableProperty] private bool _gridLog;

    /// <summary>0 dB, 1 MA, 2 RI.</summary>
    [ObservableProperty] private int _formatIndex;

    /// <summary>Keep the source pictures beside the Touchstone (D4).</summary>
    [ObservableProperty] private bool _keepSources = true;

    partial void OnSmithZ0TextChanged(string value) => Changed?.Invoke();
    partial void OnGridStartTextChanged(string value) => Changed?.Invoke();
    partial void OnGridStopTextChanged(string value) => Changed?.Invoke();
    partial void OnGridPointsTextChanged(string value) => Changed?.Invoke();
    partial void OnGridLogChanged(bool value) => Changed?.Invoke();
    partial void OnFormatIndexChanged(int value) => Changed?.Invoke();

    partial void OnShownPictureChanged(int value)
    {
        // Another picture shown: the plots list follows to its first plot, so the traces listed are the ones on screen.
        if (SelectedFrame is { } f && f.Picture - 1 != value && Frames.FirstOrDefault(r => r.Picture - 1 == value) is { } first)
            SelectedFrame = first;
        OnPropertyChanged(nameof(ShownManual));
        SyncTool();
        UndoCommand.NotifyCanExecuteChanged();
        ShownChanged?.Invoke();
    }

    public static IReadOnlyList<string> Formats { get; } = ["DB", "MA", "RI"];

    // ── the pictures ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The session's first picture: a new session, every choice forgotten.</summary>
    public void SetPrimary(ImageSource source)
    {
        Pictures.Clear();
        _assign.Clear();
        _side.Clear();
        _fit.Clear();
        ForgetUndo();
        _freq.Clear();
        _excluded.Clear();
        _axis.Clear();
        _fill.Clear();
        _phase.Clear();
        _log.Clear();
        _manualPlots.Clear();
        ForgetIgnoreBoxes();
        ForgetAllPoints();
        Session = null;
        Assembly = null;
        SelectedTrace = null;
        AskingLabel = false;
        SelectedFrame = null;
        Frames.Clear();
        Traces.Clear();
        Groups.Clear();
        ShownTraces.Clear();
        ShownGroups.Clear();
        Matrix.Clear();
        ReportLines.Clear();
        _undoing = true;
        try { PortsText = ""; }
        finally { _undoing = false; }
        Add(source, notify: false);
        ShownPicture = 0;
        OnPropertyChanged(nameof(ShownManual));
        SyncTool();
        RefreshDerived();
    }

    /// <summary>A picture joins the session (R-im14-12, R-im14-13).</summary>
    public void Add(ImageSource source, bool notify = true)
    {
        Pictures.Add(new PlotPictureRowViewModel(source, RemovePicture, p => ShownPicture = Pictures.IndexOf(p)));
        Renumber();
        if (notify)
        {
            ShownPicture = Pictures.Count - 1;
            Changed?.Invoke();
        }
    }

    private void RemovePicture(PlotPictureRowViewModel row)
    {
        int i = Pictures.IndexOf(row);
        if (i <= 0) return;
        Pictures.RemoveAt(i);
        Renumber();
        // Plot and trace numbers move when a picture goes; choices held by number would land on the wrong one.
        _assign.Clear();
        _side.Clear();
        _fit.Clear();
        ForgetUndo();
        _freq.Clear();
        _excluded.Clear();
        _axis.Clear();
        ForgetIgnoreBoxes();
        ForgetPoints(i, shift: true);
        _log.Clear();
        _manualPlots.Remove(i);
        foreach (var k in _manualPlots.Keys.Where(k => k > i).OrderBy(k => k).ToList())
        {
            var m = _manualPlots[k];
            _manualPlots.Remove(k);
            m.Picture = k - 1;
            _manualPlots[k - 1] = m;
        }
        if (ShownPicture >= Pictures.Count) ShownPicture = Pictures.Count - 1;
        OnPropertyChanged(nameof(ShownManual));
        Changed?.Invoke();
    }

    /// <summary>
    /// A picture of the session swapped in place for another — cropped to a box, or restored (R-ptu6-2) — the others kept.
    /// Its plots and traces are renumbered by the re-read, so every choice held by number is forgotten, as when a picture
    /// is removed, and what was read on the old picture is dropped until the new one is read.
    /// </summary>
    public void ReplacePicture(int index, ImageSource source, ImageSource? original)
    {
        if (index < 0 || index >= Pictures.Count) return;
        Pictures[index] = new PlotPictureRowViewModel(source, RemovePicture, p => ShownPicture = Pictures.IndexOf(p)) { Original = original };
        Renumber();
        _assign.Clear();
        _side.Clear();
        _fit.Clear();
        ForgetUndo();
        _freq.Clear();
        _excluded.Clear();
        _axis.Clear();
        ForgetIgnoreBoxes();
        ForgetPoints(index, shift: false);
        _log.Clear();
        _manualPlots.Remove(index);   // its pixels changed: the box and the points drawn on them are gone with them
        OnPropertyChanged(nameof(ShownManual));
        Session = null;
        Assembly = null;
        SelectedTrace = null;
        AskingLabel = false;
        RefreshDerived();
        if (ShownPicture == index) ShownChanged?.Invoke();
        else ShownPicture = index;
        Changed?.Invoke();
    }

    private void Renumber()
    {
        for (int i = 0; i < Pictures.Count; i++) Pictures[i].Number = i + 1;
        OnPropertyChanged(nameof(HasSeveral));
    }

    public bool HasSeveral => Pictures.Count > 1;

    /// <summary>The pictures as the reading takes them.</summary>
    public IReadOnlyList<PlotPictureInput> Inputs() =>
        [.. Pictures.Select(p => new PlotPictureInput(p.Source, p.Name == "Pasted picture" ? ImageSource.PastedName : p.Source.OriginalName))];

    // ── the options (the CLI's flags, field for field) ──────────────────────────────────────────────

    /// <summary>Why a typed field cannot be read, or null.</summary>
    public string? Blocked()
    {
        if (SmithZ0Text.Trim().Length > 0 && Z0() is null) return "Smith Z0 needs an impedance in ohms (50).";
        if (PortsInvalid) return $"Ports needs a whole number from 1 to {TouchstoneAssembly.MaxPorts.ToString(CultureInfo.InvariantCulture)}.";
        foreach (var ((plot, axis), text) in _axis)
            if (text.Trim().Length > 0 && Clicks(text, axis) is null)
                return PlotAxisLabels.Pairs(text, axis).Count == 1 && !text.Contains(',')
                    ? $"Plot {plot}'s {RectAxes.Name(axis)} axis needs one more value: click another {RectAxes.Name(axis)} label on the picture."
                    : $"Plot {plot}'s {RectAxes.Name(axis)} axis needs two pixel positions and their values: 120px=1GHz,900px=5GHz.";
        foreach (var (trace, (start, stop)) in _freq)
            if ((start.Trim().Length > 0 || stop.Trim().Length > 0) && Range(start, stop) is null)
                return $"{trace}'s frequency needs a start and a stop with units, the stop above the start (1 GHz, 5 GHz).";
        if (GridText() is { } g && Grid() is null && g.Length > 0)
            return "The output grid needs a start, a stop (with units) and a number of points, at least 2.";
        foreach (var ((i, j), (index, delay)) in _phase)
            if (index == 2 && Seconds(delay) is null) return $"S{i}{j}'s delay needs a time with its unit (250 ps).";
        foreach (var (trace, fit) in _fit.OrderBy(f => f.Key, StringComparer.Ordinal))
            if (fit.Spec is null)
                return fit.Model == (int)TraceFitModel.Polynomial
                    ? $"{trace}'s polynomial degree needs a whole number from 1 to {TraceFitSpec.MaxDegree.ToString(CultureInfo.InvariantCulture)}."
                    : $"{trace}'s spline smoothing needs a positive RMS in the trace's own units (0.05), or nothing for one pixel.";
        return null;
    }

    public PlotReadingOptions ReadingOptions() => Manual ? ManualOptions() : new()
    {
        Plots = _excluded.Count == 0 || Session is null ? null : [.. Session.Plots.Select(p => p.Number).Where(n => !_excluded.Contains(n))],
        Axes = [.. _axis.Where(a => a.Value.Trim().Length > 0)
                        .Select(a => (a.Key, Clicks: Clicks(a.Value, a.Key.Axis)))
                        .Where(a => a.Clicks is not null)
                        .Select(a => new PlotAxisOverride(a.Key.Plot, a.Key.Axis, a.Clicks!))],
        Traces = [.. _assign.Keys.Union(_side.Keys)
                            .Select(name => new PlotTraceOverride(name, _assign.TryGetValue(name, out var a) ? a : null,
                                                                  _side.TryGetValue(name, out var side) ? side : null))],
        SmithZ0 = Z0(),
        Frequencies = [.. _freq.Select(f => (f.Key, R: Range(f.Value.Start, f.Value.Stop))).Where(f => f.R is not null)
                               .Select(f => new PlotFrequencyOverride(f.Key, f.R!.Value.Start, f.R.Value.Stop))],
        Hints = _ignore.GroupBy(b => b.Plot).ToDictionary(g => g.Key, g => new TraceHints([], [.. g.Select(b => b.Box)])),
        Scales = [.. _log.Select(l => (l.Key.Plot, l.Key.Axis, l.Value))],
    };

    /// <summary>The options of a session read by hand (R-pdg4-1): each picture's box and scales, in picture order, and the
    /// curves on each — the records the CLI's by-hand flags spell.</summary>
    private PlotReadingOptions ManualOptions()
    {
        var plots = new List<ManualPlot>();
        var curves = new List<ManualCurve>();
        foreach (var m in _manualPlots.OrderBy(m => m.Key).Select(m => m.Value))
        {
            if (m.Picture >= Pictures.Count || m.Plot() is not { } plot) continue;
            plots.Add(plot);
            curves.AddRange(m.CurvesOn(plots.Count));
        }
        return new PlotReadingOptions { Manual = plots, ManualCurves = curves };
    }

    // ── Read: Automatic | Manual (brief-pdg-4-digitise-by-hand.md R-pdg4-5) ─────────────────────────

    private readonly Dictionary<int, ManualPlotViewModel> _manualPlots = [];

    /// <summary>The session is read by hand.</summary>
    [ObservableProperty] private bool _manual;

    /// <summary>The Read toggles — the user's choice, which is remembered (<see cref="ReadModeChosen"/>).</summary>
    public bool IsAutomatic
    {
        get => !Manual;
        set { if (value && Manual) { Manual = false; ReadModeChosen?.Invoke(); } }
    }

    public bool IsManual
    {
        get => Manual;
        set { if (value && !Manual) { Manual = true; ReadModeChosen?.Invoke(); } }
    }

    /// <summary>Raised when the user chose Automatic or Manual — not when a command opened the dialog in one.</summary>
    public event Action? ReadModeChosen;

    /// <summary>Make: CSV is chosen (R-pdg4-6): the Touchstone's controls are hidden. Set by the dialog.</summary>
    [ObservableProperty] private bool _makeCsv;

    /// <summary>What a press on the canvas does in Manual.</summary>
    [ObservableProperty] private ManualTool _manualTool;

    /// <summary>The shown picture's plot read by hand; null outside Manual.</summary>
    public ManualPlotViewModel? ShownManual => Manual && ShownPicture >= 0 && ShownPicture < Pictures.Count ? ManualOf(ShownPicture) : null;

    /// <summary>A picture's plot read by hand, made on first use — its box pre-placed on a frame the automatic finder
    /// happens to find there, as a starting suggestion only (nothing else of the automatic reader is used).</summary>
    public ManualPlotViewModel ManualOf(int picture)
    {
        if (_manualPlots.TryGetValue(picture, out var m)) return m;
        m = new ManualPlotViewModel(picture, OnManualEdited);
        m.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ManualPlotViewModel.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName == nameof(ManualPlotViewModel.SelectedCurve) && m.SelectedCurve is not null && ManualTool is ManualTool.None or ManualTool.Box)
                ManualTool = ManualTool.Points;
        };
        _manualPlots[picture] = m;
        if (picture < Pictures.Count
            && PlotReading.FindPlots(Pictures[picture].Source).FirstOrDefault(f => f.Type == PlotType.Rectangular) is { } frame)
            m.SuggestBox(frame.Left, frame.Top, frame.Right, frame.Bottom);
        return m;
    }

    /// <summary>A picture shown in Manual with no box yet: the canvas's next drag draws one.</summary>
    private void SyncTool()
    {
        if (ShownManual is { HasBox: false }) ManualTool = ManualTool.Box;
    }

    private void OnManualEdited()
    {
        UndoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TouchstoneOffered));
        Changed?.Invoke();
    }

    partial void OnManualChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAutomatic));
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(ShownManual));
        OnPropertyChanged(nameof(TouchstoneOffered));
        ManualTool = value ? ManualTool.Points : ManualTool.None;
        SyncTool();
        UndoCommand.NotifyCanExecuteChanged();
        Changed?.Invoke();
    }

    partial void OnMakeCsvChanged(bool value)
    {
        foreach (var t in Traces) t.SetCsv(value);
        RefreshDerived();
    }

    /// <summary>Find curve: the next click on the canvas follows the curve under it into the selected curve's points.</summary>
    [RelayCommand]
    private void FindCurve()
    {
        if (ShownManual?.SelectedCurve is null)
        {
            StatusRaised?.Invoke("Add a curve first: Find curve fills the selected curve's points.");
            return;
        }
        ManualTool = ManualTool.Find;
    }

    /// <summary>A click with Find curve armed, picture pixels: the curve under it followed. The tool goes back to Points.</summary>
    public bool FindAt(double x, double y)
    {
        ManualTool = ManualTool.Points;
        if (ShownManual is not { } m || ShownPicture >= Pictures.Count) return false;
        if (m.FindCurve(Pictures[ShownPicture].Source.Raster, x, y) is { } why)
        {
            StatusRaised?.Invoke(why);
            return false;
        }
        return true;
    }

    /// <summary>Draw the plot area again: the next drag on the canvas is the box.</summary>
    [RelayCommand] private void RedrawBox() => ManualTool = ManualTool.Box;

    /// <summary>Place points again after Escape stopped it: the next double-click adds one to the selected curve.</summary>
    [RelayCommand]
    private void PlacePoints()
    {
        if (ShownManual?.SelectedCurve is null)
        {
            StatusRaised?.Invoke("Add a curve first: its points are placed on the picture.");
            return;
        }
        ManualTool = ManualTool.Points;
    }

    /// <summary>What the scales read at a picture point inside the shown box — the hover readout of Manual — or null.</summary>
    public string? ManualReadout(double x, double y)
    {
        if (ShownManual?.Plot() is not { Refusal: null } plot) return null;
        var (bx, by) = plot.ToBox(x, y);
        if (bx < plot.Left || bx > plot.Right || by < plot.Top || by > plot.Bottom) return null;
        var r = plot.Calibrate();
        if (r.X is not { } xa || r.Y is not { } ya) return null;
        string xs = TraceProbe.Fixed(xa.ValueAt(bx) / (plot.XIsFrequency ? plot.XUnit : 1), xa.PerPixel(bx) / (plot.XIsFrequency ? plot.XUnit : 1));
        string xu = plot.XIsFrequency ? $" {Unit(plot.XUnit)}" : "";
        string ys = TraceProbe.Fixed(ya.ValueAt(by), ya.PerPixel(by));
        string y2 = r.Y2 is { } y2a ? $" · {TraceProbe.Fixed(y2a.ValueAt(by), y2a.PerPixel(by))}{(plot.Y2Unit.Length > 0 ? " " + plot.Y2Unit : "")} (right)" : "";
        return $"{xs}{xu} · {ys}{(plot.YUnit.Length > 0 ? " " + plot.YUnit : "")}{y2}";
    }

    private static string Unit(double multiplier) => multiplier switch { >= 1e12 => "THz", >= 1e9 => "GHz", >= 1e6 => "MHz", >= 1e3 => "kHz", _ => "Hz" };

    /// <summary>R-pdg4-4: with Make: CSV, a curve said to be an S-parameter quantity on a frequency axis offers a Touchstone
    /// as well — an offer, never a default.</summary>
    public bool TouchstoneOffered => Manual && MakeCsv && Session is { } s
                                     && s.Traces.Any(t => t.Manual && t.Assignment.Assigned && s.Plots[t.Plot - 1].Rect is { XIsFrequency: true });

    /// <summary>Also save as Touchstone…: the dialog asks where and writes it.</summary>
    [RelayCommand] private void AlsoSaveTouchstone() => TouchstoneAsked?.Invoke();

    public event Action? TouchstoneAsked;

    /// <summary>How many sample rows Create writes, and of how many curves — step 6's summary.</summary>
    public string ManualSummary => Session is not { } s ? ""
        : $"{s.Traces.Count(t => t.Manual).ToString(CultureInfo.InvariantCulture)} curve(s), "
          + $"{s.Traces.Where(t => t.Manual).Sum(t => t.Samples.Count).ToString(CultureInfo.InvariantCulture)} sampled point(s)";

    // ── Ignore here (R-ptu6-3) ───────────────────────────────────────────────────────────────────────

    /// <summary>The boxes drawn with Ignore here, every picture's.</summary>
    public IReadOnlyList<PlotIgnoreBox> IgnoreBoxes => _ignore;

    /// <summary>The box clicked on the canvas: Delete removes it.</summary>
    [ObservableProperty] private PlotIgnoreBox? _selectedIgnoreBox;

    /// <summary>
    /// A box drawn on the shown picture goes to the plot whose frame holds its centre, and the session is read again with
    /// it excluded. A box outside every frame is refused in the status line.
    /// </summary>
    public bool Ignore(PixelBox box)
    {
        if (Session is not { } session)
        {
            StatusRaised?.Invoke("The picture is still being read: draw the box once its plots are found.");
            return false;
        }
        var plot = session.Plots.FirstOrDefault(p => p.Picture == ShownPicture && p.Frame.Box.Distance(box.CentreX, box.CentreY) == 0);
        if (plot is null)
        {
            StatusRaised?.Invoke("That box is outside every plot: draw it over a legend or a note inside a plot's frame.");
            return false;
        }
        _ignore.Add(new PlotIgnoreBox(plot.Number, ShownPicture, box));
        OnPropertyChanged(nameof(IgnoreBoxes));
        Changed?.Invoke();
        return true;
    }

    /// <summary>The topmost ignore box on the shown picture under a picture pixel, or null.</summary>
    public PlotIgnoreBox? IgnoreBoxAt(double x, double y) =>
        _ignore.LastOrDefault(b => b.Picture == ShownPicture && b.Box.Distance(x, y) == 0);

    public void RemoveIgnoreBox(PlotIgnoreBox box)
    {
        if (!_ignore.Remove(box)) return;
        if (Equals(SelectedIgnoreBox, box)) SelectedIgnoreBox = null;
        OnPropertyChanged(nameof(IgnoreBoxes));
        Changed?.Invoke();
    }

    private void ForgetIgnoreBoxes()
    {
        _ignore.Clear();
        SelectedIgnoreBox = null;
        OnPropertyChanged(nameof(IgnoreBoxes));
    }

    // ── points on a curve (brief-pdg-3-snap-to-curve.md R-pdg3-2) ────────────────────────────────────

    /// <summary>How near a curve the pointer must be for the hover readout and the Point tool, screen pixels.</summary>
    public const double SnapScreenPx = 8;

    /// <summary>How far a re-read may move a pinned point's curve and still find it, picture pixels.</summary>
    private const double ResnapPx = 6;

    /// <summary>The points pinned, every picture's, in the order pinned.</summary>
    public ObservableCollection<PlotProbePointViewModel> ProbePoints { get; } = [];

    /// <summary>The point clicked on the canvas or in the list: Delete removes it.</summary>
    [ObservableProperty] private PlotProbePointViewModel? _selectedProbePoint;

    /// <summary>The x typed in the Points section, as the axis rows spell one (<c>2.4 GHz</c>).</summary>
    [ObservableProperty] private string _probeXText = "";

    public bool HasProbePoints => ProbePoints.Count > 0;

    private int _nextPoint = 1;

    /// <summary>The nearest traced curve to a point of the shown picture within <paramref name="reachPx"/> picture pixels;
    /// a tie goes to the selected trace's plot.</summary>
    public TraceProbeHit? ProbeAt(double x, double y, double reachPx) =>
        Session is { } s ? TraceProbe.Nearest(s, ShownPicture, x, y, reachPx, SelectedTrace?.Plot) : null;

    /// <summary>What the hover readout says of <paramref name="hit"/>, the trace's fit after it when one is set.</summary>
    public string ProbeText(TraceProbeHit hit) => Session is { } s ? TraceProbe.Readout(s, hit, Assembly?.Fits.GetValueOrDefault(hit.Name)) : "";

    /// <summary>A point pinned on the nearest curve to a click; a click far from every curve is refused in the status line.</summary>
    public bool Pin(double x, double y, double reachPx)
    {
        if (ProbeAt(x, y, reachPx) is not { } hit)
        {
            StatusRaised?.Invoke(Session is null ? "The picture is still being read: pin a point once its traces are found."
                                                 : "That is not on a traced curve: click on a curve the reading traced.");
            return false;
        }
        AddPoint(hit, ShownPicture, null);
        return true;
    }

    /// <summary>The selected trace's value at the x typed in the Points section, pinned (<see cref="TraceProbe.AtX"/>).</summary>
    [RelayCommand]
    private void PinAtX()
    {
        if (TraceProbe.ParseX(ProbeXText) is not { } x)
        {
            StatusRaised?.Invoke("Type an x as the axis rows take one: a frequency with its unit (2.4 GHz), or a number in the axis's own units.");
            return;
        }
        if (SelectedTrace is not { IsChart: false } row || Session is not { } s)
        {
            StatusRaised?.Invoke("Select a trace on rectangular axes first: the x is read on it.");
            return;
        }
        // In Simple mode the row is the colour group: whichever of its pieces reaches x answers.
        IReadOnlyList<PlotTraceRowViewModel> rows = Detailed ? [row] : GroupOf(row)?.Members ?? [row];
        var hit = rows.Select(r => s.Traces.FirstOrDefault(t => t.Name == r.Name)).OfType<PlotTraceRead>()
                      .Select(t => TraceProbe.AtX(s, t.Number, x)).FirstOrDefault(h => h is not null);
        if (hit is null)
        {
            StatusRaised?.Invoke($"{row.Name} does not reach {ProbeXText.Trim()}: a point is read only inside a trace's own range.");
            return;
        }
        AddPoint(hit, s.Plots[hit.Plot - 1].Picture, x);
        ProbeXText = "";
    }

    private void AddPoint(TraceProbeHit hit, int picture, double? typedX)
    {
        var point = new PlotProbePointViewModel(_nextPoint++, picture, RemoveProbePoint) { TypedX = typedX, Px = hit.Px, Py = hit.Py };
        Describe(point, hit);
        ProbePoints.Add(point);
        SelectedProbePoint = point;
        OnPropertyChanged(nameof(HasProbePoints));
    }

    private void Describe(PlotProbePointViewModel point, TraceProbeHit? hit)
    {
        point.Hit = hit;
        point.Text = hit is null ? $"{point.Name} · no traced curve here now" : $"{point.Name} · {ProbeText(hit)}";
        if (hit is null) return;
        point.Px = hit.Px;
        point.Py = hit.Py;
    }

    public void RemoveProbePoint(PlotProbePointViewModel point)
    {
        if (!ProbePoints.Remove(point)) return;
        if (ReferenceEquals(SelectedProbePoint, point)) SelectedProbePoint = null;
        if (ProbePoints.Count == 0) _nextPoint = 1;
        OnPropertyChanged(nameof(HasProbePoints));
    }

    /// <summary>The topmost pinned point on the shown picture within <paramref name="reachPx"/> of a picture pixel, or null.</summary>
    public PlotProbePointViewModel? ProbePointAt(double x, double y, double reachPx) =>
        ProbePoints.LastOrDefault(p => p.Picture == ShownPicture && (p.Px - x) * (p.Px - x) + (p.Py - y) * (p.Py - y) <= reachPx * reachPx);

    /// <summary>Copy: every point as tab-separated text with a header row, values to the picture's resolution.</summary>
    public string PointsTsv()
    {
        var sb = new System.Text.StringBuilder("point\ttrace\tx\tx_unit\ty\ty_unit\n");
        foreach (var p in ProbePoints)
        {
            sb.Append(p.Name).Append('\t');
            if (p.Hit is { } h && Session is { } s)
            {
                var c = TraceProbe.Columns(s, h);
                sb.Append(h.Name).Append('\t').Append(c.X).Append('\t').Append(c.XUnit).Append('\t').Append(c.Y).Append('\t').Append(c.YUnit);
            }
            else sb.Append("\t\t\t\t");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>After a re-read every point snaps again by where it sat on its picture; a typed point is read again at its x
    /// on the curve it lands on.</summary>
    private void Resnap()
    {
        if (Session is not { } s) return;
        foreach (var p in ProbePoints)
        {
            var hit = TraceProbe.Nearest(s, p.Picture, p.Px, p.Py, ResnapPx, SelectedTrace?.Plot);
            if (hit is { Value: null } && p.TypedX is { } x) hit = TraceProbe.AtX(s, hit.Trace, x) ?? hit;
            Describe(p, hit);
        }
    }

    /// <summary>Points on a picture that has gone or changed its pixels are dropped; later pictures' move down.</summary>
    private void ForgetPoints(int picture, bool shift)
    {
        foreach (var p in ProbePoints.Where(p => p.Picture == picture).ToList()) RemoveProbePoint(p);
        if (shift)
            foreach (var p in ProbePoints.Where(p => p.Picture > picture)) p.Picture--;
    }

    private void ForgetAllPoints()
    {
        ProbePoints.Clear();
        SelectedProbePoint = null;
        _nextPoint = 1;
        OnPropertyChanged(nameof(HasProbePoints));
    }

    public TouchstoneAssemblyOptions AssemblyOptions() => new()
    {
        Fill = new Dictionary<(int, int), FillRule>(_fill),
        Phase = _phase.ToDictionary(p => p.Key, p => p.Value.Index switch
        {
            1 => new PhaseRule(MissingPhase.Zero),
            2 => new PhaseRule(MissingPhase.Delay, Seconds(p.Value.Delay) ?? 0),
            _ => PhaseRule.Default,
        }),
        Grid = Grid(),
        Format = FormatIndex switch { 1 => MatrixFormat.MA, 2 => MatrixFormat.RI, _ => MatrixFormat.DB },
        Ports = TypedPorts(),
        Fits = [.. _fit.Where(f => f.Value.Spec is not null).Select(f => new PlotFitOverride(f.Key, f.Value.Spec!))],
    };

    private double? Z0()
    {
        string t = SmithZ0Text.Trim();
        foreach (string unit in new[] { "Ohm", "ohm", "Ω" })
            if (t.EndsWith(unit, StringComparison.Ordinal)) { t = t[..^unit.Length].Trim(); break; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) && z > 0 && double.IsFinite(z) ? z : null;
    }

    private string? GridText() =>
        GridStartText.Trim().Length + GridStopText.Trim().Length + GridPointsText.Trim().Length == 0 ? null : GridStartText + GridStopText + GridPointsText;

    private AssemblyGrid? Grid()
    {
        if (GridText() is null) return null;
        if (Range(GridStartText, GridStopText) is not { } r
            || !int.TryParse(GridPointsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 2) return null;
        return new AssemblyGrid(r.Start, r.Stop, n, GridLog);
    }

    private static (double Start, double Stop)? Range(string start, string stop) =>
        RecognitionSweep.Parse(start.Replace(" ", "")) is { } a && RecognitionSweep.Parse(stop.Replace(" ", "")) is { } b && b.Hz > a.Hz
            ? (a.Hz, b.Hz) : null;

    /// <summary><c>120px=1GHz,900px=5GHz</c> as two clicks; a y value is a plain number in the axis's own units.</summary>
    internal static AxisClicks? Clicks(string text, PlotAxis axis)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        var v = new List<(double P, double V)>();
        foreach (string part in parts)
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) return null;
            string px = part[..eq].Trim();
            if (px.EndsWith("px", StringComparison.OrdinalIgnoreCase)) px = px[..^2];
            string value = part[(eq + 1)..].Replace(" ", "");
            if (!double.TryParse(px, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)) return null;
            double? val = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d
                        : axis == PlotAxis.X && RecognitionSweep.Parse(value) is { } f ? f.Hz : null;
            if (val is not { } got || !double.IsFinite(got)) return null;
            v.Add((p, got));
        }
        return v[0].P == v[1].P ? null : new AxisClicks(v[0].P, v[0].V, v[1].P, v[1].V);
    }

    private static double? Seconds(string s)
    {
        s = s.Replace(" ", "");
        (string Suffix, double Scale)[] units = [("ps", 1e-12), ("ns", 1e-9), ("us", 1e-6), ("µs", 1e-6), ("ms", 1e-3), ("fs", 1e-15), ("s", 1)];
        foreach (var (suffix, scale) in units)
            if (s.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(s[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
                return v * scale;
        return null;
    }

    // ── a tick label clicked on the canvas (R-ptu4-1, R-ptu4-2) ──────────────────────────────────────

    /// <summary>The label editor is open: a tick label was clicked and its value is asked for.</summary>
    [ObservableProperty] private bool _askingLabel;

    /// <summary>The value typed for the clicked label — pre-filled with what was read, if anything.</summary>
    [ObservableProperty] private string _labelText = "";

    /// <summary>Why the typed value was refused, or empty.</summary>
    [ObservableProperty] private string _labelError = "";

    /// <summary>The clicked label: its plot, its axis and its pixel along that axis.</summary>
    public PlotAxisLabel? Label { get; private set; }

    /// <summary><c>y label at 312 px</c>.</summary>
    public string LabelCaption => Label is { } l ? $"{RectAxes.Name(l.Axis)} label at {PlotAxisLabels.Px(l.Pixel)} px" : "";

    /// <summary>A refusal for the dialog's status line.</summary>
    public event Action<string>? StatusRaised;

    /// <summary>Opens the label editor when <paramref name="item"/> is a tick label of a rectangular plot on the shown
    /// picture, read or not; closes it otherwise. True when it opened.</summary>
    public bool AskLabel(ImageOverlayItem? item)
    {
        Label = Session is { } s && item is not null ? PlotAxisLabels.Find(s, ShownPicture, item) : null;
        LabelText = Label?.Read ?? "";
        LabelError = "";
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(LabelCaption));
        AskingLabel = Label is not null;
        return AskingLabel;
    }

    /// <summary>The typed value becomes one <c>&lt;px&gt;px=&lt;value&gt;</c> pair of its plot's axis row; the second pair
    /// completes the axis and a third replaces the older. Null when committed, else why not.</summary>
    public string? CommitLabel()
    {
        if (Label is not { } l) return null;
        var plot = Session?.Plots.FirstOrDefault(p => p.Number == l.Plot);
        string existing = _axis.GetValueOrDefault((l.Plot, l.Axis), "");
        string? why = $"Plot {l.Plot.ToString(CultureInfo.InvariantCulture)} is no longer read.";
        if ((plot is null ? null : PlotAxisLabels.Value(LabelText, l.Axis, plot, existing, out why)) is not { } value)
        {
            LabelError = why ?? "";
            StatusRaised?.Invoke(LabelError);
            return LabelError;
        }
        string text = PlotAxisLabels.Merge(existing, l.Axis, l.Pixel, value);
        AskingLabel = false;
        LabelError = "";
        if (Frames.FirstOrDefault(f => f.Number == l.Plot) is { } row)
            row.SetText(l.Axis, text);   // OnFrameEdited holds it and reads again
        else
        {
            _axis[(l.Plot, l.Axis)] = text;
            AxisTextChanged?.Invoke();
            Changed?.Invoke();
        }
        return null;
    }

    [RelayCommand] private void SetLabel() => CommitLabel();

    [RelayCommand] private void CancelLabel() => AskingLabel = false;

    /// <summary>
    /// <paramref name="overlay"/> with each tick label a value was typed for showing that value rather than what was read
    /// (PTU-7: a label misread as <c>2.n</c> and typed as 2.4 reads 2.4 on the canvas). The typed value is the axis row's
    /// pair at the label's tick, so a value typed into the row itself relabels its label too.
    /// </summary>
    public ImageOverlay Relabel(ImageOverlay overlay)
    {
        if (Session is not { } s || _axis.Values.All(t => t.Trim().Length == 0)) return overlay;
        bool any = false;
        var items = overlay.Items.Select(item =>
        {
            if (PlotAxisLabels.Find(s, ShownPicture, item) is not { } l) return item;
            var pairs = PlotAxisLabels.Pairs(_axis.GetValueOrDefault((l.Plot, l.Axis), ""), l.Axis);
            if (pairs.FirstOrDefault(p => Math.Abs(p.Px - l.Pixel) < 1) is not { Value: { } value }) return item;
            any = true;
            return item with { Label = PlotAxisLabels.Shown(value, l.Read), Unknown = false };
        }).ToList();
        return any ? overlay.WithItems(items) : overlay;
    }

    // ── a reading arrived ────────────────────────────────────────────────────────────────────────────

    /// <summary>The rows, rebuilt from what came back, with the user's choices laid over them.</summary>
    public void Apply(PlotSession session, TouchstoneAssemblyResult assembly)
    {
        Session = session;
        Assembly = assembly;

        // The plot chosen is held by number across a re-read; a new reading starts on its first plot on the shown picture.
        int? plotChosen = SelectedFrame?.Number;
        Frames.Clear();
        foreach (var p in session.Plots)
            Frames.Add(new PlotFrameRowViewModel(p, !_excluded.Contains(p.Number), _axis.GetValueOrDefault((p.Number, PlotAxis.X), ""),
                                                 _axis.GetValueOrDefault((p.Number, PlotAxis.Y), ""), () => OnFrameEdited(p.Number),
                                                 _axis.GetValueOrDefault((p.Number, PlotAxis.Y2), ""),
                                                 session.Traces.Any(t => t.Plot == p.Number && t.Axis == PlotSide.Right),
                                                 a => _log.TryGetValue((p.Number, a), out bool l) ? l : null,
                                                 (a, l) => { _log[(p.Number, a)] = l; Changed?.Invoke(); }));

        // The selection is held by name across a re-read, as the choices are: the rows are new objects.
        string? chosen = SelectedTrace?.Name;
        Traces.Clear();
        foreach (var t in session.Traces)
        {
            var (start, stop) = _freq.GetValueOrDefault(t.Name, ("", ""));
            Traces.Add(new PlotTraceRowViewModel(t, session.Plots[t.Plot - 1], start, stop, OnTraceEdited, EntryChoices,
                                                 _fit.GetValueOrDefault(t.Name, new(0, "", "")), assembly.Fits.GetValueOrDefault(t.Name), MakeCsv));
        }
        Groups.Clear();
        foreach (var (members, debris) in PlotColourGroups.Group(session))
        {
            var rows = members.Select(m => TraceRow(m.Name)!).ToList();
            Groups.Add(new PlotTraceGroupViewModel(rows, PlotColourGroups.Title(session, members), debris, Batch));
        }
        SelectedFrame = (plotChosen is { } n ? FrameRow(n) : null)
                        ?? Frames.FirstOrDefault(f => f.Picture - 1 == ShownPicture) ?? Frames.FirstOrDefault();
        SyncChoices();   // the highest port read may have moved
        ListShown();
        SelectedTrace = TraceRow(chosen);
        SelectedGroup = GroupOf(SelectedTrace);

        Matrix.Clear();
        foreach (var c in assembly.Matrix)
        {
            bool missingPhase = c.Phase.Length > 0;
            int selected = missingPhase
                ? _phase.TryGetValue((c.I, c.J), out var ph) ? ph.Index : 0
                : _fill.TryGetValue((c.I, c.J), out var fr) ? FillIndex(c.I, c.J, assembly.Ports, fr) : 0;
            var cell = new PlotMatrixCellViewModel(c, assembly.Ports, missingPhase, selected, OnMatrixEdited);
            if (missingPhase && _phase.TryGetValue((c.I, c.J), out var held)) cell.DelayText = held.Delay;
            Matrix.Add(cell);
        }

        ReportLines.Clear();
        foreach (string line in session.Lines()) ReportLines.Add(line);
        foreach (string line in assembly.Report) ReportLines.Add(line);
        if (ShownPicture >= Pictures.Count) ShownPicture = Math.Max(0, Pictures.Count - 1);
        Resnap();
        RefreshDerived();
    }

    private static int FillIndex(int i, int j, int ports, FillRule rule)
    {
        var choices = PlotMatrixCellViewModel.FillChoices(i, j, ports);
        string want = rule switch
        {
            FillRule.Zero => "zero",
            FillRule.Unassigned => "unassigned",
            FillRule.Reciprocal => $"= S{j}{i}",
            FillRule.Symmetric => $"= S{3 - i}{3 - i}",
            _ => "default",
        };
        return Math.Max(0, choices.ToList().IndexOf(want));
    }

    private void OnFrameEdited(int plot)
    {
        var row = Frames.FirstOrDefault(f => f.Number == plot);
        if (row is null) return;
        if (row.Include) _excluded.Remove(plot); else _excluded.Add(plot);
        _axis[(plot, PlotAxis.X)] = row.XAxisText;
        _axis[(plot, PlotAxis.Y)] = row.YAxisText;
        _axis[(plot, PlotAxis.Y2)] = row.Y2AxisText;
        AxisTextChanged?.Invoke();
        Changed?.Invoke();
    }

    private void OnTraceEdited(PlotTraceRowViewModel row)
    {
        if (row.Edited || row.AxisEdited || row.FitEdited)
        {
            var before = _quiet > 0 || _undoing ? null : State();
            if (row.Edited)
            {
                if (row.Assignment is { } a) _assign[row.Name] = a;
                else _assign.Remove(row.Name);
            }
            if (row.AxisEdited) _side[row.Name] = row.Side;
            // Pieces fitted together share one fit (R-pdg2-3): an edit on one is an edit on all of them.
            if (row.FitEdited)
                foreach (string name in Session is { } s ? TraceFit.FittedWith(s, row.Name) : [row.Name])
                    _fit[name] = row.Choice;
            if (before is not null && !Unchanged(before)) Remember(before);
        }
        _freq[row.Name] = (row.StartText, row.StopText);
        if (_quiet > 0) _pending = true;
        else Changed?.Invoke();
    }

    private int _quiet;
    private bool _pending;

    /// <summary>Several pieces assigned at once (a group's combo, R-ptu5-2): one re-read, not one a piece.</summary>
    private void Batch(Action edit)
    {
        var before = _quiet == 0 && !_undoing ? State() : null;
        _quiet++;
        try { edit(); }
        finally { _quiet--; }
        if (before is not null && !Unchanged(before)) Remember(before);
        if (_quiet > 0 || !_pending) return;
        _pending = false;
        Changed?.Invoke();
    }

    /// <summary>The Touchstone of a reading, every set of pieces assigned alike joined first (R-ptu5-1) — the dialog's one
    /// way to the assembly. The joins lead the assembly's report.</summary>
    public static TouchstoneAssemblyResult Assemble(PlotSession session, Func<PlotSession, TouchstoneAssemblyResult> build)
    {
        var (joined, lines) = TraceJoin.Session(session);
        var result = build(joined);
        return lines.Count == 0 ? result : result with { Report = [.. lines.Select(l => $"joined {l}"), .. result.Report] };
    }

    private void OnMatrixEdited(PlotMatrixCellViewModel cell)
    {
        if (cell.IsPhase) _phase[(cell.I, cell.J)] = (cell.SelectedIndex, cell.DelayText);
        else if (cell.Fill is { } f) _fill[(cell.I, cell.J)] = f;
        Changed?.Invoke();
    }

    // ── what the dialog shows of it ──────────────────────────────────────────────────────────────────

    /// <summary>Why no Touchstone can be made yet — the refusal's sentence and the control that answers it — or null.</summary>
    public string? Refusal => Assembly?.Refusal is { } r ? $"{r.Sentence} — {r.Control}." : null;

    /// <summary>The refusal's kind, so its control is highlighted.</summary>
    public string RefusalKind => Assembly?.Refusal?.Kind ?? "";

    /// <summary>What the Touchstone waits for is said, and its control highlighted, only while one is being made (R-pdg4-6).</summary>
    public bool RefusalShown => !MakeCsv && Refusal is not null;

    public bool HighlightTraces => !MakeCsv && RefusalKind is "unassigned" or "frequency" or "empty";
    public bool HighlightMatrix => !MakeCsv && RefusalKind == "fill";
    public bool HighlightGrid => !MakeCsv && RefusalKind == "range";
    public bool HighlightFrames => !MakeCsv && RefusalKind == "axis";

    /// <summary>The file's ports, for the matrix grid's columns.</summary>
    public int Ports => Assembly?.Ports ?? 0;

    /// <summary>The matrix grid is shown: there is one, and Make is Touchstone (R-pdg4-6).</summary>
    public bool HasMatrix => Matrix.Count > 0 && !MakeCsv;
    public bool HasSmith => Session?.Plots.Any(p => p.Type == PlotType.Smith) == true;
    public bool HasTraces => Session?.Traces.Count > 0;

    /// <summary>The default file name: the first picture's, with the extension the reading makes.</summary>
    public string DefaultFileName(bool csv) =>
        (Pictures.Count == 0 ? "plot" : System.IO.Path.GetFileNameWithoutExtension(Pictures[0].Source.OriginalName))
        + (csv ? ".csv" : Assembly?.Extension ?? ".s2p");

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(Refusal));
        OnPropertyChanged(nameof(RefusalKind));
        OnPropertyChanged(nameof(RefusalShown));
        OnPropertyChanged(nameof(HighlightTraces));
        OnPropertyChanged(nameof(HighlightMatrix));
        OnPropertyChanged(nameof(HighlightGrid));
        OnPropertyChanged(nameof(HighlightFrames));
        OnPropertyChanged(nameof(Ports));
        OnPropertyChanged(nameof(HasMatrix));
        OnPropertyChanged(nameof(HasSmith));
        OnPropertyChanged(nameof(HasTraces));
        OnPropertyChanged(nameof(TouchstoneOffered));
        OnPropertyChanged(nameof(ManualSummary));
    }
}

/// <summary>How an overlay item names its trace (R-ptu3-1): <c>trace:T3</c> is T3, and a bridged gap <c>gap:T3:17</c> is
/// T3's too. The reader builds the ids (ImageOverlay.BuildPlot); this side only reads them.</summary>
internal static class PlotTraceLink
{
    public static string? TraceName(ImageOverlayItem? item)
    {
        if (item is not { Class: ImageOverlayClass.Trace }) return null;
        if (item.Id.StartsWith(ImageOverlay.TracePrefix, StringComparison.Ordinal)) return item.Id[ImageOverlay.TracePrefix.Length..];
        if (!item.Id.StartsWith(ImageOverlay.GapPrefix, StringComparison.Ordinal)) return null;
        string rest = item.Id[ImageOverlay.GapPrefix.Length..];
        int colon = rest.LastIndexOf(':');
        return colon > 0 ? rest[..colon] : null;
    }

    /// <summary>The whole trace's item on <paramref name="overlay"/>, or null when it is not drawn there.</summary>
    public static ImageOverlayItem? ItemFor(ImageOverlay overlay, string? name) =>
        name is null ? null : overlay.Items.FirstOrDefault(i => i.Class == ImageOverlayClass.Trace && i.Id == ImageOverlay.TracePrefix + name);
}

/// <summary>The traces table's colour groups (R-ptu5-2). Per plot, longest piece first, each piece joins the first group of
/// its colour (within the reader's own colour-merge ΔE) whose x coverage it overlaps by less than <see cref="Overlap"/> of
/// its own span, else starts one — so two black curves over the same band stay two rows and the pieces of one red curve
/// are one. A chart trace is a group of its own. Longest total first; a group shorter in total than the reader's debris
/// length (<see cref="PlotTraces.MinTrackShare"/> of the axis) last.</summary>
internal static class PlotColourGroups
{
    public const double Overlap = 0.05;

    private static readonly double SameColour = new ColourClusterOptions().MergeDeltaE;

    private sealed class Building(PlotTraceRead first, double lo, double hi)
    {
        public Lab Colour { get; } = CieLab.FromRgb(first.Curve.Rgb);
        public List<PlotTraceRead> Members { get; } = [first];
        public List<(double Lo, double Hi)> Cover { get; } = [(lo, hi)];
    }

    public static List<(List<PlotTraceRead> Members, bool Debris)> Group(PlotSession session)
    {
        var all = new List<(List<PlotTraceRead> Members, double Length, bool Debris)>();
        foreach (var plot in session.Traces.GroupBy(t => t.Plot))
        {
            var groups = new List<Building>();
            foreach (var t in plot.OrderByDescending(Length).ThenBy(t => t.Number))
            {
                var (lo, hi) = Span(t);
                var lab = CieLab.FromRgb(t.Curve.Rgb);
                var into = t.Parametric ? null : groups.FirstOrDefault(g =>
                    !g.Members[0].Parametric && g.Colour.DeltaE(lab) <= SameColour && Covered(g.Cover, lo, hi) < Overlap * Math.Max(hi - lo, 1));
                if (into is null) { groups.Add(new Building(t, lo, hi)); continue; }
                into.Members.Add(t);
                into.Cover.Add((lo, hi));
            }
            var frame = session.Plots[plot.Key - 1].Frame;
            double axis = frame.Right > frame.Left ? frame.Right - frame.Left : 2 * frame.Radius;
            foreach (var g in groups)
            {
                double length = g.Members.Sum(Length);
                bool debris = length < PlotTraces.MinTrackShare * axis;
                all.Add(([.. g.Members.OrderBy(m => Span(m).Lo).ThenBy(m => m.Number)], length, debris));
            }
        }
        return [.. all.OrderBy(g => g.Debris).ThenByDescending(g => g.Length).Select(g => (g.Members, g.Debris))];
    }

    /// <summary><c>Red · 4 pieces · 2.3–2.6 GHz</c>, and the plot when the session has several.</summary>
    public static string Title(PlotSession session, IReadOnlyList<PlotTraceRead> members)
    {
        string pieces = members.Count == 1 ? "1 piece" : $"{members.Count.ToString(CultureInfo.InvariantCulture)} pieces";
        var xs = members.SelectMany(m => m.Samples).Select(s => s.X).Where(x => !double.IsNaN(x)).ToList();
        var plot = session.Plots[members[0].Plot - 1];
        string range = xs.Count == 0 ? "no frequency" : Range(xs.Min(), xs.Max(), plot.Rect is not { XIsFrequency: false });
        string where = session.Plots.Count > 1 ? $" · plot {plot.Number.ToString(CultureInfo.InvariantCulture)}" : "";
        return $"{Word(members[0].Curve.Rgb)} · {pieces} · {range}{where}";
    }

    private static string Range(double lo, double hi, bool hz)
    {
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        if (!hz) return $"{F(lo)}–{F(hi)}";
        var (div, unit) = hi >= 1e9 ? (1e9, "GHz") : hi >= 1e6 ? (1e6, "MHz") : hi >= 1e3 ? (1e3, "kHz") : (1, "Hz");
        return $"{F(lo / div)}–{F(hi / div)} {unit}";
    }

    /// <summary>A colour's everyday name, from its Lab hue: what a legend would call it.</summary>
    public static string Word(int rgb)
    {
        var lab = CieLab.FromRgb(rgb);
        double chroma = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
        if (chroma < 12) return lab.L < 25 ? "Black" : lab.L > 85 ? "White" : "Grey";
        double h = Math.Atan2(lab.B, lab.A) * 180 / Math.PI;
        if (h < 0) h += 360;
        return h switch
        {
            < 45 or >= 345 => "Red",
            < 85 => "Orange",
            < 115 => "Yellow",
            < 180 => "Green",
            < 250 => "Cyan",
            < 315 => "Blue",
            _ => lab.L < 40 ? "Purple" : "Magenta",
        };
    }

    private static double Length(PlotTraceRead t)
    {
        var (lo, hi) = Span(t);
        return hi - lo;
    }

    /// <summary>The piece's x extent in pixels, along its drawn points.</summary>
    private static (double Lo, double Hi) Span(PlotTraceRead t) =>
        t.Curve.Points.Count == 0 ? (0, 0) : (t.Curve.Points.Min(p => p.X), t.Curve.Points.Max(p => p.X));

    private static double Covered(List<(double Lo, double Hi)> cover, double lo, double hi) =>
        cover.Sum(c => Math.Max(0, Math.Min(hi, c.Hi) - Math.Max(lo, c.Lo)));
}

/// <summary>A plot's calibration in words, as the matrix grid and the frames list show it.</summary>
internal static class TouchstoneAssemblyText
{
    public static string Calibration(PlotRead p) => p.Type switch
    {
        PlotType.Rectangular when p.Rect is { } r =>
            $"x {Axis(r.X)} · y{(r.YSide == PlotSide.Right ? " (right)" : "")} {Axis(r.Y)}"
            + (r.TwoY ? $" · y2 (right) {Axis(r.Y2)}" : "") + (r.XIsFrequency && !r.XUnitStated ? " · GHz assumed" : ""),
        PlotType.Smith when p.Smith is { } s => $"Z0 {s.Z0.ToString("0.###", CultureInfo.InvariantCulture)} Ω{(s.Z0Stated ? "" : " (assumed)")}"
                                                + (s.Admittance ? " · admittance chart" : ""),
        PlotType.Polar when p.Polar is { } pl => pl.Db ? "dB radius" : "linear radius",
        _ => "",
    };

    private static string Axis(AxisCalibration? a) => a is null ? "uncalibrated"
        : $"{a.Scale.ToString().ToLowerInvariant()}{(a.Manual ? " (typed)" : $", {a.Used.ToString(CultureInfo.InvariantCulture)} labels")}";
}

/// <summary>A tick label clicked on the canvas (R-ptu4-1): its plot, the axis it labels, its pixel along that axis — at
/// its tick, as the reader pairs a label with its tick — what was read of it, and where the editor stands.</summary>
public sealed record PlotAxisLabel(int Plot, PlotAxis Axis, double Pixel, string Read, double X, double Y);

/// <summary>The dialog's own lookup of a clicked tick label and the axis-row text it becomes (R-ptu4-1, R-ptu4-2). The
/// reader is not asked: the label's place against its frame says which axis it labels, and the pairs are the typed row's
/// <c>120px=1GHz,900px=5GHz</c> grammar — the CLI's — so a click and a typed row are one way in.</summary>
internal static class PlotAxisLabels
{
    private static readonly Regex Leading = new(@"^[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d{1,3})?", RegexOptions.CultureInvariant);

    private static readonly (string Name, double Scale)[] Units = [("Hz", 1), ("kHz", 1e3), ("MHz", 1e6), ("GHz", 1e9), ("THz", 1e12)];

    public static string Px(double p) => p.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The tick label <paramref name="item"/> is — a label the reader read (<c>tick:</c>) or a word it boxed and
    /// could not read (<c>text:</c>) — on a rectangular plot of picture <paramref name="picture"/>; null for anything
    /// else. Below or above its frame it labels x; left of it, y; right of it, y2 — or y on a plot whose only y axis is on
    /// the right (R-pdg1-3); at a corner, the side it stands further out.</summary>
    public static PlotAxisLabel? Find(PlotSession session, int picture, ImageOverlayItem item)
    {
        if (item.Class != ImageOverlayClass.Text) return null;
        var rects = session.Plots.Where(p => p.Picture == picture && p.Type == PlotType.Rectangular).ToList();
        string[] id = item.Id.Split(':');
        PixelBox box;
        string read;
        if (id is ["tick", var n, var k] && int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            && int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && rects.FirstOrDefault(p => p.Number == number) is { Rect: { } r } plot)
        {
            var labels = index >= 2000 ? r.Y2Labels : index >= 1000 ? r.YLabels : r.XLabels;
            index %= 1000;
            if (index >= labels.Count) return null;
            (box, read) = (labels[index].Box, labels[index].Text);
            rects = [plot];
        }
        else if (id is ["text", var w] && int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out int word)
                 && session.Words.TryGetValue(picture, out var words) && words.FirstOrDefault(x => x.Id == word) is { } found)
            (box, read) = (found.Box, found.Text);
        else return null;

        PlotAxisLabel? best = null;
        double nearest = double.MaxValue;
        foreach (var p in rects)
        {
            var f = p.Frame;
            double cx = box.CentreX, cy = box.CentreY;
            double dx = cx < f.Left ? f.Left - cx : cx > f.Right ? cx - f.Right : 0;
            double dy = cy > f.Bottom ? cy - f.Bottom : cy < f.Top ? f.Top - cy : 0;
            double d = Math.Max(dx, dy);
            // Inside the frame, or too far from it to be one of its labels.
            if (d == 0 || d > 0.3 * Math.Max(f.Right - f.Left, f.Bottom - f.Top) || d >= nearest) continue;
            nearest = d;
            bool x = dy >= dx, right = !x && cx > f.Right;
            var axis = x ? PlotAxis.X : right && p.Rect?.YSide != PlotSide.Right ? PlotAxis.Y2 : PlotAxis.Y;
            best = new PlotAxisLabel(p.Number, axis, AtTick(box, x ? f.XTicks : right ? f.Y2Ticks : f.YTicks, x), read, box.Right + 1, box.Bottom + 1);
        }
        return best;
    }

    /// <summary>The label's pixel along its axis: the nearest tick within half its extent, else its own centre — the
    /// reader's pairing (RectAxes), so a clicked value lands where a read one would.</summary>
    private static double AtTick(PixelBox b, IReadOnlyList<double> ticks, bool horizontal)
    {
        double centre = (horizontal ? b.CentreX : b.CentreY) - 0.5;
        double half = 0.5 * (horizontal ? b.Width : b.Height) + 1;
        double at = centre, best = double.MaxValue;
        foreach (double t in ticks)
            if (Math.Abs(t - centre) <= half && Math.Abs(t - centre) < best) { best = Math.Abs(t - centre); at = t; }
        return at;
    }

    /// <summary>The well-formed <c>px=value</c> pairs of an axis row, in order.</summary>
    public static List<(double Px, string Value)> Pairs(string text, PlotAxis axis)
    {
        var pairs = new List<(double, string)>();
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string px = part[..eq].Trim();
            if (px.EndsWith("px", StringComparison.OrdinalIgnoreCase)) px = px[..^2];
            string value = part[(eq + 1)..].Replace(" ", "");
            if (double.TryParse(px, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                && (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || axis == PlotAxis.X && RecognitionSweep.Parse(value) is not null))
                pairs.Add((p, value));
        }
        return pairs;
    }

    /// <summary>A typed value as its label shows it: with its unit when the picture's label carried one (<c>2.4 GHz</c>),
    /// else the number alone, as the picture's other labels read.</summary>
    public static string Shown(string value, string read)
    {
        if (read.Contains("Hz", StringComparison.OrdinalIgnoreCase)) return value;
        var m = Leading.Match(value);
        return m.Success ? m.Value : value;
    }

    /// <summary>The row with the clicked pair in it: a pair at the same pixel is replaced; with two already, the older goes.</summary>
    public static string Merge(string existing, PlotAxis axis, double pixel, string value)
    {
        var pairs = Pairs(existing, axis);
        pairs.RemoveAll(p => Math.Abs(p.Px - pixel) < 1);
        while (pairs.Count >= 2) pairs.RemoveAt(0);
        pairs.Add((pixel, value));
        return string.Join(",", pairs.Select(p => $"{Px(p.Px)}px={p.Value}"));
    }

    /// <summary>A typed label value as the row spells it, or null with <paramref name="why"/>. A y value is a number in the
    /// axis's own units (a unit word after it is dropped); an x value on a frequency axis takes a unit — a bare number
    /// takes the unit the other x labels were read in, else is refused.</summary>
    public static string? Value(string typed, PlotAxis axis, PlotRead plot, string existing, out string? why)
    {
        string t = typed.Replace(" ", "");
        why = null;
        if (axis != PlotAxis.X)
        {
            var m = Leading.Match(t);
            if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && double.IsFinite(y)
                && t[m.Length..].All(c => char.IsLetter(c) || c is '°' or '%'))
                return m.Value;
            why = "Type the label's value: -10.";
            return null;
        }
        if (RecognitionSweep.Parse(t) is { } f) return f.Text + f.Unit;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.IsFinite(x))
        {
            why = "Type the label's value with its unit: 2.4 GHz.";
            return null;
        }
        if (plot.Rect is { XIsFrequency: false }) return t;
        if (XUnit(plot, existing) is { } unit) return t + unit;
        why = "Say the unit: 2.4 GHz.";
        return null;
    }

    /// <summary>The unit the x labels were read in: a clicked pair's, the title's or a label's own suffix; null when
    /// none was read (the GHz the reader assumes is not one).</summary>
    private static string? XUnit(PlotRead plot, string existing)
    {
        foreach (var (_, v) in Pairs(existing, PlotAxis.X))
            if (RecognitionSweep.Parse(v) is { } f) return f.Unit;
        if (plot.Rect is not { XUnitStated: true } r) return null;
        if (r.XUnit != 1) return Name(r.XUnit);
        foreach (var l in r.XLabels)
        {
            string text = l.Text.Replace(" ", "");
            var m = Leading.Match(text);
            if (m.Success && m.Length < text.Length && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                && n != 0 && Name(l.Value / n) is { } u)
                return u;
        }
        return null;
    }

    private static string? Name(double scale) => Units.FirstOrDefault(u => Math.Abs(scale / u.Scale - 1) < 1e-6).Name;
}
