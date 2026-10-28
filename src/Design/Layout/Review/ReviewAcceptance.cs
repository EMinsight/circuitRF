// Accepted review findings (brief-review-2 R-rev2-7) — on the terms of DrcWaivers and the Impedance
// review's acceptances: a statement about THIS artwork, saved on the layout, keyed by place, and gone
// when the place changes. They are their own list: a review acceptance is not a DRC waiver and not an
// Impedance acceptance, and each list belongs to its own run.

namespace CircuitRF.Design.Layout.Review;

/// <summary>One accepted finding, saved in <see cref="LayoutView.ReviewAcceptances"/>.</summary>
public sealed class ReviewAcceptance
{
    /// <summary><see cref="ReviewFinding.Key"/> of the finding accepted.</summary>
    public string Key { get; set; } = "";

    public string CheckId { get; set; } = "";

    /// <summary>Why. Required: this is the sentence a reader of a clean review relies on.</summary>
    public string Reason { get; set; } = "";

    public DateTime AcceptedUtc { get; set; }

    public string LayerName { get; set; } = "";

    /// <summary>The finding's sentence when it was accepted, so a stale acceptance can still be
    /// recognised and removed — <c>DrcWaiver.RuleName</c>'s role.</summary>
    public string Summary { get; set; } = "";

    /// <summary>The acceptance of <paramref name="finding"/> with <paramref name="reason"/>. An empty
    /// reason throws.</summary>
    public static ReviewAcceptance For(ReviewFinding finding, string layerName, string reason, DateTime acceptedUtc)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An acceptance needs a reason.", nameof(reason));
        return new ReviewAcceptance
        {
            Key = finding.Key, CheckId = finding.CheckId, Reason = reason.Trim(), AcceptedUtc = acceptedUtc,
            LayerName = layerName, Summary = finding.Sentence,
        };
    }

    /// <summary>
    /// <paramref name="result"/> with <paramref name="acceptances"/> applied: each Warning they match
    /// marked accepted, each check row's accepted count recounted, and the acceptances matching nothing
    /// listed as <see cref="LayoutReviewResult.StaleAcceptances"/>. Starts from the findings as the run
    /// made them, whatever was applied before, so a panel re-applies after every Accept with no re-run;
    /// null or empty gives the result as if there were none. An acceptance whose check did not run this
    /// time is neither applied nor stale — nothing was looked at to say it no longer matches.
    /// </summary>
    public static LayoutReviewResult Apply(LayoutReviewResult result, IReadOnlyList<ReviewAcceptance>? acceptances)
    {
        ArgumentNullException.ThrowIfNull(result);
        acceptances ??= [];
        var byKey = new Dictionary<string, ReviewAcceptance>(StringComparer.Ordinal);
        foreach (var a in acceptances) byKey.TryAdd(a.Key, a);
        var matched = new HashSet<ReviewAcceptance>(ReferenceEqualityComparer.Instance);

        var findings = new List<ReviewFinding>(result.Findings.Count);
        foreach (var found in result.Findings)
        {
            // A check whose findings are accepted elsewhere keeps what its run applied, and a review
            // acceptance never covers one of them (brief-review-5 R-rev5-4).
            bool elsewhere = LayoutReview.FindCheck(found.CheckId)?.SettingsOwner is not null;
            var f = found with { Accepted = elsewhere ? found.RunAccepted : null };
            if (!elsewhere && f.Severity == ReviewSeverity.Warning && byKey.TryGetValue(f.Key, out var a))
            {
                matched.Add(a);
                f = f with { Accepted = a };
            }
            findings.Add(f);
        }

        var rows = result.Checks.Select(r => r with
        {
            AcceptedCount = findings.Count(f => f.IsAccepted && f.CheckId == r.Id),
        }).ToList();

        var ran = result.Checks.Where(r => r.State == ReviewCheckState.Ran).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var stale = acceptances.Where(a => !matched.Contains(a) && ran.Contains(a.CheckId)).ToList();
        return result with { Findings = findings, Checks = rows, StaleAcceptances = stale };
    }
}
