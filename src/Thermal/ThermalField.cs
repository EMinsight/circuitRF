// brief-em3d-74 R-em3d74-3 — what the solver gives back, read over places: the temperature at a point (by the element's
// own interpolation, not the nearest node), a region's or a surface's max, min and volume- or area-weighted average, a SPOT
// (a disk on a face — the triangles clipped to the disk, as an IR microscope averages), and T sampled along a line.
//
// A point is located through a uniform grid of the tetrahedra's corner boxes, and read with the barycentric coordinates of
// the element's corners — exact for a straight-sided element, the linear approximation of the reference point for a curved
// one (the interpolation itself is still the element's full P2 one). Max and min are nodal.

namespace CircuitRF.Thermal;

/// <summary>A temperature statistic over a place.</summary>
public readonly record struct ThermalStats(double MaxC, double MinC, double AvgC, double Measure);

/// <summary>A solved field over its mesh.</summary>
public sealed class ThermalField(ThermalMesh mesh, double[] temperature)
{
    public ThermalMesh Mesh { get; } = mesh;
    public double[] Temperature { get; } = temperature;

    private Grid? _grid;

    /// <summary>The temperature at (x, y, z), metres; null outside every element.</summary>
    public double? At(double x, double y, double z)
    {
        _grid ??= new Grid(Mesh);
        int nn = Mesh.NodesPerTet;
        Span<double> n = stackalloc double[10], dx = stackalloc double[10], dy = stackalloc double[10], dz = stackalloc double[10];
        foreach (int e in _grid.Candidates(x, y, z))
        {
            if (!Barycentric(e, x, y, z, out double l1, out double l2, out double l3)) continue;
            ReferenceElement.Tet(Mesh.Order, l1, l2, l3, n, dx, dy, dz);
            double t = 0;
            for (int i = 0; i < nn; i++) t += n[i] * Temperature[Mesh.Tets[nn * e + i]];
            return t;
        }
        return null;
    }

    /// <summary>T at <paramref name="count"/> evenly spaced points from <paramref name="from"/> to <paramref name="to"/>
    /// (both ends included); NaN where a point lies outside the mesh.</summary>
    public double[] Line((double X, double Y, double Z) from, (double X, double Y, double Z) to, int count)
    {
        var t = new double[count];
        for (int i = 0; i < count; i++)
        {
            double s = count == 1 ? 0 : (double)i / (count - 1);
            t[i] = At(from.X + s * (to.X - from.X), from.Y + s * (to.Y - from.Y), from.Z + s * (to.Z - from.Z)) ?? double.NaN;
        }
        return t;
    }

    /// <summary>Over the tetrahedra of <paramref name="region"/>: nodal max and min, volume-weighted average, volume.</summary>
    public ThermalStats? Region(int region)
    {
        int nn = Mesh.NodesPerTet;
        var shapes = TetShapes.For(Mesh.Order);
        Span<double> xyz = stackalloc double[30], gx = stackalloc double[10], gy = stackalloc double[10], gz = stackalloc double[10];
        double max = double.NegativeInfinity, min = double.PositiveInfinity, sum = 0, vol = 0;
        for (int e = 0; e < Mesh.TetCount; e++)
        {
            if (Mesh.TetRegion[e] != region) continue;
            Gather(Mesh.Tets, nn * e, nn, xyz);
            for (int i = 0; i < nn; i++)
            {
                double ti = Temperature[Mesh.Tets[nn * e + i]];
                max = Math.Max(max, ti); min = Math.Min(min, ti);
            }
            for (int q = 0; q < ReferenceElement.TetQuadraturePoints; q++)
            {
                if (!ReferenceElement.TetGradients(xyz, nn, shapes.Dx(q), shapes.Dy(q), shapes.Dz(q), gx, gy, gz, out double det)) continue;
                double w = ReferenceElement.TetW[q] * det, tq = 0;
                var nq = shapes.N(q);
                for (int i = 0; i < nn; i++) tq += nq[i] * Temperature[Mesh.Tets[nn * e + i]];
                sum += w * tq; vol += w;
            }
        }
        return vol > 0 ? new ThermalStats(max, min, sum / vol, vol) : null;
    }

    /// <summary>Over the triangles of <paramref name="tags"/>: nodal max and min, area-weighted average, area.</summary>
    public ThermalStats? Surface(IReadOnlySet<int> tags)
    {
        int nf = Mesh.NodesPerTriangle;
        var shapes = TriShapes.For(Mesh.Order);
        Span<double> xyz = stackalloc double[18];
        double max = double.NegativeInfinity, min = double.PositiveInfinity, sum = 0, area = 0;
        for (int t = 0; t < Mesh.TriangleCount; t++)
        {
            if (!tags.Contains(Mesh.TriangleTag[t])) continue;
            Gather(Mesh.Triangles, nf * t, nf, xyz);
            for (int i = 0; i < nf; i++)
            {
                double ti = Temperature[Mesh.Triangles[nf * t + i]];
                max = Math.Max(max, ti); min = Math.Min(min, ti);
            }
            for (int q = 0; q < ReferenceElement.TriQuadraturePoints; q++)
            {
                double w = ReferenceElement.TriW[q] * ReferenceElement.TriJacobian(xyz, nf, shapes.Du(q), shapes.Dv(q)), tq = 0;
                var nq = shapes.N(q);
                for (int i = 0; i < nf; i++) tq += nq[i] * Temperature[Mesh.Triangles[nf * t + i]];
                sum += w * tq; area += w;
            }
        }
        return area > 0 ? new ThermalStats(max, min, sum / area, area) : null;
    }

    /// <summary>
    /// Over the part of <paramref name="tags"/>' triangles inside the disk of <paramref name="radius"/> about
    /// <paramref name="centre"/>: each triangle is subdivided until its pieces are an eighth of the radius across, a piece
    /// wholly inside is integrated and a piece on the rim counts by its centroid. Max and min are over the quadrature
    /// points inside. Null when the disk covers none of them.
    /// </summary>
    public ThermalStats? Spot(IReadOnlySet<int> tags, (double X, double Y, double Z) centre, double radius)
    {
        int nf = Mesh.NodesPerTriangle;
        var xyz = new double[18];
        var nodeT = new double[6];
        double max = double.NegativeInfinity, min = double.PositiveInfinity, sum = 0, area = 0;
        var shapes = TriShapes.For(Mesh.Order);
        var n = new double[6];
        var du = new double[6];
        var dv = new double[6];
        for (int t = 0; t < Mesh.TriangleCount; t++)
        {
            if (!tags.Contains(Mesh.TriangleTag[t])) continue;
            Gather(Mesh.Triangles, nf * t, nf, xyz);
            for (int i = 0; i < nf; i++) nodeT[i] = Temperature[Mesh.Triangles[nf * t + i]];
            double edge = 0, near = double.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                edge = Math.Max(edge, Dist(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2], xyz[3 * j], xyz[3 * j + 1], xyz[3 * j + 2]));
                near = Math.Min(near, Dist(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2], centre.X, centre.Y, centre.Z));
            }
            if (near > radius + edge) continue;
            int depth = Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(edge / (radius / 8), 1))), 0, 10);
            Sub((0, 0), (1, 0), (0, 1), depth);
        }
        return area > 0 ? new ThermalStats(max, min, sum / area, area) : null;

        (double X, double Y, double Z) Pos((double U, double V) p)
        {
            double l0 = 1 - p.U - p.V;
            return (l0 * xyz[0] + p.U * xyz[3] + p.V * xyz[6], l0 * xyz[1] + p.U * xyz[4] + p.V * xyz[7], l0 * xyz[2] + p.U * xyz[5] + p.V * xyz[8]);
        }
        bool In((double U, double V) p)
        {
            var q = Pos(p);
            return Dist(q.X, q.Y, q.Z, centre.X, centre.Y, centre.Z) <= radius;
        }
        void Integrate((double U, double V) a, (double U, double V) b, (double U, double V) c)
        {
            double sub = Math.Abs((b.U - a.U) * (c.V - a.V) - (c.U - a.U) * (b.V - a.V));
            for (int q = 0; q < ReferenceElement.TriQuadraturePoints; q++)
            {
                double s = ReferenceElement.TriU[q], r = ReferenceElement.TriV[q];
                double u = a.U + s * (b.U - a.U) + r * (c.U - a.U), v = a.V + s * (b.V - a.V) + r * (c.V - a.V);
                ReferenceElement.Tri(Mesh.Order, u, v, n, du, dv);
                double w = ReferenceElement.TriW[q] * sub * ReferenceElement.TriJacobian(xyz, nf, du, dv), tq = 0;
                for (int i = 0; i < nf; i++) tq += n[i] * nodeT[i];
                sum += w * tq; area += w;
                max = Math.Max(max, tq); min = Math.Min(min, tq);
            }
        }
        void Sub((double U, double V) a, (double U, double V) b, (double U, double V) c, int left)
        {
            bool ia = In(a), ib = In(b), ic = In(c);
            if (ia && ib && ic) { Integrate(a, b, c); return; }
            if (left == 0)
            {
                if (In(((a.U + b.U + c.U) / 3, (a.V + b.V + c.V) / 3))) Integrate(a, b, c);
                return;
            }
            var ab = ((a.U + b.U) / 2, (a.V + b.V) / 2);
            var bc = ((b.U + c.U) / 2, (b.V + c.V) / 2);
            var ca = ((c.U + a.U) / 2, (c.V + a.V) / 2);
            Sub(a, ab, ca, left - 1); Sub(ab, b, bc, left - 1); Sub(ca, bc, c, left - 1); Sub(ab, bc, ca, left - 1);
        }
    }

    private void Gather(int[] conn, int at, int count, Span<double> xyz)
    {
        for (int i = 0; i < count; i++)
        {
            int v = conn[at + i];
            xyz[3 * i] = Mesh.Nodes[3 * v]; xyz[3 * i + 1] = Mesh.Nodes[3 * v + 1]; xyz[3 * i + 2] = Mesh.Nodes[3 * v + 2];
        }
    }

    private static double Dist(double ax, double ay, double az, double bx, double by, double bz)
        => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz));

    /// <summary>The point's barycentric coordinates L1..L3 in element <paramref name="e"/>'s corners; false outside.</summary>
    private bool Barycentric(int e, double x, double y, double z, out double l1, out double l2, out double l3)
    {
        int nn = Mesh.NodesPerTet;
        var nd = Mesh.Nodes;
        int a = Mesh.Tets[nn * e], b = Mesh.Tets[nn * e + 1], c = Mesh.Tets[nn * e + 2], d = Mesh.Tets[nn * e + 3];
        double ax = nd[3 * a], ay = nd[3 * a + 1], az = nd[3 * a + 2];
        double m00 = nd[3 * b] - ax, m10 = nd[3 * b + 1] - ay, m20 = nd[3 * b + 2] - az;
        double m01 = nd[3 * c] - ax, m11 = nd[3 * c + 1] - ay, m21 = nd[3 * c + 2] - az;
        double m02 = nd[3 * d] - ax, m12 = nd[3 * d + 1] - ay, m22 = nd[3 * d + 2] - az;
        double det = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
        l1 = l2 = l3 = 0;
        if (det == 0) return false;
        double px = x - ax, py = y - ay, pz = z - az;
        l1 = (px * (m11 * m22 - m12 * m21) - m01 * (py * m22 - m12 * pz) + m02 * (py * m21 - m11 * pz)) / det;
        l2 = (m00 * (py * m22 - m12 * pz) - px * (m10 * m22 - m12 * m20) + m02 * (m10 * pz - py * m20)) / det;
        l3 = (m00 * (m11 * pz - py * m21) - m01 * (m10 * pz - py * m20) + px * (m10 * m21 - m11 * m20)) / det;
        const double tol = -1e-9;
        return l1 >= tol && l2 >= tol && l3 >= tol && 1 - l1 - l2 - l3 >= tol;
    }

    /// <summary>A uniform grid of the elements' corner boxes.</summary>
    private sealed class Grid
    {
        private readonly double _x0, _y0, _z0, _hx, _hy, _hz;
        private readonly int _nx, _ny, _nz;
        private readonly int[] _start, _items;

        public Grid(ThermalMesh m)
        {
            int nn = m.NodesPerTet, ne = m.TetCount;
            double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
            for (int i = 0; i < m.NodeCount; i++)
            {
                x0 = Math.Min(x0, m.Nodes[3 * i]); y0 = Math.Min(y0, m.Nodes[3 * i + 1]); z0 = Math.Min(z0, m.Nodes[3 * i + 2]);
                x1 = Math.Max(x1, m.Nodes[3 * i]); y1 = Math.Max(y1, m.Nodes[3 * i + 1]); z1 = Math.Max(z1, m.Nodes[3 * i + 2]);
            }
            int side = Math.Max(1, (int)Math.Round(Math.Cbrt(Math.Max(ne, 1))));
            _nx = _ny = _nz = Math.Min(side, 200);
            double pad = 1e-9 * Math.Max(Math.Max(x1 - x0, y1 - y0), Math.Max(z1 - z0, 1e-12));
            _x0 = x0 - pad; _y0 = y0 - pad; _z0 = z0 - pad;
            _hx = (x1 - x0 + 2 * pad) / _nx; _hy = (y1 - y0 + 2 * pad) / _ny; _hz = (z1 - z0 + 2 * pad) / _nz;
            var lists = new List<int>[_nx * _ny * _nz];
            for (int e = 0; e < ne; e++)
            {
                double ex0 = double.PositiveInfinity, ey0 = ex0, ez0 = ex0, ex1 = double.NegativeInfinity, ey1 = ex1, ez1 = ex1;
                for (int k = 0; k < 4; k++)
                {
                    int v = m.Tets[nn * e + k];
                    ex0 = Math.Min(ex0, m.Nodes[3 * v]); ey0 = Math.Min(ey0, m.Nodes[3 * v + 1]); ez0 = Math.Min(ez0, m.Nodes[3 * v + 2]);
                    ex1 = Math.Max(ex1, m.Nodes[3 * v]); ey1 = Math.Max(ey1, m.Nodes[3 * v + 1]); ez1 = Math.Max(ez1, m.Nodes[3 * v + 2]);
                }
                for (int i = Cell(ex0, _x0, _hx, _nx); i <= Cell(ex1, _x0, _hx, _nx); i++)
                    for (int j = Cell(ey0, _y0, _hy, _ny); j <= Cell(ey1, _y0, _hy, _ny); j++)
                        for (int k = Cell(ez0, _z0, _hz, _nz); k <= Cell(ez1, _z0, _hz, _nz); k++)
                            (lists[(i * _ny + j) * _nz + k] ??= []).Add(e);
            }
            _start = new int[lists.Length + 1];
            for (int c = 0; c < lists.Length; c++) _start[c + 1] = _start[c] + (lists[c]?.Count ?? 0);
            _items = new int[_start[^1]];
            for (int c = 0; c < lists.Length; c++) lists[c]?.CopyTo(_items, _start[c]);
        }

        private static int Cell(double v, double v0, double h, int n) => Math.Clamp((int)Math.Floor((v - v0) / h), 0, n - 1);

        public ReadOnlySpan<int> Candidates(double x, double y, double z)
        {
            if (x < _x0 || y < _y0 || z < _z0 || x > _x0 + _hx * _nx || y > _y0 + _hy * _ny || z > _z0 + _hz * _nz) return [];
            int c = (Cell(x, _x0, _hx, _nx) * _ny + Cell(y, _y0, _hy, _ny)) * _nz + Cell(z, _z0, _hz, _nz);
            return _items.AsSpan(_start[c], _start[c + 1] - _start[c]);
        }
    }
}
