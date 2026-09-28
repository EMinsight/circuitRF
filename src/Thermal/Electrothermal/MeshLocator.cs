// brief-em3d-77 R-em3d77-3e — which tetrahedron a point lies in, and the element's shape functions there: how a wire element,
// which the mesh does not conform to, reads the solid around it and hands its heat to it. A uniform grid of the elements'
// corner boxes, as ThermalField's probes use; a point within a small tolerance outside (on a face, a rounding) still counts.

namespace CircuitRF.Thermal.Electrothermal;

/// <summary>A point's element and that element's shape-function values at it.</summary>
public readonly record struct MeshPoint(int Element, int Region, double[] N);

/// <summary>Point location in a <see cref="ThermalMesh"/>.</summary>
public sealed class MeshLocator
{
    private readonly ThermalMesh _m;
    private readonly double _x0, _y0, _z0, _hx, _hy, _hz;
    private readonly int _nx, _ny, _nz;
    private readonly int[] _start, _items;

    public MeshLocator(ThermalMesh mesh)
    {
        _m = mesh;
        int nn = mesh.NodesPerTet, ne = mesh.TetCount;
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        for (int i = 0; i < mesh.NodeCount; i++)
        {
            x0 = Math.Min(x0, mesh.Nodes[3 * i]); y0 = Math.Min(y0, mesh.Nodes[3 * i + 1]); z0 = Math.Min(z0, mesh.Nodes[3 * i + 2]);
            x1 = Math.Max(x1, mesh.Nodes[3 * i]); y1 = Math.Max(y1, mesh.Nodes[3 * i + 1]); z1 = Math.Max(z1, mesh.Nodes[3 * i + 2]);
        }
        if (ne == 0) { x0 = y0 = z0 = 0; x1 = y1 = z1 = 1; }
        int side = Math.Max(1, (int)Math.Round(Math.Cbrt(Math.Max(ne, 1))));
        _nx = _ny = _nz = Math.Min(side, 200);
        double pad = 1e-6 * Math.Max(Math.Max(x1 - x0, y1 - y0), Math.Max(z1 - z0, 1e-12));
        _x0 = x0 - pad; _y0 = y0 - pad; _z0 = z0 - pad;
        _hx = (x1 - x0 + 2 * pad) / _nx; _hy = (y1 - y0 + 2 * pad) / _ny; _hz = (z1 - z0 + 2 * pad) / _nz;
        Tolerance = 1e-9 * Math.Max(Math.Max(x1 - x0, y1 - y0), Math.Max(z1 - z0, 1e-12));
        var lists = new List<int>[_nx * _ny * _nz];
        for (int e = 0; e < ne; e++)
        {
            double ex0 = double.PositiveInfinity, ey0 = ex0, ez0 = ex0, ex1 = double.NegativeInfinity, ey1 = ex1, ez1 = ex1;
            for (int k = 0; k < 4; k++)
            {
                int v = mesh.Tets[nn * e + k];
                ex0 = Math.Min(ex0, mesh.Nodes[3 * v]); ey0 = Math.Min(ey0, mesh.Nodes[3 * v + 1]); ez0 = Math.Min(ez0, mesh.Nodes[3 * v + 2]);
                ex1 = Math.Max(ex1, mesh.Nodes[3 * v]); ey1 = Math.Max(ey1, mesh.Nodes[3 * v + 1]); ez1 = Math.Max(ez1, mesh.Nodes[3 * v + 2]);
            }
            for (int i = Cell(ex0 - Tolerance, _x0, _hx, _nx); i <= Cell(ex1 + Tolerance, _x0, _hx, _nx); i++)
                for (int j = Cell(ey0 - Tolerance, _y0, _hy, _ny); j <= Cell(ey1 + Tolerance, _y0, _hy, _ny); j++)
                    for (int k = Cell(ez0 - Tolerance, _z0, _hz, _nz); k <= Cell(ez1 + Tolerance, _z0, _hz, _nz); k++)
                        (lists[(i * _ny + j) * _nz + k] ??= []).Add(e);
        }
        _start = new int[lists.Length + 1];
        for (int c = 0; c < lists.Length; c++) _start[c + 1] = _start[c] + (lists[c]?.Count ?? 0);
        _items = new int[_start[^1]];
        for (int c = 0; c < lists.Length; c++) lists[c]?.CopyTo(_items, _start[c]);
    }

    /// <summary>How far outside an element a point may lie and still be in it, metres (a billionth of the mesh's size).</summary>
    public double Tolerance { get; }

    /// <summary>The element <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/> lies in, lowest index first; null in
    /// none. <paramref name="accept"/> may pass over an element (one of a region the caller does not want).</summary>
    public MeshPoint? Locate(double x, double y, double z, Func<int, bool>? accept = null)
    {
        if (x < _x0 || y < _y0 || z < _z0 || x > _x0 + _hx * _nx || y > _y0 + _hy * _ny || z > _z0 + _hz * _nz) return null;
        int c = (Cell(x, _x0, _hx, _nx) * _ny + Cell(y, _y0, _hy, _ny)) * _nz + Cell(z, _z0, _hz, _nz);
        int best = -1;
        double bestOut = double.PositiveInfinity, b1 = 0, b2 = 0, b3 = 0;
        for (int p = _start[c]; p < _start[c + 1]; p++)
        {
            int e = _items[p];
            if (accept is not null && !accept(e)) continue;
            if (!Barycentric(e, x, y, z, out double l1, out double l2, out double l3, out double outside)) continue;
            if (outside < bestOut) { (best, bestOut, b1, b2, b3) = (e, outside, l1, l2, l3); if (outside <= 0) break; }
        }
        if (best < 0) return null;
        var n = new double[_m.NodesPerTet];
        Span<double> dx = stackalloc double[10], dy = stackalloc double[10], dz = stackalloc double[10];
        ReferenceElement.Tet(_m.Order, b1, b2, b3, n, dx, dy, dz);
        return new MeshPoint(best, _m.TetRegion[best], n);
    }

    /// <summary>The volume of element <paramref name="e"/> (its corners), m³.</summary>
    public double Volume(int e)
    {
        int nn = _m.NodesPerTet;
        var nd = _m.Nodes;
        int a = _m.Tets[nn * e], b = _m.Tets[nn * e + 1], c = _m.Tets[nn * e + 2], d = _m.Tets[nn * e + 3];
        double ax = nd[3 * a], ay = nd[3 * a + 1], az = nd[3 * a + 2];
        double m00 = nd[3 * b] - ax, m10 = nd[3 * b + 1] - ay, m20 = nd[3 * b + 2] - az;
        double m01 = nd[3 * c] - ax, m11 = nd[3 * c + 1] - ay, m21 = nd[3 * c + 2] - az;
        double m02 = nd[3 * d] - ax, m12 = nd[3 * d + 1] - ay, m22 = nd[3 * d + 2] - az;
        return Math.Abs(m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20)) / 6;
    }

    /// <summary>The edge of a regular tetrahedron of element <paramref name="e"/>'s volume, metres: its size.</summary>
    public double Size(int e) => Math.Cbrt(6 * Math.Sqrt(2) * Volume(e));

    private static int Cell(double v, double v0, double h, int n) => Math.Clamp((int)Math.Floor((v - v0) / h), 0, n - 1);

    /// <summary>The point's barycentric L1..L3 in element <paramref name="e"/>'s corners, and how far outside it lies in
    /// barycentric terms (≤ 0 inside); false beyond the tolerance.</summary>
    private bool Barycentric(int e, double x, double y, double z, out double l1, out double l2, out double l3, out double outside)
    {
        int nn = _m.NodesPerTet;
        var nd = _m.Nodes;
        int a = _m.Tets[nn * e], b = _m.Tets[nn * e + 1], c = _m.Tets[nn * e + 2], d = _m.Tets[nn * e + 3];
        double ax = nd[3 * a], ay = nd[3 * a + 1], az = nd[3 * a + 2];
        double m00 = nd[3 * b] - ax, m10 = nd[3 * b + 1] - ay, m20 = nd[3 * b + 2] - az;
        double m01 = nd[3 * c] - ax, m11 = nd[3 * c + 1] - ay, m21 = nd[3 * c + 2] - az;
        double m02 = nd[3 * d] - ax, m12 = nd[3 * d + 1] - ay, m22 = nd[3 * d + 2] - az;
        double det = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
        l1 = l2 = l3 = 0;
        outside = double.PositiveInfinity;
        if (det == 0) return false;
        double px = x - ax, py = y - ay, pz = z - az;
        l1 = (px * (m11 * m22 - m12 * m21) - m01 * (py * m22 - m12 * pz) + m02 * (py * m21 - m11 * pz)) / det;
        l2 = (m00 * (py * m22 - m12 * pz) - px * (m10 * m22 - m12 * m20) + m02 * (m10 * pz - py * m20)) / det;
        l3 = (m00 * (m11 * pz - py * m21) - m01 * (m10 * pz - py * m20) + px * (m10 * m21 - m11 * m20)) / det;
        outside = -Math.Min(Math.Min(l1, l2), Math.Min(l3, 1 - l1 - l2 - l3));
        // a barycentric tolerance scaled by the element: a point a Tolerance outside a face counts
        double scale = Math.Cbrt(Math.Abs(det));
        return outside <= Tolerance / Math.Max(scale, 1e-300) + 1e-12;
    }
}
