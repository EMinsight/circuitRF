// brief-railrf-36 — a six-layer board in seconds. One test per claim:
//
//   1. The via-to-copper decision is still Clipper's at the dilation boundary, now that it is asked
//      locally (R-rail36-2) — straight and diagonal edges, either side of the boundary.
//   2. A barrel against a holed plane is decided without clipping the whole plane (R-rail36-2) — a
//      counter of vertices handed to Clipper, never a clock.
//   3. Two rails and a re-run on the same board extract connectivity once (R-rail36-3).
//
// The field board itself (six layers, 3,671 pieces, 893,563 vertices) stays outside the repo; its
// numbers are in src/Design/RESOLVED.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class BoardScaleConnectivityTests
{
    private static readonly LayerKey Top = new(1, 0), Plane = new(2, 0), Bot = new(3, 0), Via = new(9, 0);

    private static Technology Stackup(bool groundReference = false)
    {
        var tech = new Technology { Name = "board scale" };
        StackupLayer Cu(string name, LayerKey key) => new()
        {
            Kind = StackupKind.Conductor, Name = name, ThicknessDbu = 35, SigmaSm = 5.8e7, DrawingLayers = [key],
        };
        tech.Stackup.Layers =
        [
            Cu("TOP", Top),
            new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [Via], SpanFromLayer = "TOP", SpanToLayer = "BOT" },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = 200_000, Epsr = 4.3 },
            Cu("PLANE", Plane),
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = 400_000, Epsr = 4.3 },
            Cu("BOT", Bot),
        ];
        tech.Stackup.Layers[5].IsGroundReference = groundReference;
        return tech;
    }

    private static Path64 Box(long x0, long y0, long x1, long y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    private static Path64 Diamond(long cx, long cy, long r) => [new(cx + r, cy), new(cx, cy + r), new(cx - r, cy), new(cx, cy - r)];

    private static Path64 Reversed(Path64 p) { var q = new Path64(p); q.Reverse(); return q; }

    private static Path64 Circle(long cx, long cy, long r)
    {
        var p = new Path64();
        for (int i = 0; i < 32; i++)
            p.Add(new Point64(cx + (long)Math.Round(r * Math.Cos(i * Math.PI / 16)), cy + (long)Math.Round(r * Math.Sin(i * Math.PI / 16))));
        return p;
    }

    /// <summary>A 1,000,000-DBU plane with <paramref name="holes"/> round antipads of 32 vertices on a
    /// 30,000-DBU pitch, so it is well past the size the local test starts at.</summary>
    private static Paths64 HoledPlane(int holes, Path64? extraHole = null)
    {
        var plane = new Paths64 { Box(0, 0, 1_000_000, 1_000_000) };
        for (int i = 0; i < holes; i++)
        {
            long x = 20_000 + (i % 30) * 30_000, y = 20_000 + (i / 30) * 30_000;
            plane.Add(Reversed(Circle(x + 5_000, y + 5_000, 5_000)));
        }
        if (extraHole is not null) plane.Add(Reversed(extraHole));
        return plane;
    }

    /// <summary>A barrel and a pad on each outer layer under it, so it joins TOP and BOT whatever the
    /// plane decides — the question is only whether the plane is in the net.</summary>
    private static Dictionary<LayerKey, Paths64> Board(Paths64 plane, Paths64 barrels)
    {
        var pads = new Paths64();
        foreach (var b in barrels)
        {
            long cx = (b.Min(p => p.X) + b.Max(p => p.X)) / 2, cy = (b.Min(p => p.Y) + b.Max(p => p.Y)) / 2;
            pads.Add(Box(cx - 3_000, cy - 3_000, cx + 3_000, cy + 3_000));
        }
        return new Dictionary<LayerKey, Paths64>
        {
            [Top] = DrcRegions.Union(pads),
            [Plane] = DrcRegions.Union(plane),
            [Bot] = DrcRegions.Union(pads),
            [Via] = DrcRegions.Union(barrels),
        };
    }

    private static bool PlaneJoinsBarrel(IReadOnlyList<DrcNetPiece> pieces) =>
        pieces.Single(p => p.Layer == Plane).Net == pieces.Single(p => p.Layer == Via).Net;

    /// <summary>What the decision was before brief 36, written out as the reference: the barrel grown
    /// by one DBU, intersected with the WHOLE piece.</summary>
    private static bool WholePieceClipTouches(Paths64 barrel, Paths64 plane)
    {
        var grown = Clipper.InflatePaths(barrel, 1.0, JoinType.Miter, EndType.Polygon, 2.0);
        return Clipper.BooleanOp(ClipType.Intersection, grown, DrcRegions.Union(plane), FillRule.NonZero).Count > 0;
    }

    /// <summary>
    /// <b>Claim 1.</b> A barrel exactly on an antipad's edge touches the plane and one a single DBU
    /// inside it does not — the dilation meets that edge along a line, which Clipper reports as
    /// nothing. The local test clips the near rings unmodified, so both straight and diagonal edges
    /// decide exactly as the whole-plane clip did, on both sides of the boundary.
    /// </summary>
    [Fact]
    public void TheTouchDecisionAtTheDilationBoundaryIsTheWholePlaneClips()
    {
        var tech = Stackup();
        var outcomes = new HashSet<(string Edge, bool Touches)>();

        // Straight: a square antipad [500k, 520k]², the barrel's right edge `gap` inside its right edge.
        foreach (long gap in new long[] { 0, 1 })
        {
            var plane = HoledPlane(40, Box(500_000, 500_000, 520_000, 520_000));
            var barrel = new Paths64 { Box(516_000 - gap, 508_000, 520_000 - gap, 512_000) };

            using var cache = ConnectivityCache.BeginIsolated();
            bool joined = PlaneJoinsBarrel(DrcConnectivity.Extract(Board(plane, barrel), tech));

            Assert.Equal(WholePieceClipTouches(barrel, plane), joined);
            Assert.Equal(gap == 0, joined);
            outcomes.Add(("straight", joined));
        }

        // Diagonal: a diamond antipad and a diamond barrel in its right-hand corner, their 45° edges
        // parallel and `gap` apart in x + y (gap/√2 DBU).
        foreach (long gap in new long[] { 0, 1, 2, 3 })
        {
            var plane = HoledPlane(40, Diamond(510_000, 510_000, 10_000));
            var barrel = new Paths64 { Diamond(518_000 - gap, 510_000, 2_000) };

            using var cache = ConnectivityCache.BeginIsolated();
            bool joined = PlaneJoinsBarrel(DrcConnectivity.Extract(Board(plane, barrel), tech));

            Assert.Equal(WholePieceClipTouches(barrel, plane), joined);
            outcomes.Add(("diagonal", joined));
        }

        // Both sides of the boundary were actually visited on both kinds of edge.
        Assert.Contains(("straight", true), outcomes);
        Assert.Contains(("straight", false), outcomes);
        Assert.Contains(("diagonal", true), outcomes);
        Assert.Contains(("diagonal", false), outcomes);
    }

    /// <summary>
    /// <b>Claim 2.</b> Two hundred barrels against a plane with two hundred antipads — half standing
    /// in a clearance, half on the copper — are decided with fewer plane vertices handed to Clipper
    /// than ONE whole-plane clip holds. Before brief 36 it was two hundred of them.
    /// </summary>
    [Fact]
    public void ABarrelAgainstAHoledPlaneIsDecidedWithoutClippingThePlane()
    {
        var tech = Stackup();
        var plane = HoledPlane(200);
        long planeVertices = plane.Sum(p => (long)p.Count);

        var inClearance = new Paths64();
        var onCopper = new Paths64();
        for (int i = 0; i < 200; i++)
        {
            long x = 20_000 + (i % 30) * 30_000, y = 20_000 + (i / 30) * 30_000;
            if (i % 2 == 0) inClearance.Add(Box(x + 4_000, y + 4_000, x + 6_000, y + 6_000));   // mid-antipad
            else onCopper.Add(Box(x + 18_000, y + 4_000, x + 20_000, y + 6_000));                // between antipads
        }

        using var cache = ConnectivityCache.BeginIsolated();
        using var scope = ConnectivityCounters.Begin();
        var pieces = DrcConnectivity.Extract(Board(plane, [.. inClearance, .. onCopper]), tech);

        int planeNet = pieces.Single(p => p.Layer == Plane).Net;
        var barrels = pieces.Where(p => p.Layer == Via).ToList();
        Assert.Equal(100, barrels.Count(b => b.Net == planeNet));
        Assert.Equal(100, barrels.Count(b => b.Net != planeNet));

        var c = scope.Counters;
        Assert.Equal(1, c.Extractions);
        Assert.True(c.VerticesClipped < planeVertices,
            $"{c.VerticesClipped} vertices clipped over {c.Clips} clips; one whole-plane clip is {planeVertices}");
    }

    /// <summary>
    /// <b>Claim 3.</b> A document with two rails, run twice, partitions the board's copper ONCE: each
    /// rail's walk and the re-run ask the same artwork the same question, and the cache is keyed on
    /// the copper and the stackup, not on the objects they arrived in.
    /// </summary>
    [Fact]
    public void TwoRailsAndAReRunExtractConnectivityOnce()
    {
        const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);

        RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
            new() { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };
        PlacedPin Pad(string refdes, string pin, string net, double x, double y) =>
            new PlacedPin(refdes, pin, net, Mm(x), Mm(y), PinSource.BoardNetlist) { Layer = Top };

        var doc = new RailDocument { Name = "two rails" };
        foreach (var (name, src, load) in new[] { ("A", "BT1", "U1"), ("B", "BT2", "U2") })
        {
            var rail = new RailSpec { Name = name, NetName = name, ReferenceLayer = Bot };
            rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = src, Pin = "1" }, OpenCircuitVoltageV = 3.3 });
            rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = load, Pin = "1" }, DcCurrentA = 0.1 });
            doc.Rails.Add(rail);
        }

        IReadOnlyList<PlacedPin> pads =
        [
            Pad("BT1", "1", "A", 0.5, 0.15), Pad("U1", "1", "A", 9.5, 0.15),
            Pad("BT2", "1", "B", 0.5, 2.15), Pad("U2", "1", "B", 9.5, 2.15),
        ];
        var request = new RailDcRequest
        {
            Document = doc, Technology = Stackup(groundReference: true), DbuPerMicron = DbuPerMicron,
            Shapes = [Rect(Top, 0, 0, 10, 0.3), Rect(Top, 0, 2, 10, 2.3), Rect(Bot, -1, -3, 11, 5)],
            Pads = pads, NetPoints = [.. pads.Select(p => new PdnNetPoint(p.Net!, p.X, p.Y) { Layer = Top })],
        };

        using var cache = ConnectivityCache.BeginIsolated();
        using var scope = ConnectivityCounters.Begin();
        var first = RailDcRun.Run(request);
        var again = RailDcRun.Run(request);

        Assert.Null(first.Refusal);
        Assert.Equal(2, first.Rails.Count);
        Assert.Equal(first.Rails.Select(r => r.RailName), again.Rails.Select(r => r.RailName));
        Assert.Equal(1, scope.Counters.Extractions);
        Assert.True(scope.Counters.CacheHits >= 3, $"{scope.Counters.CacheHits} cache hits");
    }
}
