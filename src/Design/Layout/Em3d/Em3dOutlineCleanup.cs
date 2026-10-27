// brief-dfr28-3 — Robust meshing (EmSetup.RobustMeshing): a layout outline's points that lie within a fraction of a
// micrometre of the line through their neighbours are removed before a 3D run meshes it.
//
// WHY. Imported artwork hands the mesher features far below anything it can resolve: arcs flattened into ~7 µm segments
// that end in a 1.4 µm stub where they meet a straight edge, 0.1 µm and 0.4 µm edges left where draws were merged. On the
// TP149 board three consecutive points of such an arc, all in the board's top plane, became the corners of a FLAT
// tetrahedron (Gmsh's boundary recovery, which its optimiser cannot undo with all four nodes on the boundary); its NaN
// reached every unknown and EMerge wrote S = −I. Removing every point whose removal moves the outline by under 0.5 µm
// took 42 of the board's 3,379 points, its shortest edge went from 0.1 µm to 5 µm, and the flat element was gone.
//
// THE GUARANTEE. A point is removed only when EVERY original point the merged segment now stands for lies within the
// tolerance of it, so no part of an outline moves by more than the tolerance however many neighbours go; Stats.MaxMoveM is
// that distance measured on the result, not an estimate. A point of a corner is never near its neighbours' line, so a
// rectangle, a trace's end (where a port sits) and a cut along the board edge keep every point they have.

namespace CircuitRF.Design.Layout.Em3d;

public static class Em3dOutlineCleanup
{
    /// <summary>The largest move, metres: well under any process's etch tolerance and any element a mesher makes.</summary>
    public const double MaxToleranceM = 0.5e-6;

    /// <summary>The tolerance as a fraction of the conductor's own thickness, so a thin-film metal (1–3 µm) moves by tens of
    /// nanometres at most and copper (17–70 µm) by the <see cref="MaxToleranceM"/> cap.</summary>
    public const double ThicknessFraction = 1.0 / 50;

    /// <summary>The tolerance for a conductor <paramref name="thicknessM"/> thick.</summary>
    public static double Tolerance(double thicknessM) => Math.Min(MaxToleranceM, Math.Max(0, thicknessM) * ThicknessFraction);

    /// <summary>What a run's cleanup did, over every outline it saw.</summary>
    public sealed class Stats
    {
        public int Points { get; set; }
        public int Removed { get; set; }
        public double MaxMoveM { get; set; }
    }

    /// <summary><paramref name="ring"/> with its removable points removed (the same list when none is), counted into
    /// <paramref name="stats"/>. A ring keeps at least three points.</summary>
    public static IReadOnlyList<T> Clean<T>(IReadOnlyList<T> ring, Func<T, (double X, double Y)> xy, double tolerance, Stats stats)
    {
        int n = ring.Count;
        stats.Points += n;
        if (n <= 3 || !(tolerance > 0)) return ring;
        var p = new (double X, double Y)[n];
        for (int i = 0; i < n; i++) p[i] = xy(ring[i]);
        int[] next = new int[n], prev = new int[n];
        for (int i = 0; i < n; i++) { next[i] = (i + 1) % n; prev[i] = (i + n - 1) % n; }
        var alive = new bool[n];
        Array.Fill(alive, true);
        int left = n;

        // The farthest of the original points strictly between a and c (ring order) from the segment a–c.
        double Covered(int a, int c)
        {
            double worst = 0;
            for (int k = (a + 1) % n; k != c; k = (k + 1) % n)
                worst = Math.Max(worst, Distance(p[k], p[a], p[c]));
            return worst;
        }

        var touched = new bool[n];
        bool changed = true;
        while (changed && left > 3)
        {
            changed = false;
            Array.Clear(touched);
            for (int b = 0; b < n && left > 3; b++)
            {
                if (!alive[b] || touched[b]) continue;
                int a = prev[b], c = next[b];
                if (Covered(a, c) >= tolerance) continue;
                alive[b] = false;
                next[a] = c; prev[c] = a;
                touched[a] = touched[c] = true;
                left--;
                changed = true;
            }
        }
        if (left == n) return ring;

        var kept = new List<T>(left);
        for (int i = 0; i < n; i++)
            if (alive[i])
            {
                kept.Add(ring[i]);
                stats.MaxMoveM = Math.Max(stats.MaxMoveM, Covered(i, next[i]));
            }
        stats.Removed += n - left;
        return kept;
    }

    private static double Distance((double X, double Y) q, (double X, double Y) a, (double X, double Y) c)
    {
        double ux = c.X - a.X, uy = c.Y - a.Y, l2 = ux * ux + uy * uy;
        double t = l2 > 0 ? Math.Clamp(((q.X - a.X) * ux + (q.Y - a.Y) * uy) / l2, 0, 1) : 0;
        double dx = q.X - (a.X + t * ux), dy = q.Y - (a.Y + t * uy);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
