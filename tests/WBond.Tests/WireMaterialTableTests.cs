namespace CircuitRF.WBond.Tests;

/// <summary>
/// A wire metal's σ(T) table — read from the shipped material library, clamped at its ends, and
/// carried through a <c>.wBond</c> without bloating it.
/// </summary>
public class WireMaterialTableTests
{
    [Fact]
    public void EveryShippedConductor_IsAWireMetal_AndTheFourWireMetalsCarryTheirTables()
    {
        Assert.Equal(9, WireMaterials.Library.Count);
        foreach (var m in WireMaterials.All)
        {
            Assert.Same(m, WireMaterials.ByName(m.Name));
            Assert.NotNull(m.SigmaVsTemp);
            Assert.Equal(m.Sigma20, m.SigmaAt(20.0));
        }
    }

    [Fact]
    public void ATemperatureOutsideTheTable_IsHeldAtItsNearestEnd_AndSaysSo()
    {
        var gold = WireMaterials.Gold;
        var (lo, hi) = gold.ConductivityRangeC!.Value;

        Assert.Equal(gold.SigmaAt(lo), gold.SigmaAt(-55.0));
        Assert.Equal(gold.SigmaAt(hi), gold.SigmaAt(hi + 500.0));
        Assert.Null(gold.ClampNote(85.0));
        Assert.Contains("outside Gold's conductivity table", gold.ClampNote(-55.0));
    }

    [Fact]
    public void AnOldFilesShippedMetal_TakesTheShippedTable_AndAWrittenFileDoesNotRepeatIt()
    {
        string json = WBondIo.Write(new WBondDesign());
        Assert.DoesNotContain("SigmaVsTemp", json);   // the shape every file before tables had

        var read = WBondIo.Read(json);
        Assert.Same(WireMaterials.Gold, read.Materials.First(m => m.Name == "Gold"));
    }

    [Fact]
    public void AUserMetalsOwnTable_RoundTrips()
    {
        var design = new WBondDesign();
        design.Materials.Add(new WireMaterial("Mine", 1e7, 0.004, 9000,
            [new SigmaPoint(-60, 1.3e7), new SigmaPoint(200, 0.6e7)]));

        var mine = WBondIo.Read(WBondIo.Write(design)).Materials.Single(m => m.Name == "Mine");

        Assert.Equal(design.Materials[^1].SigmaVsTemp, mine.SigmaVsTemp);
    }

    [Fact]
    public void TheDefaultWireTemperatureIs125_AndAFilesOldDefault85ReadsAs125()
    {
        Assert.Equal(125.0, new WBondDesign().OperatingTempC);

        var old = new WBondDesign { OperatingTempC = 85.0 };       // what every file before 2026-10 states
        Assert.Equal(125.0, WBondIo.Read(WBondIo.Write(old)).OperatingTempC);

        var chosen = new WBondDesign { OperatingTempC = 150.0 };   // anything else is a value someone chose
        Assert.Equal(150.0, WBondIo.Read(WBondIo.Write(chosen)).OperatingTempC);
    }
}
