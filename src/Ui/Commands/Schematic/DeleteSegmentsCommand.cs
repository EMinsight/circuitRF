using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Deletes specific wire segments from the schematic.
///
/// Removing a middle segment splits the wire into two (points before the cut / points after).
/// Removing an end segment drops the outer endpoint.
/// Wires with fewer than 2 remaining points are removed entirely.
/// Multiple segments on the same wire are processed together in a single operation.
/// Fully undoable — undo restores the original wire(s) exactly.
/// </summary>
internal sealed class DeleteSegmentsCommand : IUiCommand
{
    private readonly SchematicEditModel _model;

    // Per-affected-wire snapshot: original wire + its list index, plus the replacement wire(s).
    private sealed record WireSegmentDeleteSnap(
        EditableWire         Original,
        int                  OriginalIndex,
        List<EditableWire>   Replacements);

    private readonly List<WireSegmentDeleteSnap> _snaps = [];

    public string Description => _snaps.Sum(s => s.Replacements.Count == 0 ? 1 : 1) == 1
        ? "Delete Segment" : "Delete Segments";

    /// <param name="spans">For a segment something joins between its vertices, the stretch the user
    /// selected (<see cref="SchematicSelection.GetSelectedSpans"/>): only that stretch is removed, so the
    /// pieces either side still end on the junctions they reached. Null or absent: the whole segment.</param>
    public DeleteSegmentsCommand(
        SchematicEditModel model,
        IReadOnlyList<(string WireId, int SegmentIndex)> segments,
        IReadOnlyDictionary<(string WireId, int SegmentIndex), ((double X, double Y) A, (double X, double Y) B)>? spans = null)
    {
        _model = model;

        // Group by wire so multiple segments on the same wire are processed together.
        foreach (var group in segments.GroupBy(s => s.WireId))
        {
            var wire = model.FindWire(group.Key);
            if (wire is null || wire.Points.Count < 2) continue;

            int wireIndex = model.Wires.IndexOf(wire);
            var cuts = group.Select(g => g.SegmentIndex)
                            .Where(i => i >= 0 && i < wire.Points.Count - 1)
                            .Distinct()
                            .OrderBy(i => i)
                            .ToList();
            if (cuts.Count == 0) continue;

            var cutSpans = new Dictionary<int, ((double X, double Y) A, (double X, double Y) B)?>();
            foreach (int i in cuts)
                cutSpans[i] = spans is not null && spans.TryGetValue((wire.Id, i), out var sp) ? sp : null;
            var pieces = ComputePieces(wire.Points, cutSpans);
            var replacements = pieces.Select(p =>
            {
                var nw = new EditableWire();
                nw.Points.AddRange(p);
                return nw;
            }).ToList();

            _snaps.Add(new WireSegmentDeleteSnap(wire, wireIndex, replacements));
        }
    }

    public void Execute()
    {
        foreach (var snap in _snaps)
        {
            int insertAt = Math.Min(snap.OriginalIndex, _model.Wires.Count);
            _model.Wires.Remove(snap.Original);
            for (int i = 0; i < snap.Replacements.Count; i++)
                _model.Wires.Insert(Math.Min(insertAt + i, _model.Wires.Count), snap.Replacements[i]);
        }
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var snap in _snaps)
        {
            foreach (var r in snap.Replacements)
                _model.Wires.Remove(r);
            _model.Wires.Insert(Math.Min(snap.OriginalIndex, _model.Wires.Count), snap.Original);
        }
        _model.NotifyChanged();
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits a point list at the given cut segments, discarding any resulting piece with fewer than 2
    /// points. A whole-segment cut at index i ends a piece at Points[i] and starts the next at
    /// Points[i+1]; a SPAN cut (A..B on segment i) ends the piece at whichever of A/B is nearer
    /// Points[i] and starts the next at the other, so the stretch outside the span stays drawn.
    /// </summary>
    private static List<List<(double X, double Y)>> ComputePieces(
        IReadOnlyList<(double X, double Y)> pts,
        IReadOnlyDictionary<int, ((double X, double Y) A, (double X, double Y) B)?> cuts)
    {
        var pieces = new List<List<(double X, double Y)>>();
        var piece  = new List<(double X, double Y)> { pts[0] };
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (!cuts.TryGetValue(i, out var span))
            {
                piece.Add(pts[i + 1]);
                continue;
            }
            if (span is { } sp)
            {
                var a = pts[i];
                double Da((double X, double Y) p) => (p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y);
                var (near, far) = Da(sp.A) <= Da(sp.B) ? (sp.A, sp.B) : (sp.B, sp.A);
                piece.Add(near);
                AddNormalized(pieces, piece);
                piece = [far, pts[i + 1]];
            }
            else
            {
                AddNormalized(pieces, piece);
                piece = [pts[i + 1]];
            }
        }
        AddNormalized(pieces, piece);
        return pieces;
    }

    // Normalizes a candidate piece and adds it to the list if it has ≥ 2 distinct points.
    private static void AddNormalized(
        List<List<(double X, double Y)>> pieces,
        List<(double X, double Y)> piece)
    {
        var norm = WireGeometry.NormalizePoints(piece).ToList();
        if (norm.Count >= 2) pieces.Add(norm);
    }
}
