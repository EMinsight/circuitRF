// brief-em3d-28 R-em3d28-2 — the scene from the problem, through the one tessellation (R-em3d5-1).
//
// Everything is tessellated by Em3dTessellation, so the viewer's surfaces are the section renderer's,
// the CSXCAD writer's and the size estimate's: three consumers already agree on where a wire's surface
// is, and the viewer does not make a fourth answer.
//
// COLOURS (R-em3d28-2c). A conductor is painted with its drawing layer's colour from the technology,
// resolved by Em3dSectionRenderer.ObjectColours — the function brief 5's pictures use, so a via is the
// same colour in the 2D section, the layout editor and here. Dielectrics, air and the air-box faces
// take the fixed palette below, one set for the light variant and one for the dark, which is how the
// 2D editors follow the application theme. Ports take the theme's pin colour, as in the section view.
//
// FACES AND EDGES (brief-em3d-43 R-em3d43-3a / -5). Every vertex carries the face it lies on, so a vertex
// the tessellation shares between two faces (a box's corner is on three) is emitted once PER FACE: flat
// interpolation takes the face from one vertex, and a shared one would give a triangle its neighbour's
// face. Shading is flat already (the fragment shader's derivative normal), so nothing looks different. A
// sheet is one face, 0. Each solid and sheet also gets its FEATURE EDGES — where two of its faces meet,
// or a sheet's boundary — in the line buffer, drawn only when selected (the Object-mode outline).
//
// EDIT LOCALITY (R-em3d43-1b). With a Scene3DTessellationCache, a solid whose primitive is the same as
// last build's is not tessellated again: the elaborator hands back its cached primitive for an unchanged
// object, so an edit to one object of a thousand tessellates one. An editor also fixes the ORIGIN, so
// an edit that does not move the bounds' centre leaves every other object's vertex bytes identical — which
// is what lets the session upload only the changed ranges (Scene3DPatch).
//
// THE PALETTE, documented because it is a user-visible choice: dielectrics cycle through six hues in
// problem order (green, blue, amber, violet, teal, rose) at 35 % opacity, so a stack of two substrates
// is two colours; air is a pale blue at 8 %; an air-box face is grey for PEC, blue for absorbing,
// orange for PMC and violet for symmetry.

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Theming;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Render.Scene3D;

/// <summary>brief-em3d-43 — what an editor's build adds to the viewer's.</summary>
/// <param name="FaceNames">An object's face names by its name (the elaboration's provenance), or null.</param>
/// <param name="Cache">Tessellations kept from the last build; null tessellates everything.</param>
/// <param name="DrawAirBox">False draws no air-box faces or edges — a document with no setup has none.</param>
/// <param name="Origin">The scene-local origin to use, world metres; null takes the air box's centre.</param>
/// <param name="FeatureShare">brief-em3d-44 R-em3d44-3b — an object's share key and translation when it is one
/// element of an instance (objects under one key share ONE feature table), or null for its own table.</param>
/// <param name="Instancing">brief-em3d-48 R-em3d48-3a — where an object stands in an array or a repeated placement, or
/// null: an element after the first is not tessellated — it draws the first's triangles under its own offset.</param>
/// <param name="Context">brief-em3d-48 R-em3d48-4a — true for the parent drawn around a pushed-in child: dimmed, and
/// never hovered or selected (the snap still reaches it).</param>
/// <param name="EditorBoundaries">brief-em3d-49 R-em3d49-3a — the air box as the 3D editor draws its active setup's: an
/// absorbing face untinted, a PEC face metal grey, a PMC face its own hue, a symmetry face hatched, and every face
/// pickable last (<see cref="Scene3DObject.PickLast"/>). False — the read-only viewer — keeps brief 28's tints.</param>
/// <param name="FaceTints">brief-em3d-49 R-em3d49-4d — the face boundaries, each drawn tinted just off its face.</param>
/// <param name="Wireframe">3D editor bugs round 1 — true for an object with no material: drawn as a wireframe
/// (<see cref="Scene3DObject.Wireframe"/>), never taken for the outermost dielectric.</param>
public sealed record Scene3DBuildOptions(
    Func<string, IReadOnlyList<string>?>? FaceNames = null,
    Scene3DTessellationCache? Cache = null,
    bool DrawAirBox = true,
    (double X, double Y, double Z)? Origin = null,
    Func<string, Scene3DFeatureShare?>? FeatureShare = null,
    Func<string, Scene3DInstancing?>? Instancing = null,
    Func<string, bool>? Context = null,
    bool EditorBoundaries = false,
    IReadOnlyList<Scene3DFaceTint>? FaceTints = null,
    Func<string, bool>? Wireframe = null);

/// <summary>brief-em3d-49 R-em3d49-4d — one face boundary to draw: its name (<c>object/face</c>), its kind and its pieces.</summary>
public sealed record Scene3DFaceTint(string Name, Em3dFaceBoundaryKind Kind, IReadOnlyList<Em3dFacePolygon> Pieces);

/// <summary>
/// brief-em3d-48 R-em3d48-3a — an object's place in a RUN: the objects one placement of one child document puts in the
/// problem under one rotation, element after element, each element's objects in the child's own order. <see cref="Run"/>
/// is compared with Equals; <see cref="Element"/> numbers the elements; (<see cref="Tx"/>, <see cref="Ty"/>,
/// <see cref="Tz"/>) is the element's translation (world metres) and <see cref="LocalName"/> the object's name inside
/// the element — which is what pairs an element's k-th object with the prototype's.
/// </summary>
public readonly record struct Scene3DInstancing(object Run, int Element, double Tx, double Ty, double Tz, string LocalName);

/// <summary>
/// brief-em3d-43 R-em3d43-1b — tessellations kept between builds, keyed by the primitive (a sheet by its
/// outline, holes, height and frame). A key a build did not use is dropped at its end, so the cache holds
/// one scene's worth, never a history. Not thread-safe: one per document, used by one build at a time.
/// </summary>
public sealed class Scene3DTessellationCache
{
    private Dictionary<object, Em3dTriangleMesh> _kept = [];
    private Dictionary<object, Em3dTriangleMesh> _used = [];
    private long _misses;

    /// <summary>Tessellations this cache had to make — one per primitive it had not kept.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    internal Em3dTriangleMesh Get(object key, Func<Em3dTriangleMesh> make, ref long misses)
    {
        if (_used.TryGetValue(key, out var m)) return m;
        if (!_kept.TryGetValue(key, out m))
        {
            m = make();
            Interlocked.Increment(ref misses);
            Interlocked.Increment(ref _misses);
        }
        _used[key] = m;
        return m;
    }

    internal void EndBuild()
    {
        (_kept, _used) = (_used, _kept);
        _used.Clear();
    }

    /// <summary>How many tessellations are kept.</summary>
    public int Count => _kept.Count;
}

/// <summary>Builds a <see cref="Scene3DModel"/> from an <see cref="Em3dProblem"/>.</summary>
public static class Scene3DBuilder
{
    /// <summary>Opacity of a dielectric solid.</summary>
    public const byte DielectricAlpha = 90;
    /// <summary>Opacity of the air.</summary>
    public const byte AirAlpha = 20;
    /// <summary>Opacity of an air-box face.</summary>
    public const byte FaceAlpha = 40;
    /// <summary>Opacity of a port's sheet.</summary>
    public const byte PortAlpha = 170;
    /// <summary>A port's arrow is this fraction of the port's longer side.</summary>
    public const float ArrowFraction = 0.8f;
    /// <summary>Segments around a coaxial port's annulus.</summary>
    public const int AnnulusSegments = 32;

    /// <summary>brief-em3d-53 M6 — a material's own display colour (<c>#rrggbb</c>), when its technology states one;
    /// null keeps the scene's palette. A qualified or stackup-derived name finds nothing, which is the palette.</summary>
    private static (byte R, byte G, byte B)? MaterialColour(Technology? tech, string material)
    {
        if (tech?.FindMaterial(material)?.Color is not { Length: 7 } hex || hex[0] != '#') return null;
        return uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint v)
            ? ((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : null;
    }

    private static long _tessellations;

    /// <summary>How many solids and sheets have been tessellated in this process — gate 5's counter
    /// (a hover must not move it).</summary>
    public static long Tessellations => Interlocked.Read(ref _tessellations);

    private static readonly (byte R, byte G, byte B)[] DielectricLight =
        [(92, 158, 92), (89, 140, 191), (191, 153, 77), (153, 115, 179), (77, 166, 166), (179, 102, 102)];
    private static readonly (byte R, byte G, byte B)[] DielectricDark =
        [(120, 190, 120), (120, 170, 225), (225, 185, 105), (185, 150, 215), (105, 200, 200), (215, 135, 135)];

    /// <summary>The scene for <paramref name="problem"/>. <paramref name="origins"/> and
    /// <paramref name="tech"/> colour the conductors; either may be absent.</summary>
    public static Scene3DModel Build(Em3dProblem problem, long generation,
                                     IReadOnlyDictionary<string, Em3dObjectOrigin>? origins = null,
                                     Technology? tech = null, ColorTheme? theme = null,
                                     ColorVariant variant = ColorVariant.Light,
                                     IReadOnlyList<string>? notes = null, Scene3DBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        origins ??= new Dictionary<string, Em3dObjectOrigin>();
        theme ??= ColorTheme.BuiltIn;
        options ??= new Scene3DBuildOptions();
        bool dark = variant == ColorVariant.Dark;

        var box = problem.Boundary;
        var origin = options.Origin is { } fixedOrigin
            ? (fixedOrigin.X, fixedOrigin.Y, fixedOrigin.Z)
            : ((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, (box.Min.Z + box.Max.Z) / 2);
        var cache = options.Cache;
        Em3dTriangleMesh Tessellate(object key, Func<Em3dTriangleMesh> make)
        {
            if (cache is not null) return cache.Get(key, make, ref _tessellations);
            Interlocked.Increment(ref _tessellations);
            return make();
        }
        IReadOnlyList<string> FacesOf(string name) => options.FaceNames?.Invoke(name) ?? [];
        // brief-em3d-44 R-em3d44-3 — each solid's and sheet's snap features, one table per shared key.
        var shared = new Dictionary<object, (Scene3DFeatureTable Table, double X, double Y, double Z)>();
        Scene3DFeatureRef Features(string name, Em3dTriangleMesh mesh, bool sheet)
        {
            if (options.FeatureShare?.Invoke(name) is not { } share) return new(Scene3DFeatureTable.Of(mesh, sheet), 0, 0, 0, false);
            if (shared.TryGetValue(share.Key, out var t))
                return new(t.Table, share.Tx - t.X, share.Ty - t.Y, share.Tz - t.Z, true);
            var table = Scene3DFeatureTable.Of(mesh, sheet);
            shared[share.Key] = (table, share.Tx, share.Ty, share.Tz);
            return new(table, 0, 0, 0, true);
        }
        Vector3 L(Point3 p) => new((float)(p.X - origin.Item1), (float)(p.Y - origin.Item2), (float)(p.Z - origin.Item3));

        var conductorColours = Em3dSectionRenderer.ObjectColours(problem, origins, tech, theme, variant);
        var pin = theme.Resolve(ColorRole.LayoutPCellPin, variant);
        var ink = dark ? (R: (byte)205, G: (byte)210, B: (byte)215) : (R: (byte)60, G: (byte)64, B: (byte)70);
        var dielectrics = problem.Solids.Where(s => s.Role == Em3dRole.Dielectric && options.Wireframe?.Invoke(s.Name) != true)
                                 .Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();
        var materials = problem.Materials.Select((m, i) => (m, i)).ToDictionary(t => t.m.Name, t => t, StringComparer.Ordinal);

        var b = new Accumulator(L);
        var solidRuns = new RunTracker(b);
        var sheetRuns = new RunTracker(b);
        bool Dim(string name) => options.Context?.Invoke(name) == true;

        // ── solids ───────────────────────────────────────────────────────────────────────────
        bool Wire(string name) => options.Wireframe?.Invoke(name) == true;
        string? outermost = OutermostDielectric(problem, Wire);
        // 3D editor bugs round 1 — a wireframe object's triangles: the ink colour at alpha 0; its edges: the ink, opaque.
        // 3D editor bugs round 2 — on a light background the mid-grey ink was too faint for an object drawn as its edges
        // ALONE (one-pixel lines, nothing filled): the wireframe's edges are near-black there. Dark keeps the ink.
        var wireInk = dark ? ink : (R: (byte)20, G: (byte)22, B: (byte)28);
        uint wireFill = Scene3DVertex.Pack(wireInk.R, wireInk.G, wireInk.B, 0), wireEdge = Scene3DVertex.Pack(wireInk.R, wireInk.G, wireInk.B, 255);
        foreach (var s in problem.Solids)
        {
            var place = options.Instancing?.Invoke(s.Name);
            if (place is { } pl && solidRuns.Element(pl, s.Name)) continue;
            bool wire = Wire(s.Name);
            var kind = wire ? Scene3DKind.Body : KindOf(s, origins);
            uint rgba;
            bool translucent = false;
            switch (s.Role)
            {
                case Em3dRole.Air:
                    rgba = dark ? Scene3DVertex.Pack(150, 190, 255, AirAlpha) : Scene3DVertex.Pack(140, 185, 240, AirAlpha);
                    translucent = true;
                    break;
                case Em3dRole.Dielectric:
                {
                    var pal = dark ? DielectricDark : DielectricLight;
                    var c = MaterialColour(tech, s.Material) ?? pal[Math.Max(0, dielectrics.IndexOf(s.Material)) % pal.Length];
                    rgba = Scene3DVertex.Pack(c.R, c.G, c.B, DielectricAlpha);
                    translucent = true;
                    break;
                }
                default:
                {
                    var c = conductorColours.TryGetValue(s.Name, out var sk) ? sk : new SkiaSharp.SKColor(150, 150, 155);
                    if (MaterialColour(tech, s.Material) is { } own) c = new SkiaSharp.SKColor(own.R, own.G, own.B);
                    rgba = Scene3DVertex.Pack(c.Red, c.Green, c.Blue, 255);
                    break;
                }
            }
            bool dim = Dim(s.Name);
            if (dim) (rgba, translucent) = (Dimmed(rgba, dark), true);
            if (wire) (rgba, translucent) = (wireFill, true);
            var solid = s;
            var mesh = Tessellate(s.Primitive, () => Em3dTessellation.Of(solid));
            var (m, slot) = materials.TryGetValue(s.Material, out var mt) ? (mt.m, mt.i) : ((Em3dMaterial?)null, -1);
            b.Object(new Scene3DObject
            {
                Id = 0, Name = s.Name, Kind = kind, Material = s.Material, MaterialValues = m, MaterialSlot = slot,
                Rgba = rgba, Translucent = translucent,
                InitiallyVisible = wire || (s.Role != Em3dRole.Air && s.Name != outermost),
                FaceNames = FacesOf(s.Name),
                CapCentres = s.Primitive is Em3dCylinder cyl ? [cyl.AxisStart, cyl.AxisEnd] : null,
                Context = dim, Wireframe = wire,
            }, mesh, wire && s.Primitive is Em3dCylinder c0 ? CylinderGenerators(c0).Select(q => (q, wireEdge)) : null,
               faces: true, features: Features(s.Name, mesh, sheet: false), wireEdges: wire ? wireEdge : null);
            if (place is { } p0) solidRuns.Prototype(p0, s.Name);
            else solidRuns.Break();
        }

        // ── sheets ───────────────────────────────────────────────────────────────────────────
        foreach (var sh in problem.Sheets)
        {
            var place = options.Instancing?.Invoke(sh.Name);
            if (place is { } pl && sheetRuns.Element(pl, sh.Name)) continue;
            var c = conductorColours.TryGetValue(sh.Name, out var sk) ? sk : new SkiaSharp.SKColor(150, 150, 155);
            if (MaterialColour(tech, sh.Material) is { } own) c = new SkiaSharp.SKColor(own.R, own.G, own.B);
            var sheet = sh;
            var mesh = Tessellate((sh.Outline, sh.Holes, sh.Z, sh.Frame), () => Em3dTessellation.OfSheet(sheet));
            var (m, slot) = materials.TryGetValue(sh.Material, out var mt) ? (mt.m, mt.i) : ((Em3dMaterial?)null, -1);
            var names = FacesOf(sh.Name);
            bool dim = Dim(sh.Name);
            bool wire = Wire(sh.Name);
            uint rgba = Scene3DVertex.Pack(c.Red, c.Green, c.Blue, 255);
            b.Object(new Scene3DObject
            {
                Id = 0, Name = sh.Name, Kind = Scene3DKind.Sheet, Material = sh.Material, MaterialValues = m,
                MaterialSlot = slot, Rgba = wire ? wireFill : dim ? Dimmed(rgba, dark) : rgba, Translucent = dim || wire,
                FaceNames = names.Count > 0 ? names : SheetFaceNames, Context = dim, Wireframe = wire,
            }, mesh, faces: true, sheet: true, features: Features(sh.Name, mesh, sheet: true), wireEdges: wire ? wireEdge : null);
            if (place is { } p0) sheetRuns.Prototype(p0, sh.Name);
            else sheetRuns.Break();
        }

        // ── ports: a named sheet and a direction arrow ───────────────────────────────────────
        foreach (var p in problem.Ports)
        {
            var obj = new Scene3DObject
            {
                Id = 0, Name = p.Name, Kind = Scene3DKind.Port, PortNumber = p.Number,
                Rgba = Scene3DVertex.Pack(pin.R, pin.G, pin.B, PortAlpha), Translucent = true,
            };
            var (verts, tris) = PortSheet(p);
            uint line = Scene3DVertex.Pack(pin.R, pin.G, pin.B, 255);
            b.Object(obj, new Em3dTriangleMesh(verts, tris), PortArrow(p).Select(q => (q, line)));
        }

        // ── brief-em3d-49: face boundaries, tinted just off their faces (never z-fighting the solid) ─
        if (options.FaceTints is { Count: > 0 } tints)
        {
            double lift = 1e-4 * Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
            foreach (var t in tints)
            {
                var (r, g, bl) = t.Kind == Em3dFaceBoundaryKind.Pec ? ((byte)150, (byte)150, (byte)158) : ((byte)214, (byte)168, (byte)64);
                uint fill = Scene3DVertex.Pack(r, g, bl, BoundaryTintAlpha), edge = Scene3DVertex.Pack(r, g, bl, 255);
                var verts = new List<Point3>();
                var tris = new List<Em3dTriangle>();
                var lines = new List<(Point3, uint)>();
                foreach (var piece in t.Pieces)
                {
                    // brief-em3d-65 — a curved kernel face (Palace takes a boundary on one): its own triangles, each lifted
                    // along its own normal.
                    if (piece.Mesh is { } curved)
                    {
                        foreach (var tr in curved.Triangles)
                        {
                            Point3 a = curved.Vertices[tr.A], c1 = curved.Vertices[tr.B], c2 = curved.Vertices[tr.C];
                            double ux = c1.X - a.X, uy = c1.Y - a.Y, uz = c1.Z - a.Z, vx = c2.X - a.X, vy = c2.Y - a.Y, vz = c2.Z - a.Z;
                            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                            var tn = nl > 0 ? new Point3(nx / nl, ny / nl, nz / nl) : new Point3(0, 0, 0);
                            Point3 Lift(Point3 q) => new(q.X + tn.X * lift, q.Y + tn.Y * lift, q.Z + tn.Z * lift);
                            int k0 = verts.Count;
                            verts.Add(Lift(a)); verts.Add(Lift(c1)); verts.Add(Lift(c2));
                            tris.Add(new Em3dTriangle(k0, k0 + 1, k0 + 2, t.Name));
                        }
                        continue;
                    }
                    var n = piece.Normal;
                    Point3 Up(Point3 q) => new(q.X + n.X * lift, q.Y + n.Y * lift, q.Z + n.Z * lift);
                    var mesh = Em3dTessellation.OfSheet(piece.AsSheet(t.Name, "", 0, 0));
                    int at = verts.Count;
                    verts.AddRange(mesh.Vertices.Select(Up));
                    tris.AddRange(mesh.Triangles.Select(tr => new Em3dTriangle(tr.A + at, tr.B + at, tr.C + at, t.Name)));
                    foreach (var ring in piece.Holes.Prepend(piece.Outer))
                        for (int k = 0; k < ring.Count; k++) { lines.Add((Up(ring[k]), edge)); lines.Add((Up(ring[(k + 1) % ring.Count]), edge)); }
                }
                b.Object(new Scene3DObject
                {
                    Id = 0, Name = FaceTintPrefix + t.Name, Kind = Scene3DKind.Boundary, Rgba = fill, Translucent = true,
                }, new Em3dTriangleMesh(verts, tris), lines);
            }
        }

        // ── the air box: its six faces (hidden until asked for) and its twelve edges ─────────
        if (options.DrawAirBox)
        {
            foreach (var (face, kind, corners) in Faces(box))
            {
                var (r, g, bl) = kind switch
                {
                    Em3dBoundaryKind.Pec       => ((byte)150, (byte)150, (byte)158),
                    Em3dBoundaryKind.Absorbing => ((byte)80, (byte)130, (byte)235),
                    Em3dBoundaryKind.Pmc       => ((byte)235, (byte)140, (byte)50),
                    _                          => ((byte)160, (byte)80, (byte)210),
                };
                // brief-em3d-49 R-em3d49-3a — the editor's box: an absorbing face is untinted (it is what the space does
                // anyway), a symmetry face hatched, and every face picked last.
                bool editor = options.EditorBoundaries;
                byte alpha = editor && kind == Em3dBoundaryKind.Absorbing ? (byte)0 : FaceAlpha;
                uint hatchInk = Scene3DVertex.Pack(r, g, bl, 255);
                b.Object(new Scene3DObject
                {
                    Id = 0, Name = Em3dAirBox.FaceName(face), Kind = Scene3DKind.Boundary, Boundary = kind,
                    Rgba = Scene3DVertex.Pack(r, g, bl, alpha), Translucent = true, InitiallyVisible = false,
                    PickLast = editor, FaceNames = editor ? [face] : [],
                }, new Em3dTriangleMesh(corners, [new Em3dTriangle(0, 1, 2, "", editor ? 0 : -1), new Em3dTriangle(0, 2, 3, "", editor ? 0 : -1)]),
                   editor && kind == Em3dBoundaryKind.Symmetry ? Hatch(corners).Select(q => (q, hatchInk)) : null,
                   faces: editor);
            }
            uint edge = Scene3DVertex.Pack(ink.R, ink.G, ink.B, 255);
            b.Object(new Scene3DObject { Id = 0, Name = "airbox", Kind = Scene3DKind.Boundary, Rgba = edge },
                     null, BoxEdges(box).Select(q => (q, edge)));
        }

        cache?.EndBuild();
        return b.Finish(generation, origin, problem, notes);
    }

    /// <summary>A sheet is one surface, face 0.</summary>
    public static readonly IReadOnlyList<string> SheetFaceNames = ["surface"];

    /// <summary>Opacity of the parent drawn around a pushed-in child.</summary>
    public const byte ContextAlpha = 70;

    /// <summary>brief-em3d-48 R-em3d48-4a — a colour pulled halfway to the background's grey and made translucent: the
    /// parent around a pushed-in child reads as surroundings, not as something to edit.</summary>
    private static uint Dimmed(uint rgba, bool dark)
    {
        byte g = dark ? (byte)70 : (byte)190;
        byte Mix(uint c) => (byte)((c + g) / 2);
        return Scene3DVertex.Pack(Mix(rgba & 0xFF), Mix((rgba >> 8) & 0xFF), Mix((rgba >> 16) & 0xFF), ContextAlpha);
    }

    /// <summary>
    /// brief-em3d-48 R-em3d48-3a — follows the runs of one pass (solids, or sheets) as the builder walks them: the
    /// first element of a run is the PROTOTYPE, tessellated as any object is; each later element's k-th object, when it
    /// is named as the prototype's k-th is, becomes an element object that draws the prototype's triangles under its own
    /// offset. Anything that does not line up is drawn as an ordinary object — slower, never wrong.
    /// </summary>
    private sealed class RunTracker(Accumulator acc)
    {
        private object? _run;
        private int _protoElement, _element = -1, _k;
        private (double X, double Y, double Z) _protoAt;
        private readonly List<(string Local, int Index)> _proto = [];
        private bool _contiguous;
        private int _group = -1;

        public void Break() { _run = null; _proto.Clear(); _group = -1; }

        /// <summary>An object the builder has just added as an ordinary object: it starts a run, or extends its prototype.</summary>
        public void Prototype(Scene3DInstancing p, string name)
        {
            int index = acc.LastIndex;
            if (!Equals(_run, p.Run) || p.Element != _protoElement || _element != _protoElement)
            {
                _run = p.Run;
                _protoElement = _element = p.Element;
                _protoAt = (p.Tx, p.Ty, p.Tz);
                _proto.Clear();
                _contiguous = true;
                _group = -1;
            }
            if (_proto.Count > 0 && _proto[^1].Index + 1 != index) _contiguous = false;
            _proto.Add((p.LocalName, index));
        }

        /// <summary>True when the object is a later element's and was deferred as an element object.</summary>
        public bool Element(Scene3DInstancing p, string name)
        {
            if (!Equals(_run, p.Run) || !_contiguous || _proto.Count == 0 || p.Element == _protoElement) return false;
            if (p.Element != _element) { _element = p.Element; _k = 0; }
            if (_k >= _proto.Count || _proto[_k].Local != p.LocalName) return false;
            if (_group < 0) _group = acc.NewGroup(_proto[0].Index, _proto.Count);
            acc.Defer(_group, _element, _proto[_k].Index, name, p.Tx - _protoAt.X, p.Ty - _protoAt.Y, p.Tz - _protoAt.Z);
            _k++;
            return true;
        }
    }

    private static Scene3DKind KindOf(Em3dSolid s, IReadOnlyDictionary<string, Em3dObjectOrigin> origins)
    {
        if (origins.TryGetValue(s.Name, out var o))
            return o.Kind switch
            {
                Em3dObjectKind.Dielectric => Scene3DKind.Dielectric,
                Em3dObjectKind.Air        => Scene3DKind.Air,
                Em3dObjectKind.Body       => Scene3DKind.Body,
                Em3dObjectKind.Via        => s.Role == Em3dRole.Air ? Scene3DKind.Air : Scene3DKind.Via,
                Em3dObjectKind.Wire       => Scene3DKind.Wire,
                _                         => Scene3DKind.Conductor,
            };
        return s.Role switch
        {
            Em3dRole.Air        => Scene3DKind.Air,
            Em3dRole.Dielectric => Scene3DKind.Dielectric,
            _                   => s.Primitive is Em3dSweep or Em3dSphere or Em3dTruncatedSphere ? Scene3DKind.Wire : Scene3DKind.Conductor,
        };
    }

    /// <summary>The dielectric that encloses the most volume — the substrate a user looks through.
    /// Ties go to the first in problem order.</summary>
    public static string? OutermostDielectric(Em3dProblem problem) => OutermostDielectric(problem, null);

    private static string? OutermostDielectric(Em3dProblem problem, Func<string, bool>? skip)
    {
        string? best = null;
        double vol = -1;
        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Dielectric && skip?.Invoke(s.Name) != true))
        {
            double v = Em3dSizeEstimate.Volume(s.Primitive);
            if (v > vol) { vol = v; best = s.Name; }
        }
        return best;
    }

    // ── geometry of ports and the box ────────────────────────────────────────────────────────

    /// <summary>3D editor bugs round 1 — four lines along a wireframe cylinder's side, a quarter turn apart: its feature
    /// edges are only the two rims, which alone read as two circles, not a solid.</summary>
    private static IEnumerable<Point3> CylinderGenerators(Em3dCylinder c)
    {
        var a = new Vector3((float)(c.AxisEnd.X - c.AxisStart.X), (float)(c.AxisEnd.Y - c.AxisStart.Y), (float)(c.AxisEnd.Z - c.AxisStart.Z));
        if (a.LengthSquared() == 0) yield break;
        a = Vector3.Normalize(a);
        var u = Vector3.Normalize(Vector3.Cross(a, MathF.Abs(a.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        var w = Vector3.Cross(a, u);
        foreach (var d in new[] { u, w, -u, -w })
        {
            double dx = d.X * c.Radius, dy = d.Y * c.Radius, dz = d.Z * c.Radius;
            yield return new Point3(c.AxisStart.X + dx, c.AxisStart.Y + dy, c.AxisStart.Z + dz);
            yield return new Point3(c.AxisEnd.X + dx, c.AxisEnd.Y + dy, c.AxisEnd.Z + dz);
        }
    }

    private static (List<Point3>, List<Em3dTriangle>) PortSheet(Em3dPort p)
    {
        var (a, c) = (p.Min, p.Max);
        var verts = new List<Point3>();
        var tris = new List<Em3dTriangle>();
        if (p.Annulus is { } an)
        {
            double cx = (a.X + c.X) / 2, cy = (a.Y + c.Y) / 2, z = a.Z;
            for (int k = 0; k < AnnulusSegments; k++)
            {
                double t = 2 * Math.PI * k / AnnulusSegments;
                verts.Add(new Point3(cx + an.InnerRadiusM * Math.Cos(t), cy + an.InnerRadiusM * Math.Sin(t), z));
                verts.Add(new Point3(cx + an.OuterRadiusM * Math.Cos(t), cy + an.OuterRadiusM * Math.Sin(t), z));
            }
            for (int k = 0; k < AnnulusSegments; k++)
            {
                int i0 = 2 * k, o0 = 2 * k + 1, i1 = 2 * ((k + 1) % AnnulusSegments), o1 = i1 + 1;
                tris.Add(new Em3dTriangle(i0, o0, o1, p.Name));
                tris.Add(new Em3dTriangle(i0, o1, i1, p.Name));
            }
            return (verts, tris);
        }
        if (a.X == c.X)      verts.AddRange([new(a.X, a.Y, a.Z), new(a.X, c.Y, a.Z), new(a.X, c.Y, c.Z), new(a.X, a.Y, c.Z)]);
        else if (a.Y == c.Y) verts.AddRange([new(a.X, a.Y, a.Z), new(c.X, a.Y, a.Z), new(c.X, a.Y, c.Z), new(a.X, a.Y, c.Z)]);
        else                 verts.AddRange([new(a.X, a.Y, a.Z), new(c.X, a.Y, a.Z), new(c.X, c.Y, a.Z), new(a.X, c.Y, a.Z)]);
        tris.Add(new Em3dTriangle(0, 1, 2, p.Name));
        tris.Add(new Em3dTriangle(0, 2, 3, p.Name));
        return (verts, tris);
    }

    /// <summary>The arrow's segments, as pairs of points: shaft then two barbs. A wave port's arrow is
    /// its voltage path; a lumped port's runs along its direction through the sheet's centre.</summary>
    private static List<Point3> PortArrow(Em3dPort p)
    {
        Point3 from, to;
        if (p.Kind == Em3dPortKind.Wave && p.VoltagePath is { } vp) (from, to) = (vp.From, vp.To);
        else
        {
            var c = new Point3((p.Min.X + p.Max.X) / 2, (p.Min.Y + p.Max.Y) / 2, (p.Min.Z + p.Max.Z) / 2);
            double side = Math.Max(p.Max.X - p.Min.X, Math.Max(p.Max.Y - p.Min.Y, p.Max.Z - p.Min.Z));
            if (p.Annulus is { } an) side = 2 * an.OuterRadiusM;
            double h = side * ArrowFraction / 2;
            var d = p.Direction;
            from = new Point3(c.X - d.X * h, c.Y - d.Y * h, c.Z - d.Z * h);
            to = new Point3(c.X + d.X * h, c.Y + d.Y * h, c.Z + d.Z * h);
        }
        var dir = new Vector3((float)(to.X - from.X), (float)(to.Y - from.Y), (float)(to.Z - from.Z));
        float len = dir.Length();
        if (len <= 0) return [];
        dir /= len;
        var side3 = Vector3.Cross(dir, MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
        side3 = Vector3.Normalize(side3);
        double head = len * 0.25;
        Point3 Barb(float s) => new(to.X - dir.X * head + side3.X * head * 0.5 * s,
                                    to.Y - dir.Y * head + side3.Y * head * 0.5 * s,
                                    to.Z - dir.Z * head + side3.Z * head * 0.5 * s);
        return [from, to, to, Barb(1), to, Barb(-1)];
    }

    private static IEnumerable<(string Face, Em3dBoundaryKind Kind, Point3[] Corners)> Faces(Em3dAirBox b)
    {
        var (lo, hi) = (b.Min, b.Max);
        yield return ("xmin", b.Faces.XMin, [new(lo.X, lo.Y, lo.Z), new(lo.X, lo.Y, hi.Z), new(lo.X, hi.Y, hi.Z), new(lo.X, hi.Y, lo.Z)]);
        yield return ("xmax", b.Faces.XMax, [new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, lo.Y, hi.Z)]);
        yield return ("ymin", b.Faces.YMin, [new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, lo.Y, hi.Z), new(lo.X, lo.Y, hi.Z)]);
        yield return ("ymax", b.Faces.YMax, [new(lo.X, hi.Y, lo.Z), new(lo.X, hi.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, hi.Y, lo.Z)]);
        yield return ("zmin", b.Faces.ZMin, [new(lo.X, lo.Y, lo.Z), new(lo.X, hi.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, lo.Y, lo.Z)]);
        yield return ("zmax", b.Faces.ZMax, [new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z)]);
    }

    /// <summary>brief-em3d-49 — the name prefix of a face boundary's tint object; the air-box toggle does not hide these.</summary>
    public const string FaceTintPrefix = "boundary:";

    /// <summary>Opacity of a face boundary's tint.</summary>
    public const byte BoundaryTintAlpha = 110;

    /// <summary>Hatch lines across a rectangle (corners in order): a symmetry face, which states neither wall.</summary>
    private static List<Point3> Hatch(Point3[] c)
    {
        var lines = new List<Point3>();
        const int n = 12;
        for (int k = 1; k < 2 * n; k++)
        {
            double t = (double)k / n;
            // Diagonals of slope 1 in the face's own (s, u) frame: from edge 0→1 or 1→2 to edge 0→3 or 3→2.
            Point3 On(Point3 a, Point3 b, double f) => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);
            var p = t <= 1 ? On(c[0], c[1], t) : On(c[1], c[2], t - 1);
            var q = t <= 1 ? On(c[0], c[3], t) : On(c[3], c[2], t - 1);
            lines.Add(p);
            lines.Add(q);
        }
        return lines;
    }

    private static List<Point3> BoxEdges(Em3dAirBox b)
    {
        var (lo, hi) = (b.Min, b.Max);
        Point3 C(int k) => new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z);
        var e = new List<Point3>(24);
        for (int k = 0; k < 8; k++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((k & bit) == 0) { e.Add(C(k)); e.Add(C(k | bit)); }
        return e;
    }

    // ── accumulation ─────────────────────────────────────────────────────────────────────────

    /// <summary>The face a triangle of an unnamed-face primitive (a sweep, a sphere) carries: one face, and
    /// no feature edges drawn for it.</summary>
    public const int FaceUnknown = 0xFFFE;

    private sealed class Accumulator(Func<Point3, Vector3> local)
    {
        private readonly List<Scene3DObject> _objects = [];
        private readonly List<Scene3DVertex> _verts = [];
        private readonly List<uint[]> _objIndices = [];
        private readonly List<Scene3DVertex> _lines = [];
        private readonly List<Scene3DLineBatch> _lineBatches = [];
        private readonly List<(uint Id, List<Scene3DVertex> Lines)> _edges = [];
        private readonly List<Scene3DFeatureRef> _features = [];
        private readonly List<(int ProtoIndex, int Count)> _groups = [];
        private readonly List<(int Group, int Element, int Proto, string Name, double Dx, double Dy, double Dz)> _deferred = [];

        /// <summary>The object-list index of the object added last.</summary>
        public int LastIndex => _objects.Count - 1;

        public int NewGroup(int protoIndex, int count)
        {
            _groups.Add((protoIndex, count));
            return _groups.Count - 1;
        }

        /// <summary>An element object, made in <see cref="Finish"/> after every object that owns geometry.</summary>
        public void Defer(int group, int element, int proto, string name, double dx, double dy, double dz)
            => _deferred.Add((group, element, proto, name, dx, dy, dz));

        /// <summary><paramref name="faces"/>: tag each vertex with its triangle's face (un-welding a vertex
        /// shared by two faces) and collect the feature edges. <paramref name="sheet"/>: the whole mesh is
        /// face 0.</summary>
        /// <paramref name="wireEdges"/>: also draw the feature edges, always, in that colour — a wireframe object.
        public void Object(Scene3DObject o, Em3dTriangleMesh? mesh, IEnumerable<(Point3 P, uint Rgba)>? lines = null,
                           bool faces = false, bool sheet = false, Scene3DFeatureRef features = default, uint? wireEdges = null)
        {
            _features.Add(features);
            uint id = (uint)(_objects.Count + 1);
            var obj = new Scene3DObject
            {
                Id = id, Name = o.Name, Kind = o.Kind, Material = o.Material, MaterialValues = o.MaterialValues,
                MaterialSlot = o.MaterialSlot, Rgba = o.Rgba, Translucent = o.Translucent,
                InitiallyVisible = o.InitiallyVisible, PortNumber = o.PortNumber, Boundary = o.Boundary,
                FaceNames = o.FaceNames, CapCentres = o.CapCentres, Context = o.Context, PickLast = o.PickLast,
                Wireframe = o.Wireframe,
            };
            var min = new Vector3(float.MaxValue);
            List<Scene3DVertex>? featureEdges = null;
            var max = new Vector3(float.MinValue);
            uint[] idx = [];
            obj.FirstVertex = _verts.Count;
            if (mesh is not null && !faces)
            {
                int first = _verts.Count;
                foreach (var p in mesh.Vertices)
                {
                    var q = local(p);
                    min = Vector3.Min(min, q); max = Vector3.Max(max, q);
                    _verts.Add(new Scene3DVertex(q.X, q.Y, q.Z, id, o.Rgba));
                }
                idx = new uint[mesh.Triangles.Count * 3];
                int w = 0;
                foreach (var t in mesh.Triangles)
                {
                    idx[w++] = (uint)(first + t.A);
                    idx[w++] = (uint)(first + t.B);
                    idx[w++] = (uint)(first + t.C);
                }
            }
            else if (mesh is not null)
            {
                // One scene vertex per (mesh vertex, face): a vertex's face must be its triangle's.
                var at = new Dictionary<(int V, int F), uint>();
                var q = new Vector3[mesh.Vertices.Count];
                for (int k = 0; k < q.Length; k++) { q[k] = local(mesh.Vertices[k]); min = Vector3.Min(min, q[k]); max = Vector3.Max(max, q[k]); }
                uint V(int v, int f)
                {
                    if (at.TryGetValue((v, f), out uint i)) return i;
                    i = (uint)_verts.Count;
                    _verts.Add(new Scene3DVertex(q[v].X, q[v].Y, q[v].Z, id, o.Rgba, (uint)f));
                    at[(v, f)] = i;
                    return i;
                }
                idx = new uint[mesh.Triangles.Count * 3];
                int w = 0;
                // Each undirected edge's faces, in first-seen order, for the feature edges. Keyed by the corners WELDED BY
                // POSITION, as Scene3DFeatureTable welds them (3D editor bugs round 2): a polyhedron's tessellation repeats
                // each corner once per face, so by index no two faces ever shared an edge and a polyhedron had no edges at
                // all — a material-less one, drawn as its edges only, vanished the moment a box became one.
                var weld = new Dictionary<Point3, int>();
                var welded = new int[mesh.Vertices.Count];
                for (int k = 0; k < welded.Length; k++)
                {
                    if (!weld.TryGetValue(mesh.Vertices[k], out int wk)) weld[mesh.Vertices[k]] = wk = k;
                    welded[k] = wk;
                }
                var edgeFaces = new Dictionary<(int, int), (int F0, int F1, int Count)>();
                void Edge(int a, int c, int f)
                {
                    a = welded[a]; c = welded[c];
                    var key = a < c ? (a, c) : (c, a);
                    edgeFaces[key] = edgeFaces.TryGetValue(key, out var e) ? (e.F0, e.Count == 1 ? f : e.F1, e.Count + 1) : (f, -1, 1);
                }
                foreach (var t in mesh.Triangles)
                {
                    int f = sheet ? 0 : t.Face;
                    if (f < 0) f = FaceUnknown;
                    idx[w++] = V(t.A, f);
                    idx[w++] = V(t.B, f);
                    idx[w++] = V(t.C, f);
                    Edge(t.A, t.B, f); Edge(t.B, t.C, f); Edge(t.C, t.A, f);
                }
                var edges = new List<Scene3DVertex>();
                foreach (var ((a, c), (f0, f1, count)) in edgeFaces)
                {
                    if (f0 == FaceUnknown) continue;                            // a sweep or a sphere: no named faces
                    bool feature = count == 1 ? sheet : f0 != f1;               // a sheet's rim; where two faces meet
                    if (!feature) continue;
                    uint packed = (uint)(f0 & 0xFFFF) | ((uint)((count == 1 ? 0xFFFF : f1) & 0xFFFF) << 16);
                    edges.Add(new Scene3DVertex(q[a].X, q[a].Y, q[a].Z, id, o.Rgba, packed));
                    edges.Add(new Scene3DVertex(q[c].X, q[c].Y, q[c].Z, id, o.Rgba, packed));
                }
                if (edges.Count > 0) _edges.Add((id, edges));
                featureEdges = edges;
            }
            obj.VertexCount = _verts.Count - obj.FirstVertex;
            if (lines is not null || (wireEdges is not null && featureEdges is { Count: > 0 }))
            {
                int first = _lines.Count;
                foreach (var (p, rgba) in lines ?? [])
                {
                    var q = local(p);
                    min = Vector3.Min(min, q); max = Vector3.Max(max, q);
                    _lines.Add(new Scene3DVertex(q.X, q.Y, q.Z, id, rgba));
                }
                if (wireEdges is uint wr && featureEdges is not null)
                    foreach (var e in featureEdges) _lines.Add(new Scene3DVertex(e.X, e.Y, e.Z, id, wr));
                if (_lines.Count > first) _lineBatches.Add(new Scene3DLineBatch(id, first, _lines.Count - first));
            }
            if (min.X > max.X) { min = max = Vector3.Zero; }
            obj.Min = min; obj.Max = max;
            _objects.Add(obj);
            _objIndices.Add(idx);
        }

        public Scene3DModel Finish(long generation, (double, double, double) origin, Em3dProblem problem, IReadOnlyList<string>? notes)
        {
            var indices = new uint[_objIndices.Sum(i => i.Length)];
            var batches = new List<Scene3DBatch>();
            int at = 0;
            foreach (bool translucent in new[] { false, true })
                for (int k = 0; k < _objects.Count; k++)
                {
                    var o = _objects[k];
                    if (o.Translucent != translucent || _objIndices[k].Length == 0) continue;
                    _objIndices[k].CopyTo(indices, at);
                    batches.Add(new Scene3DBatch(o.Id, o.MaterialSlot, at, _objIndices[k].Length, translucent));
                    at += _objIndices[k].Length;
                }

            var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
            var cmin = new Vector3(float.MaxValue); var cmax = new Vector3(float.MinValue);
            foreach (var o in _objects)
            {
                bmin = Vector3.Min(bmin, o.Min); bmax = Vector3.Max(bmax, o.Max);
                if (o.Kind is Scene3DKind.Air or Scene3DKind.Boundary) continue;
                if (o.Kind == Scene3DKind.Dielectric && !o.InitiallyVisible) continue;
                cmin = Vector3.Min(cmin, o.Min); cmax = Vector3.Max(cmax, o.Max);
            }
            // brief-em3d-48 — the unit cube an element over the triangle budget is drawn as, after every ordinary line.
            Scene3DLineBatch unitBox = default;
            if (_deferred.Count > 0)
            {
                int first = _lines.Count;
                uint grey = Scene3DVertex.Pack(150, 150, 160, 255);
                for (int k = 0; k < 8; k++)
                    for (int bit = 1; bit <= 4; bit <<= 1)
                        if ((k & bit) == 0)
                        {
                            int j = k | bit;
                            _lines.Add(new Scene3DVertex(k & 1, (k >> 1) & 1, (k >> 2) & 1, 0, grey));
                            _lines.Add(new Scene3DVertex(j & 1, (j >> 1) & 1, (j >> 2) & 1, 0, grey));
                        }
                unitBox = new Scene3DLineBatch(0, first, _lines.Count - first);
            }

            // The feature edges follow every ordinary line, so LineBatches' offsets are what they were.
            var edgeBatches = new List<Scene3DLineBatch>(_edges.Count);
            var edgeOf = new Dictionary<uint, int>();
            foreach (var (id, lines) in _edges)
            {
                edgeOf[id] = edgeBatches.Count;
                edgeBatches.Add(new Scene3DLineBatch(id, _lines.Count, lines.Count));
                _lines.AddRange(lines);
            }

            // brief-em3d-48 — the prototypes, then every element's objects and batches, AFTER everything that owns bytes.
            int ownedObjects = _objects.Count, ownedBatches = batches.Count, ownedEdges = edgeBatches.Count;
            var batchOf = new Dictionary<uint, int>();
            for (int k = 0; k < batches.Count; k++) batchOf[batches[k].ObjectId] = k;
            var groups = new List<Scene3DInstanceGroup>();
            foreach (var (protoIndex, count) in _groups)
            {
                uint firstId = (uint)(protoIndex + 1);
                int opaqueFirst = -1, opaqueCount = 0, tris = 0;
                var gmin = new Vector3(float.MaxValue); var gmax = new Vector3(float.MinValue);
                for (int k = 0; k < count; k++)
                {
                    var o = _objects[protoIndex + k];
                    gmin = Vector3.Min(gmin, o.Min); gmax = Vector3.Max(gmax, o.Max);
                    if (!batchOf.TryGetValue(o.Id, out int bi)) continue;
                    var pb = batches[bi];
                    tris += pb.IndexCount / 3;
                    if (pb.Translucent) continue;
                    if (opaqueFirst == -1) { opaqueFirst = pb.FirstIndex; opaqueCount = pb.IndexCount; }
                    else if (opaqueFirst >= 0 && opaqueFirst + opaqueCount == pb.FirstIndex) opaqueCount += pb.IndexCount;
                    else opaqueFirst = -2;
                }
                if (opaqueFirst < 0) opaqueCount = opaqueFirst == -1 ? 0 : -1;
                groups.Add(new Scene3DInstanceGroup(firstId, count, Math.Max(opaqueFirst, 0), opaqueCount, tris, gmin, gmax));
            }
            var elements = new List<Scene3DElement>();
            int at2 = 0;
            while (at2 < _deferred.Count)
            {
                var (group, element, _, _, dx, dy, dz) = _deferred[at2];
                int end = at2;
                while (end < _deferred.Count && _deferred[end].Group == group && _deferred[end].Element == element) end++;
                var offset = new Vector3((float)dx, (float)dy, (float)dz);
                uint firstId = (uint)(_objects.Count + 1);
                int firstBatch = batches.Count, index = elements.Count;
                for (int k = at2; k < end; k++)
                {
                    var d = _deferred[k];
                    var proto = _objects[d.Proto];
                    uint id = (uint)(_objects.Count + 1);
                    var obj = new Scene3DObject
                    {
                        Id = id, Name = d.Name, Kind = proto.Kind, Material = proto.Material, MaterialValues = proto.MaterialValues,
                        MaterialSlot = proto.MaterialSlot, Rgba = proto.Rgba, Translucent = proto.Translucent,
                        InitiallyVisible = proto.InitiallyVisible, FaceNames = proto.FaceNames, Context = proto.Context,
                        Wireframe = proto.Wireframe,
                        CapCentres = proto.CapCentres?.Select(c => new Point3(c.X + d.Dx, c.Y + d.Dy, c.Z + d.Dz)).ToArray(),
                        Element = index, Prototype = proto.Id,
                        FirstVertex = proto.FirstVertex, VertexCount = proto.VertexCount,
                        Min = proto.Min + offset, Max = proto.Max + offset,
                    };
                    _objects.Add(obj);
                    var f = _features[d.Proto];
                    _features.Add(f.Table is null ? f : new Scene3DFeatureRef(f.Table, f.Dx + d.Dx, f.Dy + d.Dy, f.Dz + d.Dz, true));
                    if (batchOf.TryGetValue(proto.Id, out int bi))
                        batches.Add(batches[bi] with { ObjectId = id, Element = index, Offset = offset });
                    if (edgeOf.TryGetValue(proto.Id, out int ei))
                        edgeBatches.Add(edgeBatches[ei] with { ObjectId = id, Element = index, Offset = offset });
                    bmin = Vector3.Min(bmin, obj.Min); bmax = Vector3.Max(bmax, obj.Max);
                    if (obj.Kind is not (Scene3DKind.Air or Scene3DKind.Boundary) && !(obj.Kind == Scene3DKind.Dielectric && !obj.InitiallyVisible))
                    { cmin = Vector3.Min(cmin, obj.Min); cmax = Vector3.Max(cmax, obj.Max); }
                }
                uint protoFirst = groups[group].FirstId;
                elements.Add(new Scene3DElement(group, firstId, end - at2, firstBatch, batches.Count - firstBatch, offset, firstId - protoFirst));
                at2 = end;
            }

            if (bmin.X > bmax.X) { bmin = new Vector3(-1e-3f); bmax = new Vector3(1e-3f); }
            if (cmin.X > cmax.X) { cmin = bmin; cmax = bmax; }

            return new Scene3DModel
            {
                Generation = generation, Origin = origin,
                Vertices = [.. _verts], Indices = indices, LineVertices = [.. _lines],
                Objects = [.. _objects], Batches = [.. batches], LineBatches = [.. _lineBatches],
                EdgeBatches = [.. edgeBatches],
                Features = [.. _features],
                Groups = [.. groups], Elements = [.. elements],
                GeometryObjectCount = ownedObjects, GeometryBatchCount = ownedBatches, GeometryEdgeBatchCount = ownedEdges,
                UnitBox = unitBox,
                BoundsMin = bmin, BoundsMax = bmax, ContentMin = cmin, ContentMax = cmax,
                Problem = problem, Notes = notes ?? [],
            };
        }
    }
}
