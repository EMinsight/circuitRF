using RfCore.Data;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// Reductions over one axis of a cube (docs/design/measurements.md "Reductions over an axis"): the band worst cases
/// <c>max_over</c>/<c>min_over</c>, and the statistics of a sample — <c>mean_over</c>, <c>std_over</c>,
/// <c>median_over</c>, <c>pctl_over</c>, <c>skew_over</c>, <c>kurt_over</c>, <c>yield_over</c>, <c>cpk</c>,
/// <c>sigma_to</c> — plus three that build a new axis: <c>histogram</c>, <c>cdf</c> and <c>yield_sens</c>.
///
/// <para><b>One operand rule.</b> <c>f(x [, fixed…] [, lo, hi] [, "axis"])</c>: the axis defaults to <c>trial</c>
/// (a Monte Carlo run's, docs/design/yield.md §8), then <c>freq</c>, then a rank-1 cube's only axis; a range keeps
/// the points whose axis value lies in [lo, hi]. The axis is reduced away and every other axis is kept, so a swept
/// run gives one value per point.</para>
///
/// <para><b>NaN.</b> The statistics skip a NaN — over trials it is a trial with no value — while
/// <c>max_over</c>/<c>min_over</c> propagate it, so a bad grid point in a band is never hidden (SampleStatistics).</para>
///
/// <para><b>Companions.</b> A function that builds an axis has more to say than one cube holds: the bin width of a
/// histogram, the trial count per bin of a yield sensitivity. It leaves them in <see cref="TakeCompanions"/>, and the
/// measurement evaluator stores each beside the measurement as <c>&lt;name&gt;:&lt;companion&gt;</c>.</para>
/// </summary>
public sealed partial class Evaluator
{
    /// <summary>The axis a Monte Carlo or yield run stacks its trials on, and the reductions' default.</summary>
    public const string TrialAxis = "trial";

    private Dictionary<string, DataCube>? _companions;

    /// <summary>The companions the last axis-building call left (<c>width</c>, <c>count</c>), cleared by taking.</summary>
    public IReadOnlyDictionary<string, DataCube> TakeCompanions()
    {
        var c = _companions ?? [];
        _companions = null;
        return c;
    }

    // ── The band worst cases ───────────────────────────────────────────────────────

    /// <summary>
    /// <c>max_over(x [, lo, hi] [, "axis"])</c> / <c>min_over(…)</c>: the largest (smallest) value of a REAL quantity
    /// over the points of one axis whose values lie in <c>[lo, hi]</c>, inclusive to a relative 1e-9 so a band edge
    /// that IS a grid point is inside it. A complex cube is refused (complex numbers have no order); a range holding
    /// no grid point is refused with the axis's extent, because an empty band in a spec check is a mistyped limit. A
    /// NaN in the band propagates.
    /// </summary>
    private Value EvalReduceOver(CallExpr cl, Scope scope, string name, bool max)
    {
        var (cube, axis, lo, hi) = ReduceOperand(cl, scope, name, 0, out _);
        return ReduceAlong(cube, axis, lo, hi, name, cube.Unit,
                           max ? xs => xs.Aggregate(Math.Max) : xs => xs.Aggregate(Math.Min));
    }

    // ── The statistics of a sample ───────────────────────────────────────────────────

    private Value EvalStatisticOver(CallExpr cl, Scope scope)
    {
        string name = cl.Name;
        var (cube, axis, lo, hi) = ReduceOperand(cl, scope, name, 0, out _);
        return name switch
        {
            "mean_over"   => ReduceAlong(cube, axis, lo, hi, name, cube.Unit, SampleStatistics.Mean),
            "std_over"    => ReduceAlong(cube, axis, lo, hi, name, cube.Unit, SampleStatistics.StdDev),
            "median_over" => ReduceAlong(cube, axis, lo, hi, name, cube.Unit, SampleStatistics.Median),
            "skew_over"   => ReduceAlong(cube, axis, lo, hi, name, "", SampleStatistics.Skewness),
            "kurt_over"   => ReduceAlong(cube, axis, lo, hi, name, "", SampleStatistics.ExcessKurtosis),
            _             => ReduceAlong(cube, axis, lo, hi, name, "", SampleStatistics.FractionTrue),
        };
    }

    /// <summary><c>pctl_over(x, p [, lo, hi] [, "axis"])</c> — the p-th percentile, p from 0 to 100.</summary>
    private Value EvalPercentileOver(CallExpr cl, Scope scope)
    {
        var (cube, axis, lo, hi) = ReduceOperand(cl, scope, "pctl_over", 1, out var fixedArgs);
        double p = fixedArgs[0].Kind == ValueKind.Real ? fixedArgs[0].AsReal() : double.NaN;
        if (!(p >= 0 && p <= 100))
            throw new ExpressionException("pctl_over(): the percentile must be a number from 0 to 100, e.g. pctl_over(x, 95).");
        return ReduceAlong(cube, axis, lo, hi, "pctl_over", cube.Unit, xs => SampleStatistics.Percentile(xs, p));
    }

    /// <summary><c>cpk(x, lo, hi [, "axis"])</c> — the process capability index; either limit may be written
    /// <c>"none"</c>, not both.</summary>
    private Value EvalCpk(CallExpr cl, Scope scope)
    {
        var (cube, axis, rlo, rhi) = ReduceOperand(cl, scope, "cpk", 2, out var limits);
        double? Limit(Value v) => v.Kind switch
        {
            ValueKind.Real   => v.AsReal(),
            ValueKind.String when v.AsString() is "" or "none" => null,
            _ => throw new ExpressionException("cpk(): a limit must be a number, or \"none\" for a one-sided specification."),
        };
        double? lo = Limit(limits[0]), hi = Limit(limits[1]);
        if (lo is null && hi is null)
            throw new ExpressionException("cpk(): give at least one limit, e.g. cpk(x, \"none\", 5).");
        return ReduceAlong(cube, axis, rlo, rhi, "cpk", "", xs => SampleStatistics.Cpk(xs, lo, hi));
    }

    /// <summary><c>sigma_to(x, limit [, "axis"])</c> — (limit − mean)/σ.</summary>
    private Value EvalSigmaTo(CallExpr cl, Scope scope)
    {
        var (cube, axis, lo, hi) = ReduceOperand(cl, scope, "sigma_to", 1, out var fixedArgs);
        if (fixedArgs[0].Kind != ValueKind.Real)
            throw new ExpressionException("sigma_to(): the limit must be a number, e.g. sigma_to(x, 14).");
        double limit = fixedArgs[0].AsReal();
        return ReduceAlong(cube, axis, lo, hi, "sigma_to", "", xs => SampleStatistics.SigmaTo(xs, limit));
    }

    // ── Functions that build an axis ────────────────────────────────────────────────────

    /// <summary><c>histogram(x, bins [, lo, hi])</c> — counts over a <c>bin</c> axis of bin centres; companion
    /// <c>width</c>.</summary>
    private Value EvalHistogram(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length is not (2 or 4)) throw new ArityException("histogram", 2, cl.Args.Length);
        var x = OneAxis(EvalExpr(cl.Args[0], scope), "histogram");
        int bins = Bins(EvalExpr(cl.Args[1], scope), "histogram");
        double? lo = null, hi = null;
        if (cl.Args.Length == 4)
        {
            var (a, b) = (EvalExpr(cl.Args[2], scope), EvalExpr(cl.Args[3], scope));
            if (a.Kind != ValueKind.Real || b.Kind != ValueKind.Real)
                throw new ExpressionException("histogram(): the range ends must be numbers, e.g. histogram(x, 20, 10, 20).");
            (lo, hi) = (a.AsReal(), b.AsReal());
        }

        var (centres, width, index) = SampleStatistics.Bin(x.RealValues, bins, lo, hi);
        var counts = new double[bins];
        foreach (int k in index) if (k >= 0) counts[k]++;
        var axis = new Axis("bin", centres, x.Unit);
        _companions = new() { ["width"] = new DataCube([axis], [.. centres.Select(_ => width)]) { Unit = x.Unit } };
        return new Value(new DataCube([axis], counts));
    }

    /// <summary><c>cdf(x)</c> — the empirical CDF: the present values sorted, as a <c>value</c> axis, against the
    /// fraction of values at or below each (i/n).</summary>
    private Value EvalCdf(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("cdf", 1, cl.Args.Length);
        var x = OneAxis(EvalExpr(cl.Args[0], scope), "cdf");
        var sorted = SampleStatistics.Present(x.RealValues);
        Array.Sort(sorted);
        if (sorted.Length == 0) throw new ExpressionException("cdf(): the value has no points that are numbers.");
        var fraction = new double[sorted.Length];
        for (int i = 0; i < sorted.Length; i++) fraction[i] = (i + 1.0) / sorted.Length;
        return new Value(new DataCube([new Axis("value", sorted, x.Unit)], fraction));
    }

    /// <summary><c>yield_sens(pass, x, bins)</c> — per bin of x, the fraction of trials that pass; companions
    /// <c>count</c> (trials in the bin) and <c>width</c>. A bin with no trial is NaN.</summary>
    private Value EvalYieldSens(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 3) throw new ArityException("yield_sens", 3, cl.Args.Length);
        var pass = OneAxis(EvalExpr(cl.Args[0], scope), "yield_sens");
        var x    = OneAxis(EvalExpr(cl.Args[1], scope), "yield_sens");
        int bins = Bins(EvalExpr(cl.Args[2], scope), "yield_sens");
        if (pass.Axes[0].Length != x.Axes[0].Length)
            throw new ExpressionException(
                $"yield_sens(): the pass/fail value has {pass.Axes[0].Length} points and the parameter {x.Axes[0].Length}; give both over the same trials.");

        var p = pass.RealValues;
        var v = x.RealValues;
        // A trial counts only where both are present: one with no pass/fail did not take part.
        var xs = new double[v.Length];
        for (int i = 0; i < v.Length; i++) xs[i] = double.IsNaN(p[i]) ? double.NaN : v[i];
        var (centres, width, index) = SampleStatistics.Bin(xs, bins);
        var counts = new double[bins];
        var passes = new double[bins];
        for (int i = 0; i < index.Length; i++)
        {
            if (index[i] < 0) continue;
            counts[index[i]]++;
            if (p[i] != 0) passes[index[i]]++;
        }
        var fraction = new double[bins];
        for (int k = 0; k < bins; k++) fraction[k] = counts[k] == 0 ? double.NaN : passes[k] / counts[k];

        var axis = new Axis("bin", centres, x.Unit);
        _companions = new()
        {
            ["count"] = new DataCube([axis], counts),
            ["width"] = new DataCube([axis], [.. centres.Select(_ => width)]) { Unit = x.Unit },
        };
        return new Value(new DataCube([axis], fraction));
    }

    // ── The operand rule ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>f(x, fixed₁ … fixedₖ [, lo, hi] [, "axis"])</c>: x as a real cube, the k fixed arguments as given,
    /// the optional range and axis. The axis defaults to <see cref="TrialAxis"/>, then <c>freq</c>, then a rank-1
    /// cube's only axis.
    /// </summary>
    private (DataCube Cube, string Axis, double? Lo, double? Hi) ReduceOperand(
        CallExpr cl, Scope scope, string name, int fixedCount, out Value[] fixedArgs)
    {
        int rest = cl.Args.Length - 1 - fixedCount;
        if (rest is < 0 or > 3) throw new ArityException(name, 1 + fixedCount, cl.Args.Length);

        var v = EvalExpr(cl.Args[0], scope);
        fixedArgs = new Value[fixedCount];
        for (int i = 0; i < fixedCount; i++) fixedArgs[i] = EvalExpr(cl.Args[1 + i], scope);

        double? lo = null, hi = null;
        int at = 1 + fixedCount;
        if (rest >= 2)
        {
            var loVal = EvalExpr(cl.Args[at], scope);
            var hiVal = EvalExpr(cl.Args[at + 1], scope);
            if (loVal.Kind != ValueKind.Real || hiVal.Kind != ValueKind.Real)
                throw new ExpressionException($"{name}(): the range ends must be numbers, e.g. {name}(x, 0.1GHz, 3GHz).");
            (lo, hi) = (loVal.AsReal(), hiVal.AsReal());
            if (lo > hi)
                throw new ExpressionException($"{name}(): the range is empty — {lo:G6} is above {hi:G6}. Give the low end first.");
            at += 2;
        }
        string? axisName = null;
        if (rest is 1 or 3)
        {
            var a = EvalExpr(cl.Args[at], scope);
            axisName = a.Kind == ValueKind.String ? a.AsString() : a.ToString()!;
        }

        if (v.Kind != ValueKind.Cube)
            throw new ExpressionException($"{name}(): this value is a single number, so it has no axis to reduce over.");
        var cube = v.AsCube();
        if (cube.DataKind != DataKind.Real)
            throw new ExpressionException(
                $"{name}(): the values are complex, which have no order. Reduce a real quantity: dB(…), mag(…) or real(…) of it.");

        string axes = string.Join(", ", cube.Axes.Select(x => x.Name));
        if (axisName is null)
        {
            if (cube.Axes.Any(x => x.Name == TrialAxis))   axisName = TrialAxis;
            else if (cube.Axes.Any(x => x.Name == "freq")) axisName = "freq";
            else if (cube.Rank == 1)                       axisName = cube.Axes[0].Name;
            else throw new ExpressionException(
                $"{name}(): this value has no 'trial' or 'freq' axis; name the axis to reduce as the last argument. Its axes are: {axes}.");
        }
        if (!cube.Axes.Any(x => x.Name == axisName))
            throw new ExpressionException($"{name}(): no axis named '{axisName}'. Its axes are: {axes}.");
        return (cube, axisName, lo, hi);
    }

    /// <summary>
    /// Applies <paramref name="reduce"/> to the values along <paramref name="axisName"/> (those in [lo, hi] when a
    /// range is given) at every point of the other axes. A scalar when no other axis remains.
    /// </summary>
    private static Value ReduceAlong(DataCube cube, string axisName, double? lo, double? hi, string name, string unit,
                                     Func<double[], double> reduce)
    {
        var axis = cube.Axis(axisName);
        var picked = new List<int>(axis.Length);
        double tol = lo is null ? 0 : 1e-9 * Math.Max(Math.Abs(lo.Value), Math.Abs(hi!.Value));
        for (int k = 0; k < axis.Length; k++)
        {
            double x = axis.Values[k];
            if (lo is null || (x >= lo - tol && x <= hi + tol)) picked.Add(k);
        }
        if (picked.Count == 0)
        {
            if (lo is null)
                throw new ExpressionException($"{name}(): the '{axisName}' axis has no points.");
            string extent = $"{axis.Values.Min():G6} to {axis.Values.Max():G6} {axis.Unit}".TrimEnd();
            throw new ExpressionException(
                $"{name}(): no '{axisName}' point lies in [{lo:G6}, {hi:G6}]; the axis runs from {extent}.");
        }

        var columns = new double[picked.Count][];
        IReadOnlyList<Axis> rest = [];
        for (int i = 0; i < picked.Count; i++)
        {
            var slice = cube.At(axisName, picked[i]);
            columns[i] = slice.RealValues;
            if (i == 0) rest = slice.Axes;
        }
        var result = new double[columns[0].Length];
        var sample = new double[columns.Length];
        for (int e = 0; e < result.Length; e++)
        {
            for (int i = 0; i < columns.Length; i++) sample[i] = columns[i][e];
            result[e] = reduce(sample);
        }
        if (rest.Count == 0) return new Value(result[0]);
        return new Value(new DataCube([.. rest], result) { Unit = unit });
    }

    /// <summary>A real cube with exactly one axis — what a function that builds a new axis takes.</summary>
    private static DataCube OneAxis(Value v, string name)
    {
        if (v.Kind != ValueKind.Cube)
            throw new ExpressionException($"{name}(): this value is a single number; it needs a cube over one axis (the trials).");
        var cube = v.AsCube();
        if (cube.DataKind != DataKind.Real)
            throw new ExpressionException($"{name}(): the values are complex. Take a real quantity: dB(…), mag(…) or real(…) of it.");
        if (cube.Rank != 1)
            throw new ExpressionException(
                $"{name}(): this value has the axes {string.Join(", ", cube.Axes.Select(a => a.Name))}; reduce it to one first, e.g. max_over(x, \"freq\").");
        return cube;
    }

    private static int Bins(Value v, string name)
    {
        double b = v.Kind == ValueKind.Real ? v.AsReal() : double.NaN;
        if (!(b >= 1) || b != Math.Floor(b))
            throw new ExpressionException($"{name}(): the bin count must be a whole number of at least 1, e.g. {name}(…, 20).");
        return (int)b;
    }
}
