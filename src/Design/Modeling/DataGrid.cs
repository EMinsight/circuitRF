using System.Globalization;

namespace CircuitRF.Design.Modeling;

/// <summary>
/// Whether a constituent's training points lie on a complete tensor grid in its inputs (brief MC-30 D1). Simulated
/// intrinsic-plane tables do — every combination of the swept vgs × vds (× T) present once — and then the samples ARE the
/// table: re-gridding them through a network or an interpolant only adds that interpolant's error. Whether the data is a
/// grid is a fact about the data, so it is detected here rather than chosen.
///
/// <para>Two coordinates are one site when they differ by at most 1e-9 of that axis's span. A set one site short of a grid
/// per hundred or so — an excluded point, a sample that did not converge — is reported as <see cref="Holes"/>, so the fit
/// can say which points broke the grid; anything sparser is scattered data and says nothing.</para>
/// </summary>
public sealed class DataGrid
{
    /// <summary>Each input's sites, strictly increasing.</summary>
    public required double[][] Sites { get; init; }
    /// <summary>Each point's flat index into the grid, row-major, the last axis fastest — the layout the table fit reads.</summary>
    public required int[] Flat { get; init; }
    /// <summary>Each point's index along each axis.</summary>
    public required int[][] Index { get; init; }
    /// <summary>The grid's missing sites, when the points fall short of a complete grid; empty on a complete one.</summary>
    public required IReadOnlyList<double[]> Holes { get; init; }

    public bool Complete => Holes.Count == 0;

    /// <summary>The sites per axis, as the report writes them: <c>101 × 121 × 5</c>.</summary>
    public string Shape => string.Join(" × ", Sites.Select(s => s.Length));

    /// <summary>The fraction of a grid's sites that may be missing for the points still to be called a grid with holes.</summary>
    private const double MaxHoleFraction = 0.02;

    /// <summary>The tolerance two coordinates are one site within, relative to the axis's span.</summary>
    public const double SiteTolerance = 1e-9;

    /// <summary>
    /// The grid <paramref name="x"/> lies on, complete or with holes, or null for scattered points — a duplicated site, an
    /// axis of fewer than two sites, or more missing sites than a grid with holes would have.
    /// </summary>
    public static DataGrid? Detect(IReadOnlyList<double[]> x)
    {
        if (x.Count < 4) return null;
        int d = x[0].Length;
        var sites = new double[d][];
        var index = new int[x.Count][];
        for (int k = 0; k < x.Count; k++) index[k] = new int[d];

        for (int a = 0; a < d; a++)
        {
            var order = Enumerable.Range(0, x.Count).OrderBy(k => x[k][a]).ToArray();
            double lo = x[order[0]][a], hi = x[order[^1]][a];
            double tol = SiteTolerance * (hi - lo);
            var axis = new List<double>();
            foreach (int k in order)
            {
                double v = x[k][a];
                if (axis.Count == 0 || v - axis[^1] > tol) axis.Add(v);
                index[k][a] = axis.Count - 1;
            }
            if (axis.Count < 2) return null;
            sites[a] = [.. axis];
        }

        long size = 1;
        foreach (var s in sites) size *= s.Length;
        if (size < x.Count || size - x.Count > Math.Max(1, MaxHoleFraction * size)) return null;

        var flat = new int[x.Count];
        var seen = new bool[size];
        for (int k = 0; k < x.Count; k++)
        {
            int f = 0;
            for (int a = 0; a < d; a++) f = f * sites[a].Length + index[k][a];
            if (seen[f]) return null;
            seen[f] = true;
            flat[k] = f;
        }

        var holes = new List<double[]>();
        for (int f = 0; f < size; f++)
        {
            if (seen[f]) continue;
            var site = new double[d];
            int rest = f;
            for (int a = d - 1; a >= 0; a--) { site[a] = sites[a][rest % sites[a].Length]; rest /= sites[a].Length; }
            holes.Add(site);
        }
        return new DataGrid { Sites = sites, Flat = flat, Index = index, Holes = holes };
    }

    /// <summary>The report's sentence for a grid with holes: which sites are missing (the first three), and that it re-grids.</summary>
    public string HolesSentence(string constituent, IReadOnlyList<string> inputs)
    {
        string Site(double[] s) => "(" + string.Join(", ", s.Select((v, a) => $"{inputs[a]} {v.ToString("G4", CultureInfo.InvariantCulture)}")) + ")";
        string listed = string.Join(", ", Holes.Take(3).Select(Site)) + (Holes.Count > 3 ? $" and {Holes.Count - 3} more" : "");
        return $"{constituent}: the data is a {Shape} grid missing {Holes.Count} site(s) — {listed} — so it is re-gridded.";
    }

    /// <summary>
    /// Whole grid lines held out along the first axis, so the training points are still a grid (D1): about
    /// <paramref name="fraction"/> of the interior lines, evenly spaced, never the first or the last — the table keeps
    /// the data's extent. Each point's flag, by the line it sits on.
    /// </summary>
    public bool[] HeldOutLines(double fraction, out bool[] heldLine)
    {
        int n = Sites[0].Length;
        heldLine = new bool[n];
        int count = (int)Math.Round(fraction * n);
        if (count > 0 && n - count >= 4)
        {
            double stride = (double)(n - 1) / (count + 1);
            for (int c = 1; c <= count; c++) heldLine[Math.Clamp((int)Math.Round(c * stride), 1, n - 2)] = true;
        }
        var held = new bool[Index.Length];
        for (int k = 0; k < held.Length; k++) held[k] = heldLine[Index[k][0]];
        return held;
    }
}
