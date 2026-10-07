using System.Linq;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-4 R-to4-3: Add…, the Inspector toggle and the canvas's Tune set the same flag, each in one undo step.</summary>
public sealed class TunableActivationTests
{
    private static void AssertTunedOnceThenUndone(TuningPanelFixture f)
    {
        var entry = Assert.Single(f.Top.EditModel.Tuning!.Variables);
        Assert.Equal(("R1.R", true, "25 Ohm", "100 Ohm"), (entry.Key, entry.Tune, entry.Min, entry.Max));
        Assert.Equal("R1.R", Assert.Single(f.Panel.Rows).Key);
        Assert.True(f.Top.UndoRedo.IsModified);

        f.Top.UndoRedo.Undo();
        Assert.Null(f.Top.EditModel.Tuning);
        Assert.False(f.Top.UndoRedo.CanUndo);          // it was ONE step
        Assert.Empty(f.Panel.Rows);
    }

    [Fact]
    public void AddPopup_SetsTheFlag_InOneUndoStep()
    {
        var f = new TuningPanelFixture();
        f.Panel.Add.IsOpen = true;
        f.Panel.Add.SearchText = "r1";
        Assert.Equal("R1.R", Assert.Single(f.Panel.Add.Results).Key);
        f.Panel.Add.AddSelectedCommand.Execute(null);   // Enter with nothing selected adds the first row
        AssertTunedOnceThenUndone(f);
    }

    [Fact]
    public void InspectorToggle_SetsTheFlag_InOneUndoStep()
    {
        var f = new TuningPanelFixture();
        var r1 = f.Top.EditModel.Components[0];
        var row = new ParameterRowViewModel(r1.Parameters[0], f.Top, r1.Symbol, r1);
        Assert.True(row.CanTune);
        row.IsTuned = true;
        AssertTunedOnceThenUndone(f);
    }

    [Fact]
    public void CanvasTune_SetsTheFlag_InOneUndoStep()
    {
        var f = new TuningPanelFixture();
        var r1 = f.Top.EditModel.Components[0];
        // The right-click's path: the session's surface names the clicked value, then sets it.
        var key = f.Top.Tuning!.KeyFor(f.Top.EditModel, r1, r1.Parameters[0]);
        Assert.Equal("R1.R", key);
        f.Top.Tuning.SetTuned(key!, true);
        AssertTunedOnceThenUndone(f);
    }

    [Fact]
    public void AnExpressionParameter_HasNoToggle_AndIsNotInAdd()
    {
        var f = new TuningPanelFixture();
        var varRow = f.Top.EditModel.Components[1];
        var wtot = new EditableParameter { Name = "Wtot", Expression = "Wline*2" };
        varRow.Parameters.Add(wtot);
        f.Top.EditModel.NotifyChanged();

        Assert.False(new ParameterRowViewModel(wtot, f.Top, varRow.Symbol, varRow).CanTune);

        f.Panel.Add.IncludeSubCells = true;
        f.Panel.Add.IsOpen = true;
        var offered = f.Panel.Add.Results.Select(r => r.Key).ToList();
        Assert.Contains("Wline", offered);
        Assert.DoesNotContain("Wtot", offered);
        Assert.DoesNotContain("DUT:R4.R", offered);     // R = Rbias reads a cell parameter
    }
}
