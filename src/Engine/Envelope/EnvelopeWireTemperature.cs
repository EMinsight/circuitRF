// ================================================================
//  EnvelopeWireTemperature.cs  —  WireTemp from an envelope run
//
//  A bond wire's thermal time constant is milliseconds (a 100 mil gold
//  wire's slowest mode: ~5 ms), far longer than any envelope it carries,
//  so the wire settles at the record's AVERAGE dissipation, and that is
//  what is solved: the one steady-state solve every other analysis runs,
//  driven by currents that heat the wires exactly as the record does on
//  average.  Heating goes as I², so the average is of I², never of I:
//
//    baseband    each array's RMS current, its sign that of its mean: the
//                wires' DC division is linear, so its heat is the mean of
//                i(t)² exactly (arrays share no DC);
//    product k   the record's correlation R_ab = mean(I_a(t) I_b(t)*)
//                over the arrays of the instance, factored R = L L^H and
//                handed over as one phasor set per column of L at that
//                product's frequency.  A wire's heat Σ_f ½|Σ_a s_a I_a|²
//                then sums to ½ sᵀ R s* — the time average, cross terms
//                and all, since neighbouring arrays' currents add as
//                phasors in each wire.
//
//  The average is over the same samples the run's averages are (after
//  the cold-start settle).  A pulse long against milliseconds heats the
//  wire above its average; the average is still what is reported.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using RfCore.Data;

namespace CircuitRF.Engine.Envelope;

internal static class EnvelopeWireTemperature
{
    /// <summary>Each solved-mode wBond of <paramref name="nl"/> and its branch rows, read after the products are stamped
    /// (the rows the last full stamp gave it, as an IProbe's are). A wBond held across the baseband is read through its hold.
    /// Empty when no wBond solves its temperature, so a run records nothing for one.</summary>
    internal static (string[] Paths, int[][] Rows) Rows(ElaboratedNetlist nl)
    {
        var found = nl.Components
            .Select(c => (c.InstancePath, Model: c.Model is BasebandHeldModel { Inner: WBondModel h } ? h : c.Model as WBondModel))
            .Where(x => x.Model is { ThermalSpec.Solved: true })
            .ToArray();
        return ([.. found.Select(x => x.InstancePath)], [.. found.Select(x => (int[])x.Model!.ArrayBranchIndices.Clone())]);
    }

    /// <summary>One step's currents of every instance: <c>[instance][product · arrays + array]</c>, product 0 real.</summary>
    internal static Complex[][] Sample(int[][] rows, int products, Func<int, int, Complex> at)
    {
        var s = new Complex[rows.Length][];
        for (int w = 0; w < rows.Length; w++)
        {
            int m = rows[w].Length;
            var x = s[w] = new Complex[products * m];
            for (int k = 0; k < products; k++)
                for (int a = 0; a < m; a++)
                    x[k * m + a] = rows[w][a] >= 0 ? at(k, rows[w][a]) : Complex.Zero;
            for (int a = 0; a < m; a++) x[a] = new Complex(x[a].Real, 0);
        }
        return s;
    }

    /// <summary>
    /// Adds <c>WireTemp</c> and <c>WireTempState</c> to <paramref name="ds"/> when <paramref name="nl"/> holds a wBond: fixed ones
    /// report their <c>Temp</c>, solved ones the steady state of the record's mean heat from <paramref name="fromS"/> on.
    /// <paramref name="productHz"/>[k] is product k's frequency (0 for the baseband).
    /// </summary>
    internal static void Add(DataSet ds, ElaboratedNetlist nl, EnvelopeRunResult r, IReadOnlyList<double> productHz, double fromS)
    {
        var wbonds = WireTemperatureCubes.Instances(nl);
        if (wbonds.Count == 0) return;
        var used = Enumerable.Range(0, r.Time.Length).Where(i => r.Time[i] >= fromS).ToArray();
        WireTemperatureCubes.Add(ds, WireTemperatureCubes.Compute(nl, wbonds, (path, model) =>
        {
            int w = Array.IndexOf(r.WBondPaths, path);
            return w < 0 || used.Length == 0 ? WBondArrayCurrents.Dc(new double[model.ArrayCount])
                                             : Mean(model.ArrayCount, productHz, used.Select(i => r.WBondCurrent[i][w]).ToArray());
        }, true, "over the envelope record"));
    }

    /// <summary>The currents that heat an instance's wires as <paramref name="steps"/> do on average (see the header).</summary>
    internal static WBondArrayCurrents Mean(int m, IReadOnlyList<double> productHz, Complex[][] steps)
    {
        int n = steps.Length;
        var dc = new double[m];
        for (int a = 0; a < m; a++)
        {
            double sum = 0, sq = 0;
            foreach (var x in steps) { double v = x[a].Real; sum += v; sq += v * v; }
            dc[a] = (sum < 0 ? -1 : 1) * Math.Sqrt(sq / n);
        }
        var freqs = new List<double>();
        var phasors = new List<Complex[]>();
        for (int k = 0; k < productHz.Count; k++)
        {
            double hz = Math.Abs(productHz[k]);
            if (hz == 0) continue;
            var corr = new Complex[m, m];
            foreach (var x in steps)
                for (int a = 0; a < m; a++)
                    for (int b = 0; b < m; b++)
                        corr[a, b] += x[k * m + a] * Complex.Conjugate(x[k * m + b]);
            for (int a = 0; a < m; a++) for (int b = 0; b < m; b++) corr[a, b] /= n;
            foreach (var col in Factor(corr, m)) { freqs.Add(hz); phasors.Add(col); }
        }
        return new WBondArrayCurrents(dc, freqs, phasors);
    }

    /// <summary>Columns c_j with Σ_j c_j c_jᴴ = <paramref name="r"/>, a Hermitian positive semi-definite matrix: a pivoted
    /// Cholesky that stops at the matrix's rank (one column for one array, or for arrays carrying one waveform).</summary>
    internal static List<Complex[]> Factor(Complex[,] r, int m)
    {
        var a = (Complex[,])r.Clone();
        var cols = new List<Complex[]>();
        double trace = 0;
        for (int i = 0; i < m; i++) trace += a[i, i].Real;
        while (cols.Count < m && trace > 0)
        {
            int p = 0;
            for (int i = 1; i < m; i++) if (a[i, i].Real > a[p, p].Real) p = i;
            double d = a[p, p].Real;
            if (d <= 1e-14 * trace) break;
            var c = new Complex[m];
            double s = Math.Sqrt(d);
            for (int i = 0; i < m; i++) c[i] = a[i, p] / s;
            for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) a[i, j] -= c[i] * Complex.Conjugate(c[j]);
            cols.Add(c);
        }
        return cols;
    }
}
