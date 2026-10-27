// ================================================================
//  EngineeringFormatTests.cs — reading a picofarad off an axis and out of a table.
//
//  Three failures, one cause: every number format in the Data Display was written for quantities of
//  order one, and an effective capacitance is 1e-12 to 1e-3 farads.
//
//  The gate that matters most here is NoOpInTheOrdinaryRange. Applying an engineering formatter to
//  every axis in the application is only safe because it returns exactly what the old code returned
//  wherever the old code was already right — so that claim is asserted directly, against the
//  quantities the display actually draws, rather than left as an argument in a comment.
// ================================================================

using CircuitRF.Ui.DataDisplay;
using Xunit;

namespace CircuitRF.Ui.Tests;

public class EngineeringFormatTests
{
    // ── the failure that destroyed the axis ────────────────────────────────────────────────────

    /// <summary>
    /// <b>Every tick of a picofarad axis used to be snapped to exactly zero.</b> The near-zero test
    /// was the absolute constant <c>1e-12</c> — written to stop floating-point tick accumulation
    /// printing "−3.5E-17" where zero belongs, which is a real problem, solved with a number that
    /// silently assumes the data is of order one. Measured against the tick spacing instead, it does
    /// the job it was written for at any scale.
    /// </summary>
    [Fact]
    public void ARealPicofaradSurvivesTheNearZeroSnap_AndTickNoiseStillDoesNot()
    {
        const double tick = 20e-12;          // a 100 pF axis, five ticks

        // The data. Under the old absolute 1e-12 test every one of these became 0.
        Assert.Equal(100e-12, EngineeringFormat.SnapNearZero(100e-12, tick));
        Assert.Equal(20e-12,  EngineeringFormat.SnapNearZero(20e-12,  tick));
        Assert.Equal(1e-12,   EngineeringFormat.SnapNearZero(1e-12,   tick));

        // The noise it exists to remove: accumulation residue, far below the tick.
        Assert.Equal(0.0, EngineeringFormat.SnapNearZero(-3.5e-19, tick));
        Assert.Equal(0.0, EngineeringFormat.SnapNearZero(1e-20,    tick));

        // And on an ordinary axis it behaves exactly as the constant did.
        Assert.Equal(0.0,  EngineeringFormat.SnapNearZero(-3.5e-17, 5.0));
        Assert.Equal(-20.0, EngineeringFormat.SnapNearZero(-20.0,   5.0));
    }

    // ── the no-op claim, asserted rather than argued ───────────────────────────────────────────

    /// <summary>
    /// <b>In the range where the old output was already good, the new AXIS TICK is identical.</b>
    /// This is the whole basis for turning the formatter on for every axis in the application
    /// instead of only the traces that needed it, so it is gated against the quantities the display
    /// actually draws: decibels, degrees, stability factors, magnitudes, ohms.
    ///
    /// <para>Table cells are deliberately NOT asserted here — see
    /// <see cref="EngineeringFormat.EngineeringGroup"/> for why the two rules differ. A cell is only
    /// formatted this way when its trace explicitly asks, and no existing trace does.</para>
    /// </summary>
    [Theory]
    [InlineData(-20.0)]     // dB
    [InlineData(-0.5)]
    [InlineData(0.0)]
    [InlineData(0.5)]       // an S-parameter magnitude — must NOT become "500m"
    [InlineData(1.0)]
    [InlineData(3.16)]      // µ
    [InlineData(-180.0)]    // degrees
    [InlineData(50.0)]      // ohms
    [InlineData(1234.5)]
    [InlineData(-99999.0)]
    public void NoOpInTheOrdinaryRange(double value)
    {
        int group = EngineeringFormat.GroupFor(System.Math.Abs(value));
        Assert.Equal(0, group);
        Assert.Equal(value.ToString("G3"), EngineeringFormat.Tick(value, group, 3));
    }

    // ── the range that was broken ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100e-12, "100.000 p")]      // a 100 pF decoupling capacitor
    [InlineData(1e-12,   "1.000 p")]
    [InlineData(2.2e-9,  "2.200 n")]        // ESL
    [InlineData(100e-9,  "100.000 n")]      // 100 nF
    [InlineData(10e-6,   "10.000 µ")]       // 10 µF bulk
    [InlineData(5e-3,    "5.000 m")]        // a milliohm ESR floor — "0.005" would keep one digit
    [InlineData(-5e-3,   "-5.000 m")]
    [InlineData(0.5,     "500.000 m")]      // engineering notation, because the trace asked for it
    [InlineData(50.0,    "50.000")]         // 1…1000 needs no prefix
    public void AValueTooSmallForFixedNotationGetsItsPrefix(double value, string expected)
        => Assert.Equal(expected, EngineeringFormat.Value(value, 3));

    /// <summary>Zero is zero, not "0.000 y".</summary>
    [Fact]
    public void ZeroFormatsPlainly() => Assert.Equal("0.000", EngineeringFormat.Value(0.0, 3));

    [Fact]
    public void NonFiniteIsNaN()
    {
        Assert.Equal("NaN", EngineeringFormat.Value(double.NaN, 3));
        Assert.Equal("NaN", EngineeringFormat.Value(double.PositiveInfinity, 3));
    }

    // ── one prefix per axis ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The group is chosen once for the whole axis, never per tick.</b> Per-value selection puts
    /// "900p" and "1n" on adjacent gridlines of the same axis, which reads as a discontinuity in the
    /// data rather than in the labelling.
    /// </summary>
    [Fact]
    public void OneGroupSpansTheWholeAxis_EvenAcrossADecadeBoundary()
    {
        // An axis running 900 pF to 1.1 nF: the group comes from the window, and every tick is
        // written in it.
        int group = EngineeringFormat.GroupFor(EngineeringFormat.AxisMagnitude(900e-12, 1.1e-9));

        // Taken from the window's largest end, so the ticks below it read sub-unity rather than
        // the axis changing prefix half way up. This is what a bench instrument does.
        Assert.Equal(-9, group);
        Assert.Equal("0.9", EngineeringFormat.Tick(900e-12,  group, 3)[..3]);
        Assert.EndsWith("n", EngineeringFormat.Tick(900e-12,  group, 3));
        Assert.Equal("1n",   EngineeringFormat.Tick(1000e-12, group, 3));
        Assert.Equal("1.1n", EngineeringFormat.Tick(1.1e-9,   group, 3));
    }

    [Fact]
    public void ThePrefixSequenceIsTheSiOne()
    {
        Assert.Equal("p", EngineeringFormat.Prefix(-12));
        Assert.Equal("n", EngineeringFormat.Prefix(-9));
        Assert.Equal("µ", EngineeringFormat.Prefix(-6));
        Assert.Equal("m", EngineeringFormat.Prefix(-3));
        Assert.Equal("",  EngineeringFormat.Prefix(0));
        Assert.Equal("k", EngineeringFormat.Prefix(3));
        Assert.Equal("M", EngineeringFormat.Prefix(6));
        Assert.Equal("G", EngineeringFormat.Prefix(9));
    }

    /// <summary>The axis magnitude is the largest END of the window, so a window straddling zero
    /// picks its prefix from the side that has one.</summary>
    [Fact]
    public void AxisMagnitudeIgnoresSignAndNonFiniteEnds()
    {
        Assert.Equal(2e-9, EngineeringFormat.AxisMagnitude(-2e-9, 1e-9));
        Assert.Equal(1e-9, EngineeringFormat.AxisMagnitude(double.NaN, 1e-9));
        Assert.Equal(0.0,  EngineeringFormat.AxisMagnitude(double.NaN, double.PositiveInfinity));
    }

    // ── the enum seam ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b><c>"S3"</c> is not a .NET format specifier.</b> The display built its format string by
    /// concatenating the enum with a digit count in eight places, which worked only because the
    /// three original members happen to be standard specifiers — so every one of those sites would
    /// have thrown the moment Engineering was selected on a marker or a table cell. The extension
    /// is the seam that makes the enum safe to extend; this asserts it does not throw and that the
    /// other three are byte-for-byte what they always were.
    /// </summary>
    [Theory]
    [InlineData(PrecisionFormat.G)]
    [InlineData(PrecisionFormat.F)]
    [InlineData(PrecisionFormat.E)]
    [InlineData(PrecisionFormat.S)]
    public void EveryPrecisionFormatFormatsWithoutThrowing(PrecisionFormat f)
    {
        foreach (double v in new[] { 0.0, -20.0, 1e-12, 1e9, double.NaN })
        {
            string s = f.Format(v, 3);
            Assert.False(string.IsNullOrEmpty(s));
        }

        if (f != PrecisionFormat.S)
            Assert.Equal((-20.0).ToString($"{f}3"), f.Format(-20.0, 3));
        else
            Assert.Equal("-20.000", f.Format(-20.0, 3));   // 1…1000: no prefix, same as Fixed
    }

    /// <summary>Raw <c>ToString</c> on the new member is exactly the crash the seam prevents —
    /// pinned so nobody reintroduces the concatenation.</summary>
    [Fact]
    public void ConcatenatingTheEnumIntoAFormatStringIsWhatWouldHaveThrown()
        => Assert.Throws<FormatException>(() => (1.0).ToString($"{PrecisionFormat.S}3"));

    [Fact]
    public void EngineeringIsAppendedSoOlderFilesStillLoad()
    {
        // The `.cdd` persists the NAME, and the three that existed keep their ordinals too.
        Assert.Equal(0, (int)PrecisionFormat.G);
        Assert.Equal(1, (int)PrecisionFormat.F);
        Assert.Equal(2, (int)PrecisionFormat.E);
        Assert.Equal(3, (int)PrecisionFormat.S);
        Assert.Equal("Engineering", PrecisionFormat.S.Description());
    }
    // ── designer round 28: a flat trace at −250 dB labelled all seven ticks "-250" ─────────────

    [Fact]
    public void TickLabels_CarryTheDigitsTheirStepNeeds_AndNoMoreWhereThreeAlreadyDo()
    {
        int d = EngineeringFormat.DigitsToResolve(-250.3, -249.7, 0.1, 0, 3);
        Assert.Equal(["-250.3", "-250.2", "-250.1"], new[] { -250.3, -250.2, -250.1 }.Select(v => EngineeringFormat.Tick(v, 0, d)));
        Assert.Equal(3, EngineeringFormat.DigitsToResolve(-24, 0, 4, 0, 3));
        Assert.Equal(3, EngineeringFormat.DigitsToResolve(0, 3600, 400, 0, 3));
    }
}
