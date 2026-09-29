// brief-em3d-89 — a Surfaces or Faces field plot as a PICTURE: the triangles the 3D view draws (FieldSurfacePlot), seen along a
// stated direction, orthographic, with their hidden surfaces removed by a software depth buffer. `render --field` draws it; the
// 3D view draws the same triangles on the GPU.
//
// ── What is drawn, and how it is lit ──────────────────────────────────────────────────────────
//
// The field's surfaces, each moved by the nudge the view draws it with (a painted face a hair in front of its own face), and
// every object of the 3D view's scene the field does not stand in for — shaded as the view's shader shades it (its colour times
// 0.3 + 0.7 |n·v|), a translucent one blended over what lies behind it. The field is NOT shaded: its colour is its value, as in
// the view. The value is interpolated per SAMPLE from the triangle's corner channels and read through the quantity, the range and
// the map there — the GPU's own order (brief-em3d-84's note on why a blend of corner colours reads the wrong value).
//
// Over it, every object's feature edges (a turn sharper than 30°, a silhouette, an open edge — Em3dSectionScene.FeatureEdges, the
// outline's own rule) wherever the depth buffer does not hide them. The window draws no edges; a picture needs them, because an
// unshaded field on a closed package reads as a flat blob.
//
// ── Why a depth buffer ────────────────────────────────────────────────────────────────────────
//
// A painter's sort of the triangles is right for a convex package and wrong for interlocking parts, which a bond-wire package
// always has; a depth buffer is right for both, and a PNG is pixels anyway (owner decision: PNG first). The buffer is
// SUPERSAMPLED (up to 3 × 3 per pixel, within a sample budget) and averaged down, which is the anti-aliasing.
//
// Deterministic: no clock, no hash-ordered iteration, no threads — the same plot gives the same bytes.

using System.Numerics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Fields;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>An object of the scene drawn SHADED in a surface picture (the field does not stand in for it): its triangles in world
/// metres, three corners each, its colour and whether it is translucent.</summary>
public sealed record Em3dShadedPart(string Object, string? Material, double[] Xyz, SKColor Colour, bool Translucent);

/// <summary>A field surface in a surface picture: its triangles in world metres (nudged as the view draws it), three corners
/// each, and each corner's channels.</summary>
public sealed record Em3dFieldPart(string Object, double[] Xyz, double[] Values, int Channels);

/// <summary>What a surface picture paints in its frame (brief-em3d-89).</summary>
public sealed class Em3dSurfaceLayer
{
    public required Em3dProjection Projection { get; init; }
    public required IReadOnlyList<Em3dShadedPart> Shaded { get; init; }
    public required IReadOnlyList<Em3dFieldPart> Fields { get; init; }
    /// <summary>Every drawn object's feature edges, world metres.</summary>
    public required IReadOnlyList<(Point3 A, Point3 B)> Edges { get; init; }
    public required FieldQuantity Quantity { get; init; }
    /// <summary>The phase an instantaneous quantity is read at, radians.</summary>
    public double Phase { get; init; }
    /// <summary>How far behind the depth buffer an edge may lie and still be drawn, metres: past the nudge a painted face is
    /// drawn in front of its own face with, so that face's edges show.</summary>
    public double NudgeM { get; init; }
    /// <summary>The hottest point drawn (a temperature), world metres, or null.</summary>
    public Point3? HotSpot { get; init; }
    /// <summary>The caption's lines: the first bold.</summary>
    public IReadOnlyList<string> Caption { get; init; } = [];
    /// <summary>The caption's first line alone: what the picture is.</summary>
    public string Title { get; init; } = "";
    /// <summary>The objects drawn, shaded or under the field, in draw order.</summary>
    public IReadOnlyList<string> Objects { get; init; } = [];
    /// <summary>Each object's material, for <c>--labels</c>.</summary>
    public IReadOnlyDictionary<string, string> Materials { get; init; } = new Dictionary<string, string>();

    public int Triangles => Fields.Sum(f => f.Xyz.Length / 9);
}

/// <summary>What <see cref="Em3dSurfaceField.Raster"/> made: the frame's image, where the hot spot is (device units) when it is
/// visible, and each object's visible area and centre (device units) for its label.</summary>
public sealed record Em3dSurfaceRaster(SKBitmap Image, SKRect Rect, SKPoint? HotSpot,
                                       IReadOnlyList<(string Object, SKPoint Centre, float Area, SKRect Bounds)> Visible);

public static class Em3dSurfaceField
{
    /// <summary>The most depth-buffer samples one picture takes: 8 M, ~100 MB of buffers. A frame this fills at 3 × 3 per pixel is
    /// ~940 × 940 pixels; a larger one takes 2 × 2, then 1.</summary>
    public const long SampleBudget = 8_000_000;

    /// <summary>
    /// The layer and the page's scene for a surface plot: <paramref name="surfaces"/> (scene-local, as the field builders return
    /// them) moved by <paramref name="nudges"/> and placed in the world, every object of <paramref name="scene"/> not in
    /// <paramref name="covered"/> shaded, and the feature edges of every object drawn — reflected across each of
    /// <paramref name="mirrorEdges"/> (world metres) as well. The frame is what all of it projects to.
    /// </summary>
    public static (Em3dFieldLayer Layer, Em3dScene Scene) Build(
        Scene3DModel scene, Em3dProblem problem, Em3dProjection projection,
        IReadOnlyList<FieldSurface> surfaces, IReadOnlyList<Vector3> nudges, IReadOnlyList<string> objects, IReadOnlyCollection<string> covered,
        FieldQuantity q, FieldColorScale scale, double phase, IReadOnlyList<string> legend, IReadOnlyList<string> caption,
        IReadOnlyList<(int Axis, double AtM)[]>? mirrorEdges = null, Point3? hotSpot = null)
    {
        var (ox, oy, oz) = scene.Origin;
        var fields = new List<Em3dFieldPart>();
        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            var n = i < nudges.Count ? nudges[i] : Vector3.Zero;
            var xyz = new double[s.Xyz.Length];
            for (int v = 0; v < s.VertexCount; v++)
            {
                xyz[3 * v] = s.Xyz[3 * v] + n.X + ox;
                xyz[3 * v + 1] = s.Xyz[3 * v + 1] + n.Y + oy;
                xyz[3 * v + 2] = s.Xyz[3 * v + 2] + n.Z + oz;
            }
            fields.Add(new Em3dFieldPart(i < objects.Count ? objects[i] : "", xyz, s.Values, s.Channels));
        }

        var materials = problem.Solids.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Material, StringComparer.Ordinal);
        foreach (var sh in problem.Sheets) materials.TryAdd(sh.Name, sh.Material);
        var shaded = new List<Em3dShadedPart>();
        foreach (var o in scene.Objects)
        {
            if (o.Kind is Scene3DKind.Air or Scene3DKind.Boundary or Scene3DKind.Port || !o.InitiallyVisible || o.Context ||
                covered.Contains(o.Name) || (o.Rgba >> 24) == 0) continue;
            var (first, count, offset) = Scene3DFaces.TrianglesAt(scene, o.Id);
            if (count < 3) continue;
            var xyz = new double[3 * count];
            for (int i = 0; i < count; i++)
            {
                var v = scene.Vertices[scene.Indices[first + i]];
                xyz[3 * i] = v.X + offset.X + ox;
                xyz[3 * i + 1] = v.Y + offset.Y + oy;
                xyz[3 * i + 2] = v.Z + offset.Z + oz;
            }
            uint c = o.Rgba;
            shaded.Add(new Em3dShadedPart(o.Name, o.Material, xyz, new SKColor((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24)),
                                          o.Translucent || (c >> 24) < 255));
        }

        // every object drawn — shaded, or under the field — as the outline takes it; slabs clipped to the frame as the outline clips them
        var drawn = new HashSet<string>(shaded.Select(p => p.Object).Concat(covered), StringComparer.Ordinal);
        var meshes = new List<Em3dTriangleMesh>();
        foreach (var s in problem.Solids)
            if (s.Role != Em3dRole.Air && drawn.Contains(s.Name)) meshes.Add(Em3dTessellation.Of(s));
        foreach (var sh in problem.Sheets)
            if (drawn.Contains(sh.Name)) meshes.Add(Em3dTessellation.OfSheet(sh));
        // Mirrored, a face ON a symmetry plane is inside the whole part: its edges are a seam the whole part does not have.
        var planes = (mirrorEdges ?? []).SelectMany(x => x).Distinct().ToList();
        var box = problem.Boundary;
        double seam = 1e-9 * Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        bool OnPlane(Point3 p, (int Axis, double AtM) pl) => Math.Abs((pl.Axis == 0 ? p.X : pl.Axis == 1 ? p.Y : p.Z) - pl.AtM) <= seam;
        var edges = new List<(Point3, Point3)>();
        void Edge(Point3 a, Point3 b)
        {
            if (!planes.Any(pl => OnPlane(a, pl) && OnPlane(b, pl))) edges.Add((a, b));
        }
        foreach (var m in meshes) Em3dSectionScene.FeatureEdges(m, projection.Toward, Edge);
        foreach (var set in mirrorEdges ?? [])
            foreach (var m in meshes)
                Em3dSectionScene.FeatureEdges(m with { Vertices = [.. m.Vertices.Select(p => Reflect(p, set))] }, projection.Toward, Edge);

        // the frame: what everything drawn projects to
        double u0 = double.PositiveInfinity, v0 = u0, u1 = double.NegativeInfinity, v1 = u1;
        void Grow(double[] xyz)
        {
            for (int i = 0; i + 2 < xyz.Length; i += 3)
            {
                var p = projection.Project(new Point3(xyz[i], xyz[i + 1], xyz[i + 2]));
                u0 = Math.Min(u0, p.U); v0 = Math.Min(v0, p.V); u1 = Math.Max(u1, p.U); v1 = Math.Max(v1, p.V);
            }
        }
        foreach (var f in fields) Grow(f.Xyz);
        foreach (var p in shaded) Grow(p.Xyz);
        if (double.IsInfinity(u0))
        {
            var b = problem.Boundary;
            Grow([b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z]);
        }

        var surface = new Em3dSurfaceLayer
        {
            Projection = projection, Shaded = shaded, Fields = fields, Edges = edges, Quantity = q, Phase = phase,
            NudgeM = FieldSurfacePlot.NudgeLength(scene), HotSpot = hotSpot, Caption = caption, Title = caption.Count > 0 ? caption[0] : "",
            Objects = [.. drawn.OrderBy(x => x, StringComparer.Ordinal)], Materials = materials,
        };
        var layer = new Em3dFieldLayer
        {
            Vertices = [], RasterVertices = [], VertexColours = [], Pieces = [], Raster = true, Legend = legend,
            Map = ColorMap3D.For(q), Scale = scale, Surface = surface,
        };
        var dielectrics = problem.Solids.Where(s => s.Role == Em3dRole.Dielectric).Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();
        double largest = Math.Max(u1 - u0, v1 - v0);
        var pageScene = new Em3dScene(new Em3dView(Em3dViewKind.Projection, 0) { Projection = projection }, 0,
                                      Math.Max(1e-15, Em3dSectionScene.SnapFraction * largest), [], [], [], [],
                                      new Uv(u0, v0), new Uv(u1, v1), [], dielectrics);
        return (layer, pageScene);
    }

    /// <summary>
    /// The caption: what is drawn on what, from where, of which run; how it was drawn (and that a mirrored half repeats the
    /// modelled one); and, for a temperature, the boundaries — or, for an EM field, the faces that could not be painted.
    /// </summary>
    public static List<string> Caption(bool temperature, string symbol, string target, Em3dProjection view, string setupName, string label,
                                       int mirrorPlanes, int wires, IReadOnlyList<string> boundaries, IReadOnlyList<string> refused)
    {
        var lines = new List<string>
        {
            $"{(temperature ? "Temperature" : symbol)} on {target}, {view.Name}, orthographic — setup '{setupName}', {label}",
            "Hidden surfaces removed; the field's colour is its value." +
            (mirrorPlanes > 0 ? $" Mirrored across {mirrorPlanes} symmetry plane{(mirrorPlanes == 1 ? "" : "s")}: the mirrored half repeats the modelled one." : "") +
            (wires > 0 ? " Bond wires painted from their own solved T(s)." : ""),
        };
        if (temperature) lines.Add(Em3dSectionThermal.BoundariesLine(boundaries));
        else if (refused.Count > 0) lines.Add(string.Join(" ", refused));
        return lines;
    }

    private static Point3 Reflect(Point3 p, (int Axis, double AtM)[] set)
    {
        foreach (var (axis, at) in set)
            p = axis switch { 0 => p with { X = 2 * at - p.X }, 1 => p with { Y = 2 * at - p.Y }, _ => p with { Z = 2 * at - p.Z } };
        return p;
    }

    /// <summary>
    /// <paramref name="layer"/>'s surface painted into <paramref name="page"/>'s frame at <paramref name="devicePerUnit"/> device
    /// pixels per canvas unit: depth-buffered, supersampled, the edges in <paramref name="ink"/> over it.
    /// </summary>
    public static Em3dSurfaceRaster Raster(Em3dFieldLayer layer, Em3dPageLayout page, float devicePerUnit, SKColor ink)
    {
        var s = layer.Surface ?? throw new ArgumentException("not a surface layer", nameof(layer));
        var frame = page.Frame;
        int w = Math.Max(1, (int)Math.Ceiling(frame.Width * devicePerUnit)), h = Math.Max(1, (int)Math.Ceiling(frame.Height * devicePerUnit));
        int ss = (long)w * h * 9 <= SampleBudget ? 3 : (long)w * h * 4 <= SampleBudget ? 2 : 1;
        int sw = w * ss, sh = h * ss, n = sw * sh;
        double k = devicePerUnit * ss;                                   // samples per canvas unit
        var proj = s.Projection;

        // world → (sample x, sample y, depth)
        (double X, double Y, double Z) P(double x, double y, double z)
        {
            var p = new Point3(x, y, z);
            var uv = proj.Project(p);
            return ((page.Area.MidX + (uv.U - page.CentreU) * page.Scale - frame.Left) * k,
                    (page.Area.MidY - (uv.V - page.CentreV) * page.Scale - frame.Top) * k, proj.Depth(p));
        }

        // every triangle: its corners in sample space and depth; the part it belongs to (shaded parts first, then the fields)
        var tri = new List<double>();
        var part = new List<int>();
        var local = new List<int>();
        var shade = new List<float>();
        int shadedCount = s.Shaded.Count;
        void Add(double[] xyz, int p, bool lit)
        {
            for (int t = 0; t + 8 < xyz.Length; t += 9)
            {
                var a = P(xyz[t], xyz[t + 1], xyz[t + 2]);
                var b = P(xyz[t + 3], xyz[t + 4], xyz[t + 5]);
                var c = P(xyz[t + 6], xyz[t + 7], xyz[t + 8]);
                tri.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z]);
                part.Add(p);
                local.Add(t / 9);
                float f = 1;
                if (lit)
                {
                    double e1x = xyz[t + 3] - xyz[t], e1y = xyz[t + 4] - xyz[t + 1], e1z = xyz[t + 5] - xyz[t + 2];
                    double e2x = xyz[t + 6] - xyz[t], e2y = xyz[t + 7] - xyz[t + 1], e2z = xyz[t + 8] - xyz[t + 2];
                    double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    double d = len > 0 ? Math.Abs(nx * proj.Toward.X + ny * proj.Toward.Y + nz * proj.Toward.Z) / len : 1;
                    f = (float)(0.3 + 0.7 * d);                                    // the view's shader
                }
                shade.Add(f);
            }
        }
        for (int i = 0; i < shadedCount; i++) Add(s.Shaded[i].Xyz, i, lit: true);
        for (int i = 0; i < s.Fields.Count; i++) Add(s.Fields[i].Xyz, shadedCount + i, lit: false);
        var T = tri.ToArray();
        int triangles = part.Count;

        var depth = new float[n];
        Array.Fill(depth, float.NegativeInfinity);
        var owner = new int[n];
        Array.Fill(owner, -1);

        // ── opaque: nearest wins ────────────────────────────────────────────────────────────
        for (int t = 0; t < triangles; t++)
        {
            int p = part[t];
            if (p < shadedCount && s.Shaded[p].Translucent) continue;
            Scan(T, t, sw, sh, (idx, z) => { if (z > depth[idx]) { depth[idx] = (float)z; owner[idx] = t; } });
        }

        // ── colour: shaded parts lit, fields read through the quantity, range and map, per sample ─────────────
        var colour = new uint[n];                                             // premultiplied RGBA8, R low
        var map = layer.Map;
        var scale = layer.Scale;
        var channels = new double[s.Fields.Count == 0 ? 1 : s.Fields.Max(f => f.Channels)];
        for (int j = 0; j < sh; j++)
            for (int i = 0; i < sw; i++)
            {
                int idx = j * sw + i, t = owner[idx];
                if (t < 0) continue;
                int p = part[t];
                if (p < shadedCount)
                {
                    var c = s.Shaded[p].Colour;
                    float f = shade[t];
                    colour[idx] = Pack(c.Red * f, c.Green * f, c.Blue * f, 255);
                    continue;
                }
                var fp = s.Fields[p - shadedCount];
                var (w0, w1, w2) = Bary(T, t, i + 0.5, j + 0.5);
                int ch = fp.Channels, v = 3 * local[t];
                for (int c = 0; c < ch; c++)
                    channels[c] = w0 * fp.Values[v * ch + c] + w1 * fp.Values[(v + 1) * ch + c] + w2 * fp.Values[(v + 2) * ch + c];
                double pos = scale.Position(s.Quantity.Evaluate(channels.AsSpan(0, ch), s.Phase));
                var (r, g, b) = map.Sample((float)pos);
                colour[idx] = Pack(r, g, b, 255);
            }

        // ── translucent: back to front, over what the opaque pass left in front of them ────────────────────
        // A translucent face lying ON an opaque one (a mould's bottom on the flange it sits on) is in front of it: without the
        // tolerance the two depths tie sample by sample and the tint comes out in stripes.
        double sample = 1 / (k * page.Scale);                                // metres per sample
        double coplanar = sample + s.NudgeM;
        var order = Enumerable.Range(0, triangles).Where(t => part[t] < shadedCount && s.Shaded[part[t]].Translucent)
                              .OrderBy(t => T[9 * t + 2] + T[9 * t + 5] + T[9 * t + 8]).ThenBy(t => t).ToList();
        foreach (int t in order)
        {
            var c = s.Shaded[part[t]].Colour;
            float f = shade[t], a = c.Alpha / 255f;
            float sr = c.Red * f * a, sg = c.Green * f * a, sb = c.Blue * f * a;
            Scan(T, t, sw, sh, (idx, z) =>
            {
                if (z < depth[idx] - coplanar) return;
                uint d = colour[idx];
                float k1 = 1 - a;
                colour[idx] = Pack(sr + (d & 0xFF) * k1, sg + ((d >> 8) & 0xFF) * k1, sb + ((d >> 16) & 0xFF) * k1, a * 255 + (d >> 24) * k1);
            });
        }

        // ── edges, wherever the depth buffer does not hide them ────────────────────────────────────────────
        double tol = 2 * sample + 3 * s.NudgeM;
        var inked = new bool[n];
        double half = ss / 2.0;                                               // one device pixel wide
        foreach (var (ea, eb) in s.Edges)
        {
            var a = P(ea.X, ea.Y, ea.Z);
            var b = P(eb.X, eb.Y, eb.Z);
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            int steps = Math.Max(1, (int)Math.Ceiling(len * 2));
            for (int st = 0; st <= steps; st++)
            {
                double f = (double)st / steps;
                double x = a.X + f * (b.X - a.X), y = a.Y + f * (b.Y - a.Y), z = a.Z + f * (b.Z - a.Z);
                int i0 = (int)Math.Floor(x - half), i1 = (int)Math.Ceiling(x + half) - 1;
                int j0 = (int)Math.Floor(y - half), j1 = (int)Math.Ceiling(y + half) - 1;
                for (int j = Math.Max(0, j0); j <= Math.Min(sh - 1, j1); j++)
                    for (int i = Math.Max(0, i0); i <= Math.Min(sw - 1, i1); i++)
                    {
                        int idx = j * sw + i;
                        if (z >= depth[idx] - tol) inked[idx] = true;
                    }
            }
        }

        // ── down to pixels ──────────────────────────────────────────────────────────────────
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var px = new byte[4 * w * h];
        float ia = ink.Alpha / 255f * 0.75f;
        float ir = ink.Red * ia, ig = ink.Green * ia, ib = ink.Blue * ia;
        float inv = 1f / (ss * ss);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, al = 0;
                for (int j = 0; j < ss; j++)
                    for (int i = 0; i < ss; i++)
                    {
                        int idx = (y * ss + j) * sw + x * ss + i;
                        uint d = colour[idx];
                        float cr = d & 0xFF, cg = (d >> 8) & 0xFF, cb = (d >> 16) & 0xFF, ca = d >> 24;
                        if (inked[idx])
                        {
                            float k1 = 1 - ia;
                            cr = ir + cr * k1; cg = ig + cg * k1; cb = ib + cb * k1; ca = ia * 255 + ca * k1;
                        }
                        r += cr; g += cg; b += cb; al += ca;
                    }
                int o = 4 * (y * w + x);
                px[o] = (byte)Math.Clamp(Math.Round(r * inv), 0, 255);
                px[o + 1] = (byte)Math.Clamp(Math.Round(g * inv), 0, 255);
                px[o + 2] = (byte)Math.Clamp(Math.Round(b * inv), 0, 255);
                px[o + 3] = (byte)Math.Clamp(Math.Round(al * inv), 0, 255);
            }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        var rect = new SKRect(frame.Left, frame.Top, frame.Left + w / devicePerUnit, frame.Top + h / devicePerUnit);

        // the hot spot, where the depth buffer does not hide it
        SKPoint? hot = null;
        if (s.HotSpot is { } hs)
        {
            var q = P(hs.X, hs.Y, hs.Z);
            int i = (int)Math.Floor(q.X), j = (int)Math.Floor(q.Y);
            if (i >= 0 && j >= 0 && i < sw && j < sh && q.Z >= depth[j * sw + i] - tol)
                hot = new SKPoint(frame.Left + (float)(q.X / k), frame.Top + (float)(q.Y / k));
        }

        // each object's visible area and centre, for its label (a mirrored half is not labelled: its object is)
        var names = new List<string>();
        for (int p = 0; p < shadedCount; p++) names.Add(s.Shaded[p].Object);
        foreach (var f in s.Fields) names.Add(f.Object);
        var sums = new Dictionary<string, (double X, double Y, long N, int I0, int J0, int I1, int J1)>(StringComparer.Ordinal);
        for (int j = 0; j < sh; j++)
            for (int i = 0; i < sw; i++)
            {
                int t = owner[j * sw + i];
                if (t < 0) continue;
                string name = names[part[t]];
                if (name.Length == 0 || name.EndsWith(FieldSurfacePlot.MirroredSuffix, StringComparison.Ordinal)) continue;
                var e = sums.TryGetValue(name, out var x) ? x : (0, 0, 0, int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
                sums[name] = (e.X + i + 0.5, e.Y + j + 0.5, e.N + 1, Math.Min(e.I0, i), Math.Min(e.J0, j), Math.Max(e.I1, i), Math.Max(e.J1, j));
            }
        var visible = new List<(string, SKPoint, float, SKRect)>();
        foreach (var (name, e) in sums.OrderByDescending(kv => kv.Value.N).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            double cx = e.X / e.N, cy = e.Y / e.N;
            int ci = (int)cx, cj = (int)cy;
            // the centre of a concave piece can fall off it: then the label would name what is under it
            if (owner[cj * sw + ci] is var t0 && (t0 < 0 || names[part[t0]] != name)) continue;
            visible.Add((name, new SKPoint(frame.Left + (float)(cx / k), frame.Top + (float)(cy / k)), (float)(e.N / (k * k)),
                         new SKRect(frame.Left + (float)(e.I0 / k), frame.Top + (float)(e.J0 / k), frame.Left + (float)((e.I1 + 1) / k),
                                    frame.Top + (float)((e.J1 + 1) / k))));
        }
        return new Em3dSurfaceRaster(bmp, rect, hot, visible);
    }

    /// <summary>Visits every sample whose centre triangle <paramref name="t"/> covers (its edges included), with its depth there.</summary>
    private static void Scan(double[] T, int t, int sw, int sh, Action<int, double> visit)
    {
        int o = 9 * t;
        double x0 = T[o], y0 = T[o + 1], z0 = T[o + 2], x1 = T[o + 3], y1 = T[o + 4], z1 = T[o + 5], x2 = T[o + 6], y2 = T[o + 7], z2 = T[o + 8];
        double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (!(Math.Abs(area) > 1e-12)) return;                              // edge-on: its neighbours, facing the viewer, draw
        int i0 = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)) - 0.5));
        int i1 = Math.Min(sw - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2)) - 0.5));
        int j0 = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)) - 0.5));
        int j1 = Math.Min(sh - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2)) - 0.5));
        const double Inside = -1e-9;
        for (int j = j0; j <= j1; j++)
        {
            double cy = j + 0.5;
            for (int i = i0; i <= i1; i++)
            {
                double cx = i + 0.5;
                double w0 = ((x1 - cx) * (y2 - cy) - (x2 - cx) * (y1 - cy)) / area;
                if (w0 < Inside) continue;
                double w1 = ((x2 - cx) * (y0 - cy) - (x0 - cx) * (y2 - cy)) / area;
                if (w1 < Inside) continue;
                double w2 = 1 - w0 - w1;
                if (w2 < Inside) continue;
                visit(j * sw + i, w0 * z0 + w1 * z1 + w2 * z2);
            }
        }
    }

    /// <summary>A sample's barycentric weights in triangle <paramref name="t"/>.</summary>
    private static (double W0, double W1, double W2) Bary(double[] T, int t, double cx, double cy)
    {
        int o = 9 * t;
        double x0 = T[o], y0 = T[o + 1], x1 = T[o + 3], y1 = T[o + 4], x2 = T[o + 6], y2 = T[o + 7];
        double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        double w0 = ((x1 - cx) * (y2 - cy) - (x2 - cx) * (y1 - cy)) / area;
        double w1 = ((x2 - cx) * (y0 - cy) - (x0 - cx) * (y2 - cy)) / area;
        return (w0, w1, 1 - w0 - w1);
    }

    private static uint Pack(float r, float g, float b, float a)
        => (uint)Math.Clamp((int)MathF.Round(r), 0, 255) | ((uint)Math.Clamp((int)MathF.Round(g), 0, 255) << 8) |
           ((uint)Math.Clamp((int)MathF.Round(b), 0, 255) << 16) | ((uint)Math.Clamp((int)MathF.Round(a), 0, 255) << 24);
}
