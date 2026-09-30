using CircuitRF.Core.Devices;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// brief-via-component.md §6 gate 1: the via's closed forms against values worked out by hand, and the
/// long-barrel limit that says which surface-current form Goldfarb and Pucel's inductance sits beside.
///
/// <para>The expected numbers were computed independently (double precision, outside circuitRF) from
/// the published formulas as <see cref="ViaFormulas"/>' doc comments quote them, and are committed as
/// literals. They are not the paper's own table: the paper was not to hand when this was written.</para>
/// </summary>
public class ViaFormulasTests
{
    private const double Mu0Over2Pi = 2e-7;

    [Theory]
    // h, r → L (H). A 62 mil board, a 12 mil drill; the via-transition example; a 25 mil via.
    [InlineData(1.6e-3,  0.15e-3, 5.428827127237785e-10)]
    [InlineData(0.47e-3, 0.15e-3, 7.181327566520636e-11)]
    [InlineData(0.635e-3, 0.2e-3, 9.806683010517697e-11)]
    public void GoldfarbPucelInductance_MatchesTheHandComputedValue(double h, double r, double expected)
        => Assert.Equal(expected, ViaFormulas.GoldfarbPucelInductance(h, r), expected * 1e-12);

    /// <summary>
    /// For a barrel much longer than its radius, Goldfarb and Pucel tend to <c>h·[ln(2h/r) − 3/2]</c>,
    /// which is Grover's surface-current form <c>h·[ln(2h/r) − 1]</c> less <c>h/2</c>. The brief expected
    /// the formula to tend to Grover's form itself; it does not, and the exact tube form is what shows
    /// where the two part (the paper's 1.5 on the second term, where the tube has 1).
    /// </summary>
    [Fact]
    public void AtLongBarrels_GoldfarbPucelIsGroversSurfaceFormLessHalfTheLength()
    {
        const double r = 0.1e-3, h = 1000 * r;
        double gp = ViaFormulas.GoldfarbPucelInductance(h, r);
        double limit = Mu0Over2Pi * h * (Math.Log(2 * h / r) - 1.5);
        Assert.Equal(limit, gp, limit * 1e-3);

        double grover = ViaFormulas.GroverTubeInductance(h, r);
        double groverLimit = Mu0Over2Pi * h * (Math.Log(2 * h / r) - 1.0);
        Assert.Equal(groverLimit, grover, groverLimit * 1e-3);

        // The difference is exactly half the tube form's √(r²+h²) − r term, at every length.
        double s = Math.Sqrt(r * r + h * h);
        Assert.Equal(Mu0Over2Pi * 0.5 * (s - r), grover - gp, 1e-18);
    }

    [Fact]
    public void Resistance_IsTheTubesDcValue_RisingAsRootF_PastTheSkinOnset()
    {
        const double h = 1.6e-3, r = 0.15e-3, t = 25e-6, sigma = 5.8e7;
        Assert.Equal(0.001277230891270382, ViaFormulas.DcResistance(h, r, t, sigma, solid: false), 1e-15);
        Assert.Equal(6987667.8374026045, ViaFormulas.SkinOnsetFrequency(r, t, sigma, solid: false), 1e-6);
        Assert.Equal(0.048334248435381345, ViaFormulas.AcResistance(10e9, h, r, t, sigma, solid: false), 1e-12);
        Assert.Equal(ViaFormulas.DcResistance(h, r, t, sigma, false), ViaFormulas.AcResistance(0, h, r, t, sigma, false));
    }

    /// <summary>A filled barrel is a rod: its DC value, and a high-frequency limit equal to the rod's own
    /// surface resistance h/(σ·2πr·δ) — which is what taking t = r/2 in the skin onset buys.</summary>
    [Fact]
    public void AFilledBarrel_IsARod_AtBothEnds()
    {
        const double h = 1.6e-3, r = 0.15e-3, sigma = 5.8e7;
        Assert.Equal(0.0003902649945548392, ViaFormulas.DcResistance(h, r, 25e-6, sigma, solid: true), 1e-15);

        const double f = 1e12;
        double delta = Math.Sqrt(1 / (Math.PI * f * 4e-7 * Math.PI * sigma));
        double surface = h / (sigma * 2 * Math.PI * r * delta);
        Assert.Equal(surface, ViaFormulas.AcResistance(f, h, r, 25e-6, sigma, solid: true), surface * 1e-3);
    }

    [Fact]
    public void JohnsonGraham_IsTheirFormulaInInches_AndZeroWithNoClearance()
    {
        // 1.41·4.4·(1.6/25.4)·0.6/(0.9−0.6) pF.
        Assert.Equal(7.816062992125985e-13, ViaFormulas.JohnsonGrahamCapacitance(4.4, 1.6e-3, 0.6e-3, 0.9e-3), 1e-24);
        Assert.Equal(0.0, ViaFormulas.JohnsonGrahamCapacitance(4.4, 1.6e-3, 0.6e-3, 0.6e-3));
    }
}
