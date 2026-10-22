// ================================================================
//  LoopCoilReplicationTests.cs — the gate of brief-sar-8: openEMS's MRI_Loop_Coil tutorial rebuilt from .c3d objects
//  (testdata/sar/circuitrf/loop), run through circuitRF, against SAR-1's reference (testdata/sar/openems-ref/MRI_Loop_Coil).
//
//    LoopCoilReplicationTests   fixed data, routine: circuitRF's committed output against the reference — run (a), on the
//                               reference's own grid lines, at overview §4's tolerances; run (b), on circuitRF's grid,
//                               reported. No openEMS process. Skipped where the reference set is absent (never in the repo).
//    LoopCoilRerunTests         Benchmark: both runs again through the CLI, (a) held to the same tolerances. With
//                               CRF_WRITE_SAR8_OUTPUTS=1 it also rewrites the committed output.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Sar;

/// <summary>Where the replication lives, what a run leaves, and the comparison both the routine and the Benchmark test make.</summary>
internal static class LoopReplication
{
    public static readonly string Workspace = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "circuitrf", "loop");
    public static readonly string Reference = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "openems-ref", "MRI_Loop_Coil");
    public const string C3d = "Loop/3d/Loop.c3d";

    /// <summary>The two runs: the setup's name in the .c3d, and the folder its output is kept in.</summary>
    public static readonly (string Setup, string Folder)[] Runs = [("ReferenceGrid", "reference-grid"), ("OwnGrid", "own-grid")];

    public const string Touchstone = "Loop.s1p";
    public const string Record = "sar1.json";
    public const string RunFacts = "run.json";
    public static readonly string[] Probes = ["port1_u", "port1_i"];

    /// <summary>The tutorial's feed resistance (Ω) and Larmor frequency (Hz).</summary>
    public const double Z0 = 2.5, F0 = 298e6;

    /// <summary>The volumes kept, each as the tutorial's two planes: axial (z = 0) and sagittal (y = 0).</summary>
    public static readonly string[] Quantities = [SarVolumeFiles.LocalQuantity, SarVolumeFiles.B1PlusQuantity, SarVolumeFiles.B1MinusQuantity];
    public static readonly (string Name, int Axis)[] Planes = [("z0", 2), ("y0", 1)];

    public static string PlaneFile(string quantity, string plane) => $"sar1.{quantity}.{plane}.npy";

    /// <summary>
    /// Copies what a run of <paramref name="setup"/> left under <paramref name="resultsRoot"/> into <paramref name="outDir"/>: the
    /// Touchstone, the port's probes, the SAR record, the volumes' cell centres, each volume's two planes (linear between the cell
    /// layers either side of 0, which on the reference's lines is the layer AT 0), and run.json — the grid, the stop step, how the
    /// run stopped and openEMS's wall time, from its own log — labelled as circuitRF output. The volumes themselves stay in the run
    /// directory: 3.7 MB each, where the planes are 40 kB.
    /// </summary>
    public static void Collect(string resultsRoot, string setup, string outDir)
    {
        string stem = Path.Combine(resultsRoot, $"Loop {setup}.openems");
        string p1 = Path.Combine(stem, "p1");
        Directory.CreateDirectory(outDir);
        File.Copy(stem + ".s1p", Path.Combine(outDir, Touchstone), true);
        foreach (string f in Probes) File.Copy(Path.Combine(p1, f), Path.Combine(outDir, f), true);
        CollectVolumes(p1, outDir);

        var facts = Facts(Path.Combine(p1, "openems.log"),
                          "circuitRF OUTPUT, not a reference: setup " + setup + " of Loop.c3d, run by circuitRF on openEMS " +
                          "(brief-sar-8). The reference it is compared with is testdata/sar/openems-ref/MRI_Loop_Coil.", setup);
        File.WriteAllText(Path.Combine(outDir, RunFacts), facts.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>One port run's facts from openEMS's own log: the grid, the time step, the step it stopped at and what stopped it, the
    /// energy there and openEMS's wall time.</summary>
    public static JsonObject Facts(string logPath, string note, string setup)
    {
        string log = File.ReadAllText(logPath);
        var size = Regex.Match(log, @"FDTD simulation size: (\d+)x(\d+)x(\d+)");
        var step = Regex.Match(log, @"FDTD timestep is: ([\d.e+-]+) s");
        var time = Regex.Match(log, @"Time for (\d+) iterations with [\d.e+]+ cells : ([\d.]+) sec");
        var energy = Regex.Matches(log, @"Energy: ~[\d.e+-]+ \(-\s*([\d.]+)dB\)");
        return new JsonObject
        {
            ["note"] = note,
            ["setup"] = setup,
            ["grid_lines"] = new JsonArray(size.Groups.Values.Skip(1).Select(g => (JsonNode)int.Parse(g.Value)).ToArray()),
            ["timestep_s"] = step.Success ? double.Parse(step.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            ["stopped_at_timestep"] = time.Success ? int.Parse(time.Groups[1].Value) : null,
            ["stopped_by"] = log.Contains("Found file \"ABORT\"", StringComparison.Ordinal) ? "circuitRF: the port signals decayed to the end criterion"
                           : Regex.IsMatch(log, "end-criteria of [-\\d.]+dB reached") ? "openEMS: the energy end criterion" : "the time-step ceiling",
            ["energy_at_stop_db"] = energy.Count > 0 ? -double.Parse(energy[^1].Groups[1].Value, CultureInfo.InvariantCulture) : null,
            ["openems_wall_s"] = time.Success ? double.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture) : null,
        };
    }

    /// <summary>A SAR run's record, its volumes' cell centres and each volume's two planes, from <paramref name="from"/> (a port's run
    /// directory, or brief-sar-9's recombined result) into <paramref name="outDir"/>.</summary>
    public static void CollectVolumes(string from, string outDir)
    {
        Directory.CreateDirectory(outDir);
        File.Copy(Path.Combine(from, Record), Path.Combine(outDir, Record), true);
        var lines = "xyz".Select(a => SarVolumeFiles.ReadFloat64(Path.Combine(from, SarVolumeFiles.LinesFile(0, a)))).ToArray();
        foreach (char a in "xyz") File.Copy(Path.Combine(from, SarVolumeFiles.LinesFile(0, a)), Path.Combine(outDir, SarVolumeFiles.LinesFile(0, a)), true);
        foreach (string q in Quantities)
        {
            var (vol, shape) = SarVolumeFiles.ReadFloat32(Path.Combine(from, SarVolumeFiles.VolumeFile(0, q)));
            foreach (var (name, axis) in Planes)
            {
                var (plane, n0, n1) = Slice(vol, shape, lines[axis], axis);
                SarVolumeFiles.WriteFloat32(Path.Combine(outDir, PlaneFile(q, name)), plane, n0, n1);
            }
        }
    }

    /// <summary>The plane of volume <paramref name="vol"/> (C order over x, y, z) normal to <paramref name="axis"/> at 0, linear
    /// between the two cell layers either side of it; the other two axes in order.</summary>
    private static (float[] Plane, int N0, int N1) Slice(float[] vol, int[] shape, double[] centres, int axis)
    {
        int k0 = Array.FindLastIndex(centres, c => c <= 0);
        Assert.True(k0 >= 0 && k0 + 1 < centres.Length, "the volume does not reach the plane at 0");
        double w = centres[k0] == 0 ? 0 : (0 - centres[k0]) / (centres[k0 + 1] - centres[k0]);
        int nx = shape[0], ny = shape[1], nz = shape[2];
        int n0 = axis == 0 ? ny : nx, n1 = axis == 2 ? ny : nz;
        var plane = new float[n0 * n1];
        float At(int i, int j, int k) => vol[(i * ny + j) * nz + k];
        for (int a = 0; a < n0; a++)
            for (int b = 0; b < n1; b++)
            {
                (int i, int j, int k) lo = axis switch { 0 => (k0, a, b), 1 => (a, k0, b), _ => (a, b, k0) };
                (int i, int j, int k) hi = axis switch { 0 => (k0 + 1, a, b), 1 => (a, k0 + 1, b), _ => (a, b, k0 + 1) };
                plane[a * n1 + b] = (float)((1 - w) * At(lo.i, lo.j, lo.k) + (w == 0 ? 0 : w * At(hi.i, hi.j, hi.k)));
            }
        return (plane, n0, n1);
    }

    // ── the comparison ────────────────────────────────────────────────────────────────────────────

    /// <summary>A probe file, cut after <paramref name="untilS"/> when one is given.</summary>
    public static FdtdProbe Probe(string path, double untilS = double.PositiveInfinity)
    {
        var p = OpenEmsRun.ReadProbe(path, out string? error) ?? throw new InvalidDataException(error);
        int n = Array.FindLastIndex(p.TimeS, t => t <= untilS) + 1;
        return n == p.TimeS.Length ? p : new FdtdProbe(p.TimeS[..n], p.Value[..n]);
    }

    /// <summary>S11 = (U − Z0·I)/(U + Z0·I) from a lumped port's time-domain probes, as the tutorial forms it.</summary>
    public static Complex[] S11(string voltage, string current, double[] hz, double untilS = double.PositiveInfinity)
    {
        var u = FdtdPortTransform.Dft(Probe(voltage, untilS), hz);
        var i = FdtdPortTransform.Dft(Probe(current, untilS), hz);
        return [.. u.Zip(i, (uu, ii) => (uu - Z0 * ii) / (uu + Z0 * ii))];
    }

    /// <summary>Yin = I/U from the same probes.</summary>
    public static Complex[] Yin(string voltage, string current, double[] hz, double untilS = double.PositiveInfinity)
    {
        var u = FdtdPortTransform.Dft(Probe(voltage, untilS), hz);
        var i = FdtdPortTransform.Dft(Probe(current, untilS), hz);
        return [.. u.Zip(i, (uu, ii) => ii / uu)];
    }

    /// <summary>The frequency and depth (dB) of |S11|'s minimum over 250–350 MHz, on a 10 kHz grid: finer than the 0.5 MHz gate,
    /// and than the script's own 1.2 MHz step (sar.md §3.4).</summary>
    public static (double Hz, double Db) S11Minimum(string voltage, string current, double untilS = double.PositiveInfinity)
    {
        double[] hz = [.. Enumerable.Range(0, 10001).Select(k => 250e6 + k * 1e4)];
        var s = S11(voltage, current, hz, untilS);
        int at = Enumerable.Range(0, hz.Length).MinBy(k => s[k].Magnitude);
        return (hz[at], 20 * Math.Log10(s[at].Magnitude));
    }

    /// <summary>A reference plane: its values (|·| of a complex one) over its two in-plane mesh axes.</summary>
    public static (double[] A, double[] B, double[] V) ReferencePlane(string values, string meshA, string meshB)
    {
        var v = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, values));
        return (SarReferenceSetTests.Npy.Read(Path.Combine(Reference, meshA)).Real, SarReferenceSetTests.Npy.Read(Path.Combine(Reference, meshB)).Real,
                v.Values is { } c ? [.. c.Select(z => z.Magnitude)] : v.Real);
    }

    /// <summary>circuitRF's plane, bilinear at (a, b); NaN outside it.</summary>
    public static double At(double[] la, double[] lb, float[] plane, double a, double b)
    {
        int i = Array.FindLastIndex(la, x => x <= a + 1e-9), j = Array.FindLastIndex(lb, x => x <= b + 1e-9);
        if (i < 0 || j < 0 || a > la[^1] + 1e-9 || b > lb[^1] + 1e-9) return double.NaN;
        int i1 = Math.Min(i + 1, la.Length - 1), j1 = Math.Min(j + 1, lb.Length - 1);
        double u = i1 == i ? 0 : (a - la[i]) / (la[i1] - la[i]), w = j1 == j ? 0 : (b - lb[j]) / (lb[j1] - lb[j]);
        u = Math.Clamp(u, 0, 1); w = Math.Clamp(w, 0, 1);
        double P(int x, int y) => plane[x * lb.Length + y];
        return (1 - u) * (1 - w) * P(i, j) + u * (1 - w) * P(i1, j) + (1 - u) * w * P(i, j1) + u * w * P(i1, j1);
    }

    /// <summary>Pearson's correlation of the reference plane with circuitRF's at every reference point circuitRF's plane covers.</summary>
    public static (double R, int Points) Correlation((double[] A, double[] B, double[] V) reference, double[] la, double[] lb, float[] plane)
    {
        var pairs = new List<(double X, double Y)>();
        for (int i = 0; i < reference.A.Length; i++)
            for (int j = 0; j < reference.B.Length; j++)
                if (At(la, lb, plane, reference.A[i], reference.B[j]) is var ours && !double.IsNaN(ours))
                    pairs.Add((reference.V[i * reference.B.Length + j], ours));
        double mx = pairs.Average(p => p.X), my = pairs.Average(p => p.Y);
        double sxy = pairs.Sum(p => (p.X - mx) * (p.Y - my)), sxx = pairs.Sum(p => (p.X - mx) * (p.X - mx)), syy = pairs.Sum(p => (p.Y - my) * (p.Y - my));
        return (sxy / Math.Sqrt(sxx * syy), pairs.Count);
    }

    private static double Rel(double a, double b) => Math.Abs(a - b) / Math.Abs(b);

    /// <summary>
    /// The reference's |B1+| per √W at the phantom centre: at the cell centre, and as the script printed it. The script dumped H
    /// with DumpMode 0, which gives each component where the Yee grid holds it — Hx on the x line half a cell before the cell
    /// centre, Hy on the y line — so its value "at the centre" is H a millimetre towards the coil. openEMS's own cell
    /// interpolation (DumpMode 2) is the mean of the two such samples either side; measured on one run with both dumps, that
    /// rule reproduces mode 2 to 1.4e-5. Hx and Hy are recovered from the kept complex planes (B1± = ½μ0(Hx ± jHy)), so the
    /// centre value is the reference's own field put through openEMS's own rule, on the axial plane (z = 0 is a cell centre
    /// there, and Hx, Hy lie in it).
    /// </summary>
    public static (double AtCentre, double Printed) ReferenceB1AtCentre()
    {
        var p = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "b1p_xy_t_per_sqrtw.npy"));
        var m = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "b1m_xy_t_per_sqrtw.npy"));
        var x = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "b1_xy_mesh_x_m.npy")).Real;
        var y = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "b1_xy_mesh_y_m.npy")).Real;
        int i = Array.IndexOf(x, 0.0), j = Array.IndexOf(y, 0.0), ny = y.Length;
        Assert.True(i >= 0 && j >= 0, "the reference's axial plane holds no sample at x = y = 0");
        Complex Hx(int a, int b) => p.Values![a * ny + b] + m.Values![a * ny + b];                     // μ0·Hx per √W
        Complex Hy(int a, int b) => (p.Values![a * ny + b] - m.Values![a * ny + b]) / Complex.ImaginaryOne;
        var hx = (Hx(i, j) + Hx(i + 1, j)) / 2;
        var hy = (Hy(i, j) + Hy(i, j + 1)) / 2;
        return ((0.5 * (hx + Complex.ImaginaryOne * hy)).Magnitude, p.Values![i * ny + j].Magnitude);
    }

    /// <summary>
    /// Overview §4 for one run's output in <paramref name="dir"/>. With <paramref name="hold"/> (run (a)): the S11 minimum within
    /// 0.5 MHz and 1 dB, |B1+| per √W at the phantom centre within 3 %, peak local SAR per W on each plane within 5 %, and every
    /// B1+ and SAR plane correlated ≥ 0.99 with the reference's. Without it, the same numbers are reported and nothing is held.
    /// Yin across the band and the ring-down are reported either way.
    /// </summary>
    public static void Compare(string dir, bool hold, ITestOutputHelper output)
    {
        var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(Reference, "summary.json"))).RootElement;
        var r = JsonSerializer.Deserialize<SarRunRecord>(File.ReadAllText(Path.Combine(dir, Record)))!;
        var lines = "xyz".Select(a => SarVolumeFiles.ReadFloat64(Path.Combine(dir, SarVolumeFiles.LinesFile(0, a)))).ToArray();
        float[] Plane(string q, string p) => SarVolumeFiles.ReadFloat32(Path.Combine(dir, PlaneFile(q, p))).Values;
        var failures = new List<string>();
        void Hold(bool ok, string what) { output.WriteLine((ok ? "  ok    " : "  MISS  ") + what); if (!ok) failures.Add(what); }

        // S11's minimum, from both runs' probes through the same transform, over the time both ran (sar.md §10.4): the depth of
        // this null is set by where the ring-down is cut — the reference's own probes give −63 dB to their end and −42 dB cut
        // at 225 ns — so depths from runs that stopped at different steps compare the stop steps, not the coils.
        string refU = Path.Combine(Reference, "port_ut_1"), refI = Path.Combine(Reference, "port_it_1");
        string ourU = Path.Combine(dir, Probes[0]), ourI = Path.Combine(dir, Probes[1]);
        double common = Math.Min(Probe(refU).TimeS[^1], Probe(ourU).TimeS[^1]);
        var (fRef, dbRef) = S11Minimum(refU, refI, common);
        var (f, db) = S11Minimum(ourU, ourI, common);
        var (fOwn, dbOwn) = S11Minimum(ourU, ourI);
        var (_, dbRefOwn) = S11Minimum(refU, refI);
        output.WriteLine($"  S11 minimum to each run's own end: {dbOwn:F2} dB at {fOwn / 1e6:F2} MHz, reference {dbRefOwn:F2} dB; " +
                         $"compared over the first {common * 1e9:F1} ns of both");
        Hold(Math.Abs(f - fRef) <= 0.5e6, $"S11 minimum at {f / 1e6:F2} MHz, reference {fRef / 1e6:F2} MHz (0.5 MHz)");
        Hold(Math.Abs(db - dbRef) <= 1, $"S11 minimum {db:F2} dB, reference {dbRef:F2} dB (1 dB)");

        // B1+ per √W at the phantom centre, against the reference's field AT the centre (sar.md §10.3)
        double pacc = r.AcceptedPowerW;
        double b1 = At(lines[0], lines[1], Plane(SarVolumeFiles.B1PlusQuantity, "z0"), 0, 0) / Math.Sqrt(pacc);
        var (b1Ref, b1Printed) = ReferenceB1AtCentre();
        output.WriteLine($"  the tutorial's own |B1+| at the centre, {b1Printed * 1e6:F4} µT/√W, is H half a cell off it (DumpMode 0); at the cell " +
                         $"centre, by openEMS's mode-2 rule, {b1Ref * 1e6:F4}");
        Hold(Rel(b1, b1Ref) <= 0.03, $"|B1+| at the phantom centre {b1 * 1e6:F4} µT/√W, reference {b1Ref * 1e6:F4} ({100 * (b1 / b1Ref - 1):+0.00;-0.00} %, 3 %)");

        // peak local SAR per W on each plane, and every plane's correlation with the reference's
        foreach (var (plane, refName, la, lb, peakKey) in new[]
                 {
                     ("z0", "xy", lines[0], lines[1], "peak_local_sar_xy_w_per_kg_per_w"),
                     ("y0", "xz", lines[0], lines[2], "peak_local_sar_xz_w_per_kg_per_w"),
                 })
        {
            float[] sar = Plane(SarVolumeFiles.LocalQuantity, plane);
            double peak = sar.Max() / pacc, peakRef = summary.GetProperty(peakKey).GetDouble();
            Hold(Rel(peak, peakRef) <= 0.05, $"peak local SAR on the {refName} plane {peak:F3} W/kg/W, reference {peakRef:F3} ({100 * (peak / peakRef - 1):+0.00;-0.00} %, 5 %)");
            string b = refName == "xy" ? "y" : "z";
            var (rs, ns) = Correlation(ReferencePlane($"sar_{refName}.npy", $"sar_{refName}_mesh_x_m.npy", $"sar_{refName}_mesh_{b}_m.npy"), la, lb, sar);
            Hold(rs >= 0.99, $"local SAR on the {refName} plane correlates {rs:F4} with the reference's over {ns} points (0.99)");
            var (rb, nb) = Correlation(ReferencePlane($"b1p_{refName}_t_per_sqrtw.npy", $"b1_{refName}_mesh_x_m.npy", $"b1_{refName}_mesh_{b}_m.npy"),
                                       la, lb, Plane(SarVolumeFiles.B1PlusQuantity, plane));
            Hold(rb >= 0.99, $"|B1+| on the {refName} plane correlates {rb:F4} with the reference's over {nb} points (0.99)");
        }

        // reported, not held: Yin across the band, and the ring-down
        double[] band = [.. Enumerable.Range(0, 201).Select(k => 198e6 + k * 1e6)];
        int atF0 = Array.IndexOf(band, F0);
        foreach (var (what, until) in new[] { ("each to its own end", double.PositiveInfinity), ($"over the first {common * 1e9:F1} ns of both", common) })
        {
            var y = Yin(ourU, ourI, band, until);
            var yRef = Yin(refU, refI, band, until);
            int worst = Enumerable.Range(0, band.Length).MaxBy(k => (y[k] - yRef[k]).Magnitude / yRef[k].Magnitude);
            output.WriteLine($"  Yin {what}: at 298 MHz {y[atF0].Real:F4}{y[atF0].Imaginary:+0.0000;-0.0000}j S, reference {yRef[atF0].Real:F4}" +
                             $"{yRef[atF0].Imaginary:+0.0000;-0.0000}j S; over 198–398 MHz |ΔYin|/|Yin| is largest at {band[worst] / 1e6:F0} MHz, " +
                             $"{100 * (y[worst] - yRef[worst]).Magnitude / yRef[worst].Magnitude:F3} %");
        }
        var facts = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, RunFacts)))!;
        output.WriteLine($"  stopped at step {facts["stopped_at_timestep"]} ({facts["stopped_by"]}, energy {facts["energy_at_stop_db"]} dB); " +
                         "the reference stopped at 77,409 on openEMS's energy criterion (−40.51 dB)");
        output.WriteLine($"  psSAR10g {r.PeakPerWatt("SAR10g"):F4} W/kg/W; whole-phantom {r.WholeSarPerWatt:F4} W/kg/W; mean |B1+| over the brain " +
                         $"{r.B1pMeanPerSqrtWatt * 1e6:F4} µT/√W; sar_calc's 10 g peak {(r.CrossCheck is { Count: > 0 } cc ? cc[0].RelativeDifference : null):E2} from circuitRF's");
        if (hold) Assert.True(failures.Count == 0, "missed overview §4: " + string.Join("; ", failures));
    }
}

/// <summary>Skips unless SAR-1's loop-coil reference and circuitRF's committed replication output are both on this machine.</summary>
public sealed class LoopReplicationFactAttribute : FactAttribute
{
    public LoopReplicationFactAttribute()
    {
        if (!Directory.Exists(LoopReplication.Reference))
            Skip = "needs the SAR-1 reference data, which is not in the repository: testdata/sar/openems-ref/README.md says how to make it";
        else if (LoopReplication.Runs.Any(r => !File.Exists(Path.Combine(LoopReplication.Workspace, r.Folder, LoopReplication.Record))))
            Skip = "needs circuitRF's replication output in testdata/sar/circuitrf/loop: LoopCoilRerunTests with CRF_WRITE_SAR8_OUTPUTS=1 makes it";
    }
}

public sealed class LoopCoilReplicationTests(ITestOutputHelper output)
{
    /// <summary>R-sar8-3 (a): on the reference's own grid lines, every overview §4 quantity at its tolerance.</summary>
    [LoopReplicationFact]
    public void OnTheReferencesGrid_MatchesTheTutorial()
        => LoopReplication.Compare(Path.Combine(LoopReplication.Workspace, "reference-grid"), hold: true, output);
}

/// <summary>Skips unless SAR-1's loop-coil material grid (tools/dump_loop_material.py) and openEMS are both on this machine.</summary>
public sealed class LoopMaterialFactAttribute : FactAttribute
{
    public LoopMaterialFactAttribute()
    {
        if (!File.Exists(Path.Combine(LoopReplication.Reference, "material_raw.h5")))
            Skip = "needs SAR-1's MRI_Loop_Coil/material_raw.h5, which is not in the repository: testdata/sar/openems-ref/README.md says how to make it";
        else if (SolverDiscovery.OpenEms.Find(out _) is null)
            Skip = "needs openEMS";
    }
}

public sealed class LoopPhantomVoxelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar8-voxels-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// R-sar8-1, before any run: the replication's setup, lowered by circuitRF on the reference's lines — its phantom a
    /// C3dPhantom voxelized at 2.5 mm, not the bundled file — and loaded by the installed openEMS for 20 steps, gives every cell
    /// of its raw SAR dump the density and conductivity openEMS gives the tutorial's own structure (material_raw.h5), with no
    /// exception. The smooth phantom differed in 22,362 of 463,363 tissue cells (sar.md §10.2).
    /// </summary>
    [LoopMaterialFact]
    public void TheVoxelizedPhantom_IsTheTutorialsCellForCell()
    {
        string c3d = Path.Combine(LoopReplication.Workspace, LoopReplication.C3d), cws = Path.Combine(LoopReplication.Workspace, ".cws");
        var doc = CircuitRF.Design.ThreeD.C3dPersistence.LoadFromFile(c3d);
        var (embedded, why) = CircuitRF.Design.ThreeD.C3dSetups.Select(doc, "ReferenceGrid");
        Assert.True(embedded is not null, why);
        var setup = CircuitRF.Design.ThreeD.C3dSetups.ForRun(embedded, c3d);
        var g = CircuitRF.Design.ThreeD.C3dProblemAssembly.Assemble(setup, doc, c3d, cws, new CircuitRF.Design.ThreeD.C3dElaborator());
        Assert.True(g.Ok, g.Refusal);
        var (plan, refusal) = Em3dSar.Resolve(CircuitRF.Design.Layout.Em.SarSettings.Resolve(setup.Sar!), g.Problem!, g.Phantoms);
        Assert.True(plan is not null, refusal);
        var gs = CircuitRF.Design.Layout.Em.CemOpenEms.ResolveGrid(setup.OpenEms, c3d);
        var grid = FdtdGrid.Build(g.Problem!, gs, long.MaxValue);
        Assert.Null(grid.Refusal);
        var low = CsxcadWriter.Write(g.Problem!, grid, gs, CircuitRF.Design.Layout.Em.CemOpenEms.ResolveRun(setup.OpenEms, true) with { Sar = plan });
        Assert.True(low.Ok, low.Refusal);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "model.xml"), Regex.Replace(low.Model!, "NumberOfTimesteps=\"\\d+\"", "NumberOfTimesteps=\"20\""));
        foreach (var f in low.KernelFiles ?? []) f.WriteInto(_root);
        Assert.Contains(CsxcadWriter.VoxelFile("head"), (low.KernelFiles ?? []).Select(f => f.FileName));

        var psi = new ProcessStartInfo(SolverDiscovery.OpenEms.Find(out _)!.Path)
        {
            WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add("model.xml");
        using (var proc = Process.Start(psi)!)
        {
            var err = proc.StandardError.ReadToEndAsync();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, err.Result);
        }

        var ours = RfCore.Hdf5.OpenEmsHdf5.ReadSarRaw(Path.Combine(_root, Em3dSar.RawDumpFile(0)));
        var theirs = RfCore.Hdf5.OpenEmsHdf5.ReadSarRaw(Path.Combine(LoopReplication.Reference, "material_raw.h5"));
        static int[] Match(double[] a, double[] b) => [.. a.Select(v => Array.FindIndex(b, w => Math.Abs(w - v) < 1e-9))];
        int[] mx = Match(ours.Mesh.X, theirs.Mesh.X), my = Match(ours.Mesh.Y, theirs.Mesh.Y), mz = Match(ours.Mesh.Z, theirs.Mesh.Z);
        Assert.DoesNotContain(-1, mx); Assert.DoesNotContain(-1, my); Assert.DoesNotContain(-1, mz);   // circuitRF's box inside theirs
        int tissue = 0, differ = 0;
        for (int i = 0; i < ours.Mesh.Nx; i++)
            for (int j = 0; j < ours.Mesh.Ny; j++)
                for (int k = 0; k < ours.Mesh.Nz; k++)
                {
                    int a = RfCore.Hdf5.OpenEmsArrayOrder.Index(i, j, k, ours.Mesh.Ny, ours.Mesh.Nz);
                    int b = RfCore.Hdf5.OpenEmsArrayOrder.Index(mx[i], my[j], mz[k], theirs.Mesh.Ny, theirs.Mesh.Nz);
                    if (ours.Density[a] > 0 || theirs.Density[b] > 0) tissue++;
                    if (ours.Density[a] != theirs.Density[b] || ours.Conductivity[a] != theirs.Conductivity[b]) differ++;
                }
        Assert.Equal(0, differ);
        // and nothing of the tutorial's tissue lies outside circuitRF's dump
        int all = theirs.Density.Count(d => d > 0);
        Assert.Equal(all, tissue);
    }
}

public sealed class LoopCoilRerunTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar8-rerun-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// R-sar8-2's opt-in rerun, for whoever changes the writer: the replication workspace copied out, both setups run through the
    /// CLI as a process, run (a) held to the routine test's tolerances and run (b) reported. CRF_WRITE_SAR8_OUTPUTS=1 also writes
    /// the output into the repository.
    /// </summary>
    [SarReferenceFact]
    [Trait("Category", "Benchmark")]
    public void BothRuns_ThroughTheCli_StillMatch()
    {
        Assert.True(SolverDiscovery.ReadinessFor(Em3dSolverId.OpenEms).All(r => r.Proceeds), "needs openEMS");
        string ws = Path.Combine(_root, "loop");
        foreach (string f in new[] { ".cws", "tech.ctech", "Loop/.ccell", LoopReplication.C3d })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(ws, f))!);
            File.Copy(Path.Combine(LoopReplication.Workspace, f), Path.Combine(ws, f));
        }
        // the copy reads the reference's grid file where it is, not beside itself
        string c3d = Path.Combine(ws, LoopReplication.C3d);
        File.WriteAllText(c3d, File.ReadAllText(c3d).Replace("../../../../openems-ref/MRI_Loop_Coil/MRI_Loop_Coil.xml",
            Path.Combine(LoopReplication.Reference, "MRI_Loop_Coil.xml").Replace("\\", "/")));

        bool write = Environment.GetEnvironmentVariable("CRF_WRITE_SAR8_OUTPUTS") == "1";
        foreach (var (setup, folder) in LoopReplication.Runs)
        {
            var (exit, stdout, stderr) = Cli("em", c3d, "--setup", setup);
            Assert.True(exit == 0, stderr + stdout);
            string outDir = Path.Combine(_root, folder);
            LoopReplication.Collect(Path.Combine(ws, "results"), setup, outDir);
            output.WriteLine($"── {setup}");
            LoopReplication.Compare(outDir, hold: folder == "reference-grid", output);
            if (write) LoopReplication.Collect(Path.Combine(ws, "results"), setup, Path.Combine(LoopReplication.Workspace, folder));
        }
    }

    internal static (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        string cliDir = typeof(LoopCoilRerunTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
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
