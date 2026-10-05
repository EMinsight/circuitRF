// brief-wbond-wire-temperature R-wbt-4 — a wBond's wire temperatures from the currents a converged circuit solve found in it. The
// orchestration lives here, beside the wire physics, so it is tested with no engine; src/Engine reads the currents out of its own
// solution and hands them in.
//
// THE PER-WIRE HARMONIC CURRENT IS A PHASOR SUM. Wire i at frequency f carries Σ_k share[k][i]·I_k(f) over every array k of the
// instance — share[k] the inductive division of a unit current into array k (ArrayReduction.CurrentShares, the numbers
// ArrayShare.For gives), which includes the circulating current an undriven array's shorted turn carries. The 3D thermal run adds
// these as MAGNITUDES, because it is handed array currents with no phases; here the circuit solve's phases are known, so the sum is
// the exact one. An array a DC block isolates still heats from its neighbours' circulating current: "no DC" is not "no heat".
//
// THE DC CURRENT IS DIVIDED BY THE SOLVE (WireConductiveBalance), resistively and at temperature, per array: arrays share no DC.

using System.Globalization;
using System.Numerics;

namespace CircuitRF.WBond.Thermal;

/// <summary>
/// How a wBond instance's wire temperature is set (D4): <b>fixed</b> at <paramref name="FixedC"/> (the <c>Temp</c> parameter), or
/// <b>solved</b> from the run's currents between <paramref name="StartC"/> at each array's input end and <paramref name="EndC"/>
/// at its output end.
/// </summary>
public sealed record WireThermalSpec(bool Solved, double FixedC, double StartC, double EndC)
{
    /// <summary>The temperature the component's impedance is evaluated at (D1): <see cref="FixedC"/>, or in solved mode the HIGHER
    /// of the two ends — a wire is never cooler than its hotter end, so its loss is never understated.</summary>
    public double StampC => Solved ? Math.Max(StartC, EndC) : FixedC;

    /// <summary>A fixed-temperature spec at <paramref name="tempC"/>.</summary>
    public static WireThermalSpec Fixed(double tempC) => new(false, tempC, tempC, tempC);
}

/// <summary>What a <c>WireTempState</c> entry says about its <c>WireTemp</c> (D5).</summary>
public enum WireTempState
{
    /// <summary>The instance's temperature is fixed; WireTemp is its <c>Temp</c>.</summary>
    Fixed = 0,
    /// <summary>Solved, and converged.</summary>
    Solved = 1,
    /// <summary>Solved, and no steady state exists at this drive (thermal runaway) or none was found; WireTemp is NaN.</summary>
    NoSteadyState = 2,
    /// <summary>The circuit itself did not converge at this point; WireTemp is NaN and no temperature was solved.</summary>
    CircuitNotConverged = 3,
}

/// <summary>What the temperature solve needs of one wBond component — built once per model, not per point.</summary>
public sealed class WBondThermalModel
{
    private WBondThermalModel(WireThermalSpec spec, string[] arrayNames, int[] arrayOfWire, ThermalWireSpec[] wires, double[][] share)
    {
        Spec = spec;
        ArrayNames = arrayNames;
        ArrayOfWire = arrayOfWire;
        Wires = wires;
        Share = share;
    }

    public WireThermalSpec Spec { get; }

    public string[] ArrayNames { get; }

    /// <summary>The array each wire belongs to, in the design's wire order (arrays in order, members in order).</summary>
    public int[] ArrayOfWire { get; }

    /// <summary>Each wire's length, diameter and metal, in the same order.</summary>
    public ThermalWireSpec[] Wires { get; }

    /// <summary><c>[k][i]</c>: wire i's current per ampere into array k — the inductive share, frequency-independent.</summary>
    public double[][] Share { get; }

    public int ArrayCount => ArrayNames.Length;

    /// <summary>
    /// The model of <paramref name="design"/>: each wire's arc length (<see cref="Wire.PathLengthMetres"/>, the length its resistance
    /// is computed from), diameter and metal, and the share from <paramref name="inductance"/> (the design's own reduction). The
    /// shares are only read in solved mode, so a fixed-mode model takes none.
    /// </summary>
    public static WBondThermalModel Create(WBondDesign design, WireThermalSpec spec, Func<ArrayReduction> inductance)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(spec);
        var names = design.Arrays.Select(a => a.Name).ToArray();
        var ofWire = new List<int>();
        var wires = new List<ThermalWireSpec>();
        for (int k = 0; k < design.Arrays.Count; k++)
            foreach (var w in design.Arrays[k].Wires)
            {
                ofWire.Add(k);
                wires.Add(new ThermalWireSpec(w.PathLengthMetres(), 2 * w.RadiusMetres, design.MaterialFor(w)));
            }
        var share = new double[names.Length][];
        if (spec.Solved)
        {
            var reduction = inductance();
            for (int k = 0; k < names.Length; k++)
            {
                var unit = new double[names.Length];
                unit[k] = 1.0;
                share[k] = reduction.CurrentShares(unit);
            }
        }
        return new WBondThermalModel(spec, names, [.. ofWire], [.. wires], share);
    }

    /// <summary>The metals of this model's wires that state no thermal conductivity, by name, distinct.</summary>
    public IReadOnlyList<string> MetalsWithoutK()
        => [.. Wires.Select(w => w.Material).Where(m => !m.HasThermalK).Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>One array's answer at one point.</summary>
/// <param name="WireTempC">The hottest wire's hottest point, °C; NaN for states 2 and 3.</param>
/// <param name="Outcome">The solve, in solved mode.</param>
public sealed record WireArrayTemperature(string Array, double WireTempC, WireTempState State, WireArrayOutcome? Outcome);

public static class WBondWireTemperature
{
    /// <summary>
    /// Every array's temperature at one point. <paramref name="arrayDcA"/>[k] is array k's DC current (input to output);
    /// <paramref name="frequenciesHz"/> are the run's non-DC frequencies and <paramref name="arrayPhasors"/>[f][k] array k's peak
    /// phasor at each. <paramref name="warm"/> gives an array's previous state, when there is one. A fixed-mode model reports its
    /// <c>Temp</c> and solves nothing; a point whose circuit did not converge reports state 3 and solves nothing.
    /// </summary>
    public static WireArrayTemperature[] Compute(WBondThermalModel model, double[] arrayDcA, IReadOnlyList<double> frequenciesHz,
                                                 IReadOnlyList<Complex[]> arrayPhasors, bool circuitConverged = true,
                                                 Func<int, WireArrayState?>? warm = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        int m = model.ArrayCount;
        var result = new WireArrayTemperature[m];
        if (!model.Spec.Solved)
        {
            for (int k = 0; k < m; k++) result[k] = new(model.ArrayNames[k], model.Spec.FixedC, WireTempState.Fixed, null);
            return result;
        }
        if (!circuitConverged)
        {
            for (int k = 0; k < m; k++) result[k] = new(model.ArrayNames[k], double.NaN, WireTempState.CircuitNotConverged, null);
            return result;
        }

        var peaks = WirePeaks(model, frequenciesHz, arrayPhasors);
        for (int k = 0; k < m; k++)
        {
            var members = Enumerable.Range(0, model.Wires.Length).Where(i => model.ArrayOfWire[i] == k).ToArray();
            var wires = members.Select(i => model.Wires[i]).ToArray();
            var drive = new WireArrayDrive(arrayDcA[k], frequenciesHz, [.. members.Select(i => peaks[i])]);
            var outcome = WireConductiveBalance.Advance(wires, model.Spec.StartC, model.Spec.EndC, drive, warm?.Invoke(k));
            result[k] = outcome.Solution is { } s
                ? new(model.ArrayNames[k], s.MaxC, WireTempState.Solved, outcome)
                : new(model.ArrayNames[k], double.NaN, WireTempState.NoSteadyState, outcome);
        }
        return result;
    }

    /// <summary>
    /// R-wbt-4b — each wire's peak current at each frequency, <c>|Σ_k share[k][i]·I_k(f)|</c>: the complex sum over every array of
    /// the instance, so a wire's own array's current and the circulating currents of the others combine with their phases.
    /// </summary>
    public static double[][] WirePeaks(WBondThermalModel model, IReadOnlyList<double> frequenciesHz, IReadOnlyList<Complex[]> arrayPhasors)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(arrayPhasors);
        int nf = frequenciesHz.Count;
        var peaks = new double[model.Wires.Length][];
        for (int i = 0; i < peaks.Length; i++)
        {
            var p = peaks[i] = new double[nf];
            for (int f = 0; f < nf; f++)
            {
                Complex sum = Complex.Zero;
                for (int k = 0; k < model.ArrayCount; k++) sum += model.Share[k][i] * arrayPhasors[f][k];
                p[f] = sum.Magnitude;
            }
        }
        return peaks;
    }

    /// <summary>
    /// R-wbt-4e — the sentence a state-2 array warns with: where it has no steady state (<paramref name="drive"/>, null for
    /// <paramref name="where"/>), and the last point that converged — the previous point of a sweep when there is one, else this
    /// point's own drive scale and temperature.
    /// </summary>
    public static string NoSteadyStateWarning(string instancePath, WireArrayTemperature t, string? drive, string where,
                                              (string? Drive, double MaxC)? lastPoint)
    {
        var ci = CultureInfo.InvariantCulture;
        string at = drive is null ? where : $"at {drive}";
        string head = $"wBond '{instancePath}' array '{t.Array}': ";
        var o = t.Outcome;
        if (o is null || !o.Runaway)
            return head + string.Create(ci, $"the wire temperature did not converge {at}") +
                   (o?.LastConverged is { } lc && o.ConvergedAt > 0
                       ? string.Create(ci, $"; it did up to {o.ConvergedAt:P0} of the wire current there, at {lc.MaxC:0.#} °C") : "") +
                   ". WireTemp is NaN there.";
        string last = lastPoint is { Drive: { } d, MaxC: var c }
            ? string.Create(ci, $"; the last converged point, {d}, reached {c:0.#} °C. WireTemp is NaN past it.")
            : string.Create(ci, $"; a steady state exists up to {o.ConvergedAt:P1} of the wire current there, reaching {o.LastConverged?.MaxC ?? double.NaN:0.#} °C. WireTemp is NaN there.");
        return head + $"no steady wire temperature {at} (thermal runaway)" + last;
    }
}

/// <summary>
/// The memory a run carries from one point to the next (R-wbt-3e, R-wbt-4e): each array's last converged state — a warm start —
/// with the drive it was found at and its temperature, for the runaway warning. One per sweep; a single run needs none.
/// </summary>
public sealed class WireThermalSession
{
    private readonly Dictionary<string, (WireArrayState State, string? Drive, double MaxC)> _last = new(StringComparer.Ordinal);

    /// <summary>The point being run, as a warning names it ("Pin = 27.5 dBm"); null for a run with no sweep.</summary>
    public string? Drive { get; set; }

    /// <summary>The previous converged state of <paramref name="key"/> (instance path and array), or null.</summary>
    public WireArrayState? Warm(string key) => _last.TryGetValue(key, out var e) ? e.State : null;

    /// <summary>The previous converged point of <paramref name="key"/>: its drive and its temperature.</summary>
    public (string? Drive, double MaxC)? LastConverged(string key)
        => _last.TryGetValue(key, out var e) ? (e.Drive, e.MaxC) : null;

    /// <summary>Records a converged point at the present <see cref="Drive"/>.</summary>
    public void Record(string key, WireArraySolution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        _last[key] = (solution.State, Drive, solution.MaxC);
    }
}
