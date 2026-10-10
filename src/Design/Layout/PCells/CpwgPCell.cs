namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// CPWG artwork (designer report, round 16 — Update Layout from Schematic drew a recognised board's microstrip and
/// left its grounded coplanar lines out, because nothing could draw one): the signal strip of width <c>W</c> and
/// length <c>L</c> on the signal layer, as <see cref="MlinPCell"/> draws it, and a coplanar ground strip a gap
/// <c>G</c> beyond each edge. Pin 1 at the origin, the line running to +X.
///
/// <para>Each side ground is as wide as the signal strip unless <c>Wg</c> says otherwise: the model takes the side
/// grounds as wide, and a board pours them, so this is the copper the line needs and not a claim about the board.
/// The vias stitching the side grounds to the reference are not drawn — the reference is a stackup entry here, not
/// a net — and the ground symbol's own via, where the schematic has one, is placed as for any part.</para>
/// </summary>
public static class CpwgPCell
{
    public const string GeneratorId = "CPWG";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double wMeters = parameters.Real("W", 0.0);
        double gMeters = parameters.Real("G", 0.0);
        double lMeters = parameters.Real("L", 0.0);
        double wgMeters = parameters.Real("Wg", wMeters);

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(wMeters, dbuPerMicron);
        long g = PCellUnits.MetresToDbu(gMeters, dbuPerMicron);
        long l = PCellUnits.MetresToDbu(lMeters, dbuPerMicron);
        long wg = PCellUnits.MetresToDbu(wgMeters, dbuPerMicron);

        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        long halfW = w / 2, inner = halfW + g, outer = inner + wg;
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = signalLayer, X1 = 0, Y1 = -halfW, X2 = l, Y2 = halfW },
        };
        if (wg > 0)
        {
            shapes.Add(new RectShape { Layer = signalLayer, X1 = 0, Y1 = inner, X2 = l, Y2 = outer });
            shapes.Add(new RectShape { Layer = signalLayer, X1 = 0, Y1 = -outer, X2 = l, Y2 = -inner });
        }

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w, 180.0),
            new PCellPin("2", l, 0, signalLayer, w,   0.0),
        };

        // The length grips MlinPCell has, for the same reasons; the width and gap are read off the line's
        // cross-section, and a drag that moved three strips at once would be hard to read.
        const PCellHandleQuantity len = PCellHandleQuantity.Length;
        var handles = new[]
        {
            new PCellHandle("L", 0, 0, l, 0, AxisDeg: 0, KeepAnchorFixed: true, Quantity: len),
            new PCellHandle("L", l, 0, 0, 0, AxisDeg: 180, KeepAnchorFixed: true, Quantity: len),
        };

        return new PCellResult(shapes, pins, Handles: handles);
    }
}
