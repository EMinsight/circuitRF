using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Data Display figures: plots that contain a curve, and trace cards that are pointed at a real
/// result.
///
/// <para>Every one of these is driven through the same commands the toolbar's buttons invoke —
/// <c>AddTraceCommand</c>, <c>AddContourTraceCommand</c>, the inspector's own property setters — so a
/// figure cannot show an arrangement the interface cannot reach. The data comes from
/// <see cref="DocRunData"/>, which runs the shipped test benches for real.</para>
/// </summary>
public static partial class DocDataDisplayFixtures
{
    /// <summary>A Data Display document wired to the documentation's own results directory.</summary>
    private static DataDisplayDocumentViewModel Document()
    {
        var vm = new DataDisplayDocumentViewModel();
        vm.Window.DataSourceLibrary.ResultsRootProvider = () => DocRunData.ResultsRoot;
        return vm;
    }

    /// <summary>
    /// Select one of the documentation's results files as the active data source, exactly as the
    /// toolbar's source picker does.
    /// </summary>
    /// <param name="also">
    /// Further results files to LOAD into the library alongside the selected one, for a figure whose
    /// traces come from more than one run.
    ///
    /// <para>Loading is what a second entry takes: <c>RefreshAvailableDataSources</c> only
    /// ENUMERATES what is in the results directory, and the per-trace Source combo appears once the
    /// library holds two loaded entries (<c>TraceRowViewModel.SourceSelectorVisible</c>). A figure
    /// that merely wrote a second file beside the first would therefore have no Source combo on its
    /// cards and every trace would silently come from the selected one.</para>
    /// </param>
    private static DataDisplayDocumentViewModel Sourced(
        string logicalId, IReadOnlyList<string>? also = null)
    {
        var vm = Document();
        var lib = vm.Window.DataSourceLibrary;
        lib.RefreshAvailableDataSources();

        foreach (var extra in also ?? [])
        {
            string abs = lib.ResolveAbs(extra)
                ?? throw new InvalidOperationException(
                    $"The documentation results file '{extra}' does not resolve under the docs "
                  + "results root, so a figure comparing two runs cannot load it.");
            Await(lib.LoadFileAsync(abs));
        }

        Await(lib.SelectDataSourceAsync(logicalId));
        if (lib.SelectedEntry is null)
            throw new InvalidOperationException(
                $"The documentation results file '{logicalId}' was written but the data-source library "
              + "did not pick it up, so every figure built on it would be an empty frame.");
        return vm;
    }

    /// <summary>
    /// Replace the constructor's seeded empty Smith plot with one of <paramref name="type"/>, add a
    /// trace, and hand back both the document and the plot.
    /// </summary>
    /// <summary>
    /// <see cref="Plotted"/> under a name the WSProbe figures can call it by
    /// (<see cref="DocWsProbeFixtures"/> builds its own trace cards over the same document).
    /// </summary>
    internal static (DataDisplayDocumentViewModel Doc, PlotContainerViewModel Plot) PlotFor(
        string logicalId, PlotType type, bool contour = false, int traces = 1,
        (double W, double H)? size = null, Action<PlotContainerViewModel>? before = null,
        IReadOnlyList<string>? also = null)
        => Plotted(logicalId, type, contour, traces, size, before, also);

    /// <summary><see cref="Centred"/>, for the same reason.</summary>
    internal static Action<Control> CentredPlot(PlotContainerViewModel plot) => Centred(plot);

    /// <summary>
    /// <see cref="Centred"/> for a ROW of plots: they are laid out left to right with an even gap and
    /// the whole row is centred in the canvas, so a figure that is about comparing two plots reads as
    /// one picture rather than two that happen to be near each other.
    /// </summary>
    internal static Action<Control> CentredRow(params PlotContainerViewModel[] plots)
        => Row(plots);

    /// <param name="before">Applied to the plot AFTER its type and size are set and BEFORE any trace
    /// is added. A pattern plot needs it: the dB-radial switch decides which cubes the trace card is
    /// allowed to OFFER (<c>TraceRowViewModel.OnPlotPatternModeChanged</c>), so a plot turned into a
    /// pattern plot after its traces exist is one whose traces were picked under the other rule.</param>
    private static (DataDisplayDocumentViewModel Doc, PlotContainerViewModel Plot) Plotted(
        string logicalId, PlotType type, bool contour = false, int traces = 1,
        (double W, double H)? size = null, Action<PlotContainerViewModel>? before = null,
        IReadOnlyList<string>? also = null)
    {
        var vm = Sourced(logicalId, also);
        var display = vm.Window.DataDisplay
            ?? throw new InvalidOperationException("The Data Display document has no active tab.");

        var plot = display.Plots.FirstOrDefault()
            ?? throw new InvalidOperationException("The Data Display seeded no plot to configure.");

        plot.Inspector.PlotType = type;
        // The plot is sized to the FIGURE it will appear in, not to a single house size: a Smith or
        // polar plot is square, a rectangular one is wide, and a table is neither — it is as wide as
        // its columns and no wider, which is what lets the table figure be captured in a window half
        // the width of the others instead of half-empty.
        (plot.Width, plot.Height) = size ?? type switch
        {
            PlotType.Smith or PlotType.Polar => (460.0, 460.0),
            PlotType.Table                   => (620.0, 400.0),
            // Was 560x360 in an 850x540 window, which left a third of the canvas empty around the
            // curve — fine beside body text, thin once a slide scales the whole window down.
            _                                => (700.0, 400.0),
        };

        before?.Invoke(plot);

        var command = contour ? plot.Inspector.AddContourTraceCommand : plot.Inspector.AddTraceCommand;
        for (int i = 0; i < traces; i++)
        {
            if (!command.CanExecute(null))
                throw new InvalidOperationException(
                    $"'{logicalId}' offered no {(contour ? "contour" : "standard")} trace to add. A figure "
                  + "of an empty plot is worse than no figure, so this is a hard failure.");
            command.Execute(null);
        }

        if (plot.Inspector.Traces.Count == 0)
            throw new InvalidOperationException($"'{logicalId}': adding a trace left the plot with none.");

        return (vm, plot);
    }


    /// <summary>
    /// Put the plot in the MIDDLE of the Data Display's canvas, once the canvas has a size.
    ///
    /// <para>The application centres the first plot itself
    /// (<c>DataDisplayViewModel.ComputeNewPlotPosition</c>, case 1), but only when it can see the
    /// viewport — and it cannot at fixture-construction time, because nothing has been arranged yet.
    /// It therefore fell back to the historical <c>30 + count*30</c> cascade, which is why every
    /// whole-document plot figure sat up against the top-left corner with the chart clipped on the
    /// right and a band of empty canvas below it (owner, 2026-08-24). The canvas is asked for its real
    /// bounds here and the plot is centred in them; <see cref="FigureScene.AfterLayout"/> is the one
    /// callback that runs late enough for that to be a real number.</para>
    /// </summary>
    private static Action<Control> Centred(PlotContainerViewModel plot) => root =>
    {
        var canvas = root.GetVisualDescendants().OfType<ItemsControl>()
                         .FirstOrDefault(c => c.Name == "PlotCanvas")
            ?? throw new InvalidOperationException(
                "The Data Display view no longer has a control named 'PlotCanvas'. Every whole-document "
              + "plot figure centres its plot in that control's bounds, so a rename makes them all "
              + "silently off-centre again rather than failing.");

        double w = canvas.Bounds.Width, h = canvas.Bounds.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException(
                $"The Data Display canvas arranged to {w}x{h}, so the plot cannot be centred in it.");

        // Centre the CONTAINER, not the graph rect. A plot's drawn extent is wider than Width by its
        // per-trace Y-axis label strips and taller than Height by the title and X-label clearances,
        // and those extras are asymmetric — centring on Width/Height alone leaves the picture visibly
        // low and left. The view-space numbers the canvas itself binds to already account for all of
        // it, so the correction is taken as a delta from them rather than re-derived here.
        double zoom = plot.ZoomLevel > 0 ? plot.ZoomLevel : 1.0;
        plot.Left += Math.Round(((w - plot.ViewTotalWidth) / 2.0 - plot.ViewContainerLeft) / zoom);
        plot.Top  += Math.Round(((h - plot.ViewHeight)     / 2.0 - plot.ViewTop)           / zoom);
    };

    /// <summary>
    /// Lay <paramref name="plots"/> out in one horizontal row, vertically aligned, and centre the row
    /// in the canvas — the same delta-from-the-view's-own-numbers correction <see cref="Centred"/>
    /// makes, applied to the row's total extent instead of one plot's.
    /// </summary>
    private static Action<Control> Row(PlotContainerViewModel[] plots) => root =>
    {
        if (plots.Length == 0) return;

        var canvas = root.GetVisualDescendants().OfType<ItemsControl>()
                         .FirstOrDefault(c => c.Name == "PlotCanvas")
            ?? throw new InvalidOperationException(
                "The Data Display view no longer has a control named 'PlotCanvas'. Every whole-document "
              + "plot figure centres its plot in that control's bounds, so a rename makes them all "
              + "silently off-centre again rather than failing.");

        double w = canvas.Bounds.Width, h = canvas.Bounds.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException(
                $"The Data Display canvas arranged to {w}x{h}, so the plots cannot be centred in it.");

        const double gap = 24.0;
        double rowWidth = plots.Sum(p => p.ViewTotalWidth) + gap * (plots.Length - 1);
        double x = (w - rowWidth) / 2.0;

        foreach (var p in plots)
        {
            double zoom = p.ZoomLevel > 0 ? p.ZoomLevel : 1.0;
            p.Left += Math.Round((x - p.ViewContainerLeft) / zoom);
            p.Top  += Math.Round(((h - p.ViewHeight) / 2.0 - p.ViewTop) / zoom);
            x += p.ViewTotalWidth + gap;
        }
    };

    // ── Whole-document figures ────────────────────────────────────────────────

    /// <summary>
    /// The Data Display with a rectangular plot of real swept S-parameter data in it: forward gain
    /// rolling off across 1-10 GHz.
    ///
    /// <para>The signal is picked explicitly. A new trace seeds itself on S(1,1), and this test bench
    /// presents a bare gate capacitance there, so |S11| is 1 to within rounding at every frequency —
    /// a perfectly flat line at 0 dB. That is technically "a plot with data in it" and it is useless
    /// as a figure, which is the whole reason the picker is driven here.</para>
    /// </summary>
    public static FigureScene RectangularWithData()
    {
        var (vm, plot) = Plotted(DocRunData.SParameters(), PlotType.Rect);
        PickSignal(plot.Inspector.Traces[0], "S(2,1)");
        return new FigureScene(new DataDisplayView { DataContext = Document(vm, "S-Parameters") })
            { AfterLayout = Centred(plot) };
    }

    /// <summary>
    /// Choose a trace's signal the way the trace card's own picker does — by label, from the list the
    /// card offers. Selecting by label rather than by index keeps the fixture honest: if the picker
    /// stops offering that signal, the docs build says so instead of drawing a different curve.
    /// </summary>
    internal static void PickSignal(TraceRowViewModel trace, string label)
    {
        var item = trace.AvailableSignals.FirstOrDefault(s => s.Label == label)
            ?? throw new InvalidOperationException(
                $"The trace card offers no signal called '{label}'. It offers: "
              + string.Join(", ", trace.AvailableSignals.Select(s => s.Label)) + ".");
        trace.SelectedSignal = item;
    }

    // ── Driving a trace card, by what things ARE rather than by where they sit ────────────────
    //
    //  EVERY ONE OF THESE TAKES THE CARD AS A FUNCTION, not as a reference. Almost every edit here
    //  ends in RebuildAndNotify, which REPLACES the inspector's TraceRowViewModels — so a reference
    //  held across two edits is a live object for the first and a discarded one for the second, and
    //  writing to a discarded one throws nothing and changes nothing. DocAntennaFixtures records
    //  what that looked like when it happened: a figure that came out configured for the first
    //  trace and seeded for the second, with a plausible curve and a wrong label.

    /// <summary>The n-th trace card, re-fetched from the live collection on every call.</summary>
    internal static Func<TraceRowViewModel> Card(PlotContainerViewModel plot, int index) => () =>
        index < plot.Inspector.Traces.Count
            ? plot.Inspector.Traces[index]
            : throw new InvalidOperationException(
                $"A documentation figure expected at least {index + 1} trace card(s) and the plot "
              + $"has {plot.Inspector.Traces.Count}.");

    /// <summary>Set the card's one transform combo, and refuse a transform it has DISABLED — a
    /// figure set to one would be showing a state the interface refuses.</summary>
    internal static void SetTransform(Func<TraceRowViewModel> card, CubeTransform transform)
    {
        var item = card().TraceTransformItems.FirstOrDefault(i => i.Transform == transform)
            ?? throw new InvalidOperationException(
                $"The trace card offers no '{transform}' transform.");
        if (!item.Enabled)
            throw new InvalidOperationException(
                $"The trace card offers '{transform}' but has it DISABLED on this plot, so a figure "
              + "set to it would be showing a state the interface refuses.");
        card().SelectedTransformItem = item;
    }

    /// <summary>
    /// Point one trace at one of the library's loaded results files, through the card's own Source
    /// combo — the control that appears once a display holds two datasets.
    ///
    /// <para>Matched by the entry's FILE, never by position: the combo's order follows the library's
    /// and a figure that took item 0 would draw the other run's curve after any change to it, in
    /// silence.</para>
    /// </summary>
    internal static void PickSource(Func<TraceRowViewModel> card, string logicalId)
    {
        if (!card().SourceSelectorVisible)
            throw new InvalidOperationException(
                $"The trace card has no Source combo, so a figure cannot point this trace at "
              + $"'{logicalId}'. It appears once the display holds two loaded datasets — pass the "
              + "second one as PlotFor's `also`.");

        var item = card().AvailableSourceEntries.FirstOrDefault(
                       i => !i.IsAddFromFile && i.Entry is { } e
                         && string.Equals(Path.GetFileName(e.FilePath), logicalId,
                                          StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The trace card's Source combo offers no '{logicalId}'. It offers: "
              + string.Join(", ", card().AvailableSourceEntries.Select(i => i.DisplayText)) + ".");

        card().SelectedSourceItem = item;
    }

    /// <summary>Move a trace to the RIGHT-hand y-axis, through the card's own left/right toggle —
    /// for a figure carrying two quantities whose ranges have nothing to do with each other.</summary>
    internal static void UseRightAxis(Func<TraceRowViewModel> card)
    {
        if (card().UseSecondaryAxis) return;
        card().ToggleSecondaryAxisCommand.Execute(null);
        if (!card().UseSecondaryAxis)
            throw new InvalidOperationException(
                "The trace card's secondary-axis toggle did not move the trace to the right-hand "
              + "axis, so the figure would draw a phase against a decibel scale.");
    }

    /// <summary>
    /// Draw a trace DASHED, through the trace card's own line-style picker — for the one figure whose
    /// point is that two curves coincide. Two coincident solid lines are indistinguishable from one.
    /// </summary>
    internal static void Dashed(TraceRowViewModel trace)
    {
        var item = PlotInspectorViewModel.LineModes.FirstOrDefault(
                       m => !m.IsOff && m.Type == LineType.Dashed)
            ?? throw new InvalidOperationException(
                "The trace card no longer offers a dashed line style.");
        trace.SelectedLineMode = item;
    }

    /// <summary>
    /// The Data Display with load-pull contours drawn on the Γ plane, referenced to the impedance
    /// the grid itself was swept in (<see cref="DocRunData.LoadpullGridZ0"/>).
    ///
    /// <para><b>The chart's Z0 has to be set, not left at the seeded 50 Ω.</b> A contour trace seeds
    /// its reference to the 50 Ω the Γ plane assumes by default, so a constellation swept about
    /// 250 Ω would be drawn compressed into the high-impedance edge of the Smith chart — technically
    /// the same data, and unreadable as a figure. The override is set through the trace card's own
    /// property, which is the control a reader would use.</para>
    /// </summary>
    public static FigureScene LoadpullContours()
    {
        var (vm, plot) = Plotted(DocRunData.Loadpull(), PlotType.Smith, contour: true);
        foreach (var trace in plot.Inspector.Traces)
        {
            trace.Z0OverrideEnabled = true;
            trace.Z0String = DocRunData.LoadpullGridZ0.ToString("0.###", CultureInfo.InvariantCulture);
        }
        return new FigureScene(new DataDisplayView { DataContext = Document(vm, "Load-pull") })
            { AfterLayout = Centred(plot) };
    }

    /// <summary>A Smith chart with a real swept reflection coefficient on it — the FET's own S(1,1).</summary>
    public static FigureScene SmithWithData()
    {
        var (vm, plot) = Plotted(DocRunData.SParameters(), PlotType.Smith);
        PickSignal(plot.Inspector.Traces[0], "S(1,1)");
        return new FigureScene(new DataDisplayView { DataContext = Document(vm, "S-Parameters") })
            { AfterLayout = Centred(plot) };
    }

    /// <summary>The same data on a polar plot — magnitude and angle, without the impedance grid.</summary>
    public static FigureScene PolarWithData()
    {
        var (vm, plot) = Plotted(DocRunData.SParameters(), PlotType.Polar);
        PickSignal(plot.Inspector.Traces[0], "S(2,1)");
        return new FigureScene(new DataDisplayView { DataContext = Document(vm, "S-Parameters") })
            { AfterLayout = Centred(plot) };
    }

    /// <summary>A table of the same run: a complex column and a scalar one side by side.</summary>
    public static FigureScene TableWithData()
    {
        var (vm, plot) = Plotted(DocRunData.SParameters(), PlotType.Table, traces: 2);
        PickSignal(plot.Inspector.Traces[0], "S(2,1)");
        PickSignal(plot.Inspector.Traces[^1], "S(1,1)");
        return new FigureScene(new DataDisplayView { DataContext = Document(vm, "S-Parameters") })
            { AfterLayout = Centred(plot) };
    }

    /// <summary>
    /// The New User's Guide's second worked example, plotted: <c>S(2,1)</c> in dB against frequency,
    /// filling a square window the same size as the schematic figure it sits beside.
    ///
    /// <para>The plot is sized explicitly rather than taking the house default for a rectangular
    /// plot: the pair exists so a reader can check their own result against it, and a small chart
    /// adrift in a large empty window is a worse check (owner, 2026-08-24).</para>
    /// </summary>
    public static FigureScene ExampleLowPassResponse()
    {
        var (vm, plot) = Plotted(DocRunData.ExampleSParam(), PlotType.Rect,
                                 size: (DocExampleFixtures.Square - 60, DocExampleFixtures.Square - 155));
        PickSignal(plot.Inspector.Traces[0], "S(2,1)");
        return new FigureScene(new DataDisplayView { DataContext = Document(vm, "Example_SParam_LC") })
            { AfterLayout = Centred(plot) };
    }

    // ── Plot Inspector figures ────────────────────────────────────────────────

    /// <summary>The Plot Inspector showing a trace card against the swept S-parameter run.</summary>
    public static FigureScene InspectorTraceCard()
    {
        var (_, plot) = Plotted(DocRunData.SParameters(), PlotType.Rect);
        PickSignal(plot.Inspector.Traces[0], "S(2,1)");
        return new FigureScene(new PlotInspectorView { DataContext = plot.Inspector });
    }

    /// <summary>
    /// The trace card reading a PASSIVE metric, so the Fixture control is on screen.
    ///
    /// <para>It is the one card control whose meaning cannot be guessed from its name: the fixture
    /// is an assumption about how the part was measured that nothing in the data records, and the
    /// wrong choice returns a smooth, plausible, wrong curve. A picture of where it lives is worth
    /// more than another paragraph saying so.</para>
    /// </summary>
    public static FigureScene InspectorPassiveReadout()
    {
        var (_, plot) = Plotted(DocRunData.SParameters(), PlotType.Rect);
        PickSignal(plot.Inspector.Traces[0], DerivedParameters.Esr.Description());
        return new FigureScene(new PlotInspectorView { DataContext = plot.Inspector });
    }

    /// <summary>
    /// The Plot Inspector for the SAME trace the Smith figure draws — S(1,1) on a Smith chart.
    ///
    /// <para>Deliberately not <see cref="InspectorTraceCard"/>, which reads S(2,1) on a rectangular
    /// plot: shown beside the Smith figure, a card configured for a different plot and a different
    /// signal is a picture of the wrong thing, and it is the kind of wrong nobody checks.</para>
    /// </summary>
    public static FigureScene InspectorSmith()
    {
        var (_, plot) = Plotted(DocRunData.SParameters(), PlotType.Smith);
        PickSignal(plot.Inspector.Traces[0], "S(1,1)");
        return new FigureScene(new PlotInspectorView { DataContext = plot.Inspector });
    }

    /// <summary>The Plot Inspector showing a trace card against a harmonic-balance run.</summary>
    public static FigureScene InspectorHb() => Inspector(DocRunData.HarmonicBalance(), PlotType.Rect);

    /// <summary>
    /// The Plot Inspector with its WSProbe section open — the probe picker, the metric, and the
    /// reading beside it (WSP-4 §2, for WSP-7).
    ///
    /// <para><c>1/Y0</c> on a POLAR plot, because that is the pairing the reference document reads a
    /// node's stability off (§4.9-4.10): the locus is looked at for a clockwise crossing of the
    /// negative real axis, and the card states in words what the eye is being asked to see. A
    /// rectangular card of the same quantity would be a picture of the controls with the point of
    /// them left out.</para>
    /// </summary>
    public static FigureScene InspectorWsProbe()
    {
        var (_, plot) = Plotted(DocRunData.WsProbe(), PlotType.Polar);
        var row = plot.Inspector.Traces[0];
        // The picker is a group→item cascade, so the section has to be selected before its items
        // are the ones on offer — the same two gestures a reader makes.
        row.SelectedGroup = row.AvailableGroups.First(
            g => g.EndsWith("\u25b8 WSProbe", StringComparison.Ordinal));
        PickSignal(row, WspMetricLabel(WspMetric.InvY0));
        return new FigureScene(new PlotInspectorView { DataContext = plot.Inspector });
    }

    /// <summary>The picker label a WSProbe metric appears under — built the same way the item builds
    /// it, so the figure cannot go looking for a label the card stopped using.</summary>
    private static string WspMetricLabel(WspMetric metric)
    {
        var info = WspMetrics.Info(metric)!.Value;
        return $"{info.Name} \u2014 {info.Description}";
    }

    /// <summary>The Plot Inspector showing a load-pull contour trace card.</summary>
    public static FigureScene InspectorLoadpull()
        => Inspector(DocRunData.Loadpull(), PlotType.Smith, contour: true);

    private static FigureScene Inspector(string logicalId, PlotType type, bool contour = false)
    {
        var (_, plot) = Plotted(logicalId, type, contour);
        return new FigureScene(new PlotInspectorView { DataContext = plot.Inspector });
    }

    internal static DataDisplayDocument Document(DataDisplayDocumentViewModel vm, string title)
        => new(title, vm);

    /// <summary>
    /// Wait for an asynchronous view-model call by PUMPING the dispatcher, never by blocking on it.
    ///
    /// <para>The data-source library loads a results file on a worker and continues on the captured
    /// synchronisation context — which, in this headless host, is the Avalonia dispatcher. A plain
    /// <c>GetAwaiter().GetResult()</c> therefore blocks the very thread the continuation is queued on
    /// and the docs run hangs, silently, with no output at all. That is not a hypothetical: it is what
    /// this fixture did first.</para>
    /// </summary>
    internal static void Await(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!task.IsCompleted)
        {
            UiArtworkGenerator.Pump();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    "A Data Display documentation fixture waited 30 s for its results file to load and "
                  + "it never completed.");
            System.Threading.Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();   // rethrow anything the task faulted with
    }
}
