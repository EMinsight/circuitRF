// ================================================================
//  YieldTrialRowViewModel.cs  —  the Yield panel's trial table
//  (brief-yield-10 R-ya10-7)
//
//  Rows are read from the run's DataSet — the one the CLI writes and
//  the Data Display draws — so a live table (from the published frame)
//  and a finished one are the same function of the same cubes:
//  trials.pass, trials.status and the reasons table, and each goal's
//  trials.goal:<g>:margin.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Ui.Yield;

/// <summary>One trial: its number, pass/fail, the goal it is tightest on and that margin, or why it did not evaluate.</summary>
public sealed record YieldTrialRowViewModel(int Trial, bool? Pass, string WorstGoal, double Margin, string? Reason)
{
    public bool IsPass => Pass == true;
    public bool IsFail => Pass == false;
    public bool DidNotEvaluate => Reason is not null;

    public string PassText => Reason is not null ? "—" : Pass switch { true => "pass", false => "fail", _ => "" };

    public string MarginText => double.IsFinite(Margin) ? Margin.ToString("G4", CultureInfo.InvariantCulture) : "";

    /// <summary>The rows of <paramref name="ds"/>'s trials, in trial order; empty when it is not a Monte Carlo result.</summary>
    public static IReadOnlyList<YieldTrialRowViewModel> From(DataSet ds)
    {
        const string g = "trials";
        if (!ds.ContainsGroup(g) || !ds.Contains($"{g}.status")) return [];
        var status = ds[$"{g}.status"];
        var trials = status.Axes[0].Values;
        double[]? pass = ds.Contains($"{g}.pass") ? ds[$"{g}.pass"].RealValues : null;
        string[] reasons = ds.Contains($"{g}.reasons") ? ds[$"{g}.reasons"].Axes[0].Labels ?? [] : [];
        var margins = ds.CubesIn(g)
            .Where(kv => kv.Key.StartsWith("goal:", StringComparison.Ordinal) && kv.Key.EndsWith(":margin", StringComparison.Ordinal))
            .Select(kv => (Goal: kv.Key["goal:".Length..^":margin".Length], Values: kv.Value.RealValues))
            .ToList();

        var rows = new List<YieldTrialRowViewModel>(trials.Length);
        for (int i = 0; i < trials.Length; i++)
        {
            int k = (int)Math.Round(status.RealValues[i]);
            string? reason = k > 0 ? (k - 1 < reasons.Length ? reasons[k - 1] : "did not evaluate") : null;
            bool? p = pass is null || double.IsNaN(pass[i]) ? null : pass[i] == 1;
            string worst = "";
            double margin = double.NaN;
            foreach (var (goal, values) in margins)
                if (double.IsFinite(values[i]) && !(values[i] >= margin)) { worst = goal; margin = values[i]; }
            rows.Add(new YieldTrialRowViewModel((int)Math.Round(trials[i]), reason is null ? p : false, worst, margin, reason));
        }
        return rows;
    }
}

/// <summary>How the trial table is ordered.</summary>
public enum TrialSort { Trial, Margin }
