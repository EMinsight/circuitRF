// brief-em3d-88 R-em3d88-5 — each bond wire's solved temperature ALONG ITS CHAIN at one sweep point: the chain the thermal run
// solved (ThermalWireLowering.Chain, over the same elaboration's wires and sweeps), paired node for node with the run's own
// T(s) table (ThermalResultTable.Wires, from `wires.Twire:<wire>(s)`). A section picture paints a wire from this, because a
// wire is a 1D element: its temperature is in the table, not in the 3D field the mesh carries.
//
// ONE VALUE PER CHAIN NODE is what brief 77 writes, so the pairing is by index. A table whose length differs from the chain's
// (a run of an older document) is read by arc length instead — linear between its rows, held at its ends, the 3D view's own
// rule (WireTemperature.At) — never refused, since the drawing is then still the run's numbers at the right places.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Thermal;

/// <summary>A wire's chain at one sweep point: its nodes (world metres), their arc length from the start heel (metres), and
/// the run's temperature at each (°C; NaN at a point the run did not reach).</summary>
public sealed record ThermalWireChain(string Wire, double DiameterM, Point3[] Points, double[] S, double[] T);

public static class ThermalWireChains
{
    /// <summary>
    /// Every wire of <paramref name="e"/> the run tabulated, as its chain with the table's temperatures at sweep point
    /// <paramref name="point"/> (0-based). A wire the table does not name, or whose chain cannot be built, is left out.
    /// </summary>
    public static List<ThermalWireChain> At(C3dElaboration e, ThermalResultTable table, int point)
    {
        var list = new List<ThermalWireChain>();
        foreach (var report in e.Wires)
        {
            if (table.Wires.FirstOrDefault(w => w.Wire == report.Name) is not { } row || point < 0 || point >= row.PerPoint.Length) continue;
            if (e.Solids.FirstOrDefault(s => s.Name == report.Name)?.Primitive is not Em3dSweep sweep) continue;
            if (ThermalWireLowering.Chain(report, sweep) is not { } plan) continue;
            int n = plan.NodeCount;
            var pts = new Point3[n];
            for (int i = 0; i < n; i++) pts[i] = new Point3(plan.Points[3 * i], plan.Points[3 * i + 1], plan.Points[3 * i + 2]);
            var values = row.PerPoint[point];
            var t = values.Length == n ? (double[])values.Clone() : [.. plan.S.Select(s => Interpolate(row.S, values, s))];
            list.Add(new ThermalWireChain(report.Name, plan.DiameterM, pts, plan.S, t));
        }
        return list;
    }

    /// <summary>T at arc length <paramref name="s"/>: linear between the rows, held at the ends.</summary>
    public static double Interpolate(double[] sAt, double[] t, double s)
    {
        if (sAt.Length == 0 || t.Length != sAt.Length) return double.NaN;
        if (s <= sAt[0]) return t[0];
        if (s >= sAt[^1]) return t[^1];
        int hi = Array.BinarySearch(sAt, s);
        if (hi >= 0) return t[hi];
        hi = ~hi;
        double f = (s - sAt[hi - 1]) / (sAt[hi] - sAt[hi - 1]);
        return t[hi - 1] + f * (t[hi] - t[hi - 1]);
    }
}
