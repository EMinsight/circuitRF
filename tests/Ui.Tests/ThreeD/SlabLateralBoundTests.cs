// ================================================================
//  SlabLateralBoundTests.cs — designer feedback 02, Q5: the lateral shape of a slab the stackup gives no outline.
//  A layout placed in a .c3d (the FEM/FDTD problem) and a planar setup's 3D preview (display only) share ONE rule:
//  the board outline; else the closed hull of the copper above and below (gaps closed, holes filled, a margin past
//  the copper); else the bounding box.
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Render.Scene3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class SlabLateralBoundTests
{
    private const double Mm = 1e-3;

    /// <summary>The hull closes a coplanar gap and fills a clearance hole, and stands M past the copper — never further.</summary>
    [Fact]
    public void TheHull_ClosesACoplanarGap_FillsAHole_AndStandsTheMarginPastTheCopper()
    {
        var trace  = Rect(0, -0.5, 10, 0.5);
        var ground = new PlanarPolygon(Rect(0, 1.0, 10, 3.0).Outer, [Rect(4, 1.5, 5, 2.5).Outer]);   // a clearance in it
        var hull = SlabLateralBound.CopperHull([trace, ground], 2.5 * Mm, 1.0 * Mm);

        var one = Assert.Single(hull);
        Assert.Empty(one.HoleRings);
        Assert.True(one.Contains(5 * Mm, 0.75 * Mm), "the coplanar gap");
        Assert.True(one.Contains(4.5 * Mm, 2 * Mm), "the clearance hole");
        Assert.True(one.Contains(5 * Mm, 3.9 * Mm), "inside the margin");
        Assert.False(one.Contains(5 * Mm, 4.1 * Mm), "past the margin");
    }

    /// <summary>No outline, bottom plane undrawn: the substrate and the instance's plane take the hull of the top
    /// copper, so the gap and the fringing margin beside the ground strip are substrate, and the note says so.</summary>
    [Fact]
    public void APlacedLayoutWithNoOutline_BoundsItsSubstrateByTheCopperHull_AndSaysWhich()
    {
        var r = Em3dLayoutSolids.From(Source(Cpw(), "pcb-2layer_RO4350B_20mil_1oz"), Tech2(), null, Instance());
        Assert.True(r.Ok, r.Refusal);

        var slab = Poly(r.Solids.Single(s => s.Role == Em3dRole.Dielectric).Primitive);
        Assert.True(slab.Contains(5 * Mm, 0.75 * Mm), "the coplanar gap is substrate");
        Assert.True(slab.Contains(5 * Mm, 3.5 * Mm), "the substrate runs past the outer copper edge");
        Assert.False(slab.Contains(5 * Mm, 6 * Mm), "and stops a margin past it, not at a bounding box");
        Assert.Contains(r.Notes, n => n.Contains("'RO4350' from 'Top Copper (1 oz)'", StringComparison.Ordinal));
    }

    /// <summary>A drawn board outline wins over the copper.</summary>
    [Fact]
    public void ABoardOutline_WinsOverTheCopper()
    {
        var r = Em3dLayoutSolids.From(Source(Cpw(outline: true), "pcb-2layer_RO4350B_20mil_1oz"), Tech2(), null, Instance());
        Assert.True(r.Ok, r.Refusal);
        var slab = (Em3dExtrudedPolygon)r.Solids.Single(s => s.Role == Em3dRole.Dielectric).Primitive;
        Assert.Equal((-5 * Mm, -10 * Mm, 15 * Mm, 10 * Mm),
                     (slab.Outline.Min(p => p.X), slab.Outline.Min(p => p.Y), slab.Outline.Max(p => p.X), slab.Outline.Max(p => p.Y)),
                     new Tol(1e-12));
        Assert.Contains(r.Notes, n => n.Contains("bounded by its board outline", StringComparison.Ordinal));
    }

    /// <summary>A slab whose neighbour conductor has nothing drawn walks outward to the next copper: on a four-layer
    /// board with only the outer layers drawn, the top prepreg takes the hull of the top AND bottom copper.</summary>
    [Fact]
    public void AnEmptyNeighbourConductor_WalksOutwardToTheNextCopper()
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var view = LayoutPersistence.Deserialize(Clay(
            """{ "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": -500000, "X2": 10000000, "Y2": 500000 }""",
            """{ "$type": "Rect", "Layer": { "Layer": 4, "Datatype": 0 }, "X1": 0, "Y1": -8000000, "X2": 10000000, "Y2": 8000000 }"""));
        var r = Em3dLayoutSolids.From(new EmLayoutSource("/nowhere/b.clay", view, tech, view.DbuPerMicron), tech, null, Instance());
        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n => n.Contains("'Prepreg (top)' from 'Top Copper (1 oz)' and 'Bottom Copper (1 oz)'", StringComparison.Ordinal));
    }

    /// <summary>A planar setup's 3D preview draws its substrate to the same hull — not across the air box — and shows
    /// it; without the display flag the generator's (3D solve's) slab still spans the air box.</summary>
    [Fact]
    public void APlanarPreview_DrawsItsSubstrateToTheHull_AndShowsIt()
    {
        var tech = Tech2();
        var source = Source(Cpw(), "pcb-2layer_RO4350B_20mil_1oz");
        var setup = new EmSetup
        {
            Name = "cpw", LayoutRef = "cpw/layout/cpw.clay",
            Frequency = new FrequencySpec("1", "10", 4, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolver.Palace, Problem3D = Em3dProblemType.Driven, Ports3D = [],
        };

        var solve = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(solve.Ok, solve.Refusal);
        Assert.IsType<Em3dBox>(solve.Problem!.Solids.Single(s => s.Role == Em3dRole.Dielectric).Primitive);

        var preview = Em3dGenerator.Generate(setup, source, tech, displaySlabs: true);
        Assert.True(preview.Ok, preview.Refusal);
        var slab = preview.Problem!.Solids.Single(s => s.Role == Em3dRole.Dielectric);
        Assert.True(Poly(slab.Primitive).Contains(5 * Mm, 0.75 * Mm));
        Assert.False(Poly(slab.Primitive).Contains(5 * Mm, 6 * Mm));

        var scene = Scene3DBuilder.Build(preview.Problem, 1, preview.Origins, tech,
                                         options: new Scene3DBuildOptions(HideOutermostDielectric: false));
        Assert.True(scene.Objects.Single(o => o.Name == slab.Name).InitiallyVisible);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    private static Em3dLayoutSolidsOptions Instance() => new(null, 20, Instance: true);

    private static Technology Tech2() => ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");

    private static EmLayoutSource Source(string clay, string techName)
    {
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load(techName);
        return new EmLayoutSource("/nowhere/cpw.clay", view, tech, view.DbuPerMicron);
    }

    /// <summary>A grounded-coplanar-style top layer (trace, two ground strips, 0.5 mm gaps), bottom plane undrawn.</summary>
    private static string Cpw(bool outline = false) => Clay(
        """{ "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": -500000, "X2": 10000000, "Y2": 500000 }""",
        """{ "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": 1000000, "X2": 10000000, "Y2": 3000000 }""",
        """{ "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": -3000000, "X2": 10000000, "Y2": -1000000 }""",
        """{ "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 0, "Y": 0, "Text": "1", "Height": 400000, "IsPort": true, "PortDirection": "R0" }""",
        """{ "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 10000000, "Y": 0, "Text": "2", "Height": 400000, "IsPort": true, "PortDirection": "R180" }""",
        outline ? """{ "$type": "Rect", "Layer": { "Layer": 8, "Datatype": 0 }, "X1": -5000000, "Y1": -10000000, "X2": 15000000, "Y2": 10000000 }""" : null);

    private static string Clay(params string?[] shapes) =>
        """{ "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 1000, "Shapes": [ """ +
        string.Join(", ", shapes.Where(s => s is not null)) + """ ], "Instances": [] }""";

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1)
        => new([new(x0 * Mm, y0 * Mm), new(x1 * Mm, y0 * Mm), new(x1 * Mm, y1 * Mm), new(x0 * Mm, y1 * Mm)]);

    private static PlanarPolygon Poly(Em3dPrimitive p)
    {
        var e = Assert.IsType<Em3dExtrudedPolygon>(p);
        return new PlanarPolygon([.. e.Outline.Select(q => new EmPoint(q.X, q.Y))],
                                 [.. e.Holes.Select(h => (IReadOnlyList<EmPoint>)[.. h.Select(q => new EmPoint(q.X, q.Y))])]);
    }

    private sealed class Tol(double eps) : IEqualityComparer<(double, double, double, double)>
    {
        public bool Equals((double, double, double, double) a, (double, double, double, double) b)
            => Math.Abs(a.Item1 - b.Item1) <= eps && Math.Abs(a.Item2 - b.Item2) <= eps &&
               Math.Abs(a.Item3 - b.Item3) <= eps && Math.Abs(a.Item4 - b.Item4) <= eps;
        public int GetHashCode((double, double, double, double) o) => 0;
    }
}
