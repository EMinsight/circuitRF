using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>YA-2 R-ya2-5: samples → value text against the nominal GIVEN, integer rounding, a complex
/// value composed in the schematic's form, and a non-physical draw refused rather than clamped.</summary>
public sealed class SampleValuesTests
{
    private const string Bench = """
        Zp = polar(50,30) Ohm
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in out R=50 Ohm
        R:R2 out 0 R=100 Ohm
        R:R3 out 0 R=1 kOhm m=2
        Port:P2 out 0 Num=2 Z=50 Ohm
        analysis SP1 type=sparam start=1 stop=3 npts=21 Unit=GHz
        """;

    private static (PreparedCircuit Circuit, TunableCatalog Catalog) Prepare(string tune)
    {
        var c = PreparedCircuit.FromText(Bench + "\n" + tune, null, null);
        Assert.Null(c.ReadError);
        return (c, TunableCatalog.FromNetlist(c.Tb!, c.Lib!));
    }

    private static double Si(string text)
    {
        Assert.True(TunableValue.TryParse(text, out _, out _, out double si), text);
        return si;
    }

    [Fact]
    public void PercentFollowsAMovedNominal_AbsoluteKeepsItsWidth()
    {
        var (c, catalog) = Prepare("tune R1.R dist=gauss sd=2%\ntune R2.R dist=gauss sd=1 Ohm");
        var sample = new StatisticalSample(1, new Dictionary<string, double> { ["R1.R"] = 1.5, ["R2.R"] = 1.5 });

        var atDesign = SampleValues.Apply(c.Tb!.Tuning!.Variables, catalog.Tunables, sample);
        Assert.Equal(51.5, Si(atDesign.Values["R1.R"]), 1e-9);    // 50 + 1.5·(2 % of 50)
        Assert.Equal(101.5, Si(atDesign.Values["R2.R"]), 1e-9);   // 100 + 1.5·1

        var moved = SampleValues.Nominals(catalog, new Dictionary<string, string> { ["R1.R"] = "100 Ohm", ["R2.R"] = "200 Ohm" });
        var atMoved = SampleValues.Apply(c.Tb.Tuning.Variables, moved, sample);
        Assert.Equal(103, Si(atMoved.Values["R1.R"]), 1e-9);      // 100 + 1.5·(2 % of 100)
        Assert.Equal(201.5, Si(atMoved.Values["R2.R"]), 1e-9);    // 200 + 1.5·1
    }

    [Fact]
    public void AnIntegerValue_IsRounded()
    {
        var (c, catalog) = Prepare("tune R3.m dist=unif lo=1 hi=4");
        // Φ(0.3) = 0.618: 1 + 3·0.618 = 2.85 → 3.
        var v = SampleValues.Apply(c.Tb!.Tuning!.Variables, catalog.Tunables,
                                   new StatisticalSample(1, new Dictionary<string, double> { ["R3.m"] = 0.3 }));
        Assert.Equal("3", v.Values["R3.m"]);
    }

    [Fact]
    public void AComplexPair_IsComposedInTheSchematicsForm()
    {
        var (c, catalog) = Prepare("tune mag(Zp) dist=gauss sd=1 Ohm\ntune phase(Zp) dist=gauss sd=1 deg");
        var v = SampleValues.Apply(c.Tb!.Tuning!.Variables, catalog.Tunables,
                                   new StatisticalSample(1, new Dictionary<string, double> { ["mag(Zp)"] = 2, ["phase(Zp)"] = -1 }));
        Assert.Null(v.Refusal);
        Assert.Equal("polar(52,29) Ohm", v.Values["Zp"]);
        Assert.Equal(52, v.Draws["mag(Zp)"], 1e-9);
        Assert.Equal(29, v.Draws["phase(Zp)"], 1e-9);
    }

    [Fact]
    public void ANonPhysicalDraw_RefusesTheTrial_AndIsNotClamped()
    {
        var (c, catalog) = Prepare("tune R1.R dist=gauss sd=30%");
        var v = SampleValues.Apply(c.Tb!.Tuning!.Variables, catalog.Tunables,
                                   new StatisticalSample(4, new Dictionary<string, double> { ["R1.R"] = -4 }));
        Assert.True(v.Refused);
        Assert.Equal("yield.trial.nonphysical", v.Refusal!.Id);
        Assert.Equal(-10, v.Draws["R1.R"], 1e-9);                 // 50 − 4·15, as drawn
        Assert.False(v.Values.ContainsKey("R1.R"));
    }
}
