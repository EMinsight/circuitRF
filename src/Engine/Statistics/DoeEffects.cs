using CircuitRF.Core.Design;

namespace CircuitRF.Engine.Statistics;

/// <summary>
/// One term of a design's model, on coded factors (brief-yield-14 R-ya14-4): a main effect (one factor), a two-factor
/// interaction (two), or a pure quadratic (one factor, <see cref="Squared"/>).
/// </summary>
public sealed record DoeTerm(int[] Factors, bool Squared = false)
{
    /// <summary>The textbook spelling — <c>A</c>, <c>AB</c>, <c>A^2</c>.</summary>
    public string Name => Squared ? DoeDesigns.Letter(Factors[0]) + "^2" : string.Concat(Factors.Select(DoeDesigns.Letter));

    /// <summary>The factors as a bitmask; a quadratic has none (it aliases nothing two-level).</summary>
    public ulong Mask => Squared ? 0 : Factors.Aggregate(0UL, (m, f) => m | 1UL << f);

    /// <summary>The term's regressor at a coded point.</summary>
    public double At(ReadOnlySpan<double> x)
    {
        if (Squared) return x[Factors[0]] * x[Factors[0]];
        double v = 1;
        foreach (int f in Factors) v *= x[f];
        return v;
    }
}

/// <summary>A term the model estimates, and every effect it cannot be told apart from on this design — each spelled
/// as a term, with its correlation in brackets when the confounding is partial (<c>BC (-0.33)</c>).</summary>
public sealed record DoeModelTerm(DoeTerm Term, IReadOnlyList<string> Aliases);

/// <summary>One estimated effect (R-ya14-4).</summary>
/// <param name="Effect">Twice the coded coefficient: for a main effect, the response's change from the low level to
/// the high.</param>
/// <param name="Active">|effect| exceeds the Lenth margin (and is not round-off on a response the model reproduces
/// exactly).</param>
public sealed record DoeEffect(string Term, double Effect, double Coefficient, bool Active, IReadOnlyList<string> Aliases);

/// <summary>A response fitted on a design's model (R-ya14-4/5).</summary>
/// <param name="Pse">Lenth's pseudo-standard-error of the effects.</param>
/// <param name="Margin">Lenth's margin of error: t(0.975; m/3) × PSE, m the effects.</param>
/// <param name="Curvature">Two-level designs with centre points: the cube runs' mean less the centre runs'; null
/// otherwise.</param>
/// <param name="CurvatureActive">The curvature is larger than the margin — a quadratic model (<c>ccf</c>) is needed.</param>
public sealed record DoeFit(
    double                     Intercept,
    IReadOnlyList<DoeEffect>   Effects,
    IReadOnlyList<DoeModelTerm> Terms,
    double                     Pse,
    double                     Margin,
    double                     RSquared,
    double?                    Curvature,
    bool                       CurvatureActive)
{
    /// <summary>The fitted response at a coded point.</summary>
    public double Predict(ReadOnlySpan<double> x)
    {
        double y = Intercept;
        for (int i = 0; i < Terms.Count; i++) y += Effects[i].Coefficient * Terms[i].Term.At(x);
        return y;
    }
}

/// <summary>
/// The analysis of a structured design (brief-yield-14 R-ya14-4, docs/design/yield.md §17): which terms a design can
/// estimate and what each is confounded with, the least-squares fit on coded factors, and Lenth's method for telling
/// active effects from noise-sized ones.
///
/// <para><b>Why Lenth.</b> A deterministic simulation has no pure error — repeating a run repeats its answer — so there
/// is no residual variance to test an effect against. Lenth's pseudo-standard-error (R. V. Lenth, "Quick and easy
/// analysis of unreplicated factorials", <i>Technometrics</i> 31 (1989) 469–473) estimates the noise level from the
/// effects themselves, on the premise that most of them are inactive: s0 = 1.5 × median|e|, PSE = 1.5 × the median of
/// the |e| below 2.5 s0, and the margin of error t(0.975; m/3) × PSE. An effect beyond the margin is active.</para>
/// </summary>
public static class DoeEffects
{
    /// <summary>The confidence of Lenth's margin.</summary>
    public const double Confidence = 0.95;

    /// <summary>Aliases are listed up to this many factors — a four-factor interaction is never what moved a response.</summary>
    public const int AliasOrder = 3;

    /// <summary>
    /// The model a design estimates. <c>full2</c>: every main effect and two-factor interaction, each clear.
    /// <c>frac</c>: main effects, then each two-factor interaction that is not in an earlier term's alias set — one
    /// estimate per alias chain, labelled with the whole chain. <c>pb</c>: main effects only, each with the two-factor
    /// interactions it is partially confounded with and their correlations. <c>ccf</c>: main effects, two-factor
    /// interactions and pure quadratics.
    /// </summary>
    /// <param name="cube">The two-level runs (the design without its centre and axial points) — what a Plackett–Burman
    /// design's partial aliases are measured on.</param>
    public static IReadOnlyList<DoeModelTerm> Model(DoeDesignKind kind, int k, FractionalPlan? plan, IReadOnlyList<double[]> cube)
    {
        var mains = Enumerable.Range(0, k).Select(i => new DoeTerm([i])).ToList();
        var pairs = new List<DoeTerm>();
        for (int i = 0; i < k; i++)
            for (int j = i + 1; j < k; j++) pairs.Add(new DoeTerm([i, j]));

        switch (kind)
        {
            case DoeDesignKind.Full2:
                return [.. mains.Concat(pairs).Select(t => new DoeModelTerm(t, []))];

            case DoeDesignKind.Pb:
                return [.. mains.Select(m => new DoeModelTerm(m, PartialAliases(m, pairs, cube)))];

            case DoeDesignKind.Frac:
            {
                var relation = plan is null ? [] : DoeDesigns.DefiningRelation(plan);
                var kept = new List<DoeModelTerm>();
                var covered = new HashSet<ulong>();
                foreach (var t in mains.Concat(pairs))
                {
                    var chain = relation.Select(w => t.Mask ^ w).ToList();
                    if (covered.Contains(t.Mask)) continue;
                    covered.Add(t.Mask);
                    foreach (var a in chain) covered.Add(a);
                    kept.Add(new DoeModelTerm(t, AliasNames(chain)));
                }
                return kept;
            }

            default: // Ccf
            {
                var relation = plan is null ? [] : DoeDesigns.DefiningRelation(plan);
                var squares = Enumerable.Range(0, k).Select(i => new DoeTerm([i], Squared: true));
                return [.. mains.Concat(pairs).Select(t => new DoeModelTerm(t, AliasNames(relation.Select(w => t.Mask ^ w))))
                                .Concat(squares.Select(t => new DoeModelTerm(t, [])))];
            }
        }
    }

    private static List<string> AliasNames(IEnumerable<ulong> chain)
        => [.. chain.Where(m => m != 0 && DoeDesigns.Order(m) <= AliasOrder).Distinct()
                    .OrderBy(DoeDesigns.Order).ThenBy(m => m).Select(DoeDesigns.Name)];

    /// <summary>A Plackett–Burman main effect's partial aliases: every two-factor interaction not involving it whose
    /// column is correlated with its column over the design's runs.</summary>
    private static List<string> PartialAliases(DoeTerm main, IReadOnlyList<DoeTerm> pairs, IReadOnlyList<double[]> runs)
    {
        var aliases = new List<string>();
        int n = runs.Count;
        foreach (var p in pairs)
        {
            if (p.Factors.Contains(main.Factors[0])) continue;
            double c = 0;
            foreach (var x in runs) c += main.At(x) * p.At(x);
            c /= n;
            if (Math.Abs(c) > 1e-9)
                aliases.Add($"{p.Name} ({c.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture)})");
        }
        return aliases;
    }

    /// <summary>
    /// Fits <paramref name="y"/> (NaN for a run that did not evaluate — left out) on the model's terms over coded
    /// <paramref name="x"/>, and judges each effect with Lenth's margin. Null when fewer runs evaluated than the model
    /// has coefficients.
    /// </summary>
    /// <param name="centre">Per run, whether it is a centre point — what the curvature check of a two-level design
    /// compares; pass none for a <c>ccf</c>, whose model has the quadratic terms instead.</param>
    public static DoeFit? Fit(IReadOnlyList<DoeModelTerm> terms, IReadOnlyList<double[]> x, IReadOnlyList<double> y,
                              IReadOnlyList<bool>? centre = null)
    {
        var rows = new List<double[]>();
        var ys = new List<double>();
        for (int r = 0; r < x.Count; r++)
        {
            if (!double.IsFinite(y[r])) continue;
            var row = new double[terms.Count + 1];
            row[0] = 1;
            for (int t = 0; t < terms.Count; t++) row[t + 1] = terms[t].Term.At(x[r]);
            rows.Add(row);
            ys.Add(y[r]);
        }
        if (rows.Count < terms.Count + 1) return null;

        var fit = LeastSquares.Fit(rows, ys);
        var coefficients = fit.Coefficients;
        var effects = terms.Select((_, t) => 2 * coefficients[t + 1]).ToArray();
        double pse = LenthPse(effects);
        double margin = LenthMargin(effects.Length, pse);
        double floor = NoiseFloor(effects);

        double? curvature = null;
        bool curvatureActive = false;
        if (centre is not null)
        {
            var cube = Enumerable.Range(0, x.Count).Where(r => !centre[r] && double.IsFinite(y[r])).Select(r => y[r]).ToList();
            var mid  = Enumerable.Range(0, x.Count).Where(r => centre[r] && double.IsFinite(y[r])).Select(r => y[r]).ToList();
            if (cube.Count > 0 && mid.Count > 0)
            {
                curvature = cube.Average() - mid.Average();
                curvatureActive = Math.Abs(curvature.Value) > Math.Max(margin, floor);
            }
        }

        return new DoeFit(
            coefficients[0],
            [.. terms.Select((t, i) => new DoeEffect(t.Term.Name, effects[i], coefficients[i + 1],
                                                     Math.Abs(effects[i]) > Math.Max(margin, floor), t.Aliases))],
            terms, pse, margin, fit.RSquared, curvature, curvatureActive);
    }

    /// <summary>Lenth's pseudo-standard-error of a set of effects.</summary>
    public static double LenthPse(IReadOnlyList<double> effects)
    {
        if (effects.Count == 0) return double.NaN;
        var abs = effects.Select(Math.Abs).ToArray();
        double s0 = 1.5 * Median(abs);
        var small = abs.Where(a => a < 2.5 * s0).ToArray();
        return 1.5 * (small.Length == 0 ? Median(abs) : Median(small));
    }

    /// <summary>Lenth's margin of error for <paramref name="m"/> effects: t(0.975; m/3) × PSE.</summary>
    public static double LenthMargin(int m, double pse)
        => m == 0 || !double.IsFinite(pse) ? double.NaN : SpecialFunctions.StudentTQuantile((1 + Confidence) / 2, m / 3.0) * pse;

    /// <summary>Below this an effect is round-off: on a response the model reproduces exactly the PSE is 0, and an
    /// inert factor's effect of 1e-16 must not read as active.</summary>
    public static double NoiseFloor(IReadOnlyList<double> effects)
        => 1e-9 * (effects.Count == 0 ? 0 : effects.Max(Math.Abs));

    /// <summary>The response's mean at each level a factor takes (the main-effects plot, R-ya14-6): the distinct coded
    /// values, ascending, and the mean over the runs at each.</summary>
    public static (double[] Levels, double[] Means) LevelMeans(IReadOnlyList<double[]> x, IReadOnlyList<double> y, int factor)
    {
        var groups = Enumerable.Range(0, x.Count).Where(r => double.IsFinite(y[r]))
                               .GroupBy(r => Math.Round(x[r][factor], 9)).OrderBy(g => g.Key).ToList();
        return ([.. groups.Select(g => g.Key)], [.. groups.Select(g => g.Average(r => y[r]))]);
    }

    /// <summary>The interaction plot's cells (R-ya14-6): the response's mean at each (B low|high, A low|high) over the
    /// two-level runs — rows are B's levels, columns A's. A cell no evaluated run reached is NaN.</summary>
    public static double[,] InteractionMeans(IReadOnlyList<double[]> x, IReadOnlyList<double> y, int a, int b)
    {
        var means = new double[2, 2];
        for (int bi = 0; bi < 2; bi++)
            for (int ai = 0; ai < 2; ai++)
            {
                double lb = bi == 0 ? -1 : 1, la = ai == 0 ? -1 : 1;
                var cell = Enumerable.Range(0, x.Count)
                    .Where(r => double.IsFinite(y[r]) && x[r][a] == la && x[r][b] == lb).Select(r => y[r]).ToList();
                means[bi, ai] = cell.Count == 0 ? double.NaN : cell.Average();
            }
        return means;
    }

    private static double Median(double[] v)
    {
        var s = v.Order().ToArray();
        int n = s.Length;
        return n == 0 ? double.NaN : n % 2 == 1 ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
    }
}
