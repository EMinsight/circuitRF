// ================================================================
//  Em3dPackageExampleTests.cs — the shipped 3D Package example (brief-em3d-52 §4): an MMIC's layout placed
//  in a package drawn in the 3D editor.
//
//  Brief 30's discipline: `expected-numbers.json` beside the README is the ONE source of every number the
//  README and the user page (drawing-in-3d.md) print; each value's text must appear verbatim in both, and
//  gate 4 reproduces each value from a live Palace run. Gates 1-3, 5 and the lowering need no solver; gate 4
//  runs Palace three times (the driven sweep, the lid modes, and the lid modes of the package without its
//  die) and skips without it, so it is Category=Benchmark.
//
//  The package's .c3d is DRAWN, by Em3dPackageAuthoring (R-em3d52-1b), not written by hand.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class Em3dPackageExampleTests(ITestOutputHelper output) : IDisposable
{
    private const double Mil = 25.4e-6;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-em3d52-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── 1. check ────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>check</c> on the workspace, as a process: no error, and its one warning is DRC's note that the die's
    /// port labels carry no area, which the README explains.</summary>
    [Fact]
    public void Gate1_CheckIsClean()
    {
        var (errors, warnings) = Check(Root());
        Assert.Empty(errors);
        Assert.All(warnings, w => Assert.Contains("carry no manufacturable area", w));
    }

    // ── 2. elaboration ──────────────────────────────────────────────────────────────────────────

    /// <summary>The package elaborates: the die comes in through ITS OWN technology (GaAs is the MMIC's, alumina the
    /// package's), under <c>U1/</c>; and each port runs from the floor (−) up to its lead (+), as the README says.</summary>
    [Fact]
    public void Gate2_ElaboratesWithBothTechnologies_AndThePortsPointUpToTheLeads()
    {
        var (doc, path, cws) = Package();
        var e = new C3dElaborator().Elaborate(doc, path, cws);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Contains(e.Solids, s => s.Name == "U1/GaAs" && s.Material == "GaAs");
        Assert.Contains(e.Solids, s => s.Name == "U1/Backside Metal");
        Assert.Contains(e.Solids, s => s.Name.StartsWith("U1/Metal1", StringComparison.Ordinal));
        Assert.Contains(e.Solids, s => s.Name == "base" && s.Material == "Alumina");
        Assert.Contains(e.Solids, s => s.Name == "lid" && s.Material == "Lid alloy");

        foreach (var name in new[] { "Driven", "Lid modes" })
        {
            var (_, g) = Assemble(doc, path, cws, name);
            Assert.True(g.Ok, $"{name}: {g.Refusal}");
            var ports = g.Problem!.Ports.OrderBy(p => p.Number).ToList();
            Assert.Equal(2, ports.Count);
            for (int k = 0; k < 2; k++)
            {
                Assert.Equal(("floor", $"lead{k + 1}"), (ports[k].NegativeObject, ports[k].PositiveObject));
                Assert.Equal(new Point3(0, 0, 1), ports[k].Direction);
            }
        }
    }

    /// <summary>brief-em3d-52 — a drawn package's sides are its floor's edge, a wall and the lid's edge: no one box
    /// covers an air-box face, three do together. Each face expects no surface of its own, so the closed package
    /// lowers with the box flush against its metal — which was refused before.</summary>
    [Fact]
    public void TheAirBoxFacesFlushAgainstTheDrawnMetal_AreCoveredAndExpectNothing()
    {
        var (doc, path, cws) = Package();
        foreach (var name in new[] { "Driven", "Lid modes" })
        {
            var (setup, g) = Assemble(doc, path, cws, name);
            foreach (string face in new[] { "xmin", "xmax", "ymin", "ymax", "zmin", "zmax" })
                Assert.True(GmshGeoWriter.Covered(g.Problem!, face), $"{name}: {face}");
            var low = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace));
            Assert.True(low.Ok, low.Refusal);
            Assert.All(low.Groups.Where(gr => gr.Kind == Em3dGroupKind.Face), gr => Assert.Equal(0, gr.Expected));
        }
    }

    /// <summary>brief-em3d-52 — both setups solve with SuperLU_DIST as the whole preconditioner (one multigrid level):
    /// the setting that brings each under five minutes.</summary>
    [Fact]
    public void BothSetupsAskForTheDirectSolver()
    {
        var (doc, path, cws) = Package();
        foreach (var (name, type, levels) in new[] { ("Lid modes", "SuperLU", (int?)1), ("Driven", "SuperLU", (int?)1) })
        {
            var (setup, g) = Assemble(doc, path, cws, name);
            var settings = PalaceSettings.Resolve(setup.Palace);
            var low = GmshGeoWriter.Write(g.Problem!, settings);
            var cfg = PalaceConfigWriter.Write(g.Problem!, low.Groups, settings);
            Assert.True(cfg.Ok, cfg.Refusal);
            using var json = JsonDocument.Parse(cfg.Json!);
            var linear = json.RootElement.GetProperty("Solver").GetProperty("Linear");
            Assert.Equal(type, linear.GetProperty("Type").GetString());
            Assert.Equal(levels, linear.TryGetProperty("MGMaxLevels", out var l) ? l.GetInt32() : null);
        }
    }

    // ── 3. one source ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("examples/3D Package/README.md")]
    [InlineData("docs/user/src/reference/drawing-in-3d.md")]
    public void Gate3_TheReadmeAndThePageCiteEveryNumberInTheFile(string page)
    {
        var numbers = Numbers();
        string text = File.ReadAllText(Path.Combine(RepoRoot(), page));
        Assert.Contains(numbers.ClosedForm.Readme, text);
        foreach (var run in numbers.Runs)
        {
            foreach (string s in run.Readme) Assert.Contains(s, text);
            foreach (var v in run.Values) Assert.Contains(v.Readme, text);
        }
    }

    /// <summary>The closed form the file states is the one computed here: the TM110 family of a rectangular cavity
    /// with the alumina slab on its floor, from the drawn dimensions — the transverse-resonance equation
    /// k₁·tan(k₁h₁)/εr = α·tanh(α·h₂), solved by bisection.</summary>
    [Fact]
    public void TheClosedFormInTheFileIsTheDrawnCavitys()
    {
        var cf = Numbers().ClosedForm;
        double got = LayeredCavityTm110(cf.WidthMil * Mil, cf.LengthMil * Mil, cf.SlabMil * Mil, cf.HeightMil * Mil, cf.SlabEpsr) / 1e9;
        output.WriteLine($"closed form {got:F4} GHz");
        Assert.Equal(cf.Expected, got, 2);

        // …and the drawn cavity is what the closed form was given: the parameters' defaults and the base's height.
        var (doc, path, cws) = Package();
        var e = new C3dElaborator().Elaborate(doc, path, cws);
        var b = Assert.IsType<Em3dBox>(e.Solids.Single(s => s.Name == "base").Primitive);
        Assert.Equal(cf.WidthMil * Mil, b.Max.X - b.Min.X, 9);
        Assert.Equal(cf.LengthMil * Mil, b.Max.Y - b.Min.Y, 9);
        Assert.Equal(cf.SlabMil * Mil, b.Max.Z - b.Min.Z, 9);
        var lid = Assert.IsType<Em3dBox>(e.Solids.Single(s => s.Name == "lid").Primitive);
        Assert.Equal(cf.HeightMil * Mil, lid.Min.Z, 9);
    }

    // ── 4. the solves ───────────────────────────────────────────────────────────────────────────

    [CircuitRF.Ui.Tests.Em3d.PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate4_EveryPalaceRun_ReproducesTheReadmesNumbers()
    {
        var numbers = Numbers();
        foreach (var run in numbers.Runs)
        {
            var (doc, path, cws) = Package();
            if (run.Variant == "without the die")
            {
                doc.Instances.Clear();
                doc.Objects.RemoveAll(o => o is C3dWire);
            }
            var (embedded, why) = C3dSetups.Select(doc, run.Setup);
            Assert.True(embedded is not null, why);
            var wall = Stopwatch.StartNew();
            var result = EmRunService.RunThreeDView(C3dSetups.ForRun(embedded!, path), doc, path, cws, Path.Combine(_tmp, "results"),
                                                    confirmMemory: _ => true);
            output.WriteLine($"{run.Setup} {run.Variant}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s — {result.Notes?.LastOrDefault()}");
            Assert.True(result.Status == EmRunStatus.Ok, $"{run.Setup}: {result.Error}");
            foreach (var v in run.Values)
            {
                double got = Measure(v, result);
                output.WriteLine($"  {v.Quantity} {v.Label}: {got:G6} (README {v.Expected} ± {v.Tolerance})");
                Assert.True(Math.Abs(got - v.Expected) <= v.Tolerance,
                            $"{run.Setup} {v.Quantity} {v.Label}: {got:G6}, README says {v.Expected} ± {v.Tolerance}");
            }
        }
    }

    // ── 5. no vendor names ──────────────────────────────────────────────────────────────────────

    /// <summary>The repository's vendor-name gate (WsProbeDocsTests' digests, which name nothing) over every text file
    /// of the example.</summary>
    [Fact]
    public void Gate5_NoVendorNameIsInTheExample()
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

    // ── measuring ───────────────────────────────────────────────────────────────────────────────

    private static double Measure(ExpectedValue v, EmRunResult result) => v.Quantity switch
    {
        "Mode_GHz" => result.Data![Em3dEigenResult.FrequencyCube].RealValues[v.Mode!.Value - 1] / 1e9,
        "Mode_Q"   => result.Data![Em3dEigenResult.QCube].RealValues[v.Mode!.Value - 1],
        _          => FromS(v, TouchstoneIO.ReadFile(result.SnpPath!)),
    };

    private static double FromS(ExpectedValue v, SNP snp)
    {
        int k = Array.FindIndex(snp.Frequencies, f => Math.Abs(f - v.AtGHz!.Value * 1e9) < 1);
        Assert.True(k >= 0, $"no point at {v.AtGHz} GHz");
        Complex s = v.Quantity.StartsWith("S11", StringComparison.Ordinal) ? snp.Matrices[k][0, 0] : snp.Matrices[k][1, 0];
        return v.Quantity switch
        {
            "S21_dB" or "S11_dB" => 20 * Math.Log10(s.Magnitude),
            _                    => throw new InvalidOperationException($"unknown quantity '{v.Quantity}'"),
        };
    }

    /// <summary>The TM110 family's lowest root for a PEC box a × b × h with a slab of εr, h₁ thick, on its floor.</summary>
    internal static double LayeredCavityTm110(double a, double b, double h1, double h, double er)
    {
        double kt = Math.PI * Math.Sqrt(1 / (a * a) + 1 / (b * b)), h2 = h - h1;
        double F(double k0)
        {
            double k1 = Math.Sqrt(er * k0 * k0 - kt * kt), al = Math.Sqrt(kt * kt - k0 * k0);
            return k1 * Math.Tan(k1 * h1) / er - al * Math.Tanh(al * h2);
        }
        double lo = kt / Math.Sqrt(er) * (1 + 1e-9), hi = kt * (1 - 1e-9);
        for (int i = 0; i < 200; i++)
        {
            double m = (lo + hi) / 2;
            if (F(lo) * F(m) <= 0) hi = m; else lo = m;
        }
        return (lo + hi) / 2 * 299_792_458.0 / (2 * Math.PI);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static string Root()
        => Path.Combine(ExampleWorkspaces.ResolveRoot(RepoRoot()) ?? throw new InvalidOperationException("no examples/"), "3D Package");

    private static (C3dDocument Doc, string Path, string Cws) Package()
    {
        string path = Path.Combine(Root(), "Package", "3d", "Package.c3d");
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Em3dPackageExampleTests).Assembly)
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

    private sealed record ExpectedValue(string Quantity, string Label, double Expected, double Tolerance, string Readme,
                                        double? AtGHz = null, int? Mode = null);

    private sealed record ExpectedRun(string Setup, string? Variant, string Preset, double PeakGB, List<string> Readme, List<ExpectedValue> Values);

    private sealed record ClosedFormCavity(double WidthMil, double LengthMil, double HeightMil, double SlabMil, double SlabEpsr,
                                           double Expected, string Readme);

    private sealed record ExpectedNumbers(ClosedFormCavity ClosedForm, List<ExpectedRun> Runs);

    private static ExpectedNumbers Numbers()
        => JsonSerializer.Deserialize<ExpectedNumbers>(File.ReadAllText(Path.Combine(Root(), "expected-numbers.json")))!;
}
