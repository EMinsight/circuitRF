using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>TO-1 R-to1-5: what the tunable catalog offers, what it leaves out, and what it calls read-only.</summary>
public sealed class TunableCatalogTests
{
    private static TunableCatalog Catalog(SchematicEditModel top, string? workspaceRoot = null)
        => TunableCatalog.Discover(top, TuningFixture.Resolver(), workspaceRoot);

    [Fact]
    public void OffersLiteralsVariablesIntegersAndCellParametersPerInstance()
    {
        var top = TuningFixture.Top();
        top.Components[0].Parameters.Add(new EditableParameter { Name = "m", Expression = "2" });
        var c = Catalog(top);

        var r1 = c.Find("R1.R")!;
        Assert.Equal(("50 Ohm", "top", "25 Ohm", "100 Ohm"), (r1.ValueText, r1.Location, r1.DefaultMin, r1.DefaultMax));
        Assert.Equal(TunableKind.Variable, c.Find("Wline")!.Kind);
        Assert.True(c.Find("R1.m")!.IsInteger);

        var r3 = c.Find("DUT:R3.R")!;
        Assert.Equal((2, "DUT · ×2"), (r3.InstanceCount, r3.Location));

        var x1 = c.Find("X1.Rbias")!;
        var x2 = c.Find("X2.Rbias")!;
        Assert.Equal(("2 kOhm", false), (x1.ValueText, x1.IsDefault));
        Assert.Equal(("1 kOhm", true), (x2.ValueText, x2.IsDefault));
    }

    [Fact]
    public void LeavesOutExpressionsStringsAndCellParameterReferences()
    {
        var top = TuningFixture.Top();
        top.Components[1].Parameters.Add(new EditableParameter { Name = "Wtot", Expression = "Wline*2", Unit = "" });
        top.Components[0].Parameters.Add(new EditableParameter { Name = "Tag", Expression = "\"abc\"" });
        var c = Catalog(top);

        Assert.Null(c.Find("Wtot"));
        Assert.Null(c.Find("R1.Tag"));
        Assert.Null(c.Find("DUT:R4.R"));
        Assert.NotNull(c.WhyNotOffered("DUT:R4.R"));
    }

    [Fact]
    public void ACellOutsideTheWorkspaceIsReadOnly()
    {
        var c = Catalog(TuningFixture.Top(), workspaceRoot: "/work/ws");

        Assert.Contains("another workspace", c.Find("DUT:R3.R")!.ReadOnlyReason);
        Assert.Null(c.Find("R1.R")!.ReadOnlyReason);
    }
}
