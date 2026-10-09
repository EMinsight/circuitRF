// Which part a designator names — brief-artsch-10-silkscreen-ocr.md R-as10-4.
//
// A designator is printed BESIDE its part, and on a dense board it is often nearer a neighbour's body than its own.
// Taking each label's nearest part in turn then gives one part two labels and its neighbour none. So the labels and
// the parts are matched as a whole: the one-to-one assignment that minimises the total distance from each label's
// centre to its part's body box (the Hungarian method), solved separately for each cluster of labels and parts that
// can reach one another. A label farther than three body diagonals from every part names none, and is listed.

namespace CircuitRF.Design.Layout.Recognition.Silkscreen;

/// <summary>A part a designator may name: its body box, DBU.</summary>
public sealed record RefdesCandidate(Bbox Body)
{
    public double Diagonal => Math.Sqrt((double)(Body.MaxX - Body.MinX) * (Body.MaxX - Body.MinX)
                                        + (double)(Body.MaxY - Body.MinY) * (Body.MaxY - Body.MinY));
}

/// <summary>Assigns designators to parts.</summary>
public static class RefdesAssociation
{
    /// <summary>A designator reaches a part within this many of the part's body diagonals.</summary>
    public const double ReachInBodyDiagonals = 3;

    /// <summary>
    /// For each claim, the index of the candidate it names, or −1. One-to-one; the total of the distances from each
    /// claim's point to its candidate's body box is the least possible. A claim reaches a candidate within
    /// <see cref="ReachInBodyDiagonals"/> of the candidate's body diagonals, and within the claim's own
    /// <see cref="PartClaim.ReachDbu"/> where that is positive.
    /// </summary>
    public static int[] Assign(IReadOnlyList<PartClaim> claims, IReadOnlyList<RefdesCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(candidates);
        var result = Enumerable.Repeat(-1, claims.Count).ToArray();

        // Which pairs can reach one another.
        var reach = new List<(int Claim, int Candidate, double Distance)>();
        for (int c = 0; c < claims.Count; c++)
            for (int p = 0; p < candidates.Count; p++)
            {
                double d = Distance(claims[c].X, claims[c].Y, candidates[p].Body);
                double limit = ReachInBodyDiagonals * candidates[p].Diagonal;
                if (claims[c].ReachDbu > 0) limit = Math.Min(limit, claims[c].ReachDbu);
                if (d <= limit) reach.Add((c, p, d));
            }

        // Clusters: claims and candidates joined through reachable pairs. Each is solved on its own.
        int n = claims.Count + candidates.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        foreach (var (c, p, _) in reach) parent[Find(c)] = Find(claims.Count + p);

        foreach (var cluster in reach.GroupBy(r => Find(r.Claim)))
        {
            var rows = cluster.Select(r => r.Claim).Distinct().Order().ToList();
            var cols = cluster.Select(r => r.Candidate).Distinct().Order().ToList();
            if (rows.Count == 1 && cols.Count == 1) { result[rows[0]] = cols[0]; continue; }

            // A pair out of reach costs more than every reachable pair together, so it is taken only when nothing
            // else is left — and then dropped.
            double big = 1 + cluster.Sum(r => r.Distance) * 2;
            var cost = new double[rows.Count, cols.Count];
            for (int i = 0; i < rows.Count; i++)
                for (int j = 0; j < cols.Count; j++) cost[i, j] = big;
            foreach (var (c, p, d) in cluster) cost[rows.IndexOf(c), cols.IndexOf(p)] = d;

            var assigned = Hungarian(cost);
            for (int i = 0; i < rows.Count; i++)
                if (assigned[i] >= 0 && cost[i, assigned[i]] < big) result[rows[i]] = cols[assigned[i]];
        }
        return result;
    }

    /// <summary>The distance from a point to a box: 0 inside it.</summary>
    public static double Distance(long x, long y, Bbox box)
    {
        double dx = Math.Max(0, Math.Max(box.MinX - x, x - box.MaxX));
        double dy = Math.Max(0, Math.Max(box.MinY - y, y - box.MaxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The least-total-cost assignment of rows to columns of a rectangular matrix (Kuhn-Munkres, the O(n³)
    /// potentials form). Each row gets a column, or −1 when there are more rows than columns.
    /// </summary>
    internal static int[] Hungarian(double[,] cost)
    {
        int rows = cost.GetLength(0), cols = cost.GetLength(1);
        bool transposed = rows > cols;
        int n = transposed ? cols : rows, m = transposed ? rows : cols;
        double C(int i, int j) => transposed ? cost[j, i] : cost[i, j];

        // 1-based potentials u (rows), v (columns); way[j] is the previous column on the augmenting path.
        var u = new double[n + 1];
        var v = new double[m + 1];
        var p = new int[m + 1];      // p[j]: the row matched to column j, 0 for none
        var way = new int[m + 1];
        for (int i = 1; i <= n; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, m + 1).ToArray();
            var used = new bool[m + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                double delta = double.PositiveInfinity;
                for (int j = 1; j <= m; j++)
                {
                    if (used[j]) continue;
                    double cur = C(i0 - 1, j - 1) - u[i0] - v[j];
                    if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }
                for (int j = 0; j <= m; j++)
                {
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                }
                j0 = j1;
            } while (p[j0] != 0);
            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }

        var result = Enumerable.Repeat(-1, rows).ToArray();
        for (int j = 1; j <= m; j++)
        {
            if (p[j] == 0) continue;
            if (transposed) result[j - 1] = p[j] - 1;
            else result[p[j] - 1] = j - 1;
        }
        return result;
    }
}
