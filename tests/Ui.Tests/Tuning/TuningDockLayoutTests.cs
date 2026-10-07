using System.Linq;
using CircuitRF.Ui.Docking;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-4 R-to4-1: Tuning is tabbed behind Analyses by default and in a migrated older layout.</summary>
public sealed class TuningDockLayoutTests
{
    private static void AssertBehindAnalyses(CwsDockLayout layout)
    {
        var a = layout.Panels.Single(p => p.Id == DockPanelIds.Analyses);
        var t = layout.Panels.Single(p => p.Id == DockPanelIds.Tuning);
        Assert.Equal((a.Side, a.Group, a.Inboard), (t.Side, t.Group, t.Inboard));
        Assert.True(t.Order > a.Order);
        Assert.False(t.Active);
    }

    [Fact]
    public void BothDefaults_PutTuningBehindAnalyses()
    {
        Assert.Contains(DockPanelIds.Tuning, DockPanelIds.All);
        AssertBehindAnalyses(DockLayoutDefaults.Default());
        AssertBehindAnalyses(DockLayoutDefaults.ProjectTreeAndLibrary());
    }

    [Fact]
    public void AnOlderLayout_GainsTuning_BesideWhereverItsAnalysesIs()
    {
        var old = DockLayoutDefaults.ProjectTreeAndLibrary();
        old.Panels.RemoveAll(p => p.Id == DockPanelIds.Tuning);
        AssertBehindAnalyses(DockLayoutDefaults.WithMissingPanelsFilled(old));

        // The user had moved Analyses to the right column, behind the Library.
        var moved = DockLayoutDefaults.ProjectTreeAndLibrary();
        moved.Panels.RemoveAll(p => p.Id == DockPanelIds.Tuning);
        var analyses = moved.Panels.Single(p => p.Id == DockPanelIds.Analyses);
        (analyses.Side, analyses.Group, analyses.Order) = (DockSide.Right, 0, 1);
        var filled = DockLayoutDefaults.WithMissingPanelsFilled(moved);
        AssertBehindAnalyses(filled);
        Assert.Equal(DockSide.Right, filled.Panels.Single(p => p.Id == DockPanelIds.Tuning).Side);
    }
}
