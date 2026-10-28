// Which review checks run and each check's thresholds (brief-review-2 R-rev2-6). Every check states
// its own thresholds in ONE table — id, title, kind, default — so the Review panel, `circuitrf review
// --list-checks` and the MCP tool list them from the same rows; briefs 3–5 add theirs.
//
// Saved on the layout as LayoutView.Review, beside ImpedanceReview and on its terms: absent means the
// defaults, only a threshold that DIFFERS from its default is written, and an id this build does not
// know is kept on read and written back, so a newer build's settings survive an older build's save.

using System.Globalization;

namespace CircuitRF.Design.Layout.Review;

/// <summary>What a threshold measures: a length (stored in DBU, stated in SI), a count, a density
/// (per mm²) or an area (mm²). Only a length is converted; the others are stored as stated.</summary>
public enum ReviewThresholdKind { Length, Count, Density, Area }

/// <summary>One threshold of one check.</summary>
/// <param name="Default">In SI: metres for a <see cref="ReviewThresholdKind.Length"/>; mm² for an
/// area, per mm² for a density, a plain number for a count. Converted to DBU through the layout's <c>DbuPerMicron</c>, so a threshold never silently
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
        return thresholds is null && disabled is null ? null : new ReviewSettings { Disabled = disabled, Thresholds = thresholds };
    }

    /// <summary>A stored value as a reviewer reads it: a length in <paramref name="unit"/>, else the number.</summary>
    public static string Display(ReviewThresholdDef threshold, double stored, LayoutUnit unit, int dbuPerMicron) =>
        threshold.Kind switch
        {
            ReviewThresholdKind.Length  => ReviewText.Length((long)Math.Round(stored), unit, dbuPerMicron),
            ReviewThresholdKind.Area    => $"{stored.ToString("0.###", CultureInfo.InvariantCulture)} mm²",
            ReviewThresholdKind.Density => $"{stored.ToString("0.###", CultureInfo.InvariantCulture)} per mm²",
            _                           => stored.ToString("0.###", CultureInfo.InvariantCulture),
        };
}
