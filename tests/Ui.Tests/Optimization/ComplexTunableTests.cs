using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>Overview D18: a complex value is offered by its four parts, never whole and never when it
/// reads a name, and the ranges of its parts always hold together.</summary>
public sealed class ComplexTunableTests
{
    [Theory]
    [InlineData("40+15j Ohm",      40, 15,  ComplexForm.Rect)]
    [InlineData("complex(40,-15)", 40, -15, ComplexForm.Call)]
    [InlineData("polar(2,90)",     0,  2,   ComplexForm.Polar)]
    public void AComplexLiteral_ReadsInEachForm_AndWritesBackInIt(string text, double re, double im, ComplexForm form)
    {
        Assert.True(ComplexValue.TryParse(text, out var z, out string unit, out var f));
        Assert.Equal(form, f);
        Assert.Equal(re, z.Real, 12);
        Assert.Equal(im, z.Imaginary, 12);
        Assert.Equal(text, ComplexValue.Format(z, unit, f));
    }

    [Theory]
    [InlineData("4+j*X")]     // reads a name: an expression, not offered
    [InlineData("50 Ohm")]    // a plain number is an ordinary tunable
    public void AnythingElse_IsNotAComplexLiteral(string text)
        => Assert.False(ComplexValue.TryParse(text, out _, out _, out _));

    [Fact]
    public void TheCatalog_OffersTheFourParts_NeverTheWhole_NorOneThatReadsAName()
    {
        var top = TuningFixture.Top();
        var vars = top.Components.Single(c => c.InstanceName == "VAR1").Parameters;
        vars.Add(new EditableParameter { Name = "ZL", Expression = "40+15j", Unit = "Ohm" });
        vars.Add(new EditableParameter { Name = "ZX", Expression = "4+j*Wline" });
        var c = TunableCatalog.Discover(top, TuningFixture.Resolver());

        var mag = c.Find("mag(ZL)")!;
        Assert.Equal(("Ohm", "ZL", new Complex(40, 15)), (mag.Unit, mag.WholeKey, mag.Whole));
        Assert.Equal(Math.Sqrt(40 * 40 + 15 * 15), mag.Value, 12);
        Assert.Equal("deg", c.Find("phase(ZL)")!.Unit);
        Assert.Equal(["real(ZL)", "imag(ZL)", "mag(ZL)", "phase(ZL)"], c.PartsOf("ZL").Select(t => t.Key));

        Assert.Null(c.Find("ZL"));
        Assert.Contains("its value is complex", c.WhyNotOffered("ZL"));
        Assert.Empty(c.PartsOf("ZX"));
        Assert.Contains("not a complex number", c.WhyNotOffered("real(R1.R)"));
    }

    [Fact]
    public void Ranges_ThatExcludeEachOther_LeaveNoValue_AndAMoveStopsAtTheFirstEdge()
    {
        Assert.True(new ComplexRegion([(ComplexPart.Real, 90, 120), (ComplexPart.Mag, 20, 80)]).IsEmpty);
        Assert.False(new ComplexRegion([(ComplexPart.Real, 10, 50), (ComplexPart.Mag, 20, 60), (ComplexPart.Phase, 0, 45)]).IsEmpty);
        Assert.True(new ComplexRegion([(ComplexPart.Imag, 10, 20), (ComplexPart.Phase, -90, -10)]).IsEmpty);

        // Real moves with imaginary held, and stops where the magnitude reaches its limit.
        var region = new ComplexRegion([(ComplexPart.Mag, 0, 60)]);
        var moved  = region.Move(new Complex(40, 15), ComplexPart.Real, 70);
        Assert.Equal(15, moved.Imaginary, 12);
        Assert.Equal(Math.Sqrt(60 * 60 - 15 * 15), moved.Real, 6);
    }

    [Fact]
    public void AMixedPair_IsSolvedGeometrically_AndAnImpossibleOneIsNot()
    {
        var start = new Complex(40, 15);
        var z = ComplexValue.Compose(start, new Dictionary<ComplexPart, double> { [ComplexPart.Real] = 30, [ComplexPart.Mag] = 50 })!.Value;
        Assert.Equal(40, z.Imaginary, 9);    // the sign of the imaginary part is the start's
        Assert.Null(ComplexValue.Compose(start, new Dictionary<ComplexPart, double> { [ComplexPart.Real] = 60, [ComplexPart.Mag] = 50 }));
    }

    [Fact]
    public void Overrides_TakeTheWholeValue_OrComposeParts_InTheDesignsForm()
    {
        var top = TuningFixture.Top();
        top.Components.Single(c => c.InstanceName == "VAR1").Parameters
           .Add(new EditableParameter { Name = "ZL", Expression = "polar(50,30)", Unit = "Ohm" });
        var x = NetExtractor.Extract(top, "tb", TuningFixture.Resolver());

        string Zl(IReadOnlyDictionary<string, string> values)
        {
            var tuned = TunableOverrides.Apply(x.TestBench, x.Library, values);
            Assert.Empty(tuned.Notes);
            var v = tuned.TestBench!.GlobalVariables.Single(g => g.Name == "ZL");
            return TunableValue.Text(v.Expression, v.Unit);
        }

        Assert.Equal("polar(60,45) Ohm", Zl(new Dictionary<string, string> { ["ZL"] = "polar(60,45) Ohm" }));
        Assert.Equal("polar(50,60) Ohm", Zl(new Dictionary<string, string> { ["phase(ZL)"] = "60 deg" }));
    }
}
