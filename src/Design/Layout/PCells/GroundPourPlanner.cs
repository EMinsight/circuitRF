using Clipper2Lib;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>One ground pour <see cref="GroundPourPlanner"/> proposes: the copper to draw on
/// <paramref name="DrawingLayer"/>, which is <paramref name="Ground"/>'s drawing layer.</summary>
/// <param name="Shapes">The pour, with a clearance hole around every via that passes through the plane. More than one
/// shape only when clearances split the rectangle.</param>
/// <param name="Lines">How many placed microstrip instances return through this plane.</param>
/// <param name="ViaClearances">How many vias passing through the plane were given a clearance hole — drawn vias,
/// with <see cref="GroundPourPlanner.ViaClearanceMicrons"/> round their pad, and placed VIA components, with
/// their own antipad.</param>
/// <param name="ViasJoined">How many vias END on the plane inside the pour, and so are joined to it. A layout carries
/// no nets to tell a ground via from a signal via landing on that layer, so these are counted for the designer to
/// check rather than guessed at.</param>
/// <param name="MarginDbu">How far the pour reaches beyond the lines' own artwork.</param>
public sealed record GroundPour(
    StackupLayer Ground, LayerKey DrawingLayer, IReadOnlyList<LayoutShape> Shapes,
    int Lines, int ViaClearances, int ViasJoined, long MarginDbu);

/// <summary>
/// The ground plane a layout's microstrip lines return through, drawn as artwork (designer feedback round 10).
///
/// <para><b>Why a planner and not the line generator.</b> A microstrip's generated artwork is its LINE: the ground it
/// returns through is the stackup's plane, and no generator draws it. One plane per line would overlap its neighbours
/// and run straight through the via a layer change puts at a line's end, shorting it. So the plane is ONE pour per
/// ground conductor, covering every placed microstrip that returns through it, with a clearance hole cut around every
/// via that passes through the plane. It is computed from where the lines ARE when it is asked for, because Update
/// Layout from Schematic places new lines on a grid that the designer then rearranges.</para>
///
/// <para><b>What it does not know.</b> A via's clearance is <see cref="ViaClearanceMicrons"/> beyond its pad, a
/// default and said to be one; a via inside a placed cell is not seen (only the layout's own vias are); and a via
/// ending ON the plane is left joined to it and COUNTED, since without nets a ground via and a signal via landing on
/// that layer look the same.</para>
/// </summary>
public static class GroundPourPlanner
{
    /// <summary>The generators whose artwork is a microstrip line returning through a ground plane.</summary>
    public static readonly IReadOnlySet<string> MicrostripGenerators = new HashSet<string>(StringComparer.Ordinal)
    {
        MlinPCell.GeneratorId, MBendPCell.GeneratorId, MTeePCell.GeneratorId,
        MCrossPCell.GeneratorId, MTaperPCell.GeneratorId, MKlopfPCell.GeneratorId,
    };

    /// <summary>The clearance ring around a via's pad where it passes through the plane, µm. A default: no
    /// technology states an antipad yet.</summary>
    public const double ViaClearanceMicrons = 250;

    /// <summary>How far the pour reaches beyond the lines, in substrate heights: a microstrip's field is confined to
    /// a few h either side of the strip, so five keeps the plane's edge out of it.</summary>
    public const double MarginHeights = 5;

    /// <summary>
    /// One pour per ground conductor that a placed microstrip returns through. <paramref name="onlyUndrawn"/> skips a
    /// conductor that already carries artwork: a plane the designer drew is theirs, and drawing another over it
    /// would fill the clearances they cut in it.
    /// </summary>
    public static IReadOnlyList<GroundPour> Plan(LayoutView view, Technology? technology, string layoutBaseDir,
                                                 bool onlyUndrawn = true)
    {
        if (technology is null) return [];

        var byGround = new Dictionary<string, (StackupLayer Ground, Bbox Region, double MaxH, int Lines)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var inst in view.Instances)
        {
            string cell = Path.GetFileName(inst.CellRef.TrimEnd('/', '\\'));
            if (!view.PCellSnapshots.TryGetValue(cell, out var snap) || !MicrostripGenerators.Contains(snap.GeneratorId))
                continue;
            var selection = new PCellLayerSelection(snap.SignalLayerNameOverride, snap.GroundLayerNameOverride);
            var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, selection);
            if (substrate is null) continue;
            var ground = technology.Stackup.Layers.FirstOrDefault(l =>
                l.Kind == StackupKind.Conductor && l.Name == substrate.GroundConductorName);
            if (ground is null || ground.DrawingLayers.Count == 0) continue;

            var bb = CellHierarchy.InstanceBbox(inst, layoutBaseDir);
            if (bb.IsEmpty) continue;
            if (!byGround.TryGetValue(ground.Name, out var acc))
            {
                acc = (ground, Bbox.Empty, 0, 0);
                order.Add(ground.Name);
            }
            byGround[ground.Name] = (ground, acc.Region.Union(bb), Math.Max(acc.MaxH, substrate.HeightMeters), acc.Lines + 1);
        }

        var pours = new List<GroundPour>();
        foreach (string name in order)
        {
            var (ground, region, maxH, lines) = byGround[name];
            if (onlyUndrawn && view.Shapes.Any(s => s is not ViaShape && ground.DrawingLayers.Contains(s.Layer))) continue;

            long margin = (long)Math.Ceiling(MarginHeights * maxH * 1e6 * view.DbuPerMicron);
            var outer = new Bbox(region.MinX - margin, region.MinY - margin, region.MaxX + margin, region.MaxY + margin);
            var (clearances, joined) = Clearances(view, technology, ground, outer);

            var layer = ground.DrawingLayers[0];
            var subject = new Paths64 { Rect(outer) };
            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Difference, subject, clearances, tree, FillRule.NonZero);
            pours.Add(new GroundPour(ground, layer, LayoutClipper.FromClipperTree(tree, layer, null),
                                     lines, clearances.Count, joined, margin));
        }
        return pours;
    }

    /// <summary>A disc around every layout via whose barrel passes THROUGH <paramref name="ground"/> inside
    /// <paramref name="outer"/>. A via whose span is unstated goes through every layer, as everywhere else.</summary>
    private static (Paths64 Discs, int Joined) Clearances(LayoutView view, Technology technology, StackupLayer ground, Bbox outer)
    {
        var layers = technology.Stackup.Layers;
        int g = IndexOf(layers, ground);
        var drill = ViaSpanResolver.DrillLayerKeys(technology);
        long ring = (long)Math.Round(ViaClearanceMicrons * view.DbuPerMicron);
        var discs = new Paths64();
        int joined = 0;
        // A placed VIA or VIAGND component (ViaPCell) is a via too, and the only one here that states
        // its own antipad: its clearance is that diameter, in every plane its drill passes and does not
        // land on — which is the same span its model was resolved against.
        foreach (var inst in view.Instances)
        {
            string cell = Path.GetFileName(inst.CellRef.TrimEnd('/', '\\'));
            if (!view.PCellSnapshots.TryGetValue(cell, out var snap)) continue;
            bool grounded = string.Equals(snap.GeneratorId, ViaPCell.GroundGeneratorId, StringComparison.OrdinalIgnoreCase);
            if (!grounded && !string.Equals(snap.GeneratorId, ViaPCell.GeneratorId, StringComparison.OrdinalIgnoreCase)) continue;

            var r = ViaPCell.Resolve(snap.Parameters, technology, grounded);
            if (r.Span is not { } span) continue;
            long radius = (long)Math.Round(r.Antipad / 2 * 1e6 * view.DbuPerMicron);
            if (inst.X + radius < outer.MinX || inst.X - radius > outer.MaxX ||
                inst.Y + radius < outer.MinY || inst.Y - radius > outer.MaxY) continue;

            if (ReferenceEquals(span.From, ground) || ReferenceEquals(span.To, ground)) { joined++; continue; }
            int from = IndexOf(layers, span.From), to = IndexOf(layers, span.To);
            int top = Math.Min(from, to), bottom = Math.Max(from, to);
            if (span.ViaEntry is { } entry && ViaSpanResolver.Resolve(entry, technology) is { } drillSpan)
                (top, bottom) = (IndexOf(layers, drillSpan.Top), IndexOf(layers, drillSpan.Bottom));
            if (!(top < g && g < bottom) && !(top == g || bottom == g)) continue;   // the drill never reaches it
            discs.Add(Disc(inst.X, inst.Y, radius));
        }

        foreach (var via in view.Shapes.OfType<ViaShape>())
        {
            if (!drill.Contains(via.Layer)) continue;   // on no via entry: inert everywhere, so here too
            long radius = Math.Max(via.PadSize, via.DrillSize) / 2 + ring;
            if (via.X + radius < outer.MinX || via.X - radius > outer.MaxX ||
                via.Y + radius < outer.MinY || via.Y - radius > outer.MaxY) continue;
            if (ViaSpanResolver.Resolve(via.Layer, technology) is { } span)
            {
                int top = IndexOf(layers, span.Top), bottom = IndexOf(layers, span.Bottom);
                if (top == g || bottom == g) { joined++; continue; }   // ends on the plane: joined to it
                if (!(top < g && g < bottom)) continue;               // never reaches it
            }
            discs.Add(Disc(via.X, via.Y, radius));
        }
        return (discs, joined);
    }

    private static int IndexOf(IReadOnlyList<StackupLayer> layers, StackupLayer layer)
    {
        for (int i = 0; i < layers.Count; i++) if (ReferenceEquals(layers[i], layer)) return i;
        return -1;
    }

    private static Path64 Rect(Bbox b) => [new(b.MinX, b.MinY), new(b.MaxX, b.MinY), new(b.MaxX, b.MaxY), new(b.MinX, b.MaxY)];

    private static Path64 Disc(long x, long y, long r)
    {
        const int n = 48;
        var p = new Path64(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            // Circumscribed, so the polygon's flats never come closer to the pad than the clearance says.
            double rr = r / Math.Cos(Math.PI / n);
            p.Add(new Point64(x + (long)Math.Round(rr * Math.Cos(a)), y + (long)Math.Round(rr * Math.Sin(a))));
        }
        return p;
    }
}
