// ================================================================
//  DipoleSarReplicationTests.cs — the gate of brief-sar-7: openEMS's Dipole_SAR tutorial rebuilt from .c3d objects
//  (testdata/sar/circuitrf/dipole), run through circuitRF, against SAR-1's reference (testdata/sar/openems-ref/Dipole_SAR).
//
//    DipoleSarReplicationTests   fixed data, routine: circuitRF's committed output of both runs against the reference, at
//                                overview §4's tolerances — (a) on the reference's own grid lines, (b) on circuitRF's grid.
//                                No openEMS process. Skipped where the reference set is absent (it is never in the repo).
//    NearFieldSurfaceTests       the equivalence surface sits inside the PML on given grid lines as on circuitRF's own grid
//                                (the first divergence run (a) found, sar.md §9.3).
//    DipoleSarRerunTests         Benchmark: both runs again through the CLI, held to the same tolerances. With
//                                CRF_WRITE_SAR7_OUTPUTS=1 it also rewrites the committed output.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Sar;

/// <summary>Where the replication lives, what a run leaves, and the comparison both the routine and the Benchmark test make.</summary>
internal static class DipoleReplication
{
    public static readonly string Workspace = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "circuitrf", "dipole");
    public static readonly string Reference = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "openems-ref", "Dipole_SAR");
    public const string C3d = "Dipole/3d/Dipole.c3d";

    /// <summary>The two runs: the setup's name in the .c3d, and the folder its output is kept in.</summary>
    public static readonly (string Setup, string Folder)[] Runs = [("ReferenceGrid", "reference-grid"), ("OwnGrid", "own-grid")];

    public const string Touchstone = "Dipole.s1p";
    public const string Record = "sar1.json";
    public const string RunFacts = "run.json";
    public static readonly string[] Kept = [Touchstone, "port1_u", "port1_i", Record, "sar1.SAR10g.npy", "sar1.x.npy", "sar1.y.npy", "sar1.z.npy", RunFacts];

    /// <summary>
    /// Copies what a run of <paramref name="setup"/> left under <paramref name="resultsRoot"/> into <paramref name="outDir"/>: the
    /// Touchstone, the port's probes, the SAR record and its 10 g volume with the cell centres, and run.json — the grid, the stop
    /// step and the wall time from openEMS's own log — labelled as circuitRF output.
    /// </summary>
    public static void Collect(string resultsRoot, string setup, string outDir)
    {
        string stem = Path.Combine(resultsRoot, $"Dipole {setup}.openems");
        string p1 = Path.Combine(stem, "p1");
        Directory.CreateDirectory(outDir);
        File.Copy(stem + ".s1p", Path.Combine(outDir, Touchstone), true);
        foreach (string f in Kept.Skip(1).SkipLast(1)) File.Copy(Path.Combine(p1, f), Path.Combine(outDir, f), true);

        string log = File.ReadAllText(Path.Combine(p1, "openems.log"));
        var size = Regex.Match(log, @"FDTD simulation size: (\d+)x(\d+)x(\d+)");
        var stop = Regex.Match(log, @"end-criteria of [-\d.]+dB reached after (\d+) timesteps \(([-\d.]+)dB\)");
        var time = Regex.Match(log, @"Time for (\d+) iterations with [\d.]+ cells : ([\d.]+) sec");
        var facts = new JsonObject
        {
            ["note"] = "circuitRF OUTPUT, not a reference: setup " + setup + " of Dipole.c3d, run by circuitRF on openEMS " +
                       "(brief-sar-7). The reference it is compared with is testdata/sar/openems-ref/Dipole_SAR.",
            ["setup"] = setup,
            ["grid_lines"] = new JsonArray(size.Groups.Values.Skip(1).Select(g => (JsonNode)int.Parse(g.Value)).ToArray()),
            ["stopped_at_timestep"] = time.Success ? int.Parse(time.Groups[1].Value) : null,
            ["energy_at_stop_db"] = stop.Success ? double.Parse(stop.Groups[2].Value, CultureInfo.InvariantCulture) : null,
            ["openems_wall_s"] = time.Success ? double.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture) : null,
        };
        File.WriteAllText(Path.Combine(outDir, RunFacts), facts.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>A one-port Touchstone in GHz/RI, as the em verb writes it: frequency (Hz) and S11.</summary>
    public static (double[] Hz, Complex[] S11) ReadS1p(string path)
    {
        var rows = File.ReadAllLines(path).Where(l => l.Trim().Length > 0 && l[0] is not '!' and not '#')
                       .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToList();
        Assert.Contains("# GHz S RI R 50", File.ReadAllText(path));
        return ([.. rows.Select(r => r[0] * 1e9)], [.. rows.Select(r => new Complex(r[1], r[2]))]);
    }

    public static SarRunRecord ReadRecord(string dir)
        => JsonSerializer.Deserialize<SarRunRecord>(File.ReadAllText(Path.Combine(dir, Record)))!;

    /// <summary>The reference's peak 10 g cell centre (metres) and the density there: sar_calc's maxSAR_idx on its mesh.</summary>
    public static (double[] Centre, double CellM) ReferencePeak()
    {
        var idx = JsonDocument.Parse(File.ReadAllText(Path.Combine(Reference, "summary.json"))).RootElement
                              .GetProperty("sar_10g").GetProperty("maxSAR_idx").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var mesh = "xyz".Select(a => SarReferenceSetTests.Npy.Read(Path.Combine(Reference, $"sar_10g_mesh_{a}_m.npy")).Real).ToArray();
        double cell = Enumerable.Range(0, 3).Max(a => mesh[a][idx[a] + 1] - mesh[a][idx[a]]);
        return ([mesh[0][idx[0]], mesh[1][idx[1]], mesh[2][idx[2]]], cell);
    }

    private static double Rel(double a, double b) => Math.Abs(a - b) / Math.Abs(b);

    /// <summary>
    /// Overview §4 for one run's output in <paramref name="dir"/>. <paramref name="onReferenceGrid"/> holds run (a) to the tight
    /// tolerances (S11, Zin, accepted power, psSAR10g 2 %, whole-phantom SAR 2 %, the budget); run (b) is held to psSAR10g within
    /// 5 %. Both: the peak 10 g cube lies in the reference's tissue and within one of its cells of its position.
    /// </summary>
    public static void AssertMatches(string dir, bool onReferenceGrid, ITestOutputHelper output)
    {
        var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(Reference, "summary.json"))).RootElement;
        var r = ReadRecord(dir);
        double ps = r.PeakPerWatt("SAR10g")!.Value, psRef = summary.GetProperty("psSAR10g_w_per_kg_per_w").GetDouble();
        double whole = r.WholeSarPerWatt!.Value, wholeRef = summary.GetProperty("whole_phantom_sar_w_per_kg_per_w").GetDouble();
        output.WriteLine($"psSAR10g {ps:G5} W/kg/W (reference {psRef:G5}, {100 * (ps / psRef - 1):+0.00;-0.00} %); whole-phantom " +
                         $"{whole:G5} ({100 * (whole / wholeRef - 1):+0.00;-0.00} %); absorbed {100 * r.AbsorbedFraction:F2} %, radiated " +
                         $"{100 * r.RadiationEfficiency:F2} %, budget {100 * r.PowerBudget:F2} %");

        var peak = r.Peaks.Single(p => p.Quantity == "SAR10g");
        var (refCentre, cell) = ReferencePeak();
        output.WriteLine($"peak 10 g cube at ({string.Join(", ", peak.CentreM.Select(v => (v * 1e3).ToString("F2", CultureInfo.InvariantCulture)))}) mm " +
                         $"in {peak.Object}; reference ({string.Join(", ", refCentre.Select(v => (v * 1e3).ToString("F2", CultureInfo.InvariantCulture)))}) mm");
        Assert.Equal("head/skin", peak.Object);           // the reference's peak cell holds ρ 1100 kg/m³, the skin's
        for (int a = 0; a < 3; a++)
            Assert.True(Math.Abs(peak.CentreM[a] - refCentre[a]) <= cell * (1 + 1e-6),
                        $"peak cube centre {"xyz"[a]} = {peak.CentreM[a] * 1e3} mm, reference {refCentre[a] * 1e3} mm, one cell {cell * 1e3} mm");

        if (!onReferenceGrid)
        {
            Assert.True(Rel(ps, psRef) <= 0.05, $"psSAR10g {ps} W/kg/W against {psRef}: run (b) is held to 5 %");
            return;
        }
        Assert.True(Rel(ps, psRef) <= 0.02, $"psSAR10g {ps} W/kg/W against {psRef}");
        Assert.True(Rel(whole, wholeRef) <= 0.02, $"whole-phantom SAR {whole} W/kg/W against {wholeRef}");
        Assert.InRange(r.PowerBudget!.Value, 0.95, 1.00);  // (radiated + absorbed) / accepted, as the tutorial prints it

        var (hz, s11) = ReadS1p(Path.Combine(dir, Touchstone));
        var refHz = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "freq_hz.npy")).Real;
        var refS = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "s11.npy")).Values!;
        var refZ = SarReferenceSetTests.Npy.Read(Path.Combine(Reference, "zin_ohm.npy")).Values!;
        double worst = 0;
        for (int i = 0; i < hz.Length; i++)
        {
            int k = Array.IndexOf(refHz, hz[i]);
            Assert.True(k >= 0, $"{hz[i]} Hz is not on the reference's frequency vector");
            worst = Math.Max(worst, Math.Abs(s11[i].Magnitude - refS[k].Magnitude));
            if (hz[i] != 1e9) continue;
            // Zin from S11 (the port is a 50 Ω lumped port, so S11 = (Z − 50)/(Z + 50) exactly as the tutorial forms it)
            var z = 50 * (1 + s11[i]) / (1 - s11[i]);
            output.WriteLine($"Zin at 1 GHz {z.Real:F3}{z.Imaginary:+0.000;-0.000}j Ω, reference {refZ[k].Real:F3}{refZ[k].Imaginary:+0.000;-0.000}j Ω");
            Assert.True((z - refZ[k]).Magnitude <= 0.01 * refZ[k].Magnitude, $"Zin {z} against {refZ[k]}");
            // Accepted power is formed at each run's own pulse, so it is compared as the fraction of the incident wave the
            // port accepts, 1 − |S11|², which is what it is per unit drive (sar.md §9.2).
            double acc = 1 - s11[i].Magnitude * s11[i].Magnitude, accRef = 1 - refS[k].Magnitude * refS[k].Magnitude;
            Assert.True(Rel(acc, accRef) <= 0.01, $"accepted fraction {acc} against {accRef}");
        }
        output.WriteLine($"|S11| worst difference over {hz.Length} points: {worst:E2}");
        Assert.True(worst <= 0.01, $"|S11| differs by {worst} somewhere in the band");
        Assert.Contains(1e9, hz);
    }
}

/// <summary>Skips unless SAR-1's dipole reference and circuitRF's committed replication output are both on this machine.</summary>
public sealed class DipoleReplicationFactAttribute : FactAttribute
{
    public DipoleReplicationFactAttribute()
    {
        if (!Directory.Exists(DipoleReplication.Reference))
            Skip = "needs the SAR-1 reference data, which is not in the repository: testdata/sar/openems-ref/README.md says how to make it";
        else if (DipoleReplication.Runs.Any(r => !File.Exists(Path.Combine(DipoleReplication.Workspace, r.Folder, DipoleReplication.Record))))
            Skip = "needs circuitRF's replication output in testdata/sar/circuitrf/dipole: DipoleSarRerunTests with CRF_WRITE_SAR7_OUTPUTS=1 makes it";
    }
}

public sealed class DipoleSarReplicationTests(ITestOutputHelper output)
{
    /// <summary>R-sar7-3 (a): on the reference's own grid lines, every overview §4 quantity at its tight tolerance.</summary>
    [DipoleReplicationFact]
    public void OnTheReferencesGrid_MatchesTheTutorial()
        => DipoleReplication.AssertMatches(Path.Combine(DipoleReplication.Workspace, "reference-grid"), onReferenceGrid: true, output);

    /// <summary>R-sar7-3 (b): on circuitRF's own grid at a 2.5 mm target in the phantom, psSAR10g within 5 %.</summary>
    [DipoleReplicationFact]
    public void OnItsOwnGrid_PsSar10gIsWithinFivePercent()
        => DipoleReplication.AssertMatches(Path.Combine(DipoleReplication.Workspace, "own-grid"), onReferenceGrid: false, output);
}

public sealed class NearFieldSurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar7-surface-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// R-sar7-4's first divergence (sar.md §9.3): the radiation pattern's surface sits PmlMarginCells inside the PML — which on
    /// circuitRF's own grid lies outside the air box, and on given lines is the grid's outermost PmlCells cells inside it. Both
    /// grids put the surface PmlCells + PmlMarginCells lines in from the grid's edge.
    /// </summary>
    [Fact]
    public void TheSurfaceSitsInsideThePml_OnGivenLinesAsOnItsOwnGrid()
    {
        var (c3d, cws) = SarFixtures.Workspace(_root);
        var doc = SarFixtures.Dipole();
        C3dPersistence.SaveToFile(c3d, doc);
        List<double> uniform = [.. Enumerable.Range(-60, 121).Select(v => (double)v)];
        var face = new EmAirBoxFace(null, Em3dBoundaryKind.Absorbing);
        foreach (var given in new[] { false, true })
        {
            var setup = SarFixtures.Setup();
            // given: the air box is the grid's extent (content x, y ±0.5 mm, z ±21 mm, the lines ±60 mm)
            setup.AirBox = given
                ? new EmAirBox(face with { PaddingUm = 59_500 }, face with { PaddingUm = 59_500 }, face with { PaddingUm = 59_500 },
                               face with { PaddingUm = 59_500 }, face with { PaddingUm = 39_000 }, face with { PaddingUm = 39_000 })
                : new EmAirBox(face, face, face, face, face, face);
            setup.OpenEms = new CemOpenEms { PmlCells = 8, GridLines = given ? new CemGridLines { Unit = "mm", X = uniform, Y = uniform, Z = uniform } : null };
            var g = C3dProblemAssembly.Assemble(setup, doc, c3d, cws, new C3dElaborator());
            Assert.True(g.Ok, g.Refusal);
            var grid = FdtdGrid.Build(g.Problem!, CemOpenEms.ResolveGrid(setup.OpenEms, c3d), long.MaxValue);
            Assert.Null(grid.Refusal);
            var (surface, refusal) = OpenEmsFarField.Place(g.Problem!, grid, [1e9], 8);
            Assert.True(surface is not null, refusal);
            const int lines = 8 + OpenEmsFarField.PmlMarginCells;
            Assert.Equal(grid.X.Lines[lines], surface.Min.X, 12);
            Assert.Equal(grid.Y.Lines[^(lines + 1)], surface.Max.Y, 12);
            Assert.Equal(grid.Z.Lines[lines], surface.Min.Z, 12);
        }
    }
}

public sealed class DipoleSarRerunTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar7-rerun-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// R-sar7-3's opt-in rerun, for whoever changes the writer: the replication workspace copied out, both setups run through the
    /// CLI as a process, and each run's output held to the routine test's tolerances. 15.6 minutes measured on the Debug build
    /// (sar.md §9.5). CRF_WRITE_SAR7_OUTPUTS=1 also writes the output into the repository.
    /// </summary>
    [SarReferenceFact]
    [Trait("Category", "Benchmark")]
    public void BothRuns_ThroughTheCli_StillMatch()
    {
        Assert.True(SolverDiscovery.ReadinessFor(Em3dSolverId.OpenEms).All(r => r.Proceeds), "needs openEMS");
        string ws = Path.Combine(_root, "dipole");
        foreach (string f in new[] { ".cws", "tech.ctech", "Dipole/.ccell", DipoleReplication.C3d })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(ws, f))!);
            File.Copy(Path.Combine(DipoleReplication.Workspace, f), Path.Combine(ws, f));
        }
        // the copy reads the reference's grid file where it is, not beside itself
        string c3d = Path.Combine(ws, DipoleReplication.C3d);
        File.WriteAllText(c3d, File.ReadAllText(c3d).Replace("../../../../openems-ref/Dipole_SAR/sar_dipole.xml",
            Path.Combine(DipoleReplication.Reference, "sar_dipole.xml").Replace("\\", "/")));

        bool write = Environment.GetEnvironmentVariable("CRF_WRITE_SAR7_OUTPUTS") == "1";
        foreach (var (setup, folder) in DipoleReplication.Runs)
        {
            var (exit, stdout, stderr) = Cli("em", c3d, "--setup", setup);
            Assert.True(exit == 0, stderr + stdout);
            string outDir = Path.Combine(_root, folder);
            DipoleReplication.Collect(Path.Combine(ws, "results"), setup, outDir);
            output.WriteLine($"── {setup}");
            DipoleReplication.AssertMatches(outDir, onReferenceGrid: folder == "reference-grid", output);
            if (write) DipoleReplication.Collect(Path.Combine(ws, "results"), setup, Path.Combine(DipoleReplication.Workspace, folder));
        }
    }

    private static (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        string cliDir = typeof(DipoleSarRerunTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
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
