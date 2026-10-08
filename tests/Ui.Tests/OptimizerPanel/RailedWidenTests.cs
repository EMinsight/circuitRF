using System;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Optimization;
using Xunit;

namespace CircuitRF.Ui.Tests.OptimizerPanel;

/// <summary>Overview D17 in the Optimizer panel: a best value at a bound is marked railed, and Widen
/// doubles the span on that side as one undo step.</summary>
public sealed class RailedWidenTests
{
    [Fact]
    public async Task AGoalOutsideTheRange_RailsAtMax_AndWidenDoublesThatSide_AsOneUndoStep()
    {
        var f = OptimizerPanelFixture.OneVariable("0", "20", "simplex", 100,
            OptimizerPanelFixture.Goal("G", "A", GoalType.Eq, "50"));
        f.Panel.RunCommand.Execute(null);
        await f.Panel.RunTask!.WaitAsync(TimeSpan.FromSeconds(20));

        var row = f.Row("A");
        Assert.True(row.IsRailedMax, $"best {row.Best} did not rail");
        Assert.Equal("max", row.RailedText);
        Assert.False(f.Top.UndoRedo.CanUndo);

        row.WidenCommand.Execute(null);

        var entry = f.Top.EditModel.Tuning!.Variables.Single(v => v.Key == "A");
        Assert.Equal(("0", "40"), (entry.Min, entry.Max));        // linear: the span again, above
        f.Top.UndoRedo.Undo();
        Assert.Equal("20", f.Top.EditModel.Tuning!.Variables.Single(v => v.Key == "A").Max);
        Assert.False(f.Top.UndoRedo.CanUndo);                       // it was ONE step
    }

    [Fact]
    public void OnALogRange_WidenDoublesTheRatio()
    {
        Assert.Equal((1.0, 100.0), OptimizerPanelViewModel.WidenedRange(1, 10, RailEnd.Max, log: true));
        Assert.Equal((0.1, 10.0), OptimizerPanelViewModel.WidenedRange(1, 10, RailEnd.Min, log: true));
    }
}

/// <summary>brief-tuneopt-10 R-to10-1: the Optimizer is tabbed behind Tuning by default and in a migrated layout.</summary>
public sealed class OptimizerDockLayoutTests
{
    [Fact]
    public void BothDefaults_PutTheOptimizerBehindTuning()
    {
        Assert.Contains(DockPanelIds.Optimizer, DockPanelIds.All);
        foreach (var layout in new[] { DockLayoutDefaults.Default(), DockLayoutDefaults.ProjectTreeAndLibrary() })
        {
            var t = layout.Panels.Single(p => p.Id == DockPanelIds.Tuning);
            var o = layout.Panels.Single(p => p.Id == DockPanelIds.Optimizer);
            Assert.Equal((t.Side, t.Group, t.Inboard), (o.Side, o.Group, o.Inboard));
            Assert.True(o.Order > t.Order);
            Assert.False(o.Active);
        }

        var old = DockLayoutDefaults.ProjectTreeAndLibrary();
        old.Panels.RemoveAll(p => p.Id == DockPanelIds.Optimizer);
        var filled = DockLayoutDefaults.WithMissingPanelsFilled(old);
        var tuning = filled.Panels.Single(p => p.Id == DockPanelIds.Tuning);
        var opt    = filled.Panels.Single(p => p.Id == DockPanelIds.Optimizer);
        Assert.Equal((tuning.Side, tuning.Group), (opt.Side, opt.Group));
    }
}
