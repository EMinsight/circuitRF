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
using CircuitRF.Design.Statistics;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>One entry of the Statistics menu: a command, a submenu, or both absent for a disabled label.</summary>
public sealed record StatisticsMenuEntry(
    string Header,
    Action? Run = null,
    IReadOnlyList<StatisticsMenuEntry>? Children = null,
    bool Checked = false,
    string? Tooltip = null);

public partial class TraceRowViewModel
{
    /// <summary>The card offers the Statistics menu on a Rect plot, for a trace that reads a cube — and on a Smith or
    /// Polar plot for a family, whose colour-by and nominal apply there too (brief-yield-9).</summary>
    public bool ShowStatisticsMenu => _trace.IsCubeBound && !_trace.IsWspTrace
                                      && (IsRectPlot || (_trace.IsFamily && _parent.PlotType is PlotType.Smith or PlotType.Polar));

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

        // A family on a Smith or Polar plot: the trial entries alone (brief-yield-9).
        if (!IsRectPlot)
        {
            entries.AddRange(TrialEntries(ds));
            return entries;
        }

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
        entries.AddRange(TrialEntries(ds));
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

    // ── the trial entries (brief-yield-9) ───────────────────────────────────────

    /// <summary>
    /// Colour By and the family's Envelope, Show Curves and Show Nominal (R-ya9-1…3); Scatter vs and Fit Line on a
    /// trace over trials (R-ya9-4); Contributions of each goal on a yield source (R-ya9-5). Each sets the trace through
    /// <see cref="TrialViews"/>, the functions <c>plot --trace</c> uses.
    /// </summary>
    private IEnumerable<StatisticsMenuEntry> TrialEntries(DataSet ds)
    {
        var colours = TrialViews.ColourByChoices(_trace, ds);
        if (colours.Count > 0)
            yield return new("Colour By", Children:
            [
                new("None", () => SetTrialView(t => t.ColorBy = null), Checked: _trace.ColorBy is null),
                .. colours.Select(c => new StatisticsMenuEntry(c.Label, () => SetTrialView(t => t.ColorBy = c.Value),
                                                               Checked: _trace.ColorBy == c.Value)),
            ]);

        if (_trace.IsFamily)
        {
            if (TrialViews.EnvelopeRefusal(_trace, _parent.PlotType) is { } why)
                yield return new("Envelope", Tooltip: why);
            else
            {
                yield return new("Envelope", Children:
                [
                    new("Off", () => SetTrialView(t => t.Envelope = TrialEnvelope.Off), Checked: !_trace.Envelope.IsOn),
                    .. TrialViews.EnvelopeChoices.Select(e => new StatisticsMenuEntry(e.Label,
                           () => SetTrialView(t => t.Envelope = e), Checked: _trace.Envelope == e)),
                ]);
                if (_trace.Envelope.IsOn)
                    yield return new("Show Curves", () => SetTrialView(t => t.ShowCurves = !t.ShowCurves), Checked: _trace.ShowCurves);
            }
            if (_trace.FamilyAxisName == CircuitRF.Core.Expressions.Evaluator.TrialAxis && ds.ContainsGroup(TrialResolve.NominalGroup))
                yield return new("Show Nominal", () => SetTrialView(t => t.ShowNominal = !t.ShowNominal), Checked: _trace.ShowNominal);
        }

        if (IsRectPlot && TrialViews.ScatterCandidates(_trace, ds) is { Count: > 0 } xs)
            yield return new("Scatter vs", Children:
                [.. xs.Select(x => new StatisticsMenuEntry(ScatterLabel(x), () => ApplyScatter(ds, x),
                                                           Checked: _trace.XSpec == x))]);
        if (IsRectPlot && _trace.IsVersus && _trace.ElementKind == TrialElements.Points)
            yield return new("Fit Line", () => SetTrialView(t => t.ShowFitLine = !t.ShowFitLine), Checked: _trace.ShowFitLine);

        var goals = ResultContributions.GoalsOf(ds);
        if (IsRectPlot && goals.Count > 0 && ds.Contains("trials.status"))
            yield return new("Contributions", Children:
                [.. goals.Concat(ResultContributions.MeasuresOf(ds))
                         .Select(g => new StatisticsMenuEntry(g, () => _ = AddContributionsAsync(g)))]);
    }

    private static string ScatterLabel(string spec)
        => spec.StartsWith("trials.stat:", StringComparison.Ordinal) ? spec["trials.stat:".Length..]
         : spec.StartsWith("trials.", StringComparison.Ordinal)     ? spec["trials.".Length..]
         : spec;

    /// <summary>Sets one of this trace's trial views and redraws.</summary>
    public void SetTrialView(Action<Trace> set)
    {
        set(_trace);
        _parent.RebuildAndNotify();
    }

    /// <summary>"Scatter vs" (R-ya9-4): the Versus spec and a points style, through <see cref="TrialViews.ApplyScatter"/>.</summary>
    public void ApplyScatter(DataSet ds, string xSpec)
    {
        TrialViews.ApplyScatter(_trace, ds, xSpec);
        _parent.RebuildAndNotify();
        SyncVersusFromTrace();
        OnPropertyChanged(nameof(SpecShorthand));
    }

    /// <summary>
    /// Statistics ▸ Contributions (R-ya9-5): ranks <paramref name="name"/> once, keeps the ranking in the source — in
    /// memory and in its file, so a saved display redraws it without recomputing — and adds the Pareto plot.
    /// </summary>
    public async System.Threading.Tasks.Task AddContributionsAsync(string name)
    {
        if (_trace.SourcePath is not { } sp || StatisticsSource() is not { } ds || _parent.AddPresetPlot is not { } add) return;
        string path = System.IO.Path.GetFullPath(sp);
        var (pc, refusal) = ContributionParetoPreset.Build(ds, name, DataDisplayViewModel.ComputeSourceKey(path, _parent.Library), out bool stored);
        if (pc is null)
        {
            _parent.Library?.TrialMessage?.Invoke($"Contributions: {refusal}");
            return;
        }
        if (stored && path.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
            RfCore.Export.DataSetExporter.Export(ds, path, RfCore.Export.ExportFormat.Npy);
        await add(pc);
    }
}
