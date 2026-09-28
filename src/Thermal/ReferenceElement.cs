// brief-em3d-74 R-em3d74-2b — the reference tetrahedron and triangle: shape functions and the quadrature rules.
//
// THE RULES, AND WHY THESE. Volume integrals use the 14-POINT rule of degree 5 with positive weights (the classic
// symmetric rule: two four-point orbits and one six-point orbit). A P2 stiffness at constant k is degree 2 on a straight
// element and a volumetric load degree 2, so any degree-2 rule would do there — but the tangent of a k(T) Newton step
// multiplies k(T_h) (degree ≥ 2) into it, and a curved element's Jacobian is not constant, so the margin is spent where it
// is needed. Surface integrals use the 6-point degree-4 rule (Dunavant), exact for a P2 Robin mass ∫hNᵢNⱼ on a flat
// triangle. Both were checked for exactness on every monomial of their degree (relative error ≤ 1e-15).
//
// COORDINATES. Tetrahedron: L0 = 1 − x − y − z, L1 = x, L2 = y, L3 = z. Triangle: L0 = 1 − u − v, L1 = u, L2 = v.
// Corner shape functions Lᵢ(2Lᵢ − 1), mid-edge 4LₐL_b (P2); Lᵢ (P1).

namespace CircuitRF.Thermal;

internal static class ReferenceElement
{
    /// <summary>The volume rule's points (x, y, z) and weights; the weights sum to 1/6, the reference volume.</summary>
    public static readonly double[] TetX, TetY, TetZ, TetW;

    /// <summary>The surface rule's points (u, v) and weights; the weights sum to 1/2, the reference area.</summary>
    public static readonly double[] TriU, TriV, TriW;

    static ReferenceElement()
    {
        var pts = new List<(double[] L, double W)>();
        foreach (var (a, w) in new[] { (0.0927352503108912, 0.01224884051939366), (0.3108859192633006, 0.01878132095300264) })
            for (int k = 0; k < 4; k++)
            {
                var l = new[] { a, a, a, a };
                l[k] = 1 - 3 * a;
                pts.Add((l, w));
            }
        const double b = 0.0455037041256496, w3 = 0.007091003462846911;
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
            {
                var l = new[] { 0.5 - b, 0.5 - b, 0.5 - b, 0.5 - b };
                l[i] = b; l[j] = b;
                pts.Add((l, w3));
            }
        TetX = [.. pts.Select(p => p.L[1])];
        TetY = [.. pts.Select(p => p.L[2])];
        TetZ = [.. pts.Select(p => p.L[3])];
        TetW = [.. pts.Select(p => p.W)];

        var tri = new List<(double[] L, double W)>();
        foreach (var (a, w) in new[] { (0.445948490915965, 0.223381589678011), (0.091576213509771, 0.109951743655322) })
            for (int k = 0; k < 3; k++)
            {
                var l = new[] { a, a, a };
                l[k] = 1 - 2 * a;
                tri.Add((l, w / 2));
            }
        TriU = [.. tri.Select(p => p.L[1])];
        TriV = [.. tri.Select(p => p.L[2])];
        TriW = [.. tri.Select(p => p.W)];
    }

    public static int TetQuadraturePoints => TetW.Length;
    public static int TriQuadraturePoints => TriW.Length;

    /// <summary>The tetrahedron's shape functions at (x, y, z) and their reference derivatives.</summary>
    public static void Tet(int order, double x, double y, double z, Span<double> n, Span<double> dx, Span<double> dy, Span<double> dz)
    {
        double l0 = 1 - x - y - z;
        if (order == 1)
        {
            n[0] = l0; n[1] = x; n[2] = y; n[3] = z;
            dx[0] = -1; dy[0] = -1; dz[0] = -1;
            dx[1] = 1; dy[1] = 0; dz[1] = 0;
            dx[2] = 0; dy[2] = 1; dz[2] = 0;
            dx[3] = 0; dy[3] = 0; dz[3] = 1;
            return;
        }
        Span<double> l = [l0, x, y, z];
        // dL/d(x,y,z) for L0..L3
        ReadOnlySpan<double> gx = [-1, 1, 0, 0], gy = [-1, 0, 1, 0], gz = [-1, 0, 0, 1];
        for (int i = 0; i < 4; i++)
        {
            n[i] = l[i] * (2 * l[i] - 1);
            double s = 4 * l[i] - 1;
            dx[i] = s * gx[i]; dy[i] = s * gy[i]; dz[i] = s * gz[i];
        }
        for (int k = 0; k < 6; k++)
        {
            var (a, b) = ThermalMesh.TetEdges[k];
            n[4 + k] = 4 * l[a] * l[b];
            dx[4 + k] = 4 * (l[b] * gx[a] + l[a] * gx[b]);
            dy[4 + k] = 4 * (l[b] * gy[a] + l[a] * gy[b]);
            dz[4 + k] = 4 * (l[b] * gz[a] + l[a] * gz[b]);
        }
    }

    /// <summary>The triangle's shape functions at (u, v) and their reference derivatives.</summary>
    public static void Tri(int order, double u, double v, Span<double> n, Span<double> du, Span<double> dv)
    {
        double l0 = 1 - u - v;
        if (order == 1)
        {
            n[0] = l0; n[1] = u; n[2] = v;
            du[0] = -1; dv[0] = -1; du[1] = 1; dv[1] = 0; du[2] = 0; dv[2] = 1;
            return;
        }
        Span<double> l = [l0, u, v];
        ReadOnlySpan<double> gu = [-1, 1, 0], gv = [-1, 0, 1];
        for (int i = 0; i < 3; i++)
        {
            n[i] = l[i] * (2 * l[i] - 1);
            double s = 4 * l[i] - 1;
            du[i] = s * gu[i]; dv[i] = s * gv[i];
        }
        for (int k = 0; k < 3; k++)
        {
            var (a, b) = ThermalMesh.TriangleEdges[k];
            n[3 + k] = 4 * l[a] * l[b];
            du[3 + k] = 4 * (l[b] * gu[a] + l[a] * gu[b]);
            dv[3 + k] = 4 * (l[b] * gv[a] + l[a] * gv[b]);
        }
    }

    /// <summary>
    /// The physical gradients of the tetrahedron's shape functions at one reference point, from the element's own nodes
    /// (isoparametric), and |det J|. Returns false for a degenerate or inverted-to-zero element.
    /// </summary>
    public static bool TetGradients(ReadOnlySpan<double> xyz, int nn, ReadOnlySpan<double> dx, ReadOnlySpan<double> dy,
                                    ReadOnlySpan<double> dz, Span<double> gx, Span<double> gy, Span<double> gz, out double detJ)
    {
        // J[r][c] = d x_c / d xi_r
        double j00 = 0, j01 = 0, j02 = 0, j10 = 0, j11 = 0, j12 = 0, j20 = 0, j21 = 0, j22 = 0;
        for (int i = 0; i < nn; i++)
        {
            double X = xyz[3 * i], Y = xyz[3 * i + 1], Z = xyz[3 * i + 2];
            j00 += dx[i] * X; j01 += dx[i] * Y; j02 += dx[i] * Z;
            j10 += dy[i] * X; j11 += dy[i] * Y; j12 += dy[i] * Z;
            j20 += dz[i] * X; j21 += dz[i] * Y; j22 += dz[i] * Z;
        }
        double det = j00 * (j11 * j22 - j12 * j21) - j01 * (j10 * j22 - j12 * j20) + j02 * (j10 * j21 - j11 * j20);
        detJ = Math.Abs(det);
        if (!(detJ > 0) || !double.IsFinite(det)) return false;
        double inv = 1 / det;
        // K = J^-1; grad N = K · dN/dxi
        double k00 = (j11 * j22 - j12 * j21) * inv, k01 = (j02 * j21 - j01 * j22) * inv, k02 = (j01 * j12 - j02 * j11) * inv;
        double k10 = (j12 * j20 - j10 * j22) * inv, k11 = (j00 * j22 - j02 * j20) * inv, k12 = (j02 * j10 - j00 * j12) * inv;
        double k20 = (j10 * j21 - j11 * j20) * inv, k21 = (j01 * j20 - j00 * j21) * inv, k22 = (j00 * j11 - j01 * j10) * inv;
        for (int i = 0; i < nn; i++)
        {
            gx[i] = k00 * dx[i] + k01 * dy[i] + k02 * dz[i];
            gy[i] = k10 * dx[i] + k11 * dy[i] + k12 * dz[i];
            gz[i] = k20 * dx[i] + k21 * dy[i] + k22 * dz[i];
        }
        return true;
    }

    /// <summary>A surface triangle's area element |∂x/∂u × ∂x/∂v| at one reference point (isoparametric).</summary>
    public static double TriJacobian(ReadOnlySpan<double> xyz, int nn, ReadOnlySpan<double> du, ReadOnlySpan<double> dv)
    {
        double ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0;
        for (int i = 0; i < nn; i++)
        {
            ax += du[i] * xyz[3 * i]; ay += du[i] * xyz[3 * i + 1]; az += du[i] * xyz[3 * i + 2];
            bx += dv[i] * xyz[3 * i]; by += dv[i] * xyz[3 * i + 1]; bz += dv[i] * xyz[3 * i + 2];
        }
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return Math.Sqrt(cx * cx + cy * cy + cz * cz);
    }
}
