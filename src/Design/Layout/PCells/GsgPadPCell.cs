namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// GSGPAD artwork (brief-review-6 follow-up): a ground-signal-ground probe pad and the microstrip line leaving
/// it. The signal pad is centred on the origin, where pin 1 — the probe's signal tip — is; the line of width
/// <c>W</c> runs from its +X edge for <c>L</c> to pin 2. The two ground pads sit at ±<c>Pitch</c> across the line,
/// their probe-side edges in line with the signal pad's, each holding <c>NVia</c> vias to the plane in a row along
/// it — the row's span, not <c>PadL</c>, decides how long a ground pad is when the vias need the room. The probe
/// approaches from −X.
///
/// <para>The vias resolve as a <c>VIAGND</c>'s do (<see cref="SubstrateResolver.ResolveViaSpan"/> from the signal
/// conductor to the ground reference, the technology's drill and pad), so a probe pad on a board lands a plated
/// through-hole on its plane and one on a die a backside via. The pads and the line are on the signal conductor.
/// It is a microstrip, never a coplanar line: the ground pads stop at the signal pad and do not follow the line.</para>
/// </summary>
public static class GsgPadPCell
{
    public const string GeneratorId = "GSGPAD";

    /// <summary>A via centre to the next, in via pads: room for the drill rule between neighbours.</summary>
    private const double ViaPitchInPads = 1.5;

    /// <summary>The probe pad in the cell's own coordinates, DBU: what the generator draws and what the Layout
    /// Review reads (its probe-pad check), so the two cannot disagree about where a pad is.</summary>
    /// <param name="SignalPad">The signal pad: MinX is its probe-side edge.</param>
    /// <param name="GroundPads">The two ground pads, −Y then +Y.</param>
    /// <param name="Vias">Every via centre, the −Y pad's first.</param>
    public sealed record Geometry(
        Bbox SignalPad, IReadOnlyList<Bbox> GroundPads, long LineEndX, long LineWidth,
        IReadOnlyList<(long X, long Y)> Vias, long ViaPad, long ViaDrill, long Pitch,
        ResolvedViaSpan? Span, IReadOnlyList<string> Notes);

    public static Geometry Resolve(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology,
                                   PCellLayerSelection layerSelection, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        long Dbu(string name, double fallback) => PCellUnits.MetresToDbu(Math.Max(0, parameters.Real(name, fallback)), dbuPerMicron);
        long w = Dbu("W", 0), l = Dbu("L", 0);
        long pitch = Dbu("Pitch", 150e-6), padW = Dbu("PadW", 80e-6), padL = Dbu("PadL", 80e-6);
        int n = (int)Math.Clamp(Math.Round(parameters.Real("NVia", 1)), 0, 20);

        var notes = new List<string>();
        var (span, failure, warnings) = SubstrateResolver.ResolveViaSpan(
            technology, layerSelection.SignalLayerNameOverride, layerSelection.GroundLayerNameOverride, toGround: true);
        if (n > 0)
        {
            notes.AddRange(warnings);
            if (failure is not null) notes.Add(failure.Reason);
        }
        long viaDrill = PCellUnits.MetresToDbu(span?.DrillMeters is > 0 and var d ? d : ViaDefaultsSi.Drill, dbuPerMicron);
        long viaPad = Math.Max(viaDrill, PCellUnits.MetresToDbu(span?.PadMeters is > 0 and var p ? p : ViaDefaultsSi.Pad, dbuPerMicron));

        var signal = new Bbox(-padL / 2, -padW / 2, padL - padL / 2, padW / 2);

        // The vias in a row along each ground pad, centred across it; the pad as long as the row needs.
        long viaPitch = (long)Math.Round(viaPad * ViaPitchInPads);
        long edge = Math.Max(0, (padW - viaPad) / 2);
        long rowSpan = n == 0 ? 0 : (n - 1) * viaPitch + viaPad;
        long groundL = Math.Max(padL, rowSpan + 2 * edge);
        if (pitch <= padW)
            notes.Add("The pitch is no wider than a pad, so the ground pads overlap the signal pad: make Pitch larger than PadW.");
        else if (groundL > padL && pitch - padW / 2 <= w / 2)
            notes.Add("The ground pads, lengthened for their vias, reach the line: make Pitch larger, the line narrower, or NVia smaller.");
        if (n > 0 && viaPad > padW)
            notes.Add("The technology's via pad is wider than the ground pads, so the vias overhang them: make PadW larger.");
        var grounds = new List<Bbox>();
        var vias = new List<(long, long)>();
        foreach (long y in new[] { -pitch, pitch })
        {
            grounds.Add(new Bbox(signal.MinX, y - padW / 2, signal.MinX + groundL, y + padW / 2));
            long first = signal.MinX + (groundL - rowSpan) / 2 + viaPad / 2;
            for (int k = 0; k < n; k++) vias.Add((first + k * viaPitch, y));
        }
        return new Geometry(signal, grounds, signal.MaxX + l, w, vias, viaPad, viaDrill, pitch, span, notes);
    }

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        var g = Resolve(parameters, technology, layerSelection);
        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        static RectShape Rect(LayerKey layer, Bbox b) => new() { Layer = layer, X1 = b.MinX, Y1 = b.MinY, X2 = b.MaxX, Y2 = b.MaxY };
        var shapes = new List<LayoutShape> { Rect(signalLayer, g.SignalPad) };
        if (g.LineEndX > g.SignalPad.MaxX && g.LineWidth > 0)
            shapes.Add(Rect(signalLayer, new Bbox(g.SignalPad.MaxX, -g.LineWidth / 2, g.LineEndX, g.LineWidth - g.LineWidth / 2)));
        shapes.AddRange(g.GroundPads.Select(b => Rect(signalLayer, b)));

        if (g.Vias.Count > 0)
        {
            var drillLayer = g.Span?.DrillLayer
                             ?? technology?.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Via && l.DrawingLayers.Count > 0)?.DrawingLayers[0]
                             ?? new LayerKey(9, 0);
            var toLayer = g.Span is { To.DrawingLayers.Count: > 0 } s ? s.To.DrawingLayers[0] : signalLayer;
            var fromLayer = g.Span is { From.DrawingLayers.Count: > 0 } f ? f.From.DrawingLayers[0] : signalLayer;
            var notes = new List<string>(g.Notes);

            // The via starts on another metal than the pads — a die's backside via lands on Metal1 while the pads are
            // on the metal above: each ground pad is stacked, drawn on both metals and joined by the technology's post
            // between them, as a die's probe pad is built.
            if (fromLayer != signalLayer && g.Span is { } span)
            {
                string? padConductor = technology?.Stackup.Layers
                    .FirstOrDefault(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(signalLayer))?.Name;
                var (post, _, _) = SubstrateResolver.ResolveViaSpan(technology, padConductor, span.From.Name, toGround: false);
                foreach (var b in g.GroundPads)
                {
                    shapes.Add(Rect(fromLayer, b));
                    if (post?.DrillLayer is { } postLayer) shapes.Add(Rect(postLayer, b));
                }
                if (post?.DrillLayer is null)
                    notes.Add($"No via of the technology joins {padConductor} to {span.From.Name}, so the ground pads are not "
                            + "joined to their vias: set SignalLayer to the conductor the vias start on.");
            }
            foreach (var (x, y) in g.Vias)
            {
                shapes.Add(new ViaShape { Layer = drillLayer, X = x, Y = y, PadSize = g.ViaPad, DrillSize = g.ViaDrill });
                // A via pad wider than the ground pad still lands on copper.
                shapes.Add(new CircleShape { Layer = fromLayer, Cx = x, Cy = y, R = g.ViaPad / 2 });
                if (toLayer != fromLayer) shapes.Add(new CircleShape { Layer = toLayer, Cx = x, Cy = y, R = g.ViaPad / 2 });
            }
            g = g with { Notes = notes };
        }

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, g.SignalPad.MaxY - g.SignalPad.MinY, 180.0),
            new PCellPin("2", g.LineEndX, 0, signalLayer, Math.Max(g.LineWidth, 1), 0.0),
        };
        return new PCellResult(shapes, pins, Diagnostics: g.Notes.Count > 0 ? g.Notes : null);
    }
}
