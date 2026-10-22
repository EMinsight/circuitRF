// ================================================================
//  SystemCheckTests.cs — R-sar12-5: the system check. A half-wave dipole 10 mm below a flat phantom's liquid at 1.9 GHz
//  (testdata/sar/circuitrf/system-check), psSAR1g and psSAR10g per watt accepted: circuitRF's OWN results, with their grid
//  convergence in docs/design/sar.md §14. The standard's target values are not available (overview L2).
//
//    SystemCheckResultTests   routine, no process: the committed output loads, the dipole sits 10 mm from the liquid, and the
//                             band table renders from it — as text and as the dialog's rows.
//    SystemCheckRerunTests    Benchmark: the band sweep and the two convergence setups through the CLI, held to the committed
//                             numbers. With CRF_WRITE_SAR12_OUTPUTS=1 it rewrites the committed output.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.ThreeD;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Sar;

internal static class SystemCheck
{
    public static readonly string Workspace = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "circuitrf", "system-check");
    public const string C3d = "SystemCheck/3d/SystemCheck.c3d";
    public const string Sweep = "SystemCheck";
    public const string Record = "sar1.json";
    public const string Touchstone = "SystemCheck.s1p";
    public const string RunFacts = "run.json";

    /// <summary>The three runs: the setup's name in the .c3d (the band sweep's own for the medium grid), and its folder under
    /// output/.</summary>
    public static readonly (string Setup, string Folder)[] Runs = [("Coarse", "coarse"), ("SystemCheck 1.9 GHz", "medium"), ("Fine", "fine")];

    public static string Output(string folder) => Path.Combine(Workspace, "output", folder);

    /// <summary>Copies what a run of <paramref name="setup"/> left under <paramref name="resultsRoot"/> into <paramref name="outDir"/>:
    /// the Touchstone, the SAR record, and run.json (the grid, the stop step and the wall time from openEMS's own log), labelled
    /// as circuitRF output.</summary>
    public static void Collect(string resultsRoot, string setup, string outDir)
    {
        string stem = Path.Combine(resultsRoot, $"SystemCheck {setup}.openems");
        string p1 = Path.Combine(stem, "p1");
        Directory.CreateDirectory(outDir);
        File.Copy(stem + ".s1p", Path.Combine(outDir, Touchstone), true);
        File.Copy(Path.Combine(p1, Record), Path.Combine(outDir, Record), true);
        string log = File.ReadAllText(Path.Combine(p1, "openems.log"));
        var size = Regex.Match(log, @"FDTD simulation size: (\d+)x(\d+)x(\d+)");
        var stop = Regex.Match(log, @"end-criteria of [-\d.]+dB reached after (\d+) timesteps \(([-\d.]+)dB\)");
        var time = Regex.Match(log, @"Time for (\d+) iterations with [\d.]+ cells : ([\d.]+) sec");
        var facts = new JsonObject
        {
            ["note"] = $"circuitRF OUTPUT, circuitRF's own system-check values (brief-sar-12): setup {setup} of SystemCheck.c3d, run on openEMS.",
            ["setup"] = setup,
            ["grid_lines"] = new JsonArray(size.Groups.Values.Skip(1).Select(g => (JsonNode)int.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray()),
            ["stopped_at_timestep"] = time.Success ? int.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            ["energy_at_stop_db"] = stop.Success ? double.Parse(stop.Groups[2].Value, CultureInfo.InvariantCulture) : null,
            ["openems_wall_s"] = time.Success ? double.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture) : null,
        };
        File.WriteAllText(Path.Combine(outDir, RunFacts), facts.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    public static SarRunRecord ReadRecord(string dir) => JsonSerializer.Deserialize<SarRunRecord>(File.ReadAllText(Path.Combine(dir, Record)))!;

    /// <summary>A results root holding the medium run's record and Touchstone where a run of the band sweep would leave them.</summary>
    public static string ResultsWith(string root, C3dDocument doc, string c3dPath)
    {
        var sweep = doc.BandSweeps!.Single(b => b.Name == Sweep);
        var setup = C3dBandSweeps.Make(doc, sweep).Setups.Single();
        string results = Path.Combine(root, "results");
        var run = C3dSetups.ForRun(setup, c3dPath);
        string p1 = Path.Combine(Em3dRunService.RunDirectory(results, run, Em3dSolverId.OpenEms), "p1");
        Directory.CreateDirectory(p1);
        File.Copy(Path.Combine(Output("medium"), Record), Path.Combine(p1, Record));
        File.Copy(Path.Combine(Output("medium"), Touchstone), Em3dRunService.SnpBasePath(results, run, Em3dSolverId.OpenEms) + ".s1p");
        return results;
    }
}

public sealed class SystemCheckResultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar12-check-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void TheCommittedResultLoads_TheDipoleIs10MmFromTheLiquid_AndTheTableRenders()
    {
        string c3d = Path.Combine(SystemCheck.Workspace, SystemCheck.C3d);
        var doc = C3dPersistence.LoadFromFile(c3d);
        var e = C3dElaborator.ElaborateOnce(doc, c3d, Path.Combine(SystemCheck.Workspace, ".cws"));
        Assert.Empty(e.Refusals);
        var s = C3dDevicePlacement.Measure(doc, e, ["dipole"], "flat", "liquid")!;
        Assert.Equal(10e6, s.Distance, 1e-6);

        var sweep = doc.BandSweeps!.Single();
        var rows = C3dBandSweeps.Table(doc, sweep, c3d, SystemCheck.ResultsWith(_root, doc, c3d));
        var row = Assert.Single(rows);
        Assert.Null(row.Missing);
        var record = SystemCheck.ReadRecord(SystemCheck.Output("medium"));
        Assert.Equal(1.9e9, record.FrequencyHz);
        foreach (double m in new[] { 1.0, 10.0 })
        {
            var v = row.Values.Single(x => x.MassGrams == m);
            Assert.Equal(record.PeakPerWatt(SarSettings.AveragedName(m))!.Value, v.PerWattWkg);
            Assert.Equal(v.PerWattWkg, v.AtRatedWkg);     // 1 W, no duty cycle: per watt
            Assert.Equal("flat/liquid", v.Object);
        }
        var text = C3dBandSweeps.TableText(sweep, rows);
        Assert.Contains(text, l => l.TrimStart().StartsWith("psSAR1g ", StringComparison.Ordinal));
        Assert.Contains(text, l => l.TrimStart().StartsWith("psSAR10g ", StringComparison.Ordinal));
        var shown = BandSweepDialogViewModel.Row(row);
        Assert.Equal("1.9 GHz", shown.Band);
        Assert.NotEmpty(shown.Sar1g.Text);
        Assert.NotEmpty(shown.Sar10g.Text);
    }
}

public sealed class SystemCheckRerunTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar12-rerun-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>R-sar12-5's opt-in rerun: the workspace copied out, the band sweep and the two convergence setups run through the
    /// CLI as a process, each held to its committed psSAR1g and psSAR10g per watt within 1 % (openEMS's own run-to-run spread is
    /// a few tenths of a percent, sar.md §3.2). CRF_WRITE_SAR12_OUTPUTS=1 also writes the output into the repository.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheSystemCheck_ThroughTheCli_StillGivesItsValues()
    {
        Assert.True(SolverDiscovery.ReadinessFor(Em3dSolverId.OpenEms).All(r => r.Proceeds), "needs openEMS");
        string ws = Path.Combine(_root, "system-check");
        foreach (string f in new[] { ".cws", "tech.ctech", "SystemCheck/.ccell", SystemCheck.C3d })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(ws, f))!);
            File.Copy(Path.Combine(SystemCheck.Workspace, f), Path.Combine(ws, f));
        }
        string c3d = Path.Combine(ws, SystemCheck.C3d);
        bool write = Environment.GetEnvironmentVariable("CRF_WRITE_SAR12_OUTPUTS") == "1";
        foreach (var (setup, folder) in SystemCheck.Runs)
        {
            var (exit, stdout, stderr) = folder == "medium" ? Cli("em", c3d, "--bands", SystemCheck.Sweep) : Cli("em", c3d, "--setup", setup);
            Assert.True(exit == 0, stderr + stdout);
            output.WriteLine($"── {setup}\n{stdout}");
            string outDir = Path.Combine(_root, folder);
            SystemCheck.Collect(Path.Combine(ws, "results"), setup, outDir);
            var now = SystemCheck.ReadRecord(outDir);
            if (File.Exists(Path.Combine(SystemCheck.Output(folder), SystemCheck.Record)))
            {
                var kept = SystemCheck.ReadRecord(SystemCheck.Output(folder));
                foreach (string q in new[] { "SAR1g", "SAR10g" })
                {
                    double a = now.PeakPerWatt(q)!.Value, b = kept.PeakPerWatt(q)!.Value;
                    output.WriteLine($"{q}: {a:G6} W/kg per W, committed {b:G6} ({(a / b - 1) * 100:+0.00;-0.00} %)");
                    if (!write) Assert.InRange(a / b - 1, -0.01, 0.01);
                }
            }
            if (write) SystemCheck.Collect(Path.Combine(ws, "results"), setup, SystemCheck.Output(folder));
        }
    }

    private static (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        string cliDir = typeof(SystemCheckRerunTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
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
