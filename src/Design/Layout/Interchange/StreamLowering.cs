// The per-shape lowering every stream writer shares (brief-oasis-gdstk.md §6b, R-oas-2b). A circuitRF
// layout holds shapes no stream format has: curves, circles, holes, vias, bitmaps. Lowering turns each
// structure's shapes and instances into the four element kinds GDSII and OASIS both hold, in database
// units: a polygon, a path, a text, a reference. GdsiiWriter serialises those elements, and GdstkExport
// hands the SAME elements to the gdstk worker, so the two routes' fidelity counts are the same numbers
// by construction rather than by two copies agreeing.
//
// Moved here from GdsiiWriter unchanged: native GDSII export is byte-identical across the move, held by
// tests/Ui.Tests/Interchange/GdsiiExportByteIdentityTests.cs.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One stream element, in database units. Angles are in the stream formats' own convention
/// (<see cref="GdsiiTransformCodec.ToGdsii"/>: reflect about x first, then rotate).</summary>
public abstract record StreamElement;

/// <summary>A boundary: one ring, implicitly closed (no repeated first point), holes already keyholed.</summary>
public sealed record StreamPolygon(LayerKey Layer, long[] Xy) : StreamElement;

/// <summary>An open centreline, curves already flattened. <see cref="Extension"/> is the length each
/// end extends by when <see cref="End"/> is <see cref="PathEndStyle.Extended"/> (Width/2), else 0.</summary>
public sealed record StreamPath(LayerKey Layer, long Width, PathEndStyle End, long Extension, long[] Xy) : StreamElement;

/// <summary>A text. <see cref="Layer"/>'s datatype is the TEXTTYPE (brief-gdsii-native-fixes.md D1).
/// A label is never reflected; <see cref="AngleDegrees"/> is its rotation.</summary>
public sealed record StreamLabel(LayerKey Layer, long X, long Y, string Text, long Height, double AngleDegrees, bool IsPort)
    : StreamElement;

/// <summary>A placement. <see cref="Cols"/> or <see cref="Rows"/> above 1 is an array whose pitch is in
/// the parent's unrotated frame (<see cref="LayoutInstanceTransform.ArrayCellOrigin"/>'s convention).</summary>
public sealed record StreamReference(
    string Cell, long X, long Y, bool Reflect, double AngleDegrees, double Mag,
    int Cols, int Rows, long PitchX, long PitchY) : StreamElement
{
    public bool IsArray => Rows > 1 || Cols > 1;
}

/// <summary>One structure's elements: its shapes' elements in shape order, then its references.</summary>
public sealed record StreamStructure(string Name, IReadOnlyList<StreamElement> Shapes, IReadOnlyList<StreamReference> References);

/// <summary>A lowered library and what lowering it did — the counts the export dialog shows.</summary>
public sealed record StreamLibrary(
    IReadOnlyList<StreamStructure> Structures,
    int CurvedShapesFlattened,
    int HolesKeyholed,
    int BitmapsSkipped,
    int LabelRecordsWritten,
    int ViaPadsSkipped,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> LayersRenumbered);

public static class StreamLowering
{
    /// <summary>Lowers <paramref name="structures"/>. Throws <see cref="GdsiiExportException"/> naming every
    /// shape that cannot be written — a coordinate outside 32 bits, a layer or datatype outside 0–65535, an
    /// array count outside 1–32767, a placeholder layer with no free number — before anything is lowered,
    /// so a writer that lowers first never writes a byte of a library it cannot finish.</summary>
    public static StreamLibrary Lower(IReadOnlyList<InterchangeStructure> structures, Technology? tech)
    {
        var (renumber, renumbered, unnumbered) = RenumberPlaceholderLayers(structures, tech);
        var offenders = GdsiiCoordinateValidation.CheckOverflow(structures, tech).Concat(unnumbered).ToList();
        if (offenders.Count > 0) throw new GdsiiExportException(offenders);

        var c = new Counts();
        var diagnostics = new List<string>(renumbered);
        var lowered = new List<StreamStructure>(structures.Count);

        foreach (var s in structures)
        {
            var shapes = new List<StreamElement>(s.Shapes.Count);
            foreach (var shape in s.Shapes)
                LowerShape(shapes, shape, tech, renumber, s.Name, diagnostics, c);
            var refs = s.Instances.Select(LowerInstance).ToList();
            lowered.Add(new StreamStructure(s.Name, shapes, refs));
        }

        return new StreamLibrary(lowered, c.Curves, c.Holes, c.Bitmaps, c.Labels, c.ViaPadsSkipped, diagnostics, renumbered);
    }

    private sealed class Counts
    {
        public int Curves, Holes, Bitmaps, Labels, ViaPadsSkipped;
    }

    // ── Placeholder layers (D7) ────────────────────────────────────────────────

    /// <summary>A DXF or board import gives a layer the destination technology had no number for a
    /// NEGATIVE placeholder key (<c>DxfLayerReconciliation</c>, <c>PcbLayerReconciliation</c>). GDSII
    /// numbers layers 0–65535, so each placeholder layer number is written as the lowest layer number
    /// this export does not already use, datatype kept, and the choice is reported — never wrapped to
    /// 65535 in silence. Most-recent placeholder first (−1, −2, …), so the answer does not depend on
    /// shape order.</summary>
    private static (IReadOnlyDictionary<int, int> Renumber, List<string> Renumbered, List<string> Unnumbered)
        RenumberPlaceholderLayers(IReadOnlyList<InterchangeStructure> structures, Technology? tech)
    {
        var used = new HashSet<int>();
        var placeholders = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        foreach (var shape in structures.SelectMany(s => s.Shapes))
        {
            if (shape is BitmapShape) continue; // never exported
            foreach (var key in GdsiiCoordinateValidation.LayerKeysWritten(shape, tech))
                if (key.Layer < 0) placeholders.Add(key.Layer); else used.Add(key.Layer);
        }

        var renumber = new Dictionary<int, int>();
        var renumbered = new List<string>();
        var unnumbered = new List<string>();
        int next = 0;
        foreach (int placeholder in placeholders)
        {
            while (used.Contains(next)) next++;
            string name = tech?.Layers.FirstOrDefault(l => l.Key.Layer == placeholder)?.Name is { } n
                ? $"Layer \"{n}\"" : $"Layer {placeholder}";
            if (next > GdsiiCoordinateValidation.MaxLayerNumber)
            {
                unnumbered.Add($"{name} has no GDSII number, and every number 0–{GdsiiCoordinateValidation.MaxLayerNumber} is in use.");
                continue;
            }
            renumber[placeholder] = next;
            renumbered.Add($"{name} has no GDSII number; written as GDSII layer {next}.");
            used.Add(next);
        }
        return (renumber, renumbered, unnumbered);
    }

    private static LayerKey Gds(LayerKey key, IReadOnlyDictionary<int, int> renumber) =>
        key.Layer < 0 && renumber.TryGetValue(key.Layer, out int n) ? new LayerKey(n, key.Datatype) : key;

    // ── Shapes ─────────────────────────────────────────────────────────────────

    private static void LowerShape(
        List<StreamElement> into, LayoutShape shape, Technology? tech, IReadOnlyDictionary<int, int> renumber,
        string structureName, List<string> diagnostics, Counts c)
    {
        switch (shape)
        {
            case BitmapShape:
                c.Bitmaps++; // §3.1b R10e — never exported; the count IS the report
                return;
            case LabelShape label:
                c.Labels++; // item 6/R-fix-5 — see GdsiiExportSummary.LabelRecordsWritten's doc comment
                into.Add(LowerText(label, renumber));
                return;
            case PathShape path:
                into.Add(LowerPath(path, tech, renumber, c));
                return;
            case ViaShape via:
                LowerVia(into, via, tech, renumber, structureName, diagnostics, c);
                return;
            default:
                into.Add(LowerBoundaryLike(shape, tech, renumber, c));
                return;
        }
    }

    private static StreamPolygon LowerBoundaryLike(LayoutShape shape, Technology? tech, IReadOnlyDictionary<int, int> renumber, Counts c)
    {
        long tol = LayoutFlattener.ResolveTolDbu(shape, tech);
        var rings = LayoutFlattener.Flatten(shape, tol);

        if (IsCurvedPrimitive(shape)) c.Curves++;

        long[] outRing;
        if (rings.Count == 1)
        {
            outRing = rings[0];
        }
        else
        {
            // §3.1a — one self-touching contour, a zero-width slit per inner ring.
            outRing = Keyhole(rings[0], rings.Skip(1).ToList());
            c.Holes += rings.Count - 1;
        }

        return new StreamPolygon(Gds(shape.Layer, renumber), outRing);
    }

    private static bool IsCurvedPrimitive(LayoutShape shape) => shape switch
    {
        CircleShape => true,
        RoundedRectShape => true,
        CurveShape c => c.Edges is { } edges && edges.Any(e => e.Kind != EdgeKind.Line),
        _ => false,
    };

    private static StreamPath LowerPath(PathShape path, Technology? tech, IReadOnlyDictionary<int, int> renumber, Counts c)
    {
        long tol = LayoutFlattener.ResolveTolDbu(path, tech);
        bool curved = path.Edges is { } edges && edges.Any(e => e.Kind != EdgeKind.Line);
        long[] centerline = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, tol);
        if (curved) c.Curves++;

        // Design decision (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md plan): symmetric Width/2
        // extension on both ends — genuinely exercises BGNEXTN/ENDEXTN rather than reusing PATHTYPE 2's
        // implicit square cap.
        long extension = path.End == PathEndStyle.Extended ? path.Width / 2 : 0;
        return new StreamPath(Gds(path.Layer, renumber), path.Width, path.End, extension, centerline);
    }

    private static StreamLabel LowerText(LabelShape label, IReadOnlyDictionary<int, int> renumber)
    {
        // Labels have no mirror field — reflect is always false for a LabelShape.
        var (_, angle) = GdsiiTransformCodec.ToGdsii(false, label.RotationDegrees);
        return new StreamLabel(Gds(label.Layer, renumber), label.X, label.Y, label.Text, label.Height, angle, label.IsPort);
    }

    /// <summary>§4.3/R-via-9: a <see cref="ViaShape"/> becomes one flattened-circle boundary per mapped
    /// layer it participates in — the barrel (<see cref="LayoutShape.Layer"/>, ALWAYS present) and the
    /// pad (<see cref="ViaSpanResolver.PadLayer"/> — the shape's own <see cref="ViaShape.LandingLayer"/>
    /// when an importer set one, otherwise the TOP conductor of the span its stackup via entry states,
    /// because the layout editor's Via tool has never set the per-shape field and a via that lost its
    /// pad on the way out is a via with no annular ring). A stream format has no via primitive at all, so
    /// "mapped" here means simply "has a pad layer to draw on" — the format writes a raw
    /// <c>(layer, datatype)</c> key directly with no name lookup, unlike DXF, so there is no separate
    /// "layer known to this technology" failure mode to check. Reuses <see cref="LowerBoundaryLike"/>
    /// (via a synthetic <see cref="CircleShape"/>) rather than a hand-rolled square, so the pad/barrel
    /// go through the SAME flatten-and-count path every other curved primitive does — never a second
    /// circle-to-polygon implementation.</summary>
    private static void LowerVia(
        List<StreamElement> into, ViaShape via, Technology? tech, IReadOnlyDictionary<int, int> renumber,
        string structureName, List<string> diagnostics, Counts c)
    {
        var barrel = new CircleShape { Layer = via.Layer, Net = via.Net, Cx = via.X, Cy = via.Y, R = Math.Max(via.DrillSize, 2) / 2 };
        into.Add(LowerBoundaryLike(barrel, tech, renumber, c));

        if (ViaSpanResolver.PadLayer(via, tech) is { } landing)
        {
            var pad = new CircleShape { Layer = landing, Net = via.Net, Cx = via.X, Cy = via.Y, R = Math.Max(via.PadSize, 2) / 2 };
            into.Add(LowerBoundaryLike(pad, tech, renumber, c));
        }
        else
        {
            c.ViaPadsSkipped++;
            diagnostics.Add($"{structureName}: via at ({via.X},{via.Y}) has no pad layer — " +
                            (ViaSpanResolver.Explain(via.Layer, tech) ?? "no landing layer is set.") +
                            " Pad not exported.");
        }
    }

    // ── Instances ──────────────────────────────────────────────────────────────

    private static StreamReference LowerInstance(LayoutInstance inst)
    {
        var (reflect, angle) = GdsiiTransformCodec.ToGdsii(inst.MirrorX, inst.RotationDegrees);
        return new StreamReference(inst.CellRef, inst.X, inst.Y, reflect, angle, inst.Mag,
                                   inst.Cols, inst.Rows, inst.PitchX, inst.PitchY);
    }

    // ── Keyholing (§3.1a) ──────────────────────────────────────────────────────

    /// <summary>Bridges each hole ring into the outer ring via a zero-width slit (nearest-point
    /// bridge), producing one self-touching contour — exactly what every GDSII writer does, since the
    /// format cannot express a hole any other way.</summary>
    private static long[] Keyhole(long[] outer, List<long[]> holes)
    {
        var combined = new List<long>(outer);
        foreach (var hole in holes)
        {
            int combinedPoints = combined.Count / 2;
            int holePoints = hole.Length / 2;
            int bestOuterIdx = 0, bestHoleIdx = 0;
            long bestDistSq = long.MaxValue;

            for (int oi = 0; oi < combinedPoints; oi++)
            {
                long ox = combined[oi * 2], oy = combined[oi * 2 + 1];
                for (int hi = 0; hi < holePoints; hi++)
                {
                    long dx = ox - hole[hi * 2], dy = oy - hole[hi * 2 + 1];
                    long distSq = dx * dx + dy * dy;
                    if (distSq < bestDistSq) { bestDistSq = distSq; bestOuterIdx = oi; bestHoleIdx = hi; }
                }
            }

            long bridgeX = combined[bestOuterIdx * 2], bridgeY = combined[bestOuterIdx * 2 + 1];
            var insertion = new List<long>();
            for (int k = 0; k <= holePoints; k++)
            {
                int idx = (bestHoleIdx + k) % holePoints;
                insertion.Add(hole[idx * 2]);
                insertion.Add(hole[idx * 2 + 1]);
            }
            insertion.Add(bridgeX);
            insertion.Add(bridgeY); // slit closes back at the outer bridge point

            combined.InsertRange((bestOuterIdx + 1) * 2, insertion);
        }
        return combined.ToArray();
    }
}
