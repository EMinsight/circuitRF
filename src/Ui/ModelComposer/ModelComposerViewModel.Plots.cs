// The plots panel (brief-modelcomposer-14 R-mc14-6 … R-mc14-9; overview §3): PlotControls of the Data Display, fed from
// the loaded data — never a bespoke chart. A tab strip per step; the bias cursor picks the point every frequency plot
// is drawn at. The traces are this window's, rebuilt from the data on every change (Plot.IsFixedReadout), exactly as
// railRF's results plot is.
//
// Two containers, because a plot's type is fixed when it is made: a rectangular one, and a Smith chart for the tabs that
// show reflection.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    /// <summary>The plots' host — not a Data Display document: nothing is persisted and no plot is added or deleted.</summary>
    public DataDisplayViewModel PlotHost { get; private set; } = null!;

    public PlotContainerViewModel RectContainer { get; private set; } = null!;
    public PlotContainerViewModel SmithContainer { get; private set; } = null!;
    public Plot RectPlot => RectContainer.PlotVM.Plot;
    public Plot SmithPlot => SmithContainer.PlotVM.Plot;

    /// <summary>The bias plane's click-and-lasso, set on the rectangular plot while the bias plane shows.</summary>
    public BiasPlaneLasso BiasPlaneLasso { get; private set; } = null!;

    /// <summary>How many times the plots were rebuilt — a counter, for the gate.</summary>
    public int PlotBuilds { get; private set; }

    private void BuildPlotHost()
    {
        PlotHost = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);
        RectContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 0, width: 320, height: 198);
        SmithContainer = PlotHost.AddPlot(PlotType.Smith, FreqUnit.GHz, left: 0, top: 0, width: 320, height: 320);
        foreach (var p in new[] { RectPlot, SmithPlot })
        {
            p.IsFixedReadout = true;
            p.Axes.LockedPanning = true;
            p.Axes.FontSizeLabel = TitleFontSizeLabel(p.Axes);
        }
        PlotHost.SelectOnly((PlotContainerViewModel?)null);
        // The band the info boxes sit in is reserved whether or not a marker exists (owner, review 4): sized to the boxes,
        // it moved the axes down when the first marker was made and back up when the last one went.
        RectPlot.TopBandPx = BandHeight();
        BiasPlaneLasso = new BiasPlaneLasso(() => _biasPlanePoints, OnLasso, OnPickBiasPoint, () => _biasPlanePickable);
        RebuildPlotTabs();
    }

    /// <summary>
    /// The title a tenth larger than the axis labels. The renderer draws a title at <c>FontSizeLabel × 1.4</c> and an axis
    /// label at <c>FontSizeTicks × 0.9</c>, which left the title twice the labels' size in a panel this small; harmonicaRF
    /// sets <c>FontSizeLabel</c> for its own title for the same reason (<c>HarmonicaPanelRenderer.RectTitleFontSizeLabel</c>).
    /// </summary>
    private static double TitleFontSizeLabel(Axes axes) => axes.FontSizeTicks * 0.9 * 1.1 / 1.4;

    // ── tabs ─────────────────────────────────────────────────────────────────

    public const string OutputTab = "Output", TransferTab = "Transfer", BiasPlaneTab = "Bias plane", STab = "S", SmithTab = "Smith",
                        CapacitanceTab = "C", ConductanceTab = "G", FlatnessTab = "Flatness";

    public IReadOnlyList<string> PlotTabs { get; private set; } = [];

    private string _selectedPlotTab = "";

    public string SelectedPlotTab
    {
        get => _selectedPlotTab;
        set
        {
            if (value is null || value == _selectedPlotTab || !PlotTabs.Contains(value)) { OnPropertyChanged(); return; }
            _selectedPlotTab = value;
            // The overlay BEFORE the notification: the plots panel hands the container's overlay to its PlotControl when
            // the tab changes, and set after, the bias plane's control was always handed the previous tab's — none — so a
            // click or a lasso there reached nothing.
            RectContainer.Overlay = IsBiasPlaneTab ? BiasPlaneLasso : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSmithTab));
            OnPropertyChanged(nameof(IsBiasPlaneTab));
            RebuildPlots();
        }
    }

    public bool IsSmithTab => SelectedPlotTab == SmithTab;
    public bool IsBiasPlaneTab => SelectedPlotTab == BiasPlaneTab;

    private void RebuildPlotTabs()
    {
        IReadOnlyList<string> tabs = IsPassive ? [STab, SmithTab] : ModelStepTabs() ?? StepName switch
        {
            "Data" => [OutputTab, TransferTab, BiasPlaneTab, STab, SmithTab],
            "Pads" => [STab, SmithTab],
            "Network" => [CapacitanceTab, ConductanceTab, FlatnessTab, BiasPlaneTab],
            _ => [BiasPlaneTab],
        };
        if (tabs.SequenceEqual(PlotTabs)) return;
        PlotTabs = tabs;
        OnPropertyChanged(nameof(PlotTabs));
        string keep = tabs.Contains(_selectedPlotTab) ? _selectedPlotTab : tabs[0];
        _selectedPlotTab = "";
        SelectedPlotTab = keep;
    }

    // ── bias plane options ───────────────────────────────────────────────────

    private bool _showExtrinsic = true, _showIntrinsic = true;

    public bool ShowExtrinsic { get => _showExtrinsic; set { if (SetProperty(ref _showExtrinsic, value)) RebuildPlots(); } }
    public bool ShowIntrinsic { get => _showIntrinsic; set { if (SetProperty(ref _showIntrinsic, value)) RebuildPlots(); } }

    [CommunityToolkit.Mvvm.Input.RelayCommand] private void ToggleExtrinsic() => ShowExtrinsic = !ShowExtrinsic;
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void ToggleIntrinsic() => ShowIntrinsic = !ShowIntrinsic;
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void ToggleExclude() => ExcludeArmed = !ExcludeArmed;
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void SelectPlotTab(string tab) => SelectedPlotTab = tab;

    private IReadOnlyList<BiasPlanePoint> _biasPlanePoints = [];

    /// <summary>Every point a click can pick: each set's extrinsic and intrinsic point where both are drawn, since either is
    /// the same bias.</summary>
    private IReadOnlyList<BiasPlanePoint> _biasPlanePickable = [];

    /// <summary>
    /// A click on a bias point with the lasso disarmed (owner, review 3): the cursor moves to it — the green dot and the
    /// toolbar's Vgs and Vds — and the plots follow. A point of another data set selects that set first, since the cursor's
    /// lists are the selected set's. A double-click is the plot's own marker gesture and is not taken here.
    /// </summary>
    private void OnPickBiasPoint(BiasPlanePoint picked) => PostToUi(() =>
    {
        if (!Loaded.Data.TryGetValue(picked.Set, out var d) || d.Points.FirstOrDefault(p => p.PointId == picked.Point) is not { } point) return;
        _cursorOnExtrinsic = Math.Abs(picked.X - point.Vds) < 1e-9 && Math.Abs(picked.Y - point.Vgs) < 1e-9;
        if (SelectedDataSet?.Name != picked.Set && DataSetRows.FirstOrDefault(r => r.Name == picked.Set) is { } row) SelectedDataSet = row;
        _syncingCursor = true;
        try
        {
            SelectedVgs = Snap(VgsChoices, point.Vgs);
            SelectedVds = Snap(VdsChoices, point.Vds);
        }
        finally { _syncingCursor = false; }
        SetView(Document.View with { Cursor = CurrentCursor() });
        RebuildPlots();
    });

    /// <summary>Whether the cursor's dot sits on its point's extrinsic circle — the one clicked — rather than its intrinsic one.</summary>
    private bool _cursorOnExtrinsic;

    /// <summary>What a click or lasso on the bias plane took: the network fit's points on the Network step, else exclusions.</summary>
    private void OnLasso(IReadOnlyList<(string Set, string Point)> points) =>
        PostToUi(() => { if (IsNetworkStep) ToggleFitPoints(points); else ToggleExclusion(points); });

    // ── building ─────────────────────────────────────────────────────────────

    // ── markers (owner, review 3): first class, so they outlive the rebuilds ──

    /// <summary>
    /// The markers the user put on each tab's traces, by step, tab and trace name. Every cursor move, edit and reload rebuilds
    /// the traces, so a marker kept on the trace object alone was gone at the next click. The markers move across as
    /// OBJECTS — a copy would lose its m-number, its box position and its selection — onto the rebuilt trace of the same
    /// name, as railRF's results plot carries its own.
    /// </summary>
    private readonly Dictionary<string, List<Marker>> _markers = new(StringComparer.Ordinal);

    /// <summary>The step and tab the traces now on the plots were built for.</summary>
    private string _plottedKey = "";

    /// <summary>The axes window the last rebuild left; a window that differs from it is one the user zoomed or panned.</summary>
    private (PlotRect Primary, PlotRect Secondary) _framed;

    private bool WindowIsOurs(Plot plot) => _framed == (plot.Axes.Window, plot.Axes.WindowSecondary);

    private static string MarkerKey(string tabKey, Trace t) => $"{tabKey}|{t.CubeName ?? t.QuantityName}";

    /// <summary>Puts the markers on <paramref name="plot"/>'s traces away under the tab they were built for.</summary>
    private void StashMarkers(Plot plot)
    {
        if (_plottedKey.Length == 0) return;
        foreach (var t in plot.Traces)
            _markers[MarkerKey(_plottedKey, t)] = [.. t.Markers];
    }

    /// <summary>Gives each rebuilt trace the markers its namesake on this tab carried.</summary>
    private void RestoreMarkers(Plot plot, string key)
    {
        foreach (var t in plot.Traces)
            if (!t.IsAnnotation && _markers.TryGetValue(MarkerKey(key, t), out var kept))
                foreach (var m in kept) t.Markers.Add(m);
    }

    /// <summary>The space round an info box in the band, canvas pixels.</summary>
    private const double BandGap = 6;

    /// <summary>
    /// A new marker's info box, placed in the band between the title and the axes (owner, review 3) — after the boxes
    /// already there, left to right from the axes' left edge, starting over at the left when the band is full — rather than
    /// beside its marker, where it covers the very points being read. On a Smith chart, which has no band, the boxes stack
    /// down its left side. A box the user drags stays where it is put.
    /// </summary>
    public void PlaceNewInfoBox(PlotContainerViewModel container, Marker marker)
    {
        if (container.FindMarkerInfoBoxVm(marker) is not { } box) return;
        var plot = container.PlotVM.Plot;
        var others = PlotHost.MarkerInfoBoxes.Where(b => b.Container == container && !ReferenceEquals(b.Marker, marker)).ToList();
        double left, top;
        if (plot.PlotType == PlotType.Rect)
        {
            var v = plot.Axes.Viewport;
            top = container.Top + v.Y * container.Height + BandGap;
            double x0 = container.Left + v.X * container.Width, x1 = container.Left + (v.X + v.Width) * container.Width;
            var inBand = others.Where(b => Math.Abs(b.Marker.InfoBoxPos.Y - top) < 1).ToList();
            left = inBand.Count == 0 ? x0 : inBand.Max(b => b.Marker.InfoBoxPos.X + b.BoxWidth) + BandGap;
            if (left + box.BoxWidth > x1) left = x0;
        }
        else
        {
            left = container.Left + BandGap;
            top = container.Top + BandGap + others.Sum(b => b.BoxHeight + BandGap);
        }
        marker.InfoBoxPos = new PlotPoint(left, top);
        container.OnPlotChanged(this, EventArgs.Empty);
        container.RequestPlotRedraw();
    }

    /// <summary>The rows of the box the band is sized for: a family curve's — name, curve, x and y.</summary>
    private const int BandBoxLines = 4;

    /// <summary>The rectangular plot's band (<see cref="Plot.TopBandPx"/>): one row of family-curve info boxes, the same
    /// height with or without a marker so the axes never move. A taller box (a multi-marker) overhangs the axes.</summary>
    private double BandHeight() => MarkerRenderer.InfoBoxHeight(BandBoxLines) * PlotHost.ZoomLevel + 2 * BandGap;

    /// <summary>Redraws the selected tab from the loaded data at the cursor.</summary>
    private void RebuildPlots()
    {
        if (RectContainer is null || IsChoosingKind) return;
        PlotBuilds++;
        var plot = IsSmithTab ? SmithPlot : RectPlot;
        // Both plots are cleared, not just the one drawn: the hidden one's markers would otherwise keep their info boxes
        // on screen over the plot that replaced it.
        StashMarkers(RectPlot);
        StashMarkers(SmithPlot);
        string key = $"{StepName}|{SelectedPlotTab}";
        bool sameTab = key == _plottedKey;
        _plottedKey = key;
        // One batch over the whole refill: every Clear and Add would otherwise autoscale the plot by itself, and the
        // window is decided once, below.
        var rectBatch = RectPlot.BeginTraceBatch();
        var smithBatch = SmithPlot.BeginTraceBatch();
        try
        {
            RectPlot.Traces.Clear();
            SmithPlot.Traces.Clear();
            plot.CustomTitleOn = true;
            plot.CustomXLabelOn = false;
            plot.CustomYLabelOn = false;
            _biasPlanePoints = [];
            _biasPlanePickable = [];
            try
            {
                if (!ModelStepPlot(plot))
                switch (SelectedPlotTab)
                {
                    case OutputTab: IvFamily(plot, byVgs: true); break;
                    case TransferTab: IvFamily(plot, byVgs: false); break;
                    case BiasPlaneTab: BiasPlane(plot); break;
                    case STab: SPlot(plot); break;
                    case SmithTab: SPlot(plot); break;
                    case CapacitanceTab: Intrinsic(plot, ["Cgs", "Cgd", "Cds"], 1e15, "fF"); break;
                    case ConductanceTab: Intrinsic(plot, ["Gm", "Gds"], 1e3, "mS"); break;
                    case FlatnessTab: Flatness(plot); break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
            {
                plot.Traces.Clear();
            }
            if (!plot.CustomTitleOn || plot.CustomTitle.Length == 0) plot.CustomTitle = SelectedPlotTab;
            PointsOnTop(plot);
            RestoreMarkers(plot, _plottedKey);
        }
        finally
        {
            rectBatch.Dispose();
            smithBatch.Dispose();
        }
        plot.SetAxesViewport();
        // A new tab frames its new content. A rebuild on the same tab — a cursor move, a pick, an edit, a fit — keeps a
        // window the user zoomed or panned, and otherwise honours the plot's own autoscale settings (owner, review 4: a
        // pick on a zoomed bias plane threw the zoom away, with Autoscale off too).
        bool keep = sameTab && !WindowIsOurs(plot);
        if (!sameTab) plot.Autoscale(force: true);
        else if (!keep) plot.Autoscale();
        if (!keep) _framed = (plot.Axes.Window, plot.Axes.WindowSecondary);
        var container = IsSmithTab ? SmithContainer : RectContainer;
        var hidden = IsSmithTab ? RectContainer : SmithContainer;
        hidden.OnPlotChanged(this, EventArgs.Empty);
        container.OnPlotChanged(this, EventArgs.Empty);
        container.RequestPlotRedraw();
        OnPropertyChanged(nameof(RectPlot));
        OnPropertyChanged(nameof(SmithPlot));
        NotifyCursorUse();
    }

    /// <summary>The data set the plots follow, by kind: the selected one when it holds what the tab needs, else the first that does.</summary>
    private (CmcDataSet Set, NormalisedData Data)? SetFor(Func<CmcDataSet, NormalisedData, bool> holds)
    {
        var all = Document.DataSets.Where(s => Loaded.Data.ContainsKey(s.Name)).Select(s => (s, Loaded.Data[s.Name])).ToList();
        var pick = all.FirstOrDefault(x => x.s.Name == SelectedDataSet?.Name && holds(x.s, x.Item2));
        if (pick.s is null) pick = all.FirstOrDefault(x => holds(x.s, x.Item2));
        return pick.s is null ? null : (pick.s, pick.Item2);
    }

    private static bool HasIv(CmcDataSet s, NormalisedData d) => d.Points.Any(p => double.IsFinite(p.Id) && p.Id != 0);

    /// <summary>The output (Id against Vds, one curve per Vgs) or transfer (Id against Vgs, one per Vds) family.</summary>
    private void IvFamily(Plot plot, bool byVgs)
    {
        if ((SetFor((s, d) => s.Kind is DataSetKind.DcIv or DataSetKind.PulsedIv or DataSetKind.SimulatedTables && HasIv(s, d))
             ?? SetFor(HasIv)) is not { } sd) return;
        plot.CustomTitle = $"{sd.Set.Name} · {(byVgs ? "Id(Vds)" : "Id(Vgs)")}";
        var groups = sd.Data.Points.Where(p => !p.Excluded)
                       .GroupBy(p => Math.Round(byVgs ? p.Vgs : p.Vds, 6)).OrderBy(g => g.Key).ToList();
        foreach (var g in groups)
        {
            var pts = g.OrderBy(p => byVgs ? p.Vds : p.Vgs).ToList();
            if (pts.Count < 2) continue;
            var t = Xy($"{(byVgs ? "Vgs" : "Vds")} {ModelValueFormat.G(g.Key)} V",
                       [.. pts.Select(p => byVgs ? p.Vds : p.Vgs)], [.. pts.Select(p => p.Id * 1e3)],
                       byVgs ? "Vds" : "Vgs", "V", FamilyColour, line: true, marker: MarkerType.Circle);
            t.Properties.MarkerSize = DataMarker;
            t.Properties.Custom = false;
            plot.Traces.Add(t);
        }
        var excluded = sd.Data.Points.Where(p => p.Excluded).ToList();
        if (excluded.Count > 0)
            plot.Traces.Add(Xy("excluded", [.. excluded.Select(p => byVgs ? p.Vds : p.Vgs)], [.. excluded.Select(p => p.Id * 1e3)],
                               byVgs ? "Vds" : "Vgs", "V", 9, line: false, marker: MarkerType.Cross));
        Labels(plot, byVgs ? "Vds (V)" : "Vgs (V)", "Id (mA)");
    }

    /// <summary>
    /// Every bias point of every set: extrinsic ○ where it was measured, intrinsic ● where the network puts it ([P1] Fig.
    /// 6.5), excluded ×, the hygiene flags ▲, the network fit's points ■ and the cursor, a green dot. x = Vds, y = Vgs.
    /// </summary>
    private void BiasPlane(Plot plot)
    {
        var drawn = new List<BiasPlanePoint>();
        var pickable = new List<BiasPlanePoint>();
        var ext = new List<(double X, double Y)>();
        var intr = new List<(double X, double Y)>();
        var excl = new List<(double X, double Y)>();
        var flagged = new List<(double X, double Y)>();
        var fit = new List<(double X, double Y)>();
        var flags = Loaded.Flags.Select(f => (f.DataSet, f.Point)).ToHashSet();
        // Excluded as the DOCUMENT says, not as the loaded data last said: a click shows at once, while the data's reload
        // behind it is still running — a second click in that gap read the point as still included and put it back.
        var excluded = Document.DataSets.SelectMany(s => s.Exclusions.Select(x => (s.Name, x.Point))).ToHashSet();
        var fitPoints = IsNetworkStep ? FitPoints.Select(r => (r.Set, r.Point)).ToHashSet() : [];
        var extLabels = new List<string>();
        var intrLabels = new List<string>();
        // The cursor is drawn ON the point it selects — the intrinsic one while those show — not at the measured bias.
        string? cursorSet = CursorSets().Select(x => x.Set.Name).FirstOrDefault();
        int cursorIndex = cursorSet is not null && Loaded.Data.TryGetValue(cursorSet, out var cd0) ? CursorPoint(cd0) : -1;
        (double X, double Y)? cursorAt = null;
        foreach (var set in Document.DataSets)
        {
            if (!Loaded.Data.TryGetValue(set.Name, out var d)) continue;
            var ds = Loaded.Intrinsic.GetValueOrDefault(set.Name);
            double[]? vgsi = ds?.Cubes.TryGetValue("Vgsi", out var gi) == true ? Values(gi) : null;
            double[]? vdsi = ds?.Cubes.TryGetValue("Vdsi", out var di) == true ? Values(di) : null;
            for (int i = 0; i < d.Count; i++)
            {
                var p = d.Points[i];
                (double X, double Y) e = (p.Vds, p.Vgs);
                (double X, double Y)? n = vgsi is not null && vdsi is not null && i < vgsi.Length && double.IsFinite(vgsi[i]) ? (vdsi[i], vgsi[i]) : null;
                if (!double.IsFinite(e.X) || !double.IsFinite(e.Y)) continue;
                string measured = $"{set.Name} · Vgs {ModelValueFormat.G(p.Vgs)} V, Vds {ModelValueFormat.G(p.Vds)} V";
                if (ShowExtrinsic) { ext.Add(e); extLabels.Add(set.Name); }
                if (ShowIntrinsic && n is { } nn) { intr.Add(nn); intrLabels.Add(measured); }
                var at = ShowIntrinsic && n is { } m ? m : e;
                if (set.Name == cursorSet && i == cursorIndex) cursorAt = _cursorOnExtrinsic && ShowExtrinsic ? e : at;
                drawn.Add(new BiasPlanePoint(set.Name, p.PointId, at.X, at.Y));
                if (ShowExtrinsic) pickable.Add(new BiasPlanePoint(set.Name, p.PointId, e.X, e.Y));
                if (ShowIntrinsic && n is { } ni) pickable.Add(new BiasPlanePoint(set.Name, p.PointId, ni.X, ni.Y));
                if (excluded.Contains((set.Name, p.PointId))) excl.Add(at);
                if (flags.Contains((set.Name, p.PointId))) flagged.Add(at);
                if (fitPoints.Contains((set.Name, p.PointId))) fit.Add(at);
            }
        }
        _biasPlanePoints = drawn;
        _biasPlanePickable = pickable;
        plot.CustomTitle = "Bias plane";
        // Sizes in the Data Display's marker units (a fraction of the plot's size): every point at DataMarker, the glyphs
        // over a point a little larger so the point still shows round them.
        // The points are point clouds (Trace.IsPointCloud): a marker on one is the point nearest the pointer, read out as
        // its data set and bias. The overlays on them — fit points, flags, exclusions, the cursor — are annotation, so a
        // double-click marks the point underneath rather than the glyph over it.
        if (ext.Count > 0) plot.Traces.Add(Cloud(Scatter("extrinsic", ext, 1, MarkerType.Circle, DataMarker), extLabels, "Vgs"));
        if (intr.Count > 0) plot.Traces.Add(Cloud(Scatter("intrinsic", intr, 0, MarkerType.Circle, DataMarker, xName: "Vdsi"), intrLabels, "Vgsi"));
        if (fit.Count > 0) plot.Traces.Add(Overlay(Scatter("fit points", fit, 4, MarkerType.Square, 1.6 * DataMarker)));
        if (flagged.Count > 0) plot.Traces.Add(Overlay(Scatter("flagged", flagged, 2, MarkerType.TriangleUp, 1.6 * DataMarker)));
        if (excl.Count > 0) plot.Traces.Add(Overlay(Scatter("excluded", excl, 9, MarkerType.Cross, 1.8 * DataMarker)));
        if (SelectedVgs is { } cg && SelectedVds is { } cd)
        {
            // The cursor: a green dot over the point it selects, a little larger than the points. (A hollow ring would be
            // drawn only by the markers' dark outline, not in green.)
            plot.Traces.Add(Overlay(Scatter("cursor", [cursorAt ?? (cd.Value, cg.Value)], CursorColour, MarkerType.Circle, 1.6 * DataMarker)));
        }
        Labels(plot, "Vds (V)", "Vgs (V)");
    }

    /// <summary>S at the cursor's bias: dB on the rectangular tab, the reflections on the Smith tab. On the Pads step, before
    /// and after the pads come off.</summary>
    private void SPlot(Plot plot)
    {
        bool passive = IsPassive;
        if (SetFor((s, d) => d.S is not null) is not { } sd) return;
        var data = sd.Data;
        int point = passive ? Math.Max(0, Math.Min(data.Count - 1, PassiveBlockIndex(data))) : CursorPoint(data);
        if (point < 0 || data.FrequenciesHz is not { } f) return;
        var p = data.Points[point];
        plot.CustomTitle = passive ? $"{sd.Set.Name} · {p.PointId}" : $"{sd.Set.Name} · Vgs {ModelValueFormat.G(p.Vgs)} V, Vds {ModelValueFormat.G(p.Vds)} V";
        var layers = new List<(string Tag, NormalisedData D, bool Dashed)>();
        if (IsPadsStep && Loaded.PadsRemoved.TryGetValue(sd.Set.Name, out var removed))
        {
            layers.Add(("before", data, true));
            layers.Add(("after", removed, false));
        }
        else layers.Add(("", data, false));

        int ports = data.Ports;
        var pairs = IsSmithTab
            ? Enumerable.Range(0, ports).Select(i => (i, i)).ToList()
            : Enumerable.Range(0, ports).SelectMany(i => Enumerable.Range(0, ports).Select(j => (i, j))).ToList();
        foreach (var (tag, d, dashed) in layers)
        {
            int c = 0;
            foreach (var (i, j) in pairs)
            {
                var s = new Complex[f.Length];
                for (int k = 0; k < f.Length; k++) s[k] = d.SAt(point, k, i, j);
                string name = $"S{i + 1}{j + 1}{(tag.Length > 0 ? " " + tag : "")}";
                var trace = CubeTrace(name, Style(c++, dashed ? LineType.Dashed : LineType.Solid, 1.0, null));
                if (IsSmithTab) trace.SetCubeData(f, s, null, "freq", "Hz", PlotType.Smith, FreqUnit.GHz);
                else trace.SetCubeData(f, null, [.. s.Select(x => 20 * Math.Log10(Math.Max(x.Magnitude, 1e-12)))], "freq", "Hz", PlotType.Rect, FreqUnit.GHz);
                plot.Traces.Add(trace);
            }
        }
        if (!IsSmithTab) Labels(plot, null, "dB");
    }

    /// <summary>The passive block the cursor's Vgs list stands in for: blocks have no bias, so the first is shown.</summary>
    private static int PassiveBlockIndex(NormalisedData d) => 0;

    /// <summary>Intrinsic quantities against frequency at the cursor's bias — flat when the network is right ([P1] §5.6.3).</summary>
    private void Intrinsic(Plot plot, IReadOnlyList<string> names, double scale, string unit)
    {
        var set = Document.DataSets.FirstOrDefault(s => s.Name == SelectedDataSet?.Name && Loaded.Intrinsic.TryGetValue(s.Name, out var x) && x.Cubes.ContainsKey("Y"))
               ?? Document.DataSets.FirstOrDefault(s => Loaded.Intrinsic.TryGetValue(s.Name, out var x) && x.Cubes.ContainsKey("Y"));
        if (set is null || !Loaded.Data.TryGetValue(set.Name, out var data)) return;
        var ds = Loaded.Intrinsic[set.Name];
        int point = CursorPoint(data);
        if (point < 0) return;
        double[] f = ds.Cubes["Y"].Axes[1].Values;
        int c = 0;
        foreach (string q in names)
        {
            if (!ds.Cubes.TryGetValue(q, out var cube)) continue;
            var y = new double[f.Length];
            for (int k = 0; k < f.Length; k++) y[k] = (double)cube[point, k] * scale;
            plot.Traces.Add(Xy(q, f, y, "freq", "Hz", c++, line: true, marker: null));
        }
        var p = data.Points[point];
        plot.CustomTitle = $"{set.Name} · Vgs {ModelValueFormat.G(p.Vgs)} V, Vds {ModelValueFormat.G(p.Vds)} V";
        Labels(plot, null, unit);
    }

    /// <summary>The flatness metric across bias: one curve per Vds, the mean over Cgs, Cgd and Cds against Vgs, in %.</summary>
    private void Flatness(Plot plot)
    {
        var set = Document.DataSets.FirstOrDefault(s => Loaded.Intrinsic.TryGetValue(s.Name, out var x) && x.Cubes.ContainsKey("Flatness"));
        if (set is null || !Loaded.Data.TryGetValue(set.Name, out var data)) return;
        var cube = Loaded.Intrinsic[set.Name].Cubes["Flatness"];
        int[] q = [.. new[] { "Cgs", "Cgd", "Cds" }.Select(n => IntrinsicQuantities.Names.ToList().IndexOf(n))];
        var rows = Enumerable.Range(0, data.Count)
                             .Select(i => (P: data.Points[i], F: q.Select(k => (double)cube[i, k]).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average()))
                             .Where(r => double.IsFinite(r.F) && !r.P.Excluded).ToList();
        int c = 0;
        foreach (var g in rows.GroupBy(r => Math.Round(r.P.Vds, 6)).OrderBy(g => g.Key))
        {
            var pts = g.OrderBy(r => r.P.Vgs).ToList();
            plot.Traces.Add(Xy($"Vds {ModelValueFormat.G(g.Key)} V", [.. pts.Select(r => r.P.Vgs)], [.. pts.Select(r => 100 * r.F)],
                               "Vgs", "V", c++, line: pts.Count > 1, marker: MarkerType.Circle));
        }
        plot.CustomTitle = $"{set.Name} · flatness";
        Labels(plot, "Vgs (V)", "%");
    }

    // ── trace helpers (railRF's own shapes) ──────────────────────────────────

    /// <summary>The one colour an I–V family is drawn in — blue, the palette's second, so it never reads as the Data
    /// Display's red first trace — with every member the same: a family is one quantity drawn several times.</summary>
    private const int FamilyColour = 1;

    /// <summary>
    /// The size every measured point is drawn at, in the Data Display's marker units (a radius of this many
    /// <c>min(width, height)/200</c>): on the panel's usual 500–600 px a disc of about 7 px, which is what a scatter of
    /// data needs to read as points over the lines without one point covering the next on a 0.5 V grid (owner, review 3:
    /// 0.8, a 4 px disc, was too small to see or hit).
    /// </summary>
    private const double DataMarker = 1.3;

    /// <summary>The bias cursor's colour on a plane of points — green, the palette's fourth.</summary>
    private const int CursorColour = 3;

    private static double[] Values(DataCube c) => [.. Enumerable.Range(0, c.Axes[0].Values.Length).Select(i => (double)c[i])];

    private static void Labels(Plot plot, string? x, string y)
    {
        if (x is not null) { plot.CustomXLabelOn = true; plot.CustomXLabel = x; }
        plot.CustomYLabelOn = true;
        plot.CustomYLabel = y;
    }

    private static Trace Xy(string name, double[] x, double[] y, string xName, string xUnit, int colour, bool line, MarkerType? marker)
    {
        var t = CubeTrace(name, Style(colour, LineType.Solid, 1.0, marker));
        t.Properties.LineEnabled = line;
        t.Properties.Custom = false;
        t.SetCubeData(x, null, y, xName, xUnit, PlotType.Rect, xName == "freq" ? FreqUnit.GHz : FreqUnit.Hz);
        return t;
    }

    private static Trace Scatter(string name, IReadOnlyList<(double X, double Y)> pts, int colour, MarkerType marker, double size,
                                 string xName = "Vds")
    {
        var t = Xy(name, [.. pts.Select(p => p.X)], [.. pts.Select(p => p.Y)], xName, "V", colour, line: false, marker: marker);
        t.Properties.MarkerSize = size;
        t.Properties.Custom = false;
        return t;
    }

    /// <summary>A bias plane's points as a point cloud: a marker resolves to the nearest point and reads its label and Y.</summary>
    private static Trace Cloud(Trace t, IReadOnlyList<string> labels, string yName)
    {
        t.IsPointCloud = true;
        t.PointLabels = labels;
        t.MarkerValueName = yName;
        return t;
    }

    /// <summary>
    /// Every trace that draws point symbols after every trace that does not. The renderer paints in list order and the steps
    /// add each data set before its model, so the model's line was drawn over the measured circles (owner). Stable, so the
    /// overlays (fit points, flags, the cursor) stay above the points they mark.
    /// </summary>
    private static void PointsOnTop(Plot plot)
    {
        var ordered = plot.Traces.OrderBy(t => t.Properties.MarkerEnabled ? 1 : 0).ToList();
        if (ordered.SequenceEqual(plot.Traces)) return;
        plot.Traces.Clear();
        foreach (var t in ordered) plot.Traces.Add(t);
    }

    /// <summary>Drawn over the points and not a reading of them: no marker lands on it.</summary>
    private static Trace Overlay(Trace t)
    {
        t.IsAnnotation = true;
        return t;
    }

    /// <summary>A cube-bound trace with nothing in it yet — <c>Trace</c> has no constructor without an <c>SNP</c>.</summary>
    private static Trace CubeTrace(string name, TraceProperties style) =>
        new(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Real, secondaryAxis: false, style)
        {
            CubeName = name,
            Expression = name,
            QuantityName = name,
        };

    private static TraceProperties Style(int order, LineType type, double width, MarkerType? marker)
    {
        int index = TraceProperties.LineColorOrder[order % TraceProperties.LineColorOrder.Length];
        var props = new TraceProperties
        {
            LineColorIndex = index,
            MarkerColorIndex = index,
            FillColorIndex = index,
            LineEnabled = true,
            LineType = type,
            LineWidth = width,
        };
        if (marker is { } m)
        {
            props.MarkerEnabled = true;
            props.MarkerType = m;
            props.MarkerSize = DataMarker;
        }
        props.Custom = false;
        return props;
    }
}
