using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tuning;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>The panels' digits setting: the spelling of a tuned value — shown, simulated and pushed —
/// chosen per schematic and kept in its <c>.csch</c>.</summary>
public sealed class TuningDigitsTests
{
    [Fact]
    public void Choosing_Digits_RespellsTheValues_AsOneUndoStep_AndTheCschKeepsIt()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned("R1.R", true);
        var row = f.Row("R1.R");
        row.ValueBoxText = "51.23456789";
        row.CommitValueText();
        Assert.Equal("51.2346 Ohm", row.ValueText);                 // six figures until one is chosen

        f.Top.UndoRedo.MarkSaved();
        f.Panel.SetDigitsCommand.Execute(3);
        Assert.True(f.Top.UndoRedo.IsModified);
        Assert.Equal("51.2 Ohm", row.ValueText);
        Assert.Equal("51.2 Ohm", f.Panel.CurrentValues()["R1.R"]);  // what the session simulates and Push writes
        Assert.Single(f.Panel.DigitsChoices, c => c.IsChecked && c.Digits == 3);

        var (back, _, _) = SchematicPersistence.Deserialize(SchematicPersistence.Serialize(f.Top.EditModel, "tb"));
        Assert.Equal(3, back.Tuning!.Digits);

        f.Panel.SetDigitsCommand.Execute(TuningDigits.Default);      // the default is written as nothing
        Assert.Null(f.Top.EditModel.Tuning!.Digits);
    }

    [Theory]
    [InlineData("1.83456789 pF", 3, "1.83 pF")]
    [InlineData("30.1234+52.5678j Ohm", 4, "30.12+52.57j Ohm")]
    [InlineData("R0 * 2", 3, "R0 * 2")]                               // not a value: unchanged
    public void Round_RespellsAValue_InItsOwnUnitAndForm(string text, int digits, string expected)
        => Assert.Equal(expected, TuningDigits.Round(text, digits));
}
