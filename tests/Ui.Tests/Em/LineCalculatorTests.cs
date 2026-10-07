// The line calculator (AA-3) — `impedance --tech … --layer …`, a line's answers with nothing drawn.
// One test per claim: the model column IS an elaborated MLIN on that layer, bit for bit, and its
// synthesised width is the parameter editor's; the cross-section column IS `impedance` on a line drawn
// at that width, and its synthesised width reads the target there; a patterned dielectric whose mask is
// not drawn is air in the trace cross-section (the defect the calculator exposed); and the verb carries
// both columns in --json and refuses what it cannot use.

using System.Globalization;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Lvs;

namespace CircuitRF.Ui.Tests.Em;

[Collection(LvsCliConsoleCollection.Name)]
public sealed class LineCalculatorTests : IDisposable
{
    private const string GaAs = "mmic-GaAs_2LM_100um";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-linecalc-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Workspace()
    {
        Directory.CreateDirectory(_root);
        return WorkspaceCreate.Create(_root, "ws", GaAs).WorkspaceDir;
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The model column is what an MLIN of that width ELABORATES to on that layer — a .cnl in a
    /// GaAs workspace, bound and elaborated as a run reads it — every field equal, not close; and the
    /// width it synthesises for a Z0 is HammerstadJensen.SynthesizeWidth on the bound substrate.</summary>
    [Fact]
    public void TheModelColumn_IsTheElaboratedMlin_BitForBit()
    {
        const double f = 10e9;
        var result = LineCalculator.Calculate(ShippedTechnologies.Load(GaAs), new LineCalcRequest("Metal1", [30e-6], [50], null, f));
        Assert.True(result.Ok, result.Refusal);
        var byWidth = result.Rows[0].Model!;
        var synthesised = result.Rows[1].Model!;

        string ws = Workspace();
        string cell = Path.Combine(ws, "cell");
        Directory.CreateDirectory(cell);
        string cnl = Path.Combine(cell, "lines.cnl");
        File.WriteAllText(cnl,
            "Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 c 0 Num=2 Z=50 Ohm\n" +
            $"MLIN:T1 a b W={R(byWidth.WidthM)} L=1mm SignalLayer=\"Metal1\"\n" +
            $"MLIN:T2 b c W={R(synthesised.WidthM)} L=1mm SignalLayer=\"Metal1\"\n");

        var (lib, tb) = CnlTechnologyBinding.ReadFile(cnl);
        using var netlist = new Elaborator(lib).Elaborate(tb);
        var lines = netlist.Components.Where(c => c.Model is MicrostripLineModel)
                                      .ToDictionary(c => c.InstancePath, c => (MicrostripLineModel)c.Model);
        Assert.Equal(byWidth.Line, lines.Single(k => k.Key.EndsWith("T1")).Value.LineParameters(f));
        Assert.Equal(synthesised.Line, lines.Single(k => k.Key.EndsWith("T2")).Value.LineParameters(f));

        var t1 = tb.Instances.Single(i => i.InstanceName == "T1");
        double P(string n) => double.Parse(t1.Overrides.Last(o => o.Name == n).Expression, CultureInfo.InvariantCulture);
        Assert.Equal(HammerstadJensen.SynthesizeWidth(50, P("H"), P("T"), P("Er"), new MicrostripValidityReporter("t")),
                     synthesised.WidthM);
    }

    /// <summary>The cross-section column is what `impedance` reports on a line drawn at that width —
    /// a .clay in the same workspace, analysed as the verb analyses it, the line somewhere else and of
    /// another length — and the width it synthesises reads the target there to within its one-DBU step.</summary>
    [Fact]
    public void TheCrossSectionColumn_IsImpedanceOnADrawnLine()
    {
        var result = LineCalculator.Calculate(ShippedTechnologies.Load(GaAs), new LineCalcRequest("Metal1", [30e-6], [50]));
        Assert.True(result.Ok, result.Refusal);
        var byWidth = result.Rows[0].CrossSection!;
        var synthesised = result.Rows[1].CrossSection!;
        Assert.Null(synthesised.Refusal);

        string ws = Workspace();
        string layoutDir = Path.Combine(ws, "cell", "layout");
        Directory.CreateDirectory(layoutDir);
        var metal1 = ShippedTechnologies.Load(GaAs).Layers.Single(l => l.Name == "Metal1").Key;
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = metal1, X1 = 250_000, Y1 = 400_000, X2 = 1_750_000, Y2 = 400_000 + byWidth.WidthDbu });
        view.Shapes.Add(new RectShape { Layer = metal1, X1 = 250_000, Y1 = -900_000, X2 = 3_250_000, Y2 = -900_000 + synthesised.WidthDbu });
        string clay = Path.Combine(layoutDir, "lines.clay");
        LayoutPersistence.SaveToFile(clay, view);

        var traces = TraceImpedanceAnalysis.AnalyzeFile(clay, new TraceImpedanceOptions()).AllTraces.ToList();
        Assert.Equal(2, traces.Count);
        double Drawn(long w) => traces.Single(t => Math.Abs(t.WidthMin - w) <= 1).Z0Mean!.Value;
        Assert.Equal(byWidth.Z0!.Value, Drawn(byWidth.WidthDbu), byWidth.Z0.Value * 1e-12);
        Assert.Equal(synthesised.Z0!.Value, Drawn(synthesised.WidthDbu), synthesised.Z0.Value * 1e-12);
        Assert.Equal(50, synthesised.Z0.Value, 0.005);
    }

    /// <summary>A dielectric patterned with a mask the layout does not draw is AIR in the trace
    /// cross-section, as both EM extractors take it — the same answer as a technology whose film is
    /// air outright — and drawing the mask puts it back. On the shipped GaAs technology the 0.2 µm MIM
    /// film lay on every Metal1 line, and the solve against it jumped ±2 Ω between widths 50 nm apart.</summary>
    [Fact]
    public void APatternedFilmWithNoMaskDrawn_IsAirInTheTraceCrossSection()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        var metal1 = tech.Layers.Single(l => l.Name == "Metal1").Key;
        LayoutShape[] line = [new RectShape { Layer = metal1, X1 = -1_000_000, Y1 = -34_000, X2 = 1_000_000, Y2 = 34_000 }];
        double Z0(Technology t, IReadOnlyList<LayoutShape> shapes) =>
            TraceImpedanceAnalysis.Analyze(shapes, t, LayoutUnits.DefaultDbuPerMicron, new TraceImpedanceOptions { Layers = [metal1] })
                                  .AllTraces.Single().Z0Mean!.Value;

        var air = ShippedTechnologies.Load(GaAs);
        var film = air.Stackup.Layers.Single(l => l.PresentWithLayer is { Length: > 0 });
        film.Epsr = 1; film.TanD = 0; film.PresentWithLayer = null;

        Assert.Equal(Z0(air, line), Z0(tech, line));

        var nitride = tech.Layers.Single(l => l.Name == "Nitride").Key;
        LayoutShape[] masked = [.. line, new RectShape { Layer = nitride, X1 = -5_000_000, Y1 = 4_000_000, X2 = -4_000_000, Y2 = 5_000_000 }];
        Assert.NotEqual(Z0(air, masked), Z0(tech, masked));
    }

    /// <summary>The verb: --json carries both columns, and what the calculator cannot use is refused by
    /// name rather than ignored — a layout beside --tech, a layer the technology does not have.</summary>
    [Fact]
    public void TheVerb_CarriesBothColumns_AndRefusesWhatItCannotUse()
    {
        Assert.Equal(0, InProcess("impedance", "--tech", GaAs, "--layer", "Metal1", "--z0", "50", "--freq", "10GHz", "--json"));
        var row = JsonDocument.Parse(_last).RootElement.GetProperty("result").GetProperty("impedanceLine").GetProperty("rows")[0];
        Assert.Equal(50, row.GetProperty("model").GetProperty("z0Static").GetDouble(), 1e-9);
        Assert.True(row.GetProperty("model").GetProperty("lossDbPerMm").GetDouble() > 0);
        Assert.Equal(50, row.GetProperty("crossSection").GetProperty("z0").GetDouble(), 0.005);
        Assert.Equal("microstrip", row.GetProperty("crossSection").GetProperty("configuration").GetString());

        Assert.Equal(1, InProcess("impedance", "--tech", GaAs, "--layer", "Copper", "--width", "30um", "--json"));
        Assert.Contains("impedance.layers.unknown", _last, StringComparison.Ordinal);
        Assert.Contains("Metal1", _last, StringComparison.Ordinal);
        Assert.Equal(1, InProcess("impedance", "some.clay", "--tech", GaAs, "--layer", "Metal1", "--width", "30um", "--json"));
        Assert.Contains("impedance.line.path-not-used", _last, StringComparison.Ordinal);
    }

    private string _last = "";

    private int InProcess(params string[] args)
    {
        var real = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            JsonRun.Reset();
            int exit = CliEntry.Run(args);
            _last = buffer.ToString();
            return exit;
        }
        finally { Console.SetOut(real); }
    }
}
