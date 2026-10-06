// ================================================================
//  OpenEmsCylindricalGridTests.cs — the gate for brief-em3d-120: openEMS on a cylindrical grid.
//  Gates 1, 3 and 5 need nothing installed; gate 2 needs the geometry kernel (it skips without one). Gate 4 is the existing
//  goldens, unchanged: OpenEmsBackendTests.Gate4 (lumped) and OpenEmsWavePortTests.Gate1 (coax-wave). Gate 6 runs openEMS
//  (Benchmark tier) and skips without a validated one.
//
//  Golden: testdata/em3d/openems-goldens/coax-cylindrical/p1.xml. To rewrite it after a DELIBERATE writer change, run gate 1
//  with CRF_WRITE_OPENEMS_GOLDENS=1 and review the diff.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class OpenEmsCylindricalGridTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-ocyl-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;                       // DBU per µm
    private const double C0 = 299_792_458.0;
    private const double PinR = 200e-6, BoreR = 670e-6;
    /// <summary>The coax's closed form with η₀ (113-a R-em3d113a-1b).</summary>
    private static readonly double ZCoax = 376.730313668 / (2 * Math.PI * Math.Sqrt(2.1)) * Math.Log(BoreR / PinR);

    // ── 1. the lowering, against upstream's own cylindrical coax ───────────────────────────────

    [Fact]
    public void Gate1_CoaxSection_HeadGridShapesAndSource_AreUpstreamsCylindricalCoax()
    {
        var (_, grid, low) = Lower(Coax(), Setup(OpenEmsGridKind.Cylindrical));
        var cyl = Assert.IsType<FdtdCylinder>(grid.Cylinder);
        output.WriteLine($"grid {grid.X.Lines.Count} ρ × {cyl.AzimuthCells} α × {grid.Z.Lines.Count} z; {cyl.AzimuthSetBy}");
        foreach (var n in low.Notes) output.WriteLine("note: " + n);
        var ours = XDocument.Parse(low.PortFiles[0]);
        var theirs = XDocument.Load(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "terminal", "openems", "coax-cylindrical", "model.xml"));

        // every element path upstream's file has, ours has with the same attribute names (brief 9 gate 5's comparison)
        var mine = OpenEmsBackendTests.Paths(ours);
        foreach (var (path, attributes) in OpenEmsBackendTests.Paths(theirs))
        {
            Assert.True(mine.ContainsKey(path), $"missing {path}");
            Assert.Equal(attributes, mine[path]);
        }
        Assert.Equal("1", ours.Descendants("FDTD").Single().Attribute("CylinderCoords")!.Value);
        Assert.Equal("1", ours.Descendants("ContinuousStructure").Single().Attribute("CoordSystem")!.Value);

        // ρ from the pin's surface to the shield's box; α a full 2π written to the last digit (R-em3d120-1d); the rule's count
        Assert.Equal(PinR, grid.X.Lines[0], 15);
        Assert.Equal("pin", cyl.RhoMinSetBy);
        Assert.Contains(grid.X.Lines, r => Math.Abs(r - BoreR) < 1e-15);
        Assert.Equal(2 * Math.PI, grid.Y.Lines[^1]);
        Assert.Equal(0, cyl.AzimuthCells % FdtdGrid.AzimuthMultiple);
        int bore = grid.X.Lines.ToList().FindIndex(r => Math.Abs(r - BoreR) < 1e-15);
        Assert.True(BoreR * 2 * Math.PI / cyl.AzimuthCells <= grid.X.Lines[bore] - grid.X.Lines[bore - 1], "the arc at the bore is no longer than the radial cell");

        // the coaxial cylinders are Boxes in (ρ, α, z), exact on the circles; the shield a Cartesian LinPoly
        var ptfe = ours.Descendants("Material").Single(e => e.Attribute("Name")!.Value == "ptfe").Descendants("Box").Single();
        Assert.Null(ptfe.Attribute("CoordSystem"));
        Assert.Equal((0.0, BoreR), (D(ptfe.Element("P1")!, "X"), D(ptfe.Element("P2")!, "X")));
        Assert.Equal(grid.Y.Lines[^1], D(ptfe.Element("P2")!, "Y"));
        Assert.Equal("0", ours.Descendants("Metal").Single(e => e.Attribute("Name")!.Value == "shield").Descendants("LinPoly").Single().Attribute("CoordSystem")!.Value);

        // the source: one radial component ∝ 1/ρ on a Box spanning the annulus at the source plane
        var exc = ours.Descendants("Excitation").Single(e => e.Parent!.Name == "Properties");
        Assert.Equal("1,0,0", exc.Attribute("Excite")!.Value);
        Assert.Equal(("0.0002/rho", "0", "0"), (exc.Element("Weight")!.Attribute("X")!.Value, exc.Element("Weight")!.Attribute("Y")!.Value,
                                                 exc.Element("Weight")!.Attribute("Z")!.Value));
        var src = exc.Descendants("Box").Single();
        Assert.Equal((PinR, BoreR), (D(src.Element("P1")!, "X"), D(src.Element("P2")!, "X")));

        // probes: voltage along ρ at α = 0 on grid lines; current discs whose rim is a dual line, a cell clear of the shield
        var u = ours.Descendants("ProbeBox").Single(e => e.Attribute("Name")!.Value == "port1_u").Descendants("Box").Single();
        Assert.Equal((BoreR, 0.0, PinR, 0.0), (D(u.Element("P1")!, "X"), D(u.Element("P1")!, "Y"), D(u.Element("P2")!, "X"), D(u.Element("P2")!, "Y")));
        var t = Assert.Single(low.WaveTerminals, w => w.Port == 1);
        double rim = t.CurrentBox.U1;
        int k = grid.X.Lines.ToList().FindIndex(r => r > rim);
        Assert.Equal((grid.X.Lines[k - 1] + grid.X.Lines[k]) / 2, rim);
        Assert.True(t.ClearanceCells >= 1, $"{t.ClearanceCells} cells");

        string file = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "openems-goldens", "coax-cylindrical", "p1.xml");
        if (Environment.GetEnvironmentVariable("CRF_WRITE_OPENEMS_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, System.Text.Encoding.UTF8.GetBytes(low.PortFiles[0]));
        }
        Assert.Equal(File.ReadAllText(file), low.PortFiles[0]);
    }

    // ── 2. a boolean's operands ─────────────────────────────────────────────────────────────────

    /// <summary>The 3D Connector's housing (a box with its bore subtracted and kept as PTFE) along x: written as the box, and the
    /// bore cut out at the priority just above it in the PTFE's own property. A bore NOT kept, with the PTFE drawn as a solid of
    /// its own under it (below the housing in precedence), cannot be cut by priority — the hole would put background where the
    /// PTFE is: the tessellation, and a note naming why.</summary>
    [KernelFact]
    public void Gate2_TheHousing_IsBoxPlusBoreWithPriorities_AndANonEquivalentToolKeepsItsTessellation()
    {
        var (_, _, low) = Lower(Connector(keep: true), Setup(OpenEmsGridKind.Cylindrical, OpenEmsGridAxis.X, [0, 0, 870]));
        var doc = XDocument.Parse(low.Model!);
        var housing = doc.Descendants("Metal").Single(e => e.Attribute("Name")!.Value == "housing");
        var blank = housing.Descendants("Box").Single();
        Assert.Equal("0", blank.Attribute("CoordSystem")!.Value);
        Assert.Empty(doc.Descendants("PolyhedronReader"));
        var bore = doc.Descendants("Material").Single(e => e.Attribute("Name")!.Value == "bore").Descendants("Box").ToList();
        Assert.Equal(2, bore.Count);                    // its own, and the hole above the housing
        Assert.Equal(int.Parse(blank.Attribute("Priority")!.Value) + 1, bore.Max(b => int.Parse(b.Attribute("Priority")!.Value)));
        Assert.All(bore, b => Assert.Null(b.Attribute("CoordSystem")));
        // the hole stops a hair inside the bore's radius, so the grid nodes ON the bore stay metal (R-em3d120-4b)
        var hole = bore.MaxBy(b => int.Parse(b.Attribute("Priority")!.Value))!;
        Assert.InRange(D(hole.Element("P2")!, "X"), BoreR * (1 - 1e-4), BoreR * (1 - 1e-12));
        Assert.Contains(low.Notes, n => n.StartsWith("'housing' is written as its Blank with each Tool cut out by priority"));

        var (_, _, cut) = Lower(Connector(keep: false), Setup(OpenEmsGridKind.Cylindrical, OpenEmsGridAxis.X, [0, 0, 870]));
        Assert.Single(XDocument.Parse(cut.Model!).Descendants("Metal").Single(e => e.Attribute("Name")!.Value == "housing").Descendants("PolyhedronReader"));
        Assert.Contains(cut.Notes, n => n.StartsWith("'housing' is written as its kernel tessellation, not as its Blank and Tools: 'fill' overlaps its Tool 'bore'"));
    }

    // ── 3. replay of 113-a's cylindrical coax ───────────────────────────────────────────────────

    /// <summary>
    /// 113-a's openEMS cylindrical coax (141 azimuth lines), its probe files read by OpenEmsRun with the current averaged over the
    /// two planes (116): the line's Z within 0.5 Ω of the closed form, and the forward wave's phase between the two stations
    /// (5 mm apart) within 1° of −βℓ, at 2, 6 and 10 GHz.
    /// </summary>
    [Fact]
    public void Gate3_113aCylindricalCoax_ThroughTheReaders_MeetsHalfAnOhmAndOneDegree()
    {
        string dir = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "terminal", "openems", "coax-cylindrical");
        double[] f = [2e9, 6e9, 10e9];
        FdtdProbe P(string name) => OpenEmsRun.ReadProbe(Path.Combine(dir, name), out string? e) ?? throw new InvalidOperationException(e);
        var line = FdtdLineProbes.Measure(P("ut1A"), P("ut1B"), P("ut1C"), P("it1A"), P("it1B"), 20e-6, f);
        var forward = new Complex[2][];
        for (int s = 1; s <= 2; s++)
        {
            var port = OpenEmsRun.ReadPort(dir, new OpenEmsProbeNames(s, [$"ut{s}B"], [$"it{s}A", $"it{s}B"]), out string? error);
            Assert.True(port is not null, error);
            var uf = FdtdPortTransform.Dft(port!.Voltage, f);
            var jf = FdtdPortTransform.Dft(port.Current, f);
            forward[s - 1] = [.. uf.Select((x, i) => x + line.Z[i] * jf[i])];          // the incident wave U + Z0·I, Z0 the line's own
        }
        for (int i = 0; i < f.Length; i++)
        {
            double phase = (forward[1][i] / forward[0][i]).Phase * 180 / Math.PI;
            double expected = -2 * Math.PI * f[i] * Math.Sqrt(2.1) / C0 * 5e-3 * 180 / Math.PI;
            double err = Math.IEEERemainder(phase - expected, 360);
            output.WriteLine($"{f[i] / 1e9} GHz: Z {line.Z[i].Real:F3} Ω (closed form {ZCoax:F3}); ∠S21 {phase:F3}° against −βℓ {expected:F3}° ({err:+0.000;-0.000;0.000}°)");
            Assert.InRange(line.Z[i].Real, ZCoax - 0.5, ZCoax + 0.5);
            Assert.InRange(err, -1, 1);
        }
    }

    // ── 5. refusals, before openEMS starts ──────────────────────────────────────────────────────

    [Fact]
    public void Gate5_AnAxisOffTheCoax_ASolidPastRhoMax_ALumpedPort_AndACem_AreRefusedNamingTheFix()
    {
        string Refused(C3dDocument doc, EmSetup setup)
        {
            var (problem, grid) = Problem(doc, setup);
            var low = CsxcadWriter.Write(problem, grid, CemOpenEms.ResolveGrid(setup.OpenEms), CemOpenEms.ResolveRun(setup.OpenEms));
            Assert.False(low.Ok);
            output.WriteLine(low.Refusal);
            return low.Refusal!;
        }

        Assert.StartsWith("Terminal P1's conductor on the zmin face is centred 100 µm off the cylindrical grid's axis (z through (x 100 µm, y 0 µm)): " +
                          "the axis is not coaxial with that wave port's conductors. Set OpenEms.AxisOriginUm",
                          Refused(Coax(), Setup(OpenEmsGridKind.Cylindrical, origin: [100, 0, 0])));

        var outside = Coax();
        outside.Objects.Add(new C3dBox { Name = "slab", Material = "PTFE", Min = new C3dPoint3(800 * Um, -100 * Um, 0), Size = new C3dPoint3(200 * Um, 200 * Um, 1000 * Um) });
        var wide = Setup(OpenEmsGridKind.Cylindrical);
        wide.AirBox = wide.AirBox! with { XMax = new EmAirBoxFace(400, Em3dBoundaryKind.Pec) };
        Assert.StartsWith("'slab' reaches", Refused(outside, wide));

        var lumped = Coax();
        lumped.Ports[1] = new C3dPort
        {
            Number = 2, Name = "P2", Kind = Em3dPortKind.Lumped, Plane = C3dPlane.XZ, Offset = 0, Rect = Rect(200, 400, 470, 200),
            Positive = "pin", Negative = "shield",
        };
        Assert.StartsWith("Port 2 is a lumped port, and circuitRF writes openEMS's lumped port on a Cartesian grid only.", Refused(lumped, Setup(OpenEmsGridKind.Cylindrical)));

        // a .cem naming the same .c3d: refused at its door, before any solver is looked for
        var doc = Coax();
        string ws = Workspace();
        string path = WriteC3d(ws, "Coax", doc);
        var run = EmRunService.RunThreeDView(Setup(OpenEmsGridKind.Cylindrical), doc, path, Path.Combine(ws, ".cws"), Path.Combine(_root, "r"), fromCem: true);
        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains("a cylindrical grid is written for a 3D view's own setup only", run.Error);
    }

    // ── 6. end to end ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The coax section through `circuitrf em` on a cylindrical grid: the line's measured Z within 0.5 Ω of the closed form and
    /// ∠S21 within 1° of −βℓ over the section. The same .c3d on a Cartesian grid carries the note naming the phase error the
    /// staircase brings (its lowering's own note; not run again).
    /// </summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void Gate6_CoaxSection_EndToEnd_CylindricalMeetsHalfAnOhmAndOneDegree_CartesianNamesItsError()
    {
        var doc = Coax();
        doc.Setups = [EmSetupPersistence.ToEmbedded(Setup(OpenEmsGridKind.Cylindrical))];
        string ws = Workspace();
        string path = WriteC3d(ws, "Coax", doc);
        var watch = Stopwatch.StartNew();
        var (code, stdout, stderr) = RunCli("em", path);
        output.WriteLine($"{watch.Elapsed.TotalSeconds:F1} s");
        Assert.True(code == 0, stdout + stderr);
        var m = Regex.Match(stderr, @"note: Terminal P1: the line measured Z ([0-9.]+) Ω, ε_eff ([0-9.]+) at ([0-9.]+) GHz\.");
        Assert.True(m.Success, stderr);
        double z = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine($"Z {z} Ω against {ZCoax:F3} Ω");
        Assert.InRange(z, ZCoax - 0.5, ZCoax + 0.5);

        string snp = Regex.Match(stdout, @"^Wrote (.*\.s2p)$", RegexOptions.Multiline).Groups[1].Value;
        var s = RfCore.TouchstoneIO.ReadFile(snp);
        for (int k = 0; k < s.Frequencies.Length; k++)
        {
            double fk = s.Frequencies[k];
            double phase = s[k][1, 0].Phase * 180 / Math.PI, expected = -2 * Math.PI * fk * Math.Sqrt(2.1) / C0 * LengthM * 180 / Math.PI;
            double err = Math.IEEERemainder(phase - expected, 360);
            output.WriteLine($"{fk / 1e9} GHz: ∠S21 {phase:F3}° against −βℓ {expected:F3}° ({err:+0.000;-0.000;0.000}°), |S11| {20 * Math.Log10(s[k][0, 0].Magnitude):F1} dB");
            Assert.InRange(err, -1, 1);
        }

        var (_, _, cartesian) = Lower(Coax(), Setup(OpenEmsGridKind.Cartesian));
        Assert.Contains(cartesian.Notes, n => n.Contains("Brief 113-a measured such a coax 2.4 % slow in phase velocity at 20 cells across its pin") &&
                                             n.Contains("set the openEMS section's Grid to Cylindrical, Axis Z, AxisOriginUm [0, 0, 0]"));
    }

    // ══ fixtures ════════════════════════════════════════════════════════════════════════════════

    private const double LengthM = 3e-3;

    /// <summary>The 3D Connector's coax along z, 3 mm: pin ⌀ 0.4 mm, PTFE ⌀ 1.34 mm in a 1.4 mm square shield with a 64-gon
    /// bore (116's coax), a wave port at each end with its path from the shield to the pin.</summary>
    private static C3dDocument Coax()
    {
        long len = (long)(LengthM * 1e6) * Um;
        C3dPort End(int n, long offset) => new()
        {
            Number = n, Name = $"P{n}", Kind = Em3dPortKind.Wave, Plane = C3dPlane.XY, Offset = offset, Rect = Rect(-700, -700, 1400, 1400),
            Positive = "pin", Negative = "shield", VoltagePath = new C3dVoltagePath { From = P(670, 0), To = P(200, 0) },
        };
        return new C3dDocument
        {
            Objects =
            [
                new C3dPrism
                {
                    Name = "shield", Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Height = len,
                    Outline = [P(-700, -700), P(700, -700), P(700, 700), P(-700, 700)],
                    Holes = [[.. Enumerable.Range(0, 64).Select(k => new C3dPoint2((long)Math.Round(670 * Um * Math.Cos(2 * Math.PI * k / 64)),
                                                                                  (long)Math.Round(670 * Um * Math.Sin(2 * Math.PI * k / 64))))]],
                },
                new C3dCylinder { Name = "ptfe", Material = "PTFE", Base = new C3dPoint3(0, 0, 0), Axis = C3dAxis.Z, Length = len, Radius = 670 * Um },
                new C3dCylinder { Name = "pin", Material = "Copper", Base = new C3dPoint3(0, 0, 0), Axis = C3dAxis.Z, Length = len, Radius = 200 * Um },
            ],
            Ports = [End(1, 0), End(2, len)],
        };
    }

    /// <summary>The 3D Connector's housing along x (a 4 mm box, its axis at z = 0.87 mm) with its bore subtracted — kept as PTFE,
    /// or not kept with the PTFE drawn as its own 'fill' — and a pin, wave ports on both x faces.</summary>
    private static C3dDocument Connector(bool keep)
    {
        const long zc = 870;
        C3dPort End(int n, long x) => new()
        {
            Number = n, Name = $"P{n}", Kind = Em3dPortKind.Wave, Plane = C3dPlane.YZ, Offset = x * Um, Rect = Rect(-700, zc - 700, 1400, 1400),
            Positive = "pin", Negative = "housing", VoltagePath = new C3dVoltagePath { From = P(670, zc), To = P(200, zc) },
        };
        C3dCylinder Along(string name, string material, long r) => new()
        {
            Name = name, Material = material, Base = new C3dPoint3(0, 0, zc * Um), Axis = C3dAxis.X, Length = 4000 * Um, Radius = r * Um,
        };
        var housing = new C3dBoolean
        {
            Name = "housing", Op = C3dBooleanOp.Subtract, KeepTools = keep,
            Blank = new C3dBox { Material = "Copper", Min = new C3dPoint3(0, -700 * Um, (zc - 700) * Um), Size = new C3dPoint3(4000 * Um, 1400 * Um, 1400 * Um) },
            Tools = [Along("bore", "PTFE", 670)],
        };
        var doc = new C3dDocument { Objects = [housing, Along("pin", "Copper", 200)], Ports = [End(1, 0), End(2, 4000)] };
        if (!keep) doc.Objects.Add(Along("fill", "PTFE", 670));
        return doc;
    }

    /// <summary>2 to 10 GHz (113-a's band), PEC sides, both ends stated PEC (D9 lowers each wave-port face absorbing), no field.</summary>
    private static EmSetup Setup(OpenEmsGridKind grid, OpenEmsGridAxis axis = OpenEmsGridAxis.Z, List<double>? origin = null) => new()
    {
        Name = "S1", Solver3D = Em3dSolver.OpenEms,
        Frequency = new CircuitRF.Core.Design.FrequencySpec("2", "10", 5, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        AirBox = new EmAirBox(new EmAirBoxFace(0, Em3dBoundaryKind.Pec), new EmAirBoxFace(0, Em3dBoundaryKind.Pec),
                              new EmAirBoxFace(0, Em3dBoundaryKind.Pec), new EmAirBoxFace(0, Em3dBoundaryKind.Pec),
                              new EmAirBoxFace(0, Em3dBoundaryKind.Pec), new EmAirBoxFace(0, Em3dBoundaryKind.Pec)),
        OpenEms = new CemOpenEms
        {
            CellsPerWavelength = 600, MinCellUm = 2, SaveFieldsGHz = [],
            Grid = grid == OpenEmsGridKind.Cylindrical ? grid : null,
            Axis = axis == OpenEmsGridAxis.Z ? null : axis,
            AxisOriginUm = origin,
        },
    };

    private (Em3dProblem Problem, FdtdGridResult Grid, CsxcadLowering Low) Lower(C3dDocument doc, EmSetup setup)
    {
        var (problem, grid) = Problem(doc, setup);
        var low = CsxcadWriter.Write(problem, grid, CemOpenEms.ResolveGrid(setup.OpenEms), CemOpenEms.ResolveRun(setup.OpenEms));
        Assert.True(low.Ok, low.Refusal);
        return (problem, grid, low);
    }

    private (Em3dProblem Problem, FdtdGridResult Grid) Problem(C3dDocument doc, EmSetup setup)
    {
        string ws = Workspace();
        string path = WriteC3d(ws, "Cell", doc);
        var r = C3dProblemAssembly.Assemble(setup, doc, path, Path.Combine(ws, ".cws"), new C3dElaborator());
        Assert.True(r.Ok, r.Refusal);
        var grid = FdtdGrid.Build(r.Problem!, CemOpenEms.ResolveGrid(setup.OpenEms), long.MaxValue);
        output.WriteLine($"grid {grid.X.Lines.Count} × {grid.Y.Lines.Count} × {grid.Z.Lines.Count} = {grid.Cells:N0} cells, Δt ≈ {grid.TimeStepEstimateS:G4} s");
        return (r.Problem!, grid);
    }

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "PTFE", Epsr = 2.1 }, new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(OpenEmsCylindricalGridTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        string stdout = outTask.GetAwaiter().GetResult(), stderr = errTask.GetAwaiter().GetResult();
        output.WriteLine($"$ {string.Join(' ', args)}\n{stdout}{stderr}");
        return (proc.ExitCode, stdout, stderr);
    }

    private static C3dPoint2 P(long u, long v) => new(u * Um, v * Um);
    private static C3dRect Rect(long u, long v, long du, long dv) => new() { Min = P(u, v), Size = P(du, dv) };
    private static double D(XElement e, string attr) => double.Parse(e.Attribute(attr)!.Value, System.Globalization.CultureInfo.InvariantCulture);
}
