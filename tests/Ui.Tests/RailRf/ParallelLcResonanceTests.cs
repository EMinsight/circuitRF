using CircuitRF.Design.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

/// <summary>
/// The parallel L-C anti-resonance a PDN is designed against — the supply's own inductance against a
/// decoupling capacitor (field report, 2026-10-01). In railRF the supply inductance is the SOURCE's
/// L out, not a part: a shunt inductor from the rail to the return is a DC short across the supply,
/// and the part library models capacitors.
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

    /// <summary>The designer's own library, as entered: C1 states C and ESR but no ESL, L1 states an ESL and
    /// no C. Neither can be a shunt branch, and the warning says so.</summary>
    [Fact]
    public void TheRowsAsEntered_AreNamedNotModelled()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow { PartNumber = "C1", CapacitanceFarads = 100e-9, EsrOhms = 20e-3 });
        library.Rows.Add(new PartLibraryRow { PartNumber = "L1", StatedInductanceHenries = 6e-9, EsrOhms = 20e-3 });
        var rail = Rail(new RailPart { Refdes = "C1", PartNumber = "C1" }, new RailPart { Refdes = "L1", PartNumber = "L1" });

        var result = Sweep(rail, library, new RailSourceModel(0, "VRM", RailSourceBasis.Rl, 10e-3, null, 3.3));

        output.WriteLine($"|Z| {result.Ports[0].MagnitudeOhms.Min():0.0000}..{result.Ports[0].MagnitudeOhms.Max():0.0000} ohm");
        foreach (var w in result.Warnings) output.WriteLine("warning: " + w);
        foreach (var n in result.Notes) output.WriteLine("note: " + n);
        string warning = Assert.Single(result.Warnings, w => w.Contains("2 part(s) on this rail are NOT in this answer"));
        Assert.Contains("C1 (C1) has no inductance", warning);
        Assert.Contains("L1 (L1) states an inductance and no capacitance", warning);
        Assert.Contains("source row's L out", warning);
    }
}
