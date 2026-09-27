using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The geometry worker, driven over stdin/stdout exactly as circuitRF will drive it (brief-em3d-62
/// R-em3d62-7b): the skeleton protocol's four requests, and an unknown one answered with <c>ok:false</c>
/// while the worker keeps running.
///
/// <para>These need a BUILT worker, which a fresh clone does not have — circuitRF builds and tests with no
/// OpenCASCADE anywhere (R-em3d62-3f) — so they skip with a reason naming the one command that builds it
/// (series overview §1d, case 1). A skip here is the ordinary state of CI.</para>
/// </summary>
public class GeometryWorkerProtocolTests
{
    [GeometryWorkerFact]
    public void TheWorker_AnswersTheSkeletonProtocol_AndSurvivesAnUnknownRequest()
    {
        using var w = new Worker(GeometryWorkerFactAttribute.Path!);

        var hello = w.Ask("""{"op":"hello","protocol":1}""");
        Assert.True((bool)hello["ok"]!);
        Assert.Equal(1, (int)hello["protocol"]!);
        Assert.Equal(GeometryWorkerFactAttribute.RecipeOcctVersion(), (string)hello["occt"]!);
        Assert.Equal(AppVersion.Display, (string)hello["worker"]!);

        var box = w.Ask("""{"op":"box","size_um":[100,200,300]}""");
        Assert.True((bool)box["ok"]!);
        Assert.Equal(1, (int)box["solids"]!);
        Assert.Equal(6, (int)box["faces"]!);
        Assert.Equal(6e6, (double)box["volume_um3"]!, 6);

        var unknown = w.Ask("""{"op":"no-such-operation"}""");
        Assert.False((bool)unknown["ok"]!);
        Assert.Contains("no-such-operation", (string)unknown["error"]!, StringComparison.Ordinal);
        var garbage = w.Ask("not json at all");
        Assert.False((bool)garbage["ok"]!);

        // Still running after both refusals: the self-test is the whole kernel in one request.
        var selftest = w.Ask("""{"op":"selftest"}""");
        Assert.True((bool)selftest["ok"]!, selftest.ToJsonString());
        Assert.True((bool)selftest["valid"]!);
        Assert.True((bool)selftest["step_roundtrip"]!);

        var quit = w.Ask("""{"op":"quit"}""");
        Assert.True((bool)quit["ok"]!);
        Assert.True(w.Process.WaitForExit(10_000), "the worker did not exit after quit");
        Assert.Equal(0, w.Process.ExitCode);
    }

    [GeometryWorkerFact]
    public void Version_ReportsTheRecipesOcct_WithoutSpeakingTheProtocol()
    {
        var psi = new ProcessStartInfo(GeometryWorkerFactAttribute.Path!, "--version")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(10_000));
        Assert.Equal(0, p.ExitCode);
        Assert.Equal(GeometryWorkerFactAttribute.RecipeOcctVersion(), GeometryKernelNotice.ParseOcctVersion(stdout));
    }

    // The About box's sentence, which needs no worker: it is the prominent notice when the kernel is
    // present, and must never name a library that is absent (R-em3d62-6d).
    [Theory]
    [InlineData("geometry-worker 1.0.0\nocct 8.0.1\n", "8.0.1")]
    [InlineData("geometry-worker 1.0.0\nocct 8.0.1 (built against 8.0.0 -- MISMATCH)\n", null)]
    [InlineData("", null)]
    public void TheAboutBox_ReadsTheVersionTheWorkerReports(string output, string? expected)
        => Assert.Equal(expected, GeometryKernelNotice.ParseOcctVersion(output));

    [Fact]
    public void TheAboutBox_NamesOcctOnlyWhenTheKernelIsThere()
    {
        Assert.Contains("Uses Open CASCADE Technology 8.0.1", GeometryKernelNotice.Describe(true, "8.0.1"), StringComparison.Ordinal);
        Assert.Contains("Open CASCADE Technology", GeometryKernelNotice.Describe(true, null), StringComparison.Ordinal);
        string absent = GeometryKernelNotice.Describe(false, null);
        Assert.Contains("not included", absent, StringComparison.Ordinal);
        Assert.DoesNotContain("Open CASCADE", absent, StringComparison.Ordinal);
    }

    private sealed class Worker : IDisposable
    {
        public Process Process { get; }

        public Worker(string path)
        {
            Process = Process.Start(new ProcessStartInfo(path)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8,
            })!;
            Process.ErrorDataReceived += (_, _) => { };
            Process.BeginErrorReadLine();
        }

        public JsonObject Ask(string request)
        {
            Process.StandardInput.Write(request + "\n");
            Process.StandardInput.Flush();
            var line = Process.StandardOutput.ReadLineAsync();
            Assert.True(line.Wait(TimeSpan.FromSeconds(30)), $"no answer to {request}");
            Assert.NotNull(line.Result);
            return (JsonObject)JsonNode.Parse(line.Result!)!;
        }

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { /* gone */ }
            Process.Dispose();
        }
    }
}

/// <summary>
/// Runs only where a geometry worker has been built, and otherwise skips naming how to build one. Looks
/// where a build puts it: beside this test's assemblies, then the folder <c>ensure-built</c> stages for
/// this machine's RID.
/// </summary>
public sealed class GeometryWorkerFactAttribute : FactAttribute
{
    public static readonly string? Path = Find();

    public GeometryWorkerFactAttribute()
    {
        if (Path is null)
            Skip = "No geometry worker is built on this machine (the ordinary state of a fresh clone). "
                 + "tools/geometry-worker/build.sh (build.cmd on Windows) builds OpenCASCADE and the worker, once.";
    }

    private static string? Find()
    {
        string exe = OperatingSystem.IsWindows() ? "geometry-worker.exe" : "geometry-worker";
        string beside = System.IO.Path.Combine(AppContext.BaseDirectory, "geometry-kernel", exe);
        if (File.Exists(beside)) return beside;

        string? root = RepoRoot();
        if (root is null) return null;
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64", Architecture.X86 => "x86", _ => "x64",
        };
        string staged = System.IO.Path.Combine(root, "tools", "geometry-worker", "build", $"{os}-{arch}", "geometry-kernel", exe);
        return File.Exists(staged) ? staged : null;
    }

    public static string RecipeOcctVersion()
        => File.ReadAllLines(System.IO.Path.Combine(RepoRoot()!, "tools", "geometry-worker", "occt", "recipe.env"))
               .Single(l => l.StartsWith("OCCT_VERSION=", StringComparison.Ordinal))["OCCT_VERSION=".Length..];

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }
}
