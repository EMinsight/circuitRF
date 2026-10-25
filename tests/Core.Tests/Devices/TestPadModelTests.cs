using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// brief-dfr26-7 §2 test 5: the test pad's closed forms. The pad capacitance is the designer's own hand estimate, so
/// it is checked against that formula written out here, independently of <see cref="TestPadFormulas"/>; the model's
/// stamp is checked to resonate where <c>1/(2π√(LC))</c> puts it.
/// </summary>
public class TestPadModelTests
{
    [Fact]
    public void PadCapacitance_IsTheDesignersFormula_ForAOneAndAHalfMillimetrePad71MicronsOverFr4()
    {
        const double e0 = 8.8541878128e-12, er = 4.4, r = 0.75e-3, h = 71e-6;
        double designer = e0 * er * Math.PI * r * r / h + e0 * er * 2 * Math.PI * r * Math.Log(1 + 2 * r / h);

        Assert.Equal(designer, TestPadFormulas.PadCapacitance(1.5e-3, h, er), 1e-24);
        Assert.Equal(1.538e-12, designer, 1e-15);
    }

    [Fact]
    public void LayeredDielectric_CombinesInSeries()
    {
        var (h, er) = TestPadFormulas.SeriesDielectric([(71e-6, 4.4), (69e-6, 3.0)]);
        Assert.Equal(140e-6, h, 1e-15);
        Assert.Equal(140e-6 / (71e-6 / 4.4 + 69e-6 / 3.0), er, 1e-12);
    }

    [Fact]
    public void OpenEndedPad_ResonatesWhereOneOverTwoPiRootLCPutsIt()
    {
        var model = Assert.IsType<TestPadModel>(ComponentModelFactory.TryCreate("TESTPAD", new Dictionary<string, Value>
        {
            ["D"] = new(1.5e-3), ["Hpad"] = new(71e-6), ["Erpad"] = new(4.4), ["Hvia"] = new(1.6e-3), ["Drill"] = new(0.3e-3),
        }));
        var g = model.Geometry;
        double f0 = 1 / (2 * Math.PI * Math.Sqrt(g.ViaInductance * g.PadCapacitance));
        Assert.Equal(f0, g.ResonanceHz, f0 * 1e-12);

        // Pin 2 left open: the input admittance at pin 1 is Y11 − Y12²/Y22, which goes through a pole at f0.
        Complex Yin(double f)
        {
            var (y11, y12, y22) = model.Admittance(2 * Math.PI * f);
            return y11 - y12 * y12 / y22;
        }
        Assert.True(Yin(f0 * 0.999).Imaginary > 0 && Yin(f0 * 1.001).Imaginary < 0);   // capacitive below, inductive above
        Assert.True(Yin(f0 * 0.999).Magnitude > 100 * Yin(f0 * 0.5).Magnitude);
    }

    [Fact]
    public void APlaneInClearLayers_TakesClearD_AndTheOthersTheirOwnClearance()
    {
        var model = Assert.IsType<TestPadModel>(ComponentModelFactory.TryCreate("TESTPAD", new Dictionary<string, Value>
        {
            ["D"] = new(1.5e-3), ["Hvia"] = new(1.6e-3), ["Planes"] = new(2.0),
            ["Clp1"] = new(1.0), ["Bp2"] = new(0.9e-3), ["ClearD"] = new(2.5e-3),
        }));
        Assert.Equal([2.5e-3, 0.9e-3], model.Geometry.Planes.Select(p => p.Clearance));
    }
}
