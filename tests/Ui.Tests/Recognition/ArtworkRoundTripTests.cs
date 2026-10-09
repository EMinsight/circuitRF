// brief-artsch-9-acceptance-docs-example.md §2 R-as9-1 and R-as9-2 — the acceptance gate of Create Schematic from
// Artwork: a design whose answer is known goes out as a fabricator would receive it and comes back recognised.
//
// R-as9-1: the schematic (ArtworkRoundTripBoards.OriginalCnl, drawn) → Update Layout from Schematic → arranged →
// Gerber + Excellon, placement and BOM → imported into a fresh workspace → `circuitrf recognize` as a process → compared
// with the original for topology, geometry and response; and again without the companion files.
// R-as9-2: a grounded coplanar line with a via fence, and a stripline, each drawn by hand (no generator draws them, D18),
// through the same export and import.
//
// ── The tolerances, and why ──────────────────────────────────────────────────────────────────────────────────
// Geometry: W within 1 µm — the Gerber is written at 1 nm and the importer reads it back exactly, so a width is
// measured, not estimated; L within max(1 %, 10 µm) — a length is measured between reference planes the recognition
// places (a pad's edge, half a width from a bend's corner or a tee's centre, a tap's pad centre), each from copper
// whose corners the export and import keep to the nanometre.
// Response: |ΔS| ≤ 0.02 on every entry at every point and S21's phase within ±3°. The two circuits share every model;
// what can still differ is what the geometry tolerance admits (10 µm of line is under 0.1° at 3 GHz) and what neither
// side models: the pads are not in either circuit — the lines meet the parts at the pads' edges, which is where the
// designer drew them to — and there is no step model on either side for a pad wider than its line (D18). Small
// entries are judged by |ΔS|, never by dB.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.Recognition.ArtworkRoundTripBoards;

namespace CircuitRF.Ui.Tests.Recognition;

/// <summary>The R-as9-1 board, built, laid out, written out and imported once for both variants.</summary>
public sealed class ArtworkRoundTripBoard : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "crf-as9-rt-" + Guid.NewGuid().ToString("N")[..12]);
    public string SchematicPath { get; }
    public string ImportedClay { get; }
    public string Placement { get; }
    public string Bom { get; }

    public ArtworkRoundTripBoard()
    {
        var (cellDir, schematic, clay, tech) = Original(Path.Combine(Root, "original"));
        SchematicPath = schematic;
        var (imported, pos, bom) = ExportAndImport(Root, cellDir, clay, tech, TwoLayer(), companions: true);
        (ImportedClay, Placement, Bom) = (imported, pos!, bom!);
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
}

public sealed class ArtworkRoundTripTests(ArtworkRoundTripBoard board, ITestOutputHelper output)
    : IClassFixture<ArtworkRoundTripBoard>, IDisposable
{
    private const double WidthTolMetres = 1e-6, LengthTolMetres = 10e-6, LengthTolFraction = 0.01;
    private const double DsTol = 0.02, PhaseTolDeg = 3;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as9-rt2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static readonly string[] Sweep = ["--start", "0.1GHz", "--stop", "3GHz", "--npts", "59"];

    [Fact]
    public void WithItsCompanions_TheBoardComesBackAsTheDesign()
    {
        string cnl = Path.Combine(board.Root, "with.cnl"), csv = Path.Combine(board.Root, "with.csv");
        Recognize([board.ImportedClay, "--placement", board.Placement, "--bom", board.Bom, "--into", "new:Board_model",
                   "-o", cnl, "--parts-out", csv, .. Sweep]);
        var original = Read(OriginalCnl);
        var recognised = Read(File.ReadAllText(cnl));

        // Topology: the same elements by type; each part by its designator, its kind, its value and its connection.
        Assert.Equal(CountsByType(original), CountsByType(recognised));
        foreach (string part in new[] { "C1", "L1", "R1" })
        {
            var (o, r) = (original.Instances.Single(i => i.InstanceName == part), recognised.Instances.Single(i => i.InstanceName == part));
            Assert.Equal(o.Reference, r.Reference);
            var (ov, rv) = (o.Overrides.Single(), r.Overrides.Single());
            Assert.Equal((ov.Name, ov.Expression, ov.Unit), (rv.Name, rv.Expression, rv.Unit));
        }
        Assert.Equal(new Dictionary<string, string> { ["C1"] = "series", ["L1"] = "shunt", ["R1"] = "series" }, Connections(csv));
        var map = Map(original, recognised, parts: new() { ["C1"] = "C1", ["L1"] = "L1", ["R1"] = "R1" });
        AssertSameConnections(original, recognised, map);

        AssertSameGeometry(original, recognised, map);

        // Response: both simulated over 0.1–3 GHz.
        string recognisedCsch = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(board.ImportedClay)!)!, "..",
                                             "Board_model", "schematic", "Board_model.csch");
        var (a, b) = (Simulate(board.SchematicPath, "a.s2p"), Simulate(recognisedCsch, "b.s2p"));
        Assert.Equal(59, a.FrequencyCount);
        Assert.Equal(a.Frequencies, b.Frequencies);
        for (int k = 0; k < a.FrequencyCount; k++)
        {
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True((a[k][i, j] - b[k][i, j]).Magnitude <= DsTol,
                                $"S{i + 1}{j + 1} at {a.Frequencies[k] / 1e9:G4} GHz: {a[k][i, j]} against {b[k][i, j]}");
            double dPhase = ((a[k][1, 0].Phase - b[k][1, 0].Phase) * 180 / Math.PI % 360 + 540) % 360 - 180;
            Assert.True(Math.Abs(dPhase) <= PhaseTolDeg, $"S21 phase at {a.Frequencies[k] / 1e9:G4} GHz differs by {dPhase:F2}°");
        }
    }

    [Fact]
    public void WithoutItsCompanions_TheBoardComesBackWithAKnobForEveryValue()
    {
        string cnl = Path.Combine(board.Root, "without.cnl"), csv = Path.Combine(board.Root, "without.csv");
        Recognize([board.ImportedClay, "--into", "new:Board_knobs", "-o", cnl, "--parts-out", csv, .. Sweep]);
        var original = Read(OriginalCnl);
        var recognised = Read(File.ReadAllText(cnl));

        // Every element but the parts by type; the three parts, whose kinds nothing on the copper states.
        static bool IsPart(Instance i) => i.Reference is "C" or "L" or "R";
        Assert.Equal(CountsByType(original, i => !IsPart(i)), CountsByType(recognised, i => !IsPart(i)));
        var parts = recognised.Instances.Where(IsPart).ToList();
        Assert.Equal(3, parts.Count);

        // Every value a variable <Refdes>_<Param>, a global, with its tune entry.
        foreach (var p in parts)
        {
            var value = p.Overrides.Single();
            Assert.Equal($"{p.InstanceName}_{value.Name}", value.Expression);
            Assert.Contains(recognised.GlobalVariables, v => v.Name == value.Expression);
            Assert.Contains(recognised.Tuning!.Variables, t => t.Key == value.Expression && t.Tune);
        }

        // The parts are told apart by what the copper does state: the land pattern and the connection.
        var rows = File.ReadAllLines(csv).Skip(1).Select(l => l.Split(',')).ToList();
        string By(string @case, string connection) => rows.Single(r => r[3] == @case && r[2] == connection)[0];
        var map = Map(original, recognised,
                      parts: new() { ["C1"] = By("0402", "series"), ["L1"] = By("0402", "shunt"), ["R1"] = By("0603", "series") });
        AssertSameConnections(original, recognised, map);
        AssertSameGeometry(original, recognised, map);
    }

    // ── R-as9-2 ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A 600 µm line across a 20 × 10 mm board between top grounds 250 µm away on each side, each ground
    /// stitched to the Bottom plane by a fence of 19 vias.</summary>
    private static IEnumerable<LayoutShape> Gcpw()
    {
        const double w = 600, g = 250, y = 5_000;
        yield return Rect(Bottom, 0, 0, 20_000, 10_000);
        yield return Rect(Top, 0, y - w / 2, 20_000, y + w / 2);
        yield return Rect(Top, 0, y + w / 2 + g, 20_000, 10_000);
        yield return Rect(Top, 0, 0, 20_000, y - w / 2 - g);
        for (int i = 0; i < 19; i++)
        {
            yield return ViaAt(1_000 + 1_000 * i, y + w / 2 + g + 600);
            yield return ViaAt(1_000 + 1_000 * i, y - w / 2 - g - 600);
        }
    }

    [Fact]
    public void ACoplanarLine_ComesBackAsCpwg_ItsFenceDropped_AndAsMlinWithItsGapsUnderMicrostrip()
    {
        var (cellDir, clay, tech) = Drawn(Path.Combine(_root, "original"), TwoLayer(), Gcpw());
        var (imported, _, _) = ExportAndImport(_root, cellDir, clay, tech, TwoLayer(), companions: false);

        string cnl = Path.Combine(_root, "cpwg.cnl");
        var report = JsonNode.Parse(Recognize([imported, "-o", cnl, "--json"]))!;
        var tb = Read(File.ReadAllText(cnl));
        Assert.Equal(["CPWG", "Port", "Port"], tb.Instances.Select(i => i.Reference).Order());
        var line = tb.Instances.Single(i => i.Reference == "CPWG");
        AssertNear(600e-6, Metres(line, "W"), WidthTolMetres, "W");
        AssertNear(250e-6, Metres(line, "G"), WidthTolMetres, "G");
        AssertNear(20e-3, Metres(line, "L"), Math.Max(LengthTolMetres, LengthTolFraction * 20e-3), "L");
        // All 38 fence vias are stitching, dropped; nothing of them is in the circuit.
        var findings = report["result"]!["recognize"]!["report"]!.AsArray();
        Assert.Equal(38, findings.Single(f => f!["class"]!.GetValue<string>() == nameof(RecognitionFindingClass.StitchingViasDropped))!["count"]!.GetValue<int>());

        Recognize([imported, "--coplanar", "microstrip", "--into", "new:Board_ms"]);
        var drawn = SchematicPersistence.LoadFromFile(
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(imported)!)!, "..", "Board_ms", "schematic", "Board_ms.csch")).Item1;
        var mlin = Assert.Single(drawn.Components, c => c.Symbol == SymbolKind.Mlin);
        var gaps = mlin.ArtworkMeasured.Where(kv => kv.Key.Contains("Gap", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, gaps.Count);
        Assert.All(gaps, kv => AssertNear(250e-6, kv.Value, WidthTolMetres, kv.Key));
    }

    /// <summary>A 300 µm line on the inner layer of the four-layer board, edge to edge, between the Plane above and the
    /// Bottom plane below, with a top pour; the three are stitched by two rows of vias.</summary>
    private static IEnumerable<LayoutShape> Stripline()
    {
        foreach (var layer in new[] { Top, Plane, Bottom }) yield return Rect(layer, 0, 0, 20_000, 10_000);
        yield return Rect(Inner, 0, 4_850, 20_000, 5_150);
        for (int i = 0; i < 10; i++)
        {
            yield return ViaAt(1_000 + 2_000 * i, 1_000);
            yield return ViaAt(1_000 + 2_000 * i, 9_000);
        }
    }

    [Fact]
    public void AStripline_ComesBackAsSlin_ItsHeightsFromTheTechnology()
    {
        var (cellDir, clay, tech) = Drawn(Path.Combine(_root, "original"), FourLayer(), Stripline());
        var (imported, _, _) = ExportAndImport(_root, cellDir, clay, tech, FourLayer(), companions: false);

        string cnl = Path.Combine(_root, "slin.cnl");
        Recognize([imported, "-o", cnl]);
        var tb = Read(File.ReadAllText(cnl));
        Assert.Equal(["Port", "Port", "SLIN"], tb.Instances.Select(i => i.Reference).Order());
        var line = tb.Instances.Single(i => i.Reference == "SLIN");
        AssertNear(300e-6, Metres(line, "W"), WidthTolMetres, "W");
        AssertNear(20e-3, Metres(line, "L"), Math.Max(LengthTolMetres, LengthTolFraction * 20e-3), "L");

        // H1 and H2 are not written: the extraction injects them from the stackup the netlist names.
        Assert.DoesNotContain(line.Overrides, o => o.Name is "H1" or "H2" or "Er");
        string layer = line.Overrides.Single(o => o.Name == "SignalLayer").Expression;
        string techPath = Path.Combine(Path.GetDirectoryName(cnl)!, tb.Technology!);
        var (stripline, failure, _) = SubstrateResolver.ResolveStripline(TechPersistence.LoadFromFile(techPath), layer);
        Assert.True(stripline is not null, failure?.ToString());
        Assert.Equal((800e-6, 200e-6), (Math.Round(stripline!.H1Meters, 9), Math.Round(stripline.H2Meters, 9)));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>`circuitrf recognize` as a process; returns its stdout, where the report is.</summary>
    private string Recognize(string[] args)
    {
        var (exit, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], ["recognize", .. args]);
        output.WriteLine($"$ recognize {string.Join(' ', args)} -> {exit}\n{stdout}\n{stderr}");
        Assert.True(exit == 0, stderr);
        return stdout;
    }

    private RfCore.SNP Simulate(string csch, string name)
    {
        string s2p = Path.Combine(board.Root, name);
        var (exit, _, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "sparam", csch, "-o", s2p);
        Assert.True(exit == 0, stderr);
        return RfCore.TouchstoneIO.ReadFile(s2p);
    }

    private static TestBench Read(string cnl) => new CnlReader().Read(cnl).Item2;

    private static SortedDictionary<string, int> CountsByType(TestBench tb, Func<Instance, bool>? where = null) =>
        new(tb.Instances.Where(where ?? (_ => true)).GroupBy(i => i.Reference).ToDictionary(g => g.Key, g => g.Count()));

    private static Dictionary<string, string> Connections(string csv)
    {
        var lines = File.ReadAllLines(csv);
        int connection = Array.IndexOf(lines[0].Split(','), "Connection");
        return lines.Skip(1).Select(l => l.Split(',')).ToDictionary(c => c[0], c => c[connection]);
    }

    private static double Metres(Instance i, string name)
    {
        var o = i.Overrides.Single(p => p.Name == name);
        double scale = o.Unit switch { "mm" => 1e-3, "um" or "µm" => 1e-6, "m" => 1, _ => throw new InvalidOperationException($"{i.InstanceName}.{name}: unit '{o.Unit}'") };
        return double.Parse(o.Expression, System.Globalization.CultureInfo.InvariantCulture) * scale;
    }

    private static void AssertNear(double expected, double actual, double tol, string what) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"{what}: {actual * 1e6:F3} µm against {expected * 1e6:F3} µm (±{tol * 1e6:F1} µm)");

    /// <summary>Each original instance's counterpart: ports by number, the parts as given, a line by its length (all
    /// seven differ), and the bend, the tee and the via — one of each.</summary>
    private static Dictionary<string, string> Map(TestBench original, TestBench recognised, Dictionary<string, string> parts)
    {
        var map = new Dictionary<string, string>(parts);
        foreach (var o in original.Instances.Where(i => !parts.ContainsKey(i.InstanceName)))
        {
            var candidates = recognised.Instances.Where(r => r.Reference == o.Reference).ToList();
            var r = o.Reference switch
            {
                "Port" => candidates.Single(c => c.Overrides.Single(p => p.Name == "Num").Expression ==
                                                 o.Overrides.Single(p => p.Name == "Num").Expression),
                "MLIN" => candidates.MinBy(c => Math.Abs(Metres(c, "L") - Metres(o, "L")))!,
                _ => Assert.Single(candidates),
            };
            map[o.InstanceName] = r.InstanceName;
        }
        Assert.Equal(map.Count, map.Values.Distinct().Count());
        return map;
    }

    /// <summary>Each element touches the same elements, and ground, as its counterpart.</summary>
    private static void AssertSameConnections(TestBench original, TestBench recognised, Dictionary<string, string> map)
    {
        static Dictionary<string, (HashSet<string> Neighbours, bool Grounded)> Graph(TestBench tb)
        {
            var onNet = tb.Instances.SelectMany(i => i.NetBindings.Where(n => n != "0").Select(n => (n, i.InstanceName)))
                                    .ToLookup(x => x.n, x => x.InstanceName);
            return tb.Instances.ToDictionary(
                i => i.InstanceName,
                i => (i.NetBindings.Where(n => n != "0").SelectMany(n => onNet[n]).Where(x => x != i.InstanceName).ToHashSet(),
                      i.Reference != "Port" && i.NetBindings.Contains("0")));
        }
        var (o, r) = (Graph(original), Graph(recognised));
        foreach (var (name, (neighbours, grounded)) in o)
        {
            var counterpart = r[map[name]];
            Assert.True(neighbours.Select(n => map[n]).ToHashSet().SetEquals(counterpart.Neighbours),
                        $"{name} touches {string.Join(", ", neighbours.Order())}; {map[name]} touches {string.Join(", ", counterpart.Neighbours.Order())}");
            Assert.True(grounded == counterpart.Grounded, $"{name}: grounded {grounded}, {map[name]}: grounded {counterpart.Grounded}");
        }
    }

    /// <summary>Every line's W within 1 µm and L within max(1 %, 10 µm); the bend's and the tee's widths within 1 µm.</summary>
    private static void AssertSameGeometry(TestBench original, TestBench recognised, Dictionary<string, string> map)
    {
        foreach (var o in original.Instances.Where(i => i.Reference is "MLIN" or "MBEND" or "MTEE"))
        {
            var r = recognised.Instances.Single(i => i.InstanceName == map[o.InstanceName]);
            foreach (var w in o.Overrides.Where(p => p.Name is "W" or "W1" or "W2" or "W3"))
                AssertNear(Metres(o, w.Name), Metres(r, w.Name), WidthTolMetres, $"{o.InstanceName}.{w.Name}");
            if (o.Reference == "MLIN")
            {
                double l = Metres(o, "L");
                AssertNear(l, Metres(r, "L"), Math.Max(LengthTolMetres, LengthTolFraction * l), $"{o.InstanceName}.L");
            }
        }
    }
}
