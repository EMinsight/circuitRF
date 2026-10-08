using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// The rules a setup's statistical part must satisfy (docs/design/yield.md §4) — the tolerances on its
/// entries, its correlations, its statistics settings, its corners and its yield goals. Called by
/// <see cref="TuningValidator"/>, so <c>check</c> reports them, and by every run that draws, so a setup
/// <c>check</c> passes never refuses later for a reason <c>check</c> could have seen. Diagnostics are
/// RETURNED; the caller decides where they go.
/// </summary>
public static class StatisticsValidator
{
    /// <summary>A draw at or below zero more likely than this is a warning (yield overview D2).</summary>
    public const double NonPhysicalThreshold = 1e-9;

    public static IReadOnlyList<Diagnostic> Validate(TestBench tb, TunableCatalog catalog)
    {
        var f = new List<Diagnostic>();
        if (tb.Tuning is not { } setup) return f;

        foreach (var e in setup.Variables) Entry(e, catalog, f);
        ComplexParts(setup, f);
        Correlations(setup, f);
        Settings(setup.Statistics, f);
        Corners(setup, tb, catalog, f);

        if (!setup.Variables.Any(e => e.IsStatistical))
            foreach (var g in setup.Goals.Where(g => g.Enabled && g.Use == GoalUse.Yield))
                f.Add(StatisticsDiagnostics.NothingVaries(g.Name));
        return f;
    }

    // ── One entry ────────────────────────────────────────────────────────────

    private static void Entry(TunableEntry e, TunableCatalog catalog, List<Diagnostic> f)
    {
        string who = $"tune {e.Key}";
        var sp = e.Spread ?? new StatSpread();
        string dist = Word(e.Distribution);

        if (e.Distribution == StatDistribution.None)
        {
            foreach (var (key, _) in Written(sp)) f.Add(StatisticsDiagnostics.SpreadExtra(who, dist, key));
            return;
        }

        if (catalog.Find(e.Key) is not { } t)
        {
            f.Add(StatisticsDiagnostics.NotTunable(who));
            return;
        }
        if (!e.Stat) f.Add(StatisticsDiagnostics.DistributionOff(who));

        // Exactly the keys the distribution reads (D2).
        var allowed = e.Distribution switch
        {
            StatDistribution.Gauss or StatDistribution.LogNorm
                => sp.Sd is not null ? ["sd", "trunc"] : new[] { "tol", "sigmas", "trunc" },
            StatDistribution.Unif
                => sp.Tol is not null ? ["tol"] : new[] { "lo", "hi" },
            _   => ["lo", "hi", "by"],
        };
        bool shapeOk = true;
        foreach (var (key, _) in Written(sp))
            if (!allowed.Contains(key)) { f.Add(StatisticsDiagnostics.SpreadExtra(who, dist, key)); shapeOk = false; }
        string? missing = e.Distribution switch
        {
            StatDistribution.Gauss or StatDistribution.LogNorm when sp.Sd is null && sp.Tol is null => "sd=, or tol= with sigmas=",
            StatDistribution.Gauss or StatDistribution.LogNorm when sp.Sd is null && sp.Sigmas is null => "sigmas= with tol=",
            StatDistribution.Unif when sp.Tol is null && (sp.Lo is null || sp.Hi is null) => "tol=, or lo= and hi=",
            StatDistribution.Discrete when sp.Lo is null || sp.Hi is null || sp.By is null => "lo=, hi= and by=",
            _ => null,
        };
        if (missing is not null) { f.Add(StatisticsDiagnostics.SpreadMissing(who, dist, missing)); shapeOk = false; }

        if (t.IsInteger && e.Distribution is StatDistribution.Gauss or StatDistribution.LogNorm)
            f.Add(StatisticsDiagnostics.ContinuousOnInteger(who, dist));

        double scale = t.Unit.Length == 0 ? 1 : Core.Expressions.Units.Scale(t.Unit) ?? 1;
        double nominal = t.Value * scale;
        if (e.Distribution == StatDistribution.LogNorm && nominal <= 0)
            f.Add(StatisticsDiagnostics.LogNormNotPositive(who, t.ValueText));

        // Each written value must be one, and the widths positive.
        foreach (var (key, text) in Written(sp))
        {
            double? v = key is "sigmas" or "trunc" ? ResolvedSpread.Number(text)
                      : key is "lo" or "hi" ? ResolvedSpread.Point(text, nominal, t.Unit)
                      : ResolvedSpread.Width(text, nominal, t.Unit);
            if (v is null) { f.Add(StatisticsDiagnostics.SpreadNotANumber(who, key, text)); shapeOk = false; }
            else if (key is "sd" or "tol" or "sigmas" or "trunc" or "by" && v <= 0)
            { f.Add(StatisticsDiagnostics.SpreadNotPositive(who, key, text)); shapeOk = false; }
        }
        if (!shapeOk || ResolvedSpread.Of(e, t) is not { } r) return;

        if (r.Lo is { } lo && r.Hi is { } hi)
        {
            if (lo >= hi) { f.Add(StatisticsDiagnostics.SpreadInverted(who, sp.Lo ?? "", sp.Hi ?? "")); return; }
            if (r.Step is { } by)
            {
                double steps = (hi - lo) / by;
                if (Math.Abs(steps - Math.Round(steps)) > 1e-6 * Math.Max(1, steps))
                    f.Add(StatisticsDiagnostics.StepNotDividing(who, sp.By!, sp.Lo!, sp.Hi!));
            }
        }

        if (e.Stat && MustBePositive(t, nominal) && r.NonPhysicalProbability() is var p && p > NonPhysicalThreshold)
            f.Add(StatisticsDiagnostics.NonPhysical(who, p));
    }

    /// <summary>A resistance, capacitance, inductance, conductance or length with a positive nominal —
    /// or a magnitude — is non-physical at or below zero.</summary>
    private static bool MustBePositive(Tunable t, double nominal)
    {
        if (nominal <= 0) return false;
        if (t.Part == ComplexPart.Mag) return true;
        if (t.Part is not null) return false;
        return Core.Expressions.Units.BaseUnit(t.Unit) is "Ohm" or "F" or "H" or "S" or "metre";
    }

    // ── Complex parts (D2) ───────────────────────────────────────────────────

    private static void ComplexParts(TuningSetup setup, List<Diagnostic> f)
    {
        var byWhole = setup.Variables
            .Where(e => e.Distribution != StatDistribution.None)
            .Select(e => TunableKey.TryParse(e.Key, out var k) && k.Part is { } part ? (Whole: k.Whole.ToString(), Part: part, e.Key) : default)
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Whole, StringComparer.Ordinal);
        foreach (var g in byWhole)
        {
            var parts = g.Select(x => x.Part).Distinct().ToList();
            string list = string.Join(", ", g.Select(x => x.Key));
            if (parts.Count > 2) f.Add(StatisticsDiagnostics.TooManyParts(g.Key, list));
            else if (parts.Count == 2 && !(parts.All(p => p is ComplexPart.Real or ComplexPart.Imag)
                                           || parts.All(p => p is ComplexPart.Mag or ComplexPart.Phase)))
                f.Add(StatisticsDiagnostics.MixedParts(g.Key, list));
        }
    }

    // ── Correlations (D3) ────────────────────────────────────────────────────

    private static void Correlations(TuningSetup setup, List<Diagnostic> f)
    {
        var stat = setup.Variables.Where(e => e.IsStatistical).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        bool valid = true;
        foreach (var c in setup.Correlations)
        {
            string who = $"correlate {c.First} {c.Second}";
            if (c.First == c.Second) { f.Add(StatisticsDiagnostics.CorrelateSameKey(who)); valid = false; }
            foreach (var key in new[] { c.First, c.Second }.Distinct())
                if (!stat.Contains(key)) { f.Add(StatisticsDiagnostics.CorrelateNotStatistical(who, key)); valid = false; }
            if (!(c.Rho > -1 && c.Rho < 1)) { f.Add(StatisticsDiagnostics.RhoOutOfRange(who, c.Rho)); valid = false; }
        }
        if (valid && CorrelationOf(setup) is { Repaired: true } m)
            f.Add(StatisticsDiagnostics.CorrelationRepaired(m.LargestChange));
    }

    /// <summary>The correlation matrix the setup's <c>correlate</c> lines describe, over the statistical
    /// entries they name in setup order, and — when it is not positive definite — the nearest one that is
    /// (D3). Null when there are no correlations. A pair stated twice takes its last value.</summary>
    public static CorrelationMatrix? CorrelationOf(TuningSetup setup)
    {
        if (setup.Correlations.Count == 0) return null;
        var named = setup.Correlations.SelectMany(c => new[] { c.First, c.Second }).ToHashSet(StringComparer.Ordinal);
        var keys = setup.Variables.Select(e => e.Key).Where(named.Contains).Distinct().ToList();
        foreach (var k in named.Where(k => !keys.Contains(k))) keys.Add(k);

        int n = keys.Count;
        var a = new double[n, n];
        for (int i = 0; i < n; i++) a[i, i] = 1;
        foreach (var c in setup.Correlations)
        {
            int i = keys.IndexOf(c.First), j = keys.IndexOf(c.Second);
            if (i == j) continue;
            a[i, j] = a[j, i] = c.Rho;
        }
        if (NearestCorrelation.IsPositiveDefinite(a)) return new CorrelationMatrix(keys, a, false, 0);
        var (fixedMatrix, change) = NearestCorrelation.Repair(a);
        return new CorrelationMatrix(keys, fixedMatrix, true, change);
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    private static void Settings(StatisticsSettings? s, List<Diagnostic> f)
    {
        if (s is null || !s.AutoStop) return;
        if (s.Sampling == StatSampling.Lhs) f.Add(StatisticsDiagnostics.LhsWithAutoStop());
        if (s.Target is null) f.Add(StatisticsDiagnostics.AutoStopNeedsTarget());
    }

    // ── Corners (D10) ────────────────────────────────────────────────────────

    private static void Corners(TuningSetup setup, TestBench tb, TunableCatalog catalog, List<Diagnostic> f)
    {
        var globals = tb.GlobalVariables.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var c in setup.Corners)
        {
            string who = $"corner {c.Name}";
            if (c.Temp is { } temp && !TunableValue.TryParse(temp, out _, out _, out _))
                f.Add(StatisticsDiagnostics.CornerTempNotANumber(who, temp));

            if (c.Trial is not null)
            {
                var missing = new List<string>();
                if (c.Seed is null)     missing.Add("seed=");
                if (c.Sampling is null) missing.Add("sampling=");
                if (c.Trials is null)   missing.Add("trials=");
                if (missing.Count > 0) f.Add(StatisticsDiagnostics.CornerTrialIncomplete(who, string.Join(", ", missing)));
            }

            foreach (var key in c.Values.Keys)
            {
                bool known = TunableKey.TryParse(key, out var k) && k.IsVariable && k.Cell is null && k.Part is null
                    ? globals.Contains(key) || catalog.FindValue(key) is not null
                    : catalog.FindValue(key) is not null;
                if (!known) f.Add(StatisticsDiagnostics.CornerUnknownKey(who, key));
            }
        }
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>The spread keys written on an entry, in the writer's order.</summary>
    internal static IEnumerable<(string Key, string Text)> Written(StatSpread sp)
    {
        if (sp.Sd     is { } sd) yield return ("sd", sd);
        if (sp.Tol    is { } tol) yield return ("tol", tol);
        if (sp.Sigmas is { } k) yield return ("sigmas", k);
        if (sp.Lo     is { } lo) yield return ("lo", lo);
        if (sp.Hi     is { } hi) yield return ("hi", hi);
        if (sp.By     is { } by) yield return ("by", by);
        if (sp.Trunc  is { } tr) yield return ("trunc", tr);
    }

    internal static string Word(StatDistribution d)
        => Core.Netlist.AnalysisDirectiveSchema.DistTokens[(int)d];
}

/// <summary>A correlation matrix over <see cref="Keys"/>, repaired to the nearest valid one when
/// <see cref="Repaired"/>, with the largest change that made.</summary>
public sealed record CorrelationMatrix(IReadOnlyList<string> Keys, double[,] Matrix, bool Repaired, double LargestChange);
