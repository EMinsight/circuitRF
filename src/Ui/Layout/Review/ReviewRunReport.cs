// What a Layout Review run says in the Messages panel (brief-review-6 R-rev6-7), on DrcRunReport's terms:
// the run's diagnostics first, then ONE line — the counts, and where the findings are.

using CircuitRF.Design.Layout.Review;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Layout;

public static class ReviewRunReport
{
    /// <summary>"Review: 3 checks with findings (41 findings, 6 accepted), 1 not run — see the Review panel."</summary>
    public static string Verdict(LayoutReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int withFindings = result.Checks.Count(c => c.State != ReviewCheckState.NotRun && c.FindingCount > 0);
        int findings = result.Checks.Sum(c => c.FindingCount);
        int accepted = result.Checks.Sum(c => c.AcceptedCount);
        int notRun = result.Checks.Count(c => c.State == ReviewCheckState.NotRun);
        int ran = result.Checks.Count(c => c.State == ReviewCheckState.Ran);

        static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";
        string text = findings == 0
            ? $"Review: no findings from {N(ran, "check", "checks")}"
            : $"Review: {N(withFindings, "check", "checks")} with findings ({N(findings, "finding", "findings")}"
              + (accepted > 0 ? $", {accepted:N0} accepted)" : ")");
        if (notRun > 0) text += $", {notRun} not run";
        if (result.Cancelled)
            text += $" (cancelled: {result.Checks.Count(c => c.State != ReviewCheckState.Cancelled)} of {result.Checks.Count} checks finished)";
        return text + (findings > 0 || notRun > 0 ? " — see the Review panel." : ".");
    }

    /// <summary>Posts one run's diagnostics and its verdict. Null sink posts nothing.</summary>
    public static void Post(IMessageSink? messages, LayoutReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (messages is null) return;
        foreach (var d in result.Diagnostics) messages.Warning($"Review — {d}");
        string verdict = Verdict(result);
        if (result.Checks.Sum(c => c.FindingCount - c.AcceptedCount) == 0 && !result.Cancelled) messages.Success(verdict);
        else messages.Warning(verdict);
    }
}
