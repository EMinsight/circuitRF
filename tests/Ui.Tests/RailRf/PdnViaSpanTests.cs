// ================================================================
//  PdnViaSpanTests.cs — brief-railrf-38-vias-through-inner-copper.md §6
//
//  A plated barrel joins every conductor it passes that has copper at its centre, in the readings as
//  in the region walk. Before this brief both readings stamped ONE resistor between a via's span
//  ENDS, so a supply that went Top → via → an inner layer → via → Top was connected in the walk and
//  not in the netlist. One test per claim:
//
//    1. that board solves in both models, and each barrel is a named breakdown row;
//    2. with copper on every layer, the segments add up to today's full-span barrel, and the via
//       check still reads the hole as ONE barrel;
//    3. a clearance ring on the inner layer is not joined, and the walk says the same;
//    4. a hole the walk joins through and the reading cannot stamp — a plated slot — is a note.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PdnViaSpanTests
{
    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Gnd = new(2, 0);
    private static readonly LayerKey In2 = new(3, 0);
    private static readonly LayerKey Bot = new(4, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);

    private const double ViaA = 8.0, ViaB = 22.0, Y = 2.0;

    /// <summary>The note R-rail38-3 adds — its opening words, which nothing else prints.</summary>
    private const string DroppedHole = "The region walk joins the rail's copper through";

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    /// <summary>TOP, the reference GND, IN2 and BOT; one plated through-hole entry TOP → BOT.</summary>
    private static Technology Board()
    {
        StackupLayer Copper(string name, double um, LayerKey layer, bool reference = false) => new()
        {
            Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Um(um), SigmaSm = 5.8e7,
            DrawingLayers = [layer], IsGroundReference = reference,
        };
        StackupLayer Core(string name, double mm) =>
            new() { Kind = StackupKind.Dielectric, Name = name, ThicknessDbu = Mm(mm), Epsr = 4.3 };

        var tech = new Technology { Name = "four layer" };
        tech.Stackup.Layers =
        [
            Copper("TOP", 35, Top), Core("D1", 0.2), Copper("GND", 17.5, Gnd, reference: true),
            Core("D2", 1.0), Copper("IN2", 17.5, In2), Core("D3", 0.2), Copper("BOT", 35, Bot),
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer], Fill = ViaFillKind.Plated,
                WallThicknessDbu = Um(25), SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
        ];
        return tech;
    }

    /// <summary>The full span today's barrel is measured over: TOP's near face to BOT's far face.</summary>
    private const double FullSpanMetres = (35 + 200 + 17.5 + 1000 + 17.5 + 200 + 35) * 1e-6;

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    private static ViaShape Via(double x) => new()
    {
        Layer = ViaLayer, LandingLayer = Top, X = Mm(x), Y = Mm(Y), DrillSize = Mm(0.3), PadSize = Mm(0.5),
    };

    /// <summary>A 1 mm square clearance round each hole on <paramref name="layer"/>, the rest of a
    /// 30 × 4 mm plate filled.</summary>
    private static IEnumerable<LayoutShape> Plate(LayerKey layer, params double[] holes)
    {
        double x = 0;
        foreach (double h in holes.OrderBy(v => v))
        {
            yield return Rect(layer, x, 0, h - 0.5, 4);
            yield return Rect(layer, h - 0.5, 0, h + 0.5, Y - 0.5);
            yield return Rect(layer, h - 0.5, Y + 0.5, h + 0.5, 4);
            x = h + 0.5;
        }
        yield return Rect(layer, x, 0, 30, 4);
    }

    /// <summary>
    /// The field report's shape: the source's run on TOP, a via down to IN2, IN2 across, a via back
    /// up, and the load's run on TOP. Nothing on BOT, so both barrels' span ENDS are not both on the
    /// rail — which is exactly what the readings used to skip.
    /// </summary>
    private static List<LayoutShape> UpDownUp() =>
    [
        Rect(Top, 0, Y - 0.25, ViaA + 0.3, Y + 0.25),
        Rect(In2, ViaA - 0.3, Y - 0.25, ViaB + 0.3, Y + 0.25),
        Rect(Top, ViaB - 0.3, Y - 0.25, 30, Y + 0.25),
        Via(ViaA), Via(ViaB),
        .. Plate(Gnd, ViaA, ViaB),
    ];

    private static RailDcResult Solve(List<LayoutShape> shapes, PdnModelKind model)
    {
        var doc = new RailDocument { Name = "via span" };
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Gnd };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.3,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 1.0 });
        doc.Rails.Add(rail);

        var run = RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Technology = Board(),
            DbuPerMicron = DbuPerMicron,
            Shapes = shapes,
            Pads =
            [
                new PlacedPin("BT1", "1", "VDD", Mm(0.5), Mm(Y), PinSource.BoardNetlist),
                new PlacedPin("U1", "VDD", "VDD", Mm(29.5), Mm(Y), PinSource.BoardNetlist),
            ],
            Model = model,
            Mesh = new PdnMeshSettings { CellSizeMetres = 0.1e-3, PortRefinementRatio = 1 },
        });

        Assert.Null(run.Refusal);
        return run.Rails[0];
    }

    private static List<PdnViaBarrel> BarrelsAt(RailDcResult r, double xMm) =>
        [.. r.Netlist.Origins.Where(o => o.Kind == PdnOriginKind.Via && o.Barrel!.X == Mm(xMm)).Select(o => o.Barrel!)];

    // ── 1 ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rail copper on TOP and IN2, none on BOT, joined only by two through vias: it solves in both
    /// readings, each barrel is a breakdown row naming the two layers it joins, and nothing here is
    /// the dropped-hole note — the walk and the reading agree.
    /// </summary>
    [Theory]
    [InlineData(PdnModelKind.Accurate)]
    [InlineData(PdnModelKind.Fast)]
    public void AViaDownToAnInnerLayerAndBackUpSolves_AndEachBarrelIsANamedRow(PdnModelKind model)
    {
        var r = Solve(UpDownUp(), model);

        Assert.True(r.Ports[0].DropV > 0);
        var viaRows = r.Breakdown.Where(b => b.Label.Contains("plated via from TOP to IN2", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, viaRows.Count);
        Assert.All(viaRows, b => Assert.True(b.DropV > 0));

        Assert.DoesNotContain(r.Notes, n => n.Contains(DroppedHole, StringComparison.Ordinal));
    }

    // ── 2 ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// With rail copper on TOP, IN2 and BOT at a hole, the barrel is two segments whose lengths and
    /// resistances add up to today's full-span barrel to 1e-12 — the outer faces at the span's ends,
    /// IN2's centre between. And the via check reads each hole as ONE barrel, TOP → BOT, over the
    /// full span, carrying its larger segment current.
    /// </summary>
    [Fact]
    public void WithCopperOnEveryLayerTheSegmentsAddUpToTheFullSpanBarrel_AndTheViaCheckSeesOneHole()
    {
        var shapes = UpDownUp();
        shapes.Add(Rect(Bot, ViaA - 0.3, Y - 0.3, ViaA + 0.3, Y + 0.3));
        shapes.Add(Rect(Bot, ViaB - 0.3, Y - 0.3, ViaB + 0.3, Y + 0.3));

        var r = Solve(shapes, PdnModelKind.Accurate);

        var segments = BarrelsAt(r, ViaA);
        Assert.Equal(2, segments.Count);
        Assert.Equal((Top, In2), (segments[0].FromLayer, segments[0].ToLayer));
        Assert.Equal((In2, Bot), (segments[1].FromLayer, segments[1].ToLayer));

        double span = segments.Sum(s => s.SpanMetres);
        Assert.Equal(FullSpanMetres, span, 1e-12);

        // R is ρ·h/A over one annulus, so the whole barrel is either segment scaled to the full span.
        double whole = segments[0].ResistanceOhms * FullSpanMetres / segments[0].SpanMetres;
        Assert.True(Math.Abs(segments.Sum(s => s.ResistanceOhms) - whole) <= 1e-12 * whole);

        var t = r.ViaCheck.Transitions.Single(t => t.Worst!.Barrel.X == Mm(ViaA));
        var hole = Assert.Single(t.Vias);
        Assert.Equal((Top, Bot), (t.FromLayer, t.ToLayer));
        Assert.Equal(FullSpanMetres, hole.Barrel.SpanMetres, 1e-12);
    }

    // ── 3 ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// IN2 continues past the second hole with a clearance ring round it; BOT carries a parallel run
    /// between the two holes so the rail still goes through. At the ringed hole the barrel joins TOP
    /// and BOT and not IN2 — one whole TOP → BOT barrel, exactly as the walk joined it — while the
    /// first hole, where IN2 is solid, is segmented.
    /// </summary>
    [Fact]
    public void AClearanceRingOnTheInnerLayerIsNotJoined_ExactlyAsTheWalkSays()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, Y - 0.25, ViaA + 0.3, Y + 0.25),
            Rect(Top, ViaB - 0.3, Y - 0.25, 30, Y + 0.25),
            Rect(Bot, ViaA - 0.3, Y - 0.25, ViaB + 0.3, Y + 0.25),
            // IN2: solid at the first hole, a 0.6 mm ring cleared round the second.
            Rect(In2, ViaA - 0.3, Y - 0.5, ViaB - 0.3, Y + 0.5),
            Rect(In2, ViaB + 0.3, Y - 0.5, ViaB + 1.0, Y + 0.5),
            Rect(In2, ViaB - 0.3, Y - 0.5, ViaB + 0.3, Y - 0.3),
            Rect(In2, ViaB - 0.3, Y + 0.3, ViaB + 0.3, Y + 0.5),
            Via(ViaA), Via(ViaB),
        };
        shapes.AddRange(Plate(Gnd, ViaA, ViaB));

        var r = Solve(shapes, PdnModelKind.Accurate);

        Assert.Equal(2, BarrelsAt(r, ViaA).Count);

        var ringed = Assert.Single(BarrelsAt(r, ViaB));
        Assert.Equal((Top, Bot), (ringed.FromLayer, ringed.ToLayer));
        Assert.Equal(FullSpanMetres, ringed.SpanMetres, 1e-12);

        // The walk's own answer at the same hole, which is what the reading took: TOP and BOT.
        var walked = Assert.Single(r.Regions!.Holes, h => Math.Abs(h.X - Mm(ViaB)) < Mm(0.01));
        Assert.Equal([Top, Bot], walked.Touched.OrderBy(l => l.Layer));
        Assert.DoesNotContain(r.Notes, n => n.Contains(DroppedHole, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same board with the reference plane SOLID under both holes: each barrel touches it on its
    /// way through. The reading passes it by — the rail is never joined to its own return inside the
    /// netlist — and the run says the board shorts there, which before this brief nothing did.
    /// </summary>
    [Fact]
    public void ABarrelTouchingTheReferenceOnItsWayThroughIsPassedBy_AndNamed()
    {
        var shapes = UpDownUp().Where(x => x.Layer != Gnd).ToList();
        shapes.Add(Rect(Gnd, 0, 0, 30, 4));

        var r = Solve(shapes, PdnModelKind.Accurate);

        Assert.Equal((Top, In2), (BarrelsAt(r, ViaA)[0].FromLayer, BarrelsAt(r, ViaA)[0].ToLayer));
        var note = Assert.Single(r.Notes, n => n.Contains("touch the reference copper", StringComparison.Ordinal));
        Assert.StartsWith("2 of the rail's via barrel(s)", note, StringComparison.Ordinal);
        Assert.Contains("on GND", note, StringComparison.Ordinal);
    }

    // ── 4 ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rail38-3. A plated SLOT on the via layer — geometry the walk joins through and a reading,
    /// which prices round holes, never stamps — beside the round via that carries the rail. The rail
    /// solves, and the run says which hole and which layers were left out.
    /// </summary>
    [Fact]
    public void AHoleTheWalkJoinsAndTheReadingCannotStampIsNamed()
    {
        const double slot = 12.0;
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, Y - 0.25, slot + 0.5, Y + 0.25),
            Rect(In2, ViaA - 0.3, Y - 0.25, ViaB + 0.3, Y + 0.25),
            Rect(Top, ViaB - 0.3, Y - 0.25, 30, Y + 0.25),
            Rect(ViaLayer, slot - 0.15, Y - 0.4, slot + 0.15, Y + 0.4),
            Via(ViaA), Via(ViaB),
        };
        shapes.AddRange(Plate(Gnd, ViaA, slot, ViaB));

        var r = Solve(shapes, PdnModelKind.Accurate);

        var note = Assert.Single(r.Notes, n => n.Contains(DroppedHole, StringComparison.Ordinal));
        Assert.Contains("1 hole(s)", note, StringComparison.Ordinal);
        Assert.Contains("joining TOP, IN2 — no barrel stamped", note, StringComparison.Ordinal);
    }
}
