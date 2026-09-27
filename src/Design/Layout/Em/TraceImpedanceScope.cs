// What an Impedance Analysis reviews, and the review saved on the layout (brief-impedance-2). The field
// report's two boards held every trace to 50 Ω because the analysis had no idea which traces were meant
// to be controlled; on both, trace WIDTH alone separated the RF path from the routing, and width is how
// a fabrication drawing's impedance note is written ("457 µm on L1: 50 Ω controlled"), so width is the
// first thing a scope can say.

using System.Globalization;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// Which traces an Impedance Analysis reviews.
///
/// <para><b>A trace is in scope when its layer is analysed, AND (no selector is set OR it matches any
/// selector), AND (no width class is set for its layer OR its dominant width is in one).</b> Widths are
/// a FILTER; regions, picks and nets — brief-impedance-4, which add <c>Regions</c>, <c>Picks</c> and
/// <c>Nets</c> here — are SELECTORS. Until those exist, the selector clause is always true.</para>
///
/// <para><b>Scope selects TRACES, never COPPER.</b> Every cross-section still sees every conductor on
/// every layer; scope decides only which traces are cut, solved and reported.</para>
/// </summary>
public sealed class TraceImpedanceScope
{
    /// <summary>The width classes under review, per layer; empty means every width on every layer.</summary>
    public List<TraceWidthSelector> Widths { get; set; } = [];

    /// <summary>Whether nothing is selected or filtered — the same as no scope at all.</summary>
    public bool IsEmpty => Widths.Count == 0;

    /// <summary>The width classes set for <paramref name="layerName"/>, by name, ignoring case.</summary>
    public IEnumerable<TraceWidthSelector> WidthsOn(string layerName) =>
        Widths.Where(w => string.Equals(w.LayerName, layerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a trace on <paramref name="layerName"/> whose dominant width is
    /// <paramref name="widthMicrons"/> is in scope.</summary>
    public bool Includes(string layerName, double widthMicrons)
    {
        bool any = false;
        foreach (var w in WidthsOn(layerName))
        {
            any = true;
            if (w.Matches(widthMicrons)) return true;
        }
        return !any;
    }

    public TraceImpedanceScope Clone() => new() { Widths = [.. Widths] };
}

/// <summary>One width class under review: traces on <paramref name="LayerName"/> whose dominant width is
/// within <paramref name="ToleranceMicrons"/> of <paramref name="NominalMicrons"/>. The layer is named,
/// as the CLI's <c>--layers</c> names it.</summary>
public sealed record TraceWidthSelector(string LayerName, double NominalMicrons, double ToleranceMicrons)
{
    public bool Matches(double widthMicrons) => Math.Abs(widthMicrons - NominalMicrons) <= ToleranceMicrons + 1e-6;

    /// <summary>A selector for a width typed by hand (<c>--width Top=457</c>): the survey's own merge
    /// tolerance around it.</summary>
    public static TraceWidthSelector Around(string layerName, double nominalMicrons) =>
        new(layerName, nominalMicrons, TraceImpedanceAnalysis.MergeToleranceMicrons(nominalMicrons));
}

/// <summary>
/// The Impedance Analysis settings and scope, saved on the layout (<see cref="LayoutView.ImpedanceReview"/>)
/// so a re-run — in the editor or headlessly — reviews what the last one reviewed.
///
/// <para><b>Review state, not artwork</b>: changing it marks the layout dirty and is not undoable, on
/// <see cref="LayoutView.DrcWaivers"/>' terms.</para>
/// </summary>
public sealed class TraceImpedanceReview
{
    public double TargetOhms { get; set; } = TraceImpedanceOptions.DefaultTargetOhms;
    public double TolerancePercent { get; set; } = TraceImpedanceOptions.DefaultTolerancePercent;
    public double WarningPercent { get; set; } = TraceImpedanceOptions.DefaultWarningPercent;

    /// <summary>The highest frequency the traces carry, Hz, or null for off.</summary>
    public double? MaxFrequencyHz { get; set; }

    /// <summary>The copper layers analysed, BY NAME; null for every copper layer.</summary>
    public List<string>? Layers { get; set; }

    /// <summary>Which traces are reviewed; null for every trace.</summary>
    public TraceImpedanceScope? Scope { get; set; }

    /// <summary>Whether <paramref name="other"/> says the same thing — so a dialog closed with nothing
    /// changed does not mark the layout dirty. A null or empty scope and a null layer list are the
    /// same as their absence; <paramref name="other"/> null is the default review.</summary>
    public bool SameAs(TraceImpedanceReview? other)
    {
        other ??= new TraceImpedanceReview();
        static bool SameList<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
            (a is null || a.Count == 0) ? (b is null || b.Count == 0) : b is not null && a.SequenceEqual(b);
        return TargetOhms == other.TargetOhms && TolerancePercent == other.TolerancePercent
            && WarningPercent == other.WarningPercent && MaxFrequencyHz == other.MaxFrequencyHz
            && SameList(Layers, other.Layers) && SameList(Scope?.Widths, other.Scope?.Widths);
    }
}

// ── the survey ──────────────────────────────────────────────────────────────────────────────────

/// <summary>What <see cref="TraceImpedanceAnalysis.Survey(IReadOnlyList{LayoutShape}, Technology, int,
/// TraceImpedanceOptions, CircuitRF.Engine.RunControl?)"/> found: the traces on each layer grouped by
/// width, with nothing solved but one typical cut per class.</summary>
public sealed record TraceWidthSurvey
{
    /// <summary>Why nothing was surveyed, or null.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    public IReadOnlyList<TraceWidthLayer> Layers { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The typical-Z0 cuts solved — one per class.</summary>
    public int SolveCount { get; init; }

    public TimeSpan Elapsed { get; init; }

    internal static TraceWidthSurvey Refused(string why) => new() { Refusal = why };
}

/// <summary>One surveyed layer.</summary>
public sealed record TraceWidthLayer
{
    public LayerKey Layer { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Narrowest first.</summary>
    public IReadOnlyList<TraceWidthClass> Classes { get; init; } = [];
    public int PoursSkipped { get; init; }
}

/// <summary>
/// The traces on one layer whose dominant widths are within max(1 %, 1 µm) of one another. Widths and
/// lengths in µm.
/// </summary>
public sealed record TraceWidthClass
{
    /// <summary>The length-weighted mean of the dominant widths merged.</summary>
    public double NominalMicrons { get; init; }
    public double MinMicrons { get; init; }
    public double MaxMicrons { get; init; }
    public int TraceCount { get; init; }
    public double TotalLengthMicrons { get; init; }

    /// <summary>ONE cross-section at the middle of the class's longest trace — "typical", and said so
    /// wherever it is shown: it is not the class's Z0, which the analysis measures trace by trace.</summary>
    public double? TypicalZ0 { get; init; }

    /// <summary>Why there is no typical Z0, or null.</summary>
    public string? TypicalRefusal { get; init; }

    /// <summary>The selector that picks exactly this class on <paramref name="layerName"/>: its nominal
    /// with a tolerance wide enough to take every width merged into it.</summary>
    public TraceWidthSelector Selector(string layerName) => new(
        layerName, Math.Round(NominalMicrons, 3),
        Math.Round(Math.Max(TraceImpedanceAnalysis.MergeToleranceMicrons(NominalMicrons),
                            Math.Max(NominalMicrons - MinMicrons, MaxMicrons - NominalMicrons)), 3));

    /// <summary>"457 µm", "99.5–101 µm".</summary>
    public string WidthText => MaxMicrons - MinMicrons < 0.05
        ? $"{Um(NominalMicrons)} µm" : $"{Um(MinMicrons)}–{Um(MaxMicrons)} µm";

    private static string Um(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}

// ── choosing a scope from a survey ──────────────────────────────────────────────────────────────

/// <summary>One row of the width list a reviewer ticks: a surveyed class, or a class saved in the scope
/// that the survey no longer finds (<see cref="Class"/> null) — kept and shown, never silently dropped
/// (R-imp2-3c), because the artwork changing under a saved scope is exactly what the reviewer must
/// see.</summary>
public sealed record TraceWidthRow(string LayerName, TraceWidthClass? Class, TraceWidthSelector Selector, bool Ticked)
{
    public bool Missing => Class is null;
}

/// <summary>The rows a survey and a saved scope make, and the scope ticked rows make — kept out of any
/// window so the dialog and the panel that replaces it (brief-impedance-3) share them.</summary>
public static class TraceWidthRows
{
    /// <summary>Every surveyed class, ticked when the saved scope selects it — none is ticked from its
    /// Z0 (R-imp2-1c: a trace drawn at the wrong width is what the review exists to catch) — then every
    /// saved class that matches nothing surveyed, ticked, as a missing row.</summary>
    public static List<TraceWidthRow> Merge(TraceWidthSurvey survey, TraceImpedanceScope? saved)
    {
        var rows = new List<TraceWidthRow>();
        foreach (var layer in survey.Layers)
            foreach (var c in layer.Classes)
                rows.Add(new TraceWidthRow(layer.Name, c, c.Selector(layer.Name),
                    saved?.WidthsOn(layer.Name).Any(w => w.Matches(c.NominalMicrons)) == true));

        foreach (var w in saved?.Widths ?? [])
        {
            var layer = survey.Layers.FirstOrDefault(l => string.Equals(l.Name, w.LayerName, StringComparison.OrdinalIgnoreCase));
            if (layer is null || !layer.Classes.Any(c => w.Matches(c.NominalMicrons)))
                rows.Add(new TraceWidthRow(w.LayerName, null, w, true));
        }
        return rows;
    }

    /// <summary>The scope the ticked rows say: each ticked row's selector.</summary>
    public static TraceImpedanceScope ScopeOf(IEnumerable<TraceWidthRow> ticked) =>
        new() { Widths = [.. ticked.Select(r => r.Selector)] };
}
