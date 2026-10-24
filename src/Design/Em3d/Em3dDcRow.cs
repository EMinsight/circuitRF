// brief-emdc-2 — the 0 Hz row of a 3D result, in one place for every solver: the sentence a problem says about its 0 Hz point,
// the splice that puts circuitRF's conduction solve (Em3dDcSolve) at the front of a solver's frequency axis, and the header lines
// the .sNp says it with.
//
// THE SPLICE runs once per leg, after its DataSet is complete and before both its .npy and its .sNp are written, so the two
// files carry the same rows. In the group that holds S, S gains the DC matrix and every other cube on the same frequency axis
// gains NaN at 0 Hz: none of them (a mode impedance, a pattern) has a DC meaning. Other groups are a solver's own record and are
// left alone.
//
// THE REFERENCE. Em3dDcResult's S is referred to each port's Re Z₀. Every solver's S cube is referred to the group's Z0 cube,
// which is each port's Z₀ (Palace's lumped ports and renormalised wave ports, openEMS's probed ports, EMerge's renormalised ones
// alike), and the Touchstone exporter renormalises the whole group from that cube to the header's one R. So the DC row needs a
// conversion only where a port's Z₀ is complex, and gets it through the same RFNetwork.SToS the exporter uses.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal.Conduction;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

public static class Em3dDcRow
{
    /// <summary>R-emdc2-1 — what a driven problem says about its sweep's 0 Hz point, on the .clay route and the .c3d route
    /// alike; <c>explain</c> says it too.</summary>
    public const string ZeroHzNote =
        "The sweep's 0 Hz point is answered by circuitRF's conduction solve of the conductors, not by the 3D solver.";

    /// <summary>The prefix of every header line about the DC row.</summary>
    public const string HeaderPrefix = "circuitRF-EM 3D DC row: ";

    /// <summary>R-emdc2-2 — the warning a refused DC solve becomes: the refusal, and what it costs a circuit.</summary>
    public static string RefusedWarning(string refusal)
        => $"No 0 Hz row: {refusal} This result has no 0 Hz row, so a circuit's DC solve through it uses the lowest frequency.";

    /// <summary>
    /// R-emdc2-3 — <paramref name="group"/>'s frequency axis with f = 0 put at its front: S from <paramref name="dc"/>, NaN in
    /// every other cube on that axis. Throws when the group has no S, when its first frequency is already 0 or below (a defect),
    /// or when <paramref name="dc"/> has no S.
    /// </summary>
    public static void Prepend(DataSet data, string group, Em3dDcResult dc)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(dc);
        if (dc.S is not { } dcS) throw new ArgumentException("The DC result was refused and has no S to splice.", nameof(dc));
        var cubes = data.CubesIn(group);
        if (!cubes.TryGetValue("S", out var s) || s.Rank != 3)
            throw new InvalidOperationException($"Group '{group}' holds no S cube over frequency, so it has no row to put 0 Hz in.");
        var f = s.Axes[0];
        if (f.Length > 0 && f.Values[0] <= 0)
            throw new InvalidOperationException($"Group '{group}''s frequency axis already starts at {f.Values[0].ToString("R", CultureInfo.InvariantCulture)} Hz, " +
                                                "so a 0 Hz row cannot be put in front of it.");
        int n = s.Axes[1].Length;
        if (dcS.GetLength(0) != n || dcS.GetLength(1) != n)
            throw new InvalidOperationException($"The DC row is {dcS.GetLength(0)}×{dcS.GetLength(1)} and group '{group}''s S is {n}×{n}.");

        var row = Referred(dcS, cubes.TryGetValue("Z0", out var z0) ? z0.ComplexValues : null);
        var axis = new Axis(f.Name, [0, .. f.Values], f.Unit, f.Labels is { } l ? ["", .. l] : null);
        foreach (var (name, cube) in cubes.ToList())
        {
            int at = -1;
            for (int d = 0; d < cube.Rank; d++)
                if (cube.Axes[d].Unit == f.Unit && cube.Axes[d].Values.AsSpan().SequenceEqual(f.Values)) { at = d; break; }
            if (at < 0) continue;
            var spliced = Spliced(cube, at, axis, name == "S" ? row : null);
            spliced.Unit = cube.Unit;
            data.AddToGroup(group, name, spliced);
        }
    }

    /// <summary>
    /// R-emdc2-4 — the header lines a solver's <c>.sNp</c> says its DC row with: none when the sweep asked for no 0 Hz point
    /// (<paramref name="dc"/> null), the refusal when the solve refused, else the solve and one line per lumped element.
    /// </summary>
    public static IReadOnlyList<string> HeaderLines(Em3dDcResult? dc, Em3dProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        if (dc is null) return [];
        // ASCII, as every header line is: the encoding a Touchstone file is written in turns a dash or an Ω into "?"
        if (dc.S is null) return [HeaderPrefix + "none - " + Ascii(dc.Refusal ?? "the conduction solve gave no answer.")];
        var ci = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            HeaderPrefix + $"circuitRF conduction solve of the conductors, {dc.Elements.ToString("N0", ci)} elements, " +
                           $"{(dc.ElapsedMs / 1000).ToString("0.##", ci)} s; ports as terminal pairs",
        };
        foreach (var le in problem.LumpedElements)
        {
            string what = DcConduction.LumpedAtDc(le.R, le.L, le.C, le.Topology == Em3dRlcTopology.Series) switch
            {
                DcLumpedKind.Tie      => "short",
                DcLumpedKind.Resistor => $"resistor {le.R!.Value.ToString("G6", ci)} Ohm",
                _                     => "open",
            };
            lines.Add(HeaderPrefix + $"lumped element '{Ascii(le.Name)}' is a {what}");
        }
        return lines;
    }

    /// <summary>The DC matrix, referred to <paramref name="z0"/> when a port's Z₀ is complex; as solved (each port's Re Z₀)
    /// otherwise.</summary>
    private static Complex[] Referred(double[,] s, Complex[]? z0)
    {
        int n = s.GetLength(0);
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) m[i, j] = s[i, j];
        if (z0 is { Length: var len } && len == n && z0.Any(z => z.Imaginary != 0))
            m = RFNetwork.SToS(m, [.. z0.Select(z => new Complex(z.Real, 0))], z0);
        var v = new Complex[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) v[i * n + j] = m[i, j];
        return v;
    }

    /// <summary><paramref name="cube"/> with one more index on axis <paramref name="at"/>, at its front: <paramref name="first"/>
    /// there (S's DC matrix, in the cube's own layout), NaN when it is null.</summary>
    private static DataCube Spliced(DataCube cube, int at, Axis axis, Complex[]? first)
    {
        var axes = cube.Axes.ToArray();
        axes[at] = axis;
        int outer = 1, inner = 1;
        for (int d = 0; d < at; d++) outer *= cube.Axes[d].Length;
        for (int d = at + 1; d < cube.Rank; d++) inner *= cube.Axes[d].Length;
        int nOld = cube.Axes[at].Length, nNew = nOld + 1;
        if (first is not null && (outer != 1 || first.Length != inner))
            throw new InvalidOperationException("The DC matrix does not fit the S cube's layout.");

        if (cube.DataKind == DataKind.Complex)
        {
            var src = cube.ComplexValues;
            var dst = new Complex[outer * nNew * inner];
            for (int o = 0; o < outer; o++)
            {
                for (int k = 0; k < inner; k++) dst[(o * nNew) * inner + k] = first?[k] ?? new Complex(double.NaN, double.NaN);
                Array.Copy(src, o * nOld * inner, dst, (o * nNew + 1) * inner, nOld * inner);
            }
            return new DataCube(axes, dst);
        }
        var real = cube.RealValues;
        var r = new double[outer * nNew * inner];
        for (int o = 0; o < outer; o++)
        {
            for (int k = 0; k < inner; k++) r[(o * nNew) * inner + k] = double.NaN;
            Array.Copy(real, o * nOld * inner, r, (o * nNew + 1) * inner, nOld * inner);
        }
        return new DataCube(axes, r);
    }

    private static string Ascii(string s)
        => new(s.Replace("µm", "um").Replace("–", "-").Replace("—", "-").Replace("Ω", "Ohm").Replace("×", "x").Replace("₀", "0")
                .Replace("−", "-").Select(ch => ch < 128 ? ch : '?').ToArray());
}
