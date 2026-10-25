using System.Globalization;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// The artwork of a <c>TESTPAD</c> (brief-dfr26-7 R-dfr26-7-1): a round pad of diameter <c>D</c> at the origin on
/// <c>PadLayer</c>, fed either through a via at the pad's centre from <c>ViaFrom</c> (<c>Feed = Via</c>) or by a
/// short stub of width <c>FeedW</c> leaving the pad on one side (<c>N</c>/<c>E</c>/<c>S</c>/<c>W</c>).
///
/// <para><b>Pins.</b> Pin 1 is the feed: at the origin on <c>ViaFrom</c> when via-fed — the via's own pad — or at
/// the stub's far end, facing out. Pin 2 is the pad centre on <c>PadLayer</c>. Both are ordinary pins, for the
/// schematic and the ratsnest alike.</para>
///
/// <para><b>Built as <see cref="ViaPCell"/> builds a via</b>: the drill on the stackup's via layer for the span,
/// copper on the two conductors it joins and none on the layers between (the via component's rule) — unless
/// <c>InnerPadD</c> is set, when each conductor between outside <c>ClearLayers</c> gets the unused pad the model
/// counts (brief-dfr26-8). <c>AntipadD</c> is the via's antipad a pour cuts in each plane it passes.</para>
///
/// <para><b>The cut-outs are declared, not drawn.</b> <c>ClearLayers</c> and <c>ClearD</c> are read by
/// <see cref="GroundPourPlanner"/>, which cuts the disc out of each plane it pours, and by the Cut Clearances into
/// Planes command for planes that are drawn artwork (<see cref="TestPadClearances"/>). Drawing them here would put
/// a hole-shaped shape on a plane layer, which is copper, not a hole.</para>
///
/// <para><b>The EM port.</b> With <c>Centre = Port</c> the cell carries one more label at the pad centre, flagged
/// <see cref="LabelShape.EmPort"/>: an internal port to ground numbered <c>PortNum</c> (empty: the next free
/// number). That flag is what makes an EM setup collect it from inside the placed instance
/// (<see cref="Em.EmComponentPorts"/>); the cell's two pin labels stay what every generated cell's are.</para>
/// </summary>
public static class TestPadPCell
{
    public const string GeneratorId = "TESTPAD";

    /// <summary>The four sides a stub may leave on, with the stub's outward direction in degrees.</summary>
    public static readonly IReadOnlyDictionary<string, double> SideDirections =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["E"] = 0, ["N"] = 90, ["W"] = 180, ["S"] = 270 };

    public const string ViaFeed = "Via";
    public const string CentreOpen = "Open";
    public const string CentrePort = "Port";

    /// <summary>The cut-out a TESTPAD gets when <c>ClearD</c> is left empty: the pad plus this.</summary>
    public const double DefaultClearRingMeters = CircuitRF.Core.Devices.ComponentModelFactory.DefaultTestPadClearRingMeters;

    /// <summary>What a test pad instance's parameters resolve to — shared with <see cref="GroundPourPlanner"/>
    /// and <see cref="TestPadClearances"/>, which need the same layers and the same cut-out.</summary>
    public sealed record Resolved(
        ResolvedTestPad? Stack,
        LayerKey PadLayer,
        double D,
        string Feed,
        double FeedW,
        double Drill,
        double ViaPad,
        double ViaAntipad,
        LayerKey? ViaFromLayer,
        LayerKey DrillLayer,
        IReadOnlyList<string> ClearLayers,
        double ClearD,
        bool CentreIsPort,
        int? PortNum,
        IReadOnlyList<string> Notes,
        double InnerPadD = 0)
    {
        public bool ViaFed => Stack?.Via is not null;
    }

    /// <summary>The <c>ClearLayers</c> text as conductor names: comma- or semicolon-separated, trimmed.</summary>
    public static IReadOnlyList<string> ParseClearLayers(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    public static Resolved Resolve(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology)
    {
        string? Text(string key) => parameters.Text(key) is { Length: > 0 } t ? t.Trim() : null;
        var notes = new List<string>();

        string feed = Text("Feed") ?? ViaFeed;
        if (!feed.Equals(ViaFeed, StringComparison.OrdinalIgnoreCase) && !SideDirections.ContainsKey(feed))
        {
            notes.Add($"Feed '{feed}' is not one of N, E, S, W or Via; the pad is fed through a via");
            feed = ViaFeed;
        }
        bool viaFed = feed.Equals(ViaFeed, StringComparison.OrdinalIgnoreCase);

        var clear = ParseClearLayers(Text("ClearLayers"));
        var (stack, failure, warnings) = SubstrateResolver.ResolveTestPad(technology, Text("PadLayer"), Text("ViaFrom"), viaFed, clear);
        notes.AddRange(warnings);
        if (failure is not null) notes.Add(failure.Reason);

        double d = parameters.Real("D", CircuitRF.Core.Devices.ComponentModelFactory.DefaultTestPadDiameterMeters);
        if (!(d > 0)) d = CircuitRF.Core.Devices.ComponentModelFactory.DefaultTestPadDiameterMeters;
        double drill = parameters.Real("Drill", stack?.Via?.DrillMeters ?? ViaDefaultsSi.Drill);
        if (!(drill > 0)) drill = ViaDefaultsSi.Drill;
        double viaPad = Math.Max(stack?.Via?.PadMeters ?? ViaDefaultsSi.Pad, drill);
        // An explicit 0 is "no cut-out" (brief-dfr27-1), as the model reads it; only a value that is not a size takes
        // the default.
        double clearD = parameters.Real("ClearD", d + DefaultClearRingMeters);
        if (!(clearD >= 0)) clearD = d + DefaultClearRingMeters;

        double innerPadD = parameters.Real("InnerPadD", 0.0);
        if (!(innerPadD > 0)) innerPadD = 0;
        double antipad = parameters.Real("AntipadD", viaPad + ViaDefaultsSi.AntipadRing);
        if (!(antipad > 0)) antipad = viaPad + ViaDefaultsSi.AntipadRing;
        if (innerPadD > 0 && innerPadD >= antipad)
            notes.Add($"InnerPadD ({innerPadD * 1e3:0.###} mm) is no smaller than AntipadD ({antipad * 1e3:0.###} mm): each unused pad would touch its plane");

        double feedW = parameters.Real("FeedW", double.NaN);
        if (!(feedW > 0)) feedW = FiftyOhmWidth(technology, stack?.Pad.Name) ?? 0.3e-3;

        var padLayer = stack is { Pad.DrawingLayers.Count: > 0 } s1 ? s1.Pad.DrawingLayers[0] : new LayerKey(2, 0);
        LayerKey? fromLayer = stack?.Via is { From.DrawingLayers.Count: > 0 } v ? v.From.DrawingLayers[0] : null;
        var drillLayer = stack?.Via?.DrillLayer
                         ?? technology?.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Via && l.DrawingLayers.Count > 0)?.DrawingLayers[0]
                         ?? new LayerKey(9, 0);

        bool port = (Text("Centre") ?? CentreOpen).Equals(CentrePort, StringComparison.OrdinalIgnoreCase);
        int? portNum = null;
        if (port && Text("PortNum") is { } pnText)
        {
            if (double.TryParse(pnText.TrimStart('P', 'p'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                && n >= 1 && n == Math.Floor(n)) portNum = (int)n;
            else notes.Add($"PortNum '{pnText}' is not a port number; the port takes the next free one");
        }

        return new Resolved(stack, padLayer, d, viaFed ? ViaFeed : feed.ToUpperInvariant(), feedW, drill, viaPad,
                            antipad, fromLayer, drillLayer, clear, clearD, port, portNum, notes, innerPadD);
    }

    /// <summary>The microstrip width that is 50 Ω on <paramref name="conductor"/>, or null when the stackup gives it
    /// no substrate.</summary>
    private static double? FiftyOhmWidth(Technology? technology, string? conductor)
    {
        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, new PCellLayerSelection(conductor, null));
        if (substrate is null) return null;
        double w = HammerstadJensen.SynthesizeWidth(50.0, substrate.HeightMeters, substrate.ThicknessMeters,
                                                    substrate.RelativePermittivity, new MicrostripValidityReporter("(test pad feed width)"));
        return w > 0 && double.IsFinite(w) ? w : null;
    }

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
    {
        var r = Resolve(parameters, technology);
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long d = PCellUnits.MetresToDbu(r.D, dbuPerMicron);
        long radius = d / 2;

        var shapes = new List<LayoutShape> { new CircleShape { Layer = r.PadLayer, Cx = 0, Cy = 0, R = radius } };
        var pins = new List<PCellPin>();

        if (r.ViaFed && r.ViaFromLayer is { } from)
        {
            long drill = PCellUnits.MetresToDbu(r.Drill, dbuPerMicron);
            long viaPad = PCellUnits.MetresToDbu(r.ViaPad, dbuPerMicron);
            shapes.Add(new ViaShape { Layer = r.DrillLayer, X = 0, Y = 0, PadSize = viaPad, DrillSize = drill });
            shapes.Add(new CircleShape { Layer = from, Cx = 0, Cy = 0, R = viaPad / 2 });
            pins.Add(new PCellPin("1", 0, 0, from, viaPad, 0.0));
            // The unused pads the model counts (brief-dfr26-8): one on each conductor the via passes outside
            // ClearLayers, so the artwork and the model stay one statement.
            if (r.InnerPadD > 0 && r.Stack is { } st)
            {
                long innerR = PCellUnits.MetresToDbu(r.InnerPadD, dbuPerMicron) / 2;
                foreach (var slot in st.InnerPadSlots)
                    if (slot.Conductor.DrawingLayers.Count > 0)
                        shapes.Add(new CircleShape { Layer = slot.Conductor.DrawingLayers[0], Cx = 0, Cy = 0, R = innerR });
            }
        }
        else
        {
            double dir = SideDirections.TryGetValue(r.Feed, out var deg) ? deg : 0;
            long w = PCellUnits.MetresToDbu(r.FeedW, dbuPerMicron);
            long end = radius + Math.Max(w, 1);
            var (cx, cy) = dir switch { 90 => (0L, 1L), 180 => (-1L, 0L), 270 => (0L, -1L), _ => (1L, 0L) };
            // From the pad's centre out past its edge by one feed width, so the stub overlaps the pad and ends on
            // bare board where a line joins it.
            long x0 = cy != 0 ? -w / 2 : Math.Min(0, cx * end), x1 = cy != 0 ? w / 2 : Math.Max(0, cx * end);
            long y0 = cx != 0 ? -w / 2 : Math.Min(0, cy * end), y1 = cx != 0 ? w / 2 : Math.Max(0, cy * end);
            shapes.Add(new RectShape { Layer = r.PadLayer, X1 = x0, Y1 = y0, X2 = x1, Y2 = y1 });
            pins.Add(new PCellPin("1", cx * end, cy * end, r.PadLayer, w, dir));
        }
        pins.Add(new PCellPin("2", 0, 0, r.PadLayer, d, 270.0));

        if (r.CentreIsPort)
            shapes.Add(new LabelShape
            {
                Layer    = r.PadLayer,
                X        = 0,
                Y        = 0,
                Text     = r.PortNum is { } n ? $"P{n}" : EmPortAutoText,
                Height   = technology?.DefaultLabelHeightDbu is > 0 and var h ? h : 5000,
                IsPort   = true,
                EmPort   = true,
                // Committed, as the Port tool commits it: a via-fed pad has its via's land at the same point on
                // ViaFrom, so the port says which conductor it is on rather than leaving it to the metal under it.
                PortLayer = r.PadLayer,
                PortKind = PlanarPortKind.Internal,
                HAlign   = LabelHAlign.Center,
                VAlign   = LabelVAlign.Middle,
            });

        return new PCellResult(shapes, pins, Diagnostics: r.Notes.Count > 0 ? r.Notes : null);
    }

    /// <summary>The text of a centre port with no stated number. It names no number, so the port extractor gives it
    /// the next free one, as it does a port label drawn without a number.</summary>
    public const string EmPortAutoText = "TP";
}
