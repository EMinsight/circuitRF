// ================================================================
//  TraceRowViewModel.Statistics.cs  —  the trace card's Statistics menu
//  (brief-yield-8 R-ya8-2, R-ya8-4)
//
//  Every entry is TraceStatistics.Build (src/Render) applied to this
//  card's trace: the menu writes an ORDINARY expression into the trace
//  and sets its draw style, and the card's spec box then shows what was
//  done. `circuitrf plot --trace …,stat=` calls the same Build, so the
//  window and the CLI cannot rewrite a trace differently.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Expressions;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>One entry of the Statistics menu: a command, a submenu, or both absent for a disabled label.</summary>
public sealed record StatisticsMenuEntry(
    string Header,
    Action? Run = null,
    IReadOnlyList<StatisticsMenuEntry>? Children = null,
    bool Checked = false);

public partial class TraceRowViewModel
{
    /// <summary>The card offers the Statistics menu on a Rect plot, for a trace that reads a cube.</summary>
    public bool ShowStatisticsMenu => IsRectPlot && _trace.IsCubeBound && !_trace.IsWspTrace;

    /// <summary>The DataSet this trace is drawn from, or null when its source is not loaded.</summary>
    private DataSet? StatisticsSource()
    {
        string? path = _trace.SourcePath is { } sp ? System.IO.Path.GetFullPath(sp) : null;
        return _parent.LibraryEntries.FirstOrDefault(e =>
            string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))?.Data;
    }

    /// <summary>
    /// The menu as it stands for this trace: Histogram (count or percent), CDF and Normal Quantile — over the
    /// <c>trial</c> axis when the trace has one, else one submenu per axis — then Yield Sensitivity vs each
    /// statistical variable when the source scores goals, the Normal Fit toggle on a histogram, and Back to Curves on
    /// a trace the menu rewrote.
    /// </summary>
    public IReadOnlyList<StatisticsMenuEntry> StatisticsMenu()
    {
        var entries = new List<StatisticsMenuEntry>();
        if (StatisticsSource() is not { } ds) return entries;

        IReadOnlyList<StatisticsMenuEntry> Over(string axis) =>
        [
            new("Histogram",               () => ApplyStatistic(TraceStatistic.Histogram, axis)),
            new("Histogram (Percent)",     () => ApplyStatistic(TraceStatistic.Histogram, axis, percent: true)),
            new("CDF",                     () => ApplyStatistic(TraceStatistic.Cdf, axis)),
            new("Normal Quantile",         () => ApplyStatistic(TraceStatistic.Quantile, axis)),
        ];

        if (!TraceStatistics.IsAddedSeries(_trace))
        {
            var axes = TraceStatistics.Axes(_trace);
            if (axes.Contains(Evaluator.TrialAxis)) entries.AddRange(Over(Evaluator.TrialAxis));
            else if (axes.Count == 1)               entries.AddRange(Over(axes[0]));
            else foreach (var a in axes)            entries.Add(new($"Over {a}", Children: Over(a)));

            var stats = TraceStatistics.StatSpecs(ds);
            if (TraceStatistics.PassSpec(ds) is not null && stats.Count > 0)
                entries.Add(new("Yield Sensitivity vs", Children:
                    [.. stats.Select(s => new StatisticsMenuEntry(s[(s.IndexOf("stat:", StringComparison.Ordinal) + 5)..],
                                                                  () => ApplyStatistic(TraceStatistic.YieldSensitivity, null, statSpec: s)))]));

            if (_trace.Expression is { } e && e.TrimStart().StartsWith("histogram(", StringComparison.Ordinal))
                entries.Add(new("Normal Fit", ToggleNormalFit, Checked: _trace.ShowNormalFit));
        }
        if (_trace.StatisticsOrigin is not null) entries.Add(new("Back to Curves", BackToCurves));
        return entries;
    }

    /// <summary>Rewrites this trace as one menu entry does. Nothing changes when the entry cannot apply.</summary>
    public void ApplyStatistic(TraceStatistic kind, string? axis, bool percent = false, string? statSpec = null)
    {
        if (StatisticsSource() is not { } ds) return;
        var (rewrite, _) = TraceStatistics.Build(_trace, ds, kind, axis, percent: percent, statSpec: statSpec);
        if (rewrite is null) return;

        // A yield sensitivity's companion is added once: a second rewrite replaces the one already there.
        foreach (var old in _parent.Traces.Where(r => !ReferenceEquals(r, this) && TraceStatistics.IsAddedSeries(r.Trace)
                     && string.Equals(r.Trace.SourcePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase)).ToList())
            _parent.RemoveTrace(old);

        TraceStatistics.Apply(_trace, rewrite);
        if (rewrite.Companion is { } companion)
            _parent.AddResolvedTrace(TraceStatistics.CompanionOf(_trace, companion));
        AfterStatisticsChange();
    }

    /// <summary>Turns the fitted normal curve over this histogram on or off (R-ya8-4).</summary>
    public void ToggleNormalFit()
    {
        _trace.ShowNormalFit = !_trace.ShowNormalFit;
        _parent.RebuildAndNotify();
    }

    /// <summary>Restores the trace exactly as it was before the first menu entry, removing any series the menu added.</summary>
    public void BackToCurves()
    {
        var removed = TraceStatistics.BackToCurves(_parent.Plot, _trace);
        foreach (var t in removed)
            if (_parent.Traces.FirstOrDefault(r => ReferenceEquals(r.Trace, t)) is { } vm) _parent.RemoveTrace(vm);
        if (removed.Any(t => ReferenceEquals(t, _trace))) return;
        AfterStatisticsChange();
    }

    private void AfterStatisticsChange()
    {
        _parent.RebuildAndNotify();
        if (_trace.CubeName is not null) RebuildSignals();
        else                             RebuildAxisRoles();
        SyncVersusFromTrace();
        OnPropertyChanged(nameof(IsCubeBoundTrace));
    }
}
