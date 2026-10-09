using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Statistics;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Yield page's figures: the plots of the one-click yield display, each on its own, over the shipped
/// Yield example's bandpass filter at its own settings and seed — the run the page's own example table quotes.
///
/// <para><b>Composed, not hand-built.</b> Every plot is <see cref="YieldDisplayPreset"/>'s, pasted into a
/// document the way a <c>.cdd</c> opens, so a figure shows exactly what <b>Open yield display</b> writes; the
/// one figure that differs (the envelope) changes only what the trace card's own Envelope and Show Curves
/// entries change.</para>
///
/// <para>The run is 500 trials of a three-resonator filter — about a second — and is deterministic for its
/// seed, so it is run on every regeneration rather than committed, as every other Data Display figure's
/// data is (<see cref="DocRunData"/>).</para>
/// </summary>
public static partial class DocDataDisplayFixtures
{
    private const string YieldFile = "BandpassYield.yield.npy";

    private static (string Id, DataSet Data)? _yield;

    /// <summary>The shipped BandpassYield example's yield run, written to the docs results root.</summary>
    private static (string Id, DataSet Data) YieldResult()
    {
        if (_yield is { } done) return done;
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the Yield figures have no design to run.");
        string csch = Path.Combine(root, "Yield", "BandpassYield", "schematic", "BandpassYield.csch");

        var run = StatisticalRun.Create(PreparedCircuit.FromSchematic(csch));
        if (run.Refusal is { } refused)
            throw new InvalidOperationException($"The Yield figures' run of '{csch}' was refused: {refused.Render()}");
        var result = run.Run();
        if (result.Data is not { } ds || result.Outcome is not (StatisticalOutcome.Finished or StatisticalOutcome.BelowTarget))
            throw new InvalidOperationException(
                $"The Yield figures' run of '{csch}' ended {result.Outcome} ({result.FinishReason}), so every yield figure would be empty.");

        Directory.CreateDirectory(DocRunData.ResultsRoot);
        string path = Path.Combine(DocRunData.ResultsRoot, YieldFile);
        if (File.Exists(path)) File.Delete(path);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        _yield = (YieldFile, ds);
        return _yield.Value;
    }

    /// <summary>
    /// A document holding the yield display's plots of <paramref name="kind"/> (and, when named, of
    /// <paramref name="goals"/>, in that order), sized <paramref name="size"/>, with <paramref name="shape"/>
    /// applied to each plot's configuration first.
    /// </summary>
    private static (DataDisplayDocumentViewModel Doc, PlotContainerViewModel[] Plots) YieldPlots(
        YieldDisplayPreset.PlotKind kind, string[]? goals, (double W, double H)? size, Action<PlotContainerConfig>? shape = null)
    {
        var (id, ds) = YieldResult();
        var vm = Sourced(id);
        var display = vm.Window.DataDisplay
            ?? throw new InvalidOperationException("The Data Display document has no active tab.");
        foreach (var seeded in display.Plots.ToList()) display.RemovePlot(seeded);

        var composed = YieldDisplayPreset.Compose(ds, id).Where(p => p.Kind == kind).ToList();
        var picked = goals is null
            ? composed.Select(p => p.Config).ToList()
            : goals.Select(g => composed.FirstOrDefault(p => p.Goal == g)?.Config
                  ?? throw new InvalidOperationException($"The yield display composed no {kind} plot for '{g}'.")).ToList();
        if (picked.Count == 0)
            throw new InvalidOperationException($"The yield display composed no {kind} plot, so its figure would be empty.");
        foreach (var pc in picked)
        {
            if (size is { } wh) (pc.Width, pc.Height) = wh;
            shape?.Invoke(pc);
        }

        var paste = display.PasteFromConfigAsync(new DataDisplayConfig { Plots = picked });
        Await(paste);
        return (vm, [.. paste.Result]);
    }

    private static FigureScene YieldScene((DataDisplayDocumentViewModel Doc, PlotContainerViewModel[] Plots) built)
        => new(new DataDisplayView { DataContext = Document(built.Doc, "BandpassYield") })
            { AfterLayout = built.Plots.Length == 1 ? Centred(built.Plots[0]) : Row(built.Plots) };

    /// <summary>
    /// The passband zoomed to where the spec is decided — 0.82 to 1.18 GHz, −2 to +0.3 dB. With ±2 % parts the
    /// 500 curves are a band a fraction of a dB wide, so over the whole sweep the failures' red covers it and the
    /// envelope reads as one line; here the passes, the failures dipping through the limit and the band are each
    /// visible.
    /// </summary>
    private static void Passband(PlotContainerConfig pc)
        => pc.Axes = new AxesConfig
        {
            AutoscaleX = false, AutoscaleY = false,
            WindowX = 0.82, WindowWidth = 0.36, WindowY = -2.0, WindowHeight = 2.3,
        };

    /// <summary>The passband spec's 500 trials, passes grey and fails red, the nominal over them and the spec's
    /// limit across its band.</summary>
    public static FigureScene YieldFamily()
        => YieldScene(YieldPlots(YieldDisplayPreset.PlotKind.Family, ["PassbandSpec"], (700, 400), Passband));

    /// <summary>The same trials as a P1–P99 envelope around the median, with the curves off — the trace card's
    /// Envelope ▸ P1–P99 and Show Curves unticked.</summary>
    public static FigureScene YieldEnvelope()
        => YieldScene(YieldPlots(YieldDisplayPreset.PlotKind.Family, ["PassbandSpec"], (700, 400), pc =>
        {
            Passband(pc);
            foreach (var t in pc.Traces) { t.Envelope = "p:1"; t.Curves = false; }
            pc.CustomTitle = "PassbandSpec: P1–P99 envelope";
        }));

    /// <summary>The trace card of the passband family, with its Statistics button (the bar chart under the
    /// remove button) — where every statistic of a trace starts.</summary>
    public static FigureScene YieldTraceCard()
    {
        var (_, plots) = YieldPlots(YieldDisplayPreset.PlotKind.Family, ["PassbandSpec"], (700, 400));
        var card = plots[0].Inspector.Traces.FirstOrDefault()
            ?? throw new InvalidOperationException("The yield family plot has no trace card.");
        if (!card.ShowStatisticsMenu)
            throw new InvalidOperationException("The yield family's trace card does not offer the Statistics button, so its figure would not show it.");
        return new FigureScene(new PlotInspectorView { DataContext = plots[0].Inspector });
    }

    /// <summary>Each spec's worst value as a histogram with its limit: the passband spec straddling its limit,
    /// the stop-band spec well clear of its own.</summary>
    public static FigureScene YieldHistograms()
        => YieldScene(YieldPlots(YieldDisplayPreset.PlotKind.Histogram, ["PassbandSpec", "StopHighSpec"], (520, 340)));

    /// <summary>
    /// The statistics table — one row per spec's worst value, one column per statistic, each column as wide as its
    /// own header and values (<see cref="StatisticsTablePreset"/> sizes them) — split into two tables stacked, the
    /// columns in order. Whole, its fifteen columns are too wide for a page to show at a readable size.
    /// </summary>
    public static FigureScene YieldStatisticsTable()
    {
        var (id, ds) = YieldResult();
        var vm = Sourced(id);
        var display = vm.Window.DataDisplay
            ?? throw new InvalidOperationException("The Data Display document has no active tab.");
        foreach (var seeded in display.Plots.ToList()) display.RemovePlot(seeded);

        var whole = StatisticsTablePreset.Build(ds, id)
            ?? throw new InvalidOperationException("The yield run carries no statistics table, so its figure would be empty.");
        int half = (whole.Traces.Count + 1) / 2;
        PlotContainerConfig Part(int skip, int take, string title)
        {
            var part = JsonSerializer.Deserialize<PlotContainerConfig>(JsonSerializer.Serialize(whole, DataDisplayJson.Options), DataDisplayJson.Options)!;
            part.Traces = [.. part.Traces.Skip(skip).Take(take)];
            part.Width = part.FreqColumnWidth + part.Traces.Sum(t => t.ColumnWidth);
            part.Height = 80;
            part.CustomTitle = title;
            return part;
        }
        var paste = display.PasteFromConfigAsync(new DataDisplayConfig
        {
            Plots = [Part(0, half, "Statistics"), Part(half, whole.Traces.Count - half, "Statistics, continued")],
        });
        Await(paste);
        return new FigureScene(new DataDisplayView { DataContext = Document(vm, "BandpassYield") })
            { AfterLayout = Stack([.. paste.Result]) };
    }

    /// <summary>Plots one above another, left-aligned and centred as a block — <see cref="Row"/>, turned on its
    /// side.</summary>
    private static Action<Avalonia.Controls.Control> Stack(PlotContainerViewModel[] plots) => root =>
    {
        var canvas = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Avalonia.Controls.ItemsControl>()
                         .FirstOrDefault(c => c.Name == "PlotCanvas")
            ?? throw new InvalidOperationException("The Data Display view no longer has a control named 'PlotCanvas'.");
        double w = canvas.Bounds.Width, h = canvas.Bounds.Height;
        const double gap = 20.0;
        double width = plots.Max(p => p.ViewTotalWidth);
        double height = plots.Sum(p => p.ViewHeight) + gap * (plots.Length - 1);
        double x = (w - width) / 2.0, y = (h - height) / 2.0;
        foreach (var p in plots)
        {
            double zoom = p.ZoomLevel > 0 ? p.ZoomLevel : 1.0;
            p.Left += Math.Round((x - p.ViewContainerLeft) / zoom);
            p.Top  += Math.Round((y - p.ViewTop) / zoom);
            y += p.ViewHeight + gap;
        }
    };

    /// <summary>The yield in each bin of the value that drives the passband spec most, with the trials per bin.</summary>
    public static FigureScene YieldSensitivity()
        => YieldScene(YieldPlots(YieldDisplayPreset.PlotKind.YieldSensitivity, null, (700, 400)));
}
