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

    // ── brief-dfr26-8: unused inner pads ────────────────────────────────────────────────────────

    /// <summary>The unscreened inner-pad term against an axisymmetric finite-difference solve of one pad on an 18 µm
    /// plane between planes 69 and 460 µm away, every plane cleared to the same antipad (fixed reference data,
    /// computed outside circuitRF; εr 4.4).</summary>
    [Theory]
    [InlineData(0.60e-3, 0.94e-3, 0.3e-3, 45.21e-15)]
    [InlineData(0.75e-3, 1.20e-3, 0.3e-3, 67.76e-15)]
    [InlineData(0.45e-3, 0.80e-3, 0.3e-3, 16.06e-15)]
    [InlineData(0.60e-3, 0.94e-3, 0.2e-3, 64.29e-15)]
    public void InnerPad_MatchesTheFieldSolve_WithinTwentyPercent(double pad, double antipad, double drill, double solved)
        => Assert.InRange(TestPadFormulas.InnerPadCapacitance(pad, antipad, drill, 18e-6, 4.4), 0.8 * solved, 1.2 * solved);

    [Fact]
    public void InnerPads_JoinTheLadder_AndAPadNoNarrowerThanItsAntipadCountsNothing()
    {
        var p = new Dictionary<string, Value>
        {
            ["D"] = new(1.5e-3), ["Hpad"] = new(71e-6), ["Erpad"] = new(4.4), ["Hvia"] = new(1.6e-3), ["Drill"] = new(0.3e-3),
            ["InnerPadD"] = new(0.6e-3), ["AntipadD"] = new(0.94e-3), ["InnerPads"] = new(2.0),
            ["Zq1"] = new(0.5e-3), ["Tq1"] = new(18e-6), ["Erq1"] = new(4.4), ["Hqa1"] = new(460e-6), ["Kqa1"] = new(2.0), ["Hqb1"] = new(69e-6), ["Kqb1"] = new(1.0),
            ["Zq2"] = new(0.6e-3), ["Tq2"] = new(18e-6), ["Erq2"] = new(4.4), ["Hqa2"] = new(69e-6), ["Kqa2"] = new(1.0), ["Hqb2"] = new(460e-6), ["Kqb2"] = new(2.0),
        };
        var model = Assert.IsType<TestPadModel>(ComponentModelFactory.TryCreate("TESTPAD", p));
        var g = model.Geometry;
        // Each keeps its open side and 46 % of the side it shares with the other.
        Assert.InRange(g.InnerPadCapacitance, 2 * 0.70 * 39e-15, 2 * 0.76 * 46e-15);

        // Well below resonance, pin 1 with pin 2 open looks like everything capacitive hanging off it.
        double w = 2 * Math.PI * 1e6;
        var (y11, y12, y22) = model.Admittance(w);
        Assert.Equal(g.StubCapacitance, (y11 - y12 * y12 / y22).Imaginary / w, g.StubCapacitance * 1e-4);

        p["InnerPadD"] = new(0.94e-3);
        Assert.Equal(0.0, Assert.IsType<TestPadModel>(ComponentModelFactory.TryCreate("TESTPAD", p)).Geometry.InnerPadCapacitance);
    }
}
