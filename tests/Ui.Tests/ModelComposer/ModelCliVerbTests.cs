using CircuitRF.Cli;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-13 §4: the <c>model</c> verb run as a PROCESS writes the bytes the in-process functions write —
/// <c>new</c>, <c>import</c>, <c>fit --step all --seed 1</c>, <c>export</c> and <c>check-charge</c> on the synthetic device's
/// raw files — and its exit codes are D16's: 3 for a planted non-conservative device, 130 for a cancelled run that wrote
/// nothing.
/// </summary>
[Collection(CircuitRF.Ui.Tests.Lvs.LvsCliConsoleCollection.Name)]
public sealed class ModelCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crf-mc13-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const string Created = "2026-10-19T00:00:00Z";

    [Fact]
    public void TheVerbAsAProcess_WritesTheInProcessFunctionsBytes()
    {
        string a = Path.Combine(_root, "cli"), b = Path.Combine(_root, "inproc");
        ModelCliFixture.WriteRaw(a);
        ModelCliFixture.WriteRaw(b);
        string ca = Path.Combine(a, "fet.cmc"), cb = Path.Combine(b, "fet.cmc");

        Cli(a, 0, "model", "new", "fet.cmc", "--kind", "active", "--network", "gan-two-shell", "--fingers", "4", "--unit-width", "125 um");
        ModelCommands.New(cb, CmcKind.Active, "gan-two-shell", new BasePeriphery(4, "125 um"));
        SameFiles(a, b);

        Cli(a, 0, ["model", "import", "fet.cmc", .. ModelCliFixture.ImportS]);
        Cli(a, 0, ["model", "import", "fet.cmc", .. ModelCliFixture.ImportDc]);
        ModelCommands.Import(cb, ModelCliFixture.SSet(cb));
        ModelCommands.Import(cb, ModelCliFixture.DcSet);
        SameFiles(a, b);

        ModelCliFixture.Quick(ca);
        ModelCliFixture.Quick(cb);
        var fit = Cli(a, 0, "model", "fit", "fet.cmc", "--step", "all", "--seed", "1");
        var lines = ModelCommands.Fit(cb, "all", new FitOptions(Seed: 1));
        Assert.Equal(lines.Select(l => l.Text), StdoutLines(fit.StdOut));
        SameFiles(a, b);

        Cli(a, 0, "model", "export", "fet.cmc", "-o", "fet.cmodel", "--created", Created);
        CmodelExport.Export(cb, Path.Combine(b, "fet.cmodel"), created: Created);
        SameFiles(a, b);

        var check = Cli(a, 0, "model", "check-charge", "fet.cmc");
        var report = ConservationCheck.RunFile(cb, new LargeSignalSettings());
        Assert.Equal(report.Lines, StdoutLines(check.StdOut).Take(report.Lines.Count));
    }

    [Fact]
    public void APlantedNonConservativeDevice_Exits3_AndPassesAboveItsReading()
    {
        string cnl = Path.Combine(_root, "dut.cnl");
        File.WriteAllText(cnl, ComposedTestModels.SddGlobals + ConservationCheckLargeSignalTests.CapacitanceDevice);
        // Measured in MC-8: 0.41 % of the fundamental gate current — above the 0.1 % default, below 1 %.
        var failed = Cli(_root, 3, "model", "check-charge", "dut.cnl", "--device", "X1", "--bias", "-0.8,1.5");
        Assert.Contains("FAILED", failed.StdOut, StringComparison.Ordinal);
        Cli(_root, 0, "model", "check-charge", "dut.cnl", "--device", "X1", "--bias", "-0.8,1.5", "--threshold", "1%");
    }

    [Fact]
    public void ACancelledRun_Exits130_AndWritesNothing()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        string model = Path.Combine(dir, "fet.cmodel");
        var before = Snapshot(dir);

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            using (RunHost.Install(cts.Token, observer: null))
            {
                Assert.Equal(130, InProcess("model", "fit", cmc, "--step", "all", "--seed", "7"));
                Assert.Equal(130, InProcess("model", "export", cmc, "-o", model, "--created", Created));
            }
        }
        Assert.Equal(before, Snapshot(dir));

        // Not vacuous: the same export with no cancellation writes the model.
        Assert.Equal(0, InProcess("model", "export", cmc, "-o", model, "--created", Created));
        Assert.True(File.Exists(model));
    }

    /// <summary>Designer round 28: a passive block has no bias, so its Touchstone imports with no --pattern — the dialog's
    /// empty pattern. An active document still needs one.</summary>
    [Fact]
    public void APassiveDocument_ImportsATouchstoneWithNoPattern_AndAnActiveOneIsStillAsked()
    {
        string s6p = Path.Combine(_root, "board.s6p");
        var f = new StringWriter();
        f.WriteLine("# Hz S RI R 50");
        foreach (double hz in new[] { 1e9, 2e9, 3e9, 4e9 })
        {
            f.Write(hz.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < 6; i++)
            {
                for (int j = 0; j < 6; j++) f.Write(i == j ? " 0.1 0" : (i ^ 1) == j ? " 0.9 0" : " 0 0");
                f.WriteLine();
            }
        }
        File.WriteAllText(s6p, f.ToString());
        string passive = Path.Combine(_root, "board.cmc"), active = Path.Combine(_root, "fet.cmc");
        ModelCommands.New(passive, CmcKind.Passive);
        ModelCommands.New(active, CmcKind.Active);

        Assert.Equal(0, InProcess("model", "import", passive, s6p, "--as", "PassiveSParams"));
        Assert.Null(Assert.Single(CmcFile.Read(passive).DataSets).Import.Pattern);
        Assert.NotEqual(0, InProcess("model", "import", active, s6p, "--as", "SParams"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private (int ExitCode, string StdOut, string StdErr) Cli(string cwd, int expected, params string[] args)
    {
        var run = ModelCliFixture.Run(cwd, args);
        output.WriteLine($"$ {string.Join(' ', args)}  → {run.ExitCode}\n{run.StdOut}{run.StdErr}");
        Assert.Equal(expected, run.ExitCode);
        return run;
    }

    private static int InProcess(params string[] args)
    {
        JsonRun.Reset();
        return CliEntry.Run(args);
    }

    private static IReadOnlyList<string> StdoutLines(string stdout) =>
        [.. stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)];

    /// <summary>Both trees hold the same files with the same bytes — the cache index aside, which records each source's
    /// modification time.</summary>
    private static void SameFiles(string a, string b)
    {
        var files = ModelCliFixture.Files(a);
        Assert.Equal(files, ModelCliFixture.Files(b));
        foreach (string f in files)
            Assert.True(File.ReadAllBytes(Path.Combine(a, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, f))),
                        $"{f} differs between the CLI and the in-process call.");
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                 .ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
}
