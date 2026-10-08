using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// The Yield panel's compact spread editor as text (brief-yield-10 R-ya10-3): a spread shown as <c>± 2 % at 3σ</c>,
/// <c>σ 1 Ω</c>, <c>45 … 55 Ω</c>, <c>4 … 8 by 2</c>, with <c>, trunc 3σ</c> after a Gaussian one — and read back from
/// what a user types there. It only moves text between that spelling and the <c>tune</c> line's keys
/// (<see cref="StatSpread"/>): whether the result is a valid spread is <see cref="StatisticsValidator"/>'s to say, so an
/// edit is refused in <c>check</c>'s words and never in a second set of rules.
/// </summary>
public static partial class ToleranceText
{
    /// <summary>The σ count a <c>±</c> spread on a Gaussian is read at when none is written.</summary>
    public const string DefaultSigmas = "3";

    /// <summary>What a new tolerance draws from: a Gaussian, or a uniform draw for a whole-number value (D2 refuses a
    /// Gaussian there).</summary>
    public static StatDistribution DefaultDistribution(Tunable t) => t.IsInteger ? StatDistribution.Unif : StatDistribution.Gauss;

    /// <summary>A new tolerance's spread: ± 5 % at 3σ for a Gaussian or lognormal, ± 5 % uniform, 95 … 105 % by 5 %.</summary>
    public static StatSpread? DefaultSpread(StatDistribution d) => d switch
    {
        StatDistribution.Gauss or StatDistribution.LogNorm => new StatSpread { Tol = "5%", Sigmas = DefaultSigmas },
        StatDistribution.Unif                              => new StatSpread { Tol = "5%" },
        StatDistribution.Discrete                          => new StatSpread { Lo = "95%", Hi = "105%", By = "5%" },
        _                                                  => null,
    };

    /// <summary>
    /// <paramref name="old"/> as the keys <paramref name="to"/> reads: a width stays a width (<c>sd</c> or <c>tol</c>),
    /// a range stays a range, and what the new distribution cannot read becomes its default. A truncation survives only
    /// on a Gaussian or lognormal.
    /// </summary>
    public static StatSpread? Convert(StatSpread? old, StatDistribution to)
    {
        if (to == StatDistribution.None) return null;
        old ??= new StatSpread();
        var d = DefaultSpread(to)!;
        switch (to)
        {
            case StatDistribution.Gauss or StatDistribution.LogNorm:
                if (old.Sd is not null) return new StatSpread { Sd = old.Sd, Trunc = old.Trunc };
                if (old.Tol is not null) return new StatSpread { Tol = old.Tol, Sigmas = old.Sigmas ?? DefaultSigmas, Trunc = old.Trunc };
                return d;
            case StatDistribution.Unif:
                if (old.Tol is not null) return new StatSpread { Tol = old.Tol };
                if (old.Sd is not null) return new StatSpread { Tol = old.Sd };
                if (old.Lo is not null && old.Hi is not null) return new StatSpread { Lo = old.Lo, Hi = old.Hi };
                return d;
            default:
                return old.Lo is not null && old.Hi is not null && old.By is not null
                    ? new StatSpread { Lo = old.Lo, Hi = old.Hi, By = old.By } : d;
        }
    }

    // ── Showing ─────────────────────────────────────────────────────────────────────

    /// <summary>The spread as the panel shows it; empty for none.</summary>
    public static string Format(StatDistribution d, StatSpread? sp)
    {
        if (d == StatDistribution.None || sp is null) return "";
        string text;
        if (sp.Sd is { } sd) text = $"σ {Show(sd)}";
        else if (sp.Tol is { } tol)
            text = d is StatDistribution.Gauss or StatDistribution.LogNorm ? $"± {Show(tol)} at {sp.Sigmas ?? "?"}σ" : $"± {Show(tol)}";
        else if (sp.Lo is not null || sp.Hi is not null)
            text = $"{Show(sp.Lo ?? "?")} … {Show(sp.Hi ?? "?")}" + (sp.By is { } by ? $" by {Show(by)}" : "");
        else text = "";
        if (sp.Trunc is { } k) text += (text.Length > 0 ? ", " : "") + $"trunc {k}σ";
        return text;
    }

    /// <summary>A value as shown: <c>2%</c> as <c>2 %</c>, <c>1 Ohm</c> as <c>1 Ω</c>.</summary>
    private static string Show(string value)
    {
        string v = value.Trim();
        if (v.EndsWith('%')) return v[..^1].TrimEnd() + " %";
        return Regex.Replace(v, @"(?<=\d|\s)Ohm\b", "Ω");
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@",?\s*\btrunc\s*(?<k>[0-9.eE+\-]+)\s*σ?\s*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TruncPart();

    [GeneratedRegex(@"\s*(?:\bat\b|/)\s*(?<k>[0-9.eE+\-]+)\s*σ\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SigmasPart();

    [GeneratedRegex(@"\s+by\s+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ByPart();

    /// <summary>
    /// What <paramref name="text"/> says for a <paramref name="d"/> draw, over <paramref name="old"/>'s σ count when a
    /// <c>±</c> spread names none. Null when the text has no recognisable shape at all; otherwise the keys it names —
    /// which the validator then accepts or refuses.
    /// </summary>
    public static StatSpread? Parse(string text, StatDistribution d, StatSpread? old = null)
    {
        string t = text.Trim();
        if (t.Length == 0) return null;
        var sp = new StatSpread();

        if (TruncPart().Match(t) is { Success: true } tm)
        {
            sp.Trunc = tm.Groups["k"].Value;
            t = t.Remove(tm.Index, tm.Length).Trim();
        }

        if (t.StartsWith('±') || t.StartsWith("+-", StringComparison.Ordinal) || t.StartsWith("+/-", StringComparison.Ordinal))
        {
            t = t.TrimStart('±', '+', '/', '-').Trim();
            if (SigmasPart().Match(t) is { Success: true } sm)
            {
                sp.Sigmas = sm.Groups["k"].Value;
                t = t[..sm.Index].Trim();
            }
            sp.Tol = Value(t);
            if (d is StatDistribution.Gauss or StatDistribution.LogNorm) sp.Sigmas ??= old?.Sigmas ?? DefaultSigmas;
            return sp.Tol is null ? null : sp;
        }
        if (t.StartsWith('σ') || t.StartsWith("sd", StringComparison.OrdinalIgnoreCase))
        {
            sp.Sd = Value(t.TrimStart('σ').Trim() is var rest && rest.StartsWith("sd", StringComparison.OrdinalIgnoreCase) ? rest[2..] : rest);
            return sp.Sd is null ? null : sp;
        }

        int dots = t.IndexOf('…');
        int len = 1;
        if (dots < 0) { dots = t.IndexOf("...", StringComparison.Ordinal); len = 3; }
        if (dots < 0) { dots = t.IndexOf("..", StringComparison.Ordinal); len = 2; }
        if (dots >= 0)
        {
            string lo = t[..dots].Trim(), hi = t[(dots + len)..].Trim();
            if (ByPart().Match(hi) is { Success: true } bm)
            {
                sp.By = Value(hi[(bm.Index + bm.Length)..]);
                hi = hi[..bm.Index].Trim();
            }
            sp.Lo = Value(lo);
            sp.Hi = Value(hi);
            // `45 … 55 Ω`: the unit written once, after the range, belongs to both ends.
            if (sp.Lo is not null && sp.Hi is not null && UnitOf(sp.Hi) is { } u && UnitOf(sp.Lo) is null && !sp.Lo.EndsWith('%'))
                sp.Lo += " " + u;
            if (sp.By is not null && sp.Hi is not null && UnitOf(sp.Hi) is { } bu && UnitOf(sp.By) is null && !sp.By.EndsWith('%'))
                sp.By += " " + bu;
            return sp.Lo is null || sp.Hi is null ? null : sp;
        }

        // A bare width: a spread, ± it.
        sp.Tol = Value(t);
        if (sp.Tol is null) return null;
        if (d is StatDistribution.Gauss or StatDistribution.LogNorm) sp.Sigmas = old?.Sigmas ?? DefaultSigmas;
        return sp;
    }

    /// <summary>A typed value as the line keeps it: <c>2 %</c> as <c>2%</c>, <c>1 Ω</c> as <c>1 Ohm</c>; null for
    /// nothing.</summary>
    private static string? Value(string text)
    {
        string v = text.Trim();
        if (v.Length == 0) return null;
        if (v.EndsWith('%')) return v[..^1].TrimEnd() + "%";
        var m = Regex.Match(v, @"^(?<n>[+\-]?[0-9.]+(?:[eE][+\-]?[0-9]+)?)\s*(?<u>\S.*)?$", RegexOptions.CultureInvariant);
        if (!m.Success) return v;
        string n = m.Groups["n"].Value;
        if (!m.Groups["u"].Success) return n;
        return $"{n} {UnitNormalizer.ToEngineUnit(m.Groups["u"].Value.Trim())}";
    }

    private static string? UnitOf(string value)
    {
        int space = value.IndexOf(' ');
        return space < 0 ? null : value[(space + 1)..];
    }
}
