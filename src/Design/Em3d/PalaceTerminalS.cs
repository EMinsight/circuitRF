// brief-em3d-115 R-em3d115-4/-5 — a Palace run with terminal wave ports: what it read, the transform per frequency, and
// what the run says about it.
//
// The numeric half is ModalTerminalTransform (src/Engine/Em3d). This file is the Design half: which ports share a face, the
// readers (port-S.csv, port-V.csv's V_wp, port-Z.csv's Z_PV and the log's kₙ), and the §5 checks as the run's notes and
// warnings. Nothing here runs a process.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>One frequency's kₙ per port number, and the frequency Palace set its ports up at.</summary>
public sealed record PalaceWaveModeSample(double FrequencyHz, IReadOnlyDictionary<int, PalaceWaveMode> Modes);

/// <summary>What a run's files hold, per row of <c>port-S.csv</c>, in port order.</summary>
public sealed record PalaceModalSamples(double[] FrequenciesHz, ModalTerminalSample[] Samples);

/// <summary>The terminal S and the transform's own result at each frequency.</summary>
public sealed record PalaceTerminalOutcome(PalacePortS S, ModalTerminalResult[] PerFrequency);

public static class PalaceTerminalS
{
    /// <summary>A Gram fit whose largest relative residual is above this has no root consistent with Palace's Z_PV.</summary>
    public const double GramResidualLimit = 1e-3;

    /// <summary>|T_V,ii|² against Z_PV after the fit, relative: above this Palace's power convention has changed (PR #937).</summary>
    public const double PowerConventionLimit = 1e-3;

    /// <summary>The largest entry of M outside a face's own block, relative to M's largest: a terminal reading another face.</summary>
    public const double OffBlockLimit = 1e-2;

    /// <summary>On a problem with no loss mechanism, a per-port power or singular value further than this from 1.</summary>
    public const double LosslessLimit = 0.002;

    /// <summary>Palace logs the frequency it set its ports up at to four figures: a row of port-S.csv within this of it is that
    /// frequency.</summary>
    private const double LoggedPrecision = 5e-4;

    /// <summary>
    /// The faces of <paramref name="ports"/> (in that order, which is the run's port order) as index lists: a face group's
    /// terminals in number order, the lowest first (the Active entry), and every other wave port alone.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Faces(IReadOnlyList<Em3dPort> ports)
    {
        var faces = new List<IReadOnlyList<int>>();
        var seen = new HashSet<int>();
        for (int i = 0; i < ports.Count; i++)
        {
            if (!seen.Add(i)) continue;
            if (ports[i].FaceGroup is not { } g) { faces.Add([i]); continue; }
            var members = Enumerable.Range(0, ports.Count).Where(k => ports[k].FaceGroup == g).OrderBy(k => ports[k].Number).ToList();
            foreach (int k in members) seen.Add(k);
            faces.Add(members);
        }
        return faces;
    }

    /// <summary>The number of the port whose entry is Active on <paramref name="p"/>'s face (its own number when it is alone).</summary>
    public static int Leader(Em3dPort p, IReadOnlyList<Em3dPort> ports)
        => p.FaceGroup is { } g ? ports.Where(q => q.FaceGroup == g).Min(q => q.Number) : p.Number;

    /// <summary>
    /// <b>Whether the problem has no loss mechanism</b>: every material it uses lossless (no loss tangent, no finite
    /// conductivity — a conductor with none is a perfect one), no absorbing face of the air box. Only then is a terminal S
    /// whose power is not 1 a sign of a wrong transform rather than of loss.
    /// </summary>
    public static bool Lossless(Em3dProblem problem)
    {
        var f = problem.Boundary.Faces;
        if (new[] { f.XMin, f.XMax, f.YMin, f.YMax, f.ZMin, f.ZMax }.Contains(Em3dBoundaryKind.Absorbing)) return false;
        var used = problem.Solids.Select(s => s.Material).Concat(problem.Sheets.Select(s => s.Material))
                              .Concat(problem.FaceBoundaries.Select(b => b.Material).OfType<string>())
                              .Append(problem.Boundary.Material).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return problem.Materials.Where(m => used.Contains(m.Name))
                      .All(m => !(m.TanD > 0) && !(m.SigmaSm > 0 && double.IsFinite(m.SigmaSm)));
    }

    /// <summary>
    /// The run's samples: <c>port-S.csv</c>, <c>port-V.csv</c>'s V_wp and <c>port-Z.csv</c>'s Z_PV in <paramref name="post"/>,
    /// row for row, and each row's kₙ from <paramref name="modes"/> (<see cref="KnAt"/>). <paramref name="ports"/> are the
    /// wave ports' numbers in the run's order; <paramref name="knNeeded"/> those whose kₙ the transform reads (a terminal
    /// face's entries; null is all of them). Null, with the reason, when a file or a frequency is missing.
    /// </summary>
    public static PalaceModalSamples? Read(string post, IReadOnlyList<PalaceWaveModeSample> modes, IReadOnlyList<int> ports, out string? error,
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
        return new PalaceModalSamples(s.FrequenciesHz, samples);
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

    /// <summary>
    /// The terminal S of every row, each port at its own Z0. <paramref name="ports"/> are the run's wave ports in its order.
    /// Null, with the sentence naming the frequency, when a row cannot be transformed.
    /// </summary>
    public static PalaceTerminalOutcome? Convert(PalaceModalSamples samples, IReadOnlyList<Em3dPort> ports, out string? error)
    {
        error = null;
        var faces = Faces(ports);
        double[] z0 = [.. ports.Select(p => p.Z0.Real)];
        var results = new ModalTerminalResult[samples.Samples.Length];
        var mats = new Complex[samples.Samples.Length][,];
        for (int k = 0; k < results.Length; k++)
        {
            results[k] = ModalTerminalTransform.Solve(samples.Samples[k], faces, z0);
            if (results[k].Error is { } why)
            {
                error = $"At {Fmt(samples.FrequenciesHz[k] / 1e9)} GHz: {why}";
                return null;
            }
            mats[k] = results[k].S!;
        }
        return new PalaceTerminalOutcome(new PalacePortS(samples.FrequenciesHz, mats), results);
    }

    /// <summary>
    /// §5's route note, one per terminal face: <c>Port 'Left': terminals P1, P2 as modes 1 and 2 of one Palace wave port,
    /// converted to terminal S (degenerate face: Gram fit g = −0.501, residual 2e-6).</c> Over a sweep, g's range and the
    /// largest residual.
    /// </summary>
    public static IEnumerable<string> RouteNotes(IReadOnlyList<Em3dPort> ports, PalaceTerminalOutcome outcome)
    {
        var faces = Faces(ports);
        for (int f = 0; f < faces.Count; f++)
        {
            var face = faces[f];
            if (face.Count < 2) continue;
            var fits = outcome.PerFrequency.Select(r => r.Faces[f]).ToList();
            string terminals = string.Join(", ", face.Select(i => TerminalName(ports[i])));
            string modes = Ordinals(face.Count);
            string detail;
            if (fits.All(x => x.Degenerate))
            {
                double gLo = fits.Min(x => x.G.Real), gHi = fits.Max(x => x.G.Real);
                detail = $"degenerate face: Gram fit g = {Signed(gLo)}{(gHi - gLo > 0.0005 ? $" to {Signed(gHi)}" : "")}, " +
                         $"residual {Sci(fits.Max(x => x.Residual))}";
            }
            else
            {
                var ks = outcome.PerFrequency.SelectMany(r => face.Skip(1).Select(i => r.K[i])).ToList();
                detail = (fits.Any(x => x.Degenerate) ? "degenerate at some frequencies only; " : "") +
                         $"modes not degenerate, Robin correction K = {G4(ks.Min())}{(ks.Max() - ks.Min() > 0.00005 ? $" to {G4(ks.Max())}" : "")}";
            }
            yield return $"Port '{ports[face[0]].FaceGroupLabel ?? ports[face[0]].Name}': terminals {terminals} as modes {modes} of one " +
                         $"Palace wave port, converted to terminal S ({detail}).";
        }
    }

    /// <summary>§5's checks as the run's notes (reported) and warnings (outside a bound).</summary>
    public static (List<string> Notes, List<string> Warnings) Checks(IReadOnlyList<Em3dPort> ports, PalaceTerminalOutcome outcome, bool lossless)
    {
        var notes = new List<string>();
        var warnings = new List<string>();
        var faces = Faces(ports);
        var r = outcome.PerFrequency;
        double[] f = outcome.S.FrequenciesHz;
        string At(int k) => $"{Fmt(f[k] / 1e9)} GHz";
        int Worst(Func<ModalTerminalResult, double> of) => Enumerable.Range(0, r.Length).MaxBy(k => of(r[k]));

        for (int fi = 0; fi < faces.Count; fi++)
        {
            if (faces[fi].Count < 2) continue;
            int k = Worst(x => x.Faces[fi].Residual);
            if (r[k].Faces[fi].Residual > GramResidualLimit)
                warnings.Add($"Port '{ports[faces[fi][0]].FaceGroupLabel}': the Gram fit of its degenerate modes leaves a residual of " +
                             $"{Sci(r[k].Faces[fi].Residual)} at {At(k)}, so it has no root consistent with Palace's mode impedance Z_PV and " +
                             "the terminal S there is not to be trusted. Refine the mesh at the port face (a mesh region along the strips' " +
                             "edges), or run the setup on openEMS.");
        }
        int tv = Worst(x => x.TvMismatch.Max());
        if (r[tv].TvMismatch.Max() > PowerConventionLimit)
        {
            int i = Array.IndexOf(r[tv].TvMismatch, r[tv].TvMismatch.Max());
            warnings.Add($"Port {ports[i].Number}: the terminal voltage of its own mode does not give Palace's Z_PV (off by " +
                         $"{Percent(r[tv].TvMismatch[i])} at {At(tv)}). The transform assumes Palace's unit-power mode convention, so a " +
                         "Palace that changed it gives a wrong terminal S. Check the installed Palace is a version circuitRF validates.");
        }
        int ob = Worst(x => x.MOffBlock);
        if (r[ob].MOffBlock > OffBlockLimit)
            warnings.Add($"A terminal reads another port face's modes ({Percent(r[ob].MOffBlock)} of the largest voltage at {At(ob)}): " +
                         "two port faces are coupled through their modes, which the transform does not model. Move the faces apart, " +
                         "or check that no terminal's voltage path crosses onto another face.");

        double powerLo = r.Min(x => x.PortPower.Min()), powerHi = r.Max(x => x.PortPower.Max());
        double sLo = r.Min(x => x.SigmaMin), sHi = r.Max(x => x.SigmaMax);
        notes.Add($"Terminal S checks: per-port power |S_jj|² + Σ|S_ij|² {G4(powerLo)} to {G4(powerHi)}, singular values {G4(sLo)} " +
                  $"to {G4(sHi)}, reciprocity max |S_ij − S_ji| {Sci(r.Max(x => x.Reciprocity))}" +
                  (lossless ? " (no loss mechanism in the problem, so each should be 1)." : "."));
        if (lossless)
        {
            int worst = Worst(x => Math.Max(Math.Max(Math.Abs(1 - x.PortPower.Min()), Math.Abs(1 - x.PortPower.Max())),
                                            Math.Max(Math.Abs(1 - x.SigmaMin), Math.Abs(1 - x.SigmaMax))));
            var w = r[worst];
            double off = Math.Max(Math.Max(Math.Abs(1 - w.PortPower.Min()), Math.Abs(1 - w.PortPower.Max())),
                                  Math.Max(Math.Abs(1 - w.SigmaMin), Math.Abs(1 - w.SigmaMax)));
            if (off > LosslessLimit)
                warnings.Add($"The problem has no loss mechanism, yet its terminal S is {Percent(off)} from lossless at {At(worst)} " +
                             $"(per-port power {G4(w.PortPower.Min())} to {G4(w.PortPower.Max())}, singular values {G4(w.SigmaMin)} to " +
                             $"{G4(w.SigmaMax)}). The conversion from Palace's modes is not exact there: usually the mesh at the port " +
                             "face, which a mesh region along the strips' edges refines.");
        }
        return (notes, warnings);
    }

    /// <summary>
    /// brief-em3d-115 R-em3d115-5 — the diagnostics a disputed result is re-derived from, in the Palace group: the modal S and
    /// V_wp [f, i, j], Z_PV and kₙ [f, Port], G [f, i, j] and K [f, Port].
    /// </summary>
    public static void AddDiagnostics(DataSet data, string group, PalaceModalSamples samples, PalaceTerminalOutcome outcome,
                                      IReadOnlyList<Em3dPort> ports)
    {
        int nf = samples.FrequenciesHz.Length, n = ports.Count;
        var fAxis = new Axis("f", outcome.S.FrequenciesHz, "Hz");
        double[] numbers = [.. ports.Select(p => (double)p.Number)];
        DataCube Matrix(Func<int, Complex[,]> of, string unit = "")
        {
            var v = new Complex[nf * n * n];
            for (int k = 0; k < nf; k++)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) v[(k * n + i) * n + j] = of(k)[i, j];
            return new DataCube([fAxis, new Axis("i", numbers, ""), new Axis("j", numbers, "")], v) { Unit = unit };
        }
        DataCube Vector(Func<int, int, Complex> of, string unit = "")
        {
            var v = new Complex[nf * n];
            for (int k = 0; k < nf; k++) for (int i = 0; i < n; i++) v[k * n + i] = of(k, i);
            return new DataCube([fAxis, new Axis("Port", numbers, "")], v) { Unit = unit };
        }
        data.AddToGroup(group, ModalSCube, Matrix(k => samples.Samples[k].ModalS));
        data.AddToGroup(group, ModalVCube, Matrix(k => samples.Samples[k].V, "V"));
        data.AddToGroup(group, ModalZpvCube, Vector((k, i) => samples.Samples[k].ZPv[i], "Ohm"));
        data.AddToGroup(group, ModalKnCube, Vector((k, i) => samples.Samples[k].Kn[i], "1/m"));
        data.AddToGroup(group, ModalGCube, Matrix(k => outcome.PerFrequency[k].G!));
        data.AddToGroup(group, ModalKCube, Vector((k, i) => outcome.PerFrequency[k].K[i]));
    }

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

    public const string ModalSCube = "modal_S";
    public const string ModalVCube = "modal_V";
    public const string ModalZpvCube = "modal_Zpv";
    public const string ModalKnCube = "modal_kn";
    public const string ModalGCube = "modal_G";
    public const string ModalKCube = "modal_K";

    private static string TerminalName(Em3dPort p) => p.SourceLabel ?? p.Name;

    private static string Ordinals(int n) => n == 2 ? "1 and 2" : string.Join(", ", Enumerable.Range(1, n - 1)) + $" and {n}";

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-8 * Math.Max(Math.Abs(a), Math.Abs(b));
    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Fmt4(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string G4(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string Signed(double v) => (v < 0 ? "−" : "+") + Math.Abs(v).ToString("0.000", CultureInfo.InvariantCulture);
    private static string Sci(double v) => v == 0 ? "0" : v.ToString("0e0", CultureInfo.InvariantCulture);
    private static string Percent(double v) => (100 * v).ToString("0.##", CultureInfo.InvariantCulture) + " %";
}
