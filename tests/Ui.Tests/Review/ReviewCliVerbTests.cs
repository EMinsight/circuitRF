// brief-review-2 §2 — `circuitrf review` is a command line onto LayoutReview.Run and nothing else: the
// verb run as a PROCESS reports the findings the in-process call makes, in the same order; a length
// threshold given as a bare number is refused; and the exit code is 0 when it ran, 1 with --severity
// warning while an unaccepted warning remains.

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;
using CircuitRF.Ui.Tests.Lvs;

namespace CircuitRF.Ui.Tests.Review;

[Collection(LvsCliConsoleCollection.Name)]
public sealed class ReviewCliVerbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-review-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a temp folder that outlives the run is not a failure */ }
    }

    /// <summary>The run test's board on disk: copper 0.1 mm from the edge, one finding.</summary>
    private string Clay()
    {
        Directory.CreateDirectory(_dir);
        var tech = ReviewRunTests.Tech();
        TechPersistence.SaveToFile(Path.Combine(_dir, "board.ctech"), tech);
        var view = ReviewRunTests.Board(tech, 100_000);
        view.TechRef = "board.ctech";
        string clay = Path.Combine(_dir, "board.clay");
        LayoutPersistence.SaveToFile(clay, view);
        return clay;
    }

    [Fact]
    public void TheVerbRunAsAProcess_ReportsExactlyWhatLayoutReviewRunReports()
    {
        string clay = Clay();
        var direct = LayoutReview.Run(TraceImpedanceAnalysis.LoadLayout(clay));

        var (exit, stdout, _) = RunCli("review", clay, "--json");

        Assert.Equal(0, exit);
        var review = JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetProperty("review");
        var viaProcess = review.GetProperty("findings").EnumerateArray()
            .Select(f => (f.GetProperty("check").GetString(), f.GetProperty("key").GetString(), f.GetProperty("sentence").GetString()))
            .ToList();
        var inProcess = direct.Findings.Select(f => ((string?)f.CheckId, (string?)f.Key, (string?)f.Sentence)).ToList();

        Assert.NotEmpty(inProcess);
        Assert.Equal(inProcess, viaProcess);
        Assert.Equal(direct.TechnologyName, review.GetProperty("technology").GetString());
        Assert.Equal(direct.WarningCount, review.GetProperty("warnings").GetInt32());
    }

    [Fact]
    public void ABareNumberForALength_IsRefused()
    {
        string clay = Clay();

        Assert.Equal(1, InProcess("review", clay, "--set", "edge-copper.pullback=0.3", "--json"));
        Assert.Contains("review.args.set-needs-unit", _last, StringComparison.Ordinal);

        // With its unit it is read: at 0.05 mm the copper 0.1 mm from the edge is no longer a finding.
        Assert.Equal(0, InProcess("review", clay, "--set", "edge-copper.pullback=0.05mm", "--json"));
        Assert.Equal(0, Review(_last).GetProperty("warnings").GetInt32());
    }

    [Fact]
    public void TheExitCode_IsZeroWhenItRan_AndOneWithSeverityWarningWhileAWarningRemains()
    {
        string clay = Clay();

        Assert.Equal(0, InProcess("review", clay));
        Assert.Contains("edge-copper: 1 finding(s)", _last, StringComparison.Ordinal);
        Assert.Equal(1, InProcess("review", clay, "--severity", "warning"));
        Assert.Equal(0, InProcess("review", clay, "--severity", "warning", "--skip", "edge-copper"));
    }

    private static JsonElement Review(string document) =>
        JsonDocument.Parse(document).RootElement.GetProperty("result").GetProperty("review");

    private string _last = "";

    private int InProcess(params string[] args)
    {
        var real = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            JsonRun.Reset();
            int exit = CliEntry.Run(args);
            _last = buffer.ToString();
            return exit;
        }
        finally { Console.SetOut(real); }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ReviewCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
