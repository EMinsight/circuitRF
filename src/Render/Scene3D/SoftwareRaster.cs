// brief-em3d-110 R-em3d110-2a — THE software rasteriser: one triangle's samples, each visited with its barycentric weights and its
// depth. Factored out of Em3dSurfaceField (brief 89's surface pictures), which still draws through it, and used by RealisticPicture
// (the realistic view on the CPU) for its colour pass, its occlusion prepass and its shadow map. One copy: a rule fixed here is fixed
// for both pictures.
//
// A triangle arrives in SAMPLE space: x right and y DOWN in samples, a sample's centre at (i + 0.5, j + 0.5), and z whatever depth the
// caller compares (it is interpolated linearly in screen space, as a GPU interpolates its window depth). The weights are screen-space
// ones; a perspective caller turns them into the attribute weights with Perspective(), as a GPU does with 1/w.
//
// Two fill rules. Inclusive (brief 89's): a sample on an edge belongs to every triangle that edge bounds, within 1e-9 — what
// Em3dSurfaceField has always drawn, byte for byte. TopLeft (the GPU's, D3D/Metal/Vulkan alike): a sample on a shared edge belongs to
// exactly one of the two triangles, so a translucent surface is not blended twice along its own triangles' seams.
//
// Rows are bounded by [rowLo, rowHi), so a caller can raster a band of rows on its own thread: each sample is visited by the one band
// that owns its row, in the same triangle order whatever the thread count, which is what keeps a picture's bytes deterministic.

namespace CircuitRF.Render.Scene3D;

/// <summary>What a rasterised sample is handed to: its index in the <c>sw</c>-wide buffer, its column and row, its screen-space weights
/// (summing to 1) and its interpolated depth.</summary>
public interface IRasterVisitor
{
    void Visit(int index, int i, int j, double w0, double w1, double w2, double z);
}

/// <summary>Which samples on a triangle's edge it owns.</summary>
public enum RasterFill
{
    /// <summary>Every sample within 1e-9 of the triangle (brief 89's rule): a shared edge's samples belong to both triangles.</summary>
    Inclusive,
    /// <summary>The GPU's top-left rule: a sample exactly on an edge belongs to the triangle for which that edge is a top or a left edge.</summary>
    TopLeft,
}

public static class SoftwareRaster
{
    /// <summary>
    /// Visits every sample of a <paramref name="sw"/> × <paramref name="sh"/> buffer, in rows [<paramref name="rowLo"/>,
    /// <paramref name="rowHi"/>), whose centre triangle <paramref name="t"/> (x, y, z for each of three corners, sample space) covers under
    /// <paramref name="fill"/>. A triangle seen edge-on (area at most 1e-12 samples²) covers nothing: its neighbours, facing the viewer, draw.
    /// </summary>
    public static void Triangle<TV>(ReadOnlySpan<double> t, int sw, int sh, int rowLo, int rowHi, RasterFill fill, ref TV visitor)
        where TV : struct, IRasterVisitor
    {
        double x0 = t[0], y0 = t[1], z0 = t[2], x1 = t[3], y1 = t[4], z1 = t[5], x2 = t[6], y2 = t[7], z2 = t[8];
        double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (!(Math.Abs(area) > 1e-12)) return;
        int i0 = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)) - 0.5));
        int i1 = Math.Min(sw - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2)) - 0.5));
        int j0 = Math.Max(Math.Max(0, rowLo), (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)) - 0.5));
        int j1 = Math.Min(Math.Min(sh, rowHi) - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2)) - 0.5));
        if (fill == RasterFill.Inclusive)
        {
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
                    visitor.Visit(j * sw + i, i, j, w0, w1, w2, w0 * z0 + w1 * z1 + w2 * z2);
                }
            }
            return;
        }

        // TopLeft: the same weights, a sample exactly on an edge kept only by the triangle that edge is a top or left edge of
        bool e0 = TopOrLeft(x1, y1, x2, y2, x0, y0), e1 = TopOrLeft(x2, y2, x0, y0, x1, y1), e2 = TopOrLeft(x0, y0, x1, y1, x2, y2);
        for (int j = j0; j <= j1; j++)
        {
            double cy = j + 0.5;
            for (int i = i0; i <= i1; i++)
            {
                double cx = i + 0.5;
                double w0 = ((x1 - cx) * (y2 - cy) - (x2 - cx) * (y1 - cy)) / area;
                if (w0 < 0 || w0 == 0 && !e0) continue;
                double w1 = ((x2 - cx) * (y0 - cy) - (x0 - cx) * (y2 - cy)) / area;
                if (w1 < 0 || w1 == 0 && !e1) continue;
                double w2 = 1 - w0 - w1;
                if (w2 < 0 || w2 == 0 && !e2) continue;
                visitor.Visit(j * sw + i, i, j, w0, w1, w2, w0 * z0 + w1 * z1 + w2 * z2);
            }
        }
    }

    /// <summary>Whether the edge a → b of a triangle whose third corner is c is a TOP edge (horizontal, the triangle below it — y runs
    /// down) or a LEFT edge (the triangle to its right): the edges whose samples the top-left rule keeps.</summary>
    private static bool TopOrLeft(double ax, double ay, double bx, double by, double cx, double cy)
    {
        // the edge's normal, turned toward the third corner: into the triangle
        double nx = -(by - ay), ny = bx - ax;
        if (nx * (cx - ax) + ny * (cy - ay) < 0) { nx = -nx; ny = -ny; }
        return ay == by ? ny > 0 : nx > 0;
    }

    /// <summary>A sample's screen-space weights in triangle <paramref name="t"/> (as <see cref="Triangle"/> computes them).</summary>
    public static (double W0, double W1, double W2) Barycentric(ReadOnlySpan<double> t, double cx, double cy)
    {
        double x0 = t[0], y0 = t[1], x1 = t[3], y1 = t[4], x2 = t[6], y2 = t[7];
        double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        double w0 = ((x1 - cx) * (y2 - cy) - (x2 - cx) * (y1 - cy)) / area;
        double w1 = ((x2 - cx) * (y0 - cy) - (x0 - cx) * (y2 - cy)) / area;
        return (w0, w1, 1 - w0 - w1);
    }

    /// <summary>Screen-space weights turned into ATTRIBUTE weights under a perspective projection whose corners have clip w
    /// <paramref name="cw0"/>, <paramref name="cw1"/>, <paramref name="cw2"/> (each weight over its w, renormalised) — what a GPU does to
    /// every interpolated attribute. With equal w (orthographic) it changes nothing but rounding.</summary>
    public static (double W0, double W1, double W2) Perspective(double w0, double w1, double w2, double cw0, double cw1, double cw2)
    {
        double a = w0 / cw0, b = w1 / cw1, c = w2 / cw2, s = a + b + c;
        return (a / s, b / s, c / s);
    }
}
