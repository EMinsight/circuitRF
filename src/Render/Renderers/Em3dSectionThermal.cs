// brief-em3d-88 — what a TEMPERATURE section draws besides the slice: each bond wire painted from its own solved T(s), the
// thermal boundaries on the frame instead of the air box's, and the caption. `render --field` asks for these; nothing here is
// in src/Cli, which only reports them (brief 84's rule: `render` owns no rendering and no field arithmetic).
//
// ── A wire is a 1D element ────────────────────────────────────────────────────────────────────
//
// Its temperature is the run's T(s) table along its chain (ThermalWireChains), not the 3D field, which does not mesh the wire.
// Each chain segment is treated as a cylinder of the wire's diameter along it, and its cut by the plane drawn EXACTLY for that
// cylinder: parametrise the cut by the point C of the axis each chord is perpendicular to the projected axis through, and the
// chord's half-width is √(r² − d²·sin²φ), where d is C's distance from the plane and φ the angle between the axis and the plane's
// normal. So one rule draws a wire lying in the plane (a strip whose width follows how far the centreline is from the plane) and
// one crossing it at a slant (an ellipse). Only a segment ALONG the normal (sin φ ≈ 0) has no chord direction; its cut is a disc.
// A segment is finite, though, and its chords are only those of axis points inside it: one NEARLY along the normal covers a thin
// band of its circle, and a bend leaves a notch. So each node's sphere is drawn too — its cut is a disc of radius √(r² − d²) —
// and the wire is the chain of capsules it is (checked on Output Wires' loop tops, which cross the x = 820 µm plane flat).
// The colour is T interpolated along the segment by arc length — the chain's own nodes, as the 3D view colours a wire.
//
// ── The range ─────────────────────────────────────────────────────────────────────────────────
//
// FieldColorScale.MinMax over the slice (FieldSection.Cut ranges a temperature so — brief 75 D9), extended to the wires drawn, so
// the hottest thing in the picture is never clipped: at 14 A the wires run 200 K above the mould around them.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Render;

/// <summary>A wire's piece in a section: a strip of chords (<see cref="Left"/>[k] to <see cref="Right"/>[k]) at temperatures
/// <see cref="T"/>[k], picture metres. A disc is a strip too, its two halves.</summary>
public sealed record Em3dWirePiece(string Wire, IReadOnlyList<Uv> Left, IReadOnlyList<Uv> Right, IReadOnlyList<double> T);

/// <summary>Where a wire's centreline crosses the plane: the point (picture metres), its arc length and the T there.</summary>
public sealed record Em3dWireCrossing(string Wire, Uv At, double S, double T);

/// <summary>A thermal boundary as a section shows it: its cut by the plane (picture metres; none when the face is not in the
/// plane) and its label.</summary>
public sealed record Em3dBoundaryMark(string Face, ThermalBoundaryKind Kind, string Label, IReadOnlyList<(Uv A, Uv B)> Segments);

/// <summary>What a temperature section draws besides the slice.</summary>
public sealed record Em3dThermalPage(IReadOnlyList<Em3dWirePiece> Wires, IReadOnlyList<Em3dWireCrossing> Crossings,
                                     IReadOnlyList<Em3dBoundaryMark> Boundaries, IReadOnlyList<string> Caption);

public static class Em3dSectionThermal
{
    /// <summary>A segment within this of the plane's normal (sin φ) is cut as a disc.</summary>
    private const double AlongNormal = 0.05;

    /// <summary>Chords per segment: the width follows a square root, which four chords already trace to within a pixel.</summary>
    private const int ChordsPerSegment = 6;

    /// <summary>Sides of a disc.</summary>
    private const int DiscSides = 24;

    /// <summary>
    /// <paramref name="chains"/> cut by the plane <paramref name="axis"/> (0 x, 1 y, 2 z) = <paramref name="at"/> (world
    /// metres): each wire's pieces and each point its centreline crosses the plane.
    /// </summary>
    public static (List<Em3dWirePiece> Pieces, List<Em3dWireCrossing> Crossings) Wires(IReadOnlyList<ThermalWireChain> chains, int axis, double at)
    {
        var pieces = new List<Em3dWirePiece>();
        var crossings = new List<Em3dWireCrossing>();
        foreach (var c in chains)
        {
            double r = c.DiameterM / 2;
            for (int i = 0; i + 1 < c.Points.Length; i++)
            {
                var (p, q) = (c.Points[i], c.Points[i + 1]);
                double da = Coord(p, axis) - at, db = Coord(q, axis) - at;
                double tp = c.T[i], tq = c.T[i + 1];
                if (da < 0 && db > 0 || da > 0 && db < 0)
                {
                    double f = da / (da - db);
                    crossings.Add(new Em3dWireCrossing(c.Wire, Project(Lerp(p, q, f), axis), c.S[i] + f * (c.S[i + 1] - c.S[i]), tp + f * (tq - tp)));
                }
                double dx = q.X - p.X, dy = q.Y - p.Y, dz = q.Z - p.Z, len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len <= 0) continue;
                var pu = Project(p, axis);
                var qu = Project(q, axis);
                double su = (qu.U - pu.U) / len, sv = (qu.V - pu.V) / len;        // the axis' in-plane part: its length is sin φ
                double sinPhi = Math.Sqrt(su * su + sv * sv);
                if (sinPhi < AlongNormal)
                {
                    // its nearest point to the plane: the crossing, or — a segment that stops short of it, beside a bend whose
                    // other segment does cross — the end within a radius of the plane, whose tube still reaches it
                    double f = da <= 0 && db >= 0 || da >= 0 && db <= 0 ? (da == db ? 0.5 : da / (da - db)) : Math.Abs(da) < Math.Abs(db) ? 0 : 1;
                    if (Math.Abs(da + f * (db - da)) > r) continue;
                    pieces.Add(Disc(c.Wire, Project(Lerp(p, q, f), axis), r, tp + f * (tq - tp)));
                    continue;
                }
                // the axis points whose chord is not empty: |d| ≤ r / sin φ, d linear along the segment
                double reach = r / sinPhi;
                double t0 = 0, t1 = 1;
                if (!Clip(da, db, -reach, reach, ref t0, ref t1)) continue;
                double nu = -sv / sinPhi, nv = su / sinPhi;                     // the chord direction
                var left = new Uv[ChordsPerSegment + 1];
                var right = new Uv[ChordsPerSegment + 1];
                var t = new double[ChordsPerSegment + 1];
                for (int k = 0; k <= ChordsPerSegment; k++)
                {
                    double f = t0 + (t1 - t0) * k / ChordsPerSegment;
                    double d = da + f * (db - da);
                    double w = Math.Sqrt(Math.Max(0, r * r - d * d * sinPhi * sinPhi));
                    var cu = new Uv(pu.U + f * (qu.U - pu.U), pu.V + f * (qu.V - pu.V));
                    left[k] = new Uv(cu.U + w * nu, cu.V + w * nv);
                    right[k] = new Uv(cu.U - w * nu, cu.V - w * nv);
                    t[k] = tp + f * (tq - tp);
                }
                pieces.Add(new Em3dWirePiece(c.Wire, left, right, t));
            }
            // each node's sphere: the chords above are only those of axis points INSIDE a segment, so a segment nearly along the
            // normal covers a sliver of its cut and a bend leaves a notch — the node's own cut, √(r² − d²), fills both, so the wire
            // is drawn as the chain of capsules it is. Put UNDER the wire's strips: a disc is one colour, and over a strip's
            // gradient the discs read as a staircase along the wire.
            int under = pieces.FindIndex(p => p.Wire == c.Wire);
            if (under < 0) under = pieces.Count;
            for (int i = c.Points.Length - 1; i >= 0; i--)
            {
                double d = Coord(c.Points[i], axis) - at;
                if (Math.Abs(d) < r) pieces.Insert(under, Disc(c.Wire, Project(c.Points[i], axis), Math.Sqrt(r * r - d * d), c.T[i]));
            }
        }
        return (pieces, crossings);
    }

    /// <summary><paramref name="cut"/> with its range extended to hold every finite temperature of <paramref name="pieces"/>.</summary>
    public static FieldSectionCut WithWires(FieldSectionCut cut, IEnumerable<Em3dWirePiece> pieces)
    {
        double lo = cut.Scale.Lo, hi = cut.Scale.Hi;
        bool any = false;
        foreach (var t in pieces.SelectMany(p => p.T).Where(double.IsFinite))
        {
            if (!any && cut.Slice.VertexCount == 0) { lo = hi = t; }
            any = true;
            lo = Math.Min(lo, t);
            hi = Math.Max(hi, t);
        }
        return any ? cut with { Scale = cut.Scale with { Lo = lo, Hi = hi } } : cut;
    }

    /// <summary>The legend's hot spot: the hottest of the slice and the wires drawn, and what holds it — <c>393 °C, U1/wire/Out/3</c>.</summary>
    public static string HotSpot(FieldSectionCut cut, IReadOnlyList<Em3dWirePiece> pieces)
    {
        var slice = FieldHotSpot.Of(cut.Quantity, [cut.Slice]);
        double best = slice?.Value ?? double.NegativeInfinity;
        string where = "in the section";
        foreach (var p in pieces)
            foreach (var t in p.T)
                if (double.IsFinite(t) && t > best) { best = t; where = p.Wire; }
        return double.IsFinite(best) ? $"{best.ToString("0.0", CultureInfo.InvariantCulture)} °C, {where}" : "";
    }

    /// <summary>
    /// <paramref name="setup"/>'s thermal boundaries as the plane <paramref name="axis"/> = <paramref name="at"/> shows them: each
    /// face's cut, placed by the lowering's own face placement (ThermalLowerings.FacePieces, which the mesher's face groups and the
    /// editor's tints use), labelled with what it does at sweep point <paramref name="point"/> of <paramref name="table"/>. A face
    /// the plane does not cut is still returned, with no segments, so the caption can say it. Every face no boundary names is
    /// insulated.
    /// </summary>
    public static List<Em3dBoundaryMark> Boundaries(C3dDocument doc, C3dElaboration e, EmSetup setup, int axis, double at,
                                                    ThermalResultTable? table, int point)
    {
        var marks = new List<Em3dBoundaryMark>();
        foreach (var b in setup.Thermal?.Boundaries ?? [])
        {
            string label = $"{(b.Face == C3dThermal.ExposedFaces ? "every exposed face" : b.Face)}: {What(b, e, table, point)}";
            var segments = new List<(Uv, Uv)>();
            if (b.Face != C3dThermal.ExposedFaces && e.Ok &&
                ThermalLowerings.FacePieces(doc, e, e.Solids, b.Face, out _, out _) is { } pieces)
                foreach (var poly in pieces) segments.AddRange(CutPolygon(poly.Outer, axis, at));
            marks.Add(new Em3dBoundaryMark(b.Face, b.Kind, label, segments));
        }
        return marks;
    }

    /// <summary>The caption: what the picture is, where the wires' colours come from, and the boundaries the frame does not show.</summary>
    public static List<string> Caption(Em3dScene scene, string setupName, string pointLabel, IReadOnlyList<Em3dBoundaryMark> boundaries, int wires)
    {
        var lines = new List<string>
        {
            $"Temperature, {Em3dSectionRenderer.Title(scene)} — setup '{setupName}', {pointLabel}",
            (wires > 0 ? "Bond wires painted from their own solved T(s): a wire is a 1D element and is not in the 3D field. " : "") +
            "Metals are outlined, not filled: they carry a temperature too.",
        };
        var off = boundaries.Where(m => m.Segments.Count == 0).Select(m => m.Label + (m.Face == C3dThermal.ExposedFaces ? "" : " (not in this plane)")).ToList();
        lines.Add("Boundaries: " + string.Join("  ·  ", off.Append(boundaries.Count == 0 ? "every face insulated" : "every other face insulated")));
        return lines;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a boundary does, its values at the point: <c>85 °C</c>, <c>convection, h = 10 W/(m²·K) to 25 °C</c>.</summary>
    private static string What(CemThermalBoundary b, C3dElaboration e, ThermalResultTable? table, int point)
        => b.Kind == ThermalBoundaryKind.FixedT
            ? $"held at {Value(b.TempC, e, table, point, " °C")}"
            : $"convection, h = {Value(b.H, e, table, point, " W/(m²·K)")} to {Value(b.AmbientC, e, table, point, " °C")}";

    /// <summary>A setup's value at the point: a swept variable's value there, anything else in the document's own scope — or the
    /// text as written, when it reads a swept variable inside a larger expression or does not resolve.</summary>
    private static string Value(string? text, C3dElaboration e, ThermalResultTable? table, int point, string unit)
    {
        text = (text ?? "").Trim();
        string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture) + unit;
        if (table is { } t && point >= 0 && point < t.Points)
        {
            var idx = t.Indices(point);
            for (int k = 0; k < t.Axes.Count; k++)
            {
                if (text == t.Axes[k].Name) return G(t.Axes[k].Values[idx[k]]);
                if (Regex.IsMatch(text, $@"\b{Regex.Escape(t.Axes[k].Name)}\b")) return text;
            }
        }
        if (e.Resolution is { } res && C3dThermal.Evaluate(res, text, out _) is { } v && double.IsFinite(v)) return G(v);
        return text;
    }

    /// <summary>A face polygon's cut by the plane, as segments in the picture: its outline where it lies in the plane, otherwise
    /// the stretches between its edge crossings, paired in order along the cut.</summary>
    private static IEnumerable<(Uv, Uv)> CutPolygon(IReadOnlyList<Point3> ring, int axis, double at)
    {
        if (ring.Count < 3) yield break;
        double span = 0;
        foreach (var p in ring) foreach (var q in ring) span = Math.Max(span, Math.Abs(Coord(p, (axis + 1) % 3) - Coord(q, (axis + 1) % 3)) + Math.Abs(Coord(p, (axis + 2) % 3) - Coord(q, (axis + 2) % 3)));
        double tol = Math.Max(1e-15, 1e-9 * span);
        if (ring.All(p => Math.Abs(Coord(p, axis) - at) <= tol))
        {
            for (int i = 0; i < ring.Count; i++) yield return (Project(ring[i], axis), Project(ring[(i + 1) % ring.Count], axis));
            yield break;
        }
        var hits = new List<Uv>();
        for (int i = 0; i < ring.Count; i++)
        {
            var (p, q) = (ring[i], ring[(i + 1) % ring.Count]);
            double da = Coord(p, axis) - at, db = Coord(q, axis) - at;
            if (Math.Abs(da) <= tol && Math.Abs(db) <= tol) { yield return (Project(p, axis), Project(q, axis)); continue; }
            if ((da <= 0) == (db <= 0)) continue;
            hits.Add(Project(Lerp(p, q, da / (da - db)), axis));
        }
        if (hits.Count < 2) yield break;
        bool byU = hits.Max(h => h.U) - hits.Min(h => h.U) >= hits.Max(h => h.V) - hits.Min(h => h.V);
        hits.Sort((a, b) => byU ? a.U.CompareTo(b.U) : a.V.CompareTo(b.V));
        for (int k = 0; k + 1 < hits.Count; k += 2) yield return (hits[k], hits[k + 1]);
    }

    private static Em3dWirePiece Disc(string wire, Uv c, double r, double t)
    {
        int half = DiscSides / 2;
        var left = new Uv[half + 1];
        var right = new Uv[half + 1];
        for (int k = 0; k <= half; k++)
        {
            double a = Math.PI * k / half;
            left[k] = new Uv(c.U + r * Math.Cos(a), c.V + r * Math.Sin(a));
            right[k] = new Uv(c.U + r * Math.Cos(a), c.V - r * Math.Sin(a));
        }
        return new Em3dWirePiece(wire, left, right, [.. Enumerable.Repeat(t, half + 1)]);
    }

    /// <summary>Narrows [t0, t1] to where da + t·(db − da) lies in [lo, hi]; false when nothing is left.</summary>
    private static bool Clip(double da, double db, double lo, double hi, ref double t0, ref double t1)
    {
        double dd = db - da;
        if (Math.Abs(dd) < 1e-300) return da >= lo && da <= hi;
        double a = (lo - da) / dd, b = (hi - da) / dd;
        if (a > b) (a, b) = (b, a);
        t0 = Math.Max(t0, a);
        t1 = Math.Min(t1, b);
        return t1 > t0;
    }

    private static double Coord(Point3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

    private static Point3 Lerp(Point3 p, Point3 q, double f) => new(p.X + f * (q.X - p.X), p.Y + f * (q.Y - p.Y), p.Z + f * (q.Z - p.Z));

    /// <summary>A world point in the section's own (u, v): (y, z), (x, z) or (x, y), as Em3dSectionField places the slice.</summary>
    private static Uv Project(Point3 p, int axis) => axis switch { 0 => new Uv(p.Y, p.Z), 1 => new Uv(p.X, p.Z), _ => new Uv(p.X, p.Y) };
}
