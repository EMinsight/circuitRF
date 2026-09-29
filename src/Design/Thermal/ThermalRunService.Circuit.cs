// brief-em3d-79 — what a thermal run driven from a circuit adds to its result (R-em3d79-3):
//   * the "circuit" group, on the HB's own sweep axes: the carried HB cubes (Pout, PAE, … as the link's Carry names them), the
//     pin currents actually used (Ipin:<p>:dc, and Ipin:<p>:h<k> as peaks — the numbers behind every temperature), the
//     fundamental per point (F0), which points the HB did not converge at (HbSkipped), and per probe with a LimitC the first
//     sweep value at which it is crossed (LimitAt:<probe>, over the outer axes: a scalar for a one-axis sweep);
//   * the sentence per limit that is the scenario's answer: "Probe 'w1' reaches its limit of 150 °C at Pin ≈ 31.2 dBm
//     (Pout ≈ 44.9 dBm)." — interpolated linearly between the two bracketing points, and each carried scalar interpolated at
//     the same fraction.

using System.Globalization;
using CircuitRF.Design.ThreeD;
using RfCore.Data;

namespace CircuitRF.Design.Thermal;

public static partial class ThermalRunService
{
    /// <summary>The DataSet group a circuit-driven run's circuit cubes are carried in.</summary>
    public const string CircuitGroup = "circuit";

    /// <summary>R-em3d79-3 — how a point the circuit's HB did not converge at is marked.</summary>
    public const string HbNotConverged = "hb-not-converged";

    /// <summary>The run's statement of what drives it: the circuit, the chain, the instance, and port ↔ pin ↔ net.</summary>
    private static string CircuitNote(ThermalCircuitDrive d)
        => $"Currents from the circuit '{Path.GetFileName(d.SchematicPath)}', HB '{d.Analysis}' ({d.Points} point(s)" +
           (d.Axes.Count > 0 ? $" over {string.Join(" × ", d.Axes.Select(a => a.Var))}" : "") +
           $"), instance '{d.Instance}' (model '{Path.GetFileName(d.SnpPath)}'): " +
           string.Join(", ", d.Pins.Select(p => $"port {p.Pin} ↔ pin {p.Pin} ↔ net '{p.Net}'")) +
           ". One way: the HB saw the wires at the temperature its S-parameters were solved at; this run reports what they reach.";

    /// <summary>
    /// R-em3d79-3a — per probe with a limit, the first sweep value (innermost axis) at which its maximum reaches it, per row of
    /// the outer axes, interpolated between the bracketing points (NaN if never); and the sentence saying it.
    /// </summary>
    private static List<(string Name, DataCube Cube)> LimitCrossings(IReadOnlyList<C3dProbe> probes, List<Dictionary<string, ProbeRead>> reads,
                                                                     List<(string Var, double[] Values, string Unit)> axes,
                                                                     ThermalCircuitDrive circuit, Func<int, bool> ranAway, out List<string> sentences)
    {
        sentences = [];
        var cubes = new List<(string, DataCube)>();
        var inv = CultureInfo.InvariantCulture;
        int row = axes.Count > 0 ? axes[^1].Values.Length : 1;
        int rows = Math.Max(1, reads.Count / row);
        var carried = circuit.Carried.Where(c => c.Cube.DataKind == DataKind.Real).ToList();
        string Unit(string u) => u.Length > 0 && u != "1" ? " " + u : "";
        string Outer(int r)
        {
            if (axes.Count < 2) return "";
            var idx = circuit.Indices(r * row);
            return " (" + string.Join(", ", axes.Take(axes.Count - 1).Select((a, k) => $"{a.Var} = {a.Values[idx[k]].ToString("G6", inv)}{Unit(a.Unit)}")) + ")";
        }
        foreach (var p in probes)
        {
            if (p.LimitC is not { } lim) continue;
            var at = new double[rows];
            for (int r = 0; r < rows; r++)
            {
                at[r] = double.NaN;
                int prev = -1;
                double hottest = double.NaN;
                bool crossed = false;
                for (int j = 0; j < row && !crossed; j++)
                {
                    int i = r * row + j;
                    if (ranAway(i))
                    {
                        // no steady state here: every limit is passed at or before this point, and nothing interpolates into it
                        crossed = true;
                        string at0 = "the only point";
                        if (axes.Count > 0)
                        {
                            at[r] = axes[^1].Values[i % row];
                            at0 = $"{axes[^1].Var} = {at[r].ToString("G4", inv)}{Unit(axes[^1].Unit)}";
                        }
                        sentences.Add($"Probe '{p.Name}' passes its limit of {lim.ToString("G6", inv)} °C by {at0}{Outer(r)}, where no steady state " +
                                      "exists (thermal runaway)" + (double.IsFinite(hottest) ? $"; the hottest converged reading was {hottest.ToString("F1", inv)} °C." : "."));
                        break;
                    }
                    double v = reads[i].TryGetValue(p.Name, out var rd) ? rd.Max : double.NaN;
                    if (!double.IsFinite(v)) continue;
                    hottest = double.IsNaN(hottest) ? v : Math.Max(hottest, v);
                    if (v < lim) { prev = i; continue; }
                    crossed = true;
                    int a = prev < 0 ? i : prev;
                    double va = prev < 0 ? v : reads[prev][p.Name].Max;
                    double f = prev < 0 || v == va ? 0 : (lim - va) / (v - va);
                    double Lerp(Func<int, double> x) => x(a) + f * (x(i) - x(a));
                    string where;
                    if (axes.Count > 0)
                    {
                        var inner = axes[^1];
                        at[r] = Lerp(k => inner.Values[k % row]);
                        where = $"{inner.Var} ≈ {at[r].ToString("G4", inv)}{Unit(inner.Unit)}";
                    }
                    else where = "the only point";
                    string also = carried.Count == 0 ? "" :
                        " (" + string.Join(", ", carried.Select(c => $"{c.Name} ≈ {Lerp(k => c.Cube.RealValues[k]).ToString("G4", inv)}{Unit(c.Cube.Unit)}")) + ")";
                    sentences.Add(prev < 0 && axes.Count > 0
                        ? $"Probe '{p.Name}' is already past its limit of {lim.ToString("G6", inv)} °C at the first point of the sweep{Outer(r)}, {where}{also}: {v.ToString("F1", inv)} °C."
                        : $"Probe '{p.Name}' reaches its limit of {lim.ToString("G6", inv)} °C at {where}{also}{Outer(r)}.");
                }
                if (!crossed)
                    sentences.Add($"Probe '{p.Name}' stays below its limit of {lim.ToString("G6", inv)} °C over the whole sweep{Outer(r)}" +
                                  (double.IsFinite(hottest) ? $" (hottest {hottest.ToString("F1", inv)} °C)." : "."));
            }
            Axis[] outer = [.. axes.Take(Math.Max(0, axes.Count - 1)).Select(a => new Axis(a.Var, a.Values, a.Unit))];
            string unit = axes.Count > 0 ? axes[^1].Unit : "";
            cubes.Add(($"LimitAt:{p.Name}", outer.Length == 0 ? new DataCube([], at) { Unit = unit } : new DataCube(outer, at) { Unit = unit }));
        }
        return cubes;
    }

    /// <summary>R-em3d79-3 — the circuit group: carried cubes, the pin currents used, F0, the skipped points and the limits.</summary>
    private static void AddCircuit(DataSet ds, List<(string Var, double[] Values, string Unit)> axes, ThermalCircuitDrive d, bool[] skipped,
                                   List<(string Name, DataCube Cube)> crossings)
    {
        foreach (var (name, cube) in d.Carried) ds.AddToGroup(CircuitGroup, name, cube);
        ds.AddToGroup(CircuitGroup, "HbSkipped", Cube(axes, skipped.Select(s => s ? 1.0 : 0.0), "1"));
        ds.AddToGroup(CircuitGroup, "F0", Cube(axes, d.F0, "Hz"));
        foreach (var p in d.Pins)
        {
            var (dc, harmonics) = d.Carries(p);
            if (dc) ds.AddToGroup(CircuitGroup, $"Ipin:{p.Pin}:dc", Cube(axes, p.Dc.Select((v, i) => skipped[i] ? double.NaN : v), "A"));
            foreach (int k in harmonics)
                ds.AddToGroup(CircuitGroup, $"Ipin:{p.Pin}:h{k}", Cube(axes, p.Peak.Select((v, i) => skipped[i] ? double.NaN : v[k]), "A"));
        }
        foreach (var (name, cube) in crossings) ds.AddToGroup(CircuitGroup, name, cube);
    }
}
