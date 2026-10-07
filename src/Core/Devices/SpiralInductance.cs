namespace CircuitRF.Core.Devices;

/// <summary>
/// The SPIRAL and OSPIRAL coils as drawn: the centreline the layout generators walk and the path the
/// current takes out of the inner end. Unit-agnostic — the generators call it in database units, the
/// inductance in metres — so the drawn part and the part whose inductance is summed are one walk.
/// </summary>
public static class SpiralWalk
{
    /// <summary>How many sides are drawn: whole quarter turns for a square coil, whole eighths for an
    /// octagonal one.</summary>
    public static int Sides(double turns, bool octagonal)
        => octagonal ? SpiralInductorModel.OctagonalSides(turns) : SpiralInductorModel.Sides(turns);

    /// <summary>How many turns the inner end's escape crosses.</summary>
    public static int Crossings(double turns, bool octagonal)
        => octagonal ? SpiralInductorModel.OctagonalCrossings(turns) : SpiralInductorModel.Crossings(turns);

    /// <summary>
    /// The coil's centreline, inner end first. <paramref name="a"/> is the innermost side's centreline
    /// distance from the centre (<c>Din/2 + W/2</c>), <paramref name="pitch"/> is <c>W + S</c>.
    ///
    /// <para><b>Square</b>: from the corner (−a, −a), side k of length 2a + ⌊k/2⌋·pitch, turning 90°
    /// left each time. <b>Octagonal</b>: side k is the line whose outward normal points 270° + 45°·k at
    /// distance a + k·pitch/8, and a corner is where two consecutive sides cross, so side k and side k+8
    /// are parallel and exactly one pitch apart. It starts at the MIDDLE of the innermost flat, so the
    /// escape crosses each later lap's flat squarely (src/Design/RESOLVED.md, OSPIRAL).</para>
    /// </summary>
    public static List<(double X, double Y)> Centreline(double a, double pitch, int sides, bool octagonal)
    {
        if (!octagonal)
        {
            var pts = new List<(double X, double Y)>(sides + 1) { (-a, -a) };
            (double Dx, double Dy)[] dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];
            for (int k = 0; k < sides; k++)
            {
                double len = 2 * a + (k / 2) * pitch;
                var (x, y) = pts[^1];
                pts.Add((x + dirs[k % 4].Dx * len, y + dirs[k % 4].Dy * len));
            }
            return pts;
        }

        (double X, double Y) Corner(int k)              // where side k-1 crosses side k
        {
            double pa = (270.0 + 45.0 * (k - 1)) * Math.PI / 180, pb = (270.0 + 45.0 * k) * Math.PI / 180;
            double da = a + (k - 1) * pitch / 8.0, db = a + k * pitch / 8.0;
            double den = Math.Sin(pb - pa);
            return ((da * Math.Sin(pb) - db * Math.Sin(pa)) / den, (db * Math.Cos(pa) - da * Math.Cos(pb)) / den);
        }
        var c0 = Corner(0);
        var c1 = Corner(1);
        var oct = new List<(double X, double Y)>(sides + 1) { ((c0.X + c1.X) / 2, -a) };
        for (int k = 1; k <= sides; k++) oct.Add(Corner(k));
        return oct;
    }

    /// <summary>The y of the escape's far post centre: below the lowest crossed turn by a spacing.</summary>
    public static double EscapeEndY(double a, double pitch, double w, double s, int crossings)
        => -a - crossings * pitch - w - s;

    /// <summary>
    /// The whole current path from terminal 1 to terminal 2, as straight segments: the coil from its
    /// outer end inward, the escape on the bridge metal (at <paramref name="bridgeHeight"/> above the coil
    /// plane) from the inner end past every crossed turn, and the landing pad down to terminal 2 — the
    /// path <c>SpiralPCell</c>/<c>OctSpiralPCell</c> draw. The two via posts are bridge-height tall and
    /// are left out.
    /// </summary>
    public static List<PartialInductance.Segment> Path(double turns, double w, double s, double din,
                                                       double bridgeHeight, bool octagonal)
    {
        double a = din / 2 + w / 2, pitch = w + s;
        var pts = Centreline(a, pitch, Sides(turns, octagonal), octagonal);
        var path = new List<PartialInductance.Segment>(pts.Count + 1);
        for (int i = pts.Count - 1; i > 0; i--)
            path.Add(new(pts[i].X, pts[i].Y, pts[i - 1].X, pts[i - 1].Y, 0));
        var (ix, iy) = pts[0];
        double yE = EscapeEndY(a, pitch, w, s, Crossings(turns, octagonal));
        path.Add(new(ix, iy, ix, yE, bridgeHeight));
        path.Add(new(ix, yE, ix, yE - w / 2 - 2 * w, 0));
        return path;
    }
}

/// <summary>
/// The inductance of a path of straight conductors by partial-inductance summation (Greenhouse, "Design
/// of planar rectangular microelectronic inductors", <i>IEEE Trans. PHP</i> 10(2), 1974; Ruehli,
/// "Inductance calculations in a complex integrated circuit environment", <i>IBM J. Res. Dev.</i> 16(5),
/// 1972): every segment's self term, plus every pair's mutual term with the sign of the angle between
/// their currents, minus the mutual to every segment's IMAGE in a ground plane below. SI throughout.
///
/// <para><b>Every mutual is a closed form</b> — Neumann's integral over two straight filaments, which is
/// elementary for any two segments lying in horizontal planes: the parallel-filament form for parallel
/// ones, and for the rest the antiderivative <see cref="Phi"/> (exact for coplanar AND for parallel-plane
/// pairs — the image of a 45° side is the second case). No quadrature, so a large coil is well under a
/// millisecond.</para>
///
/// <para><b>Still an estimate.</b> Conductors are filaments on their centrelines (no current crowding, no
/// skin effect, no geometric-mean-distance correction between close neighbours), the ground is a perfect
/// conductor, and the substrate does not enter (magnetic fields do not see a dielectric). Against the
/// textbook square loop, built from the same terms, it is exact; against a planar EM extraction of the
/// drawn GaAs coil it reads lower, partly because the EM treats the metal as a zero-thickness sheet
/// (src/Design/RESOLVED.md).</para>
/// </summary>
public static class PartialInductance
{
    /// <summary>A straight conductor from (X1, Y1) to (X2, Y2), in the horizontal plane at height Z.
    /// Current flows from the first point to the second.</summary>
    public readonly record struct Segment(double X1, double Y1, double X2, double Y2, double Z)
    {
        public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
    }

    private const double Mu0Over4Pi = 1e-7;

    /// <summary>
    /// The path's inductance, H. <paramref name="w"/>, <paramref name="t"/> are every conductor's width and
    /// thickness; <paramref name="groundDepth"/> is how far below the z = 0 plane a perfect ground plane
    /// lies (≤ 0 or infinite: none). Each segment's image sits at <c>−2·groundDepth − Z</c>, carrying the
    /// reversed current.
    /// </summary>
    public static double OfPath(IReadOnlyList<Segment> path, double w, double t, double groundDepth)
    {
        bool ground = groundDepth > 0 && double.IsFinite(groundDepth);
        double floor = 0.2235 * (w + t);            // a rectangle's geometric mean distance from itself
        double l = 0;
        for (int i = 0; i < path.Count; i++)
        {
            var a = path[i];
            if (a.Length <= 0) continue;
            l += MmicPassiveFormulas.RibbonInductance(a.Length, w, t);
            for (int j = 0; j < path.Count; j++)
            {
                var b = path[j];
                if (b.Length <= 0) continue;
                if (i != j) l += Mutual(a, b, floor);
                if (ground) l -= Mutual(a, b with { Z = -2 * groundDepth - b.Z }, floor);
            }
        }
        return l;
    }

    /// <summary>
    /// The mutual inductance of two straight filaments in horizontal planes, H, signed by the angle
    /// between their currents: Neumann's <c>(µ₀/4π)·cos θ·∬ dl·dl′/r</c>. Perpendicular filaments couple
    /// not at all. <paramref name="minDistance"/> floors the separation of parallel ones, so two lying on
    /// one line are treated as the conductor's own geometric mean distance apart rather than diverging.
    /// </summary>
    public static double Mutual(in Segment a, in Segment b, double minDistance)
    {
        double la = a.Length, lb = b.Length;
        double ux = (a.X2 - a.X1) / la, uy = (a.Y2 - a.Y1) / la;
        double vx = (b.X2 - b.X1) / lb, vy = (b.Y2 - b.Y1) / lb;
        double c = ux * vx + uy * vy;
        if (Math.Abs(c) < 1e-12) return 0;
        double h = b.Z - a.Z;

        if (Math.Abs(c) > 1 - 1e-12)
        {
            // Parallel: (µ₀/4π)·[f(s+m) − f(s) − f(s+m−l) + f(s−l)], f(z) = z·asinh(z/d) − √(z² + d²),
            // with b reversed onto a's direction (and the sign carried) when they are antiparallel.
            double bx1 = b.X1, by1 = b.Y1, sign = 1;
            if (c < 0) { bx1 = b.X2; by1 = b.Y2; sign = -1; }
            double rx = bx1 - a.X1, ry = by1 - a.Y1;
            double s = rx * ux + ry * uy;
            double px = rx - s * ux, py = ry - s * uy;
            double d = Math.Max(Math.Sqrt(px * px + py * py + h * h), minDistance);
            double F(double z) => z * Math.Asinh(z / d) - Math.Sqrt(z * z + d * d);
            return sign * Mu0Over4Pi * (F(s + lb) - F(s) - F(s + lb - la) + F(s - la));
        }

        // Not parallel: the two lines' projections cross at O. x runs along a from O, y along b.
        double det = -ux * vy + uy * vx;
        double qx = b.X1 - a.X1, qy = b.Y1 - a.Y1;
        double x0 = (-qx * vy + qy * vx) / det;     // A1 + x0·u = O
        double y0 = (ux * qy - uy * qx) / det;      // B1 + y0·v = O
        double x1 = -x0, x2 = la - x0, y1 = -y0, y2 = lb - y0;
        return Mu0Over4Pi * c * (Phi(x2, y2, c, h) - Phi(x1, y2, c, h) - Phi(x2, y1, c, h) + Phi(x1, y1, c, h));
    }

    /// <summary>
    /// An antiderivative of <c>1/R</c>, <c>R = √(x² + y² − 2cxy + h²)</c>, in x and y:
    /// <c>x·ln(y − cx + R) + y·ln(x − cy + R) − (h/s)·atan((c·h² + x·y·s²)/(h·s·R))</c> with
    /// <c>s = √(1 − c²)</c>. Exact for filaments in one plane (h = 0) and in parallel planes.
    /// </summary>
    internal static double Phi(double x, double y, double c, double h)
    {
        double s2 = 1 - c * c, s = Math.Sqrt(s2);
        double r = Math.Sqrt(Math.Max(x * x + y * y - 2 * c * x * y + h * h, 0));
        double v = 0;
        if (x != 0) v += x * LogPlusR(y - c * x, x * x * s2 + h * h, r);
        if (y != 0) v += y * LogPlusR(x - c * y, y * y * s2 + h * h, r);
        if (h != 0) v -= h / s * Math.Atan((c * h * h + x * y * s2) / (h * s * r));
        return v;
    }

    /// <summary><c>ln(u + R)</c> where <c>R² = u² + rest</c>, without the cancellation a large negative
    /// <c>u</c> would cause: then <c>u + R = rest/(R − u)</c>.</summary>
    private static double LogPlusR(double u, double rest, double r)
    {
        if (u >= 0) return Math.Log(u + r);
        return rest > 0 ? Math.Log(rest / (r - u)) : 0;     // rest = 0 with u < 0 is the filament's own end: its weight is 0
    }
}
