// ================================================================
//  RailAnchorSidesTests.cs — brief-railrf-37
//
//  A bare coordinate resolves on the side parts are mounted on (§1); the netlist a CAD tool wrote
//  beside its Gerbers is found and offered (§2); a series row with no terminals typed takes its
//  part's own two pads, and one not on the board is said to be (§3); and a hole drawn as a circle on
//  a via-bound drill layer is a barrel to both readings, as it already was to the walk.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailAnchorSidesTests : IDisposable
{
    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Gnd = new(2, 0);
    private static readonly LayerKey In2 = new(3, 0);
    private static readonly LayerKey Bot = new(4, 0);
    private static readonly LayerKey Drill = new(9, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-anchor-sides-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    /// <summary>TOP, the ground plane (the reference), a second inner plane, BOTTOM.</summary>
    private static Technology FourLayer()
    {
        var tech = new Technology { Name = "four" };
        tech.Layers.AddRange([
            new LayerDef { Key = Top, Name = "Top Copper" }, new LayerDef { Key = Gnd, Name = "GND" },
            new LayerDef { Key = In2, Name = "Inner 2" }, new LayerDef { Key = Bot, Name = "Bottom Copper" }]);
        tech.Stackup.Layers =
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP1", ThicknessDbu = Mm(0.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "GND", ThicknessDbu = Um(18), SigmaSm = 5.8e7, DrawingLayers = [Gnd] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(0.8), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "IN2", ThicknessDbu = Um(18), SigmaSm = 5.8e7, DrawingLayers = [In2] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP2", ThicknessDbu = Mm(0.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot] },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    /// <summary>
    /// A supply trace on TOP from BT1 to x = 19.8 mm, the ground plane the reference, a second plane
    /// on IN2 under all of it (another supply, joined to the trace by nothing), and a patch of IN2-only
    /// copper out at x = 30 mm with nothing above or below it.
    /// </summary>
    private static PdnExtractionRequest Board(RailPortAnchor load, params LayoutShape[] more) => new()
    {
        Rail = Rail(load),
        Shapes = [Rect(Top, 0, 0, 20, 0.5), Rect(Gnd, -1, -5, 33, 5), Rect(In2, -1, -4, 23, 4), Rect(In2, 29, -1, 31, 1), .. more],
        Technology = FourLayer(),
        DbuPerMicron = DbuPerMicron,
        Pads = [new PlacedPin("BT1", "1", null, Mm(0.2), Mm(0.25), PinSource.Artwork) { Layer = Top }],
    };

    private static RailSpec Rail(RailPortAnchor load)
    {
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Gnd };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.3 });
        rail.Loads.Add(new RailLoad { Anchor = load, DcCurrentA = 0.03 });
        return rail;
    }

    // ══ §1 — THE MOUNTING SIDE ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The field report's shape: a load dropped on a top-side trace over an inner plane. Brief 34
    /// refused it as two nets; it is Top, with nothing to say, because an inner layer is never a
    /// candidate for a coordinate.
    /// </summary>
    [Fact]
    public void ACoordinateOverTopAndAnInnerPlane_IsTop_WithNoRefusalAndNoNote()
    {
        var x = PdnGraphExtractor.Extract(Board(new RailPortAnchor { Point = (Mm(19.8), Mm(0.25)) }));

        Assert.Null(x.Refusal);
        Assert.Empty(x.AnchorAmbiguities);
        Assert.All(x.Regions!.Power.SelectMany(i => i.Copper), c => Assert.Equal(Top, c.Layer));
        Assert.DoesNotContain(x.Netlist!.Provenance.Notes, n => n.Contains("resolved to", StringComparison.Ordinal));
    }

    /// <summary>
    /// A bottom-side part: a PAD anchor seeds its land, and a bare coordinate on that pad takes the
    /// part's side over the Top copper above it — saying which part decided it.
    /// </summary>
    [Fact]
    public void OnABottomSidePartsPad_BothTheRefdesAndACoordinateMeanBottom()
    {
        var pad = new PlacedPin("U2", "VDD", null, Mm(12), Mm(0.25), PinSource.Artwork) { Layer = Bot };
        var request = Board(new RailPortAnchor { Point = (pad.X, pad.Y) }, Rect(Bot, 11.7, 0, 12.3, 0.5), Rect(Bot, 5, -3, 18, 3))
            with { Pads = [new PlacedPin("BT1", "1", null, Mm(0.2), Mm(0.25), PinSource.Artwork) { Layer = Top }, pad] };

        Assert.Equal(Bot, Assert.Single(PdnAttachments.ResolveLands(new RailPortAnchor { Refdes = "U2" }, request.Pads)).Layer);

        var sides = PdnAnchorSides.Resolve(request, LayerRegions.Build(request.Shapes, request.Technology), Gnd);
        Assert.Null(sides.Refusal);
        Assert.Equal(Bot, sides.Request.Rail.Loads[0].Anchor.Layer);
        var note = Assert.Single(sides.Notes);
        Assert.Contains("U2", note, StringComparison.Ordinal);
        Assert.Contains("'Top Copper'", note, StringComparison.Ordinal);
    }

    /// <summary>Copper on neither outer layer: refused, naming the inner copper and the spelling
    /// that selects it — and offered as a click, like brief 34's candidates.</summary>
    [Fact]
    public void ACoordinateOnInnerCopperOnly_IsRefusedNamingTheInnerLayer()
    {
        var x = PdnGraphExtractor.Extract(Board(new RailPortAnchor { Point = (Mm(30), Mm(0)) }));

        Assert.NotNull(x.Refusal);
        Assert.Contains("'Inner 2' (layer 3/0)", x.Refusal, StringComparison.Ordinal);
        Assert.Contains("\"Layer\": 3", x.Refusal, StringComparison.Ordinal);
        var offer = Assert.Single(x.AnchorAmbiguities);
        Assert.Equal(In2, Assert.Single(offer.Candidates).Layer);
    }

    // ══ §2 — THE NETLIST BESIDE THE GERBERS ══════════════════════════════════════════════════

    /// <summary>
    /// A document naming a CAD tool's part list as its netlist is told, first, that it is a part list
    /// — and then where the IPC-D-356 file sitting in the Gerber folder is, found by content under a
    /// name no extension rule would take.
    /// </summary>
    [Fact]
    public void APartListNamedAsTheNetlist_SaysSoFirst_AndNamesTheNetlistBesideTheGerbers()
    {
        string gerbers = Directory.CreateDirectory(Path.Combine(_root, "gerbers")).FullName;
        File.WriteAllText(Path.Combine(gerbers, "top.art"), "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.1*%\nD10*\nX0Y0D03*\nM02*\n");
        string ipc = Path.Combine(gerbers, "board.txt");
        static string Record(string pin, int counts) =>
            $"317{"VDD",-14}  {"U1-" + pin,-11}D0300PA00X+{counts:000000}Y+{counts:000000}\n";
        File.WriteAllText(ipc, "P  JOB       board\nP  UNITS     CUST 1\n" + Record("1", 1000) + Record("2", 2000) + "999\n");

        string partList = Path.Combine(_root, "external", "parts.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(partList)!);
        File.WriteAllText(partList, "FILE_TYPE = PART_LIST;\nPART 'R1' VALUE '10K';\nEND.\n");

        Assert.Equal(Path.GetFullPath(ipc), BoardNetlistFile.FindBeside(Path.Combine(gerbers, "top.art")));

        var doc = new RailDocument { BoardNetlistRef = "external/parts.dat", ArtworkSourceRef = "gerbers" };
        var read = RailArtwork.ResolveBoardNetlist(doc, Path.Combine(_root, "board.crail"), DbuPerMicron, out _, out string? error);

        Assert.Null(read);
        Assert.StartsWith("it is a part list or bill of materials, not a board netlist", error, StringComparison.Ordinal);
        Assert.Contains($"beside the Gerbers this board was imported from: '{Path.GetFullPath(ipc)}'", error, StringComparison.Ordinal);
    }

    // ══ §3 — SERIES TERMINALS FROM THE BOARD ═════════════════════════════════════════════════

    /// <summary>
    /// A series row with its refdes and nothing else — what a re-import leaves — takes its own two
    /// pads and solves exactly as the typed one does; with no pad on the board it is said to be NOT
    /// PLACED, not to "name no terminals".
    /// </summary>
    [Fact]
    public void ARefdesOnlySeriesRow_TakesItsOwnTwoPads_AndOneNotOnTheBoardIsSaidToBe()
    {
        var typed = RailDcRun.Run(SeriesElementTests.DcRequest(dcrOhms: 0.060));
        Assert.Null(typed.Refusal);

        var bare = SeriesElementTests.DcRequest(dcrOhms: 0.060);
        var rail = bare.Document.Rails[0];
        int k = rail.Parts.FindIndex(p => p.IsSeries);
        rail.Parts[k] = rail.Parts[k] with { TerminalA = null, TerminalB = null };

        var derived = RailDcRun.Run(bare);
        Assert.Null(derived.Refusal);
        Assert.Equal(typed.Rails[0].Ports.Select(p => p.DropV), derived.Rails[0].Ports.Select(p => p.DropV));

        var unplaced = RailDcRun.Run(new RailDcRequest
        {
            Document = bare.Document, Technology = bare.Technology, Shapes = bare.Shapes,
            Pads = [.. bare.Pads.Where(p => p.Refdes != "FB1")], NetPoints = bare.NetPoints,
        });
        Assert.NotNull(unplaced.Refusal);
        Assert.Contains("FB1 is not placed on the board", unplaced.Refusal, StringComparison.Ordinal);
    }

    // ══ A HOLE DRAWN AS A CIRCLE IS A BARREL ═════════════════════════════════════════════════

    /// <summary>
    /// A rail that changes layer only through a circle on a via-bound drill layer — how a Gerber-format
    /// drill file draws its holes. The walk always joined it; now both readings do, and the barrel is
    /// priced as a via.
    /// </summary>
    [Fact]
    public void ARailThroughACircleOnTheDrillLayer_IsJoinedAndPricedAsAVia()
    {
        var tech = FourLayer();
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [Drill], SpanFromLayer = "TOP", SpanToLayer = "IN2", Plated = true,
        });

        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Gnd };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.3 });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Point = (Mm(19.5), Mm(0.25)), Layer = In2 }, DcCurrentA = 0.1 });

        var doc = new RailDocument();
        doc.Rails.Add(rail);
        var run = RailDcRun.Run(new RailDcRequest
        {
            Document = doc, Technology = tech, DbuPerMicron = DbuPerMicron,
            // The ground plane has an antipad round the hole, as a real one does: a barrel through
            // solid ground would be the rail shorted to its own return.
            Shapes = [Rect(Top, 0, 0, 10.4, 0.5), Rect(In2, 9.6, 0, 20, 0.5),
                      Rect(Gnd, -1, -5, 9.7, 5), Rect(Gnd, 10.3, -5, 21, 5), Rect(Gnd, 9.7, 0.6, 10.3, 5), Rect(Gnd, 9.7, -5, 10.3, -0.1),
                      new CircleShape { Layer = Drill, Cx = Mm(10), Cy = Mm(0.25), R = Mm(0.15) }],
            Pads = [new PlacedPin("BT1", "1", null, Mm(0.2), Mm(0.25), PinSource.Artwork) { Layer = Top }],
        });

        Assert.Null(run.Refusal);
        Assert.Contains(run.Rails[0].Breakdown, r => r.Label.Contains("via", StringComparison.OrdinalIgnoreCase));
    }
}
