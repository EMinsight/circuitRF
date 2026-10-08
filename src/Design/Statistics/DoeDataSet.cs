using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Statistics;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// A design of experiments as one <see cref="DataSet"/> — <c>&lt;design&gt;.doe.npy</c> (brief-yield-14 R-ya14-5; the
/// names are recorded in docs/design/results-dataset-layout.md §"Design of experiments"):
/// <list type="bullet">
/// <item><c>runs</c> — over a <c>run</c> axis (1…N): <c>coded:&lt;key&gt;</c> and <c>actual:&lt;key&gt;</c> (base SI)
/// per factor, every response (<c>goal:&lt;g&gt;:worst</c>, <c>goal:&lt;g&gt;:margin</c>, each scalar measure by its
/// name; NaN where a run did not evaluate), <c>kind</c> (0 cube, 1 axial, 2 centre) and <c>status</c> (0 evaluated);</item>
/// <item><c>effects</c> — per response, over a <c>term</c> axis labelled with the model's terms: <c>&lt;r&gt;:effect</c>,
/// <c>&lt;r&gt;:coefficient</c>, <c>&lt;r&gt;:active</c> (1/0), <c>&lt;r&gt;:abs</c> (|effect|, sorted largest first on a
/// <c>rank</c> axis labelled with the terms — the effects Pareto) with <c>&lt;r&gt;:margin_line</c> beside it, and the
/// scalars <c>&lt;r&gt;:intercept</c>, <c>&lt;r&gt;:lenth_pse</c>, <c>&lt;r&gt;:lenth_margin</c>, <c>&lt;r&gt;:r2</c>,
/// <c>&lt;r&gt;:curvature</c>; and <c>aliases</c>, each term's alias set as its label;</item>
/// <item><c>main</c> — <c>&lt;r&gt;:&lt;key&gt;</c>, the response's mean over a <c>level</c> axis (the main-effects plot);</item>
/// <item><c>interaction</c> — <c>&lt;r&gt;:&lt;keyA&gt;*&lt;keyB&gt;</c> over (<c>by_level</c>, <c>level</c>): one line
/// per level of B across A (the interaction plot);</item>
/// <item><c>doe</c> — the summary: the design, its factors and levels, the run counts, the resolution and generators.</item>
/// </list>
/// </summary>
internal static class DoeDataSet
{
    public const string RunsGroup        = "runs";
    public const string EffectsGroup     = "effects";
    public const string MainGroup        = "main";
    public const string InteractionGroup = "interaction";
    public const string SummaryGroup     = "doe";

    public static DataSet Build(DoeRun run, DoeResult r)
    {
        var ds = new DataSet();
        var records = r.Records;
        int n = records.Count;
        var runAxis = new Axis("run", [.. Enumerable.Range(1, n).Select(i => (double)i)]);

        // ── runs ───────────────────────────────────────────────────────────────────
        for (int i = 0; i < run.Factors.Count; i++)
        {
            var f = run.Factors[i];
            ds.AddToGroup(RunsGroup, "coded:" + f.Key, Column(runAxis, records.Select(x => x.Coded[i]), ""));
            double scale = f.Unit.Length == 0 ? 1 : Units.Scale(f.Unit) ?? 1;
            ds.AddToGroup(RunsGroup, "actual:" + f.Key,
                Column(runAxis, records.Select(x => x.Actual[i] * scale), f.Unit.Length == 0 ? "" : Units.BaseUnit(f.Unit)));
        }
        foreach (var resp in r.Responses)
            ds.AddToGroup(RunsGroup, resp.Name, Column(runAxis,
                records.Select(x => x.Evaluated && x.Responses.TryGetValue(resp.Name, out double v) ? v : double.NaN), ""));
        ds.AddToGroup(RunsGroup, "kind", Column(runAxis, records.Select(x => (double)(int)x.Kind), ""));
        ds.AddToGroup(RunsGroup, "status", Column(runAxis, records.Select(x => x.Evaluated ? 0.0 : 1.0), ""));

        // ── effects ────────────────────────────────────────────────────────────────
        var terms = run.Model;
        if (terms.Count > 0)
        {
            var termAxis = new Axis("term", [.. Enumerable.Range(1, terms.Count).Select(i => (double)i)], "", [.. terms.Select(t => t.Term.Name)]);
            ds.AddToGroup(EffectsGroup, "aliases", new DataCube(
                [new Axis("term", [.. Enumerable.Range(1, terms.Count).Select(i => (double)i)], "",
                          [.. terms.Select(t => t.Aliases.Count == 0 ? "" : string.Join(", ", t.Aliases))])],
                [.. terms.Select(t => (double)t.Aliases.Count)]));
            foreach (var resp in r.Responses)
            {
                if (resp.Fit is not { } fit) continue;
                string p = resp.Name + ":";
                ds.AddToGroup(EffectsGroup, p + "effect",      new DataCube([termAxis], [.. fit.Effects.Select(e => e.Effect)]));
                ds.AddToGroup(EffectsGroup, p + "coefficient", new DataCube([termAxis], [.. fit.Effects.Select(e => e.Coefficient)]));
                ds.AddToGroup(EffectsGroup, p + "active",      new DataCube([termAxis], [.. fit.Effects.Select(e => e.Active ? 1.0 : 0.0)]));
                // The effects Pareto (R-ya14-6): |effect| largest first, with the Lenth margin as a line beside it.
                var order = fit.Effects.Select((e, i) => (e, i)).OrderByDescending(t => Math.Abs(t.e.Effect)).ToList();
                var rankAxis = new Axis("rank", [.. Enumerable.Range(1, order.Count).Select(i => (double)i)], "", [.. order.Select(t => t.e.Term)]);
                ds.AddToGroup(EffectsGroup, p + "abs", new DataCube([rankAxis], [.. order.Select(t => Math.Abs(t.e.Effect))]));
                ds.AddToGroup(EffectsGroup, p + "margin_line", new DataCube([rankAxis], [.. order.Select(_ => fit.Margin)]));
                ds.AddToGroup(EffectsGroup, p + "intercept",    DataCube.Scalar(fit.Intercept));
                ds.AddToGroup(EffectsGroup, p + "lenth_pse",    DataCube.Scalar(fit.Pse));
                ds.AddToGroup(EffectsGroup, p + "lenth_margin", DataCube.Scalar(fit.Margin));
                ds.AddToGroup(EffectsGroup, p + "r2",           DataCube.Scalar(fit.RSquared));
                ds.AddToGroup(EffectsGroup, p + "curvature",    DataCube.Scalar(fit.Curvature ?? double.NaN));
            }
        }

        // ── main and interaction ───────────────────────────────────────────────────
        foreach (var resp in r.Responses)
        {
            foreach (var m in resp.Main)
                ds.AddToGroup(MainGroup, $"{resp.Name}:{m.Factor}", new DataCube([new Axis("level", m.Levels)], m.Means));
            foreach (var it in resp.Interactions)
            {
                var by = new Axis("by_level", [-1.0, 1.0], "", [$"{it.B} low", $"{it.B} high"]);
                var level = new Axis("level", [-1.0, 1.0]);
                ds.AddToGroup(InteractionGroup, $"{resp.Name}:{it.A}*{it.B}",
                    new DataCube([by, level], [it.Means[0, 0], it.Means[0, 1], it.Means[1, 0], it.Means[1, 1]]));
            }
        }

        // ── doe (the summary) ──────────────────────────────────────────────────────
        var d = run.Settings;
        Label(ds, "design", run.Describe(), (int)d.EffectiveDesign);
        Label(ds, "factor_source", d.EffectiveFactors == DoeFactorSource.Stat ? "stat" : "opt", (int)d.EffectiveFactors);
        Label(ds, "levels", d.EffectiveLevels, d.SigmaK ?? 0);
        if (run.Factors.Count > 0)
            ds.AddToGroup(SummaryGroup, "factors", new DataCube(
                [new Axis("factor", [.. Enumerable.Range(1, run.Factors.Count).Select(i => (double)i)], "",
                          [.. run.Factors.Select(f => $"{f.Letter} = {f.Key}: {f.Low} | {f.Centre} | {f.High}")])],
                [.. Enumerable.Range(1, run.Factors.Count).Select(i => (double)i)]));
        Scalar(ds, "runs", n);
        Scalar(ds, "centre", records.Count(x => x.Kind == DoeRunKind.Centre));
        Scalar(ds, "evaluated", records.Count(x => x.Evaluated));
        Scalar(ds, "did_not_evaluate", records.Count(x => !x.Evaluated));
        Scalar(ds, "resolution", run.Plan?.Resolution ?? double.NaN);
        if (run.Plan is { } plan) Label(ds, "generators", string.Join(", ", plan.Generators), plan.Fraction);
        foreach (var g in run.Goals) Label(ds, $"goal:{g.Name}:spec", TuningDirectiveText.GoalLine(g), 0);
        return ds;
    }

    private static DataCube Column(Axis axis, IEnumerable<double> values, string unit) => new([axis], [.. values]) { Unit = unit };

    private static void Scalar(DataSet ds, string name, double v) => ds.AddToGroup(SummaryGroup, name, DataCube.Scalar(v));

    /// <summary>A text as a one-point cube labelled with it — a cube holds numbers, an axis label text.</summary>
    private static void Label(DataSet ds, string name, string text, double value)
        => ds.AddToGroup(SummaryGroup, name, new DataCube([new Axis(name, [value], "", [text])], [value]));
}
