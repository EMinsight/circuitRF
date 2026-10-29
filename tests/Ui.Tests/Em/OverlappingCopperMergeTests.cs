// Overlapping copper on one level reaches the kernel as ONE conductor (round-7 field report): a placed
// part's footprint pad over an imported board's own pad was two polygons, the feed-lead decision read
// one of them, grew no lead, and the published S11 was an open circuit.
//
// Brief PM-1 R-pm1-2: copper that only TOUCHES is one conductor too. Imported artwork arrives as pieces
// drawn edge to edge, and the mesher sized the whole part from the narrowest PIECE — a wedge where a
// mitred bend or a notched T-junction was cut in two.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class OverlappingCopperMergeTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);
    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    [Fact]
    public void OverlappingShapesAreMerged_AndASeparateOneIsLeftAlone()
    {
        var separate = new RectShape { Layer = TopCopper, X1 = Mm(30), Y1 = 0, X2 = Mm(32), Y2 = Mm(2.9) };
        var x = PlanarExtractor.Extract(
        [
            new RectShape { Layer = TopCopper, X1 = 0,        Y1 = 0,         X2 = Mm(20),   Y2 = Mm(2.9) },  // the board's line
            new RectShape { Layer = TopCopper, X1 = Mm(19),   Y1 = Mm(-0.2),  X2 = Mm(20.1), Y2 = Mm(2.6) },  // a footprint pad over its end
            separate,
        ], StarterTechnologies.Pcb2Layer(), Dbu, 5e9);

        Assert.True(x.Ok, x.Refusal);
        Assert.Equal(2, Assert.Single(x.Problem!.Layers).Polygons.Count);
        Assert.Contains(x.Notes, n => n.StartsWith("2 overlapping or touching conductor shape(s) were merged into 1", StringComparison.Ordinal));
    }

    [Fact]
    public void ShapesThatShareAnEdge_AreMerged_AndACornerOnlyTouchIsNot()
    {
        var x = PlanarExtractor.Extract(
        [
            new RectShape { Layer = TopCopper, X1 = 0,      Y1 = 0,       X2 = Mm(20), Y2 = Mm(2.9) },
            new RectShape { Layer = TopCopper, X1 = Mm(20), Y1 = 0,       X2 = Mm(25), Y2 = Mm(2.9) },   // abuts, no overlap
            new RectShape { Layer = TopCopper, X1 = Mm(25), Y1 = Mm(2.9), X2 = Mm(27), Y2 = Mm(5) },     // a corner only
        ], StarterTechnologies.Pcb2Layer(), Dbu, 5e9);

        Assert.True(x.Ok, x.Refusal);
        Assert.Equal(2, Assert.Single(x.Problem!.Layers).Polygons.Count);
        Assert.Contains(x.Notes, n => n.StartsWith("2 overlapping or touching conductor shape(s) were merged into 1", StringComparison.Ordinal));
    }

    private static PolygonShape Poly(params (double X, double Y)[] mm)
        => new() { Layer = TopCopper, Xy = [.. mm.SelectMany(p => new[] { Mm(p.X), Mm(p.Y) })] };

    private static double Narrowest(IReadOnlyList<LayoutShape> shapes)
    {
        var x = PlanarExtractor.Extract([.. shapes], StarterTechnologies.Pcb2Layer(), Dbu, 5e9);
        Assert.True(x.Ok, x.Refusal);
        var (nx, ny) = SurfaceMesher.MeasureNarrowness(x.Problem!);
        return Math.Min(nx, ny);
    }

    /// <summary>R-pm1-5: a 0.5 mm L with a MITRED join, drawn as the two pieces either side of the mitre — each
    /// piece ends in a wedge — measures what the same L drawn as one polygon measures.</summary>
    [Fact]
    public void AnLDrawnAsTwoAbuttingPieces_MeasuresTheSameNarrowestWidthAsTheLDrawnWhole()
    {
        double whole = Narrowest([Poly((0, 0), (5, 0), (5, 4), (4.5, 4), (4.5, 0.5), (0, 0.5))]);
        double pieces = Narrowest(
        [
            Poly((0, 0), (5, 0), (4.5, 0.5), (0, 0.5)),     // the arm, its far end mitred
            Poly((5, 0), (5, 4), (4.5, 4), (4.5, 0.5)),     // the leg, mitred to match
        ]);

        Assert.Equal(0.5e-3, whole, 1e-9);
        Assert.Equal(whole, pieces, 1e-12);
    }

    /// <summary>R-pm1-5: a 0.2 mm line notched from the right where a 0.4 mm arm meets it from the left, the
    /// notched stretch drawn as two trapezoids the way an import delivers it. Each trapezoid's vertical runs fall to
    /// nothing at the notch's tip, and it was one of those that sized the mesh.</summary>
    [Fact]
    public void ANotchedTDrawnAsTwoTrapezoidsAndALine_ReportsNoWidthBelowTheLine()
    {
        double narrowest = Narrowest(
        [
            new RectShape { Layer = TopCopper, X1 = 0,       Y1 = 0,       X2 = Mm(0.2), Y2 = Mm(1) },
            Poly((0, 1), (0.2, 1), (0.1, 1.2), (0, 1.2)),                  // below the notch's tip
            Poly((0, 1.2), (0.1, 1.2), (0.2, 1.4), (0, 1.4)),              // above it
            new RectShape { Layer = TopCopper, X1 = 0,       Y1 = Mm(1.4), X2 = Mm(0.2), Y2 = Mm(3) },
            new RectShape { Layer = TopCopper, X1 = Mm(-2),  Y1 = Mm(1),   X2 = 0,       Y2 = Mm(1.4) },  // the arm
        ]);

        Assert.True(narrowest >= 0.2e-3 * (1 - 1e-9), $"narrowest {narrowest * 1e6:F1} µm, below the 200 µm line");
    }

    /// <summary>brief-em3d-52 — the 3D generator's merge also joins shapes that share an EDGE (a line ending exactly on a
    /// pad), which as separate solids leave the mesher a fused face no conductor claims; a corner-only touch stays two.</summary>
    [Fact]
    public void TheThreeDMerge_JoinsShapesSharingAnEdge_ButNotACornerOnlyTouch()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        List<(LayoutShape, int)> Shapes(params RectShape[] r) => [.. r.Select(x => ((LayoutShape)x, 0))];
        var line = new RectShape { Layer = TopCopper, X1 = 0,      Y1 = Mm(0.5), X2 = Mm(20), Y2 = Mm(1.5) };
        var pad  = new RectShape { Layer = TopCopper, X1 = Mm(20), Y1 = 0,       X2 = Mm(22), Y2 = Mm(2) };
        var corner = new RectShape { Layer = TopCopper, X1 = Mm(22), Y1 = Mm(2), X2 = Mm(24), Y2 = Mm(4) };

        Assert.Single(PlanarExtractor.MergeOverlapping(Shapes(line, pad), tech, out _, out _, touching: true));
        Assert.Equal(2, PlanarExtractor.MergeOverlapping(Shapes(line, pad), tech, out _, out _).Count);
        Assert.Equal(2, PlanarExtractor.MergeOverlapping(Shapes(pad, corner), tech, out _, out _, touching: true).Count);
    }
}
