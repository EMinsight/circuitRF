using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Tuning;
using CircuitRF.Ui.Tuning;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>brief-tuneopt-5 R-to5-2 / R-to5-7: recall is best effort. Every key is applied, skipped or
/// widened and said so; nothing throws.</summary>
public sealed class PresetRecallTests
{
    private static TuningPreset Preset(params (string Key, string Value)[] values)
    {
        var p = new TuningPreset { Name = "P" };
        foreach (var (k, v) in values) p.Values[k] = v;
        return p;
    }

    private static TunableCatalog Catalog(SchematicEditModel? top = null)
        => TunableCatalog.Discover(top ?? TuningFixture.Top(), TuningFixture.Resolver());

    private static SchematicEditModel WithZl(string expr = "40+15j")
    {
        var top = TuningFixture.Top();
        top.Components.Single(c => c.InstanceName == "VAR1").Parameters
           .Add(new EditableParameter { Name = "ZL", Expression = expr, Unit = "Ohm" });
        return top;
    }

    [Fact]
    public void AllPresent_AreApplied_AndTurnedOnForTuning()
    {
        var r = PresetRecall.Apply(Preset(("R1.R", "75 Ohm"), ("Wline", "250 um"), ("DUT:R3.R", "12 Ohm")), Catalog(), null);

        Assert.Equal("Applied 3 of 3", r.Summary);
        Assert.Equal(["R1.R", "Wline", "DUT:R3.R"], r.Values.Keys);
        Assert.All(r.Setup!.Variables, e => Assert.True(e.Tune));
    }

    [Fact]
    public void ADeletedInstance_IsSkipped_AndNamed()
    {
        var r = PresetRecall.Apply(Preset(("R1.R", "60 Ohm"), ("R7.R", "10 Ohm")), Catalog(), null);

        Assert.Equal("Applied 1 of 2 · R7 no longer exists", r.Summary);
        Assert.Equal(PresetRecallOutcome.Missing, r.Items.Single(i => i.Key == "R7.R").Outcome);
        Assert.False(r.Values.ContainsKey("R7.R"));
    }

    [Fact]
    public void ARenamedVariable_IsSkipped()
    {
        var r = PresetRecall.Apply(Preset(("Wold", "100 um")), Catalog(), null);

        Assert.Equal(PresetRecallOutcome.Missing, r.Items.Single().Outcome);
        Assert.Empty(r.Values);
        Assert.Null(r.Setup);
    }

    [Fact]
    public void AnOutOfRangeValue_IsApplied_AndItsRangeWidened()
    {
        var setup = new TuningSetup { Variables = { new TunableEntry { Key = "R1.R", Tune = true, Min = "25 Ohm", Max = "100 Ohm" } } };
        var r = PresetRecall.Apply(Preset(("R1.R", "150 Ohm")), Catalog(), setup);

        var item = r.Items.Single();
        Assert.Equal(PresetRecallOutcome.Widened, item.Outcome);
        Assert.Equal("Applied 1 of 1 · R1.R range widened", r.Summary);
        Assert.Equal(("25 Ohm", "150 Ohm"), (r.Setup!.Variables[0].Min, r.Setup.Variables[0].Max));
        Assert.Equal("100 Ohm", setup.Variables[0].Max);                     // the argument is left alone
    }

    [Fact]
    public void AParameterNowAnExpression_IsSkippedAsNoLongerTunable()
    {
        var r = PresetRecall.Apply(Preset(("R1.R", "50 Ohm")), Catalog(TuningFixture.Top(r1: "2*Wline")), null);

        Assert.Equal(PresetRecallOutcome.NotTunable, r.Items.Single().Outcome);
        Assert.Equal("Applied 0 of 1 · R1.R is no longer tunable", r.Summary);
    }

    [Fact]
    public void AWholeComplexValue_IsRecalledIntoBothPartRows()
    {
        var f = new TuningPanelFixture(extraVar: ("ZL", "40+15j", "Ohm"));
        f.Panel.SetTuned(["real(ZL)", "imag(ZL)"], on: true);
        var (setup, _) = TuningPresets.LockIn(f.Top.EditModel.Tuning, new Dictionary<string, string> { ["ZL"] = "30+52j Ohm" }, DateTime.UtcNow);
        f.Top.EditModel.Tuning = setup;
        f.Top.EditModel.NotifyChanged();

        f.Panel.Presets.Single().RecallCommand.Execute(null);

        Assert.Equal(30, f.Row("real(ZL)").Value, 9);
        Assert.Equal(52, f.Row("imag(ZL)").Value, 9);                        // widened: imag's default range ends at 22.5
        Assert.Equal("30+52j Ohm", f.Panel.CurrentValues()["ZL"]);
        Assert.Contains("ZL range widened", f.Panel.StatusText);
    }

    [Fact]
    public void AnOutOfRangeComplexValue_WidensThePartsRange()
    {
        var setup = new TuningSetup { Variables =
        {
            new TunableEntry { Key = "real(ZL)", Tune = true, Min = "20 Ohm", Max = "80 Ohm" },
            new TunableEntry { Key = "imag(ZL)", Tune = true, Min = "7.5 Ohm", Max = "30 Ohm" },
        } };
        var r = PresetRecall.Apply(Preset(("ZL", "30+52j Ohm")), Catalog(WithZl()), setup);

        Assert.Equal(PresetRecallOutcome.Widened, r.Items.Single().Outcome);
        Assert.Equal("52 Ohm", r.Setup!.Variables.Single(v => v.Key == "imag(ZL)").Max);
        Assert.Equal("20 Ohm", r.Setup.Variables.Single(v => v.Key == "real(ZL)").Min);
        Assert.False(ComplexRegion.Of(r.Setup, "ZL", "Ohm").IsEmpty);
        Assert.Equal("30+52j Ohm", r.Values["ZL"]);
    }

    [Fact]
    public void AnImpossiblePartPair_IsReportedAndSkipped()
    {
        var r = PresetRecall.Apply(Preset(("real(ZL)", "60 Ohm"), ("mag(ZL)", "50 Ohm")), Catalog(WithZl()), null);

        Assert.All(r.Items, i => Assert.Equal(PresetRecallOutcome.NotApplicable, i.Outcome));
        Assert.Equal("Applied 0 of 2 · real(ZL) is no longer applicable · 1 more", r.Summary);
        Assert.Empty(r.Values);
        Assert.Null(r.Setup);
    }
}
