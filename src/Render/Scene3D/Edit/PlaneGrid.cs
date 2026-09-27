// brief-em3d-45 R-em3d45-2 — the drawing plane's grid: its spacing, and the uniforms its shader reads.
//
// A SHADER OVER THE VIEWPORT (R-em3d45-2c; made infinite by 3D editor bugs round 1). The grid is not a vertex buffer
// of lines: the vertex shader covers the viewport with one quad made from six vertex indices, and the fragment shader
// casts the fragment's ray at the plane and finds the lines from where it lands. So an orbit, a pan or a zoom changes
// a few uniforms and uploads nothing — brief 28 gate 3's counter holds with the grid on (gate 7). It used to be a quad
// on the plane of the view's half-diagonal around the focus, faded out towards its rim, and drawn through the camera's
// projection — whose near and far planes bracket the SCENE, so the plane beyond the scene was cut off as well: the
// grid read as a patch. The fragment now writes its own depth, clamped, so the plane runs to the horizon.
//
// THE SPACING ADAPTS in the display unit's own steps — 1, 2, 5 × 10ⁿ of a µm, a mil, … — chosen so that one
// minor cell is at least MinPixels on screen at the view's FOCUS (where the view's line of sight meets the
// plane). A 1-2-5 step is at most 2.5× the one before, so the spacing stays in [8, 20] px there: well inside
// the brief's 8–40 px band. Major lines every 10 minor lines, every 5 in mil and inch — the layout grid's own
// spacing for those units. Away from the focus the shader decimates by the same factor: a fragment draws the
// finest level of (minor × major-every^k) whose cells span a few pixels THERE, so a perspective view's distant
// plane shows ever coarser lines and fades out only where even those would crowd — the far edge of the view.
//
// SUBTLE (R-em3d45-2a/-2b). Thin lines at low alpha, faded with obliqueness, so an edge-on plane is not a grey
// slab; the axis lines through the world origin in the axis indicator's colours. The pass runs after the opaque
// objects with depth test on and depth write off.
//
// THE GRID IS NOT THE SNAP (R-em3d45-2d): the snap uses the document's SnapDbu, which need not be the drawn
// minor spacing; the editor's status line shows both when they differ.

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>What a pane's drawing grid shows — set by the editor; null on a pane with no drawing plane.</summary>
public sealed class DrawingGridSettings
{
    public bool Visible = true;
    public DrawingPlane Plane = DrawingPlane.Default;
    public int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    public LayoutUnit Unit = LayoutUnit.Um;
    /// <summary>True on a dark background: light lines.</summary>
    public bool Dark = true;
}

/// <summary>One frame's grid: the minor spacing (DBU), major every n minor lines, and the minor cell's size on
/// screen at the focus (pixels).</summary>
public readonly record struct PlaneGridSpacing(long MinorDbu, int MajorEvery, double MinorPixels);

public static class PlaneGrid
{
    /// <summary>A minor cell is at least this many pixels at the focus.</summary>
    public const double MinPixels = 8;

    /// <summary>The brief's upper bound of the band (R-em3d45-2a).</summary>
    public const double MaxPixels = 40;

    /// <summary>Floats in the grid's uniform block: fourteen vec4s.</summary>
    public const int Floats = 56;

    /// <summary>The coarse phase is taken modulo this many minor cells: every level up to major-every⁶ is aligned
    /// to the world's own multiples of its spacing.</summary>
    public const int CoarseLevels = 6;

    /// <summary>Major lines every 10 minor lines, every 5 in mil and inch (the layout grid's spacing).</summary>
    public static int MajorEvery(LayoutUnit unit) => unit is LayoutUnit.Mil or LayoutUnit.Inch ? 5 : 10;

    /// <summary>The spacing for a view whose focus is <paramref name="metresPerPixel"/> metres a pixel.</summary>
    public static PlaneGridSpacing Spacing(double metresPerPixel, LayoutUnit unit, int dbuPerMicron)
    {
        int major = MajorEvery(unit);
        if (!(metresPerPixel > 0) || double.IsInfinity(metresPerPixel) || dbuPerMicron <= 0) return new PlaneGridSpacing(0, major, 0);
        double mPerDbu = C3dLowering.Metres(1, dbuPerMicron);
        double unitMetres = LayoutUnits.ToDbu(1m, unit, dbuPerMicron) * mPerDbu;
        double units = LayoutGridMath.CeilingNiceStep(MinPixels * metresPerPixel / unitMetres);
        long minor = 1;
        if (units > 0 && units < 1e15)
            minor = Math.Max(1, LayoutUnits.ToDbu((decimal)units, unit, dbuPerMicron));
        return new PlaneGridSpacing(minor, major, minor * mPerDbu / metresPerPixel);
    }

    /// <summary>
    /// The view's focus on the plane, scene-local: where the line of sight through the camera's target meets it,
    /// or — seen edge-on, or with the plane behind the camera — the target dropped onto it.
    /// </summary>
    public static Vector3 Focus(Scene3DModel scene, in Camera3D cam, DrawingPlane plane, int dbuPerMicron)
    {
        var n = DrawingPlane.UnitNormal(plane.Normal);
        var o = scene.ToLocal(0, 0, 0);
        float w = (float)C3dLowering.Metres(plane.OffsetDbu, dbuPerMicron) + Vector3.Dot(o, n);
        var t = cam.Target;
        var f = cam.Forward;
        float fn = Vector3.Dot(f, n);
        float tn = Vector3.Dot(t, n);
        if (MathF.Abs(fn) > (float)DrawingPlane.EdgeOnCosine)
        {
            float s = (w - tn) / fn;
            var hit = t + f * s;
            // A hit far beyond the scene (a grazing look) or behind the eye is no focus.
            if (s > -cam.Distance && (hit - t).Length() <= 50 * MathF.Max(cam.Distance, 1e-12f)) return hit;
        }
        return t + n * (w - tn);
    }

    /// <summary>World metres a pixel at <paramref name="focus"/> (scene-local) for a <paramref name="height"/>-pixel view.</summary>
    public static double MetresPerPixel(in Camera3D cam, Vector3 focus, float height)
    {
        if (cam.Projection == Projection3D.Orthographic) return cam.WorldPerPixel(height);
        float depth = MathF.Max(cam.ViewDepth(focus), 1e-6f * MathF.Max(cam.Distance, 1e-12f));
        float fov = cam.FovY > 0 && cam.FovY < MathF.PI ? cam.FovY : Camera3D.DefaultFovY;
        return 2.0 * depth * Math.Tan(fov * 0.5) / Math.Max(1f, height);
    }

    /// <summary>The spacing this view draws at: what the frame plan writes, and what the status line reports.</summary>
    public static PlaneGridSpacing SpacingFor(Scene3DModel scene, in Camera3D cam, DrawingGridSettings g, float height)
    {
        var focus = Focus(scene, cam, g.Plane, g.DbuPerMicron);
        return Spacing(MetresPerPixel(cam, focus, height), g.Unit, g.DbuPerMicron);
    }

    // Axis colours: the axis indicator's (Viewer3DOverlay), so the lines through the origin read as its arms.
    private static readonly Vector3[] AxisRgb = [new(220 / 255f, 60 / 255f, 60 / 255f), new(60 / 255f, 170 / 255f, 70 / 255f), new(60 / 255f, 110 / 255f, 230 / 255f)];

    /// <summary>
    /// The grid's uniform block (<see cref="Floats"/> floats), the WGSL <c>U</c>'s <c>g*</c> members:
    /// <c>gu</c>/<c>gv</c> the plane's unit axes and each one's fine phase, <c>gn</c> its normal and local offset,
    /// <c>gq</c> the view ray's origin (w 1 in perspective), <c>gs</c> (minor metres, major every, 0, on),
    /// <c>gcol</c> the line colour and alpha, <c>gax</c> where the world origin's lines are (local u, v) and the coarse
    /// phases, <c>gcu</c>/<c>gcv</c> the colours of the lines along u and along v, then the ray per clip position
    /// (<c>gox</c>, <c>goy</c>, <c>gd</c>, <c>gdx</c>, <c>gdy</c>). <paramref name="flipY"/> as the view-projection was
    /// written. Returns the spacing.
    /// </summary>
    public static PlaneGridSpacing Fill(Span<float> u, Scene3DModel scene, in Camera3D cam, DrawingGridSettings g, float width, float height,
                                        bool flipY = false)
    {
        u[..Floats].Clear();
        var plane = g.Plane;
        var focus = Focus(scene, cam, plane, g.DbuPerMicron);
        double mpp = MetresPerPixel(cam, focus, height);
        var s = Spacing(mpp, g.Unit, g.DbuPerMicron);
        if (s.MinorDbu <= 0) return s;
        var (ua, va) = DrawingPlane.AxesOf(plane.Plane);
        var un = DrawingPlane.UnitNormal(ua);
        var vn = DrawingPlane.UnitNormal(va);
        var nn = DrawingPlane.UnitNormal(plane.Normal);
        double mPerDbu = C3dLowering.Metres(1, g.DbuPerMicron);
        double minor = s.MinorDbu * mPerDbu;
        double majorLen = minor * s.MajorEvery;
        double coarseLen = minor * Math.Pow(s.MajorEvery, CoarseLevels);
        double Origin(C3dAxis a) => a switch { C3dAxis.X => scene.Origin.X, C3dAxis.Y => scene.Origin.Y, _ => scene.Origin.Z };
        // The phase: local + phase is the world coordinate less a whole number of cells, so a line falls on every
        // multiple of its spacing — computed here in doubles, so the shader's floats never hold a large world
        // coordinate. The fine phase (whole MAJOR cells) serves the minor and major lines exactly; the coarse one
        // (whole major-every⁶ minor cells) the coarser levels, whose cells dwarf its rounding.
        static double Mod(double o, double len) { double r = o % len; return r < 0 ? r + len : r; }
        u[0] = un.X; u[1] = un.Y; u[2] = un.Z; u[3] = (float)Mod(Origin(ua), majorLen);
        u[4] = vn.X; u[5] = vn.Y; u[6] = vn.Z; u[7] = (float)Mod(Origin(va), majorLen);
        u[8] = nn.X; u[9] = nn.Y; u[10] = nn.Z; u[11] = (float)(plane.OffsetDbu * mPerDbu - Origin(plane.Normal));
        u[16] = (float)minor; u[17] = s.MajorEvery; u[18] = 0; u[19] = 1;
        var line = g.Dark ? new Vector3(0.86f, 0.88f, 0.92f) : new Vector3(0.18f, 0.2f, 0.24f);
        // 3D editor bugs round 2 — on a light background the grid at 0.24 (and its axis lines at 0.6) could not be seen: dark
        // lines on white need about twice the alpha light ones on near-black do.
        u[20] = line.X; u[21] = line.Y; u[22] = line.Z; u[23] = g.Dark ? 0.2f : 0.5f;
        u[24] = (float)-Origin(ua); u[25] = (float)-Origin(va);
        u[26] = (float)Mod(Origin(ua), coarseLen); u[27] = (float)Mod(Origin(va), coarseLen);
        var cu = AxisRgb[(int)ua];
        var cv = AxisRgb[(int)va];
        float axisAlpha = g.Dark ? 0.6f : 0.85f;
        u[28] = cu.X; u[29] = cu.Y; u[30] = cu.Z; u[31] = axisAlpha;
        u[32] = cv.X; u[33] = cv.Y; u[34] = cv.Z; u[35] = axisAlpha;
        WriteRay(u, cam, width, height, flipY);
        return s;
    }

    /// <summary>
    /// The view's ray at clip position (x, y) — the position the view-projection maps a point to, so y is negated when
    /// that matrix was written flipped: origin <c>gq + x·gox + y·goy</c>, direction <c>gd + x·gdx + y·gdy</c>. In
    /// perspective every ray leaves the eye (gq.w = 1: the plane behind it is not drawn); in orthographic every ray is
    /// the view direction, from a point on the view plane. The same frustum <see cref="Camera3D.ProjectionMatrix"/> makes.
    /// </summary>
    public static void WriteRay(Span<float> u, in Camera3D cam, float width, float height, bool flipY)
    {
        float aspect = MathF.Max(1, width) / MathF.Max(1, height);
        float fov = cam.FovY > 0 && cam.FovY < MathF.PI ? cam.FovY : Camera3D.DefaultFovY;
        float sy = flipY ? -1 : 1;
        var eye = cam.Eye;
        var (r, up, f) = (cam.Right, cam.Up, cam.Forward);
        Vector3 ox = default, oy = default, dx = default, dy = default;
        float persp;
        if (cam.Projection == Projection3D.Orthographic)
        {
            float h = MathF.Max(1e-12f, cam.Distance * MathF.Tan(fov * 0.5f));
            ox = r * (h * aspect);
            oy = up * (h * sy);
            persp = 0;
        }
        else
        {
            float ty = MathF.Tan(fov * 0.5f);
            dx = r * (ty * aspect);
            dy = up * (ty * sy);
            persp = 1;
        }
        static void Put(Span<float> u, int at, Vector3 v, float w) { u[at] = v.X; u[at + 1] = v.Y; u[at + 2] = v.Z; u[at + 3] = w; }
        Put(u, 12, eye, persp);
        Put(u, 36, ox, 0);
        Put(u, 40, oy, 0);
        Put(u, 44, f, 0);
        Put(u, 48, dx, 0);
        Put(u, 52, dy, 0);
    }

    /// <summary>
    /// Where the grid shader's ray for clip position (<paramref name="x"/>, <paramref name="y"/>) meets the plane,
    /// scene-local — the same arithmetic as <c>fs_grid</c>, on the CPU, for the gates. Null when it misses (parallel,
    /// or behind a perspective eye).
    /// </summary>
    public static Vector3? RayHit(ReadOnlySpan<float> u, float x, float y)
    {
        static Vector3 V(ReadOnlySpan<float> u, int at) => new(u[at], u[at + 1], u[at + 2]);
        var ro = V(u, 12) + V(u, 36) * x + V(u, 40) * y;
        var rd = V(u, 44) + V(u, 48) * x + V(u, 52) * y;
        var n = V(u, 8);
        float dn = Vector3.Dot(rd, n);
        if (MathF.Abs(dn) < 1e-4f * rd.Length()) return null;
        float t = (u[11] - Vector3.Dot(ro, n)) / dn;
        if (u[15] > 0.5f && t <= 0) return null;
        return ro + rd * t;
    }
}
