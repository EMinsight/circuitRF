using System.Numerics;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Engine.Tests.Em3d;

// brief-dfr27-5 R-dfr27-5-1 — lines that nearly coincide share a line when they are closer than a quarter of the local cell
// they sit in, not only when they are closer than MinCell. Counters only: line counts, cell sizes, where the merged line went.

public sealed class FdtdNearFeatureMergeTests
{
    private const double Um = 1e-6;

    /// <summary>
    /// The round-26 board's shape: two strips 150 µm wide on two layers, the second 93 µm along from the first. Each edge's
    /// thirds pair (local cell 90 µm, three fifths of the width) puts a line 3 µm from one of the other strip's, three times
    /// over. Each such pair becomes one line, reported with its local cell, and no 3 µm cell is left.
    /// </summary>
    [Fact]
    public void TwoLayersEdges_ThirdsLinesAFewMicronsApart_ShareOneLine()
    {
        var box = Box(-2000, 2000, -2000, 2000, 0, 1254);
        var p = new Em3dProblem(
            [new Em3dSolid("substrate", "Alumina", Em3dRole.Dielectric, new Em3dBox(new Point3(-2000 * Um, -2000 * Um, 0), new Point3(2000 * Um, 2000 * Um, 254 * Um)), 2),
             new Em3dSolid("air", "Air", Em3dRole.Air, new Em3dBox(box.Min, box.Max), 1)],
            [Sheet("top", -1500, 1500, -150, 0, 254), Sheet("bottom", -1500, 1500, -57, 93, 100)],
            Materials, [], box, new Em3dFrequency(1e9, 20e9, 20, Em3dSweepKind.Linear), 20);

        // MinCell as the board's (1 µm, a tenth of its smallest copper) — this fixture's own default, a tenth of 1.5 mm, would
        // merge the two lines without the rule.
        var g = FdtdGrid.Build(p, OpenEmsGridSettings.Default with { MinCellM = 1 * Um }, long.MaxValue);
        var merges = g.Merges.Where(m => m.Axis == FdtdAxis.Y && m.LocalCellM is not null).ToList();
        Assert.Equal(3, merges.Count);
        Assert.All(merges, m =>
        {
            Assert.Contains(m.First.Concat(m.Second), s => s.Feature == "top");
            Assert.Contains(m.First.Concat(m.Second), s => s.Feature == "bottom");
            Assert.Contains("a quarter of their local cell (90 µm)", m.Sentence);
        });
        Assert.True(g.Y.SmallestCellM > 20 * Um, $"smallest y cell {g.Y.SmallestCellM / Um} µm");
    }

    /// <summary>
    /// Who merges: two features' thirds lines (at their midpoint), a thirds line and another feature's own line (onto the
    /// feature's line), and a planar outline's extreme and a face (onto the face). Who keeps MinCell: one conductor's lines
    /// from edges with its metal on opposite sides, two lines with no thirds cell, a solid's extreme, and any of them when the
    /// caller does not ask for the rule.
    /// </summary>
    [Fact]
    public void WhatMerges_AndWhereTheLineGoes()
    {
        const double h = 150 * Um;
        // An outside line has its metal on the far side of the edge; an inside line on its own side.
        var below = Line(100, new FdtdLineSource("a", FdtdLineKind.ThirdsOutside, 0, h), h);
        var aboveOther = Line(103, new FdtdLineSource("b", FdtdLineKind.ThirdsInside, 53 * Um, h), h);
        var aboveSame = Line(103, new FdtdLineSource("a", FdtdLineKind.ThirdsInside, 53 * Um, h), h);
        var edge = Line(110, new FdtdLineSource("b", FdtdLineKind.MetalEdge, 110 * Um));
        var face = Line(110, new FdtdLineSource("fill", FdtdLineKind.MaterialFace, 110 * Um));
        var outline = Line(108, new FdtdLineSource("pour", FdtdLineKind.MetalExtreme, 108 * Um) { Outline = true });
        var solid = Line(108, new FdtdLineSource("barrel", FdtdLineKind.MetalExtreme, 108 * Um));
        var far = Line(400, new FdtdLineSource("c", FdtdLineKind.MetalEdge, 400 * Um));
        var near = Line(0, new FdtdLineSource("d", FdtdLineKind.MetalEdge, 0));

        Assert.Equal([101.5, 400], Merged(below, aboveOther, far));
        Assert.Equal([110, 400], Merged(below, edge, far));
        Assert.Equal([0, 110, 400], Merged(near, outline, face, far));

        Assert.Equal([100, 103, 400], Merged(below, aboveSame, far));
        Assert.Equal([0, 108, 110, 400], Merged(near, solid, face, far));
        Assert.Equal([100, 110, 400], Merged(Line(100, new FdtdLineSource("e", FdtdLineKind.MetalEdge, 100 * Um)), face, far));
        Assert.Equal([100, 103, 400], Merged(false, below, aboveOther, far));
    }

    /// <summary>
    /// A slot in ONE pour clamps the thirds rule's cell at its edges, as a gap to separate metal always did: a 200 µm notch
    /// cut into a pour takes a local cell of at most 3/7 of it, set by the pour itself, where it took the 239.5 µm
    /// wavelength cell before and its two edges' pairs crossed.
    /// </summary>
    [Fact]
    public void ASlotInOnePour_ClampsItsEdgesThirds_ToTheGap()
    {
        var box = Box(-2000, 2000, -2000, 2000, 0, 1254);
        // A U: the notch from y = -1000 to the top edge, x = -100..100.
        IReadOnlyList<Point2> u =
        [
            new(-1500 * Um, -1500 * Um), new(1500 * Um, -1500 * Um), new(1500 * Um, 1500 * Um), new(100 * Um, 1500 * Um),
            new(100 * Um, -1000 * Um), new(-100 * Um, -1000 * Um), new(-100 * Um, 1500 * Um), new(-1500 * Um, 1500 * Um),
        ];
        var p = new Em3dProblem(
            [new Em3dSolid("substrate", "Alumina", Em3dRole.Dielectric, new Em3dBox(new Point3(-2000 * Um, -2000 * Um, 0), new Point3(2000 * Um, 2000 * Um, 254 * Um)), 2),
             new Em3dSolid("air", "Air", Em3dRole.Air, new Em3dBox(box.Min, box.Max), 1)],
            [new Em3dSheet("pour", "Copper", u, [], 254 * Um, 5 * Um, 10)],
            Materials, [], box, new Em3dFrequency(1e9, 20e9, 20, Em3dSweepKind.Linear), 20);

        var settings = OpenEmsGridSettings.Default;
        var lines = FdtdGrid.CollectRequired(p, FdtdAxis.X, settings, 1 * Um);
        var notch = lines.SelectMany(l => l.Sources)
                         .Where(s => s.Kind is FdtdLineKind.ThirdsInside or FdtdLineKind.ThirdsOutside && Math.Abs(Math.Abs(s.FeatureAtM) - 100 * Um) < 1e-12)
                         .ToList();
        Assert.Equal(4, notch.Count);
        Assert.All(notch, s =>
        {
            Assert.True(s.LocalCellM <= 3.0 / 7 * 200 * Um + 1e-15, $"local cell {s.LocalCellM / Um} µm");
            Assert.Equal("pour", s.LocalCellSetBy);
        });
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<Em3dMaterial> Materials =
    [
        new("Air", 1, null, 0, 1, 0),
        new("Alumina", 9.8, null, 0, 1, 0),
        new("Copper", 1, null, 0, 1, 5.8e7),
    ];

    private static FdtdRequiredLine Line(double atUm, FdtdLineSource source, double hint = double.PositiveInfinity)
        => new(atUm * Um, false, [source], hint);

    private static List<double> Merged(params FdtdRequiredLine[] lines) => Merged(true, lines);

    private static List<double> Merged(bool nearFeatures, params FdtdRequiredLine[] lines)
        => [.. FdtdGrid.Merge(lines, FdtdAxis.Y, 1 * Um, [], [], nearFeatures).Select(l => Math.Round(l.PositionM / Um, 9))];

    private static Em3dAirBox Box(double x0, double x1, double y0, double y1, double z0, double z1)
        => new(new Point3(x0 * Um, y0 * Um, z0 * Um), new Point3(x1 * Um, y1 * Um, z1 * Um),
               new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                             Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing));

    private static Em3dSheet Sheet(string name, double x0, double x1, double y0, double y1, double zUm)
        => new(name, "Copper", [new(x0 * Um, y0 * Um), new(x1 * Um, y0 * Um), new(x1 * Um, y1 * Um), new(x0 * Um, y1 * Um)], [],
               zUm * Um, 5 * Um, 10);
}
