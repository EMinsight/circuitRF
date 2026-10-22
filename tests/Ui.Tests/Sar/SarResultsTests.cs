// ================================================================
//  SarResultsTests.cs — the gates of brief-sar-6: SAR and B1± as results of a .c3d run.
//
//    SarSetupTests          the Sar group round-trips; two SAR frequencies with a frequency-dependent tissue are refused; the
//                           dumps are cropped to the tissue plus one cell, and the tissue is written with its density
//    B1ConventionTests      a circularly polarised H built by hand: |B1+| = |B| and B1− = 0 for B0 along +z, the reverse for −z
//    SarResultsWiringTests  SAR-1's dipole raw dump through the result path (no openEMS process) gives SAR-5's numbers in the
//                           .npy's sar group, per watt accepted as openEMS's tutorial prints them; render --field draws it
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Em3d.Sar;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Sar;

/// <summary>A workspace whose technology holds copper and Dipole_SAR's three tissues as its script states them, with their
/// densities — and, for the refusal, a frequency-dependent muscle.</summary>
internal static class SarSixFixtures
{
    public const long Mm = SarFixtures.Mm;

    public static (string Path, string Cws) Workspace(string root)
    {
        string ws = Path.Combine(root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 },
                new TechMaterial { Name = "skin", Epsr = 50, Sigma20 = 0.65, DensityKgM3 = 1100 },
                new TechMaterial { Name = "headbone", Epsr = 13, Sigma20 = 0.1, DensityKgM3 = 2000 },
                new TechMaterial { Name = "brain", Epsr = 60, Sigma20 = 0.7, DensityKgM3 = 1040 },
                // Gabriel 1996 muscle, Table 1 (the shipped record's parameters)
                new TechMaterial
                {
                    Name = "Muscle", Epsr = 54.81, Sigma20 = 0.9782, DensityKgM3 = 1050,
                    ColeCole = new TechColeCole
                    {
                        EpsInf = 4, SigmaIonic = 0.2,
                        Terms =
                        [
                            new TechColeColeTerm { DeltaEps = 50, TauSeconds = 7.234e-12, Alpha = 0.1 },
                            new TechColeColeTerm { DeltaEps = 7000, TauSeconds = 3.537e-07, Alpha = 0.1 },
                            new TechColeColeTerm { DeltaEps = 1.2e6, TauSeconds = 3.183e-04, Alpha = 0.1 },
                            new TechColeColeTerm { DeltaEps = 2.5e7, TauSeconds = 2.274e-03, Alpha = 0 },
                        ],
                    },
                },
            ],
        });
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "Cell.c3d"), cws);
    }

    /// <summary>SarFixtures' dipole with Dipole_SAR's head beside it: three nested ellipsoids about (100, 0, 0) mm, semi-axes
    /// 80/100/100, 75/95/95 and 65/85/85 mm — skin, headbone and brain, as the tutorial's XML writes them.</summary>
    public static C3dDocument DipoleAndHead()
    {
        var doc = SarFixtures.Dipole();
        var head = C3dPhantom.New(C3dPhantomShape.Ellipsoid3, "head", new C3dPoint3(100 * Mm, 0, 0), 1000);
        head.SemiAxes = new C3dPoint3(80 * Mm, 100 * Mm, 100 * Mm);
        head.Shell1 = 5 * Mm;
        head.Shell2 = 10 * Mm;
        head.Layers = [new() { Name = "skin", Material = "skin" }, new() { Name = "headbone", Material = "headbone" },
                       new() { Name = "brain", Material = "brain" }];
        doc.Objects.Add(head);
        return doc;
    }

    public static EmSetup Setup(CemSar sar) => new()
    {
        Name = "S", Solver3D = Em3dSolverId.OpenEms,
        Frequency = new FrequencySpec("0.5", "1.5", 11, SweepKind.Linear, "GHz", "GHz"),
        Sar = sar,
    };
}

public sealed class SarSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar6-setup-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>R-sar6-1 — every key of the group survives a .cem and a .c3d's embedding, and a setup without the group writes
    /// no Sar key at all (every existing setup keeps its bytes).</summary>
    [Fact]
    public void TheGroup_RoundTrips_AndASetupWithoutItWritesNoKey()
    {
        var sar = new CemSar
        {
            FrequenciesGHz = [0.298], MassesGrams = [1, 10, 0.5], Method = SarMethod.Simple, Region = ["head"], B1 = true,
            B0Axis = "-z", B1Region = ["head/brain"], CrossCheck = true,
        };
        var setup = SarSixFixtures.Setup(sar);
        string json = EmSetupPersistence.Serialize(setup);
        var back = EmSetupPersistence.Deserialize(json);
        Assert.Equal(json, EmSetupPersistence.Serialize(back));
        Assert.Equal(json, EmSetupPersistence.Serialize(EmSetupPersistence.FromEmbedded(EmSetupPersistence.ToEmbedded(setup))));
        Assert.Equal(JsonSerializer.Serialize(sar), JsonSerializer.Serialize(back.Sar));
        Assert.Equal(JsonSerializer.Serialize(sar), JsonSerializer.Serialize(back.Clone().Sar));

        var s = SarSettings.Resolve(back.Sar!);
        Assert.Equal([0.298e9], s.FrequenciesHz);
        Assert.Equal((2, -1), (s.B0Axis, s.B0Sign));
        Assert.Empty(s.Problems());
        // the defaults: 1 and 10 g, Cube, B1 on below 500 MHz and off above
        Assert.Equal([1.0, 10.0], SarSettings.Resolve(new CemSar { FrequenciesGHz = [0.298] }).MassesGrams);
        Assert.True(SarSettings.Resolve(new CemSar { FrequenciesGHz = [0.298] }).B1);
        Assert.False(SarSettings.Resolve(new CemSar { FrequenciesGHz = [1.0] }).B1);

        Assert.DoesNotContain("\"Sar\"", EmSetupPersistence.Serialize(SarSixFixtures.Setup(null!)));
    }

    /// <summary>L6 — two SAR frequencies with a frequency-dependent tissue are refused, naming the material and the second-run
    /// alternative; one frequency evaluates the tissue there, and the note says it was the SAR frequency.</summary>
    [Fact]
    public void TwoSarFrequencies_WithADispersiveTissue_AreRefused()
    {
        var (path, cws) = SarSixFixtures.Workspace(_root);
        var doc = SarFixtures.Dipole();
        doc.Objects.Add(new C3dBox { Name = "arm", Material = "Muscle", Min = new(20 * SarSixFixtures.Mm, -10 * SarSixFixtures.Mm, -10 * SarSixFixtures.Mm),
                                     Size = new(20 * SarSixFixtures.Mm, 20 * SarSixFixtures.Mm, 20 * SarSixFixtures.Mm) });
        C3dPersistence.SaveToFile(path, doc);

        var two = C3dProblemAssembly.Assemble(SarSixFixtures.Setup(new CemSar { FrequenciesGHz = [0.9, 1.0] }), doc, path, cws, new C3dElaborator());
        Assert.False(two.Ok);
        Assert.Contains("'Muscle'", two.Refusal);
        Assert.Contains("frequency-dependent", two.Refusal);
        Assert.Contains("second setup", two.Refusal);

        var one = C3dProblemAssembly.Assemble(SarSixFixtures.Setup(new CemSar { FrequenciesGHz = [0.9] }), doc, path, cws, new C3dElaborator());
        Assert.True(one.Ok, one.Refusal);
        Assert.Contains(one.Notes, n => n.Contains("900 MHz (the SAR frequency)", StringComparison.Ordinal));
        Assert.Equal(1050, one.Problem!.SolidDensities["arm"]);
    }

    /// <summary>R-sar6-2 — the raw SAR dump and B1's H dump cover the tissue's bounding box widened to the grid lines outside it
    /// and one cell more on every side; each tissue is written with its density and nothing else is; the cell-constant
    /// material is on.</summary>
    [Fact]
    public void TheDumps_AreCroppedToTheTissuePlusOneCell_AndTissueCarriesItsDensity()
    {
        var (path, cws) = SarSixFixtures.Workspace(_root);
        var doc = SarSixFixtures.DipoleAndHead();
        C3dPersistence.SaveToFile(path, doc);
        var setup = SarSixFixtures.Setup(new CemSar { FrequenciesGHz = [1.0], B1 = true });
        var g = C3dProblemAssembly.Assemble(setup, doc, path, cws, new C3dElaborator());
        Assert.True(g.Ok, g.Refusal);
        var (plan, refusal) = Em3dSar.Resolve(SarSettings.Resolve(setup.Sar!), g.Problem!, g.Phantoms);
        Assert.True(plan is not null, refusal);
        Assert.Equal(["head/skin", "head/headbone", "head/brain"], plan.Region);
        Assert.Equal(["head/brain"], plan.B1Region);                    // the phantom's innermost layer

        var gs = CemOpenEms.ResolveGrid(setup.OpenEms, path);
        var grid = FdtdGrid.Build(g.Problem!, gs, long.MaxValue);
        var run = CemOpenEms.ResolveRun(setup.OpenEms, setup.RequestsSar) with { Sar = plan };
        Assert.True(run.CellConstantMaterial);
        var low = CsxcadWriter.Write(g.Problem!, grid, gs, run);
        Assert.True(low.Ok, low.Refusal);
        var xml = XDocument.Parse(low.Model!);
        Assert.Equal("1", xml.Descendants("FDTD").Single().Attribute("CellConstantMaterial")?.Value);

        // the box: the last line at or below the tissue's low side, one more cell out; the first at or above its high side, one more
        static (double Lo, double Hi) Expected(IReadOnlyList<double> lines, double lo, double hi)
        {
            int a = lines.Count(l => l <= lo) - 1, b = lines.Count - lines.Count(l => l >= hi);
            return (lines[Math.Max(0, a - 1)], lines[Math.Min(lines.Count - 1, b + 1)]);
        }
        // the tissue's own bounds, from its solids (the head's skin holds the other two)
        var b = Em3dProblem.Bounds(g.Problem!.Solids.Single(s => s.Name == "head/skin").Primitive);
        var x = Expected(grid.X.Lines, b.X0, b.X1);
        var y = Expected(grid.Y.Lines, b.Y0, b.Y1);
        var z = Expected(grid.Z.Lines, b.Z0, b.Z1);
        foreach (var (name, type, file) in new[] { (Em3dSar.RawDump(0), "29", "1"), (Em3dSar.HDump(0), "11", "0") })
        {
            var box = xml.Descendants("DumpBox").Single(d => d.Attribute("Name")!.Value == name);
            Assert.Equal((type, file, "2"), (box.Attribute("DumpType")!.Value, box.Attribute("FileType")!.Value, box.Attribute("DumpMode")!.Value));
            Assert.Equal(CsxcadWriter.OverSampling.ToString(CultureInfo.InvariantCulture), box.Attribute("OverSampling")?.Value);
            Assert.Equal(1e9, SarFixtures.D(box.Element("FD_Samples")!.Value));
            var p1 = box.Descendants("P1").Single();
            var p2 = box.Descendants("P2").Single();
            Assert.Equal(x, (SarFixtures.D(p1.Attribute("X")!.Value), SarFixtures.D(p2.Attribute("X")!.Value)));
            Assert.Equal(y, (SarFixtures.D(p1.Attribute("Y")!.Value), SarFixtures.D(p2.Attribute("Y")!.Value)));
            Assert.Equal(z, (SarFixtures.D(p1.Attribute("Z")!.Value), SarFixtures.D(p2.Attribute("Z")!.Value)));
        }
        double Density(string name) => SarFixtures.D(xml.Descendants().Single(e => e.Name == "Material" && e.Attribute("Name")!.Value == name)
                                                         .Element("Property")!.Attribute("Density")!.Value);
        Assert.Equal([1100.0, 2000.0, 1040.0], new[] { "head/skin", "head/headbone", "head/brain" }.Select(Density));

        // a lossy tissue with no density inside the region is refused, naming it
        var noRho = g.Problem! with { SolidDensities = new Dictionary<string, double> { ["head/skin"] = 1100, ["head/headbone"] = 2000 } };
        var (none, why) = Em3dSar.Resolve(SarSettings.Resolve(setup.Sar!), noRho, g.Phantoms);
        Assert.Null(none);
        Assert.Contains("'brain'", why);
        Assert.Contains("density", why);
    }
}

public sealed class B1ConventionTests
{
    /// <summary>R-sar6-4 — H = H₀(x̂ − jŷ) rotates from +x toward +y: all of it is B1+ for B0 along +z, |B1+| = μ0·H₀ = |B|, and
    /// none is B1−; along −z the same field is all B1−. The cyclic permutation for B0 along +x holds the same rule.</summary>
    [Fact]
    public void ACircularlyPolarisedH_IsAllB1Plus_AlongPlusZ_AndAllB1Minus_AlongMinusZ()
    {
        const double h0 = 2.5;
        var (p, m) = SarB1.FromH(h0, -Complex.ImaginaryOne * h0, 0, b0Axis: 2, b0Sign: +1);
        double b = SarB1.Mu0 * h0;
        Assert.Equal(b, p.Magnitude, 15);
        Assert.Equal(0, m.Magnitude, 15);

        var (p2, m2) = SarB1.FromH(h0, -Complex.ImaginaryOne * h0, 0, b0Axis: 2, b0Sign: -1);
        Assert.Equal(0, p2.Magnitude, 15);
        Assert.Equal(b, m2.Magnitude, 15);

        // the opposite rotation is all B1− along +z
        var (p3, m3) = SarB1.FromH(h0, Complex.ImaginaryOne * h0, 0, 2, +1);
        Assert.Equal(0, p3.Magnitude, 15);
        Assert.Equal(b, m3.Magnitude, 15);

        // B0 along +x: (y, z) take (x, y)'s place
        var (px, mx) = SarB1.FromH(0, h0, -Complex.ImaginaryOne * h0, 0, +1);
        Assert.Equal(b, px.Magnitude, 15);
        Assert.Equal(0, mx.Magnitude, 15);
    }
}

public sealed class SarResultsWiringTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar6-wiring-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Ref(string file) => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "openems-ref", "Dipole_SAR", file);

    /// <summary>
    /// SAR-1's dipole raw dump, laid where a run of this setup leaves it, through OpenEmsSarResults with no openEMS process: the
    /// 10 g peak, absorbed power and mass are SAR-5's (sar_calc's on the same dump), the accepted power formed from the port's
    /// own probes is the tutorial's (to 1e-4), so psSAR10g per watt is its 2.435 W/kg, and the .npy's sar group
    /// carries it. Then `render --field` draws the plot, and its maximum-intensity projection.
    /// </summary>
    [SarReferenceFact]
    public void TheDipoleDump_ThroughTheResultPath_GivesSar5sNumbers_PerWattAccepted_AndRenders()
    {
        var summary = JsonDocument.Parse(File.ReadAllText(Ref("summary.json"))).RootElement;
        var sar10 = summary.GetProperty("sar_10g");

        var (c3d, cws) = SarSixFixtures.Workspace(_root);
        var doc = SarSixFixtures.DipoleAndHead();
        var setup = SarSixFixtures.Setup(new CemSar { FrequenciesGHz = [1.0], MassesGrams = [10] });
        doc.Setups = [EmSetupPersistence.ToEmbedded(setup)];
        doc.FieldPlots =
        [
            new C3dFieldPlot
            {
                Name = "SAR10g", Setup = "S", Quantity = "SAR10g", On = C3dFieldPlotOn.ClipPlane, Axis = C3dAxis.Z, Offset = 0,
                Solution = new C3dFieldSolution { GHz = 1, Port = 1 },
            },
        ];
        C3dPersistence.SaveToFile(c3d, doc);
        var g = C3dProblemAssembly.Assemble(setup, doc, c3d, cws, new C3dElaborator());
        Assert.True(g.Ok, g.Refusal);
        var (plan, refusal) = Em3dSar.Resolve(SarSettings.Resolve(setup.Sar!), g.Problem!, g.Phantoms);
        Assert.True(plan is not null, refusal);

        // the run directory as a run of this setup leaves it: the raw dump, the port's probes, the document it solved
        var runSetup = C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, c3d);
        string runDir = Em3dRunService.RunDirectory(Path.Combine(Path.GetDirectoryName(cws)!, "results"), runSetup, Em3dSolverId.OpenEms);
        string p1 = Path.Combine(runDir, OpenEmsRun.PortDirectory(1));
        Directory.CreateDirectory(p1);
        File.Copy(Ref("SAR_raw_dump29.h5"), Path.Combine(p1, Em3dSar.RawDumpFile(0)));
        File.Copy(Ref("port_ut_1"), Path.Combine(p1, CsxcadWriter.VoltageProbe(1)));
        File.Copy(Ref("port_it_1"), Path.Combine(p1, CsxcadWriter.CurrentProbe(1)));
        C3dRunInputs.Take(doc, c3d, C3dElaborator.ElaborateOnce(doc, c3d, null).FilesRead).KeepIn(runDir);

        var u = FdtdPortTransform.Dft(OpenEmsRun.ReadProbe(Path.Combine(p1, CsxcadWriter.VoltageProbe(1)), out _)!, [1e9])[0];
        var i = FdtdPortTransform.Dft(OpenEmsRun.ReadProbe(Path.Combine(p1, CsxcadWriter.CurrentProbe(1)), out _)!, [1e9])[0];
        var outcome = OpenEmsSarResults.Process(plan, g.Problem!, runDir, [new SarPortDrive(1, 1e9, u, i)]);
        Assert.True(outcome.Error is null, outcome.Error);
        foreach (string n in outcome.Notes) output.WriteLine(n);
        var r = Assert.Single(outcome.Records);

        double Rel(double a, double b) => Math.Abs(a - b) / Math.Abs(b);
        var peak = r.Peaks.Single(p => p.Quantity == "SAR10g");
        Assert.True(Rel(peak.Value, sar10.GetProperty("maxSAR").GetDouble()) < 1e-6, $"peak {peak.Value}");
        Assert.Equal([14, 44, 45], peak.Cell);
        Assert.True(Rel(r.AbsorbedPowerW, sar10.GetProperty("power").GetDouble()) < 1e-6, $"P_abs {r.AbsorbedPowerW}");
        Assert.True(Rel(r.TissueMassKg, sar10.GetProperty("mass").GetDouble()) < 1e-6, $"mass {r.TissueMassKg}");
        Assert.Equal("head/skin", peak.Object);                           // the skin nearest the dipole
        double pacc = summary.GetProperty("accepted_power_w").GetDouble();
        output.WriteLine($"P_acc {r.AcceptedPowerW:G6} W against the tutorial's {pacc:G6} W; psSAR10g {r.PeakPerWatt("SAR10g"):G6} W/kg per W");
        // the same probes, one DFT convention: measured equal to six figures (sar.md §8.4)
        Assert.True(Rel(r.AcceptedPowerW, pacc) < 1e-4, $"P_acc {r.AcceptedPowerW} against {pacc}");
        Assert.True(Rel(r.PeakPerWatt("SAR10g")!.Value, summary.GetProperty("psSAR10g_w_per_kg_per_w").GetDouble()) < 1e-4);
        Assert.True(Rel(r.WholeSarPerWatt!.Value, summary.GetProperty("whole_phantom_sar_w_per_kg_per_w").GetDouble()) < 1e-4);

        // the .npy's sar group: the scalars over the SAR frequency and the driven port
        var data = new DataSet();
        OpenEmsSarResults.AddTo(data, outcome.Records, plan.Settings);
        var cubes = data.CubesIn(OpenEmsSarResults.Group);
        Assert.Equal(r.PeakPerWatt("SAR10g")!.Value, cubes["psSAR10g"].RealValues[0]);
        Assert.Equal(r.AcceptedPowerW, cubes[OpenEmsSarResults.AcceptedCube].RealValues[0]);
        Assert.Equal(r.WholeSarPerWatt!.Value, cubes[OpenEmsSarResults.WholeSarCube].RealValues[0]);
        Assert.False(cubes.ContainsKey(OpenEmsSarResults.RadiatedCube));       // no radiation pattern was asked for
        // the volumes beside the dump, float32, referenced from the record
        Assert.Equal(["SAR", "SAR10g"], r.Volumes);
        var (vol, shape) = SarVolumeFiles.ReadFloat32(Path.Combine(p1, SarVolumeFiles.VolumeFile(0, "SAR10g")));
        Assert.Equal([83, 90, 91], shape);
        Assert.Equal((float)peak.Value, vol[(14 * 90 + 44) * 91 + 45]);

        // render --field: the plot drawn headlessly, then as its projection along z
        string png = Path.Combine(_root, "sar.png"), mip = Path.Combine(_root, "mip.png");
        var (exit, stdout, stderr) = Cli("render", c3d, "-o", png, "--field", "SAR10g", "--json");
        Assert.True(exit == 0, stderr + stdout);
        Assert.True(new FileInfo(png).Length > 0);
        var field = JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetProperty("render").GetProperty("em3d").GetProperty("field");
        Assert.Equal("SAR10g", field.GetProperty("quantity").GetString());
        Assert.True(field.GetProperty("triangles").GetInt32() > 0);
        (exit, stdout, stderr) = Cli("render", c3d, "-o", mip, "--field", "SAR10g", "--mip", "z");
        Assert.True(exit == 0, stderr + stdout);
        Assert.True(new FileInfo(mip).Length > 0);
        (exit, stdout, _) = Cli("render", c3d, "--list-fields");
        Assert.Equal(0, exit);
        Assert.Contains("data: ready", stdout);
    }

    private static (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        string cliDir = typeof(SarResultsWiringTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = PalaceBackendTests.RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
