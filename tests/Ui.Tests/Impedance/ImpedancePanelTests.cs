// The Impedance panel (brief-impedance-3) — one test per gate in §5: splitting run from export lost
// nothing (the same PDF bytes); the filter's three settings list the expected rows; an edit marks the
// results stale and keeps them; the results overlay never reaches a render of the document; and the
// panel is a TOOL, so opening and closing it never changes which document pane is active.

using System.Text;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Render;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Impedance;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Impedance;

public class ImpedancePanelTests
{
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Gnd = new(2, 0);

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static Technology Tech() => new()
    {
        Name = "panel",
        Layers =
        [
            new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
            new LayerDef { Key = Gnd, Name = "Plane", Purpose = "conductor" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Open,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35),
                                   SigmaSm = 5.8e7, DrawingLayers = [Top] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(500), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = Um(35),
                                   SigmaSm = 5.8e7, DrawingLayers = [Gnd] },
            ],
        },
    };

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2) };

    /// <summary>A 950 µm microstrip and a 100 µm one over a plane.</summary>
    private static LayoutEditorViewModel Board()
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Top, -6000, -475, 6000, 475));
        view.Shapes.Add(Rect(Top, -6000, 3000, 6000, 3100));
        view.Shapes.Add(Rect(Gnd, -8000, -8000, 8000, 8000));
        return new LayoutEditorViewModel(view) { Technology = Tech() };
    }

    private static TraceRun Trace(string id, TraceVerdict verdict, double y, params TraceIssue[] issues) => new()
    {
        Id = id, Layer = Top, LayerName = "Top", Verdict = verdict, Issues = issues,
        WidthMin = Um(100), WidthMax = Um(100), Z0Min = 48, Z0Max = 52, InTolerance = 1,
        StartX = Um(-1000), StartY = Um(y), EndX = Um(1000), EndY = Um(y),
        Stations = [new TraceStation { X = 0, Y = Um(y), Ux = 0, Uy = 1, Length = Um(2000), Width = Um(100), Z0 = 50 }],
    };

    /// <summary>One trace of each verdict; the warning and the fail carry a finding each.</summary>
    private static TraceImpedanceReport OneOfEach() => new()
    {
        TargetOhms = 50, TolerancePercent = 10, WarningPercent = 20,
        Layers =
        [
            new TraceLayerResult
            {
                Layer = Top, Name = "Top",
                Traces =
                [
                    Trace("T1", TraceVerdict.Pass, 0),
                    Trace("T2", TraceVerdict.Warning, 1000,
                          new TraceIssue(TraceIssueKind.ReferenceStep, IssueSeverity.Warning, 0, Um(1000), 0, Um(1000), "step")),
                    Trace("T3", TraceVerdict.Fail, 2000,
                          new TraceIssue(TraceIssueKind.ReturnBroken, IssueSeverity.Fail, 0, Um(2000), Um(200), Um(2000), "broken")),
                    Trace("T4", TraceVerdict.Unsolved, 3000),
                ],
            },
        ],
    };

    /// <summary>§5.1 — run, hold, export: the PDF is byte for byte the one the old one-shot path wrote
    /// for the same options (the same analysis, titled, sourced and unit-stamped the same way).</summary>
    [Fact]
    public async Task RunThenExport_WritesTheOneShotPathsPdf()
    {
        var vm = Board();
        var options = new TraceImpedanceOptions { Layers = [Top] };
        var held = await vm.RunTraceImpedanceAsync(options, new RunControl());
        Assert.NotNull(held);
        Assert.Same(held, vm.ImpedanceReport);

        string pdf = Path.Combine(Path.GetTempPath(), $"imp-panel-{Guid.NewGuid():N}.pdf");
        try
        {
            Assert.True(await vm.ExportTraceImpedancePdfAsync(pdf));

            var oneShot = TraceImpedanceAnalysis.Analyze(vm.Model.Shapes, vm.Technology!, vm.Model.DbuPerMicron,
                              options with { DisplayUnit = vm.DisplayUnit }, new RunControl())
                          with { Title = "Untitled layout", SourcePath = null, TechnologyPath = null,
                                 CreatedUtc = held!.CreatedUtc, Elapsed = held.Elapsed };
            Assert.Equal(TraceImpedanceReportDocument.Pdf(oneShot), await File.ReadAllBytesAsync(pdf));
        }
        finally { File.Delete(pdf); }
    }

    /// <summary>§5.2 — the filter's three settings, on one trace of each verdict. The default is
    /// Warnings and failures; a trace that could not be solved was not reviewed, so it is a failure.</summary>
    [Fact]
    public void TheFilter_ListsTheExpectedRows()
    {
        var vm = new LayoutEditorViewModel(new LayoutView()) { ImpedanceReport = OneOfEach() };
        var panel = new ImpedancePanelViewModel();
        panel.SetEditor(vm);

        string Ids() => string.Join(",", panel.Rows.OfType<ImpedanceTraceResultRow>().Select(r => r.Id));

        Assert.Equal(ImpedanceResultFilter.WarningsAndFailures, panel.Filter);
        Assert.Equal("T2,T3,T4", Ids());
        panel.Filter = ImpedanceResultFilter.Failures;
        Assert.Equal("T3,T4", Ids());
        panel.Filter = ImpedanceResultFilter.All;
        Assert.Equal("T1,T2,T3,T4", Ids());

        // A row's strings are the PDF's; expanding it lists its finding with its severity.
        var t3 = panel.Rows.OfType<ImpedanceTraceResultRow>().Single(r => r.Id == "T3");
        Assert.Equal("FAIL", t3.VerdictText);
        Assert.Equal("48.0–52.0 Ω", t3.Z0Text);
        t3.IsExpanded = true;
        var finding = Assert.IsType<ImpedanceFindingResultRow>(panel.Rows[panel.Rows.IndexOf(t3) + 1]);
        Assert.Equal("FAIL", finding.SeverityText);
    }

    /// <summary>§5.3 — an edit after a run marks the results stale and keeps them; the PDF of a stale
    /// report says so.</summary>
    [Fact]
    public void AnEdit_MarksTheResultsStale_AndKeepsThem()
    {
        var vm = Board();
        var report = OneOfEach();
        vm.ImpedanceReport = report;
        Assert.False(vm.IsImpedanceStale);

        vm.Model.Shapes.Add(Rect(Top, -6000, 5000, 6000, 5100));
        vm.Model.NotifyChanged();

        Assert.True(vm.IsImpedanceStale);
        Assert.Same(report, vm.ImpedanceReport);
        Assert.True(vm.ImpedanceReportForExport!.Stale);
    }

    /// <summary>Clear (field report, 2026-09-28) drops the results and the canvas colours, and keeps
    /// the review the next run reads.</summary>
    [Fact]
    public void Clear_DropsTheResultsAndTheOverlay_AndKeepsTheReview()
    {
        var vm = Board();
        vm.ImpedanceReport = OneOfEach();
        vm.Model.Shapes.Add(Rect(Top, -6000, 5000, 6000, 5100));
        vm.Model.NotifyChanged();
        Assert.True(vm.IsImpedanceStale);
        var review = vm.Model.ImpedanceReview;

        vm.ClearTraceImpedance();

        Assert.Null(vm.ImpedanceReport);
        Assert.False(vm.IsImpedanceStale);
        Assert.Null(vm.Overlay.Impedance);
        Assert.Same(review, vm.Model.ImpedanceReview);
    }

    /// <summary>§5.4 — the overlay is drawn from the editor's overlay and nowhere else: a render of the
    /// document (the `render` verb's options, <c>Overlay = null</c>) is the same picture before and
    /// after a run, while the canvas's own overlay does carry the results.</summary>
    [Fact]
    public void TheOverlay_IsAbsentFromARenderOfTheLayout()
    {
        var vm = Board();
        string before = Svg(vm, overlay: null);

        vm.ImpedanceReport = OneOfEach();
        Assert.Equal(4, vm.Overlay.Impedance?.Traces.Count);
        Assert.Equal(2, vm.Overlay.Impedance!.Findings.Count);

        Assert.Equal(before, Svg(vm, overlay: null));
        Assert.NotEqual(before, Svg(vm, vm.Overlay));

        // Show on canvas off: nothing on the canvas either.
        vm.ShowImpedanceOverlay = false;
        Assert.Null(vm.Overlay.Impedance);
    }

    private static string Svg(LayoutEditorViewModel vm, LayoutOverlay? overlay)
    {
        var vp = new LayoutViewport(Um(-8000), Um(-6000), 1e-4, 1600, 1200);
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, 1600, 1200), stream))
            LayoutRenderer.Draw(canvas, vm.Model, vm.Technology, vp, new LayoutRenderOptions
            {
                Theme = LayoutRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light),
                Overlay = overlay,
            });
        // Skia numbers an SVG's clip-path ids process-wide, so two identical pictures differ there only.
        return System.Text.RegularExpressions.Regex.Replace(
            Encoding.UTF8.GetString(stream.DetachAsData().ToArray()), @"\bcl_\d+", "cl_");
    }

    /// <summary>
    /// §5.5 — the dock trap: Dock's <c>Tool</c> declares <c>IDocument</c>, and a tool counted as a
    /// document pane once emptied the LVS panel when the bottom strip's tabs were flipped. The Impedance
    /// panel docked, brought to the front, and closed again leaves the document pane — and its active
    /// document — exactly as they were.
    /// </summary>
    [Fact]
    public void OpeningAndClosingThePanel_LeavesTheActiveDocumentPaneAlone()
    {
        var f = new CircuitRfDockFactory();
        var layout = new StubDocument("Amp.clay", StubDocument.StubKind.Welcome);
        var impedance = new ImpedanceTool();
        Assert.IsAssignableFrom<ITool>(impedance);
        Assert.Contains(DockPanelIds.Impedance, DockPanelIds.All);

        var documents = new DocumentDock
        {
            Id = "Documents", VisibleDockables = f.CreateList<IDockable>(layout), ActiveDockable = layout,
        };
        var side = new ToolDock
        {
            Id = "Side", VisibleDockables = f.CreateList<IDockable>(new PropertiesTool()),
        };
        var row = new ProportionalDock
        {
            Orientation = Orientation.Horizontal,
            VisibleDockables = f.CreateList<IDockable>(side, documents),
            ActiveDockable = documents,
        };
        var root = f.CreateRootDock();
        root.VisibleDockables = f.CreateList<IDockable>(row);
        f.InitLayout(root);

        f.InsertDockable(side, impedance, side.VisibleDockables!.Count);
        side.ActiveDockable = impedance;
        f.SetActiveDockable(impedance);
        Assert.Same(documents, Assert.Single(DockLayoutCapture.EnumerateDocumentPanes(root)));
        Assert.Same(layout, documents.ActiveDockable);

        f.RemoveDockable(impedance, collapse: false);
        Assert.Same(documents, Assert.Single(DockLayoutCapture.EnumerateDocumentPanes(root)));
        Assert.Same(layout, documents.ActiveDockable);
    }
}
