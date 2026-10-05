using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.WBond.Thermal;
using RfCore.Data;

namespace CircuitRF.Engine;

/// <summary>One wBond's array currents at one point: the DC current per array, and each array's peak phasor at every non-DC
/// frequency the run carries (<c>Phasors[f][k]</c>).</summary>
public sealed record WBondArrayCurrents(double[] DcA, IReadOnlyList<double> FrequenciesHz, IReadOnlyList<Complex[]> Phasors)
{
    /// <summary>DC only — a DC run's currents.</summary>
    public static WBondArrayCurrents Dc(double[] dc) => new(dc, [], []);
}

/// <summary>Every wBond array's temperature at one point, in the <c>wire array</c> axis' order.</summary>
public sealed record WireTemperaturePoint(string[] Labels, double[] TempC, double[] State);

/// <summary>
/// brief-wbond-wire-temperature R-wbt-4 — the <c>WireTemp</c> cube and its state twin, from the currents a converged run
/// found in its wBond instances. Every DC, harmonic-balance and loadpull builder calls this one place, so they cannot disagree
/// about the axis, the labels, the states or the warnings.
///
/// <para><b>Both cubes are present whenever the netlist holds a wBond and absent otherwise</b> (D5), so every other run's
/// DataSet is byte-identical. <c>WireTemp</c> is real, °C, on the axis <c>wire array</c> labelled
/// <c>&lt;instance path&gt;:&lt;array&gt;</c> — the <c>{path}:{n}</c> spelling the <c>I</c> cube's pin currents use.</para>
///
/// <para><b><c>WireTempState</c> is NOT <c>__</c>-prefixed</b>, though it is metadata, and that is deliberate:
/// <c>DataSet.StackSweepAxis</c> passes a <c>__</c> cube through a sweep UNSTACKED — the first point's copy stands for every
/// point — so a swept run would have reported point 0's states (solved, say) beside a later point's NaN. It is a per-point
/// result like <c>Converged</c>, and is named like one. 0 = fixed, 1 = solved, 2 = no steady state, 3 = the circuit did not
/// converge.</para>
/// </summary>
public static class WireTemperatureCubes
{
    public const string Cube = "WireTemp";
    public const string StateCube = "WireTempState";
    public const string AxisName = "wire array";

    /// <summary>The netlist's wBond instances, in netlist order.</summary>
    public static IReadOnlyList<(string Path, WBondModel Model)> Instances(ElaboratedNetlist netlist)
        => [.. netlist.Components.Where(c => c.Model is WBondModel).Select(c => (c.InstancePath, (WBondModel)c.Model))];

    /// <summary>Whether any instance solves its temperature — the only case in which a run needs its currents at all.</summary>
    public static bool AnySolved(IReadOnlyList<(string Path, WBondModel Model)> instances)
        => instances.Any(i => i.Model.ThermalSpec.Solved);

    /// <summary>
    /// One point. <paramref name="currents"/> is asked only for a solved-mode instance at a converged point.
    /// <paramref name="where"/> is how a warning names a point with no sweep drive ("at the operating point").
    /// <paramref name="session"/> (else the netlist's own) carries warm starts and the last converged point from one sweep
    /// point to the next; a runaway warns once per instance and array per run, through the netlist's keyed channel.
    /// </summary>
    public static WireTemperaturePoint Compute(ElaboratedNetlist netlist, IReadOnlyList<(string Path, WBondModel Model)> instances,
                                               Func<string, WBondModel, WBondArrayCurrents> currents, bool circuitConverged,
                                               string where, WireThermalSession? session = null)
    {
        session ??= netlist.WireThermal;
        var labels = new List<string>();
        var temps = new List<double>();
        var states = new List<double>();
        foreach (var (path, model) in instances)
        {
            var thermal = model.Thermal;
            WireArrayTemperature[] result;
            if (thermal.Spec.Solved && circuitConverged)
            {
                var c = currents(path, model);
                result = WBondWireTemperature.Compute(thermal, c.DcA, c.FrequenciesHz, c.Phasors, true,
                                                      k => session?.Warm(Key(path, thermal.ArrayNames[k])));
            }
            else
                result = WBondWireTemperature.Compute(thermal, new double[thermal.ArrayCount], [], [], circuitConverged);

            foreach (var t in result)
            {
                string key = Key(path, t.Array);
                labels.Add(key);
                temps.Add(t.WireTempC);
                states.Add((double)t.State);
                if (t.State == WireTempState.Solved && t.Outcome?.Solution is { } s)
                    session?.Record(key, s);
                else if (t.State == WireTempState.NoSteadyState)
                    netlist.AddWarningOnce($"wiretemp:{key}",
                        WBondWireTemperature.NoSteadyStateWarning(path, t, session?.Drive, where, session?.LastConverged(key)));
            }
        }
        return new WireTemperaturePoint([.. labels], [.. temps], [.. states]);
    }

    /// <summary>Adds the two cubes for one point: <c>[wire array]</c>.</summary>
    public static void Add(DataSet ds, WireTemperaturePoint point)
    {
        var axis = ArrayAxis(point.Labels);
        ds.Add(Cube, new DataCube([axis], point.TempC) { Unit = "°C" });
        ds.Add(StateCube, new DataCube([ArrayAxis(point.Labels)], point.State));
    }

    /// <summary>The <c>wire array</c> axis.</summary>
    public static Axis ArrayAxis(string[] labels)
        => new(AxisName, [.. Enumerable.Range(0, labels.Length).Select(i => (double)i)], "", labels);

    private static string Key(string path, string array) => $"{path}:{array}";

    /// <summary>
    /// R-wbt-4a — a wBond's array currents read out of a full solution vector at each frequency, at the branch rows its last
    /// <c>Stamp</c> was given (<see cref="WBondModel.ArrayBranchIndices"/>) — exactly as an IProbe's or an SnP pin's current is
    /// read. <paramref name="solution"/>(m) is the solution at frequency index m: the single-tone back-solver's, or the two- and
    /// multi-tone full-network solve over the mixing lattice; that solve IS the stamp's own equation
    /// <c>V_in − V_out = Z_arr·I</c>, so this is the current <c>Z_arr⁻¹·(V_in − V_out)</c> would give, without a second
    /// factorisation. With capacitance on, the branch is the series current between the end shunts — the wires' current, which
    /// is what heats them. A frequency of 0 is the DC term; a negative one (a mixing product) is read at its magnitude.
    /// </summary>
    public static WBondArrayCurrents FromBranchRows(WBondModel model, IReadOnlyList<double> frequenciesHz, Func<int, Complex[]> solution)
    {
        int m = model.ArrayCount;
        var dc = new double[m];
        var freqs = new List<double>();
        var phasors = new List<Complex[]>();
        for (int f = 0; f < frequenciesHz.Count; f++)
        {
            var x = solution(f);
            var i = new Complex[m];
            for (int k = 0; k < m; k++)
            {
                int row = model.ArrayBranchIndices[k];
                i[k] = row >= 0 && row < x.Length ? x[row] : Complex.Zero;
            }
            double hz = Math.Abs(frequenciesHz[f]);
            if (hz == 0) for (int k = 0; k < m; k++) dc[k] = i[k].Real;
            else { freqs.Add(hz); phasors.Add(i); }
        }
        return new WBondArrayCurrents(dc, freqs, phasors);
    }
}
