// Which review checks run and each check's thresholds (brief-review-2 R-rev2-6). Every check states
// its own thresholds in ONE table — id, title, kind, default — so the Review panel, `circuitrf review
// --list-checks` and the MCP tool list them from the same rows; briefs 3–5 add theirs.
//
// Saved on the layout as LayoutView.Review, beside ImpedanceReview and on its terms: absent means the
// defaults, only a threshold that DIFFERS from its default is written, and an id this build does not
// know is kept on read and written back, so a newer build's settings survive an older build's save.

using System.Globalization;

namespace CircuitRF.Design.Layout.Review;

/// <summary>
/// What is being reviewed: a printed circuit board or a MMIC die. Each check belongs to one or both
/// (<see cref="ReviewCheckDef.Targets"/>), and a review runs only the checks of its target — a PCB check asked of
/// a die reports nothing useful and its fix sentence (add a soldermask, a board outline) is wrong advice there.
/// Saved on the layout; absent is <see cref="Pcb"/>, what every review was before MMIC checks existed.
/// </summary>
public enum ReviewTarget { Pcb, Mmic }

/// <summary>What a threshold measures: a length (stored in DBU, stated in SI), a count, an area (stored in mm², said
/// in the layout's unit squared), or a number of substrate heights — a length that scales with the stackup, so one
/// default suits a 100 µm die and a 1.6 mm board. Only a length is converted on storing; the others are stored as
/// stated. There is no density: one has no readable spelling in a small unit, so a check that needs one asks for a
/// pitch (a length) instead.</summary>
public enum ReviewThresholdKind { Length, Count, Area, Heights }

/// <summary>One threshold of one check.</summary>
/// <param name="Default">In SI: metres for a <see cref="ReviewThresholdKind.Length"/>; mm² for an
/// area, a number of heights, a plain number for a count. Converted to DBU through the layout's <c>DbuPerMicron</c>, so a threshold never silently
/// takes a different value on a board of a different resolution.</param>
public sealed record ReviewThresholdDef(string Id, string Title, ReviewThresholdKind Kind, double Default)
{
    /// <summary>The default as stored: DBU for a length, the number itself otherwise.</summary>
    public double DefaultStored(int dbuPerMicron) =>
        Kind == ReviewThresholdKind.Length ? Math.Round(Default * 1e6 * dbuPerMicron) : Default;
}

/// <summary>One built-in check: its id, the words a reviewer reads, and its thresholds.</summary>
public sealed record ReviewCheckDef(string Id, string Title, string Description, IReadOnlyList<ReviewThresholdDef> Thresholds)
{
    /// <summary>Where this check's settings and acceptances live when they are not the review's — the
    /// Impedance review's, for <c>impedance</c> (brief-review-5 R-rev5-4/5) — or null. Such a check has no
    /// thresholds of its own, only the enable flag is the review's, and its findings are accepted there:
    /// <see cref="ReviewAcceptance.Apply"/> never matches a review acceptance against them.</summary>
    public string? SettingsOwner { get; init; }

    /// <summary>What this check reviews: a board, a die, or both. A review runs only its target's checks.</summary>
    public IReadOnlyList<ReviewTarget> Targets { get; init; } = [ReviewTarget.Pcb];

    public bool AppliesTo(ReviewTarget target) => Targets.Contains(target);

    /// <summary>A finding is between a conductor and the next one below it, and names the upper one — the report
    /// draws it on the upper layer with the lower one's copper outlined (brief-review-7 R-rev7-2).</summary>
    public bool SpansAdjacentCopper { get; init; }

    public ReviewThresholdDef? Threshold(string id) =>
        Thresholds.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// The review's settings — saved on <see cref="LayoutView.Review"/>, and what a run is handed.
/// <b>Review state, not artwork</b>: changing it marks the layout dirty and is not undoable.
/// </summary>
public sealed class ReviewSettings
{
    /// <summary>Check ids turned off. Null or empty: every check runs. An id this build does not know
    /// is kept.</summary>
    public List<string>? Disabled { get; set; }

    /// <summary>Thresholds that differ from their default, keyed <c>check-id.threshold-id</c>: DBU for a
    /// length, the number itself for a count or density. A key this build does not know is kept.</summary>
    public Dictionary<string, double>? Thresholds { get; set; }

    /// <summary>What is reviewed. Null is <see cref="ReviewTarget.Pcb"/>, and is what a PCB review saves, so a
    /// layout reviewed as a board before this existed reads the same.</summary>
    public ReviewTarget? Target { get; set; }

    public ReviewTarget TargetOrDefault => Target ?? ReviewTarget.Pcb;

    public static string KeyOf(string checkId, string thresholdId) => $"{checkId}.{thresholdId}";

    public bool IsEnabled(string checkId) =>
        Disabled is null || !Disabled.Contains(checkId, StringComparer.Ordinal);

    public void SetEnabled(string checkId, bool enabled)
    {
        if (enabled) { Disabled?.RemoveAll(id => string.Equals(id, checkId, StringComparison.Ordinal)); return; }
        Disabled ??= [];
        if (!Disabled.Contains(checkId, StringComparer.Ordinal)) Disabled.Add(checkId);
    }

    /// <summary>The threshold's value as stored — the saved one, else the default.</summary>
    public double Value(ReviewCheckDef check, ReviewThresholdDef threshold, int dbuPerMicron) =>
        Thresholds is not null && Thresholds.TryGetValue(KeyOf(check.Id, threshold.Id), out double v)
            ? v : threshold.DefaultStored(dbuPerMicron);

    /// <summary>Sets a threshold, stored units. Setting it to its default removes it.</summary>
    public void Set(ReviewCheckDef check, ReviewThresholdDef threshold, double value, int dbuPerMicron)
    {
        string key = KeyOf(check.Id, threshold.Id);
        if (value == threshold.DefaultStored(dbuPerMicron)) { Thresholds?.Remove(key); return; }
        Thresholds ??= [];
        Thresholds[key] = value;
    }

    public ReviewSettings Clone() => new()
    {
        Disabled   = Disabled is null ? null : [.. Disabled],
        Thresholds = Thresholds is null ? null : new Dictionary<string, double>(Thresholds),
        Target     = Target,
    };

    /// <summary>
    /// What the <c>.clay</c> holds: the known thresholds equal to their default dropped, everything
    /// unknown kept, keys sorted so the file does not churn — or null when nothing is left, so a layout
    /// at the defaults writes no <c>Review</c> at all.
    /// </summary>
    public ReviewSettings? ForSave(int dbuPerMicron)
    {
        Dictionary<string, double>? thresholds = null;
        if (Thresholds is not null)
            foreach (var (key, value) in Thresholds.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (LayoutReview.FindThreshold(key) is { } known
                    && value == known.Threshold.DefaultStored(dbuPerMicron)) continue;
                (thresholds ??= [])[key] = value;
            }
        List<string>? disabled = Disabled is { Count: > 0 } d ? [.. d.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] : null;
        ReviewTarget? target = TargetOrDefault == ReviewTarget.Pcb ? null : TargetOrDefault;
        return thresholds is null && disabled is null && target is null
            ? null : new ReviewSettings { Disabled = disabled, Thresholds = thresholds, Target = target };
    }

    /// <summary>A stored value as a reviewer reads it: a length in <paramref name="unit"/>, else the number.</summary>
    public static string Display(ReviewThresholdDef threshold, double stored, LayoutUnit unit, int dbuPerMicron) =>
        threshold.Kind switch
        {
            ReviewThresholdKind.Length  => ReviewText.Length((long)Math.Round(stored), unit, dbuPerMicron),
            ReviewThresholdKind.Area    => ReviewText.Area(stored, unit),
            ReviewThresholdKind.Heights => $"{stored.ToString("0.###", CultureInfo.InvariantCulture)} × h",
            _                           => stored.ToString("0.###", CultureInfo.InvariantCulture),
        };
}
