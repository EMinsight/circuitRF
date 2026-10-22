using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emw-3 §7 — terminal groups on EMerge. Tests 3–5 need no EMerge: EMW-1's fixtures (testdata/em3d/terminal/
//  emerge-modal/) formed into the transform's sample as the run forms it, and EMerge's refusals. Tests 6–8 run a real EMerge
//  (Category=Benchmark, Skipped without one). The transform's new Robin form is ModalTerminalTransformTests' (test 1).
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class EmergeTerminalPortTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emerge-terminal-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Examples => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Wave Ports");
    private static string Terminal => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "terminal");

    // ── 3. The Coupled Microstrip, no EMerge ───────────────────────────────────────────────────

    /// <summary>
    /// §7 test 3 — EMW-1's Coupled Microstrip (two non-degenerate modes per face) formed as §5 forms it, V = T_V·W⁻¹·(1 + S)/√2,
    /// through <see cref="WavePortTerminalS.Convert"/> with EMerge's Robin form: within 0.015 of Palace's committed terminal S
    /// at 2, 5, 7.5 and 11 GHz, within 0.002 of the fixture's own expectation (formed from the V EMerge measured), and passive.
    /// </summary>
    [Fact]
    public void TheCoupledMicrostrip_FromItsModes_MeetsPalaceAndTheMeasuredRoute()
    {
        var (f, samples, expect) = Fixture("coupled-microstrip");
        var outcome = WavePortTerminalS.Convert(new WavePortModalSamples(f, samples), Ports(), ModalRobinForm.SharedWavenumber, out string? error);
        Assert.True(outcome is not null, error);
        var palace = TouchstoneIO.ReadFile(Path.Combine(Terminal, "coupled-microstrip", "palace", "CoupledMicrostrip.s4p"));
        for (int k = 0; k < f.Length; k++)
        {
            int p = Array.FindIndex(palace.Frequencies, x => Math.Abs(x - f[k]) < 1);
            double toPalace = MaxDiff(outcome.S.S[k], (i, j) => palace[p][i, j]);
            double toExpect = MaxDiff(outcome.S.S[k], (i, j) => expect[k][i, j]);
            output.WriteLine($"{f[k] / 1e9:G4} GHz: |ΔS| {toPalace:F4} from Palace, {toExpect:F5} from the measured route, σ_max {outcome.PerFrequency[k].SigmaMax:F5}");
            Assert.True(toPalace <= 0.015, $"{f[k] / 1e9:G4} GHz: |ΔS| {toPalace:F4} from Palace");
            Assert.True(toExpect <= 0.002, $"{f[k] / 1e9:G4} GHz: |ΔS| {toExpect:F5} from the measured route");
            Assert.True(outcome.PerFrequency[k].SigmaMax <= 1.001, $"σ_max {outcome.PerFrequency[k].SigmaMax}");
        }
    }

    // ── 4. The Pair with PEC sides, no EMerge ──────────────────────────────────────────────────

    /// <summary>
    /// §7 test 4 — EMW-1's Pair with PEC sides (two DEGENERATE modes per face, an arbitrary basis): within 0.015 of Cohn's ideal
    /// coupled line (Z_even 57.511 Ω, Z_odd 43.10 Ω, √2.1, 15 mm), reciprocal to 1e-3, and the route note names the face
    /// degenerate with its g.
    /// </summary>
    [Fact]
    public void ThePair_WithPecSides_IsCohnsCoupledLine()
    {
        var (f, samples, _) = Fixture("pair-pec-sides");
        var ports = Ports();
        var outcome = WavePortTerminalS.Convert(new WavePortModalSamples(f, samples), ports, ModalRobinForm.SharedWavenumber, out string? error);
        Assert.True(outcome is not null, error);
        for (int k = 0; k < f.Length; k++)
        {
            double theta = 2 * Math.PI * f[k] * Math.Sqrt(2.1) / 299792458.0 * 15e-3;
            var cohn = CoupledLine(57.511, 43.10, theta, 50);
            double worst = MaxDiff(outcome.S.S[k], (i, j) => cohn[i, j]);
            output.WriteLine($"{f[k] / 1e9:G4} GHz: |ΔS| {worst:F4} from Cohn's line, reciprocity {outcome.PerFrequency[k].Reciprocity:E1}");
            Assert.True(worst <= 0.015, $"{f[k] / 1e9:G4} GHz: |ΔS| {worst:F4}");
            Assert.True(outcome.PerFrequency[k].Reciprocity <= 1e-3);
        }
        var notes = WavePortTerminalS.RouteNotes(ports, outcome, "EMerge").ToList();
        foreach (string n in notes) output.WriteLine(n);
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Matches(@"of one EMerge wave port, converted to terminal S \(degenerate face: Gram fit g = [−+]\d\.\d{3}", n));
    }

    // ── 5. What EMerge refuses of a terminal group ─────────────────────────────────────────────

    /// <summary>§7 test 5 — each §6 case refused with its sentence, naming openEMS, and the note a comparison goes on with;
    /// a pair of equal-speed terminals (a stripline's), which Palace refuses, is not.</summary>
    [Theory]
    [InlineData("moved", "has its reference plane moved 1 mm off its face; EMerge runs terminal ports with the reference plane on the port face")]
    [InlineData("three-on-one-speed", "has three terminals on a face of one material, whose modes travel at one speed; EMerge runs at most two")]
    [InlineData("ten", "has 10 terminals; EMerge runs at most nine terminals on one face")]
    [InlineData("lumped", "EMerge runs terminal ports only beside other wave ports in this version, and this problem also has a lumped port")]
    [InlineData("equal-speed-pair", null)]
    [InlineData("three-of-different-speeds", null)]
    public void WhatEMergeRefusesOfATerminalGroup_IsRefusedByName(string row, string? expected)
    {
        Em3dTerminalFacts facts = row switch
        {
            "moved" => new([("Left", 2)], false, ("Left", 1e-3)),
            "three-on-one-speed" => new([("Left", 3)], false, null) { OneSpeed = ["Left"] },
            "ten" => new([("Left", 10)], false, null),
            "lumped" => new([("Left", 2)], true, null),
            "equal-speed-pair" => new([("Left", 2), ("Right", 2)], false, null) { OneSpeed = ["Left", "Right"] },
            _ => new([("Left", 3)], false, null) { OneSpeed = [] },
        };
        var refusal = EmergeSolver.Descriptor.PortRefusal(facts);
        if (expected is null) { Assert.Null(refusal); return; }
        Assert.NotNull(refusal);
        output.WriteLine(refusal.Alone);
        Assert.Contains(expected, refusal.Alone, StringComparison.Ordinal);
        Assert.EndsWith("Set the setup's solver to openEMS.", refusal.Alone, StringComparison.Ordinal);
        Assert.EndsWith(", so this setup ran without EMerge.", refusal.InCompare, StringComparison.Ordinal);
    }

    // ── 6–8. With EMerge installed ─────────────────────────────────────────────────────────────

    /// <summary>
    /// §7 test 7 — the 3D Wave Ports Coupled Microstrip end to end on EMerge (its Palace setup, Solver3D Emerge, a point sweep
    /// so the run measures V and makes every check): within 0.02 of Palace's committed S4P at its 21 frequencies, the route
    /// note on both faces, and no warning.
    /// </summary>
    [EmergeFact]
    [Trait("Category", "Benchmark")]
    public void TheCoupledMicrostrip_OnEMerge_MeetsPalacesRun()
    {
        var run = RunCoupledMicrostrip(adaptive: false);
        var palace = TouchstoneIO.ReadFile(Path.Combine(Terminal, "coupled-microstrip", "palace", "CoupledMicrostrip.s4p"));
        var s = run.Data!["S"];
        var f = s.Axes[0].Values;
        double worst = 0, at = 0;
        for (int k = 0; k < f.Length; k++)
        {
            int p = Array.FindIndex(palace.Frequencies, x => Math.Abs(x - f[k]) < 1);
            double d = 0;
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) d = Math.Max(d, (palace[p][i, j] - s.ComplexValues[(k * 4 + i) * 4 + j]).Magnitude);
            output.WriteLine($"{f[k] / 1e9:G4} GHz: |ΔS| {d:F4} from Palace");
            if (d > worst) (worst, at) = (d, f[k]);
        }
        output.WriteLine($"worst |ΔS| {worst:F4} at {at / 1e9:G4} GHz");
        Assert.Contains(run.Notes!, n => n.StartsWith("Port 'xmin': terminals P1, P2 as modes 1 and 2 of one EMerge wave port", StringComparison.Ordinal));
        Assert.Contains(run.Notes!, n => n.StartsWith("Port 'xmax': terminals P3, P4 as modes 1 and 2 of one EMerge wave port", StringComparison.Ordinal));
        Assert.Contains(run.Notes!, n => n.StartsWith("Terminal voltages measured in EMerge's field", StringComparison.Ordinal));
        Assert.Empty(run.Warnings ?? []);
        Assert.True(worst <= 0.02, $"worst |ΔS| {worst:F4} at {at / 1e9:G4} GHz");
    }

    /// <summary>
    /// §7 test 6 — the adaptive sweep's terminal S against a point sweep's at 2, 5, 7.5 and 11 GHz, to 1e-4: a non-degenerate
    /// face's modes at a requested frequency come from a 2D solve there, its basis fixed only by EMerge's sign gauge. The sweep
    /// at EMW-1's tolerance, 1e-4: at the preset's 1e-3 the fit's own error between samples reaches 2.2e-4 (src/Design/RESOLVED.md,
    /// EMW-3), which says nothing of the basis.
    /// </summary>
    [EmergeFact]
    [Trait("Category", "Benchmark")]
    public void TheCoupledMicrostrip_AdaptiveSweep_MatchesAPointSweep()
    {
        var point = RunCoupledMicrostrip(adaptive: false);
        var adaptive = RunCoupledMicrostrip(adaptive: true, tolerance: 1e-4);
        var a = adaptive.Data!["S"];
        var b = point.Data!["S"];
        double worst = 0;
        foreach (double ghz in new[] { 2, 5, 7.5, 11 })
        {
            int ka = Array.FindIndex(a.Axes[0].Values, x => Math.Abs(x - ghz * 1e9) < 1);
            int kb = Array.FindIndex(b.Axes[0].Values, x => Math.Abs(x - ghz * 1e9) < 1);
            double d = 0;
            for (int i = 0; i < 16; i++) d = Math.Max(d, (a.ComplexValues[ka * 16 + i] - b.ComplexValues[kb * 16 + i]).Magnitude);
            output.WriteLine($"{ghz} GHz: |ΔS| {d:E2} between the adaptive and the point sweep");
            worst = Math.Max(worst, d);
        }
        Assert.True(worst <= 1e-4, $"worst |ΔS| {worst:E2}");
    }

    /// <summary>§7 test 8 — the Pair as drawn (PMC sides) splits its reference into two conductors on the face, which then
    /// carries more equal-speed modes than terminals: refused by the presolve's count, naming the cause.</summary>
    [EmergeFact]
    [Trait("Category", "Benchmark")]
    public void ThePairAsDrawn_IsRefused_ItsReferenceSplitByThePmcSides()
    {
        string ws = Workspace();
        string path = Path.Combine(ws, "Pair", "3d", "Pair.c3d");
        var cell = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var setup = cell["Setups"]![0]!.DeepClone().AsObject();
        setup["Name"] = "EMerge";
        setup["Solver3D"] = "Emerge";
        setup.Remove("OpenEms");
        setup.Remove("Mesh");
        setup["EMerge"] = JsonNode.Parse("""{"Quality": "Draft", "AdaptiveSweep": false}""");
        setup["Frequency"]!["StopExpr"] = "10";
        setup["Frequency"]!["NumPoints"] = 2;
        cell["Setups"]!.AsArray().Add(setup);
        File.WriteAllText(path, cell.ToJsonString());

        var run = Run(ws, path, "EMerge");
        output.WriteLine(run.Error);
        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains("of one speed for 2 terminals: its reference is split into separate conductors on the face", run.Error, StringComparison.Ordinal);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A fixture's frequencies, each sample formed from its modes as the run forms it (§5), and its expected S.</summary>
    private static (double[] F, ModalTerminalSample[] Samples, Complex[][,] Expect) Fixture(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Terminal, "emerge-modal", name + ".json")));
        static Complex C(JsonElement e) => new(e[0].GetDouble(), e[1].GetDouble());
        static Complex[,] Matrix(JsonElement e)
        {
            int n = e.GetArrayLength();
            var m = new Complex[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = C(e[i][j]);
            return m;
        }
        var faces = doc.RootElement.GetProperty("faces").EnumerateArray()
                       .Select(f => (IReadOnlyList<int>)[.. f.EnumerateArray().Select(x => x.GetInt32())]).ToList();
        var rows = doc.RootElement.GetProperty("samples").EnumerateArray().ToList();
        var samples = rows.Select(r =>
        {
            int n = r.GetProperty("S").GetArrayLength();
            var tv = new Complex[n, n];
            var w = new Complex[n, n];
            var faceData = r.GetProperty("faces").EnumerateArray().ToList();
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var (ftv, fw) = (Matrix(faceData[fi].GetProperty("TV")), Matrix(faceData[fi].GetProperty("W")));
                for (int a = 0; a < faces[fi].Count; a++)
                    for (int b = 0; b < faces[fi].Count; b++) { tv[faces[fi][a], faces[fi][b]] = ftv[a, b]; w[faces[fi][a], faces[fi][b]] = fw[a, b]; }
            }
            Complex[] gamma = [.. r.GetProperty("Kn").EnumerateArray().Select(k => Complex.ImaginaryOne * C(k))];
            return EmergeTerminalS.Sample(Matrix(r.GetProperty("S")), tv, w, gamma, faces);
        }).ToArray();
        return ([.. rows.Select(r => r.GetProperty("frequencyHz").GetDouble())], samples, [.. rows.Select(r => Matrix(r.GetProperty("expect")))]);
    }

    /// <summary>The fixtures' four terminals: two per face, ports 1 and 2 on xmin, 3 and 4 on xmax, each at 50 Ω.</summary>
    private static Em3dPort[] Ports()
    {
        Em3dPort T(int n, int group, string label) => new(n, $"P{n}", "strip", "airbox/zmin", default, default, default, 50,
                                                          new Em3dReferencePlane(default, default, 0))
            { Kind = Em3dPortKind.Wave, FaceGroup = group, FaceGroupLabel = label, SourceLabel = $"P{n}" };
        return [T(1, 1, "xmin"), T(2, 1, "xmin"), T(3, 3, "xmax"), T(4, 3, "xmax")];
    }

    private static double MaxDiff(Complex[,] a, Func<int, int, Complex> b)
    {
        double worst = 0;
        for (int i = 0; i < a.GetLength(0); i++) for (int j = 0; j < a.GetLength(1); j++) worst = Math.Max(worst, (a[i, j] - b(i, j)).Magnitude);
        return worst;
    }

    /// <summary>Cohn's ideal coupled line: ports 1 and 2 the two lines' near ends, 3 and 4 their far ends (the example's
    /// numbering), the even and odd modes each a line of its own impedance and electrical length.</summary>
    private static Complex[,] CoupledLine(double ze, double zo, double theta, double z0)
    {
        static Complex[,] Line(double zm, double th, double z)
        {
            var d = 2 * zm * z * Math.Cos(th) + Complex.ImaginaryOne * (zm * zm + z * z) * Math.Sin(th);
            var s11 = Complex.ImaginaryOne * (zm * zm - z * z) * Math.Sin(th) / d;
            var s21 = 2 * zm * z / d;
            return new[,] { { s11, s21 }, { s21, s11 } };
        }
        var (se, so) = (Line(ze, theta, z0), Line(zo, theta, z0));
        int[][] ends = [[0, 1], [2, 3]];
        var s = new Complex[4, 4];
        for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
            {
                Complex same = (se[p, q] + so[p, q]) / 2, cross = (se[p, q] - so[p, q]) / 2;
                s[ends[p][0], ends[q][0]] = s[ends[p][1], ends[q][1]] = same;
                s[ends[p][0], ends[q][1]] = s[ends[p][1], ends[q][0]] = cross;
            }
        return s;
    }

    /// <summary>The Coupled Microstrip's Palace setup on EMerge (its 21 frequencies), a point sweep or the adaptive one.</summary>
    private EmRunResult RunCoupledMicrostrip(bool adaptive, double? tolerance = null)
    {
        string ws = Workspace();
        string path = Path.Combine(ws, "Coupled Microstrip", "3d", "Coupled Microstrip.c3d");
        var cell = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var setup = cell["Setups"]!.AsArray().Single(x => (string)x!["Name"]! == "Palace")!.DeepClone().AsObject();
        setup["Name"] = "EMerge";
        setup["Solver3D"] = "Emerge";
        setup.Remove("Palace");
        setup["EMerge"] = JsonNode.Parse($$"""{"AdaptiveSweep": {{(adaptive ? "true" : "false")}}, "SaveFieldsGHz": []}""");
        if (tolerance is { } tol) setup["EMerge"]!["Tolerance"] = tol;
        cell["Setups"]!.AsArray().Add(setup);
        File.WriteAllText(path, cell.ToJsonString());
        // On the CPU: Auto puts this mesh (336,748 unknowns) on the GPU, where the adaptive sweep did not converge in 79
        // samples at 7.6 s each, while the CPU converged in 9 at 1.9 s each (src/Design/RESOLVED.md, EMW-3).
        var run = Run(ws, path, "EMerge", EmergeDevice.Cpu);
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        return run;
    }

    /// <summary>A copy of the 3D Wave Ports example, its own workspace.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N")[..6]);
        EmergeRunTests.CopyDirectory(Examples, ws);
        return ws;
    }

    private EmRunResult Run(string ws, string path, string setupName, EmergeDevice device = EmergeDevice.Auto)
    {
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == setupName).Setup!, path);
        var run = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(ws, ".cws"), Path.Combine(ws, "results"), emergeDevice: device);
        foreach (string n in run.Notes ?? []) output.WriteLine("note: " + n);
        foreach (string w in run.Warnings ?? []) output.WriteLine("warning: " + w);
        return run;
    }
}
