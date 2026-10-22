using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Fields;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emw-2 §7 — single-terminal wave ports on EMerge: what the result does with a wave port's S (no EMerge needed), and
//  two runs against a real EMerge (Category=Benchmark, Skipped without one): the polarity of a wave port against a lumped
//  one, and the 3D Wave Ports Launch against Palace's recorded Draft run. The lowering's own gates are EmergeLoweringTests.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class EmergeWavePortTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emerge-wave-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Examples => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Wave Ports");

    // ── 1. Shift, then renormalise ─────────────────────────────────────────────────────────────

    /// <summary>
    /// R-emw2-4 — EMW-1's coax (testdata/em3d/terminal/emerge-modal/coax.json: 10 mm, its modal S, Z_PV and β at 2, 6 and
    /// 10 GHz) through the result's own code with each reference plane 1 mm into the line: ∠S21 is −β(ℓ − 2 mm) to 0.05°, and
    /// S at 50 Ω is within 0.006 of the ideal line of that length, Z_PV (the two faces' mean) and β. The other order, the shift
    /// after the renormalisation, moves S11 by at most 0.0032 on this line, under the tolerance, so it is not gated
    /// (src/Design/RESOLVED.md, EMW-2): Z_PV is within 1 % of 50 Ω, and the order matters only as far as the renormalisation
    /// mixes the waves.
    /// </summary>
    [Fact]
    public void TheCoax_ShiftedThenRenormalised_IsTheShorterIdealLine()
    {
        const double length = 10e-3, shift = 1e-3;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "terminal",
                                                                         "emerge-modal", "coax.json")));
        static Complex C(JsonElement e) => new(e[0].GetDouble(), e[1].GetDouble());
        var samples = doc.RootElement.GetProperty("samples").EnumerateArray().ToList();
        var f = samples.Select(s => s.GetProperty("frequencyHz").GetDouble()).ToArray();
        var s = samples.Select(x =>
        {
            var m = new Complex[2, 2];
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) m[i, j] = C(x.GetProperty("S")[i][j]);
            return m;
        }).ToArray();
        Complex[][] z = [.. samples.Select(x => x.GetProperty("ZPv").EnumerateArray().Select(v => new Complex(v.GetDouble(), 0)).ToArray())];
        Complex[][] gamma = [.. samples.Select(x => x.GetProperty("Kn").EnumerateArray().Select(k => Complex.ImaginaryOne * C(k)).ToArray())];
        Em3dPort Port(int n) => new(n, $"P{n}", "pin", "shield", default, default, default, 50,
                                    new Em3dReferencePlane(default, default, shift)) { Kind = Em3dPortKind.Wave };
        Em3dPort[] ports = [Port(1), Port(2)];

        var at50 = EmergeResults.AtPortReferences(new EmergeSweep(f, s, z, gamma), ports, out bool renormalised);
        Assert.True(renormalised);
        for (int k = 0; k < f.Length; k++)
        {
            double beta = gamma[k][0].Imaginary, theta = beta * (length - 2 * shift), zc = (z[k][0].Real + z[k][1].Real) / 2;
            var ideal = IdealLine(zc, theta, 50);
            double angle = (at50.S[k][1, 0].Phase + theta) * 180 / Math.PI;
            angle -= 360 * Math.Round(angle / 360);
            double worst = 0;
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) worst = Math.Max(worst, (at50.S[k][i, j] - ideal[i, j]).Magnitude);
            output.WriteLine($"{f[k] / 1e9:G4} GHz: ∠S21 off −β(ℓ − 2d) by {angle:F4}°, max |ΔS| {worst:F4} against the ideal line");
            Assert.InRange(angle, -0.05, 0.05);
            Assert.True(worst <= 0.006, $"{f[k] / 1e9:G4} GHz: max |ΔS| {worst:F4}");
        }
    }

    /// <summary>A lossless line of impedance <paramref name="zc"/> and electrical length <paramref name="theta"/>, at
    /// <paramref name="z0"/>.</summary>
    private static Complex[,] IdealLine(double zc, double theta, double z0)
    {
        var d = 2 * zc * z0 * Math.Cos(theta) + Complex.ImaginaryOne * (zc * zc + z0 * z0) * Math.Sin(theta);
        var s11 = Complex.ImaginaryOne * (zc * zc - z0 * z0) * Math.Sin(theta) / d;
        var s21 = 2 * zc * z0 / d;
        return new[,] { { s11, s21 }, { s21, s11 } };
    }

    // ── 2. With EMerge installed ───────────────────────────────────────────────────────────────

    /// <summary>
    /// R-emw2-2a — the polarity of a wave port against a lumped one: EMW-1's case A, one 1.575 mm microstrip 15 mm long in the
    /// Coupled Microstrip's housing (its laminate and PEC walls, the lid 3 mm above), a wave port on the xmin face with its path
    /// from the floor to the strip and a lumped port across the strip's end at x = 15 mm. EMerge's raw ∠S21 is within 20° of
    /// −βℓ, β the mode's own; a reversed convention is 180° off. The strip is a 35 µm copper box; a copper sheet runs as well.
    /// </summary>
    [EmergeFact]
    [Trait("Category", "Benchmark")]
    public void AWavePortAndALumpedPort_OnAThru_AgreeOnPolarity()
    {
        string ws = Workspace();
        var cell = JsonNode.Parse(File.ReadAllText(Path.Combine(ws, "Coupled Microstrip", "3d", "Coupled Microstrip.c3d")))!.AsObject();
        cell["Objects"] = new JsonArray(
            cell["Objects"]![0]!.DeepClone(),
            JsonNode.Parse("""
                {"$type": "Box", "Name": "strip", "Material": "Copper", "Min": [0, {"Expr": "-w/2", "Unit": "Mm"}, {"Expr": "h", "Unit": "Mm"}],
                 "Size": [{"Expr": "len", "Unit": "Mm"}, {"Expr": "w", "Unit": "Mm"}, 35000]}
                """));
        var variables = cell["Variables"]!.AsArray().Where(v => (string)v!["Name"]! is not ("wa" or "wb" or "s")).Select(v => v!.DeepClone()).ToList();
        variables.Add(JsonNode.Parse("""{"Name": "w", "Expression": "1.575", "Unit": "Mm"}"""));
        cell["Variables"] = new JsonArray([.. variables]);
        cell["MeshRegions"] = new JsonArray();
        cell["Ports"] = JsonNode.Parse("""
            [{"Number": 1, "Name": "P1", "Kind": "Wave", "Plane": "YZ", "Offset": 0, "Rect": {"Min": [-4500000, 0], "Size": [9000000, 3508000]},
              "Positive": "strip", "Negative": "airbox/zmin", "VoltagePath": {"From": [0, 0], "To": [0, 508000]}, "Z0": "50"},
             {"Number": 2, "Name": "P2", "Kind": "Lumped", "Plane": "YZ", "Offset": 15000000,
              "Rect": {"Min": [-787500, 0], "Size": [1575000, 508000]}, "Z0": "50"}]
            """);
        var setup = cell["Setups"]![0]!.DeepClone().AsObject();
        setup["Name"] = "EMerge";
        setup["Solver3D"] = "Emerge";
        setup.Remove("Palace");
        setup["EMerge"] = JsonNode.Parse("""{"Quality": "Draft"}""");
        setup["Frequency"]!["StopExpr"] = "10";
        setup["Frequency"]!["NumPoints"] = 3;
        setup["AirBox"]!["XMin"] = JsonNode.Parse("""{"PaddingUm": 0, "Boundary": "Pec"}""");
        setup["AirBox"]!["XMax"] = JsonNode.Parse("""{"PaddingUm": 1000, "Boundary": "Pec"}""");
        cell["Setups"] = new JsonArray(setup);
        string path = Path.Combine(ws, "Thru", "3d", "Thru.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, cell.ToJsonString());

        var run = Run(ws, path, "EMerge");
        string dir = RunDirectory(run);
        var (s, _) = PlainNpy.ReadComplex(Path.Combine(dir, EmergeResults.SFile));
        var (gamma, _) = PlainNpy.ReadComplex(Path.Combine(dir, EmergeResults.GammaFile));
        var (f, _) = PlainNpy.ReadReal(Path.Combine(dir, EmergeResults.FrequencyFile));
        for (int k = 0; k < f.Length; k++)
        {
            double off = (s[k * 4 + 2].Phase + gamma[k * 2].Imaginary * 15e-3) * 180 / Math.PI;
            off -= 360 * Math.Round(off / 360);
            output.WriteLine($"{f[k] / 1e9:G4} GHz: raw ∠S21 off −βℓ by {off:F2}° (β/k₀ {gamma[k * 2].Imaginary * 299792458 / (2 * Math.PI * f[k]):F4})");
            Assert.InRange(off, -20, 20);
        }
    }

    /// <summary>
    /// §7 test 7 — the 3D Wave Ports Launch on EMerge at Draft against Palace's recorded Draft run
    /// (examples/3D Wave Ports/expected-numbers.json): the coax's Z_PV within 1 % of 50.02 Ω, and |ΔS| ≤ 0.03 on S11 and S21 at its
    /// five frequencies, as magnitudes (the record holds dB). One field step of the run draws: its E opens and is finite and
    /// not zero.
    /// </summary>
    [EmergeFact]
    [Trait("Category", "Benchmark")]
    public void TheLaunch_OnEMergeAtDraft_MeetsPalacesDraftRun()
    {
        string ws = Workspace();
        string path = Path.Combine(ws, "Launch", "3d", "Launch.c3d");
        var cell = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var setup = cell["Setups"]!.AsArray().Single(x => (string)x!["Name"]! == "Palace")!.DeepClone().AsObject();
        setup["Name"] = "EMerge";
        setup["Solver3D"] = "Emerge";
        setup.Remove("Palace");
        setup["EMerge"] = JsonNode.Parse("""{"Quality": "Draft"}""");
        cell["Setups"]!.AsArray().Add(setup);
        File.WriteAllText(path, cell.ToJsonString());

        var run = Run(ws, path, "EMerge");
        var zMode = run.Data!.CubesIn(EmergeResults.Group)[Em3dRunService.WaveModeImpedanceCube].ComplexValues;
        output.WriteLine($"Z_PV {string.Join(", ", zMode.Select(z => z.Real.ToString("F3")))} Ω");
        Assert.All(zMode, z => Assert.InRange(z.Real, 50.021 * 0.99, 50.021 * 1.01));

        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Examples, "expected-numbers.json")));
        var palace = expected.RootElement.GetProperty("Runs").EnumerateArray()
                             .Single(r => r.GetProperty("Cell").GetString() == "Launch" && r.GetProperty("Setup").GetString() == "Palace");
        var s = run.Data["S"];
        var f = s.Axes[0].Values;
        var bad = new List<string>();
        foreach (var row in palace.GetProperty("Recorded").EnumerateArray())
        {
            int k = Array.FindIndex(f, x => Math.Abs(x - row.GetProperty("GHz").GetDouble() * 1e9) < 1);
            foreach (var (name, i, j) in new[] { ("S11", 0, 0), ("S21", 1, 0) })
            {
                double mine = s.ComplexValues[(k * 2 + i) * 2 + j].Magnitude, theirs = Math.Pow(10, row.GetProperty(name + "_dB").GetDouble() / 20);
                output.WriteLine($"{f[k] / 1e9:G4} GHz {name}: {20 * Math.Log10(mine):F3} dB against Palace's {20 * Math.Log10(theirs):F3} dB, |Δ| {Math.Abs(mine - theirs):F4}");
                if (Math.Abs(mine - theirs) > 0.03) bad.Add($"{name} at {f[k] / 1e9:G4} GHz: |Δ| {Math.Abs(mine - theirs):F4}");
            }
        }

        var fields = FieldRun.OpenEmerge(RunDirectory(run))!;
        var step = FieldStep.Open(fields.Solutions.First(x => x.Excitation == 1).VolumePvtu!, fields.ToMetres);
        var e = step.Load("E")!;
        Assert.All(e.Re, v => Assert.True(float.IsFinite(v)));
        Assert.Contains(e.Re, v => v != 0);
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A copy of the 3D Wave Ports example, its own workspace.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws");
        EmergeRunTests.CopyDirectory(Examples, ws);
        return ws;
    }

    private EmRunResult Run(string ws, string path, string setupName)
    {
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == setupName).Setup!, path);
        var run = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(ws, ".cws"), Path.Combine(ws, "results"));
        foreach (string n in run.Notes ?? []) output.WriteLine("note: " + n);
        foreach (string w in run.Warnings ?? []) output.WriteLine("warning: " + w);
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        return run;
    }

    /// <summary>The run directory EMerge solved in: the .sNp's own name without its extension.</summary>
    private static string RunDirectory(EmRunResult run) => Path.ChangeExtension(run.SnpPath!, null);
}
