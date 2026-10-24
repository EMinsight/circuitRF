// brief-emdc-1 §5 tests 7–11 — a 3D problem's DC conduction solve end to end through Gmsh: what is meshed, where a contact
// lies, the DC block that started the series, the refusals, and the two routes to an Em3dProblem.

using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class Em3dDcLoweringTests(ITestOutputHelper output) : IDisposable
{
    private const double Mm = 1e-3, Um = 1e-6, Copper = 5.8e7;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emdc-" + Guid.NewGuid().ToString("N")[..10]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Dir(string name) => Path.Combine(_root, name);

    private static readonly Em3dMaterial[] Materials =
    [
        new("Cu", 1, null, 0, 1, Copper),
        new("Sub", 3.5, null, 0, 1, 0),
        new("Air", 1, null, 0, 1, 0),
    ];

    private static Em3dSolid Box(string name, string material, Em3dRole role, double x0, double y0, double z0, double x1, double y1, double z1, int order)
        => new(name, material, role, new Em3dBox(new(x0, y0, z0), new(x1, y1, z1)), order);

    private static Em3dFaces All(Em3dBoundaryKind k) => new(k, k, k, k, k, k);

    private static Em3dPort Lumped(int n, string pos, string neg, double x, double y0, double y1, double z0, double z1)
        => new(n, $"P{n}", pos, neg, new(x, y0, z0), new(x, y1, z1), new(0, 0, 1), 50, new Em3dReferencePlane(new(x, 0, 0), new(1, 0, 0), 0));

    /// <summary>A 1 mm × 100 µm copper ground under 200 µm of substrate, and on it a 500 µm wide copper trace (100 µm thick) from x = 0 to
    /// 2 mm cut by a 100 µm gap at its middle (or with the gap filled by a third copper box); a lumped port at each end, from the
    /// ground's top up to the trace.</summary>
    private static Em3dProblem GapCapacitor(bool filled)
    {
        const double h = 0.2 * Mm, tt = 100 * Um, w = 0.25 * Mm;
        var solids = new List<Em3dSolid>
        {
            Box("sub", "Sub", Em3dRole.Dielectric, 0, -0.5 * Mm, 0, 2 * Mm, 0.5 * Mm, h, 1),
            Box("gnd", "Cu", Em3dRole.Conductor, 0, -0.5 * Mm, -0.1 * Mm, 2 * Mm, 0.5 * Mm, 0, 2),
            Box("t1", "Cu", Em3dRole.Conductor, 0, -w, h, 0.95 * Mm, w, h + tt, 3),
            Box("t2", "Cu", Em3dRole.Conductor, 1.05 * Mm, -w, h, 2 * Mm, w, h + tt, 4),
        };
        if (filled) solids.Add(Box("fill", "Cu", Em3dRole.Conductor, 0.95 * Mm, -w, h, 1.05 * Mm, w, h + tt, 5));
        return new Em3dProblem(solids, [], Materials,
                               [Lumped(1, "t1", "gnd", 0, -w, w, 0, h), Lumped(2, "t2", "gnd", 2 * Mm, -w, w, 0, h)],
                               new Em3dAirBox(new(0, -0.5 * Mm, -0.1 * Mm), new(2 * Mm, 0.5 * Mm, 1 * Mm), All(Em3dBoundaryKind.Absorbing)),
                               new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
    }

    // ── 7. only conductors are meshed ────────────────────────────────────────────────────────────

    /// <summary>A plated via in a substrate over a ground: the meshed regions are the two conductors only, the bore is a void, and
    /// the barrel's meshed volume is the tube's, π(r² − rᵢ²)h, not the solid cylinder's.</summary>
    [GmshFact]
    public void OnlyConductorsAreMeshed_AndAViasBoreIsCutOutOfItsBarrel()
    {
        const double h = 0.5 * Mm, r = 0.2 * Mm, ri = 0.15 * Mm;
        var problem = new Em3dProblem(
            [
                Box("sub", "Sub", Em3dRole.Dielectric, -1 * Mm, -1 * Mm, 0, 1 * Mm, 1 * Mm, h, 1),
                Box("gnd", "Cu", Em3dRole.Conductor, -1 * Mm, -1 * Mm, -0.1 * Mm, 1 * Mm, 1 * Mm, 0, 2),
                new("barrel", "Cu", Em3dRole.Conductor, new Em3dCylinder(new(0, 0, 0), new(0, 0, h), r), 3),
                new("bore", "Air", Em3dRole.Air, new Em3dCylinder(new(0, 0, 0), new(0, 0, h), ri), 4),
            ],
            [], Materials, [Lumped(1, "barrel", "gnd", 0.5 * Mm, -0.1 * Mm, 0.1 * Mm, 0, h)],
            new Em3dAirBox(new(-1 * Mm, -1 * Mm, -0.1 * Mm), new(1 * Mm, 1 * Mm, 1 * Mm), All(Em3dBoundaryKind.Absorbing)),
            new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
        var low = Em3dDcLowering.Build(problem, out string? refusal);
        Assert.True(low is not null, refusal);
        Assert.Equal(["gnd", "barrel"], low!.Input.Solids.Select(s => s.Name));
        Assert.All(low.Input.Solids, s => Assert.Equal(Em3dRole.Conductor, s.Role));
        Assert.Equal(["bore"], low.Input.Voids.Select(s => s.Name));

        string dir = Dir("via");
        var step = PalaceRun.Mesh(dir, low.Gmsh, SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)).Installation!.Path, null, default);
        Assert.True(step.Ok, step.Message);
        var raw = MshReader.Read(Path.Combine(dir, GmshGeoWriter.MeshFile));
        int barrelGroup = low.Gmsh.Groups.Single(g => g.Name == "barrel").Attribute;
        double barrel = 0;
        for (int e = 0; e < raw.TetCount; e++) if (raw.TetPhysical[e] == barrelGroup) barrel += TetVolume(raw, e);
        double tube = Math.PI * (r * r - ri * ri) * h;
        output.WriteLine($"barrel's meshed volume {barrel * 1e9:G6} mm³, the tube's {tube * 1e9:G6} mm³");
        Assert.InRange(barrel / tube, 0.97, 1.01);
    }

    // ── 8. contacts ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A microstrip-style lumped port's positive contact is the trace's END face (500 µm × 100 µm), not its underside,
    /// and its negative contact is on the ground body — its end face, the smallest surface the port's bottom edge lies on.</summary>
    [GmshFact]
    public void Contacts_APortsPositiveIsTheTracesEndFace_ItsNegativeIsOnTheGround()
    {
        var prep = Em3dDcSolve.Prepare(GapCapacitor(filled: false), Dir("contacts"), default);
        Assert.True(prep.Meshed is not null, prep.Refusal);
        foreach (var n in prep.Notes) output.WriteLine(n);
        var pos = prep.Meshed!.Contacts.Single(c => c.Label == "P1+");
        var neg = prep.Meshed.Contacts.Single(c => c.Label == "P1−");
        Assert.Equal("t1", pos.Object);
        Assert.Single(pos.Surfaces);
        Assert.Equal(0.5 * Mm * 100 * Um, pos.AreaM2, 1e-12);
        Assert.Equal("gnd", neg.Object);
        Assert.Single(neg.Surfaces);
        Assert.Equal(1 * Mm * 0.1 * Mm, neg.AreaM2, 1e-12);
        Assert.Contains(prep.Notes, x => x.StartsWith("P1+: 't1' surface", StringComparison.Ordinal) && x.Contains("50000 µm²", StringComparison.Ordinal));
    }

    // ── 9. the DC block that started this ────────────────────────────────────────────────────────

    /// <summary>A 3D series gap capacitor — two traces over a ground plane, ports at the outer ends — is an open at DC,
    /// |S₂₁| &lt; 1e-12; with the gap filled by metal it is a through, S₂₁ within 1e-4 of 1.</summary>
    [GmshFact]
    public void TheGapCapacitor_IsAnOpen_AndFilledItIsAThrough()
    {
        var open = Em3dDcSolve.Solve(GapCapacitor(filled: false), Dir("gap"), default);
        Assert.True(open.S is not null, open.Refusal);
        foreach (var n in open.Notes) output.WriteLine(n);
        Assert.True(Math.Abs(open.S![1, 0]) < 1e-12, $"|S21| = {Math.Abs(open.S[1, 0]):G3}");
        var through = Em3dDcSolve.Solve(GapCapacitor(filled: true), Dir("filled"), default);
        Assert.True(through.S is not null, through.Refusal);
        foreach (var n in through.Notes) output.WriteLine(n);
        Assert.Equal(1, through.S![1, 0], 1e-4);
    }

    // ── 10. refusals ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A hollow waveguide's wave port (both contacts are the wall's one cross-section), a port with Re Z₀ = 0, a
    /// non-driven problem and a lumped element stating no R, L or C are each refused, with the sentence that says why.</summary>
    [GmshFact]
    public void Refusals_AWaveguidePort_AZeroZ0_AndANonDrivenProblem()
    {
        const double a = 1 * Mm, b = 0.5 * Mm, t = 0.1 * Mm, l = 1 * Mm;
        var faces = new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                  Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing);
        var lo = new Point3(0, -a / 2 - t, -b / 2 - t);
        var hi = new Point3(l, a / 2 + t, b / 2 + t);
        var guide = new Em3dProblem(
            [
                Box("wall", "Cu", Em3dRole.Conductor, lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z, 1),
                Box("inside", "Air", Em3dRole.Air, 0, -a / 2, -b / 2, l, a / 2, b / 2, 2),
            ],
            [], Materials,
            [new Em3dPort(1, "P1", "wall", "wall", new(0, lo.Y, lo.Z), new(0, hi.Y, hi.Z), new(0, 0, 1), 50,
                          new Em3dReferencePlane(new(0, 0, 0), new(1, 0, 0), 0)) { Kind = Em3dPortKind.Wave }],
            new Em3dAirBox(lo, hi, faces), new Em3dFrequency(8e9, 12e9, 5, Em3dSweepKind.Linear), 20);
        var r = Em3dDcSolve.Solve(guide, Dir("guide"), default);
        Assert.Null(r.S);
        Assert.Equal("Port P1 has no DC meaning: its positive and negative contacts are the same surface of 'wall', so no DC current can " +
                     "flow through it (a hollow waveguide's wave port is this case).", r.Refusal);

        var shorted = GapCapacitor(filled: true);
        shorted = shorted with { Ports = [shorted.Ports[0] with { Z0 = 0 }, shorted.Ports[1]] };
        Assert.StartsWith("Port P1's reference impedance has a real part of 0 Ω", Em3dDcSolve.Solve(shorted, Dir("z0"), default).Refusal);

        var modes = GapCapacitor(filled: true) with { Type = Em3dProblemType.Eigenmode };
        Assert.Equal("A DC row is solved for a driven problem only; this one is eigenmode, which writes no S-parameters.",
                     Em3dDcSolve.Solve(modes, Dir("modes"), default).Refusal);

        var p = GapCapacitor(filled: false);
        var empty = p with { LumpedElements = [new Em3dLumpedElement("E1", p.Ports[0].Min, p.Ports[0].Max, new(0, 0, 1), null, null, null, Em3dRlcTopology.Series)] };
        Assert.Equal("Lumped element 'E1' states no R, L or C, so it has no DC meaning.", Em3dDcSolve.Solve(empty, Dir("empty"), default).Refusal);
    }

    // ── 11. both routes ──────────────────────────────────────────────────────────────────────────

    /// <summary>A plain trace through each route to an Em3dProblem — Em3dGenerator from a .clay, C3dProblemAssembly from a
    /// .c3d — solves, and its series resistance from S₁₁ agrees with the trace's own L/(σA) to 1 %: the solve reads only the
    /// problem.</summary>
    [GmshFact]
    public void BothRoutes_APlainTrace_IsItsOwnResistance()
    {
        // Em3dGeneratorTests.Microstrip()'s board, its trace 1 mm long rather than 10, so the 1 oz copper's ~17 µm elements stay few
        const string clay = """
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": -250000, "X2": 1000000, "Y2": 250000 },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 0, "Y": 0, "Text": "1", "Height": 100000, "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 1000000, "Y": 0, "Text": "2", "Height": 100000, "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var setup = new EmSetup
        {
            Name = "thru", LayoutRef = "thru/layout/thru.clay",
            Frequency = new FrequencySpec("1", "10", 4, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
        };
        var source = new EmLayoutSource("/nowhere/thru.clay", view, tech, view.DbuPerMicron);
        var fromClay = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(fromClay.Ok, fromClay.Refusal);
        Check("clay", fromClay.Problem!);

        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = Copper }, new TechMaterial { Name = "Fill", Epsr = 2 }, new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        const long um = 1000;
        C3dRect Rect(long u, long v, long du, long dv) => new() { Min = new C3dPoint2(u * um, v * um), Size = new C3dPoint2(du * um, dv * um) };
        var doc = new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(2000 * um, 1000 * um, 100 * um) },
                new C3dBox { Name = "trace", Material = "Copper", Min = new C3dPoint3(0, 400 * um, 100 * um), Size = new C3dPoint3(2000 * um, 200 * um, 50 * um) },
            ],
        };
        doc.Ports.Add(new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.YZ, Offset = 0, Rect = Rect(400, 0, 200, 100) });
        doc.Ports.Add(new C3dPort { Number = 2, Name = "P2", Plane = C3dPlane.YZ, Offset = 2000 * um, Rect = Rect(400, 0, 200, 100) });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var c3dSetup = new EmSetup
        {
            Name = "S1", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "10", 4, SweepKind.Linear, "GHz", "GHz"),
            AirBox = new EmAirBox(ZMin: new EmAirBoxFace(0, Em3dBoundaryKind.Pec)),
        };
        var fromC3d = C3dProblemAssembly.Assemble(c3dSetup, doc, path, Path.Combine(ws, ".cws"));
        Assert.True(fromC3d.Problem is not null, fromC3d.Refusal);
        Check("c3d", fromC3d.Problem!);
    }

    /// <summary>A structure drawn as sheets alone — a 2 mm × 0.5 mm copper sheet 35 µm thick over a PEC floor — meshes as a
    /// surface (the thermal writer refuses a solid-less input unless the DC lowering asks) and is its own L/(σtW).</summary>
    [GmshFact]
    public void ASheetOnlyTrace_IsItsOwnResistance()
    {
        const double h = 0.2 * Mm, w = 0.25 * Mm;
        var trace = new Em3dSheet("trace", "Cu", [new(0, -w), new(2 * Mm, -w), new(2 * Mm, w), new(0, w)], [], h, 35 * Um, 1);
        var problem = new Em3dProblem([], [trace], Materials,
                                      [Lumped(1, "trace", "airbox/zmin", 0, -w, w, 0, h), Lumped(2, "trace", "airbox/zmin", 2 * Mm, -w, w, 0, h)],
                                      new Em3dAirBox(new(0, -0.5 * Mm, 0), new(2 * Mm, 0.5 * Mm, 1 * Mm),
                                                     new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                                                   Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing)),
                                      new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
        Check("sheets", problem);
    }

    /// <summary>The same trace in a box whose every face is PEC, fed by wave ports on its end faces: the trace ends lie in the
    /// ports' rectangles, which the full-wave mesh claims before the faces, so they are not grounded and the trace is still its
    /// own resistance rather than two shorts.</summary>
    [GmshFact]
    public void AWavePortOnAPecFace_DoesNotGroundTheConductorEndingInIt()
    {
        const double h = 0.2 * Mm, w = 0.25 * Mm;
        var trace = new Em3dSheet("trace", "Cu", [new(0, -w), new(2 * Mm, -w), new(2 * Mm, w), new(0, w)], [], h, 35 * Um, 1);
        Em3dPort Wave(int n, double x)
            => new(n, $"P{n}", "trace", "airbox/zmin", new(x, -0.4 * Mm, 0), new(x, 0.4 * Mm, 0.6 * Mm), new(0, 0, 1), 50,
                   new Em3dReferencePlane(new(x, 0, 0), new(1, 0, 0), 0)) { Kind = Em3dPortKind.Wave };
        var problem = new Em3dProblem([], [trace], Materials, [Wave(1, 0), Wave(2, 2 * Mm)],
                                      new Em3dAirBox(new(0, -0.5 * Mm, 0), new(2 * Mm, 0.5 * Mm, 1 * Mm), All(Em3dBoundaryKind.Pec)),
                                      new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
        Check("wave-on-pec", problem);
    }

    /// <summary>The problem's DC two-port, its series R = 2Z₀S₁₁/(1 − S₁₁) against L/(σA) of the conductor port 1 lands on.</summary>
    private void Check(string route, Em3dProblem problem)
    {
        var r = Em3dDcSolve.Solve(problem, Dir(route), default);
        foreach (var n in r.Notes) output.WriteLine($"{route}: {n}");
        Assert.True(r.S is not null, r.Refusal);
        string trace = problem.Ports[0].PositiveObject;
        double sigma, area, length;
        if (problem.Solids.FirstOrDefault(s => s.Name == trace) is { } solid)
        {
            var b = Em3dProblem.Bounds(solid.Primitive);
            sigma = problem.Materials.Single(m => m.Name == solid.Material).SigmaSm;
            (length, area) = (b.X1 - b.X0, (b.Y1 - b.Y0) * (b.Z1 - b.Z0));
        }
        else
        {
            var sh = problem.Sheets.Single(s => s.Name == trace);
            var b = sh.WorldBounds();
            sigma = problem.Materials.Single(m => m.Name == sh.Material).SigmaSm;
            (length, area) = (b.X1 - b.X0, (b.Y1 - b.Y0) * sh.ThicknessM);
        }
        double expected = length / (sigma * area), s11 = r.S![0, 0], z0 = problem.Ports[0].Z0.Real;
        double ohms = 2 * z0 * s11 / (1 - s11);
        output.WriteLine($"{route}: R = {ohms * 1e3:G6} mΩ from S11, L/(σA) = {expected * 1e3:G6} mΩ ({r.Elements:N0} elements, {r.ElapsedMs:F0} ms)");
        Assert.InRange(ohms / expected, 0.99, 1.01);
    }

    // ── sizing: a flat conductor across its width ────────────────────────────────────────────────

    /// <summary>A box is sized by its second-smallest side / 8, never finer than the thermal rule (two through its thinnest): the
    /// trace's 500 µm width gives 62.5 µm where the thermal rule gives 50; the gap's fill, 100 µm wide, keeps the thermal 50. A
    /// cylinder (a bond wire) keeps the thermal rule.</summary>
    [Fact]
    public void Sizing_AFlatConductorIsSizedAcrossItsWidth_NeverFinerThanTheThermalRule_AndAWireKeepsIt()
    {
        var p = GapCapacitor(filled: true);
        var wire = new Em3dSolid("wire", "Cu", Em3dRole.Conductor, new Em3dCylinder(new(0.5 * Mm, 0, 0.3 * Mm), new(1.5 * Mm, 0, 0.3 * Mm), 25 * Um), 6);
        var low = Em3dDcLowering.Build(p with { Solids = [.. p.Solids, wire] }, out string? refusal);
        Assert.True(low is not null, refusal);
        var dc = low.Input;
        var thermal = dc with { InPlaneElements = null };
        double Dc(string n) => GmshGeoWriter.ThermalSolidSizeM(dc, dc.Solids.Single(s => s.Name == n)) / Um;
        double Thermal(string n) => GmshGeoWriter.ThermalSolidSizeM(thermal, thermal.Solids.Single(s => s.Name == n)) / Um;
        Assert.Equal(62.5, Dc("t1"), 9);
        Assert.Equal(50, Thermal("t1"), 9);
        Assert.Equal(50, Dc("fill"), 9);
        Assert.Equal(Thermal("wire"), Dc("wire"), 12);
    }

    private static double TetVolume(MshMesh m, int e)
    {
        double[] P(int k) { int v = m.Tets[4 * e + k]; return [m.Nodes[3 * v] * Um, m.Nodes[3 * v + 1] * Um, m.Nodes[3 * v + 2] * Um]; }
        double[] a = P(0), b = P(1), c = P(2), d = P(3);
        double[] u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], v = [c[0] - a[0], c[1] - a[1], c[2] - a[2]], w = [d[0] - a[0], d[1] - a[1], d[2] - a[2]];
        return Math.Abs(u[0] * (v[1] * w[2] - v[2] * w[1]) - u[1] * (v[0] * w[2] - v[2] * w[0]) + u[2] * (v[0] * w[1] - v[1] * w[0])) / 6;
    }
}
