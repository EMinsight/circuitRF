using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-dfr27-5 R-dfr27-5-5 — when Palace will not fit, the question offers what would, each with its estimate; when nothing
// does, it says so. Arithmetic only: the process scaling was measured once, on the round-26 board (Em3dSizeEstimate).

public sealed class PalaceFitRemedyTests
{
    private const double GB = 1e9;

    /// <summary>The process scaling lies on or above every one-excitation peak measured (1, 2, 4 and 8 processes: 2.5, 3.0, 3.5
    /// and 4.3 GB) and is exact at the two it was drawn through.</summary>
    [Fact]
    public void TheProcessFactor_LiesOnOrAboveEveryMeasuredPeak()
    {
        foreach (var (n, gb) in new[] { (1, 2.5), (2, 3.0), (4, 3.5), (8, 4.3) })
            Assert.True(Em3dSizeEstimate.PalaceProcessFactor(n) * 4.3 >= gb - 1e-9, $"{n} processes");
        Assert.Equal(1.0, Em3dSizeEstimate.PalaceProcessFactor(8), 12);
        Assert.Equal(2.5 / 4.3, Em3dSizeEstimate.PalaceProcessFactor(1), 12);
    }

    /// <summary>
    /// The designer's run: the board's 148,988 tetrahedra at Draft on eight processes, in a 3.8 GB Linux subsystem. One
    /// process is offered with its estimate and does not fit; the crop is sized; the question says no setting fits and only
    /// a smaller crop would, and its default is not to run.
    /// </summary>
    [Fact]
    public void TheDesignersRun_NoSettingFits_AndTheQuestionSaysSo()
    {
        var draft = PalaceSettings.Preset(PalaceQuality.Draft);
        var v = Em3dRunService.MeshMemoryVerdict(148_988, draft, (long)(3.8 * GB), processes: 8);
        Assert.Equal(Em3dMemoryLevel.Severe, v.Level);
        var fewer = Assert.Single(v.Remedies, r => r.Processes is not null);
        Assert.Equal(1, fewer.Processes);
        Assert.False(fewer.Fits(v.PhysicalBytes));
        Assert.Single(v.Remedies, r => r.Sized);
        Assert.True(v.NothingFits);
        Assert.Empty(v.Applicable);
        Assert.Contains("No setting fits it", v.Text);
        Assert.Contains("only a smaller crop would", v.Text);
    }

    /// <summary>
    /// On a machine where fewer processes do fit, the most that fit are offered as something the run takes itself; chosen,
    /// the run's gate holds that count and the run keeps its memory ceiling (it was not forced).
    /// </summary>
    [Fact]
    public void FewerProcessesThatFit_AreOffered_AndChosenTheRunTakesThem()
    {
        var draft = PalaceSettings.Preset(PalaceQuality.Draft);
        long e8 = Em3dSizeEstimate.PalaceMemoryBytesForMesh(148_988, 1, 0);
        long physical = (long)(e8 * 0.65);    // eight processes are past 150 % of it; one fits
        var v = Em3dRunService.MeshMemoryVerdict(148_988, draft, physical, processes: 8);
        var offered = Assert.Single(v.Applicable);
        Assert.True(offered.Processes < 8);
        Assert.True(offered.EstimateBytes <= physical);
        Assert.True(offered.Processes == 7 ||
                    e8 * Em3dSizeEstimate.PalaceProcessFactor(offered.Processes!.Value + 1) > physical);   // the most that fit
        Assert.False(v.NothingFits);

        var gate = new Em3dRunService.MemoryGate(q => { q.Choice.Remedy = q.Applicable[0]; return true; });
        var log = new Em3dRunService.RunLog();
        Assert.Null(gate.Admit(v, log));
        Assert.Equal(offered.Processes, gate.ProcessCap);
        Assert.False(gate.Forced);
        Assert.Contains("as chosen", Assert.Single(log.Warnings));
    }
}
