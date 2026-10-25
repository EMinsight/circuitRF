using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

// brief-dfr26-1 — a multilayer board in EM: a port's own level at an edge over inner pours, the 3D pad-to-plane port,
// and what a Gerber import marks as ground. Synthetic geometry only, on the shipped four-layer technology's stack.
// In the collection the Gmsh-counting classes share: two tests here start Gmsh, and PalaceRun.GmshInvocations is per process.
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class MultilayerPortRound26Tests : IDisposable
{
    private const int Dbu = 1000;
    private static readonly LayerKey L1 = new(1, 0), L2 = new(2, 0), L3 = new(3, 0), L4 = new(4, 0), ViaLayer = new(9, 0);
    private static readonly LayerKey Text = new(63, 0);
    private readonly string _root = Directory.CreateTempSubdirectory("dfr26-1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Mm(double v) => (long)Math.Round(v * 1000 * Dbu);

    /// <summary>The four-layer stack with the ground references this test names; the boundary is Open below unless
    /// the bottom copper is one.</summary>
    private static Technology Tech(bool in1, bool in2, bool bottom)
    {
        var t = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var c = t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        c[0].IsGroundReference = false;
        c[1].IsGroundReference = in1;
        c[2].IsGroundReference = in2;
        c[3].IsGroundReference = bottom;
        t.Stackup.Bottom = bottom ? BoundaryCondition.Ground : BoundaryCondition.Open;
        return t;
    }

    private static RectShape Pour(LayerKey layer) => new() { Layer = layer, X1 = Mm(-2), Y1 = Mm(-5), X2 = Mm(12), Y2 = Mm(5) };

    private static LabelShape Label(string text, double xMm, LayerKey layer, LayerKey? committed, PlanarPortKind kind,
                                    LayoutRotation? direction = null)
        => new()
        {
            Layer = layer, PortLayer = committed, X = Mm(xMm), Y = 0, Text = text, Height = Mm(0.2), IsPort = true,
            PortKind = kind, PortDirection = direction,
        };

    private static (EmSetup, EmLayoutSource) Source(Technology tech, IEnumerable<LayoutShape> shapes)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        var setup = new EmSetup
        {
            Name = "r26", LayoutRef = "r26/layout/r26.clay",
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
        };
        return (setup, new EmLayoutSource("/nowhere/r26.clay", view, tech, Dbu));
    }

    // ── R-dfr26-1-1 ─────────────────────────────────────────────────────────────────────────────

    private static LayoutShape[] TraceOverPours(LabelShape p1, LabelShape p2) =>
        [new RectShape { Layer = L1, X1 = 0, Y1 = Mm(-0.15), X2 = Mm(10), Y2 = Mm(0.15) }, Pour(L2), Pour(L3), p1, p2];

    [Fact]
    public void AnEdgePortOverInnerPours_ResolvesToItsOwnLevel_PlanarAnd3D()
    {
        var tech = Tech(in1: false, in2: false, bottom: true);
        var shapes = TraceOverPours(Label("P1", 0, L1, L1, PlanarPortKind.Edge, LayoutRotation.R0),
                                    Label("P2", 10, L1, L1, PlanarPortKind.Edge, LayoutRotation.R180));
        string[] levels = ["Top Copper (1 oz)", "Inner 1 (Ground Plane)", "Inner 2"];

        var problem = PlanarExtractor.Extract(shapes, tech, Dbu, 3e9, new EmExtractionSettings(AnalysisLevelNames: levels)).Problem!;
        Assert.Equal(3, problem.Layers.Count);
        int top = problem.Layers.ToList().FindIndex(l => l.Name == "Top Copper (1 oz)");
        var planar = EmPortExtraction.Extract(shapes, problem, Dbu, technology: tech);
        Assert.True(planar.Ok, planar.Refusal);
        Assert.All(planar.Ports, p => Assert.Equal(top, p.LayerIndex));

        var (setup, source) = Source(tech, shapes);
        var g = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(g.Ok, g.Refusal);
        Assert.All(g.Problem!.Ports, p => Assert.StartsWith("Top Copper (1 oz)/", p.PositiveObject, StringComparison.Ordinal));
    }

    [Fact]
    public void ALabelCommittingNoLevel_OverTheSamePours_IsStillRefusedAsMultiLevel()
    {
        var tech = Tech(in1: false, in2: false, bottom: true);
        var shapes = TraceOverPours(Label("P1", 0, Text, null, PlanarPortKind.Edge, LayoutRotation.R0),
                                    Label("P2", 10, L1, L1, PlanarPortKind.Edge, LayoutRotation.R180));
        string[] levels = ["Top Copper (1 oz)", "Inner 1 (Ground Plane)", "Inner 2"];
        var problem = PlanarExtractor.Extract(shapes, tech, Dbu, 3e9, new EmExtractionSettings(AnalysisLevelNames: levels)).Problem!;

        var r = EmPortExtraction.Extract(shapes, problem, Dbu, technology: tech);

        Assert.False(r.Ok);
        Assert.Contains("of this EM setup's 3 conductor levels", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("'Inner 1 (Ground Plane)'", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("'Inner 2'", r.Refusal!, StringComparison.Ordinal);
    }

    // ── R-dfr26-1-2 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A 1.5 mm pad on Bottom Copper under two ground planes; Inner 2, the nearer, optionally has a 2 mm
    /// cut-out under the pad.</summary>
    private static List<LayoutShape> PadUnderPlanes(bool cutOut)
    {
        var in2 = cutOut
            ? (LayoutShape)new PolygonShape
            {
                Layer = L3,
                Xy = [Mm(-2), Mm(-5), Mm(12), Mm(-5), Mm(12), Mm(5), Mm(-2), Mm(5)],
                Holes = [[Mm(4), Mm(-1), Mm(6), Mm(-1), Mm(6), Mm(1), Mm(4), Mm(1)]],
            }
            : Pour(L3);
        return
        [
            Pour(L2), in2,
            new CircleShape { Layer = L4, Cx = Mm(5), Cy = 0, R = Mm(0.75) },
            Label("P1", 5, L4, L4, PlanarPortKind.Internal),
        ];
    }

    [Theory]
    [InlineData(false, "Inner 2")]
    [InlineData(true, "Inner 1 (Ground Plane)")]
    public void APadToPlanePort_EndsOnTheNearestPlaneWithMetalOverThePad(bool cutOut, string expected)
    {
        var tech = Tech(in1: true, in2: true, bottom: false);
        var (setup, source) = Source(tech, PadUnderPlanes(cutOut));

        var g = Em3dGenerator.Generate(setup, source, tech);

        Assert.True(g.Ok, g.Refusal);
        var port = Assert.Single(g.Problem!.Ports);
        Assert.StartsWith("Bottom Copper (1 oz)/", port.PositiveObject, StringComparison.Ordinal);
        Assert.StartsWith(expected + "/", port.NegativeObject, StringComparison.Ordinal);
        // Vertical, centred on the label, the pad's own chord wide.
        Assert.Equal(port.Min.X, port.Max.X, 12);
        Assert.Equal(5e-3, port.Min.X, 9);
        Assert.Equal(1.5e-3, port.Max.Y - port.Min.Y, 4);   // a faceted circle's chord
        Assert.Equal(35e-6, port.Min.Z, 9);                     // the pad's top face
        Assert.Contains(g.Notes, n => n.Contains("pad-to-plane port", StringComparison.Ordinal) &&
                                      n.Contains($"to '{expected}'", StringComparison.Ordinal));
        if (cutOut)
        {
            // A DRAWN plane is metal with its holes: the cut-out is in the problem, not filled.
            int holes = g.Problem.Solids.Where(s => s.Name.StartsWith("Inner 2/", StringComparison.Ordinal))
                                        .Select(s => (s.Primitive as Em3dExtrudedPolygon)?.Holes.Count ?? 0)
                         .Concat(g.Problem.Sheets.Where(s => s.Name.StartsWith("Inner 2/", StringComparison.Ordinal))
                                                 .Select(s => s.Holes.Count))
                         .Single();
            Assert.Equal(1, holes);
            Assert.Contains("passing through the opening in 'Inner 2'", g.Notes.Single(n => n.Contains("pad-to-plane", StringComparison.Ordinal)), StringComparison.Ordinal);
        }
    }

    /// <summary>The test point's through via stands at the label: the sheet stands tangent to the barrel on the side
    /// away from the feed trace (a Top Copper line along x, so +y) — and, where the plane clears the barrel by an
    /// antipad, at the antipad's edge, where the plane's metal begins.</summary>
    [Theory]
    [InlineData(false, 0.15)]
    [InlineData(true, 0.45)]
    public void APadToPlanePortOnAVia_StandsBesideTheBarrel_AwayFromTheFeed(bool antipad, double offsetMm)
    {
        var (setup, source) = PadOnAVia(antipad);
        var tech = source.Technology!;

        var g = Em3dGenerator.Generate(setup, source, tech);

        Assert.True(g.Ok, g.Refusal);
        var port = Assert.Single(g.Problem!.Ports);
        Assert.Equal(port.Min.Y, port.Max.Y, 12);               // the sheet's line runs along x, beside the barrel
        Assert.Equal(offsetMm * 1e-3, port.Min.Y, 6);
        Assert.StartsWith("Inner 2/", port.NegativeObject, StringComparison.Ordinal);
        string note = g.Notes.Single(n => n.Contains("pad-to-plane", StringComparison.Ordinal));
        Assert.Contains("A via barrel stands at the label", note, StringComparison.Ordinal);
        Assert.Contains("+y side", note, StringComparison.Ordinal);
        Assert.Contains(antipad ? "clearance round the via" : "tangent to it", note, StringComparison.Ordinal);
    }

    /// <summary>The pad of <see cref="PadUnderPlanes"/> on a through via, fed by a Top Copper line along x; with
    /// <paramref name="antipad"/>, both planes clear the barrel by a 0.9 mm antipad (a 64-gon, a vertex at +y).</summary>
    private static (EmSetup, EmLayoutSource) PadOnAVia(bool antipad)
    {
        var tech = Tech(in1: true, in2: true, bottom: false);
        var shapes = PadUnderPlanes(cutOut: false);
        shapes.Add(new RectShape { Layer = L1, X1 = Mm(2), Y1 = Mm(-0.15), X2 = Mm(8), Y2 = Mm(0.15) });
        shapes.Add(new ViaShape { Layer = ViaLayer, X = Mm(5), Y = 0, DrillSize = Mm(0.3), PadSize = Mm(0.6) });
        if (antipad)
        {
            long[] Ring() => [.. Enumerable.Range(0, 64).SelectMany(k =>
                new[] { Mm(5 + 0.45 * Math.Sin(2 * Math.PI * k / 64)), Mm(0.45 * Math.Cos(2 * Math.PI * k / 64)) })];
            foreach (var key in new[] { L2, L3 })
            {
                shapes.RemoveAll(s => s.Layer == key);
                shapes.Add(new PolygonShape { Layer = key, Xy = [Mm(-2), Mm(-5), Mm(12), Mm(-5), Mm(12), Mm(5), Mm(-2), Mm(5)], Holes = [Ring()] });
            }
        }
        return Source(tech, shapes);
    }

    /// <summary>
    /// A dielectric under a plane with an antipad, cut by the plane and the via together, meshes. Gmsh's OCC kernel
    /// emptied that slab when the plane's hole was wound against its outline (Clipper's convention), and the script then
    /// stopped on the air box's background — on every multilayer board with a via through a plane's antipad.
    /// </summary>
    [CircuitRF.Ui.Tests.Em3d.GmshFact]
    public void ASlabUnderAPlaneWithAnAntipad_CutByThePlaneAndItsVia_Meshes()
    {
        var (setup, source) = PadOnAVia(antipad: true);
        var face = new EmAirBoxFace(1000, null);                // a millimetre of air keeps the mesh small
        setup.AirBox = new EmAirBox(face, face, face, face, face, face);
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        var low = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace));
        Assert.True(low.Ok, low.Refusal);

        var step = PalaceRun.Mesh(Path.Combine(_root, "mesh"), low, Gmsh(), null, default);

        Assert.True(step.Ok, step.Message);
    }

    /// <summary>
    /// A blind via whose lower end is flush with the plane it lands on, sticking out past that plane's edge (as a crop
    /// through a via leaves it), meshes. The fragment made one face across the plane's underside and the via's end, which
    /// lay in neither conductor's box, so the run's entity check refused the mesh for a face belonging to nothing.
    /// </summary>
    [CircuitRF.Ui.Tests.Em3d.GmshFact]
    public void ABlindViaOverhangingItsPlanesEdge_LeavesNoFaceUnclaimed()
    {
        var tech = Tech(in1: true, in2: false, bottom: false);
        var (setup, source) = Source(tech,
        [
            new RectShape { Layer = L1, X1 = 0, Y1 = Mm(-0.15), X2 = Mm(10), Y2 = Mm(0.15) },
            Pour(L2),
            new ViaShape { Layer = new LayerKey(10, 0), X = Mm(5), Y = Mm(5), DrillSize = Mm(0.3), PadSize = Mm(0.6) },
            Label("P1", 0, L1, L1, PlanarPortKind.Edge, LayoutRotation.R0),
            Label("P2", 10, L1, L1, PlanarPortKind.Edge, LayoutRotation.R180),
        ]);
        var face = new EmAirBoxFace(1000, null);
        setup.AirBox = new EmAirBox(face, face, face, face, face, face);
        var g = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(g.Ok, g.Refusal);
        var low = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace));

        var step = PalaceRun.Mesh(Path.Combine(_root, "overhang"), low, Gmsh(), null, default);

        Assert.True(step.Ok, step.Message);
    }

    private static string Gmsh() => SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)).Installation!.Path;

    // ── R-dfr26-1-3 / -4 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AGerberImport_MarksThePlanes_LeavesFloodedOuterLayersAsSignal_AndOpensTheBottom()
    {
        const string header = "%FSLAX46Y46*%\n%MOMM*%\n";
        static string Flood(string fn) => header + $"%TF.FileFunction,{fn}*%\nG01*\n" +
            "G36*\nX0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\nG37*\nM02*\n";
        static string Pad(string fn) => header + $"%TF.FileFunction,{fn}*%\n%ADD10C,1.500*%\nD10*\nX5000000Y5000000D03*\nM02*\n";
        string src = Directory.CreateDirectory(Path.Combine(_root, "set")).FullName;
        File.WriteAllText(Path.Combine(src, "b.gtl"), Flood("Copper,L1,Top,Signal"));
        File.WriteAllText(Path.Combine(src, "b.g2"), Flood("Copper,L2,Inr,Plane"));
        File.WriteAllText(Path.Combine(src, "b.g3"), Flood("Copper,L3,Inr,Plane"));
        File.WriteAllText(Path.Combine(src, "b.gbl"), Pad("Copper,L4,Bot,Signal"));
        string parent = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;

        var r = GerberImport.Import([.. Directory.EnumerateFiles(src).Order()], parent, "board", null, Dbu, null, null);

        Assert.NotNull(r.TechPath);
        var tech = TechPersistence.LoadFromFile(r.TechPath!);
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        Assert.Equal([false, true, true, false], conductors.Select(c => c.IsGroundReference));
        Assert.Equal(BoundaryCondition.Open, tech.Stackup.Bottom);
        string marked = r.Messages.Single(m => m.Contains("marked as a ground reference", StringComparison.Ordinal));
        Assert.Contains($"'{conductors[1].Name}' (100%)", marked, StringComparison.Ordinal);
        Assert.Contains($"'{conductors[2].Name}' (100%)", marked, StringComparison.Ordinal);
        Assert.Contains($"'{conductors[0].Name}' (100%) covers as much but is an outer layer", marked, StringComparison.Ordinal);
        Assert.Contains("Technology editor ▸ Stackup", marked, StringComparison.Ordinal);
        Assert.Contains(r.Messages, m => m.Contains("Bottom boundary was set to Open", StringComparison.Ordinal));
        Assert.Empty(TechValidation.Analyze(tech).Where(p => p.Area == TechProblemArea.Stackup));
    }

    // ── R-dfr26-1-5 ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARefusedPort_WithPortsOptional_KeepsTheSolids_AndHoldsTheRefusalAside()
    {
        var tech = Tech(in1: false, in2: false, bottom: true);
        var shapes = TraceOverPours(Label("P1", 0, Text, null, PlanarPortKind.Edge, LayoutRotation.R0),
                                    Label("P2", 10, L1, L1, PlanarPortKind.Edge, LayoutRotation.R180));
        var (setup, source) = Source(tech, shapes);

        var solve = Em3dGenerator.Generate(setup, source, tech);
        var picture = Em3dGenerator.Generate(setup, source, tech, portsOptional: true);

        Assert.False(solve.Ok);
        Assert.NotNull(picture.Problem);
        Assert.Empty(picture.Problem!.Ports);
        Assert.Contains(picture.Problem.Solids, s => s.Name.StartsWith("Top Copper (1 oz)/", StringComparison.Ordinal));
        Assert.Equal(solve.Refusal, picture.PortRefusal);
    }
}
