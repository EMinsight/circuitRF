// The hierarchy walk the two HIERARCHICAL exports (GDSII, DXF) share — brief-bsp-3 R-bsp3-4.
//
// A structure/block is written once and shared by every placement of its cell, and a bottom-side
// placement's layers are not its cell's (brief-bsp-1 D2). So the walk visits (cell, side) pairs: every
// reachable cell as stored, exactly as before, and — only for a cell some placement puts on the bottom —
// a second node holding the bottom view CellLayoutResolver hands every other reader (PlacementSideViews),
// layers swapped. Sides compose with no work here: the bottom view's own placements already carry the
// composed side (D5), so a child's node is (its cell, its Side as the parent's view states it), and a
// variant may itself reference variants.
//
// A design with no bottom placement walks exactly the nodes, in exactly the order, the two exports
// walked before; that is what keeps their bytes unchanged.

using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout.Interchange;

internal static class InterchangeHierarchy
{
    /// <summary>One structure to write: a cell folder on one face. <see cref="View"/> is the cell as stored
    /// for <see cref="PlacementSide.Top"/>, the layer-swapped bottom view for a variant.</summary>
    internal sealed class Node(string dir, PlacementSide side, LayoutView view)
    {
        public string Dir { get; } = dir;
        public PlacementSide Side { get; } = side;
        public LayoutView View { get; } = view;

        /// <summary>The node each of <see cref="View"/>'s placements references, in order; null where the
        /// reference does not resolve to an existing cell folder.</summary>
        public List<Node?> Targets { get; } = [];

        public string LayoutDir => CellFolder.SubFolderPath(Dir, ViewType.Layout);
        public string CellName => Path.GetFileName(Dir);
    }

    internal sealed record Walk(IReadOnlyList<Node> Tops, IReadOnlyList<Node> Variants, IReadOnlyList<string> Notes)
    {
        public IEnumerable<Node> All => Tops.Concat(Variants);
    }

    /// <summary>The report line R-bsp3-4 asks for — a format limit, stated rather than fixed.</summary>
    internal static string VariantNote(int variants, string structureWord) =>
        $"Bottom-side placements were written as {variants} variant {structureWord}{(variants == 1 ? "" : "s")} " +
        $"(named with a _B suffix); reading this file back gives them as separate cells.";

    internal static Walk Collect(string rootAbs, LayoutView? rootView, Technology? tech, Func<string, LayoutView> loadPrimary)
    {
        var tops = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var bottoms = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var topOrder = new List<Node>();
        var variantOrder = new List<Node>();
        var notes = new List<string>();
        var queue = new Queue<Node>();

        Node Top(string dir)
        {
            if (tops.TryGetValue(dir, out var node)) return node;
            // item 5/R-fix-4: the root uses the caller's live view; every other cell resolves from disk.
            var view = string.Equals(dir, rootAbs, StringComparison.OrdinalIgnoreCase) && rootView is not null
                ? rootView
                : loadPrimary(dir);
            node = new Node(dir, PlacementSide.Top, view);
            tops[dir] = node;
            topOrder.Add(node);
            queue.Enqueue(node);
            return node;
        }

        Node Bottom(string dir, LayoutInstance inst)
        {
            if (bottoms.TryGetValue(dir, out var node)) return node;
            var top = Top(dir);
            var res = PlacementSideViews.Bottom(
                inst, new CellLayoutResolution { State = CellLayoutState.Resolved, View = top.View, ResolvedCellDir = dir }, tech);
            notes.AddRange(res.SideNotes);
            if (res.State == CellLayoutState.SideRefused)
            {
                // The flatten drops a refused part; a hierarchical file keeps the reference so it is not
                // silently short a part, and says on which layers it went.
                notes.Add(res.Refusal + " It was written on the layers its cell draws on.");
                node = top;
            }
            else if (ReferenceEquals(res.View, top.View))
                node = top;   // no technology to swap against — already said in the side note
            else
            {
                node = new Node(dir, PlacementSide.Bottom, res.View!);
                variantOrder.Add(node);
                queue.Enqueue(node);
            }
            bottoms[dir] = node;
            return node;
        }

        Top(rootAbs);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var layoutDir = node.LayoutDir;
            foreach (var inst in node.View.Instances)
            {
                if (Workspace.ExternalCellRef.ResolveCellDir(inst.CellRef, layoutDir) is not { } targetDir
                    || !Directory.Exists(targetDir))
                {
                    node.Targets.Add(null);
                    continue;
                }
                node.Targets.Add(inst.Side == PlacementSide.Bottom ? Bottom(targetDir, inst) : Top(targetDir));
            }
        }

        return new Walk(topOrder, variantOrder, notes.Distinct().ToList());
    }

    /// <summary><paramref name="baseName"/> + <c>_B</c>, made unique against every name already given out
    /// with the same <c>_2</c>, <c>_3</c> suffixing the naming classes use for a collision.</summary>
    internal static string VariantName(string baseName, ISet<string> used, int maxLength)
    {
        string sanitized = baseName.Length + 2 > maxLength ? baseName[..(maxLength - 2)] : baseName;
        sanitized += "_B";
        var candidate = sanitized;
        int n = 2;
        while (!used.Add(candidate))
        {
            var suffix = $"_{n++}";
            int keep = Math.Min(sanitized.Length, maxLength - suffix.Length);
            candidate = string.Concat(sanitized.AsSpan(0, Math.Max(keep, 0)), suffix);
        }
        return candidate;
    }

    /// <summary>The exported placement — the reference renamed to its target structure; the transform
    /// as stored. A bottom placement's side is in WHICH structure it references, so it is not repeated.</summary>
    internal static LayoutInstance Exported(LayoutInstance inst, string targetName) => new()
    {
        CellRef = targetName,
        X = inst.X, Y = inst.Y, RotationDegrees = inst.RotationDegrees, MirrorX = inst.MirrorX, Mag = inst.Mag,
        Rows = inst.Rows, Cols = inst.Cols, PitchX = inst.PitchX, PitchY = inst.PitchY,
    };
}
