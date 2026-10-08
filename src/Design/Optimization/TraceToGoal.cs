using System.Globalization;
using CircuitRF.Core.Design;

namespace CircuitRF.Design.Optimization;

/// <summary>What a trace is, in the terms a goal needs (brief-tuneopt-9 R-to9-4).</summary>
public enum TraceGoalKind
{
    /// <summary>A cube-bound trace: a slice of a run cube, a measurement, a WSProbe metric.</summary>
    Cube,
    /// <summary>A network trace — one S-matrix element in a chosen format.</summary>
    Network,
    /// <summary>A quantity derived from the whole S matrix (µ, K, MAG, σ, group delay …).</summary>
    Derived,
}

/// <summary>The trace card's element-wise transform. <see cref="PowerDb"/> is the card's plain
/// <c>dB</c>, which is 10·log10 — not the 20·log10 a measure line's <c>dB()</c> is.</summary>
public enum TraceValueTransform { None, dB20, dB10, PowerDb, Mag, Phase, Real, Imag, Conj }

/// <summary>A network trace's format (the card's dependent-variable choice).</summary>
public enum TraceNetworkFormat { Complex, Db, Mag, Phase, Real, Imaginary }

/// <summary>The derived network quantities a goal can name; <see cref="Other"/> is everything else
/// (stability circles, the passive readouts), which has no expression equivalent.</summary>
public enum TraceDerivedMetric { Mu, MuPrime, K, DeltaMag, MaxGain, Passivity, GroupDelay, Other }

/// <summary>
/// Everything the translation reads off a trace and its plot — filled in by the Data Display (the
/// <c>Trace</c> model is below the firewall in <c>src/Render</c>), so the translation itself is plain
/// data in, goal out, and testable with no display.
/// </summary>
public sealed record TraceGoalSource
{
    public TraceGoalKind Kind { get; init; }

    /// <summary>Why the trace cannot become a goal whatever its transform, when the reader already
    /// knows (a "versus" trace, a renormalized view, a source that is not a schematic's results).</summary>
    public string? Unsupported { get; init; }

    /// <summary>The analysis the trace's group names, or null for a measurement (resolved from the
    /// bench when one is given).</summary>
    public string? Analysis { get; init; }

    // ── Cube ─────────────────────────────────────────────────────────────────

    /// <summary>The read WITHOUT the transform, in the expression language: <c>SP1.S[:, 2, 1]</c>,
    /// <c>wsp_ZG(SP1.wsp, SP1.idx("G"))</c>, <c>Eff</c>.</summary>
    public string? Body { get; init; }

    public TraceValueTransform Transform { get; init; }

    /// <summary>True when the value before the transform is complex.</summary>
    public bool ValueIsComplex { get; init; }

    // ── Network / Derived ────────────────────────────────────────────────────

    /// <summary>The run's S cube spec, <c>SP1.S</c>.</summary>
    public string? SCube { get; init; }

    /// <summary>The port count of that S cube.</summary>
    public int PortCount { get; init; } = 2;

    /// <summary>Network (and a cube S slice): the 1-based Sij — i the response, j the drive port; 0 when unknown.</summary>
    public int Row { get; init; }
    public int Col { get; init; }

    public TraceNetworkFormat Format { get; init; } = TraceNetworkFormat.Db;

    public TraceDerivedMetric Metric { get; init; }

    /// <summary>What the card calls the metric, for a refusal's sentence.</summary>
    public string? MetricLabel { get; init; }

    public int InputPort { get; init; } = 1;
    public int OutputPort { get; init; } = 2;
    public bool MaxGainIsLog { get; init; } = true;
    public bool PassivityWholeNetwork { get; init; } = true;

    // ── The plot ─────────────────────────────────────────────────────────────

    /// <summary>The axis the trace is plotted along (<c>freq</c>, a sweep variable).</summary>
    public string Axis { get; init; } = "freq";

    /// <summary>The visible X range, clipped to the data, in <see cref="AxisUnit"/>; null for the whole axis.</summary>
    public double? Lo { get; init; }
    public double? Hi { get; init; }

    /// <summary>The unit the X numbers are in, as value text takes it (<c>GHz</c>, <c>dBm</c>, empty).</summary>
    public string AxisUnit { get; init; } = "";

    /// <summary>The visible marker's value, as plotted; null when the trace has no visible marker.</summary>
    public double? MarkerValue { get; init; }

    /// <summary>The unit the plotted value is in when it is not the expression's own (group delay is
    /// plotted in ns and computed in s); empty otherwise.</summary>
    public string MarkerUnit { get; init; } = "";
}

/// <summary>The goal a trace becomes, or why it cannot become one.</summary>
public sealed record TraceGoalResult(OptimizationGoal? Goal, string? Reason)
{
    public bool CanTranslate => Goal is not null;
}

/// <summary>
/// "Add as goal…" (brief-tuneopt-9 R-to9-4): a plotted trace → a pre-filled goal — its analysis, its
/// expression (the cube and slice with the trace's transform as the function a measure line uses),
/// the visible X range, and a limit from the visible marker when there is one.
///
/// <para><b>The transform is translated, not copied.</b> The card's plain <c>dB</c> is a POWER dB
/// (10·log10) while a measure line's <c>dB()</c> is 20·log10, so the card's <c>dB20</c> becomes
/// <c>dB(…)</c> and its <c>dB</c> becomes <c>dB10(…)</c>: copying the name would make every power
/// goal 3 dB wrong in a way that still evaluates.</para>
///
/// <para><b>A goal needs a real quantity.</b> A complex trace with no reducing transform (a Smith
/// chart curve) and <c>conj</c> have no order and are refused with that reason; so are the network
/// quantities that have no expression (stability circles, the passive readouts).</para>
/// </summary>
public static class TraceToGoal
{
    public static TraceGoalResult Translate(TraceGoalSource src, TestBench? tb = null)
    {
        if (src.Unsupported is { } why) return new(null, why);

        string? expression; GoalType type; string limit = "";
        string? reason;
        switch (src.Kind)
        {
            case TraceGoalKind.Cube:
                (expression, type, reason) = Cube(src);
                break;
            case TraceGoalKind.Network:
                (expression, type, reason) = Network(src);
                break;
            default:
                (expression, type, limit, reason) = Derived(src);
                break;
        }
        if (expression is null) return new(null, reason);

        string? analysis = src.Analysis;
        if (analysis is null && tb is not null) analysis = GoalTemplates.AnalysisReferencedBy(expression, tb);

        if (src.MarkerValue is { } m && double.IsFinite(m))
            limit = (Number(m) + (src.MarkerUnit.Length > 0 ? " " + src.MarkerUnit : "")).Trim();

        return new(new OptimizationGoal
        {
            Name       = GoalTemplates.Identifier(expression),
            Expression = expression,
            Analysis   = analysis,
            Range      = Range(src),
            Type       = type,
            Limit      = limit,
        }, null);
    }

    private static (string?, GoalType, string?) Cube(TraceGoalSource src)
    {
        if (string.IsNullOrWhiteSpace(src.Body)) return (null, default, "This trace reads nothing a goal could name.");
        // A family's "~" iterates an axis; in an expression ':' keeps it, and the goal then holds at every curve.
        string body = src.Body.Replace('~', ':');
        bool transmission = src.Row > 0 && src.Col > 0 && src.Row != src.Col;
        GoalType magnitudeType = transmission ? GoalType.Ge : GoalType.Le;

        return src.Transform switch
        {
            TraceValueTransform.None when src.ValueIsComplex => (null, default, ComplexReason),
            TraceValueTransform.None    => (body, GoalType.Le, null),
            TraceValueTransform.dB20    => ($"dB({body})",   magnitudeType, null),
            TraceValueTransform.dB10    => ($"dB10({body})", magnitudeType, null),
            TraceValueTransform.PowerDb => ($"dB10({body})", magnitudeType, null),
            TraceValueTransform.Mag     => ($"mag({body})",  magnitudeType, null),
            TraceValueTransform.Phase   => ($"phase({body})", GoalType.Eq, null),
            TraceValueTransform.Real    => ($"real({body})", GoalType.Le, null),
            TraceValueTransform.Imag    => ($"imag({body})", GoalType.Le, null),
            _ => (null, default, "A conjugate is still complex, and a complex value has no order to hold a limit against. " +
                                 "Plot it in dB, magnitude, phase, real or imaginary to make it a goal."),
        };
    }

    private const string ComplexReason =
        "This trace is complex (a Smith or polar curve), and a complex value has no order to hold a limit " +
        "against. Plot it in dB, magnitude, phase, real or imaginary to make it a goal.";

    private static (string?, GoalType, string?) Network(TraceGoalSource src)
    {
        if (src.SCube is null || src.Row < 1 || src.Col < 1)
            return (null, default, "This trace's S-parameter cannot be named in an expression.");
        string sij = $"{src.SCube}({src.Row}, {src.Col})";
        GoalType magnitudeType = src.Row != src.Col ? GoalType.Ge : GoalType.Le;
        return src.Format switch
        {
            TraceNetworkFormat.Db        => ($"dB({sij})",    magnitudeType, null),
            TraceNetworkFormat.Mag       => ($"mag({sij})",   magnitudeType, null),
            TraceNetworkFormat.Phase     => ($"phase({sij})", GoalType.Eq, null),
            TraceNetworkFormat.Real      => ($"real({sij})",  GoalType.Le, null),
            TraceNetworkFormat.Imaginary => ($"imag({sij})",  GoalType.Le, null),
            _                            => (null, default, ComplexReason),
        };
    }

    private static (string?, GoalType, string, string?) Derived(TraceGoalSource src)
    {
        if (src.SCube is null) return (null, default, "", "This trace's S-parameters cannot be named in an expression.");
        string s = src.SCube;
        string pair = $", {src.InputPort}, {src.OutputPort}";
        // A two-port's (1, 2) is implied; anything else is spelled out, since mu(SP1.S) is refused on an N-port.
        string implied = src.PortCount == 2 && src.InputPort == 1 && src.OutputPort == 2 ? "" : pair;
        return src.Metric switch
        {
            TraceDerivedMetric.Mu         => ($"mu({s}{implied})",        GoalType.Ge, "1", null),
            TraceDerivedMetric.MuPrime    => ($"mu_prime({s}{implied})",  GoalType.Ge, "1", null),
            TraceDerivedMetric.K          => ($"K({s}{implied})",         GoalType.Ge, "1", null),
            TraceDerivedMetric.DeltaMag   => ($"delta_mag({s}{implied})", GoalType.Le, "1", null),
            TraceDerivedMetric.MaxGain    => (src.MaxGainIsLog ? $"max_gain({s}{implied})" : $"max_gain_lin({s}{implied})",
                                              GoalType.Ge, "", null),
            TraceDerivedMetric.Passivity  => (src.PassivityWholeNetwork ? $"passivity({s})" : $"passivity({s}{pair})",
                                              GoalType.Le, "1", null),
            TraceDerivedMetric.GroupDelay => ($"group_delay({s}{pair})",  GoalType.Le, "", null),
            _ => (null, default, "",
                  $"{src.MetricLabel ?? "This quantity"} has no expression equivalent, so it cannot be a goal. " +
                  "A measure line can compute what you need and be added as a goal by name."),
        };
    }

    private static GoalRange? Range(TraceGoalSource src)
    {
        if (src.Lo is not { } lo || src.Hi is not { } hi || !(hi >= lo)) return null;
        string u = src.AxisUnit.Length > 0 ? " " + src.AxisUnit : "";
        return new GoalRange { Axis = src.Axis, Lo = Number(lo) + u, Hi = Number(hi) + u };
    }

    /// <summary>Six significant figures — a range edge or a marker reading, not a stored value.</summary>
    private static string Number(double v)
        => Math.Round(v, 12).ToString("G6", CultureInfo.InvariantCulture);
}
