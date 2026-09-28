// The plated holes railRF's two readings price as barrels — brief-railrf-37 (field report, 2026-09-28).
//
// ── WHAT WAS WRONG ──────────────────────────────────────────────────────────────────────────────
//
// The region walk (DrcConnectivity) joins conductors through ANY geometry on a drawing layer a
// stackup Via entry claims — which is what a drill flash is on a Gerber-format drill layer, a plain
// circle. Both railRF readings built their barrels from ViaShapes only. So on a board whose holes
// arrived partly as circles on the drill layer, the walk joined a rail through two of them and the
// readings could not: the fast model called it "a fault in the fast reading", the accurate mesh
// "rail copper nothing joins to a source", and a rail the designer could see was continuous did not
// solve in either. The field report's supply ran from a resistor down to an inner layer and back up
// to its load through exactly two such holes.
//
// A circle on a via-bound layer is therefore a barrel here, of its own diameter, exactly as the
// planar and 3D EM extractors already read it. Nothing on a board without such circles changes.

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>Every plated hole of the artwork, as railRF prices it.</summary>
internal static class PdnBarrels
{
    /// <summary>
    /// Every <see cref="ViaShape"/>, and a via for every <see cref="CircleShape"/> on a drawing layer
    /// a <see cref="StackupKind.Via"/> entry claims — the hole's centre and its diameter as the drill,
    /// on that layer, so the entry's span and plating apply to it as to any other via.
    /// </summary>
    public static List<ViaShape> Of(IReadOnlyList<LayoutShape> shapes, Technology tech)
    {
        var viaLayers = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Via)
            .SelectMany(l => l.DrawingLayers)
            .ToHashSet();

        var found = new List<ViaShape>();
        foreach (var shape in shapes)
        {
            if (shape is ViaShape via) found.Add(via);
            else if (shape is CircleShape { R: > 0 } hole && viaLayers.Contains(hole.Layer))
                found.Add(new ViaShape { X = hole.Cx, Y = hole.Cy, DrillSize = 2 * hole.R, Layer = hole.Layer, Net = hole.Net });
        }
        return found;
    }
}
