// ================================================================
//  Em3dConnectorExampleTests.cs — the shipped 3D Connector example (brief-em3d-70 §5): a coaxial connector launching
//  onto a microstrip at a board edge, built from a Subtract that keeps its Tool, a Unite with a STEP part, and a fillet.
//
//  Brief 30's discipline: `expected-numbers.json` beside the README is the ONE source of every number the README and
//  the user pages print; each value's text must appear verbatim in each page that quotes it, and gate 6 reproduces the
//  shipped setups' values from live runs. What needs the geometry kernel is a [KernelFact]; gate 6 needs Palace and
//  openEMS too and is Category=Benchmark.
//
//  Flange and Launch are DRAWN, by Em3dConnectorAuthoring (R-em3d70-1d), not written by hand.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Engine.Em3d;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class Em3dConnectorExampleTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-em3d70-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── 1. check ────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>check</c> on the workspace, as a process: no error, and every warning is one the README lists — the
    /// fidelity rows, which never change the exit code.</summary>
    [KernelFact]
    public void Gate1_CheckIsClean()
    {
        var (errors, warnings) = Check(Root());
        Assert.Empty(errors);
        string readme = File.ReadAllText(Path.Combine(Root(), "README.md"));
        foreach (string w in warnings) output.WriteLine(w);
        // The two fidelity rows the README quotes: the fillet under one cell, and the bore staircased.
        string[] listed = ["will not represent the 100 µm fillet on 'pin'", "staircases the 670 µm radius of 'housing' (bore:side)"];
        Assert.All(warnings, w => Assert.Contains(listed, l => w.Contains(l, StringComparison.Ordinal)));
        Assert.All(listed, l => Assert.Contains(l, readme));
    }

    // ── 2. elaboration ──────────────────────────────────────────────────────────────────────────

    /// <summary>The housing is ONE kernel solid (the box less its bore, united with the STEP flange) under the Blank's name
    /// and material; the bore is kept as its own PTFE dielectric; the pin is a kernel solid with its fillet's face; the
    /// board comes in through its layout. The names the faces carry — a Tool's <c>bore:side</c>, a STEP part's
    /// <c>flange:face&lt;n&gt;</c>, the fillet's <c>fillet(side|top)</c> — all resolve, and the ports find pin and housing,
    /// line and ground.</summary>
    [KernelFact]
    public void Gate2_TheLaunchElaborates_AsTheReadmeSays()
    {
        var (doc, path, cws) = Launch();
        var e = new C3dElaborator().Elaborate(doc, path, cws);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var housing = e.Solids.Single(s => s.Name == "housing");
        Assert.Equal(("Connector alloy", Em3dRole.Conductor), (housing.Material, housing.Role));
        Assert.IsType<Em3dShapeSolid>(housing.Primitive);
        var bore = e.Solids.Single(s => s.Name == "bore");
        Assert.Equal(("PTFE", Em3dRole.Dielectric), (bore.Material, bore.Role));
        Assert.Equal(housing.Order + 1, bore.Order);
        Assert.IsType<Em3dShapeSolid>(e.Solids.Single(s => s.Name == "pin").Primitive);
        Assert.Contains(e.Solids, s => s.Name == "B1/Laminate" && s.Material == "PTFE-glass laminate");
        Assert.DoesNotContain(e.Solids, s => s.Name == "flange");      // a Tool of the Unite, not an object of its own

        var faces = e.Provenance["housing"].FaceNames;
        Assert.Contains(faces, f => f.StartsWith("bore:side", StringComparison.Ordinal));
        Assert.Contains(faces, f => Regex.IsMatch(f, @"^flange:face\d+"));
        Assert.Contains("fillet(side|top)", e.Provenance["pin"].FaceNames);

        foreach (string name in new[] { "Palace", "openEMS" })
        {
            var (_, g) = Assemble(doc, path, cws, name);
            Assert.True(g.Ok, $"{name}: {g.Refusal}");
            var ports = g.Problem!.Ports.OrderBy(p => p.Number).ToList();
            Assert.Equal(("pin", "housing"), (ports[0].PositiveObject, ports[0].NegativeObject));
            Assert.Equal(("B1/Top Copper/1", "B1/Bottom Copper"), (ports[1].PositiveObject, ports[1].NegativeObject));
        }
    }

    /// <summary>
    /// Designer feedback round 11 — the estimate made BEFORE meshing tracks the mesh Gmsh then makes. Gmsh 4.15.2 made
    /// 93,371 tetrahedra from this setup's script here and 93,086 on a Windows run; the volumes alone said 2,037, because
    /// on a connector the refinement round the metal IS the mesh. Pinned to that count, so no mesher runs here.
    /// </summary>
    [KernelFact]
    public void ThePalaceEstimate_BeforeMeshing_TracksGmshsCount()
    {
        var (doc, path, cws) = Launch();
        var (setup, g) = Assemble(doc, path, cws, "Palace");
        Assert.True(g.Ok, g.Refusal);
        var estimate = Em3dRunService.EstimatePalace(g.Problem!, PalaceSettings.Resolve(setup.Palace))!;
        output.WriteLine($"estimate {estimate.Tetrahedra:N0} tetrahedra against Gmsh's 93,371");
        Assert.InRange(estimate.Tetrahedra / 93_371.0, 0.8, 1.5);
    }

    // ── 3. the STEP file is circuitRF's own ─────────────────────────────────────────────────────

    /// <summary>R-em3d70-1c — Flange re-exported matches the shipped file byte for byte but for FILE_NAME's time stamp,
    /// and the Step object's Hash is the shipped file's.</summary>
    [KernelFact]
    public void Gate3_TheShippedStepFile_IsFlangeExported()
    {
        using var kernel = KernelForTests.New();
        string shipped = Path.Combine(Root(), "Launch", "3d", "flange.step");
        Directory.CreateDirectory(_tmp);
        string again = Path.Combine(_tmp, "flange.step");
        StepExport.Export(Path.Combine(Root(), "Flange", "3d", "Flange.c3d"), again,
                          new StepExportOptions { WorkspaceCws = Path.Combine(Root(), ".cws") }, kernel);
        Assert.Equal(WithoutStamp(File.ReadAllText(shipped)), WithoutStamp(File.ReadAllText(again)));

        var (doc, _, _) = Launch();
        var step = C3dOperands.SelfAndDescendants(doc.Objects.Single(o => o.Name == "housing")).OfType<C3dStep>().Single();
        Assert.Equal(("flange.step", "flange.step"), (step.File, step.SourcePath));
        Assert.Equal(C3dValidation.StepHash(shipped), step.Hash);
    }

    /// <summary>FILE_NAME's time stamp — the field brief 69's gate 1 allows to differ — and the circuitRF version its
    /// originating system names, which the VERSION file changes at every release. The OCCT processor's own version is kept:
    /// a kernel that writes different bytes is the format change this gate exists to catch.</summary>
    private static string WithoutStamp(string step)
        => Regex.Replace(Regex.Replace(step, @"FILE_NAME\('([^']*)','[^']*'", "FILE_NAME('$1',''"), @"'circuitRF [^']*'", "'circuitRF'");

    // ── 4. one source ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("examples/3D Connector/README.md")]
    [InlineData("docs/user/src/reference/em-solvers.md")]
    public void Gate4_TheReadmeAndThePageCiteEveryNumberInTheFile(string page)
    {
        var numbers = Numbers();
        string text = File.ReadAllText(Path.Combine(RepoRoot(), page));
        foreach (string s in numbers.References.SelectMany(r => r.Readme)) Assert.Contains(s, text);
        foreach (var run in numbers.Runs.Concat(numbers.Alternatives))
        {
            foreach (string s in run.Readme) Assert.Contains(s, text);
            foreach (var v in run.Values) Assert.Contains(v.Readme, text);
        }
    }

    /// <summary>The references the file states are the closed forms of the drawn dimensions: the coax's
    /// Z₀ = (60/√εr)·ln(D/d), and Hammerstad–Jensen's microstrip Z₀ for the board's line.</summary>
    [KernelFact]
    public void TheClosedFormsInTheFile_AreTheDrawnGeometrys()
    {
        var refs = Numbers().References;
        var coax = refs.Single(r => r.Name == "coax");
        var line = refs.Single(r => r.Name == "microstrip");
        var (doc, path, cws) = Launch();
        var e = new C3dElaborator().Elaborate(doc, path, cws);
        double d = 2 * Assert.IsType<Em3dCylinder>(e.Solids.Single(s => s.Name == "bore").Primitive).Radius;
        var strip = Em3dProblem.Bounds(e.Solids.Single(s => s.Name == "B1/Top Copper/1").Primitive);
        var slab = Em3dProblem.Bounds(e.Solids.Single(s => s.Name == "B1/Laminate").Primitive);
        Assert.Equal(coax.OuterMm, d * 1e3, 9);
        Assert.Equal(line.WidthMm, (strip.Y1 - strip.Y0) * 1e3, 6);
        Assert.Equal(line.HeightMm, (slab.Z1 - slab.Z0) * 1e3, 6);

        double zCoax = 60 / Math.Sqrt(coax.Epsr) * Math.Log(coax.OuterMm / coax.InnerMm);
        double zLine = Hammerstad(line.WidthMm / line.HeightMm, line.Epsr);
        output.WriteLine($"coax {zCoax:F3} Ω, line {zLine:F3} Ω");
        Assert.Equal(coax.Expected, zCoax, 2);
        Assert.Equal(line.Expected, zLine, 2);
    }

    /// <summary>Hammerstad and Jensen's zero-thickness microstrip impedance, width/height u.</summary>
    internal static double Hammerstad(double u, double er)
    {
        double a = 1 + Math.Log((Math.Pow(u, 4) + Math.Pow(u / 52, 2)) / (Math.Pow(u, 4) + 0.432)) / 49 + Math.Log(1 + Math.Pow(u / 18.1, 3)) / 18.7;
        double b = 0.564 * Math.Pow((er - 0.9) / (er + 3), 0.053);
        double eeff = (er + 1) / 2 + (er - 1) / 2 * Math.Pow(1 + 10 / u, -a * b);
        double f = 6 + (2 * Math.PI - 6) * Math.Exp(-Math.Pow(30.666 / u, 0.7528));
        return 60 * Math.Log(f / u + Math.Sqrt(1 + 4 / (u * u))) / Math.Sqrt(eeff);
    }

    // ── 5. disabled means absent ────────────────────────────────────────────────────────────────

    /// <summary>With the pin's Fillet disabled the pin is the plain cylinder, and what Palace and openEMS are handed is
    /// exactly what the same document with the Fillet removed hands them.</summary>
    [KernelFact]
    public void Gate5_ADisabledFillet_LowersExactlyAsNoFilletAtAll()
    {
        var (off, path, cws) = Launch();
        ((C3dFillet)off.Objects.Single(o => o.Name == "pin")).Enabled = false;
        var (gone, _, _) = Launch();
        int k = gone.Objects.FindIndex(o => o.Name == "pin");
        var target = C3dBooleans.Copy(((C3dFillet)gone.Objects[k]).Target!);
        target.Name = "pin";
        gone.Objects[k] = target;

        var e = new C3dElaborator().Elaborate(off, path, cws);
        Assert.IsType<Em3dCylinder>(e.Solids.Single(s => s.Name == "pin").Primitive);

        foreach (string name in new[] { "Palace", "openEMS" })
        {
            var (setup, a) = Assemble(off, path, cws, name);
            var (_, b) = Assemble(gone, path, cws, name);
            Assert.True(a.Ok && b.Ok, a.Refusal ?? b.Refusal);
            if (setup.Solver3D == Em3dSolver.Palace)
            {
                var settings = PalaceSettings.Resolve(setup.Palace);
                var la = GmshGeoWriter.Write(a.Problem!, settings);
                var lb = GmshGeoWriter.Write(b.Problem!, settings);
                Assert.Equal((la.Geo, la.GroupsJson), (lb.Geo, lb.GroupsJson));
                Assert.Equal(la.KernelFiles?.Select(f => (f.FileName, f.Sha256)), lb.KernelFiles?.Select(f => (f.FileName, f.Sha256)));
                Assert.Equal(PalaceConfigWriter.Write(a.Problem!, la.Groups, settings).Json, PalaceConfigWriter.Write(b.Problem!, lb.Groups, settings).Json);
            }
            else
            {
                var gs = CemOpenEms.ResolveGrid(setup.OpenEms);
                string? Model(Em3dProblem p) => CsxcadWriter.Write(p, FdtdGrid.Build(p, gs, long.MaxValue), gs, CemOpenEms.ResolveRun(setup.OpenEms)).Model;
                Assert.Equal(Model(a.Problem!), Model(b.Problem!));
            }
        }
    }

    // ── 6. the solves: a regression test of 3D EM results ───────────────────────────────────────

    /// <summary>
    /// The four shipped runs — Palace and openEMS, the pin's fillet enabled and disabled — each reproducing its RECORDED
    /// |S11|, |S21| and |S22| at every frequency of the sweep, within the file's regression tolerances. This is the
    /// regression net for 3D EM: a change anywhere between the .c3d and the Touchstone (elaboration, the kernel, the
    /// lowering, the mesher, the solvers' inputs) that moves an answer shows here, by run, quantity and frequency. The
    /// tolerances are tight on purpose — a failure is a question to answer, and a deliberate change re-records the
    /// table (README ▸ the regression test). The openEMS runs also carry the fidelity warning naming the pin.
    /// </summary>
    [CircuitRF.Ui.Tests.Em3d.PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate6_EveryRun_ReproducesItsRecordedSParameters()
    {
        var numbers = Numbers();
        var tol = numbers.RegressionTolerance;
        var moved = new List<string>();
        foreach (var run in numbers.Runs)
        {
            var (doc, path, cws) = Launch();
            if (run.Variant == "no fillet") ((C3dFillet)doc.Objects.Single(o => o.Name == "pin")).Enabled = false;
            var (embedded, why) = C3dSetups.Select(doc, run.Setup);
            Assert.True(embedded is not null, why);
            var wall = Stopwatch.StartNew();
            var result = EmRunService.RunThreeDView(C3dSetups.ForRun(embedded!, path), doc, path, cws, Path.Combine(_tmp, "results"),
                                                    confirmMemory: _ => true);
            output.WriteLine($"{run.Setup}, {run.Variant}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s (recorded {run.Readme[0]})");
            Assert.True(result.Status == EmRunStatus.Ok, $"{run.Setup} {run.Variant}: {result.Error}");
            if (run.Setup == "openEMS" && run.Variant == "fillet")
                Assert.Contains(result.Warnings, w => w.Contains("will not represent the 100 µm fillet on 'pin'", StringComparison.Ordinal));
            var snp = TouchstoneIO.ReadFile(result.SnpPath!);
            Assert.Equal(run.Recorded!.Count, snp.Frequencies.Length);
            foreach (var r in run.Recorded)
            {
                int k = Array.FindIndex(snp.Frequencies, f => Math.Abs(f - r.GHz * 1e9) < 1);
                Assert.True(k >= 0, $"{run.Setup} {run.Variant}: no point at {r.GHz} GHz");
                foreach (var (q, want, t, s) in new[] { ("S11", r.S11_dB, tol.S11_dB, snp.Matrices[k][0, 0]),
                                                         ("S21", r.S21_dB, tol.S21_dB, snp.Matrices[k][1, 0]),
                                                         ("S22", r.S22_dB, tol.S22_dB, snp.Matrices[k][1, 1]) })
                {
                    double got = 20 * Math.Log10(s.Magnitude);
                    output.WriteLine($"  {r.GHz,4} GHz |{q}| {got,9:F4} dB  recorded {want,9:F4}  Δ {got - want,8:F4}");
                    if (Math.Abs(got - want) > t)
                        moved.Add($"{run.Setup}, {run.Variant}: |{q}| at {r.GHz} GHz is {got:F4} dB, recorded {want:F4} dB (± {t})");
                }
            }
        }
        Assert.True(moved.Count == 0, "3D EM results moved from the recorded ones:\n" + string.Join("\n", moved));
    }

    /// <summary>What the pages quote is what was recorded: every quoted value is its run's recorded one, rounded.</summary>
    [Fact]
    public void TheQuotedValues_AreTheRecordedOnes()
    {
        foreach (var run in Numbers().Runs)
            foreach (var v in run.Values)
            {
                var r = run.Recorded!.Single(x => x.GHz == v.AtGHz);
                Assert.Equal(v.Quantity == "S11_dB" ? r.S11_dB : r.S21_dB, v.Expected);
            }
    }

    // ── 7. no vendor names ──────────────────────────────────────────────────────────────────────

    /// <summary>The repository's vendor-name gate (WsProbeDocsTests' digests, which name nothing) over every text file of
    /// the example, the STEP file's product names included.</summary>
    [Fact]
    public void Gate7_NoVendorNameIsInTheExample()
    {
        foreach (string file in Directory.EnumerateFiles(Root(), "*", SearchOption.AllDirectories))
        {
            var words = Regex.Matches(File.ReadAllText(file).ToLowerInvariant(), @"[a-z][a-z0-9]*").Select(w => w.Value).ToList();
            for (int i = 0; i < words.Count; i++)
            {
                Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i]), Docs.WsProbeDocsTests.BannedDigests);
                if (i + 1 < words.Count)
                    Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i] + " " + words[i + 1]), Docs.WsProbeDocsTests.BannedDigests);
            }
        }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static string Root()
        => Path.Combine(ExampleWorkspaces.ResolveRoot(RepoRoot()) ?? throw new InvalidOperationException("no examples/"), "3D Connector");

    private static (C3dDocument Doc, string Path, string Cws) Launch()
    {
        string path = Path.Combine(Root(), "Launch", "3d", "Launch.c3d");
        return (C3dPersistence.LoadFromFile(path), path, Path.Combine(Root(), ".cws"));
    }

    private static (EmSetup Setup, CircuitRF.Design.Layout.Em3d.Em3dGenerationResult Generated) Assemble(C3dDocument doc, string path,
                                                                                                        string cws, string name)
    {
        var (embedded, why) = C3dSetups.Select(doc, name);
        Assert.True(embedded is not null, why);
        var setup = C3dSetups.ForRun(embedded!, path);
        return (setup, C3dProblemAssembly.Assemble(setup, doc, path, cws));
    }

    /// <summary><c>check --json</c> as a process: the verb a user runs.</summary>
    private static (List<string> Errors, List<string> Warnings) Check(string path)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in new[] { "check", path, "--json" }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        using var doc = JsonDocument.Parse(outTask.GetAwaiter().GetResult());
        var diags = doc.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        List<string> Of(string severity) => [.. diags.Where(d => d.GetProperty("severity").GetString() == severity)
                                                     .Select(d => d.GetProperty("message").GetString()!)];
        return (Of("error"), Of("warning"));
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Em3dConnectorExampleTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }

    // ── expected-numbers.json ───────────────────────────────────────────────────────────────────

    private sealed record ExpectedValue(string Quantity, double AtGHz, double Expected, double Tolerance, string Readme);

    private sealed record RecordedPoint(double GHz, double S11_dB, double S21_dB, double S22_dB);

    /// <summary>A run: the setup, the fillet's variant, what it cost (Readme), the values the pages quote, and — for a shipped
    /// run — the recorded |S| at every frequency that gate 6 holds it to.</summary>
    private sealed record ExpectedRun(string Setup, string Variant, string Preset, List<string> Readme, List<ExpectedValue> Values,
                                      List<RecordedPoint>? Recorded = null);

    private sealed record Tolerances(double S11_dB, double S21_dB, double S22_dB);

    private sealed record Reference(string Name, double Expected, List<string> Readme, double Epsr,
                                    double InnerMm = 0, double OuterMm = 0, double WidthMm = 0, double HeightMm = 0);

    private sealed record ExpectedNumbers(List<Reference> References, List<ExpectedRun> Runs, List<ExpectedRun> Alternatives,
                                          Tolerances RegressionTolerance);

    private static ExpectedNumbers Numbers()
        => JsonSerializer.Deserialize<ExpectedNumbers>(File.ReadAllText(Path.Combine(Root(), "expected-numbers.json")))!;
}
