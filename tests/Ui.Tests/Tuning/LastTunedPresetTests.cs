using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Tuning;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-5 R-to5-5 (overview D6): every save and close of the tuned schematic calls
/// <see cref="TuningPanelViewModel.StoreLastTuned"/>, which writes the single "Last tuned" preset when
/// the session's values differ from the schematic — and only then.</summary>
public sealed class LastTunedPresetTests
{
    private static void Type(TuningRowViewModel row, string text)
    {
        row.ValueBoxText = text;
        row.CommitValueText();
    }

    [Fact]
    public void Saving_WithValuesThatDiffer_WritesLastTuned_AndDirtiesTheDocument()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned(["R1.R", "Wline"], on: true);
        f.Top.UndoRedo.MarkSaved();
        Type(f.Row("R1.R"), "75");

        Assert.True(f.Panel.StoreLastTuned());

        var p = f.Top.EditModel.Tuning!.Presets.Single();
        Assert.True(p.IsLastTuned);
        Assert.Equal(TuningPreset.LastTunedName, p.Name);
        Assert.Equal(["R1.R", "Wline"], p.Values.Keys);               // every tuned entry, not just the moved one
        Assert.Equal(("75 Ohm", "300 um"), (p.Values["R1.R"], p.Values["Wline"]));
        Assert.True(f.Top.UndoRedo.IsModified);
    }

    [Fact]
    public void Saving_WithValuesEqualToTheSchematic_WritesNothing()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned(["R1.R"], on: true);

        Assert.False(f.Panel.StoreLastTuned());
        Assert.Empty(f.Top.EditModel.Tuning!.Presets);
    }

    [Fact]
    public void ASecondSave_OverwritesLastTuned_NeverDuplicatesIt()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned(["R1.R"], on: true);
        Type(f.Row("R1.R"), "75");
        f.Panel.StoreLastTuned();
        f.Panel.LockInCommand.Execute(null);                           // an ordinary preset beside it

        Type(f.Row("R1.R"), "80");
        Assert.True(f.Panel.StoreLastTuned());
        Assert.False(f.Panel.StoreLastTuned());                        // unchanged since: nothing to write

        var presets = f.Top.EditModel.Tuning!.Presets;
        Assert.Equal("80 Ohm", presets.Single(p => p.IsLastTuned).Values["R1.R"]);
        Assert.Equal(2, presets.Count);
        Assert.Equal(TuningPreset.LastTunedName, f.Panel.Presets[0].Name);   // pinned at the top
    }
}
