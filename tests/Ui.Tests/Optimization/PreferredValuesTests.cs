using CircuitRF.Core.Design;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Smith;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>
/// brief-tuneopt-8 R-to8-2: the ladders moved out of the Smith Chart into a shared
/// <see cref="PreferredValues"/> and gained a resistor ladder. The Smith Chart's own tests
/// (<c>Smith/SmithPreferredValuesTests</c>) are the gate that nothing moved for it; these hold the
/// resistor ladder and what a tunable may select.
/// </summary>
public sealed class PreferredValuesTests
{
    [Fact]
    public void Resistances_SnapToE24_AndToE96_ByRatio()
    {
        var e24 = PreferredValues.ShippedResistorsOhm;
        var e96 = PreferredValues.ResistorsE96Ohm;
        Assert.Equal(24 * 7 + 1, e24.Count);                 // 1 Ω … 10 MΩ, closed on 10 MΩ
        Assert.Equal(96 * 7 + 1, e96.Count);

        Assert.Equal(4.7e3,  PreferredValues.Snap(4.8e3, e24), 9);
        Assert.Equal(4.75e3, PreferredValues.Snap(4.8e3, e96), 9);
        Assert.Equal(1.0e3,  PreferredValues.Snap(1.04e3, e24), 9);
        Assert.Equal(1.05e3, PreferredValues.Snap(1.04e3, e96), 9);

        // The Smith Chart reads the same capacitor and inductor ladders, not copies.
        Assert.Same(PreferredValues.ShippedCapacitorsFarad, SmithPreferredValues.ShippedCapacitorsFarad);
        Assert.Same(PreferredValues.ShippedInductorsHenry, SmithPreferredValues.ShippedInductorsHenry);
    }

    [Fact]
    public void Preferred_IsOfferedOnlyWhereTheUnitHasALadder()
    {
        static Tunable T(string unit, ComplexPart? part = null)
            => new("K", "", null, 1, "", "R", TunableKind.Parameter, "1", 1, unit, false, false, null, null, "0", "2", false, part);

        Assert.Equal(MatchQuantity.Resistance, PreferredValues.QuantityOfUnit("kOhm"));
        Assert.Contains(TuneDiscrete.Preferred, TunableValue.DiscreteChoices(T("pF")));
        Assert.Contains(TuneDiscrete.Preferred, TunableValue.DiscreteChoices(T("Ohm")));
        Assert.DoesNotContain(TuneDiscrete.Preferred, TunableValue.DiscreteChoices(T("um")));
        Assert.Equal([TuneDiscrete.None], TunableValue.DiscreteChoices(T("Ohm", ComplexPart.Real)));
    }
}
