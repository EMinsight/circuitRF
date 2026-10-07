using System.Globalization;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Interchange;

/// <summary>
/// brief-oasis-gdstk.md §8b — the semantic equality two GDSII readers or writers are held to: the same set
/// of cell names and the same database unit; per cell, the same MULTISET of polygons, paths, labels and
/// instances. It ignores exactly what two correct writers may differ in (§7c: element and cell order, a
/// polygon's starting vertex and orientation, a rectangle written as a 4-vertex polygon) and nothing more.
///
/// <para>Coordinates compare exactly, as integers. Rotation and magnification are rounded to 1e-9 before
/// comparing: the gdstk route snaps angles to 1e-9° on import (src/Design/RESOLVED.md, G2), so a tighter
/// tolerance would report that snap rather than a difference. A label's height stands for its
/// magnification, because circuitRF's label holds no magnification of its own (§7c's "label with mirror
/// and magnification" arrives as a height, its mirror dropped by both readers).</para>
///
/// <para>Every difference is listed, not the first, so one failing run says everything.</para>
/// </summary>
internal static class InterchangeEquality
{
    public static IReadOnlyList<string> Differences(
        IReadOnlyList<InterchangeStructure> expected, double expectedDbuPerMicron,
        IReadOnlyList<InterchangeStructure> actual, double actualDbuPerMicron)
    {
        var differences = new List<string>();
        if (Math.Abs(expectedDbuPerMicron - actualDbuPerMicron) > 1e-9 * expectedDbuPerMicron)
            differences.Add($"database unit: expected {expectedDbuPerMicron} DBU/µm, got {actualDbuPerMicron}");

        var want = expected.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var got = actual.ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (string name in want.Keys.Except(got.Keys).Order(StringComparer.Ordinal)) differences.Add($"cell {name}: missing");
        foreach (string name in got.Keys.Except(want.Keys).Order(StringComparer.Ordinal)) differences.Add($"cell {name}: not expected");

        foreach (string name in want.Keys.Intersect(got.Keys).Order(StringComparer.Ordinal))
        {
            var w = Keys(want[name]);
            var g = Keys(got[name]);
            foreach (var key in w.Keys.Union(g.Keys).Order(StringComparer.Ordinal))
            {
                int dw = w.GetValueOrDefault(key), dg = g.GetValueOrDefault(key);
                if (dw > dg) differences.Add($"cell {name}: missing {dw - dg}× {key}");
                if (dg > dw) differences.Add($"cell {name}: {dg - dw}× not expected {key}");
            }
        }
        return differences;
    }

    public static void AssertEqual(
        IReadOnlyList<InterchangeStructure> expected, double expectedDbuPerMicron,
        IReadOnlyList<InterchangeStructure> actual, double actualDbuPerMicron)
    {
        var differences = Differences(expected, expectedDbuPerMicron, actual, actualDbuPerMicron);
        Assert.True(differences.Count == 0, $"{differences.Count} difference(s):\n" + string.Join("\n", differences));
    }

    private static Dictionary<string, int> Keys(InterchangeStructure s)
    {
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in s.Shapes.Select(Key).Concat(s.Instances.Select(Key)))
            keys[key] = keys.GetValueOrDefault(key) + 1;
        return keys;
    }

    private static string Key(LayoutShape shape) => shape switch
    {
        RectShape r => PolygonKey(r.Layer, [r.X1, r.Y1, r.X2, r.Y1, r.X2, r.Y2, r.X1, r.Y2], null),
        PolygonShape p => PolygonKey(p.Layer, p.Xy, p.Holes),
        PathShape p => $"path {Layer(p.Layer)} w={p.Width} {p.End} {string.Join(",", p.Xy)}",
        LabelShape l => $"label {Layer(l.Layer)} \"{l.Text}\" {l.X},{l.Y} r={Round(l.RotationDegrees)} h={l.Height}",
        _ => $"{shape.GetType().Name} {Layer(shape.Layer)}",
    };

    private static string Key(LayoutInstance i) =>
        $"ref {i.CellRef} {i.X},{i.Y} r={Round(i.RotationDegrees)} mirror={i.MirrorX} mag={Round(i.Mag)} {i.Cols}x{i.Rows} {i.PitchX},{i.PitchY}";

    /// <summary>A polygon as a vertex cycle: a repeated closing vertex dropped, counter-clockwise, starting
    /// at the lexicographically smallest vertex. A rectangle arrives here as its four corners, so a
    /// <see cref="RectShape"/> and the 4-vertex polygon of the same extent have one key.</summary>
    private static string PolygonKey(LayerKey layer, long[] xy, List<long[]>? holes)
    {
        var pts = Enumerable.Range(0, xy.Length / 2).Select(k => (X: xy[2 * k], Y: xy[2 * k + 1])).ToList();
        if (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);

        double area2 = 0;
        for (int k = 0; k < pts.Count; k++)
        {
            var (a, b) = (pts[k], pts[(k + 1) % pts.Count]);
            area2 += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        if (area2 < 0) pts.Reverse();

        int start = 0;
        for (int k = 1; k < pts.Count; k++)
            if (pts[k].X < pts[start].X || (pts[k].X == pts[start].X && pts[k].Y < pts[start].Y)) start = k;
        var cycle = pts.Skip(start).Concat(pts.Take(start)).Select(p => $"{p.X},{p.Y}");

        string holesKey = holes is { Count: > 0 } ? $" holes={holes.Count}" : "";
        return $"polygon {Layer(layer)} {string.Join(" ", cycle)}{holesKey}";
    }

    private static string Layer(LayerKey k) => $"{k.Layer}/{k.Datatype}";

    private static string Round(double v) => Math.Round(v, 9).ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// brief-oasis-gdstk.md §8a — the §7c simple cases, built as <see cref="InterchangeStructure"/>s so the
/// layout-level comparison tests need no file. Each mirrors the committed fixture of the same name in
/// <c>testdata/interchange/gdstk/8a/</c>, in what circuitRF's model can hold.
///
/// <para>Two deliberate departures from those fixtures. Labels carry height 1000 (magnification 1): a
/// label written by circuitRF's own writer carries its height as <c>WIDTH</c>, which gdstk does not read,
/// so any other height reads back through gdstk as 1000 — a known difference recorded at G2
/// (src/Design/RESOLVED.md), not one this corpus should rediscover in every case. And a label's mirror is
/// absent, because circuitRF's label has none.</para>
/// </summary>
internal static class GdstkCorpus
{
    public sealed record Case(string Name, int DbuPerMicron, IReadOnlyList<InterchangeStructure> Structures)
    {
        public override string ToString() => Name;
    }

    private static readonly LayerKey L1 = new(1, 0);

    public static IReadOnlyList<Case> All { get; } =
    [
        new("rectangle", 1000, [Cell("TOP", Rect(L1, 0, 0, 1000, 2000))]),
        new("polygon7", 1000, [Cell("TOP", new PolygonShape { Layer = L1, Xy = [0, 0, 700, 0, 900, 300, 600, 800, 200, 900, -100, 500, -50, 100] })]),
        new("path-ends", 1000,
        [
            Cell("TOP",
                new PathShape { Layer = L1, Xy = [0, 0, 1000, 0, 1000, 2000], Width = 200, End = PathEndStyle.Flush },
                new PathShape { Layer = L1, Xy = [0, 3000, 1000, 3000], Width = 200, End = PathEndStyle.Round },
                new PathShape { Layer = L1, Xy = [0, 6000, 1000, 6000], Width = 200, End = PathEndStyle.Square }),
        ]),
        new("labels", 1000,
        [
            Cell("TOP",
                new LabelShape { Layer = new LayerKey(5, 0), X = 100, Y = 200, Text = "IN", Height = 1000 },
                new LabelShape { Layer = new LayerKey(5, 0), X = -300, Y = 400, Text = "OUT", Height = 1000, RotationDegrees = 90 }),
        ]),
        new("sref-transform", 1000,
        [
            Cell("LEAF", Rect(L1, 0, 0, 300, 400)),
            Cell("TOP", [], new LayoutInstance { CellRef = "LEAF", X = 500, Y = -300, RotationDegrees = 90, MirrorX = true, Mag = 2 }),
        ]),
        new("aref-3x2", 1000,
        [
            Cell("LEAF", Rect(L1, 0, 0, 300, 400)),
            Cell("TOP", [], new LayoutInstance { CellRef = "LEAF", X = 0, Y = 0, Cols = 3, Rows = 2, PitchX = 1000, PitchY = 2000 }),
        ]),
        new("hierarchy", 1000,
        [
            Cell("LEAF", Rect(L1, 0, 0, 300, 400)),
            Cell("MID", [Rect(new LayerKey(2, 0), 0, 0, 2000, 100)],
                new LayoutInstance { CellRef = "LEAF", X = 0, Y = 0 },
                new LayoutInstance { CellRef = "LEAF", X = 1000, Y = 0 }),
            Cell("TOP", [], new LayoutInstance { CellRef = "MID", X = 0, Y = 5000 }),
        ]),
        new("layers-datatypes", 1000,
        [
            Cell("TOP",
                Rect(new LayerKey(1, 0), 0, 0, 100, 100), Rect(new LayerKey(1, 1), 200, 0, 300, 100),
                Rect(new LayerKey(2, 0), 0, 200, 100, 300), Rect(new LayerKey(2, 1), 200, 200, 300, 300)),
        ]),
        new("dbu-1nm", 1000, [Cell("TOP", Rect(L1, -1, -1, 123_457, 1))]),
        new("dbu-0p25nm", 4000, [Cell("TOP", Rect(L1, -1, -1, 493_827, 1))]),
        new("empty-cell", 1000, [Cell("EMPTY"), Cell("TOP", Rect(L1, 0, 0, 100, 100))]),
    ];

    public static TheoryData<string> Names()
    {
        var d = new TheoryData<string>();
        foreach (var c in All) d.Add(c.Name);
        return d;
    }

    public static Case Named(string name) => All.Single(c => c.Name == name);

    /// <summary>The plan an export writes for a case: the structures as they are, at the case's unit. What
    /// <see cref="GdsiiExport.Analyze"/> would hand either writer for the same cells, without the cell folders.</summary>
    public static GdsiiExport.ExportPlan Plan(Case c) => new(
        0, 0, 0, [], [], c.Structures.ToDictionary(s => s.Name, s => s.Name), [.. c.Structures],
        new GdsiiUnits(1e-6, 1e-6 / c.DbuPerMicron), null);

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static InterchangeStructure Cell(string name, params LayoutShape[] shapes) => new(name, [.. shapes], []);

    private static InterchangeStructure Cell(string name, LayoutShape[] shapes, params LayoutInstance[] instances) =>
        new(name, [.. shapes], [.. instances]);
}
