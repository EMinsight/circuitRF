using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Netlist.Spice;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// The card-side half of brief-gdf-1b: what a diode or bipolar card becomes once a kit device may
/// run on circuitRF's own physics. Every card here is synthetic.
/// </summary>
public sealed class KitCardTranslationTests
{
    private static SpiceModelCard Card(string text) => Assert.Single(SpiceNetlistReader.Read(text).ModelCards);

    /// <summary>
    /// R-gdf1b-2. A card stating a level circuitRF does not implement is refused BY NAME — never read
    /// as level 1, because a level-9 bipolar card's IS, NF and RE are another model's parameters under
    /// the same spellings. A level-1 card binds as before, and its honoured LEVEL is not reported as
    /// dropped.
    /// </summary>
    [Fact]
    public void AStatedLevelCircuitRfDoesNotImplement_IsRefusedByName_AndLevelOneBinds()
    {
        var nine = SpiceModelCardTranslation.Translate(Card(".model qhbt npn level=9 is=1e-17 nf=1 re=2"));
        Assert.Null(nine.Binding);
        Assert.Contains("'qhbt'", nine.Refusal!, StringComparison.Ordinal);
        Assert.Contains("NPN", nine.Refusal!, StringComparison.Ordinal);
        Assert.Contains("LEVEL=9", nine.Refusal!, StringComparison.Ordinal);

        var one = SpiceModelCardTranslation.Translate(Card(".model dj d level=1 is=1e-15 n=1.05"));
        Assert.Equal("Diode", one.Binding!.EngineReference);
        Assert.DoesNotContain("level", one.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A bipolar card's UNSTATED parameters mean this dialect's defaults — no Early effect, no knee, no
    /// junction capacitance — not circuitRF's component defaults, which describe a particular part.
    /// </summary>
    [Fact]
    public void ABipolarCardsUnstatedParameters_AreWrittenAtTheDialectsDefaults()
    {
        var t = SpiceModelCardTranslation.Translate(Card(".model qa npn is=2e-16 bf=80"));

        string Value(string name) => t.Binding!.Parameters.Single(p => p.Name == name).Expression;
        Assert.Equal("2E-16", Value("Is"));   // stated: the card's own
        Assert.Equal("0", Value("Vaf"));      // unstated: not modelled
        Assert.Equal("1", Value("Br"));
        Assert.Equal("0", Value("Cje"));
    }

    /// <summary>
    /// What a binding leaves behind is judged by whether it would MOVE a DC answer: a sidewall current
    /// does, a sidewall capacitance does not, and a knee stated at zero ("not modelled") does not.
    /// </summary>
    [Fact]
    public void DcAffectingUnmapped_ListsCurrentsNotCharges_AndIgnoresAStatedZero()
    {
        var t = SpiceModelCardTranslation.Translate(
            Card(".model dk d is=1e-15 jsw=1e-16 cjsw=1e-15 ik=0"));

        Assert.Equal(["jsw"], SpiceModelCardTranslation.DcAffectingUnmapped(t));
    }

    /// <summary>A diode card's JS is its saturation current under the dialect's other spelling, so a
    /// card stating only JS binds it as Is rather than leaving it behind.</summary>
    [Fact]
    public void ADiodeCardStatingJs_BindsItAsTheSaturationCurrent()
    {
        var t = SpiceModelCardTranslation.Translate(Card(".model dj d js=3e-15 n=1.1"));

        Assert.Equal("3E-15", t.Binding!.Parameters.Single(p => p.Name == "Is").Expression);
        Assert.DoesNotContain("js", t.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The low-level breakdown current moves a reverse-biased DC answer, so it is listed.</summary>
    [Fact]
    public void DcAffectingUnmapped_ListsTheLowLevelBreakdownCurrent()
    {
        var t = SpiceModelCardTranslation.Translate(Card(".model db d is=1e-15 bv=5 ibvl=1e-9"));

        Assert.Equal(["ibvl"], SpiceModelCardTranslation.DcAffectingUnmapped(t));
    }

    /// <summary>
    /// A corner file's section reading ANOTHER section of the same file is not a cycle. Keyed on the
    /// file alone it was refused as one, and every card behind it went unread.
    /// </summary>
    [Fact]
    public void ASectionIncludingAnotherSectionOfItsOwnFile_IsRead()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-selfsec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "corners.lib"), """
                .lib typical
                .param ka=1
                .lib 'corners.lib' cards
                .endl typical

                .lib cards
                .model dc d is={1e-15*ka}
                .endl cards
                """);

            var r = SpiceNetlistReader.Read(".lib \"corners.lib\" \"typical\"", dir);

            Assert.Equal("dc", Assert.Single(r.ModelCards).Name);
            Assert.DoesNotContain(r.Notes, n => n.Message.Contains("includes itself", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// An element line that states its own resistance takes it, and the card supplies only what the
    /// line does not — its temperature coefficients. It used to be refused for want of a sheet resistance.
    /// </summary>
    [Fact]
    public void AResistorStatingItsOwnValue_BindsOnACardHoldingOnlyCoefficients()
    {
        var r = SpiceNetlistReader.Read("""
            .subckt contact a b
            R1 a b rtc r=150
            .ends contact
            .model rtc r tc1=-4e-4 tc2=1e-5
            """);

        var inst = Assert.Single(r.Library.Find("contact")!.Instances);
        Assert.Equal("R", inst.Reference);
        Assert.Equal("150", inst.Overrides.Single(o => o.Name == "R").Expression);
        Assert.Equal(-4e-4, double.Parse(inst.Overrides.Single(o => o.Name == "TC1").Expression, System.Globalization.CultureInfo.InvariantCulture));
    }
}
