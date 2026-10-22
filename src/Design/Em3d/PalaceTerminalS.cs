// brief-em3d-115 R-em3d115-4/-5 — a Palace run with terminal wave ports: what it read, and what is Palace's own of the rest.
//
// The numeric half is ModalTerminalTransform (src/Engine/Em3d). This file is Palace's Design half: the readers
// (port-S.csv, port-V.csv's V_wp, port-Z.csv's Z_PV and the log's kₙ) and the refusal of a degenerate face. Which ports
// share a face, the transform per frequency and the §5 checks are WavePortTerminalS's (brief-emw-3), which EMerge's run
// shares; the members below that forward there hand it "Palace" and Palace's Robin form. Nothing here runs a process.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>One frequency's kₙ per port number, and the frequency Palace set its ports up at.</summary>
public sealed record PalaceWaveModeSample(double FrequencyHz, IReadOnlyDictionary<int, PalaceWaveMode> Modes);

public static class PalaceTerminalS
{
    /// <summary>The solver's name in the shared notes and warnings.</summary>
    private const string Solver = "Palace";

    public const double GramResidualLimit = WavePortTerminalS.GramResidualLimit;
    public const double PowerConventionLimit = WavePortTerminalS.PowerConventionLimit;
    public const double OffBlockLimit = WavePortTerminalS.OffBlockLimit;
    public const double LosslessLimit = WavePortTerminalS.LosslessLimit;

    /// <inheritdoc cref="WavePortTerminalS.Faces"/>
    public static IReadOnlyList<IReadOnlyList<int>> Faces(IReadOnlyList<Em3dPort> ports) => WavePortTerminalS.Faces(ports);

    /// <inheritdoc cref="WavePortTerminalS.Leader"/>
    public static int Leader(Em3dPort p, IReadOnlyList<Em3dPort> ports) => WavePortTerminalS.Leader(p, ports);

    /// <inheritdoc cref="WavePortTerminalS.Lossless"/>
    public static bool Lossless(Em3dProblem problem) => WavePortTerminalS.Lossless(problem);

    /// <summary>The terminal S of every row with Palace's Robin form, <see cref="ModalRobinForm.RealParts"/>
    /// (<see cref="WavePortTerminalS.Convert"/>).</summary>
    public static WavePortTerminalOutcome? Convert(WavePortModalSamples samples, IReadOnlyList<Em3dPort> ports, out string? error)
        => WavePortTerminalS.Convert(samples, ports, ModalRobinForm.RealParts, out error);

    /// <inheritdoc cref="WavePortTerminalS.RouteNotes"/>
    public static IEnumerable<string> RouteNotes(IReadOnlyList<Em3dPort> ports, WavePortTerminalOutcome outcome)
        => WavePortTerminalS.RouteNotes(ports, outcome, Solver);

    /// <inheritdoc cref="WavePortTerminalS.Checks"/>
    public static (List<string> Notes, List<string> Warnings) Checks(IReadOnlyList<Em3dPort> ports, WavePortTerminalOutcome outcome, bool lossless)
        => WavePortTerminalS.Checks(ports, outcome, lossless, Solver);

    /// <summary>Palace logs the frequency it set its ports up at to four figures: a row of port-S.csv within this of it is that
    /// frequency.</summary>
    private const double LoggedPrecision = 5e-4;

    /// <summary>
    /// The run's samples: <c>port-S.csv</c>, <c>port-V.csv</c>'s V_wp and <c>port-Z.csv</c>'s Z_PV in <paramref name="post"/>,
    /// row for row, and each row's kₙ from <paramref name="modes"/> (<see cref="KnAt"/>). <paramref name="ports"/> are the
    /// wave ports' numbers in the run's order; <paramref name="knNeeded"/> those whose kₙ the transform reads (a terminal
    /// face's entries; null is all of them). Null, with the reason, when a file or a frequency is missing.
    /// </summary>
    public static WavePortModalSamples? Read(string post, IReadOnlyList<PalaceWaveModeSample> modes, IReadOnlyList<int> ports, out string? error,
                                           IReadOnlyCollection<int>? knNeeded = null)
    {
        var s = PalaceRun.ReadPortS(Path.Combine(post, PalaceRun.PortSFile), ports, out error);
        if (error is not null) return null;
        var v = PalaceRun.ReadWavePortV(Path.Combine(post, PalaceRun.PortVFile), ports, out error);
        if (v is null) return null;
        var z = PalaceRun.ReadPortZ(Path.Combine(post, PalaceRun.PortZFile), ports, out double[] zf, out error);
        if (z is null) return null;
        int n = s.FrequenciesHz.Length;
        if (v.Value.FrequenciesGHz.Length != n || zf.Length != n ||
            Enumerable.Range(0, n).Any(k => !Same(v.Value.FrequenciesGHz[k] * 1e9, s.FrequenciesHz[k]) || !Same(zf[k], s.FrequenciesHz[k])))
        {
            error = $"Palace's {PalaceRun.PortSFile}, {PalaceRun.PortVFile} and {PalaceRun.PortZFile} do not list the same frequencies, " +
                    "so the terminal S cannot be formed.";
            return null;
        }
        // A one-entry face's K is 1 whatever its kₙ (it is its own Active entry), so only a terminal face's entries need one.
        var needed = knNeeded ?? ports;
        var samples = new ModalTerminalSample[n];
        for (int k = 0; k < n; k++)
        {
            var kn = KnAt(modes, s.FrequenciesHz[k], [.. ports.Where(needed.Contains)], out error);
            if (kn is null) return null;
            int next = 0;
            Complex[] all = [.. ports.Select(p => needed.Contains(p) ? kn[next++] : Complex.One)];
            samples[k] = new ModalTerminalSample(s.S[k], v.Value.V[k], [.. z[k].Select(c => c.Real)], all);
        }
        return new WavePortModalSamples(s.FrequenciesHz, samples);
    }

    /// <summary>
    /// Each port's kₙ at <paramref name="fHz"/>: the logged value when Palace set its ports up at that frequency; otherwise
    /// interpolated between the logged frequencies around it, linearly in f on ε_eff = (Re kₙ·c/ω)² (and Im kₙ·c/ω), which
    /// is what varies slowly (124: K 1.0793 → 1.0823 → 1.0863 over 2–6 GHz); outside them, the nearest's ε_eff.
    /// </summary>
    public static Complex[]? KnAt(IReadOnlyList<PalaceWaveModeSample> modes, double fHz, IReadOnlyList<int> ports, out string? error)
    {
        error = null;
        var usable = modes.Where(m => ports.All(m.Modes.ContainsKey)).OrderBy(m => m.FrequencyHz).ToList();
        if (usable.Count == 0)
        {
            error = $"Palace's log names no wavenumber kₙ for every wave port ({string.Join(", ", ports)}), which the terminal S needs.";
            return null;
        }
        if (usable.FirstOrDefault(m => Math.Abs(m.FrequencyHz - fHz) <= LoggedPrecision * fHz) is { } exact) return [.. ports.Select(p => exact.Modes[p].Kn)];
        const double c0 = 299_792_458.0;
        Complex Scaled(PalaceWaveModeSample m, int p) => m.Modes[p].Kn * c0 / (2 * Math.PI * m.FrequencyHz);   // √ε_eff, complex
        int hi = usable.FindIndex(m => m.FrequencyHz > fHz);
        var result = new Complex[ports.Count];
        for (int i = 0; i < ports.Count; i++)
        {
            Complex at;
            if (hi <= 0) at = Scaled(usable[hi < 0 ? ^1 : 0], ports[i]);
            else
            {
                var (a, b) = (usable[hi - 1], usable[hi]);
                double t = (fHz - a.FrequencyHz) / (b.FrequencyHz - a.FrequencyHz);
                Complex sa = Scaled(a, ports[i]), sb = Scaled(b, ports[i]);
                double ea = sa.Real * sa.Real, eb = sb.Real * sb.Real;
                at = new Complex(Math.Sqrt(ea + t * (eb - ea)), sa.Imaginary + t * (sb.Imaginary - sa.Imaginary));
            }
            result[i] = at * 2 * Math.PI * fHz / c0;
        }
        return result;
    }

    /// <summary>
    /// brief-em3d-115 R-em3d115-3 — the frequencies a run whose log names no kₙ (an adaptive sweep) solves each terminal face's
    /// modes at, and interpolates between: every sweep frequency when there are at most five, else five spread evenly over the
    /// sweep's own (its two ends among them). On 124's microstrip pair, three over 2–6 GHz interpolate kₙ to 4e-4 and the
    /// terminal S to 0.0004 of the Point run's.
    /// </summary>
    public static IReadOnlyList<double> KnotFrequenciesHz(IReadOnlyList<double> sweepHz)
    {
        const int Knots = 5;
        if (sweepHz.Count <= Knots) return [.. sweepHz];
        return [.. Enumerable.Range(0, Knots).Select(k => sweepHz[(int)Math.Round(k * (sweepHz.Count - 1) / (double)(Knots - 1))]).Distinct()];
    }

    /// <inheritdoc cref="WavePortTerminalS.AddDiagnostics"/>
    public static void AddDiagnostics(DataSet data, string group, WavePortModalSamples samples, WavePortTerminalOutcome outcome,
                                      IReadOnlyList<Em3dPort> ports)
        => WavePortTerminalS.AddDiagnostics(data, group, samples, outcome, ports);

    /// <summary>
    /// brief-em3d-115 — why a terminal face whose modes travel at one speed is refused on Palace. Palace returns two such
    /// modes in an arbitrary mixture, re-drawn each time it solves the face (per excitation and frequency in a sweep: the 3D
    /// Wave Ports Pair's modal S came back non-reciprocal by 0.85 over two Point frequencies), and on some meshes as nearly the
    /// same field even within a one-frequency run (its voltages 0.9998 collinear): nothing converts that to two terminals.
    /// </summary>
    public static string DegenerateRefusal(string label, IReadOnlyList<Complex> kn, double fHz)
        => $"Port '{label}''s {kn.Count} modes travel at one speed (Palace's kₙ {string.Join(", ", kn.Select(k => Fmt4(k.Real)))} m⁻¹ " +
           $"at {Fmt(fHz / 1e9)} GHz), as on a stripline or any line in a homogeneous fill. Palace returns such modes in an " +
           "arbitrary mixture, drawn again every time it solves the face and on some meshes nearly the same field twice, so " +
           "their terminal S cannot be recovered. Set the setup's solver to openEMS.";

    public const string ModalSCube = WavePortTerminalS.ModalSCube;
    public const string ModalVCube = WavePortTerminalS.ModalVCube;
    public const string ModalZpvCube = WavePortTerminalS.ModalZpvCube;
    public const string ModalKnCube = WavePortTerminalS.ModalKnCube;
    public const string ModalGCube = WavePortTerminalS.ModalGCube;
    public const string ModalKCube = WavePortTerminalS.ModalKCube;

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-8 * Math.Max(Math.Abs(a), Math.Abs(b));
    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Fmt4(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
