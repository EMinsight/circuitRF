using CircuitRF.Core.Design;

namespace CircuitRF.Engine.Statistics;

/// <summary>One fractional factorial from the embedded table: <c>2^(k−p)</c> runs at <see cref="Resolution"/>, each
/// added factor defined by its generator (<c>E=ABCD</c>).</summary>
public sealed record FractionalPlan(int Factors, int Fraction, int Resolution, IReadOnlyList<string> Generators)
{
    public int Runs => 1 << (Factors - Fraction);
}

/// <summary>
/// The point constructions of design of experiments (brief-yield-14 R-ya14-2, docs/design/yield.md §17). Every point
/// is in CODED units, −1 the low level and +1 the high; mapping a level onto a value is the caller's business.
///
/// <para><b>Factor letters</b> are the textbook's — A to Z without I, so the ninth factor is J — because the
/// fractional-factorial generators below are copied in that spelling.</para>
///
/// <para><b>The fractional factorials</b> are the table of "selected 2^(k−p) fractional factorial designs" in
/// D. C. Montgomery, <i>Design and Analysis of Experiments</i> (Wiley), the minimum-aberration choices catalogued by
/// J. Chen, D. X. Sun and C. F. J. Wu, "A catalogue of two-level and three-level fractional factorial designs with small
/// runs", <i>International Statistical Review</i> 61 (1993) 131–145. For a requested resolution the design with the
/// fewest runs whose resolution is at least that is chosen.</para>
///
/// <para><b>The Plackett–Burman designs</b> are the cyclic constructions of R. L. Plackett and J. P. Burman, "The design
/// of optimum multifactorial experiments", <i>Biometrika</i> 33 (1946) 305–325: the published first row, each later
/// row its cyclic shift by one, then a row of every factor low.</para>
/// </summary>
public static class DoeDesigns
{
    /// <summary>Above this many factors a full factorial is refused (2^10 = 1024 runs).</summary>
    public const int Full2Limit = 10;

    /// <summary>The most factors a face-centred composite takes — its cube is the full factorial or a resolution-V
    /// fraction from the table.</summary>
    public const int CcfLimit = 10;

    /// <summary>The textbook's factor letters: no I.</summary>
    public const string Letters = "ABCDEFGHJKLMNOPQRSTUVWXYZ";

    /// <summary>Factor <paramref name="i"/>'s letter, from 0.</summary>
    public static string Letter(int i) => i < Letters.Length ? Letters[i].ToString() : "X" + (i + 1);

    // ── Full factorial ──────────────────────────────────────────────────────────────

    /// <summary>The 2^k corners in standard order — the first factor alternates fastest.</summary>
    public static double[][] FullFactorial(int k)
    {
        int n = 1 << k;
        var runs = new double[n][];
        for (int r = 0; r < n; r++)
        {
            runs[r] = new double[k];
            for (int i = 0; i < k; i++) runs[r][i] = ((r >> i) & 1) == 1 ? 1 : -1;
        }
        return runs;
    }

    // ── Fractional factorial ────────────────────────────────────────────────────────

    /// <summary>The embedded table (see the class remarks): every entry, in order of factors then runs.</summary>
    public static readonly IReadOnlyList<FractionalPlan> FractionTable =
    [
        new(4, 1, 4, ["D=ABC"]),
        new(5, 1, 5, ["E=ABCD"]),
        new(6, 2, 4, ["E=ABC", "F=BCD"]),
        new(6, 1, 6, ["F=ABCDE"]),
        new(7, 3, 4, ["E=ABC", "F=BCD", "G=ACD"]),
        new(7, 2, 4, ["F=ABCD", "G=ABDE"]),
        new(7, 1, 7, ["G=ABCDEF"]),
        new(8, 4, 4, ["E=BCD", "F=ACD", "G=ABC", "H=ABD"]),
        new(8, 3, 4, ["F=ABC", "G=ABD", "H=BCDE"]),
        new(8, 2, 5, ["G=ABCD", "H=ABEF"]),
        new(9, 4, 4, ["F=BCDE", "G=ACDE", "H=ABDE", "J=ABCE"]),
        new(9, 3, 4, ["G=ABCD", "H=ACEF", "J=CDEF"]),
        new(9, 2, 6, ["H=ACDFG", "J=BCEFG"]),
        new(10, 5, 4, ["F=ABCD", "G=ABCE", "H=ABDE", "J=ACDE", "K=BCDE"]),
        new(10, 4, 4, ["G=BCDF", "H=ACDF", "J=ABDE", "K=ABCE"]),
        new(10, 3, 5, ["H=ABCG", "J=ACEFG", "K=CDEF"]),
        new(11, 6, 4, ["F=ABC", "G=BCD", "H=CDE", "J=ACD", "K=ADE", "L=BDE"]),
        new(11, 5, 4, ["G=CDE", "H=ABCD", "J=ABF", "K=BDEF", "L=ADEF"]),
        new(11, 4, 5, ["H=ABCG", "J=BCDE", "K=ACDF", "L=ABCDEFG"]),
    ];

    /// <summary>The fewest-run design in the table for <paramref name="k"/> factors whose resolution is at least
    /// <paramref name="resolution"/>; null when the table holds none (then only the full factorial has it).</summary>
    public static FractionalPlan? Fraction(int k, int resolution)
        => FractionTable.Where(p => p.Factors == k && p.Resolution >= resolution).OrderBy(p => p.Runs).FirstOrDefault();

    /// <summary>The plan's runs: the full factorial in its k − p base factors, each added factor the product of its
    /// generator's columns.</summary>
    public static double[][] Fractional(FractionalPlan plan)
    {
        int k = plan.Factors, b = k - plan.Fraction;
        var bases = FullFactorial(b);
        var gens = plan.Generators.Select(ParseGenerator).ToList();
        var runs = new double[bases.Length][];
        for (int r = 0; r < bases.Length; r++)
        {
            runs[r] = new double[k];
            Array.Copy(bases[r], runs[r], b);
            foreach (var (target, word) in gens)
            {
                double v = 1;
                foreach (int f in word) v *= runs[r][f];
                runs[r][target] = v;
            }
        }
        return runs;
    }

    /// <summary>The words of the plan's defining relation — every product of its generators' words — as factor
    /// bitmasks (bit i is factor i), shortest first.</summary>
    public static IReadOnlyList<ulong> DefiningRelation(FractionalPlan plan)
    {
        var words = plan.Generators.Select(ParseGenerator).Select(g => g.Word.Aggregate(1UL << g.Target, (m, f) => m | 1UL << f)).ToList();
        var group = new List<ulong>();
        for (int s = 1; s < 1 << words.Count; s++)
        {
            ulong w = 0;
            for (int j = 0; j < words.Count; j++) if ((s >> j & 1) == 1) w ^= words[j];
            group.Add(w);
        }
        return [.. group.OrderBy(Order).ThenBy(w => w)];
    }

    /// <summary>The resolution a defining relation gives: its shortest word's length.</summary>
    public static int ResolutionOf(IReadOnlyList<ulong> relation) => relation.Count == 0 ? int.MaxValue : relation.Min(Order);

    /// <summary>The number of factors in a term's mask.</summary>
    public static int Order(ulong mask) => System.Numerics.BitOperations.PopCount(mask);

    /// <summary>A term's mask spelled in letters (<c>AB</c>); the empty mask is <c>I</c>.</summary>
    public static string Name(ulong mask)
    {
        if (mask == 0) return "I";
        var s = new System.Text.StringBuilder();
        for (int i = 0; i < 64; i++) if ((mask >> i & 1) == 1) s.Append(Letter(i));
        return s.ToString();
    }

    private static (int Target, int[] Word) ParseGenerator(string g)
    {
        int eq = g.IndexOf('=');
        return (Letters.IndexOf(g[0]), [.. g[(eq + 1)..].Select(c => Letters.IndexOf(c))]);
    }

    // ── Plackett–Burman ─────────────────────────────────────────────────────────────

    /// <summary>The published first rows (see the class remarks), keyed by the run count.</summary>
    public static readonly IReadOnlyDictionary<int, string> PlackettBurmanRows = new Dictionary<int, string>
    {
        [12] = "++-+++---+-",
        [20] = "++--++++-+-+----++-",
        [24] = "+++++-+-++--++--+-+----",
    };

    /// <summary>The run count a Plackett–Burman design for <paramref name="k"/> factors takes — the smallest of 12, 20
    /// and 24 with room for them; null above 23 factors.</summary>
    public static int? PlackettBurmanRuns(int k) => PlackettBurmanRows.Keys.Where(n => n - 1 >= k).Order().Cast<int?>().FirstOrDefault();

    /// <summary>The <paramref name="n"/>-run design's whole matrix, n − 1 columns; the caller uses the first k.</summary>
    public static double[][] PlackettBurmanMatrix(int n)
    {
        string first = PlackettBurmanRows[n];
        int m = n - 1;
        var runs = new double[n][];
        for (int r = 0; r < m; r++)
        {
            runs[r] = new double[m];
            for (int c = 0; c < m; c++) runs[r][c] = first[((c - r) % m + m) % m] == '+' ? 1 : -1;
        }
        runs[m] = [.. Enumerable.Repeat(-1.0, m)];
        return runs;
    }

    /// <summary>The design for <paramref name="k"/> factors: the first k columns of the smallest matrix that fits.</summary>
    public static double[][] PlackettBurman(int k)
    {
        int n = PlackettBurmanRuns(k) ?? throw new ArgumentOutOfRangeException(nameof(k), "at most 23 factors");
        return [.. PlackettBurmanMatrix(n).Select(r => r[..k])];
    }

    // ── Composite designs ───────────────────────────────────────────────────────────

    /// <summary>
    /// The centre, then ±<paramref name="delta"/> on each coordinate in turn (+ before −) — the axial part every
    /// composite design shares. The quadratic surrogate of design centering (brief-yield-12) builds its design on it
    /// with δ = 1 in z-space; the face-centred composite with δ = 1 in coded units.
    /// </summary>
    public static List<double[]> CentreAndAxial(int k, double delta)
    {
        var points = new List<double[]> { new double[k] };
        for (int i = 0; i < k; i++)
        {
            var plus = new double[k]; plus[i] = delta;
            var minus = new double[k]; minus[i] = -delta;
            points.Add(plus);
            points.Add(minus);
        }
        return points;
    }

    /// <summary>The cube of a face-centred composite: the full factorial up to 4 factors, beyond that the table's
    /// fewest-run resolution-V fraction, so every two-factor interaction stays clear of every other term.</summary>
    public static (double[][] Runs, FractionalPlan? Plan) CompositeCube(int k)
    {
        if (k <= 4) return (FullFactorial(k), null);
        var plan = Fraction(k, 5) ?? throw new ArgumentOutOfRangeException(nameof(k), $"at most {CcfLimit} factors");
        return (Fractional(plan), plan);
    }

    /// <summary>The face-centred composite: the cube, then the axial points on its faces. The centre is NOT included —
    /// the caller adds its centre points (R-ya14-2's <c>centre=</c>).</summary>
    public static double[][] FaceCentred(int k)
    {
        var (cube, _) = CompositeCube(k);
        return [.. cube, .. CentreAndAxial(k, 1).Skip(1)];
    }
}
