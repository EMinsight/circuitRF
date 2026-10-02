using System.Numerics;
using CircuitRF.Design.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

/// <summary>
/// The parallel L-C anti-resonance a PDN is designed against — the supply's own inductance against a
/// decoupling capacitor (field report, 2026-10-01). The supply inductance is usually the SOURCE's
/// L out; an inductor PART (an ESL and no C) is the same branch, stamped as a series R-L to the return
/// in the impedance, and a short to the return at DC (field report, 2026-10-02).
/// </summary>
public sealed class ParallelLcResonanceTests(ITestOutputHelper output)
{
    private static RailSpec Rail(params RailPart[] parts)
    {
        var rail = new RailSpec { Name = "PDN", Band = new RailBand(1e4, 2e8, 2001, true) };
        rail.Parts.AddRange(parts);
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 1 });
        return rail;
    }

    private static PdnSweepResult Sweep(RailSpec rail, PartLibrary library, RailSourceModel source) =>
        PdnSweep.Run(new PdnSweepRequest
        {
            Rail         = rail,
            Parts        = new RailPartResolver(library).ResolveAll(rail.Parts, rail.NominalVoltageV),
            Sources      = [source],
            RankRemovals = false,
        });

    /// <summary>6 nH and 20 mΩ of supply against 100 nF with 20 mΩ: Z0 = √(L/C) = 245 mΩ, f0 = 6.50 MHz,
    /// and the peak is Z0²/(R_L + R_C) = 1.50 Ω.</summary>
    [Fact]
    public void SupplyInductanceAgainstOneCapacitor_PeaksWhereTheClosedFormSays()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow { PartNumber = "C1", CapacitanceFarads = 100e-9, EsrOhms = 20e-3 });
        var rail = Rail(new RailPart { Refdes = "C1", PartNumber = "C1", MountingInductanceHenries = 1e-15 });

        var result = Sweep(rail, library, new RailSourceModel(0, "VRM", RailSourceBasis.Rl, 20e-3, 6e-9, 3.3));

        Assert.Null(result.Refusal);
        var port = result.Ports[0];
        int at = Array.IndexOf(port.MagnitudeOhms, port.MagnitudeOhms.Max());
        double f = result.FrequenciesHz[at], z = port.MagnitudeOhms[at];
        output.WriteLine($"peak {z:0.000} ohm at {f / 1e6:0.000} MHz; warnings: {string.Join(" | ", result.Warnings)}");

        double f0 = 1 / (2 * Math.PI * Math.Sqrt(6e-9 * 100e-9));
        Assert.InRange(f, f0 * 0.98, f0 * 1.02);
        Assert.InRange(z, 1.5 * 0.97, 1.5 * 1.03);
    }

    /// <summary>The closed form, written here and not borrowed from railRF: shunt branches in parallel.</summary>
    private static double Parallel(double f, params Func<double, Complex>[] branches)
    {
        Complex y = Complex.Zero;
        foreach (var z in branches) y += 1 / z(2 * Math.PI * f);
        return (1 / y).Magnitude;
    }

    /// <summary>The designer's own library, as entered (field report, 2026-10-02): C1 states C and ESR and no
    /// ESL, L1 states an ESL and an ESR and no C. Both are KEPT now — C1 as a pure R-C, L1 as a series R-L —
    /// and the answer is the closed form 10 mΩ ∥ (R + jωL) ∥ (R + 1/jωC) at every point. The missing ESL is
    /// named in a warning and the inductor in a note.</summary>
    [Fact]
    public void TheRowsAsEntered_AreKeptAndMatchTheClosedForm()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow { PartNumber = "C1", CapacitanceFarads = 100e-9, EsrOhms = 20e-3 });
        library.Rows.Add(new PartLibraryRow { PartNumber = "L1", StatedInductanceHenries = 6e-9, EsrOhms = 20e-3 });
        var rail = Rail(new RailPart { Refdes = "C1", PartNumber = "C1" }, new RailPart { Refdes = "L1", PartNumber = "L1" });

        var result = Sweep(rail, library, new RailSourceModel(0, "VRM", RailSourceBasis.Rl, 10e-3, null, 3.3));

        foreach (var w in result.Warnings) output.WriteLine("warning: " + w);
        foreach (var n in result.Notes) output.WriteLine("note: " + n);
        Assert.Null(result.Refusal);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("NOT in this answer"));
        Assert.Contains(result.Warnings, w => w.Contains("pure R-C with 0 H of inductance: C1 (C1)"));
        Assert.Contains(result.Notes, n => n.Contains("L1 (L1) state(s) an inductance and no capacitance"));

        var port = result.Ports[0];
        double worst = 0;
        for (int k = 0; k < result.FrequenciesHz.Length; k++)
        {
            double want = Parallel(result.FrequenciesHz[k],
                _ => new Complex(10e-3, 0), w => new Complex(20e-3, w * 6e-9), w => new Complex(20e-3, -1 / (w * 100e-9)));
            worst = Math.Max(worst, Math.Abs(port.MagnitudeOhms[k] / want - 1));
        }
        output.WriteLine($"|Z| max {port.MagnitudeOhms.Max() * 1e3:0.000} mOhm; worst relative error {worst:E2}");
        Assert.True(worst < 1e-9, $"worst relative error {worst:E2}");

        // The parts table's ESR column reads the stated 20 mΩ of a part with no resonance to quote it at.
        var parts = new RailPartResolver(library).ResolveAll(rail.Parts, rail.NominalVoltageV);
        Assert.All(parts.Mounted, m => Assert.Equal(20e-3, m.EsrOhms));
    }

    /// <summary>The slide's network — 6 nH + 20 mΩ ∥ 100 nF + 20 mΩ, the generator only the excitation —
    /// entered as two PARTS with a stated ESL of 0 on the capacitor, behind a source too resistive to
    /// matter. The closed form peaks at Z0²/(R_L + R_C) = 1.510 Ω at 6.497 MHz, which the slide rounds to
    /// 1.5 Ω. (The designer's external circuit-simulator cursor read 1.457 Ω at 6.532 MHz: a grid point
    /// 0.5 % off the peak, in a tool that gives an inductor a small series resistance of its own by default
    /// — 1 mΩ more loss alone gives 1.474 Ω.) A stated zero ESL draws no warning.</summary>
    [Fact]
    public void TheSlideAsTwoParts_MatchesTheExternalSimulatorPeak()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow { PartNumber = "C1", CapacitanceFarads = 100e-9, EsrOhms = 20e-3, StatedInductanceHenries = 0 });
        library.Rows.Add(new PartLibraryRow { PartNumber = "L1", StatedInductanceHenries = 6e-9, EsrOhms = 20e-3 });
        var rail = Rail(new RailPart { Refdes = "C1", PartNumber = "C1" }, new RailPart { Refdes = "L1", PartNumber = "L1" });

        var result = Sweep(rail, library, new RailSourceModel(0, "VRM", RailSourceBasis.Rl, 1e6, null, 3.3));

        var port = result.Ports[0];
        int at = Array.IndexOf(port.MagnitudeOhms, port.MagnitudeOhms.Max());
        double f = result.FrequenciesHz[at], z = port.MagnitudeOhms[at];
        output.WriteLine($"peak {z:0.0000} ohm at {f / 1e6:0.000} MHz");
        Assert.InRange(f, 6.497e6 * 0.995, 6.497e6 * 1.005);
        Assert.InRange(z, 1.510 * 0.999, 1.510 * 1.001);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("ESL"));
    }

    /// <summary>A part with no ESR is kept with 0 Ω of loss, and the warning says what that does to a peak.</summary>
    [Fact]
    public void APartWithNoEsr_IsKeptAndWarned()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow { PartNumber = "C1", CapacitanceFarads = 100e-9, StatedInductanceHenries = 1e-9 });
        var rail = Rail(new RailPart { Refdes = "C1", PartNumber = "C1" });

        var result = Sweep(rail, library, new RailSourceModel(0, "VRM", RailSourceBasis.Rl, 20e-3, 6e-9, 3.3));

        Assert.Null(result.Refusal);
        string warning = Assert.Single(result.Warnings, w => w.Contains("0 Ω of loss: C1 (C1)"));
        Assert.Contains("unbounded where nothing else on the rail is lossy", warning);
    }

    /// <summary>A dielectric class gives an inductor part no loss, and the warning says THAT — not "no dielectric class".</summary>
    [Fact]
    public void AnInductorWithOnlyADielectricClass_IsWarnedForWhatTheClassCannotGive()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow { PartNumber = "L1", StatedInductanceHenries = 6e-9, DielectricClass = "X7R" });
        var rail = Rail(new RailPart { Refdes = "L1", PartNumber = "L1" });

        var result = Sweep(rail, library, new RailSourceModel(0, "VRM", RailSourceBasis.Rl, 20e-3, null, 3.3));

        Assert.Null(result.Refusal);
        string warning = Assert.Single(result.Warnings, w => w.Contains("0 Ω of loss: L1 (L1)"));
        Assert.Contains("state a dielectric class and no ESR", warning);
        Assert.DoesNotContain("no dielectric class", warning);
    }
}
