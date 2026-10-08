// Which line element a stretch of trace becomes — brief-artsch-5-traces-to-line-elements.md R-as5-2, overview
// D13; docs/design/artwork-to-schematic.md §6.2.
//
// The trace review names every cut's configuration ("microstrip", "grounded coplanar waveguide", "stripline",
// …) with its own coplanar threshold — a side gap of at most 3·H. Recognition re-reads the same cut from the
// numbers the review returns (the references either side, both side gaps, H) so the user's coplanar choice and
// gap factor apply without touching the review:
//
//   reference below, nothing above   microstrip family   → MLIN, or CPWG where both gaps read coplanar
//   a reference both sides           stripline           → SLIN, or TLIN where a side gap reads coplanar
//   a reference above only           → TLIN ("reference above")
//   no reference                     → TLIN ("no ground plane")
//
// and then asks the stackup whether the component can be bound at all: a line the injection cannot give a
// substrate is a TLIN that says why, never a component that would simulate on a fallback board.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>How a trace whose side ground comes close is read — R-as5-2's option.</summary>
public enum CoplanarReading
{
    /// <summary>GCPW where both side gaps are at most <see cref="RecognitionOptions.CoplanarGapFactor"/>·H.</summary>
    Auto,

    /// <summary>Every grounded-coplanar stretch is read as microstrip (the gaps are still recorded).</summary>
    Microstrip,

    /// <summary>Every microstrip stretch with a measured gap on both sides is read as GCPW.</summary>
    Gcpw,
}

/// <summary>The four line components recognition writes.</summary>
public enum LineKind { Mlin, Cpwg, Slin, Tlin }

/// <summary>One cut's reading: its line kind, and for a TLIN why.</summary>
/// <param name="Kind">The component.</param>
/// <param name="Reason">Why it is a TLIN — D13's table, in the review's own words — or null.</param>
/// <param name="Solved">Whether the cut had a Z0.</param>
public readonly record struct LineReading(LineKind Kind, string? Reason, bool Solved);

/// <summary>D13, written once.</summary>
public static class LineTypeChoice
{
    /// <summary>The fallback reason of a segment none of whose cuts was solved.</summary>
    public const string UnsolvedReason = "no cut along it could be solved";

    /// <summary>The fallback reason of a coplanar line whose two gaps differ by more than <see cref="MaxGapRatio"/>.</summary>
    public const string AsymmetricReason = "its two coplanar gaps differ by more than 1.5×";

    /// <summary>A CPWG's two gaps may differ by this factor; more and the line is a TLIN (D13).</summary>
    public const double MaxGapRatio = 1.5;

    /// <summary>
    /// What one cut is, under <paramref name="options"/>, before the stackup is asked: <see cref="LineKind"/>
    /// and the review's configuration name re-derived with the option's gap factor.
    /// </summary>
    public static (LineKind Kind, string Configuration) Read(TraceStation station, RecognitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(options);
        bool below = station.ReferenceBelow is not null, above = station.ReferenceAbove is not null;
        double reach = options.CoplanarGapFactor * (station.H ?? 0);
        bool left = station.GapLeft is { } gl && gl <= reach, right = station.GapRight is { } gr && gr <= reach;
        bool bothGaps = station.GapLeft is not null && station.GapRight is not null;

        return (below, above) switch
        {
            (true, true) => left || right
                ? (LineKind.Tlin, "stripline with coplanar ground")
                : (LineKind.Slin, "stripline"),
            (true, false) => options.Coplanar switch
            {
                CoplanarReading.Microstrip => (LineKind.Mlin, left || right ? "microstrip with coplanar ground" : "microstrip"),
                CoplanarReading.Gcpw when bothGaps => (LineKind.Cpwg, "grounded coplanar waveguide"),
                _ when left && right && options.Coplanar == CoplanarReading.Auto => (LineKind.Cpwg, "grounded coplanar waveguide"),
                _ => (LineKind.Mlin, left || right ? "microstrip with coplanar ground on one side" : "microstrip"),
            },
            (false, true) => (LineKind.Tlin, left || right ? "grounded coplanar waveguide (reference above)" : "microstrip (reference above)"),
            _ => (LineKind.Tlin, "coplanar waveguide (no ground plane)"),
        };
    }

    /// <summary>
    /// One cut's reading, the stackup asked: an MLIN or CPWG the stackup cannot give a substrate between the
    /// trace's layer and its measured reference, or a SLIN it cannot give two planes, is a TLIN naming why.
    /// </summary>
    public static LineReading Of(TraceStation station, RecognitionOptions options, LineBinder binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        var (kind, configuration) = Read(station, options);
        bool solved = station.Z0 is not null;
        if (kind == LineKind.Tlin) return new LineReading(kind, configuration, solved);
        if (binder.Refusal(kind, station.ReferenceBelow) is { } why)
            return new LineReading(LineKind.Tlin, $"the stackup cannot bind {Name(kind)} here: {why}", solved);
        return new LineReading(kind, null, solved);
    }

    /// <summary>The component's name.</summary>
    public static string Name(LineKind kind) => kind switch
    {
        LineKind.Mlin => "MLIN",
        LineKind.Cpwg => "CPWG",
        LineKind.Slin => "SLIN",
        _ => "TLIN",
    };
}

/// <summary>
/// Whether the stackup injection can bind a line on one conductor — the question asked of
/// <see cref="SubstrateResolver"/>, which is what the schematic's injection asks at extraction, answered once
/// per (component, reference) and remembered.
/// </summary>
public sealed class LineBinder
{
    private readonly Technology _tech;
    private readonly Dictionary<(LineKind, string?), string?> _answers = [];

    /// <param name="tech">The artwork's technology.</param>
    /// <param name="signalConductor">The stackup conductor the trace is on.</param>
    public LineBinder(Technology tech, string signalConductor)
    {
        _tech = tech;
        SignalConductor = signalConductor;
    }

    /// <summary>The stackup conductor the trace is on.</summary>
    public string SignalConductor { get; }

    /// <summary>The measured reference as a ground override: a stackup conductor's name, or null for one the
    /// review took from the stackup's own bottom.</summary>
    public string? GroundOverride(string? reference) =>
        reference is not null && _tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Conductor
            && string.Equals(l.Name, reference, StringComparison.OrdinalIgnoreCase)) ? reference : null;

    /// <summary>Why <paramref name="kind"/> cannot be bound with <paramref name="reference"/> under it, or null.</summary>
    public string? Refusal(LineKind kind, string? reference)
    {
        var key = (kind, reference);
        if (_answers.TryGetValue(key, out var known)) return known;
        string? why = kind switch
        {
            LineKind.Slin => SubstrateResolver.ResolveStripline(_tech, SignalConductor).Failure?.Reason,
            LineKind.Mlin or LineKind.Cpwg => SubstrateResolver.ResolveElectrical(
                _tech, new PCellLayerSelection(SignalConductor, GroundOverride(reference))).Failure?.Reason,
            _ => null,
        };
        _answers[key] = why;
        return why;
    }

    /// <summary>The dielectric and the copper a TLIN's loss is estimated from: the stackup between the trace and
    /// its reference (either side), else the trace's default substrate; null when the stackup gives neither.</summary>
    public (double Er, double TanD, double Sigma)? Substrate(string? referenceBelow, string? referenceAbove)
    {
        if (referenceBelow is not null && referenceAbove is not null
            && SubstrateResolver.ResolveStripline(_tech, SignalConductor).Stripline is { } s)
            return (s.RelativePermittivity, s.LossTangent, s.ConductivitySPerM);
        foreach (var reference in new[] { referenceBelow ?? referenceAbove, null })
            if (SubstrateResolver.ResolveElectrical(_tech, new PCellLayerSelection(SignalConductor, GroundOverride(reference)))
                    .Substrate is { } m)
                return (m.RelativePermittivity, m.LossTangent, m.ConductivitySPerM);
        return null;
    }

    /// <summary>The substrate height under the trace, metres — what the optimal miter is measured against.</summary>
    public double? HeightMeters(string? reference) =>
        SubstrateResolver.ResolveElectrical(_tech, new PCellLayerSelection(SignalConductor, GroundOverride(reference)))
            .Substrate?.HeightMeters;
}
