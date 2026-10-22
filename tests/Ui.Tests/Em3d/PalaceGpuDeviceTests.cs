using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Tests.Lvs;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-136 §7 — Palace's Device setting, short of a real GPU run: no machine here has a GPU Palace
//  supports, so these hold the JSON written, the process count, the probe's parse, the refusal and the
//  post-run check. The real run is the field tester's (R-em3d136-5c).
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class PalaceGpuDeviceTests : IDisposable
{
    private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-gpu-" + Guid.NewGuid().ToString("N")[..10]);

    public PalaceGpuDeviceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── 1, 2. the JSON ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T1_DeviceIsWrittenInTheSolverBlock_OfDrivenEigenmodeAndStaticConfigs_CpuByDefault()
    {
        foreach (var (kind, problem, settings) in Problems())
        {
            var low = GmshGeoWriter.Write(problem, settings);
            Assert.True(low.Ok, low.Refusal);
            Assert.True(SolverDevice(PalaceConfigWriter.Write(problem, low.Groups, settings).Json!) == "CPU", kind);
            Assert.True(SolverDevice(PalaceConfigWriter.Write(problem, low.Groups, settings, device: PalaceDevice.Gpu).Json!) == "GPU", kind);
        }
    }

    [Fact]
    public void T2_AGpuConfigWritesNoPartialAssemblyOrderAndNoBackend()
    {
        foreach (var (_, problem, settings) in Problems())
        {
            var low = GmshGeoWriter.Write(problem, settings);
            string json = PalaceConfigWriter.Write(problem, low.Groups, settings, device: PalaceDevice.Gpu).Json!;
            Assert.DoesNotContain("PartialAssemblyOrder", json);
            Assert.DoesNotContain("\"Backend\"", json);
        }
    }

    // ── 3. the probe's parse ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T3_TheProbeReadsTheGpuCount_NoLineIsACpuBuild_ZeroIsAGpuBuildThatSeesNone()
    {
        const string Banner = "Palace version: 0dc74cd\n";
        var (two, sure2) = SolverDiscovery.ReadGpuProbe(0, Banner + "Detected 2 Xyz devices\nDevice configuration: xyz,cpu\n");
        Assert.True(two.Available);
        Assert.Equal(2, two.Devices);
        Assert.True(sure2);

        var (none, sureNone) = SolverDiscovery.ReadGpuProbe(0, Banner + "Device configuration: cpu\n");
        Assert.False(none.Available);
        Assert.Null(none.Devices);
        Assert.True(sureNone);

        var (zero, _) = SolverDiscovery.ReadGpuProbe(0, Banner + "Detected 0 Xyz devices\nDevice configuration: cpu\n");
        Assert.False(zero.Available);
        Assert.Equal(0, zero.Devices);
    }

    // ── 4. how many processes ───────────────────────────────────────────────────────────────────

    [Fact]
    public void T4_AGpuRunIsOneProcessPerGpu_CappedByCores_AndARankRefusalNeverFiresOnAGpuCount()
    {
        var oneCore = new PhysicalCoreReading(1, true, "test");
        Assert.Equal(2, Em3dRunService.PalaceProcessCount(PalaceDevice.Gpu, null, oneCore, 2).Processes);
        Assert.Equal(1, Em3dRunService.PalaceProcessCount(PalaceDevice.Gpu, 1, oneCore, 2).Processes);
        // Two GPUs on a one-core machine: not a reason to refuse, with no cap or a cap above the GPU count.
        Assert.Null(Em3dRunService.PalaceRankRefusal(PalaceDevice.Gpu, null, oneCore, 2));
        Assert.Null(Em3dRunService.PalaceRankRefusal(PalaceDevice.Gpu, 4, oneCore, 2));
    }

    // ── 5. the refusal ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_AGpuRunOnACpuBuild_IsRefusedByTheReadinessCheck_NamingThePath()
    {
        // The readiness check is the run's first step (Em3dRunService.Run), before Gmsh is started.
        string palace = FakePalace("Palace version: 0dc74cd\nDevice configuration: cpu");
        var readiness = Discovery(palace).Check(SolverDiscovery.CapabilitiesFor(SolverTool.Palace, null, PalaceDevice.Gpu));
        Assert.False(readiness.Proceeds);
        Assert.Contains($"'{palace}'", readiness.Refusal);
        Assert.Contains("built without GPU support", readiness.Refusal);
    }

    // ── 6. after the run ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T6_ALogThatRevertedToTheCpuProducesTheWarning()
    {
        string log = Path.Combine(_root, PalaceRun.PalaceLogFile);
        File.WriteAllLines(log, ["Palace version: 0dc74cd", "Warning: Palace was not built with GPU support, reverting to CPU!",
                                 "Device configuration: cpu"]);
        Assert.True(PalaceRun.RanOnCpu(log));
        Assert.Contains("ran on the CPU", Em3dRunService.GpuRevertedWarning(_root));
        File.WriteAllLines(log, ["Device configuration: xyz,cpu"]);
        Assert.False(PalaceRun.RanOnCpu(log));
    }

    // ── 8. the preference ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void T8_ThePreferenceRoundTrips_AndAbsentIsCpu()
    {
        string json = JsonSerializer.Serialize(new AppPreferences { EmPalaceDevice = EmPalaceDevicePreference.ToStored(PalaceDevice.Gpu) });
        Assert.Contains("\"em_palace_device\":\"gpu\"", json);
        Assert.Equal(PalaceDevice.Gpu, EmPalaceDevicePreference.FromStored(JsonSerializer.Deserialize<AppPreferences>(json)!.EmPalaceDevice));
        Assert.Equal(PalaceDevice.Cpu, EmPalaceDevicePreference.FromStored(JsonSerializer.Deserialize<AppPreferences>("{}")!.EmPalaceDevice));
        Assert.Null(EmPalaceDevicePreference.ToStored(PalaceDevice.Cpu));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<(string Kind, Em3dProblem Problem, PalaceSettings Settings)> Problems()
    {
        var (driven, settings) = PalaceBackendTests.LowerableFor("microstrip");
        yield return ("driven", driven, settings);
        yield return ("eigenmode", PalaceEigenTests.Cavity(5.8e7, 1), PalaceSettings.Default);
        var (setup, source) = PalaceStaticTests.TwoLines(Em3dProblemType.Electrostatic);
        yield return ("static", Em3dGenerator.Generate(setup, source, source.Technology!).Problem!, PalaceSettings.Resolve(setup.Palace));
    }

    private static string SolverDevice(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Solver").GetProperty("Device").GetString()!;
    }

    private SolverDiscovery Discovery(string palace)
    {
        var d = SolverDiscovery.Create(SolverTool.Palace);
        d.PreferredCommand  = () => palace;
        d.ReadEnvironment   = _ => null;
        d.SearchDirectories = [];
        d.CacheDirectory    = Path.Combine(_root, "cache");
        return d;
    }

    /// <summary>A fake Palace: the F0 version banner for its version question, a clean dry run, and
    /// <paramref name="solve"/> for any solve.</summary>
    private string FakePalace(string solve)
    {
        string banner = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "f0", "banners", "palace-version.txt");
        string path = Path.Combine(_root, Windows ? "crf-fake-palace.cmd" : "crf-fake-palace");
        string script = Windows
            ? "@echo off\r\nif \"%~2\"==\"--version\" (\r\n  type \"" + banner + "\"\r\n  exit /b 0\r\n)\r\n" +
              "if \"%~2\"==\"--dry-run\" (\r\n  echo Dry-run: No errors detected\r\n  exit /b 0\r\n)\r\n" +
              string.Join("\r\n", solve.Split('\n').Select(l => "echo " + l)) + "\r\n"
            : "#!/bin/sh\nfor a in \"$@\"; do\n  if [ \"$a\" = \"--dry-run\" ]; then echo 'Dry-run: No errors detected'; exit 0; fi\n" +
              "  if [ \"$a\" = \"--version\" ]; then cat \"" + banner + "\"; exit 0; fi\ndone\n" +
              string.Join("\n", solve.Split('\n').Select(l => $"echo '{l}'")) + "\n";
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}

// ── 7. `em --device`, in process: the console is shared, so this runs in the CLI console collection ──

[Collection(LvsCliConsoleCollection.Name)]
public sealed class PalaceGpuDeviceCliTests
{
    [Fact]
    public void T7_EmDeviceParsesCpuAndGpu_AndRefusesAnythingElseListingTheTwo()
    {
        Assert.Equal(PalaceDevice.Gpu, PalaceDevices.Parse("GPU"));
        Assert.Equal(PalaceDevice.Cpu, PalaceDevices.Parse("cpu"));

        var (out0, err0) = (Console.Out, Console.Error);
        var err = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetError(err);
            JsonRun.Reset();
            exit = CliEntry.Run(["em", "setup.cem", "--device", "tpu"]);
        }
        finally { Console.SetOut(out0); Console.SetError(err0); }
        Assert.Equal(1, exit);
        Assert.Contains("--device takes auto, cpu or gpu, not 'tpu'", err.ToString());
    }
}
