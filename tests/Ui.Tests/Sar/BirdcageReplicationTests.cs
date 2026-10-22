// ================================================================
//  BirdcageReplicationTests.cs — brief-sar-8b: openEMS's MRI_LP_Birdcage tutorial rebuilt from .c3d objects on circuitRF's
//  cylindrical grid (testdata/sar/circuitrf/birdcage), run through circuitRF, against SAR-1's reference
//  (testdata/sar/openems-ref/MRI_LP_Birdcage). docs/design/sar.md §10a.
//
//    BirdcageLoweringTests      routine, needs the reference XML only: the setup lowered on the reference's own lines writes the
//                               tutorial's rungs, rings, capacitors and ports box for box, its MultiGrid and its boundaries.
//    BirdcagePhantomVoxelTests  routine, needs openEMS and SAR-1's material_raw.h5: the voxelized phantom, loaded by openEMS on
//                               the cylindrical grid for 20 steps, is the tutorial's own material grid cell for cell.
//    BirdcageReplicationTests   routine, fixed data: circuitRF's committed output (reference-grid/) against the reference — the
//                               quadrature feed as two single-port runs recombined with weights 1 and −j (R-sar8b-5) — in the loop's
//                               form (R-sar8b-6). No openEMS process. Skipped where the reference set is absent (never in the repo).
//    BirdcageRerunTests         Benchmark: the run and the recombination again through the CLI, held to the same; with
//                               CRF_WRITE_SAR8B_OUTPUTS=1 it rewrites the committed output.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Sar;

/// <summary>Where the birdcage replication lives, and the lowering of its setup.</summary>
internal static class BirdcageReplication
{
    public static readonly string Workspace = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "circuitrf", "birdcage");
    public static readonly string Reference = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "openems-ref", "MRI_LP_Birdcage");
    public const string C3d = "Birdcage/3d/Birdcage.c3d";
    public const string Setup = "ReferenceGrid";

    /// <summary>The setup lowered as a run lowers it — problem, SAR plan, grid on the reference's lines, and the CSXCAD model.</summary>
    public static (Em3dProblem Problem, FdtdGridResult Grid, CsxcadLowering Lowering) Lower()
    {
        string c3d = Path.Combine(Workspace, C3d), cws = Path.Combine(Workspace, ".cws");
        var doc = C3dPersistence.LoadFromFile(c3d);
        var (embedded, why) = C3dSetups.Select(doc, Setup);
        Assert.True(embedded is not null, why);
        var setup = C3dSetups.ForRun(embedded, c3d);
        var g = C3dProblemAssembly.Assemble(setup, doc, c3d, cws, new C3dElaborator());
        Assert.True(g.Ok, g.Refusal);
        var (settings, cylRefusal) = Em3dSar.OnCylindricalGrid(SarSettings.Resolve(setup.Sar!), setup.Sar!);
        Assert.True(settings is not null, cylRefusal);
        var (plan, refusal) = Em3dSar.Resolve(settings, g.Problem!, g.Phantoms);
        Assert.True(plan is not null, refusal);
        var gs = CemOpenEms.ResolveGrid(setup.OpenEms, c3d);
        var grid = FdtdGrid.Build(g.Problem!, gs, long.MaxValue);
        Assert.Null(grid.Refusal);
        var low = CsxcadWriter.Write(g.Problem!, grid, gs, CemOpenEms.ResolveRun(setup.OpenEms, true) with { Sar = plan });
        Assert.True(low.Ok, low.Refusal);
        return (g.Problem!, grid, low);
    }

    /// <summary>The Larmor frequency (Hz), the feeds' resistance (Ω), and the tutorial's delay of port 2's pulse (a quarter period at
    /// 128 MHz: −j there).</summary>
    public const double F0 = 128e6, Z0 = 50, Delay = 1.953125e-9;

    /// <summary>What a run of the setup is called under results/, and where its output is kept.</summary>
    public const string Stem = "Birdcage ReferenceGrid.openems", Output = "reference-grid";
    public const string Cosim = "cosim/BirdcageQuadrature.cnl", Analysis = "F1";

    /// <summary>The volumes kept, each as the plane of cells nearest z = 0 — the reference's plane (z = 1.25 mm).</summary>
    public static readonly string[] Quantities = [SarVolumeFiles.LocalQuantity, SarVolumeFiles.B1PlusQuantity, SarVolumeFiles.B1MinusQuantity];
    public static string PlaneFile(string quantity) => $"sar1.{quantity}.z0.npy";
    public static string ProbeFile(int run, int port, char what) => $"p{run}.port{port}_{what}";

    /// <summary>
    /// Copies what a run of the setup and the recombination of its fields left under <paramref name="resultsRoot"/> into
    /// <paramref name="outDir"/>, labelled as circuitRF output: the Touchstone, both ports' probes from both runs, each run's facts
    /// from openEMS's log, the field set's manifest, and the recombination's records and its three volumes' plane at z = 1.25 mm with
    /// the volumes' cell centres (ρ, α, z). The volumes themselves stay in results/.
    /// </summary>
    public static void Collect(string resultsRoot, string outDir)
    {
        string stem = Path.Combine(resultsRoot, Stem);
        Directory.CreateDirectory(outDir);
        File.Copy(stem + ".s2p", Path.Combine(outDir, "Birdcage.s2p"), true);
        for (int run = 1; run <= 2; run++)
        {
            string dir = Path.Combine(stem, $"p{run}");
            for (int port = 1; port <= 2; port++)
                foreach (char w in "ui") File.Copy(Path.Combine(dir, $"port{port}_{w}"), Path.Combine(outDir, ProbeFile(run, port, w)), true);
            var facts = LoopReplication.Facts(Path.Combine(dir, "openems.log"),
                $"circuitRF OUTPUT, not a reference: port {run} of Birdcage.c3d's setup ReferenceGrid driven alone, the other terminated in " +
                "50 Ohm, run by circuitRF on openEMS (brief-sar-8b). The reference is testdata/sar/openems-ref/MRI_LP_Birdcage.", Setup);
            File.WriteAllText(Path.Combine(outDir, $"run{run}.json"), facts.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        File.Copy(Path.Combine(stem + ".fields", PortFieldSet.ManifestFile), Path.Combine(outDir, PortFieldSet.ManifestFile), true);
        string cosim = Path.Combine(stem + ".fields", FieldCosimulation.CosimFolder, Analysis);
        foreach (string f in new[] { SarVolumeFiles.RecordFile(0), FieldCosimulation.RecordFile })
            File.Copy(Path.Combine(cosim, f), Path.Combine(outDir, f), true);
        var z = SarVolumeFiles.ReadFloat64(Path.Combine(cosim, SarVolumeFiles.LinesFile(0, 'z')));
        int k = Enumerable.Range(0, z.Length).MinBy(i => Math.Abs(z[i] - 1.25e-3));
        foreach (char a in "xyz") File.Copy(Path.Combine(cosim, SarVolumeFiles.LinesFile(0, a)), Path.Combine(outDir, SarVolumeFiles.LinesFile(0, a)), true);
        foreach (string q in Quantities)
        {
            var (vol, shape) = SarVolumeFiles.ReadFloat32(Path.Combine(cosim, SarVolumeFiles.VolumeFile(0, q)));
            var plane = new float[shape[0] * shape[1]];
            for (int i = 0; i < shape[0]; i++)
                for (int j = 0; j < shape[1]; j++) plane[i * shape[1] + j] = vol[(i * shape[1] + j) * shape[2] + k];
            SarVolumeFiles.WriteFloat32(Path.Combine(outDir, PlaneFile(q)), plane, shape[0], shape[1]);
        }
    }

    // ── the comparison ────────────────────────────────────────────────────────────────────────────

    /// <summary>A port's U and I at <paramref name="hz"/> from its probe files, cut at <paramref name="untilS"/>.</summary>
    private static (Complex[] U, Complex[] I) Ui(string u, string i, double[] hz, double untilS)
        => (FdtdPortTransform.Dft(LoopReplication.Probe(u, untilS), hz), FdtdPortTransform.Dft(LoopReplication.Probe(i, untilS), hz));

    /// <summary>Port <paramref name="port"/>'s U and I with both ports driven, from circuitRF's two single-port runs: run 1 plus run 2
    /// delayed by the tutorial's quarter period, e^(−j2πfT) — at 128 MHz exactly −j (R-sar8b-5).</summary>
    public static (Complex[] U, Complex[] I) Combined(string dir, int port, double[] hz, double untilS)
    {
        var (u1, i1) = Ui(Path.Combine(dir, ProbeFile(1, port, 'u')), Path.Combine(dir, ProbeFile(1, port, 'i')), hz, untilS);
        var (u2, i2) = Ui(Path.Combine(dir, ProbeFile(2, port, 'u')), Path.Combine(dir, ProbeFile(2, port, 'i')), hz, untilS);
        var w = hz.Select(f => Complex.FromPolarCoordinates(1, -2 * Math.PI * f * Delay)).ToArray();
        return ([.. hz.Select((_, k) => u1[k] + w[k] * u2[k])], [.. hz.Select((_, k) => i1[k] + w[k] * i2[k])]);
    }

    /// <summary>The frequency and depth (dB) of |S11|'s minimum over 110–145 MHz on a 10 kHz grid, S11 = (U − Z0·I)/(U + Z0·I) with
    /// both ports driven, as the tutorial forms it.</summary>
    private static (double Hz, double Db) S11Minimum(Func<double[], (Complex[] U, Complex[] I)> port1)
    {
        double[] hz = [.. Enumerable.Range(0, 3501).Select(k => 110e6 + k * 1e4)];
        var (u, i) = port1(hz);
        var s = u.Zip(i, (uu, ii) => (uu - Z0 * ii) / (uu + Z0 * ii)).ToArray();
        int at = Enumerable.Range(0, hz.Length).MinBy(k => s[k].Magnitude);
        return (hz[at], 20 * Math.Log10(s[at].Magnitude));
    }

    /// <summary>The power both ports accept, Σ 2·Re(U·I*) in circuitRF's DFT (SarPortDrive's header), and what both have available,
    /// Σ |U + Z0·I|²/(2·Z0), at one frequency.</summary>
    private static (double Accepted, double Available) Powers(IEnumerable<(Complex U, Complex I)> ports)
        => (ports.Sum(p => 2 * (p.U * Complex.Conjugate(p.I)).Real), ports.Sum(p => (p.U + Z0 * p.I).Magnitude * (p.U + Z0 * p.I).Magnitude / (2 * Z0)));

    private static double[] RefMesh(string name) => SarReferenceSetTests.Npy.Read(Path.Combine(Reference, name)).Real;

    /// <summary>
    /// R-sar8b-6 for the output in <paramref name="dir"/>. With <paramref name="hold"/>: the S11 minimum over the time both runs ran
    /// within 0.5 MHz and 1 dB; mean |B1+| at the smallest dumped radius per √W accepted at both ports within 3 %; the B1+ and local
    /// SAR planes correlated ≥ 0.99; peak local SAR on the plane per W within 5 %; B1+ homogeneity — the volume-weighted coefficient
    /// of variation of |B1+| over the phantom's central slice — within 5 %. The reference is normalised by its own accepted power at
    /// 128 MHz exactly, from its probes, where the converter's derived values took the frequency point nearest it (128.25 MHz).
    /// </summary>
    public static void Compare(string dir, bool hold, ITestOutputHelper output)
    {
        var r = JsonSerializer.Deserialize<SarRunRecord>(File.ReadAllText(Path.Combine(dir, SarVolumeFiles.RecordFile(0))))!;
        var cosim = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, FieldCosimulation.RecordFile)))!;
        float[] Plane(string q) => SarVolumeFiles.ReadFloat32(Path.Combine(dir, PlaneFile(q))).Values;
        var rho = SarVolumeFiles.ReadFloat64(Path.Combine(dir, SarVolumeFiles.LinesFile(0, 'x')));
        var alpha = SarVolumeFiles.ReadFloat64(Path.Combine(dir, SarVolumeFiles.LinesFile(0, 'y')));
        int na = alpha.Length;
        var failures = new List<string>();
        void Hold(bool ok, string what) { output.WriteLine((ok ? "  ok    " : "  MISS  ") + what); if (!ok) failures.Add(what); }
        Assert.True(r.IsCylindrical, "the recombined record is not a cylindrical grid's");

        // the reference's accepted power at 128 MHz, both ports, from its own probes through circuitRF's transform
        string RefProbe(string n) => Path.Combine(Reference, n);
        double end = new[] { RefProbe("port_ut1"), Path.Combine(dir, ProbeFile(1, 1, 'u')), Path.Combine(dir, ProbeFile(2, 1, 'u')) }
                     .Min(f => LoopReplication.Probe(f).TimeS[^1]);
        var refPorts = Enumerable.Range(1, 2).Select(k => Ui(RefProbe($"port_ut{k}"), RefProbe($"port_it{k}"), [F0], double.PositiveInfinity))
                                 .Select(x => (x.U[0], x.I[0])).ToList();
        double pRef = Powers(refPorts).Accepted;
        var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(Reference, "summary.json"))).RootElement;
        output.WriteLine($"  reference accepted power at 128 MHz, both ports: {pRef:E4} W as solved (at the 128.25 MHz point the converter " +
                         $"used, {summary.GetProperty("accepted_power_both_ports_at_nearest_f0_w").GetDouble():E4})");

        // circuitRF's: the fields analysis's own, against the two runs' probes recombined
        var ours = Enumerable.Range(1, 2).Select(k => Combined(dir, k, [F0], double.PositiveInfinity)).Select(x => (x.U[0], x.I[0])).ToList();
        var (acc, avail) = Powers(ours);
        double fraction = cosim["AcceptedPowerW"]!.GetValue<double>() / cosim["AvailablePowerW"]!.GetValue<double>();
        Hold(Math.Abs(acc / avail - fraction) <= 1e-3, $"the recombination accepts {100 * fraction:F3} % of its available power, the two runs' " +
                                                      $"probes recombined {100 * acc / avail:F3} % (0.1 pp)");

        // S11 with both ports driven, over the time both ran (sar.md §10.4)
        var (fRef, dbRef) = S11Minimum(hz => Ui(RefProbe("port_ut1"), RefProbe("port_it1"), hz, end));
        var (f, db) = S11Minimum(hz => Combined(dir, 1, hz, end));
        var (fRefOwn, dbRefOwn) = S11Minimum(hz => Ui(RefProbe("port_ut1"), RefProbe("port_it1"), hz, double.PositiveInfinity));
        var (fOwn, dbOwn) = S11Minimum(hz => Combined(dir, 1, hz, double.PositiveInfinity));
        output.WriteLine($"  S11 (both driven) minimum to each run's own end: {dbOwn:F2} dB at {fOwn / 1e6:F2} MHz, reference {dbRefOwn:F2} dB at " +
                         $"{fRefOwn / 1e6:F2} MHz; compared over the first {end * 1e6:F3} µs of both");
        Hold(Math.Abs(f - fRef) <= 0.5e6, $"S11 minimum at {f / 1e6:F2} MHz, reference {fRef / 1e6:F2} MHz (0.5 MHz)");
        Hold(Math.Abs(db - dbRef) <= 1, $"S11 minimum {db:F2} dB, reference {dbRef:F2} dB (1 dB)");

        // B1+ at the smallest dumped radius, per √W accepted at both ports: the same cells (both dumps are cell-centred, DumpMode 2)
        var b1Ref = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "b1p_z0_t.npy")).Values!;
        var refRho = RefMesh("b1_z0_mesh_1.npy");
        int nr = refRho.Length, nra = RefMesh("b1_z0_mesh_2.npy").Length;         // the reference's α carries the closing line twice
        Assert.Equal(na + 1, nra);
        Assert.True(Math.Abs(rho[0] - refRho[0]) < 1e-9 && Math.Abs(alpha[0] - RefMesh("b1_z0_mesh_2.npy")[0]) < 1e-9, "the planes' cells differ");
        float[] b1p = Plane(SarVolumeFiles.B1PlusQuantity), b1m = Plane(SarVolumeFiles.B1MinusQuantity), sar = Plane(SarVolumeFiles.LocalQuantity);
        double perSqrtW = 1 / Math.Sqrt(r.PerWattDivisor), perSqrtWRef = 1 / Math.Sqrt(pRef);
        double centre = Enumerable.Range(0, na).Average(j => b1p[j]) * perSqrtW;
        double centreRef = Enumerable.Range(0, na).Average(j => b1Ref[j].Magnitude) * perSqrtWRef;
        Hold(Rel(centre, centreRef) <= 0.03, $"mean |B1+| at ρ = {rho[0] * 1e3:F3} mm {centre * 1e6:F4} µT/√W, reference {centreRef * 1e6:F4} " +
                                             $"({100 * (centre / centreRef - 1):+0.00;-0.00} %, 3 %)");

        // the planes: |B1+| over ρ ≤ 100 mm, local SAR over the reference's 49 radii; peak local SAR per W
        var sarRef = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "sar_z0.npy")).Real;
        int ns = RefMesh("sar_z0_mesh_1.npy").Length;
        var (rb, _) = Correlate(nr, na, (i, j) => b1Ref[i * nra + j].Magnitude, (i, j) => b1p[i * na + j]);
        Hold(rb >= 0.99, $"|B1+| on the z = 1.25 mm plane (ρ ≤ 100 mm) correlates {rb:F5} with the reference's over {nr * na} cells (0.99)");
        var (rs, _) = Correlate(ns, na, (i, j) => sarRef[i * nra + j], (i, j) => sar[i * na + j]);
        Hold(rs >= 0.99, $"local SAR on the plane correlates {rs:F5} with the reference's over {ns * na} cells (0.99)");
        double peak = Enumerable.Range(0, ns * na).Max(c => (double)sar[c]) / r.PerWattDivisor;
        double peakRef = Enumerable.Range(0, ns).SelectMany(i => Enumerable.Range(0, na).Select(j => sarRef[i * nra + j])).Max() / pRef;
        Hold(Rel(peak, peakRef) <= 0.05, $"peak local SAR on the plane {peak:F4} W/kg/W, reference {peakRef:F4} ({100 * (peak / peakRef - 1):+0.00;-0.00} %, 5 %)");

        // B1+ homogeneity over the phantom's central slice: |B1+|'s volume-weighted coefficient of variation over ρ ≤ 100 mm, the cell
        // weights ρ·Δρ from the reference's own ρ lines (Δα and Δz are one value over the plane)
        var lines = XDocument.Load(Path.Combine(Reference, "BirdCage.xml")).Descendants("XLines").Single().Value.Split(',')
                             .Select(v => double.Parse(v, CultureInfo.InvariantCulture) * 1e-3).ToArray();
        double CoV(Func<int, int, double> b)
        {
            double sw = 0, s1 = 0, s2 = 0;
            for (int i = 0; i < nr; i++)
                for (int j = 0; j < na; j++)
                {
                    double w = refRho[i] * (lines[i + 1] - lines[i]), v = b(i, j);
                    sw += w; s1 += w * v; s2 += w * v * v;
                }
            double m = s1 / sw;
            return Math.Sqrt(Math.Max(0, s2 / sw - m * m)) / m;
        }
        double cov = CoV((i, j) => b1p[i * na + j]), covRef = CoV((i, j) => b1Ref[i * nra + j].Magnitude);
        Hold(Rel(cov, covRef) <= 0.05, $"B1+ homogeneity over the central slice: coefficient of variation {cov:F4}, reference {covRef:F4} " +
                                       $"({100 * (cov / covRef - 1):+0.00;-0.00} %, 5 %)");

        // reported: B1+ over B1− on the plane, what the recombination says of itself, and the ring-down
        var b1mRef = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "b1m_z0_t.npy")).Values!;
        double Ratio(Func<int, double> p, Func<int, double> m) => Enumerable.Range(0, nr * na).Average(p) / Enumerable.Range(0, nr * na).Average(m);
        output.WriteLine($"  mean |B1+| / mean |B1−| over the plane {Ratio(c => b1p[c], c => b1m[c]):F3}, reference " +
                         $"{Ratio(c => b1Ref[c / na * nra + c % na].Magnitude, c => b1mRef[c / na * nra + c % na].Magnitude):F3}");
        output.WriteLine($"  the recombination: absorbed {100 * r.AbsorbedFraction:F1} % of accepted, whole-phantom SAR {r.WholeSarPerWatt:F4} W/kg/W over " +
                         $"{r.TissueMassKg:F3} kg, mean |B1+| over the tissue core {r.B1pMeanPerSqrtWatt * 1e6:F4} µT/√W");
        for (int run = 1; run <= 2; run++)
        {
            var facts = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, $"run{run}.json")))!;
            output.WriteLine($"  run {run} (port {run} driven) stopped at step {facts["stopped_at_timestep"]} ({facts["stopped_by"]}, energy " +
                             $"{facts["energy_at_stop_db"]} dB, openEMS {facts["openems_wall_s"]} s); the reference stopped at 225,918 (−40 dB)");
        }
        if (hold) Assert.True(failures.Count == 0, "missed R-sar8b-6: " + string.Join("; ", failures));
    }

    /// <summary>Pearson's correlation of two planes over the same (ρ, α) cells.</summary>
    private static (double R, int N) Correlate(int nr, int na, Func<int, int, double> a, Func<int, int, double> b)
    {
        double ma = 0, mb = 0;
        int n = nr * na;
        for (int i = 0; i < nr; i++) for (int j = 0; j < na; j++) { ma += a(i, j); mb += b(i, j); }
        ma /= n; mb /= n;
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < nr; i++)
            for (int j = 0; j < na; j++)
            {
                double x = a(i, j) - ma, y = b(i, j) - mb;
                sab += x * y; saa += x * x; sbb += y * y;
            }
        return (sab / Math.Sqrt(saa * sbb), n);
    }

    private static double Rel(double a, double b) => Math.Abs(a - b) / Math.Abs(b);

    /// <summary>A property's boxes in a CSXCAD document: each (P1, P2) as (lo, hi) per coordinate.</summary>
    public static List<(double[] Lo, double[] Hi)> Boxes(XElement property)
        => [.. property.Descendants("Box").Select(b =>
        {
            double[] P(string tag) => [.. "XYZ".Select(c => double.Parse((string)b.Element(tag)!.Attribute(c.ToString())!, CultureInfo.InvariantCulture))];
            double[] p1 = P("P1"), p2 = P("P2");
            return ((double[])[.. p1.Zip(p2, Math.Min)], (double[])[.. p1.Zip(p2, Math.Max)]);
        })];
}

public sealed class BirdcageReferenceFactAttribute : FactAttribute
{
    public BirdcageReferenceFactAttribute()
    {
        if (!File.Exists(Path.Combine(BirdcageReplication.Reference, "BirdCage.xml")))
            Skip = "needs SAR-1's MRI_LP_Birdcage reference, which is not in the repository: testdata/sar/openems-ref/README.md says how to make it";
    }
}

public sealed class BirdcageLoweringTests
{
    /// <summary>
    /// R-sar8b-1/-2/-3, before any run: on the reference's own 65 × 305 × 219 lines, circuitRF writes every rung, ring, capacitor and
    /// port as the tutorial's own box in (ρ, α, z) — the reference's coordinates are mm, circuitRF's metres, α radians in both — with
    /// its MultiGrid radii, PEC at ρmax, an 8-cell PML on the two ends (the tutorial's PML_8 in its 10 added lines) and the
    /// cell-constant material.
    /// </summary>
    [BirdcageReferenceFact]
    public void TheSetup_OnTheReferencesLines_WritesTheTutorialsStructure()
    {
        var (_, grid, low) = BirdcageReplication.Lower();
        var ours = XDocument.Parse(low.PortFiles[0]);
        var theirs = XDocument.Load(Path.Combine(BirdcageReplication.Reference, "BirdCage.xml"));

        // the grid: verbatim, ρ and z from mm to m
        var refGrid = theirs.Descendants("RectilinearGrid").Single();
        double[] Lines(XDocument d, string tag, double scale)
            => [.. d.Descendants(tag).Single().Value.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture) * scale)];
        Assert.Equal(Lines(theirs, "XLines", 1e-3), Lines(ours, "XLines", 1), new Tol(1e-12));
        Assert.Equal(Lines(theirs, "YLines", 1), Lines(ours, "YLines", 1), new Tol(1e-12));
        Assert.Equal(Lines(theirs, "ZLines", 1e-3), Lines(ours, "ZLines", 1), new Tol(1e-12));

        var fdtd = ours.Descendants("FDTD").Single();
        Assert.Equal("1", (string?)fdtd.Attribute("CylinderCoords"));
        Assert.Equal("0.01,0.02", (string?)fdtd.Attribute("MultiGrid"));
        Assert.Equal("1", (string?)fdtd.Attribute("CellConstantMaterial"));
        var bc = fdtd.Element("BoundaryCond")!;
        Assert.Equal("PEC", (string?)bc.Attribute("xmax"));
        Assert.Equal(["PML_8", "PML_8"], new[] { (string?)bc.Attribute("zmin"), (string?)bc.Attribute("zmax") });

        // every metal box: the tutorial's, ρ and z in metres
        static (double[] Lo, double[] Hi) Mm((double[] Lo, double[] Hi) b) => ([b.Lo[0] * 1e-3, b.Lo[1], b.Lo[2] * 1e-3], [b.Hi[0] * 1e-3, b.Hi[1], b.Hi[2] * 1e-3]);
        var refMetal = BirdcageReplication.Boxes(theirs.Descendants("Metal").Single()).Select(Mm).ToList();
        var ourMetal = ours.Descendants("Metal").SelectMany(BirdcageReplication.Boxes).ToList();
        Assert.Equal(refMetal.Count, ourMetal.Count);
        foreach (var r in refMetal)
            Assert.True(ourMetal.Any(o => Same(o, r, 1e-9)), $"no box of circuitRF's metal is the tutorial's ({string.Join(", ", r.Lo)}) – ({string.Join(", ", r.Hi)})");
        // and each edge is a grid line's own value (§10a.6): openEMS's box test has no tolerance, and the 1e-9 above hid rung ends
        // 2e-18 m short of their line and azimuths ulps past the lines' fifteen digits, each losing a line of edges
        foreach (var (lo, hi) in ourMetal)
            foreach (var (lines, v) in new[] { (grid.X.Lines, lo[0]), (grid.X.Lines, hi[0]), (grid.Y.Lines, lo[1]), (grid.Y.Lines, hi[1]), (grid.Z.Lines, lo[2]), (grid.Z.Lines, hi[2]) })
                Assert.True(lines.Contains(v), $"a metal edge at {v:R} is not a grid line's own value");

        // the capacitors: one 2.6 pF lumped element per rung, directed along z, on the tutorial's boxes (their α edges on the grid's lines)
        var refCaps = BirdcageReplication.Boxes(theirs.Descendants("LumpedElement").Single(e => (string?)e.Attribute("Name") == "caps")).Select(Mm).ToList();
        var ourCaps = ours.Descendants("LumpedElement").Where(e => ((string?)e.Attribute("Name"))!.StartsWith("cap", StringComparison.Ordinal)).ToList();
        Assert.Equal(8, ourCaps.Count);
        foreach (var c in ourCaps)
        {
            Assert.Equal("2", (string?)c.Attribute("Direction"));
            Assert.Equal(2.6e-12, double.Parse((string)c.Attribute("C")!, CultureInfo.InvariantCulture), 1e-24);
            var box = BirdcageReplication.Boxes(c).Single();
            Assert.True(refCaps.Any(r => Same(box, r, 1e-3 * Cell(grid.Y.Lines, box.Lo[1]))), $"capacitor '{(string?)c.Attribute("Name")}' is not on a tutorial capacitor's box");
        }

        // the two ports: the tutorial's 50 Ω resistor boxes, the voltage probe along the gap and the current probe across it
        for (int k = 1; k <= 2; k++)
        {
            var rr = BirdcageReplication.Boxes(theirs.Descendants("LumpedElement").Single(e => (string?)e.Attribute("Name") == $"port_resist_{k}")).Select(Mm).Single();
            var or = ours.Descendants("LumpedElement").Single(e => (string?)e.Attribute("Name") == $"port{k}_resist");
            Assert.Equal("2", (string?)or.Attribute("Direction"));
            Assert.Equal("50", (string?)or.Attribute("R"));
            Assert.True(Same(BirdcageReplication.Boxes(or).Single(), rr, 1e-3 * Cell(grid.Y.Lines, rr.Lo[1])), $"port {k}'s resistor is not the tutorial's");
            Assert.Contains(ours.Descendants("ProbeBox"), p => (string?)p.Attribute("Name") == $"port{k}_u" && (string?)p.Attribute("Type") == "0");
            Assert.Contains(ours.Descendants("ProbeBox"), p => (string?)p.Attribute("Name") == $"port{k}_i" && (string?)p.Attribute("NormDir") == "2");
        }
        // port 1's own file excites port 1 along z, on its box
        var ex = ours.Descendants("Excitation").Single(e => (string?)e.Attribute("Name") == "port1_excite");
        Assert.Matches("^-?0,-?0,-?1$", (string?)ex.Attribute("Excite"));
    }

    private static bool Same((double[] Lo, double[] Hi) a, (double[] Lo, double[] Hi) b, double tol)
        => Enumerable.Range(0, 3).All(i => Math.Abs(a.Lo[i] - b.Lo[i]) <= tol + 1e-12 * Math.Abs(b.Lo[i]) &&
                                           Math.Abs(a.Hi[i] - b.Hi[i]) <= tol + 1e-12 * Math.Abs(b.Hi[i]));

    private static double Cell(IReadOnlyList<double> lines, double at)
    {
        int i = Math.Max(1, lines.Select((v, k) => (v, k)).MinBy(t => Math.Abs(t.v - at)).k);
        return lines[i] - lines[i - 1];
    }

    private sealed class Tol(double eps) : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) <= eps * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        public int GetHashCode(double v) => 0;
    }
}

public sealed class BirdcageMaterialFactAttribute : FactAttribute
{
    public BirdcageMaterialFactAttribute()
    {
        if (!File.Exists(Path.Combine(BirdcageReplication.Reference, "material_raw.h5")))
            Skip = "needs SAR-1's MRI_LP_Birdcage/material_raw.h5, which is not in the repository: testdata/sar/openems-ref/README.md says how to make it";
        else if (SolverDiscovery.OpenEms.Find(out _) is null)
            Skip = "needs openEMS";
    }
}

public sealed class BirdcagePhantomVoxelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar8b-voxels-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// Before any run: circuitRF's own C3dPhantom (a Cylinder3 voxelized at 2.5 mm), lowered on the reference's cylindrical lines and
    /// loaded by the installed openEMS for 20 steps, gives every cell of its raw SAR dump the density and conductivity openEMS gives
    /// the tutorial's own structure with the bundled phantom (material_raw.h5).
    /// </summary>
    [BirdcageMaterialFact]
    public void TheVoxelizedPhantom_IsTheTutorialsCellForCell()
    {
        var (_, _, low) = BirdcageReplication.Lower();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "model.xml"), Regex.Replace(low.Model!, "NumberOfTimesteps=\"\\d+\"", "NumberOfTimesteps=\"20\""));
        foreach (var f in low.KernelFiles ?? []) f.WriteInto(_root);
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
        var theirs = RfCore.Hdf5.OpenEmsHdf5.ReadSarRaw(Path.Combine(BirdcageReplication.Reference, "material_raw.h5"));
        Assert.True(ours.Mesh.Cylindrical && theirs.Mesh.Cylindrical);
        // both dumps' lines are metres (openEMS writes SI whatever the drawing unit), α radians
        static int[] Match(double[] a, double[] b) => [.. a.Select(v => Array.FindIndex(b, w => Math.Abs(w - v) < 1e-9))];
        int[] mx = Match(ours.Mesh.X, theirs.Mesh.X), my = Match(ours.Mesh.Y, theirs.Mesh.Y), mz = Match(ours.Mesh.Z, theirs.Mesh.Z);
        Assert.DoesNotContain(-1, my);
        int tissue = 0, differ = 0, compared = 0;
        for (int i = 0; i < ours.Mesh.Nx; i++)
            for (int j = 0; j < ours.Mesh.Ny; j++)
                for (int k = 0; k < ours.Mesh.Nz; k++)
                {
                    int a = RfCore.Hdf5.OpenEmsArrayOrder.Index(i, j, k, ours.Mesh.Ny, ours.Mesh.Nz);
                    if (mx[i] < 0 || mz[k] < 0)
                    {
                        Assert.True(ours.Density[a] == 0, $"circuitRF has tissue at cell ({i}, {j}, {k}), outside the tutorial's dump");
                        continue;
                    }
                    int b = RfCore.Hdf5.OpenEmsArrayOrder.Index(mx[i], my[j], mz[k], theirs.Mesh.Ny, theirs.Mesh.Nz);
                    compared++;
                    if (ours.Density[a] > 0 || theirs.Density[b] > 0) tissue++;
                    if (ours.Density[a] != theirs.Density[b] || ours.Conductivity[a] != theirs.Conductivity[b]) differ++;
                }
        Assert.Equal(0, differ);
        Assert.Equal(theirs.Density.Count(d => d > 0), tissue);
        Assert.True(compared > 0);
    }
}

/// <summary>Skips unless SAR-1's birdcage reference and circuitRF's committed replication output are both on this machine.</summary>
public sealed class BirdcageReplicationFactAttribute : FactAttribute
{
    public BirdcageReplicationFactAttribute()
    {
        if (!File.Exists(Path.Combine(BirdcageReplication.Reference, "summary.json")))
            Skip = "needs SAR-1's MRI_LP_Birdcage reference, which is not in the repository: testdata/sar/openems-ref/README.md says how to make it";
        else if (!File.Exists(Path.Combine(BirdcageReplication.Workspace, BirdcageReplication.Output, SarVolumeFiles.RecordFile(0))))
            Skip = "needs circuitRF's replication output in testdata/sar/circuitrf/birdcage: BirdcageRerunTests with CRF_WRITE_SAR8B_OUTPUTS=1 makes it";
    }
}

public sealed class BirdcageReplicationTests(ITestOutputHelper output)
{
    /// <summary>R-sar8b-6: the quadrature feed, as two single-port runs recombined on the reference's own cylindrical lines, against
    /// the tutorial's one run with both ports driven.</summary>
    [BirdcageReplicationFact]
    public void TheQuadratureFeed_BySuperposition_MatchesTheTutorial()
        => BirdcageReplication.Compare(Path.Combine(BirdcageReplication.Workspace, BirdcageReplication.Output), hold: true, output);
}

public sealed class BirdcageRerunTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar8b-rerun-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// The opt-in rerun, for whoever changes the cylindrical writer or the superposition: the workspace copied out, the setup run with
    /// `circuitrf em` and the quadrature recombined with `circuitrf fields`, both as processes, and held to the routine test's tolerances
    /// (the two port runs took about two hours together on the developer machine). CRF_WRITE_SAR8B_OUTPUTS=1 also writes the output
    /// into the repository.
    /// </summary>
    [BirdcageReferenceFact]
    [Trait("Category", "Benchmark")]
    public void TheRunAndTheRecombination_ThroughTheCli_StillMatch()
    {
        Assert.True(SolverDiscovery.ReadinessFor(Em3dSolverId.OpenEms).All(x => x.Proceeds), "needs openEMS");
        string ws = Path.Combine(_root, "birdcage");
        foreach (string f in new[] { ".cws", "tech.ctech", "Birdcage/.ccell", BirdcageReplication.C3d, BirdcageReplication.Cosim })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(ws, f))!);
            File.Copy(Path.Combine(BirdcageReplication.Workspace, f), Path.Combine(ws, f));
        }
        // the copy reads the reference's grid file where it is, not beside itself
        string c3d = Path.Combine(ws, BirdcageReplication.C3d);
        File.WriteAllText(c3d, File.ReadAllText(c3d).Replace("../../../../openems-ref/MRI_LP_Birdcage/BirdCage.xml",
            Path.Combine(BirdcageReplication.Reference, "BirdCage.xml").Replace("\\", "/")));
        var (exit, stdout, stderr) = LoopCoilRerunTests.Cli("em", c3d, "--setup", BirdcageReplication.Setup);
        Assert.True(exit == 0, stderr + stdout);
        (exit, stdout, stderr) = LoopCoilRerunTests.Cli("fields", Path.Combine(ws, BirdcageReplication.Cosim));
        Assert.True(exit == 0, stderr + stdout);
        string outDir = Path.Combine(_root, BirdcageReplication.Output);
        BirdcageReplication.Collect(Path.Combine(ws, "results"), outDir);
        BirdcageReplication.Compare(outDir, hold: true, output);
        if (Environment.GetEnvironmentVariable("CRF_WRITE_SAR8B_OUTPUTS") == "1")
            BirdcageReplication.Collect(Path.Combine(ws, "results"), Path.Combine(BirdcageReplication.Workspace, BirdcageReplication.Output));
    }
}

/// <summary>Skips unless CRF_SAR8B_RESULTS names a results folder a run of the setup and its recombination left.</summary>
public sealed class BirdcageExistingRunFactAttribute : FactAttribute
{
    public BirdcageExistingRunFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CRF_SAR8B_RESULTS") is not { Length: > 0 } dir)
            Skip = "collects from an existing run: set CRF_SAR8B_RESULTS to the results folder `circuitrf em` and `circuitrf fields` wrote";
        else if (!File.Exists(Path.Combine(BirdcageReplication.Reference, "summary.json")))
            Skip = "needs SAR-1's MRI_LP_Birdcage reference, which is not in the repository";
        else if (!Directory.Exists(Path.Combine(dir, BirdcageReplication.Stem)))
            Skip = $"'{dir}' holds no run of the setup ({BirdcageReplication.Stem})";
    }
}

public sealed class BirdcageCollectTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar8b-collect-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// The rerun's second half alone, for a run already made (two hours of openEMS are not repeated to collect it): what the results
    /// folder named by CRF_SAR8B_RESULTS holds, collected and compared, held only with CRF_SAR8B_HOLD=1; with CRF_WRITE_SAR8B_OUTPUTS=1
    /// it is also written into the repository.
    /// </summary>
    [BirdcageExistingRunFact]
    [Trait("Category", "Benchmark")]
    public void AnExistingRun_CollectedAndCompared()
    {
        string results = Environment.GetEnvironmentVariable("CRF_SAR8B_RESULTS")!;
        string outDir = Path.Combine(_root, BirdcageReplication.Output);
        BirdcageReplication.Collect(results, outDir);
        BirdcageReplication.Compare(outDir, hold: Environment.GetEnvironmentVariable("CRF_SAR8B_HOLD") == "1", output);
        if (Environment.GetEnvironmentVariable("CRF_WRITE_SAR8B_OUTPUTS") == "1")
            BirdcageReplication.Collect(results, Path.Combine(BirdcageReplication.Workspace, BirdcageReplication.Output));
    }
}

