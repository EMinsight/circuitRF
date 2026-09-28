// brief-em3d-75 R-em3d75-4e — a thermal run's numbers as the probe table shows them: every probe statistic and every measure,
// per sweep point, with the probes that crossed their LimitC flagged. READ FROM THE .npy (brief 74 §5c), never from the field
// files: the table is what the run measured, the fields are what it drew.
//
// A sweep point is one flat index into the product of the sweep's axes, last axis fastest — the order ThermalRunService solved
// them in, the order its .pvd numbers its steps, and the row-major order of every cube. So point i here is step i of the
// fields, and the viewer's sweep slider and this table can never disagree about which point is shown.

using System.Globalization;
using System.Numerics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Thermal;

/// <summary>One row: a probe statistic (<c>T:die:max</c>) or a measure, its unit, and its value at every sweep point.</summary>
/// <param name="Crossed">Per point, true where the probe reached its <c>LimitC</c>; null for a row with no limit.</param>
public sealed record ThermalResultRow(string Name, bool IsMeasure, string Unit, double[] Values, bool[]? Crossed)
{
    public bool AnyCrossed => Crossed?.Any(c => c) == true;
}

/// <summary>A line probe's T along its segment at every point: the samples' positions as fractions of the segment, 0 to 1.</summary>
public sealed record ThermalResultLine(string Probe, double[] Fraction, double[][] PerPoint);

/// <summary>A wire's T(s) at every point (brief 77 writes these under <see cref="ThermalRunService.WireGroup"/>): s in metres.</summary>
public sealed record ThermalResultWire(string Wire, double[] S, double[][] PerPoint);

/// <summary>brief-em3d-80 — Z_th of one place (a source's own, or a probe) per watt in one source, over frequency (DC first).</summary>
public sealed record ThermalResultZth(string Place, string Source, double[] FrequencyHz, Complex[] Z);

public sealed class ThermalResultTable
{
    public required IReadOnlyList<Axis> Axes { get; init; }
    public required int Points { get; init; }
    public required IReadOnlyList<ThermalResultRow> Rows { get; init; }
    public required IReadOnlyList<ThermalResultLine> Lines { get; init; }
    public required IReadOnlyList<ThermalResultWire> Wires { get; init; }

    /// <summary>brief-em3d-79 — a circuit-driven run's circuit cubes on the sweep's axes (the carried HB measures, the pin
    /// currents used, F0): what a probe can be plotted AGAINST besides the sweep variable (wire temperature against Pout).</summary>
    public IReadOnlyList<ThermalResultRow> Circuit { get; init; } = [];

    /// <summary>brief-em3d-80 — every Z_th the run computed, from its <see cref="ThermalRunService.SmallSignalGroup"/> group.</summary>
    public IReadOnlyList<ThermalResultZth> Zth { get; init; } = [];

    /// <summary>The run's .npy read into a table, or null with the reason.</summary>
    public static ThermalResultTable? Read(string npyPath, out string? error)
    {
        error = null;
        try { return From(DataSetImporter.Import(npyPath).DataSet); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>The table of <paramref name="ds"/>, a thermal run's DataSet.</summary>
    public static ThermalResultTable From(DataSet ds)
    {
        var cubes = ds.ContainsGroup(ThermalRunService.Group) ? ds.CubesIn(ThermalRunService.Group) : new Dictionary<string, DataCube>();
        // The sweep: the axes of the energy cube, which every run writes whatever its probes.
        var axes = cubes.TryGetValue("Energy:in", out var energy) ? energy.Axes.ToList() : [];
        int points = Math.Max(1, axes.Aggregate(1, (n, a) => n * a.Length));

        var rows = new List<ThermalResultRow>();
        var lines = new List<ThermalResultLine>();
        foreach (var (name, cube) in cubes)
        {
            if (name.StartsWith("Energy:", StringComparison.Ordinal) || name.StartsWith("Limit:", StringComparison.Ordinal)) continue;
            if (name.EndsWith("(s)", StringComparison.Ordinal) && cube.Rank == axes.Count + 1)
            {
                string probe = name["T:".Length..^"(s)".Length];
                var along = cube.Axes[^1];
                var v = cube.RealValues;
                int n = along.Length;
                lines.Add(new ThermalResultLine(probe, along.Values, [.. Enumerable.Range(0, points).Select(p => v.AsSpan(p * n, n).ToArray())]));
                continue;
            }
            bool[]? crossed = null;
            if (name.StartsWith("T:", StringComparison.Ordinal) && name.EndsWith(":max", StringComparison.Ordinal)
                && cubes.TryGetValue("Limit:" + name["T:".Length..^":max".Length], out var limit))
                crossed = [.. limit.RealValues.Select(x => x >= 0.5)];
            rows.Add(new ThermalResultRow(name, false, cube.Unit, cube.RealValues, crossed));
        }
        if (ds.ContainsGroup(DataSet.MeasurementsGroup))
            foreach (var (name, cube) in ds.CubesIn(DataSet.MeasurementsGroup))
                rows.Add(new ThermalResultRow(name, true, cube.Unit, cube.RealValues, null));

        var wires = new List<ThermalResultWire>();
        if (ds.ContainsGroup(ThermalRunService.WireGroup))
            foreach (var (name, cube) in ds.CubesIn(ThermalRunService.WireGroup))
            {
                if (cube.Rank != axes.Count + 1) continue;
                var s = cube.Axes[^1];
                var v = cube.RealValues;
                int n = s.Length;
                // brief-em3d-77 writes Twire:<name>(s); the wire is <name>, the scene object it colours
                string wire = name.StartsWith("Twire:", StringComparison.Ordinal) && name.EndsWith("(s)", StringComparison.Ordinal)
                    ? name["Twire:".Length..^"(s)".Length] : name;
                wires.Add(new ThermalResultWire(wire, s.Values, [.. Enumerable.Range(0, points).Select(p => v.AsSpan(p * n, n).ToArray())]));
            }
        var circuit = new List<ThermalResultRow>();
        if (ds.ContainsGroup(ThermalRunService.CircuitGroup))
            foreach (var (name, cube) in ds.CubesIn(ThermalRunService.CircuitGroup))
                if (cube.DataKind == DataKind.Real && cube.Axes.Count == axes.Count && cube.Axes.Select((a, i) => a.Name == axes[i].Name && a.Length == axes[i].Length).All(x => x))
                    circuit.Add(new ThermalResultRow(name, true, cube.Unit, cube.RealValues, null));
        var zth = new List<ThermalResultZth>();
        if (ds.ContainsGroup(ThermalRunService.SmallSignalGroup))
            foreach (var (name, cube) in ds.CubesIn(ThermalRunService.SmallSignalGroup))
            {
                if (!name.StartsWith("Zth:", StringComparison.Ordinal) || cube.DataKind != DataKind.Complex || cube.Rank != 1) continue;
                int colon = name.LastIndexOf(':');
                if (colon <= "Zth:".Length) continue;
                zth.Add(new ThermalResultZth(name["Zth:".Length..colon], name[(colon + 1)..], cube.Axes[0].Values, cube.ComplexValues));
            }
        return new ThermalResultTable { Axes = axes, Points = points, Rows = rows, Lines = lines, Wires = wires, Circuit = circuit, Zth = zth };
    }

    /// <summary>Point <paramref name="point"/>'s index along each axis (last axis fastest).</summary>
    public int[] Indices(int point)
    {
        var idx = new int[Axes.Count];
        for (int k = Axes.Count - 1; k >= 0; k--)
        {
            idx[k] = point % Axes[k].Length;
            point /= Axes[k].Length;
        }
        return idx;
    }

    /// <summary>How the slider and a column name point <paramref name="point"/>: <c>Pdiss = 0.5 W, Tsink = 25</c>, or
    /// <c>the only point</c>.</summary>
    public string PointLabel(int point)
    {
        if (Axes.Count == 0) return "the only point";
        var idx = Indices(point);
        return string.Join(", ", Axes.Select((a, k) =>
            $"{a.Name} = {a.Values[idx[k]].ToString("G6", CultureInfo.InvariantCulture)}{(a.Unit.Length > 0 ? " " + a.Unit : "")}"));
    }
}
