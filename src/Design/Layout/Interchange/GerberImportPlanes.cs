// brief-dfr26-1 R-dfr26-1-3 / R-dfr26-1-4 — what an imported board's OWN technology says about its planes and the
// boundary of its stack.
//
// A Gerber set states which copper is which layer and nothing about which of them is a plane. The minted
// technology marked none, and its stackup kept Stackup's default Bottom = Ground under a bottom layer that carries
// artwork — so a ten-layer board arrived in 3D EM with one ground reference (or none), every inner pour an ordinary
// signal level, and a mesher refusing its bottom copper as "at or below the ground plane". Both are guessed here,
// on the cell's own minted copy only (a technology the import USES is referenced and never written), and every
// guess is said once, with how to undo it.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

internal static class GerberImportPlanes
{
    /// <summary>The fraction of the board a conductor's copper has to cover to be taken for a plane.</summary>
    internal const double PlaneCoverage = 0.5;

    /// <summary>
    /// Marks every conductor whose copper covers at least <see cref="PlaneCoverage"/> of the board as a ground
    /// reference (marks the technology already carries are kept), then opens the stack's top and bottom boundary where
    /// the outer conductor carries artwork and is not a ground reference. One message for each change.
    /// </summary>
    /// <param name="shapes">The imported cell's own shapes, as written.</param>
    public static void Apply(Technology tech, IReadOnlyList<LayoutShape> shapes, int dbuPerMicron, List<string> messages)
    {
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (conductors.Count == 0) return;

        var byLayer = shapes.Where(s => s is not LabelShape and not BitmapShape)
                            .GroupBy(s => s.Layer).ToDictionary(g => g.Key, g => g.ToList());
        var copper = conductors.ToDictionary(c => c, c => c.DrawingLayers.SelectMany(k => byLayer.GetValueOrDefault(k) ?? []).ToList());

        // The board's area: its outline's closed shapes, else the box its outline strokes span, else the copper's box.
        var outlineKeys = Em3d.Em3dGenerator.BoardOutlineLayers(tech);
        var outline = shapes.Where(s => outlineKeys.Contains(s.Layer)).ToList();
        long tol = Math.Max(1, dbuPerMicron);
        double board = outline.Where(s => s is not PathShape).Select(s => Area([s], tol)).DefaultIfEmpty(0).Max();
        string boardSays = "the board outline's area";
        if (!(board > 0) && outline.Count > 0)
        {
            board = BoxArea(outline, tol);
            boardSays = "the box the board outline spans";
        }
        if (!(board > 0))
        {
            board = BoxArea(copper.Values.SelectMany(v => v).ToList(), tol);
            boardSays = "the box the copper spans (the set has no board outline)";
        }

        if (board > 0)
        {
            // An OUTER conductor is marked only on a board with no inner plane, and then only the one covering most. The
            // outer layers carry the parts, so the traces and pads ports are placed on are there — and a port on a ground
            // reference is refused — while a ground-flooded outer layer covers three-quarters of a board as readily as a
            // plane covers all of it. A two-layer board's plane IS outer, so there the one covering most is taken.
            var qualifying = new List<(StackupLayer Layer, double Coverage)>();
            foreach (var c in conductors)
            {
                if (c.IsGroundReference || copper[c].Count == 0) continue;
                double coverage = Area(copper[c], tol) / board;
                if (coverage >= PlaneCoverage) qualifying.Add((c, coverage));
            }
            bool Outer(StackupLayer c) => ReferenceEquals(c, conductors[0]) || ReferenceEquals(c, conductors[^1]);
            var planes = qualifying.Where(q => !Outer(q.Layer)).ToList();
            if (planes.Count == 0 && qualifying.Count > 0) planes = [qualifying.MaxBy(q => q.Coverage)];
            static string Row((StackupLayer Layer, double Coverage) q) => $"'{q.Layer.Name}' ({q.Coverage * 100:0}%)";
            var marked = new List<string>();
            foreach (var q in planes)
            {
                q.Layer.IsGroundReference = true;
                marked.Add(Row(q));
            }
            var outerLeft = qualifying.Except(planes).Select(Row).ToList();
            if (marked.Count > 0)
                messages.Add(
                    $"{(marked.Count == 1 ? "This conductor was" : $"These {marked.Count} conductors were")} marked as a ground " +
                    $"reference, because {(marked.Count == 1 ? "its" : "each one's")} copper covers at least half of " +
                    $"{boardSays}: {string.Join(", ", marked)}. An EM setup returns ports through a ground reference and meshes " +
                    "its openings as drawn. To undo one, clear Ground reference on it in the Technology editor ▸ Stackup." +
                    (outerLeft.Count > 0
                        ? $" {string.Join(" and ", outerLeft)} {(outerLeft.Count == 1 ? "covers" : "cover")} as much but " +
                          $"{(outerLeft.Count == 1 ? "is an outer layer" : "are the outer layers")}, where the parts and their " +
                          "ports are, so " + (outerLeft.Count == 1 ? "it was" : "they were") + " left as signal."
                        : ""));
        }

        var sides = new List<string>();
        var names = new List<string>();
        if (tech.Stackup.Top == BoundaryCondition.Ground && copper[conductors[0]].Count > 0 && !conductors[0].IsGroundReference)
        {
            tech.Stackup.Top = BoundaryCondition.Open;
            sides.Add("Top");
            names.Add($"'{conductors[0].Name}'");
        }
        if (tech.Stackup.Bottom == BoundaryCondition.Ground && copper[conductors[^1]].Count > 0 && !conductors[^1].IsGroundReference)
        {
            tech.Stackup.Bottom = BoundaryCondition.Open;
            sides.Add("Bottom");
            names.Add($"'{conductors[^1].Name}'");
        }
        if (sides.Count > 0)
            messages.Add(
                $"The stackup's {string.Join(" and ", sides)} boundar{(sides.Count == 1 ? "y was" : "ies were")} set to Open: " +
                $"{string.Join(" and ", names)} {(sides.Count == 1 ? "carries" : "carry")} artwork and " +
                $"{(sides.Count == 1 ? "is not a ground reference" : "neither is a ground reference")}, and a Ground boundary " +
                "is a solid plane beyond the outer copper, which the board does not have.");
    }

    /// <summary>The area of the union of <paramref name="shapes"/>, in DBU².</summary>
    private static double Area(IReadOnlyList<LayoutShape> shapes, long tolDbu)
    {
        var paths = new Paths64();
        foreach (var s in shapes) paths.AddRange(LayoutClipper.ToClipperPaths(s, tolDbu));
        return paths.Count == 0 ? 0 : Math.Abs(Clipper.Area(Clipper.Union(paths, LayoutClipper.Rule)));
    }

    private static double BoxArea(IReadOnlyList<LayoutShape> shapes, long tolDbu)
    {
        var paths = new Paths64();
        foreach (var s in shapes) paths.AddRange(LayoutClipper.ToClipperPaths(s, tolDbu));
        if (paths.Count == 0) return 0;
        var b = Clipper.GetBounds(paths);
        return b.right > b.left && b.bottom > b.top ? (double)(b.right - b.left) * (b.bottom - b.top) : 0;
    }
}
