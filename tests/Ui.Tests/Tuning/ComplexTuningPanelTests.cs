using System;
using System.Linq;
using CircuitRF.Ui.Tuning;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>Overview D18 in the Tuning panel: part rows are views of one complex value, a move stops at
/// the edge of every part's range, a conflicting range is refused, and Push writes the value whole.</summary>
public sealed class ComplexTuningPanelTests
{
    private static TuningPanelFixture Fixture(string expr = "40+15j")
        => new(extraVar: ("ZL", expr, "Ohm"));

    private static void Type(TuningRowViewModel row, string text)
    {
        row.ValueBoxText = text;
        row.CommitValueText();
    }

    [Fact]
    public void MovingTheRealPart_HoldsTheImaginary_UpdatesTheMagnitudeRow_AndStopsAtItsLimit()
    {
        var f = Fixture();
        f.Panel.SetTuned(["real(ZL)", "mag(ZL)"], on: true);
        var real = f.Row("real(ZL)");
        var mag  = f.Row("mag(ZL)");
        mag.MaxText = "50";
        mag.CommitMaxText();

        Type(real, "30");
        Assert.Equal(Math.Sqrt(30 * 30 + 15 * 15), f.Row("mag(ZL)").Value, 9);

        Type(f.Row("real(ZL)"), "75");                       // inside real's own range, outside mag's
        Assert.Equal(Math.Sqrt(50 * 50 - 15 * 15), f.Row("real(ZL)").Value, 6);
        Assert.Equal(50, f.Row("mag(ZL)").Value, 6);
        Assert.Equal(["ZL"], f.Panel.CurrentValues().Keys.Where(k => k.Contains("ZL")));   // whole, once
    }

    [Fact]
    public void ARangeThatLeavesNoValue_IsRefused_AndChangesNothing()
    {
        var f = Fixture();
        f.Panel.SetTuned(["real(ZL)", "mag(ZL)"], on: true);
        var real = f.Row("real(ZL)");
        real.MaxText = "500";
        real.CommitMaxText();
        var before = f.Top.EditModel.Tuning!.Variables.Single(v => v.Key == "real(ZL)").Min;
        long stamp = f.Top.UndoRedo.TopUndoStamp;

        real = f.Row("real(ZL)");
        real.MinText = "200";                                // mag's default range ends at 85.44 Ohm
        real.CommitMinText();

        Assert.StartsWith("Refused: real(ZL)'s range leaves no value of ZL", f.Panel.StatusText);
        Assert.Equal(before, f.Top.EditModel.Tuning!.Variables.Single(v => v.Key == "real(ZL)").Min);
        Assert.Equal(stamp, f.Top.UndoRedo.TopUndoStamp);   // no undo step was added
    }

    [Fact]
    public void Push_WritesTheWholeValue_InTheFormTheSchematicWroteIt()
    {
        var f = Fixture("polar(50,30)");
        f.Panel.SetTuned(["phase(ZL)"], on: true);
        Type(f.Row("phase(ZL)"), "45");

        f.Panel.PushCommand.Execute(null);

        var zl = f.Top.EditModel.Components.SelectMany(c => c.Parameters).Single(p => p.Name == "ZL");
        Assert.Equal(("polar(50,45)", "Ohm"), (zl.Expression, zl.Unit));
        Assert.Equal("Pushed 1 value", f.Panel.StatusText);
        Assert.False(f.Row("phase(ZL)").DiffersFromSchematic);
    }
}
