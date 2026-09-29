// brief-em3d-86 R-em3d86-4 — what else follows a fold. A face edit that folds a face to keep it planar renames it (zmax → zmax.0,
// zmax.1), and FollowFolds (brief 47) rewrote only the EM FaceBoundaries. Everything else that names a face BY NAME went stale:
// the tint vanished and check and the run refused "no face", although nothing thermal had been edited. Here, in the same undo
// entry, the thermal and plot references follow too:
//
//   * a thermal setup's boundary (in Setups, a JSON element per setup) on a folded face becomes one boundary per piece, with the
//     same condition — the rule FollowFolds applies to an EM boundary;
//   * a probe's Face becomes the pieces, read as ONE place over their union (the list form of Face): a probe is one reading,
//     and splitting it into two would change what the document's measures and limits read;
//   * a probe's Spot stays on the piece that holds its centre — a spot is a disk on one face, and exactly one piece holds it;
//   * a field plot's face becomes its pieces, each on the same side.
//
// One text (C3dPersistence.SerializeFaceReferences) holds all of them and the EM list, so the undo entry restores them together.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Design.ThreeD.Kernel;

public static class C3dFoldReferences
{
    /// <summary>
    /// Rewrites, in place, every reference in <paramref name="doc"/> to a face of <paramref name="folded"/> (the object as the
    /// edit left it) that <paramref name="folds"/> renamed. True when anything changed.
    /// </summary>
    public static bool Follow(C3dDocument doc, C3dObject folded, IReadOnlyDictionary<string, IReadOnlyList<string>> folds)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(folded);
        if (folds.Count == 0) return false;
        string name = folded.Name;
        bool changed = false;

        IReadOnlyList<string>? PiecesOf(string spelled)
        {
            int slash = spelled.LastIndexOf('/');
            return slash > 0 && spelled[..slash] == name && folds.TryGetValue(spelled[(slash + 1)..], out var p) ? p : null;
        }

        if (C3dFaceCommands.FollowFolds(doc.FaceBoundaries, name, folds) is { } em) { doc.FaceBoundaries = em; changed = true; }

        for (int i = 0; i < doc.Setups.Count; i++)
            if (FollowSetup(doc.Setups[i], PiecesOf, name) is { } setup) { doc.Setups[i] = setup; changed = true; }

        foreach (var p in doc.Probes)
        {
            if (p.Face is { } faces && faces.Any(f => PiecesOf(f) is not null))
            {
                p.Face = [.. faces.SelectMany(f => PiecesOf(f)?.Select(piece => $"{name}/{piece}") ?? [f])];
                changed = true;
            }
            if (p.Spot is { } spot && PiecesOf(spot.Face) is { } pieces)
            {
                spot.Face = $"{name}/{Holding(folded, pieces, spot.Center)}";
                changed = true;
            }
        }

        foreach (var plot in doc.FieldPlots)
        {
            if (!plot.Faces.Any(f => PiecesOf(f.Face) is not null)) continue;
            plot.Faces = [.. plot.Faces.SelectMany(f => PiecesOf(f.Face) is { } pieces
                ? pieces.Select(piece => new C3dFieldPlotFace { Face = $"{name}/{piece}", Side = f.Side, Unread = f.Unread })
                : [f])];
            changed = true;
        }
        return changed;
    }

    /// <summary>One embedded setup with each thermal boundary on a folded face replaced by one per piece; null when none was.</summary>
    private static JsonElement? FollowSetup(JsonElement setup, Func<string, IReadOnlyList<string>?> piecesOf, string objectName)
    {
        if (setup.ValueKind != JsonValueKind.Object) return null;
        var root = JsonNode.Parse(setup.GetRawText()) as JsonObject;
        if (Child(root, "Thermal") is not JsonObject thermal || Child(thermal, "Boundaries") is not JsonArray boundaries) return null;
        var rewritten = new JsonArray();
        bool any = false;
        foreach (var b in boundaries)
        {
            string? faceKey = b is JsonObject o ? o.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, "Face", StringComparison.OrdinalIgnoreCase)) : null;
            if (faceKey is not null && b![faceKey] is JsonValue v && v.TryGetValue(out string? face) && piecesOf(face) is { } pieces)
            {
                foreach (string piece in pieces)
                {
                    var copy = b.DeepClone().AsObject();
                    copy[faceKey] = $"{objectName}/{piece}";
                    rewritten.Add(copy);
                }
                any = true;
            }
            else rewritten.Add(b?.DeepClone());
        }
        if (!any) return null;
        string key = thermal.Select(kv => kv.Key).First(k => string.Equals(k, "Boundaries", StringComparison.OrdinalIgnoreCase));
        thermal[key] = rewritten;
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonNode? Child(JsonObject? o, string key)
        => o?.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The piece of <paramref name="folded"/> whose polygon holds <paramref name="centre"/> (world DBU; the polygons are
    /// in the object's frame) — failing that, the one nearest it in its plane.</summary>
    private static string Holding(C3dObject folded, IReadOnlyList<string> pieces, C3dPoint3 centre)
    {
        var (x, y, z) = folded.Placement.ToTransform().Inverse().Apply(centre);
        string best = pieces[0];
        double bestDistance = double.PositiveInfinity;
        foreach (string piece in pieces)
        {
            if (C3dFaceCommands.Polygon(folded, piece, out _) is not { Rings: [var outer, ..] } || outer.Count < 3) continue;
            // the plane's normal (Newell), and the two axes it is least along: the polygon seen along its normal
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0, j = outer.Count - 1; i < outer.Count; j = i++)
            {
                nx += (double)(outer[j].Y - outer[i].Y) * (outer[j].Z + outer[i].Z);
                ny += (double)(outer[j].Z - outer[i].Z) * (outer[j].X + outer[i].X);
                nz += (double)(outer[j].X - outer[i].X) * (outer[j].Y + outer[i].Y);
            }
            int drop = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0 : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
            (double U, double V) Uv(double px, double py, double pz) => drop switch { 0 => (py, pz), 1 => (pz, px), _ => (px, py) };
            var (u, v) = Uv(x, y, z);
            var ring = outer.Select(p => Uv(p.X, p.Y, p.Z)).ToList();
            bool inside = false;
            double near = double.PositiveInfinity;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var (ui, vi) = ring[i];
                var (uj, vj) = ring[j];
                if ((vi > v) != (vj > v) && u < (uj - ui) * (v - vi) / (vj - vi) + ui) inside = !inside;
                double du = uj - ui, dv = vj - vi, l2 = du * du + dv * dv;
                double t = l2 > 0 ? Math.Clamp(((u - ui) * du + (v - vi) * dv) / l2, 0, 1) : 0;
                near = Math.Min(near, Math.Sqrt(Math.Pow(ui + t * du - u, 2) + Math.Pow(vi + t * dv - v, 2)));
            }
            if (inside) return piece;
            if (near < bestDistance) { bestDistance = near; best = piece; }
        }
        return best;
    }
}
