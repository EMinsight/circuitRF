using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Optimization;
using Xunit;

namespace CircuitRF.Ui.Tests.OptimizerPanel;

/// <summary>brief-tuneopt-10 R-to10-4: the goal editor's template, analysis, type and validation rules.</summary>
public sealed class GoalEditorTests
{
    private static GoalEditorViewModel Editor(OptimizationGoal? goal = null)
    {
        const string cnl = """
            Port:P1 in  0 Num=1 Z=50 Ohm
            R:R1    in  out R=10 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=11 Unit=GHz
            analysis SP2 type=sparam start=3 stop=4 npts=11 Unit=GHz
            """;
        var tb = PreparedCircuit.FromText(cnl, null, null).Tb!;
        return new GoalEditorViewModel(GoalTemplates.For(tb), [.. tb.Analyses], [.. tb.Measurements], goal);
    }

    [Fact]
    public void ATemplate_FillsTheFields_AndItsChoicesRefillThem()
    {
        var e = Editor();
        e.SelectedTemplate = e.Sections.SelectMany(s => s.Templates).First(t => t.Id == "SP2.sij");

        Assert.Equal("dB(SP2.S(2, 1))", e.Expression);
        Assert.Equal("SP2", e.Analysis);
        Assert.Equal(("freq", "3 GHz", "4 GHz", false), (e.Axis, e.RangeLo, e.RangeHi, e.WholeRange));
        Assert.True(e.IsGe);                                         // a transmission: at least

        e.TemplateFields.Single(f => f.Field.Key == "i").Value = "1";
        Assert.Equal("dB(SP2.S(1, 1))", e.Expression);
        Assert.True(e.IsLe);                                         // a reflection: at most
    }

    [Fact]
    public void TheAnalysis_FollowsTheExpression_UntilOneIsChosen()
    {
        var e = Editor();
        e.Expression = "mag(SP2.S(2, 1))";
        Assert.Equal("SP2", e.Analysis);
        e.Expression = "mag(SP1.S(2, 1))";
        Assert.Equal("SP1", e.Analysis);

        e.Analysis = "SP2";                                          // chosen: it stays
        e.Expression = "dB(SP1.S(1, 1))";
        Assert.Equal("SP2", e.Analysis);
    }

    [Fact]
    public void TheTypeToggles_DecideWhichLimitsTheGoalCarries()
    {
        var e = Editor(new OptimizationGoal { Name = "G", Expression = "dB(SP1.S(2, 1))", Analysis = "SP1",
                                              Type = GoalType.Ge, Limit = "-1" });
        e.Sloped = true;
        e.LimitAtHi = "-2";
        var sloped = e.Build();
        Assert.Equal((GoalType.Ge, "-1", "-2", (string?)null), (sloped.Type, sloped.Limit, sloped.LimitAtHi, sloped.UpperLimit));

        e.IsIn = true;                                              // a band: two edges, no slope
        e.UpperLimit = "0";
        Assert.True(e.IsBand);
        Assert.False(e.ShowsLimitAtHi);
        var band = e.Build();
        Assert.Equal((GoalType.In, "-1", "0", (string?)null), (band.Type, band.Limit, band.UpperLimit, band.LimitAtHi));

        e.IsLe = true;                                              // back to one limit: the band edge goes
        var le = e.Build();
        Assert.Equal((GoalType.Le, (string?)null, "-2"), (le.Type, le.UpperLimit, le.LimitAtHi));
    }

    [Fact]
    public void AnInvalidExpression_ShowsTheEnginesOwnMessage_AndCannotBeCommitted()
    {
        var e = Editor();
        e.Expression = "dB(SP1.S(2, 1)";
        Assert.Equal(GoalTemplates.Validate("dB(SP1.S(2, 1)"), e.ExpressionError);
        Assert.True(e.HasExpressionError);

        var committed = new List<OptimizationGoal>();
        e.Committed += committed.Add;
        Assert.False(e.TryCommit());
        Assert.Empty(committed);
        Assert.Equal(e.ExpressionError, e.CommitError);
    }
}
