using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-dfr27-5 R-dfr27-5-2 (openEMS's time, said before a run) and R-dfr27-5-3 (a run's sentences, posted as they are known).
// Arithmetic and counters only: no solver runs.

public sealed class OpenEmsTimeEstimateTests
{
    /// <summary>cells × steps × ports over 25 million cell updates a second per core, credited to eight cores at most.</summary>
    [Fact]
    public void TheEstimate_IsCellsTimesStepsTimesPorts_OverTheCreditedCores()
    {
        var g = Grid() with { Cells = 1_000_000, Steps = 100_000 };
        Assert.Equal(1e6 * 1e5 * 6 / (4 * 25e6), OpenEmsTimeEstimate.Seconds(g, 6, 4), 6);
        Assert.Equal(OpenEmsTimeEstimate.Seconds(g, 6, 8), OpenEmsTimeEstimate.Seconds(g, 6, 64));
        Assert.Equal(["30 s", "10 min", "2 hours", "3 days"],
                     new[] { 30.0, 600, 7200, 3 * 86_400 }.Select(OpenEmsTimeEstimate.Format));
    }

    /// <summary>
    /// The round-26 board's grid as it was (24.6 M cells, 4.7 M steps, six ports) on a four-core laptop: weeks, so the run is
    /// asked about, and the question names the smallest cell, what set it and the setting that relaxes it. A grid of
    /// seconds is a note.
    /// </summary>
    [Fact]
    public void ALongRun_IsAskedAbout_AndPastADayNamesTheSmallestCell()
    {
        var board = Grid() with { Cells = 24_615_350, Steps = 4_698_891 };
        var v = OpenEmsTimeEstimate.Verdict(board, 6, 4);
        Assert.Equal(Em3dRunConcern.Time, v.Concern);
        Assert.Equal(Em3dMemoryLevel.Severe, v.Level);
        Assert.Contains("days", v.Text);
        Assert.Contains("set by the smallest cell", v.Text);
        Assert.Contains("MinCellUm", v.Text);

        var small = OpenEmsTimeEstimate.Verdict(Grid(), 2, 4);
        Assert.Equal(Em3dMemoryLevel.Fits, small.Level);
        Assert.DoesNotContain("set by the smallest cell", small.Text);
    }

    /// <summary>The panel's answer decides a long run; with no one to ask (the CLI) it is a warning and the run goes on.</summary>
    [Fact]
    public void TheTimeQuestion_GoesThroughTheMemoryChecksConfirmation()
    {
        var longRun = OpenEmsTimeEstimate.Verdict(Grid() with { Cells = 24_615_350, Steps = 4_698_891 }, 6, 4);
        var shortRun = OpenEmsTimeEstimate.Verdict(Grid(), 2, 4);

        var cli = new Em3dRunService.RunLog();
        Assert.Null(new Em3dRunService.MemoryGate(null).AdmitTime(longRun, cli));
        Assert.Equal([longRun.Text], cli.Warnings);

        var declined = new Em3dRunService.RunLog();
        Assert.EndsWith("It was not started.", new Em3dRunService.MemoryGate(_ => false).AdmitTime(longRun, declined));
        Assert.Empty(declined.Warnings);

        var confirmed = new Em3dRunService.RunLog();
        Assert.Null(new Em3dRunService.MemoryGate(_ => true).AdmitTime(longRun, confirmed));
        Assert.EndsWith("started anyway, as confirmed.", Assert.Single(confirmed.Warnings));

        var quick = new Em3dRunService.RunLog();
        bool asked = false;
        Assert.Null(new Em3dRunService.MemoryGate(_ => asked = true).AdmitTime(shortRun, quick));
        Assert.False(asked);
        Assert.Equal([shortRun.Text], quick.Notes);
    }

    /// <summary>
    /// R-dfr27-5-3 — every note and warning reaches the run's control the moment it is added, in order, carrying the file it
    /// names; the result's lists still hold all of them.
    /// </summary>
    [Fact]
    public void ARunsSentences_ArePostedAsTheyAreAdded()
    {
        var posted = new List<RunNote>();
        var log = new Em3dRunService.RunLog(new RunControl { Notes = posted.Add });
        log.Notes.Add("Solver: openEMS.");
        log.Files["Too big."] = "/a/.wslconfig";
        log.Warnings.Add("Too big.");
        log.Notes.AddRange(["port 1 on its line.", "port 2 on its line."]);

        Assert.Equal(["Solver: openEMS.", "Too big.", "port 1 on its line.", "port 2 on its line."], posted.Select(n => n.Text));
        Assert.Equal([false, true, false, false], posted.Select(n => n.Warning));
        Assert.Equal("/a/.wslconfig", posted[1].File);
        Assert.Equal(3, log.Notes.Count);
        Assert.Single(log.Warnings);
    }

    /// <summary>
    /// R-dfr27-5-4 — what a quiet openEMS is said to be doing: before its first step, building its operator for the grid's
    /// cells; after one, probably swapping. Either way Cancel keeps nothing. And the step its probes have reached, for when its
    /// own lines do not arrive: the earliest probe's last time over the time step.
    /// </summary>
    [Fact]
    public void AQuietOpenEms_IsSaidToBeQuiet_AndItsProbesGiveTheStep()
    {
        string building = OpenEmsRun.QuietSentence(0, 6, 0, 24_615_350, TimeSpan.FromMinutes(2));
        Assert.Contains("port 1 of 6", building);
        Assert.Contains("building its operator for 24.6 million cells", building);
        Assert.Contains("nothing from this run is kept yet", building);
        Assert.Contains("swapping", OpenEmsRun.QuietSentence(2, 6, 1_000, 24_615_350, TimeSpan.FromMinutes(2)));

        Assert.Equal(300, OpenEmsRun.ProbeStep([4e-12, 3e-12, 5e-12], 1e-14));
        Assert.Null(OpenEmsRun.ProbeStep([4e-12, 0], 1e-14));
        Assert.Null(OpenEmsRun.ProbeStep([4e-12], 0));
    }

    /// <summary>A small grid, built: the microstrip OpenEmsBackendTests writes.</summary>
    private static FdtdGridResult Grid()
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        setup.Solver3D = Em3dSolverId.OpenEms;
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        return FdtdGrid.Build(r.Problem!, CemOpenEms.ResolveGrid(setup.OpenEms), long.MaxValue);
    }
}
