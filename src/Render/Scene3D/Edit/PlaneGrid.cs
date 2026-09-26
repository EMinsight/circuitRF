// brief-em3d-45 R-em3d45-2 — the drawing plane's grid: its spacing, and the uniforms its shader reads.
//
// A SHADER OVER ONE QUAD (R-em3d45-2c). The grid is not a vertex buffer of lines: the vertex shader makes a
// quad on the plane from six vertex indices and this block, and the fragment shader finds the lines from the
// fragment's position. So an orbit, a pan or a zoom changes a few uniforms and uploads nothing — brief 28
// gate 3's counter holds with the grid on (gate 7).
//
// THE SPACING ADAPTS in the display unit's own steps — 1, 2, 5 × 10ⁿ of a µm, a mil, … — chosen so that one
// minor cell is at least MinPixels on screen at the view's FOCUS (where the view's line of sight meets the
// plane). A 1-2-5 step is at most 2.5× the one before, so the spacing stays in [8, 20] px there: well inside
// the brief's 8–40 px band. Major lines every 10 minor lines, every 5 in mil and inch — the layout grid's own
// spacing for those units. In perspective a minor cell shrinks with distance, so the shader fades the minor
// lines out where they would crowd closer than a few pixels.
//
// SUBTLE (R-em3d45-2a/-2b). Thin lines at low alpha, faded with distance from the focus and with obliqueness,
// so an edge-on plane is a faint line and not a grey slab; the axis lines through the world origin in the axis
// indicator's colours. The pass runs after the opaque objects with depth test on and depth write off.
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

    /// <summary>Floats in the grid's uniform block: nine vec4s.</summary>
    public const int Floats = 36;

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
    /// <c>gu</c>/<c>gv</c> the plane's unit axes and each one's phase, <c>gn</c> its normal and local offset,
    /// <c>gq</c> the quad's centre and half-size, <c>gs</c> (minor metres, major every, fade radius, on),
    /// <c>gcol</c> the line colour and alpha, <c>gax</c> where the world origin's lines are (local u, v), and
    /// <c>gcu</c>/<c>gcv</c> the colours of the lines along u and along v. Returns the spacing.
    /// </summary>
    public static PlaneGridSpacing Fill(Span<float> u, Scene3DModel scene, in Camera3D cam, DrawingGridSettings g, float width, float height)
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
        // The phase: local + phase is the world coordinate less a whole number of MAJOR cells, so a minor line
        // falls on every multiple of the spacing and a major one on every multiple of ten (or five) of them —
        // computed here in doubles, so the shader's floats never hold a large world coordinate.
        double Phase(C3dAxis a)
        {
            double o = a switch { C3dAxis.X => scene.Origin.X, C3dAxis.Y => scene.Origin.Y, _ => scene.Origin.Z };
            double r = o % majorLen;
            return r < 0 ? r + majorLen : r;
        }
        double Origin(C3dAxis a) => a switch { C3dAxis.X => scene.Origin.X, C3dAxis.Y => scene.Origin.Y, _ => scene.Origin.Z };
        // The fade radius: the view's half-diagonal at the focus, so the grid spans the view and dies away at its edges.
        float radius = (float)(mpp * 0.6 * Math.Sqrt((double)width * width + (double)height * height));
        u[0] = un.X; u[1] = un.Y; u[2] = un.Z; u[3] = (float)Phase(ua);
        u[4] = vn.X; u[5] = vn.Y; u[6] = vn.Z; u[7] = (float)Phase(va);
        u[8] = nn.X; u[9] = nn.Y; u[10] = nn.Z; u[11] = (float)(plane.OffsetDbu * mPerDbu - Origin(plane.Normal));
        u[12] = focus.X; u[13] = focus.Y; u[14] = focus.Z; u[15] = radius;
        u[16] = (float)minor; u[17] = s.MajorEvery; u[18] = radius; u[19] = 1;
        var line = g.Dark ? new Vector3(0.86f, 0.88f, 0.92f) : new Vector3(0.18f, 0.2f, 0.24f);
        u[20] = line.X; u[21] = line.Y; u[22] = line.Z; u[23] = g.Dark ? 0.2f : 0.24f;
        u[24] = (float)-Origin(ua); u[25] = (float)-Origin(va);
        var cu = AxisRgb[(int)ua];
        var cv = AxisRgb[(int)va];
        u[28] = cu.X; u[29] = cu.Y; u[30] = cu.Z; u[31] = 0.6f;
        u[32] = cv.X; u[33] = cv.Y; u[34] = cv.Z; u[35] = 0.6f;
        return s;
    }
}
