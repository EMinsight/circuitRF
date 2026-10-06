// ================================================================
//  Em3dWavePortsExampleTests.cs — the shipped 3D Wave Ports example (brief-em3d-117 §6): the 3D Connector's launch fed by a
//  coaxial wave port on Palace and on openEMS, and an edge-coupled stripline pair with a two-terminal wave port at each end
//  on openEMS.
//
//  Brief 70's discipline: `expected-numbers.json` beside the README is the ONE source of every number the README and the
//  new-user guide's walk-through print; each value's text must appear verbatim in both, the closed forms are recomputed
//  here from the cells' VARs, the recorded Pair result is held to Cohn's ideal coupled line, and gate 4 re-runs the three
//  shipped setups (Category=Benchmark). What needs the geometry kernel (the Launch) is a [KernelFact].
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
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class Em3dWavePortsExampleTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-em3d117-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── 1. check, explain, and Palace on Pair ───────────────────────────────────────────────────

    /// <summary><c>check</c> on the workspace, as a process: every document (both 3D views among them) elaborates with no
    /// error, and the one warning is the fidelity row the README quotes.</summary>
    [KernelFact]
    public void Gate1_CheckIsClean()
    {
        var (errors, warnings) = Check(Root());
        Assert.Empty(errors);
        Assert.All(warnings, w => Assert.Contains("will not represent the 100 µm fillet on 'pin'", w));
    }

    /// <summary><c>explain</c> on Pair lists both faces' terminals against the stated reference.</summary>
    [Fact]
    public void Gate1_ExplainListsPairsFourTerminals()
    {
        string stdout = Cli("explain", PairPath(), "--setup", "openEMS");
        for (int n = 1; n <= 4; n++)
            Assert.Matches($@"{n} port/{n}\s+strip_{(n % 2 == 1 ? 'a' : 'b')} → airbox/zmin", stdout);
    }

    /// <summary>A Palace setup added to Pair by hand is refused before Gmsh, with brief 114/116's sentence (overview D14).</summary>
    [Fact]
    public void Gate1_APalaceSetupOnPair_IsRefusedNamingOpenEms()
    {
        long gmsh = PalaceRun.GmshInvocations;
        var (doc, path, cws) = Pair();
        var (embedded, why) = C3dSetups.Select(doc, "openEMS");
        Assert.True(embedded is not null, why);
        var setup = C3dSetups.ForRun(embedded!, path);
        setup.Solver3D = Em3dSolver.Palace;
        var run = EmRunService.RunThreeDView(setup, doc, path, cws, Path.Combine(_tmp, "results"));
        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains("Port 'xmin' has two terminals; terminal wave ports run on openEMS only in this version. " +
                        "Set the setup's solver to openEMS.", run.Error);
        Assert.Equal(gmsh, PalaceRun.GmshInvocations);
    }

    /// <summary>Board, Flange, flange.step and the technology are the 3D Connector's, unchanged (R-em3d117-1).</summary>
    [Fact]
    public void TheCopiedCells_AreTheConnectorsByteForByte()
    {
        string connector = Path.Combine(Path.GetDirectoryName(Root())!, "3D Connector");
        foreach (string f in new[] { "Board/layout/Board.clay", "Flange/3d/Flange.c3d", "Launch/3d/flange.step", "tech/board-and-connector.ctech" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(connector, f)), File.ReadAllBytes(Path.Combine(Root(), f)));
    }

    // ── 2. one source ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("examples/3D Wave Ports/README.md")]
    [InlineData("docs/user/src/new-user-guide/index.md")]
    public void Gate2_TheReadmeAndTheWalkThroughCiteEveryNumberInTheFile(string page)
    {
        var n = Numbers();
        string text = File.ReadAllText(Path.Combine(RepoRoot(), page));
        var quoted = n.References.SelectMany(r => r.Readme)
                      .Concat(n.Ideal.Select(i => i.Readme))
                      .Concat(n.Lumped.Select(l => l.Readme))
                      .Concat(n.Lines.SelectMany(l => l.Readme))
                      .Concat(n.Runs.SelectMany(r => r.Readme.Concat(r.Values.Select(v => v.Readme))))
                      .Concat(n.Alternatives.SelectMany(r => r.Readme.Concat(r.Values.Select(v => v.Readme))));
        foreach (string s in quoted) Assert.Contains(s, text);
    }

    /// <summary>Every quoted |S| is its run's recorded value; every lumped comparison is the 3D Connector's own recorded one.</summary>
    [Fact]
    public void TheQuotedValues_AreTheRecordedOnes()
    {
        var n = Numbers();
        foreach (var run in n.Runs)
            foreach (var v in run.Values)
                Assert.Equal(run.Recorded.Single(r => r["GHz"] == v.AtGHz)[v.Quantity], v.Expected);

        using var connector = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Root())!, "3D Connector",
                                                                               "expected-numbers.json")));
        foreach (var l in n.Lumped)
        {
            var run = connector.RootElement.GetProperty("Runs").EnumerateArray()
                               .Single(r => r.GetProperty("Setup").GetString() == l.Setup && r.GetProperty("Variant").GetString() == "fillet");
            var point = run.GetProperty("Recorded").EnumerateArray().Single(p => p.GetProperty("GHz").GetDouble() == l.AtGHz);
            Assert.Equal(point.GetProperty(l.Quantity).GetDouble(), l.Expected);
        }
    }

    // ── 3. the closed forms, and the Pair against Cohn ──────────────────────────────────────────

    /// <summary>The coax's Z₀ = η₀/(2π√εr)·ln(D/d) from the Launch's VARs, Cohn's zero-thickness Z₀e/Z₀o from Pair's, and the
    /// ideal coupled line's |S| at the frequency the pages quote it.</summary>
    [Fact]
    public void Gate3_TheClosedFormsInTheFile_AreTheCellsVars()
    {
        var refs = Numbers().References;
        double coax = Eta0 / (2 * Math.PI * Math.Sqrt(Epsr)) * Math.Log(Var(LaunchPath(), "bore_d") / Var(LaunchPath(), "pin_d"));
        var (ze, zo) = Cohn(Var(PairPath(), "w"), Var(PairPath(), "s"), Var(PairPath(), "b"), Epsr);
        output.WriteLine($"coax {coax:F4} Ω, Z0e {ze:F4} Ω, Z0o {zo:F4} Ω");
        Assert.Equal(refs.Single(r => r.Name == "coax").Expected, coax, 3);
        Assert.Equal(refs.Single(r => r.Name == "even").Expected, ze, 3);
        Assert.Equal(refs.Single(r => r.Name == "odd").Expected, zo, 3);
        foreach (var i in Numbers().Ideal)
        {
            var column = CoupledLine(ze, zo, Var(PairPath(), "len"), Epsr, i.AtGHz * 1e9, 50);
            Assert.Equal(i.Expected, Db(column[i.Quantity[1] - '1']), 3);
        }
    }

    /// <summary>
    /// The recorded Pair result against Cohn's ideal coupled line, at brief 116's gate-4 tolerances: the thru (S31) within
    /// 0.1 dB / 1°, the near-end (S21) and far-end (S41) coupling within 0.5 dB / 1°, and max |ΔS| over the column ≤ 0.015.
    /// An entry whose ideal value is below −30 dB (the near-end nulls, the reflection) is judged by |ΔS| alone.
    /// </summary>
    [Fact]
    public void Gate3_TheRecordedPair_IsCohnsCoupledLine()
    {
        var (ze, zo) = Cohn(Var(PairPath(), "w"), Var(PairPath(), "s"), Var(PairPath(), "b"), Epsr);
        double len = Var(PairPath(), "len");
        var run = Numbers().Runs.Single(r => r.Cell == "Pair");
        var bad = new List<string>();
        foreach (var p in run.Recorded)
        {
            var ideal = CoupledLine(ze, zo, len, Epsr, p["GHz"] * 1e9, 50);
            double worst = 0;
            foreach (var (q, k, db) in new[] { ("S11", 0, 0.0), ("S21", 1, 0.5), ("S31", 2, 0.1), ("S41", 3, 0.5) })
            {
                var got = Complex.FromPolarCoordinates(Math.Pow(10, p[q + "_dB"] / 20), p[q + "_deg"] * Math.PI / 180);
                worst = Math.Max(worst, (got - ideal[k]).Magnitude);
                if (Db(ideal[k]) < -30) continue;
                double dDb = p[q + "_dB"] - Db(ideal[k]), dDeg = (got / ideal[k]).Phase * 180 / Math.PI;
                if (Math.Abs(dDb) > db || Math.Abs(dDeg) > 1) bad.Add($"{q} at {p["GHz"]} GHz: {dDb:+0.000;-0.000} dB, {dDeg:+0.00;-0.00}°");
            }
            output.WriteLine($"{p["GHz"],5} GHz max |ΔS| {worst:F4}");
            if (worst > 0.015) bad.Add($"max |ΔS| {worst:F4} at {p["GHz"]} GHz");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // ── 4. the solves: a regression test ────────────────────────────────────────────────────────

    /// <summary>The three shipped runs — Launch on Palace and on openEMS, Pair on openEMS — each reproducing its RECORDED |S|
    /// at every frequency within its tolerances, as the 3D Connector's gate 6 does. An entry recorded below −40 dB is held by
    /// |ΔS| ≤ 0.001 instead: a null's dB is set by digits no regression is about.</summary>
    [CircuitRF.Ui.Tests.Em3d.PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate4_EveryRun_ReproducesItsRecordedSParameters()
    {
        var moved = new List<string>();
        foreach (var run in Numbers().Runs)
        {
            var (doc, path, cws) = run.Cell == "Pair" ? Pair() : Launch();
            var (embedded, why) = C3dSetups.Select(doc, run.Setup);
            Assert.True(embedded is not null, why);
            var wall = Stopwatch.StartNew();
            var result = EmRunService.RunThreeDView(C3dSetups.ForRun(embedded!, path), doc, path, cws, Path.Combine(_tmp, "results"),
                                                    confirmMemory: _ => true);
            output.WriteLine($"{run.Cell} {run.Setup}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s (recorded {run.Readme[0]})");
            Assert.True(result.Status == EmRunStatus.Ok, $"{run.Cell} {run.Setup}: {result.Error}");
            var snp = TouchstoneIO.ReadFile(result.SnpPath!);
            Assert.Equal(run.Recorded.Count, snp.Frequencies.Length);
            foreach (var r in run.Recorded)
            {
                int k = Array.FindIndex(snp.Frequencies, f => Math.Abs(f - r["GHz"] * 1e9) < 1);
                Assert.True(k >= 0, $"{run.Cell} {run.Setup}: no point at {r["GHz"]} GHz");
                foreach (var (q, t) in run.Tolerance)
                {
                    var s = snp.Matrices[k][q[1] - '1', q[2] - '1'];
                    double got = Db(s), want = r[q + "_dB"];
                    bool ok = want < -40 ? Math.Abs(s.Magnitude - Math.Pow(10, want / 20)) <= 1e-3 : Math.Abs(got - want) <= t;
                    output.WriteLine($"  {r["GHz"],5} GHz |{q}| {got,9:F4} dB  recorded {want,9:F4}");
                    if (!ok) moved.Add($"{run.Cell} {run.Setup}: |{q}| at {r["GHz"]} GHz is {got:F4} dB, recorded {want:F4} dB (± {t})");
                }
            }
        }
        Assert.True(moved.Count == 0, "3D EM results moved from the recorded ones:\n" + string.Join("\n", moved));
    }

    // ── no vendor names ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoVendorNameIsInTheExample()
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

    // ── the closed forms ────────────────────────────────────────────────────────────────────────

    private const double Eta0 = 376.730313668, C0 = 299_792_458, Epsr = 2.1;

    /// <summary>Cohn's edge-coupled stripline at zero thickness between infinite planes b apart: Z₀ = η₀/(4√εr)·K(k′)/K(k), with
    /// k_e = tanh(πW/2b)·tanh(π(W+S)/2b) and k_o = tanh(πW/2b)·coth(π(W+S)/2b). Lengths in any one unit.</summary>
    internal static (double Even, double Odd) Cohn(double w, double s, double b, double er)
    {
        double a = Math.Tanh(Math.PI * w / (2 * b)), c = Math.PI * (w + s) / (2 * b);
        double Z(double k) => Eta0 / (4 * Math.Sqrt(er)) * K(Math.Sqrt(1 - k * k)) / K(k);
        return (Z(a * Math.Tanh(c)), Z(a / Math.Tanh(c)));
    }

    /// <summary>The complete elliptic integral of the first kind, modulus k, by the arithmetic–geometric mean.</summary>
    private static double K(double k)
    {
        double a = 1, g = Math.Sqrt(1 - k * k);
        while (Math.Abs(a - g) > 1e-15 * a) (a, g) = ((a + g) / 2, Math.Sqrt(a * g));
        return Math.PI / (2 * a);
    }

    /// <summary>Column 1 of the ideal symmetric coupled line in a homogeneous dielectric, every terminal in z0, numbered as the
    /// example: 1 and 2 at x = 0 (strips a and b), 3 and 4 at x = len — so S21 near-end, S31 thru, S41 far-end.</summary>
    internal static Complex[] CoupledLine(double zEven, double zOdd, double lenMm, double er, double f, double z0)
    {
        double th = 2 * Math.PI * f * lenMm * 1e-3 * Math.Sqrt(er) / C0;
        (Complex S11, Complex S21) Line(double zc)
        {
            var den = 2 * zc * z0 * Math.Cos(th) + Complex.ImaginaryOne * (zc * zc + z0 * z0) * Math.Sin(th);
            return (Complex.ImaginaryOne * (zc * zc - z0 * z0) * Math.Sin(th) / den, 2 * zc * z0 / den);
        }
        var (e11, e21) = Line(zEven);
        var (o11, o21) = Line(zOdd);
        return [(e11 + o11) / 2, (e11 - o11) / 2, (e21 + o21) / 2, (e21 - o21) / 2];
    }

    private static double Db(Complex s) => 20 * Math.Log10(s.Magnitude);

    /// <summary>A VAR of a .c3d, in millimetres (each is a plain number with Unit Mm).</summary>
    private static double Var(string c3d, string name)
    {
        var v = C3dPersistence.LoadFromFile(c3d).Variables.Single(x => x.Name == name);
        Assert.Equal("Mm", v.Unit);
        return double.Parse(v.Expression, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static string Root()
        => Path.Combine(ExampleWorkspaces.ResolveRoot(RepoRoot()) ?? throw new InvalidOperationException("no examples/"), "3D Wave Ports");

    private static string LaunchPath() => Path.Combine(Root(), "Launch", "3d", "Launch.c3d");

    private static string PairPath() => Path.Combine(Root(), "Pair", "3d", "Pair.c3d");

    private static (C3dDocument Doc, string Path, string Cws) Launch()
        => (C3dPersistence.LoadFromFile(LaunchPath()), LaunchPath(), Path.Combine(Root(), ".cws"));

    private static (C3dDocument Doc, string Path, string Cws) Pair()
        => (C3dPersistence.LoadFromFile(PairPath()), PairPath(), Path.Combine(Root(), ".cws"));

    /// <summary><c>check --json</c> as a process: the verb a user runs.</summary>
    private static (List<string> Errors, List<string> Warnings) Check(string path)
    {
        using var doc = JsonDocument.Parse(Cli("check", path, "--json"));
        var diags = doc.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        List<string> Of(string severity) => [.. diags.Where(d => d.GetProperty("severity").GetString() == severity)
                                                     .Select(d => d.GetProperty("message").GetString()!)];
        return (Of("error"), Of("warning"));
    }

    /// <summary>The CLI as a process; its stdout.</summary>
    private static string Cli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Em3dWavePortsExampleTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return outTask.GetAwaiter().GetResult();
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }

    // ── expected-numbers.json ───────────────────────────────────────────────────────────────────

    private sealed record ExpectedValue(string Quantity, double AtGHz, double Expected, string Readme);

    /// <summary>A shipped run: its cell and setup, what it cost (Readme), the values the pages quote, the per-quantity regression
    /// tolerance in dB, and the recorded |S| (dB) — with phase (deg) for Pair, which gate 3 needs — at every frequency.</summary>
    private sealed record ExpectedRun(string Cell, string Setup, string Preset, List<string> Readme, List<ExpectedValue> Values,
                                      Dictionary<string, double> Tolerance, List<Dictionary<string, double>> Recorded);

    private sealed record Reference(string Name, double Expected, List<string> Readme);

    /// <summary>A 3D Connector value this example's pages set beside its own (that file's fillet run).</summary>
    private sealed record LumpedValue(string Setup, string Quantity, double AtGHz, double Expected, string Readme);

    /// <summary>What a run's notes said of a line: Palace's mode impedance, openEMS's measured Z and ε_eff.</summary>
    private sealed record LineNote(string Cell, string Setup, string What, List<string> Readme);

    /// <summary>A setting measured once and not shipped: what it cost and what it read.</summary>
    private sealed record Alternative(string Cell, string Setup, string Preset, List<string> Readme, List<ExpectedValue> Values);

    private sealed record ExpectedNumbers(List<Reference> References, List<ExpectedValue> Ideal, List<LumpedValue> Lumped, List<LineNote> Lines,
                                          List<ExpectedRun> Runs, List<Alternative> Alternatives);

    private static ExpectedNumbers Numbers()
        => JsonSerializer.Deserialize<ExpectedNumbers>(File.ReadAllText(Path.Combine(Root(), "expected-numbers.json")))!;
}
