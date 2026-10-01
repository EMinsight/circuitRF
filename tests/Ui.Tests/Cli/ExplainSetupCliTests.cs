// Designer feedback round 11 — `explain` on a .c3d with several setups said "name one with --setup", and explain took no
// --setup. It does now, spelled as `em` and `render` spell it; and an air-box face whose padding the setup leaves unstated
// says what the default made it and why, instead of reading "setup" because its boundary was stated.

using System.Diagnostics;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class ExplainSetupCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-explain-setup-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Setup_ChoosesAnEmbeddedSetup_AndADefaultFaceSaysWhatTheDefaultMadeIt()
    {
        string c3d = TwoSetupView();

        // Without --setup the view cannot choose, and the note names the flag explain now takes.
        var (_, plain, _) = RunCli("explain", c3d);
        Assert.Contains("name one with --setup", plain);

        // "pinned" states every face's padding; "open" states only the boundary, so its padding is the default's.
        var (exit, json, err) = RunCli("explain", c3d, "--setup", "open", "--json");
        Assert.True(exit == 0, err + json);
        var faces = JsonNode.Parse(json)!["result"]!["explain"]!["em3d"]!["airBox"]!["faces"]!.AsArray();
        string xmax = (string)faces.Single(f => (string)f!["face"]! == "xmax")!["from"]!;
        Assert.StartsWith("default: 7.495 mm, λ/8 at 5 GHz", xmax);

        var (_, pinned, _) = RunCli("explain", c3d, "--setup", "pinned", "--json");
        var pinnedFaces = JsonNode.Parse(pinned)!["result"]!["explain"]!["em3d"]!["airBox"]!["faces"]!.AsArray();
        Assert.Equal("setup: 0 mm", (string)pinnedFaces.Single(f => (string)f!["face"]! == "xmax")!["from"]!);
    }

    [Fact]
    public void Setup_AnUnknownNameListsTheRealOnes_AndAnythingButA3dViewIsRefused()
    {
        string c3d = TwoSetupView();
        var (exit, stdout, stderr) = RunCli("explain", c3d, "--setup", "nope");
        Assert.Equal(1, exit);
        Assert.Contains("no setup named 'nope'; it has 'pinned', 'open'", stdout + stderr);

        // render takes the same flag, and an unknown name there is the same refusal, not "a planar setup".
        string svg = Path.Combine(_root, "never.svg");
        var (renderExit, renderOut, renderErr) = RunCli("render", c3d, "-o", svg, "--iso", "--setup", "nope");
        Assert.Equal(1, renderExit);
        Assert.Contains("no setup named 'nope'", renderOut + renderErr);
        Assert.False(File.Exists(svg));

        string tech = Path.Combine(_root, "ws", "tech.ctech");
        var (techExit, techOut, techErr) = RunCli("explain", tech, "--setup", "open");
        Assert.Equal(1, techExit);
        Assert.Contains("is not a 3D view", techOut + techErr);
    }

    private string TwoSetupView()
    {
        const long um = 1000;
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cavity", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cavity.c3d");
        var freq = new CircuitRF.Core.Design.FrequencySpec("5", "15", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz");
        EmSetup Modes(string name, EmAirBoxFace face) => new()
        {
            Name = name, Solver3D = Em3dSolver.Palace, Problem3D = Em3dProblemType.Eigenmode,
            AirBox = new EmAirBox(face, face, face, face, face, face), Eigenmode = new EmEigenmode3D(1, 5), Frequency = freq,
        };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "cavity", Material = "Air", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(22860 * um, 10160 * um, 25000 * um) }],
            Setups =
            [
                EmSetupPersistence.ToEmbedded(Modes("pinned", new EmAirBoxFace(0, Em3dBoundaryKind.Pec))),
                EmSetupPersistence.ToEmbedded(Modes("open", new EmAirBoxFace(null, Em3dBoundaryKind.Pec))),
            ],
        });
        return path;
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        string stdout = outTask.GetAwaiter().GetResult(), stderr = errTask.GetAwaiter().GetResult();
        output.WriteLine($"$ {string.Join(' ', args)}\n{stdout}{stderr}");
        return (proc.ExitCode, stdout, stderr);
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ExplainSetupCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}
