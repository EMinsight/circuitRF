using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Diagnostics;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// One trial of a saved Monte Carlo result, run again in full (R-ya4-7) from the Data Display (brief-yield-9 R-ya9-6):
/// the design the result was written beside — <c>&lt;name&gt;.yield.npy</c> is <c>&lt;name&gt;.csch</c>'s or
/// <c>&lt;name&gt;.cnl</c>'s — is prepared as <c>yield trial</c> prepares it and <see cref="StatisticalRun.EvaluateTrial"/>
/// replays the trial. The result comes back with the result's own <c>trial</c> axis, one long, so every trace bound
/// to the result resolves against it unchanged — as a snapshot ghost.
/// </summary>
public static class TrialReplay
{
    /// <summary>The design a result at <paramref name="resultPath"/> was run from, or null when neither is there.</summary>
    public static string? DesignFor(string resultPath)
    {
        string full = Path.GetFullPath(resultPath);
        const string suffix = ".yield.npy";
        if (!full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        string stem = full[..^suffix.Length];
        foreach (var ext in new[] { ".csch", ".cnl" })
            if (File.Exists(stem + ext)) return stem + ext;
        return null;
    }

    /// <summary>Trial <paramref name="trial"/> of the result at <paramref name="resultPath"/>, run again, as a
    /// one-trial DataSet; or the reason it could not be.</summary>
    public static (DataSet? Data, Diagnostic? Refusal) Run(string resultPath, int trial, StatisticalMode mode,
                                                         CancellationToken ct = default)
    {
        if (DesignFor(resultPath) is not { } design)
            return (null, StatisticsDiagnostics.ReplayNoDesign(Path.GetFileName(resultPath)));
        var circuit = design.EndsWith(".csch", StringComparison.OrdinalIgnoreCase)
            ? PreparedCircuit.FromSchematic(design)
            : PreparedCircuit.FromFile(design, Path.GetDirectoryName(design));
        var run = StatisticalRun.Create(circuit, new StatisticalOptions { Mode = mode, Cancellation = ct });
        if (run.Refusal is { } refused) return (null, refused);
        var record = run.EvaluateTrial(trial, ct);
        if (record.Data is not { } data) return (null, record.Reason ?? StatisticsDiagnostics.ReplayNoData(trial));
        return (Stacked(data, trial), record.Evaluated ? null : record.Reason);
    }

    /// <summary>
    /// A statistical corner naming trial <paramref name="trial"/> of <paramref name="result"/> (YA-6) — the seed,
    /// sampling and trial count the result recorded, exactly what <c>yield --save-corner</c> writes, so the corner
    /// replays this trial's z-vector against whatever the nominal is when it is evaluated.
    /// </summary>
    public static CircuitRF.Core.Design.CornerDefinition CornerOf(DataSet result, string name, int trial)
    {
        double Scalar(string n) => result.Contains($"{StatisticalDataSet.YieldGroup}.{n}")
            ? result[$"{StatisticalDataSet.YieldGroup}.{n}"].RealValues[0] : double.NaN;
        double seed = Scalar("seed"), sampling = Scalar("sampling"), trials = Scalar("trials");
        return new CircuitRF.Core.Design.CornerDefinition
        {
            Name     = name,
            Trial    = trial,
            Seed     = double.IsFinite(seed) ? (int)seed : null,
            Sampling = double.IsFinite(sampling) ? (CircuitRF.Core.Design.StatSampling)(int)sampling : null,
            Trials   = double.IsFinite(trials) ? (int)trials : null,
        };
    }

    /// <summary><paramref name="data"/> with an outer <c>trial</c> axis holding <paramref name="trial"/> alone on every
    /// cube but the <c>__</c> metadata, which passes through as a result keeps it.</summary>
    public static DataSet Stacked(DataSet data, int trial)
    {
        var axis = new Axis(Evaluator.TrialAxis, [trial]);
        var ds = new DataSet();
        foreach (var g in data.Groups)
            foreach (var (name, cube) in data.CubesIn(g))
            {
                if (name.StartsWith("__", StringComparison.Ordinal)) { ds.AddToGroup(g, name, cube); continue; }
                var stacked = DataCube.PrependAxis(axis, [cube]);
                stacked.Unit = cube.Unit;
                ds.AddToGroup(g, name, stacked);
            }
        return ds;
    }
}
