// brief-emw-3 R-emw3-2 — a run with terminal wave ports, whichever solver ran it: which ports share a face, the transform
// per frequency, and what the run says about it (its route note and the brief-em3d-115 §5 checks). Moved out of
// PalaceTerminalS, which keeps what is Palace's own: reading port-S.csv, port-V.csv, port-Z.csv and the log's kₙ. A
// solver's run hands this its display name and the Robin form its port condition needs; every sentence differs between
// solvers by that name alone, so Palace's notes and warnings are what they were.

using System.Globalization;
using CircuitRF.Engine.Em3d;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>One modal sample per frequency, in port order: what the transform reads.</summary>
public sealed record WavePortModalSamples(double[] FrequenciesHz, ModalTerminalSample[] Samples);

/// <summary>The terminal S and the transform's own result at each frequency.</summary>
public sealed record WavePortTerminalOutcome(PalacePortS S, ModalTerminalResult[] PerFrequency);

public static class WavePortTerminalS
{
    /// <summary>A Gram fit whose largest relative residual is above this has no root consistent with the solver's Z_PV.</summary>
    public const double GramResidualLimit = 1e-3;

    /// <summary>|T_V,ii|² against Z_PV after the fit, relative: above this Palace's power convention has changed (PR #937).</summary>
    public const double PowerConventionLimit = 1e-3;

    /// <summary>The largest entry of M outside a face's own block, relative to M's largest: a terminal reading another face.</summary>
    public const double OffBlockLimit = 1e-2;

    /// <summary>On a problem with no loss mechanism, a per-port power or singular value further than this from 1.</summary>
    public const double LosslessLimit = 0.002;

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
    /// The terminal S of every row, each port at its own Z0, with the Robin form <paramref name="robin"/> the solver's port
    /// condition needs. <paramref name="ports"/> are the run's wave ports in its order. Null, with the sentence naming the
    /// frequency, when a row cannot be transformed.
    /// </summary>
    public static WavePortTerminalOutcome? Convert(WavePortModalSamples samples, IReadOnlyList<Em3dPort> ports, ModalRobinForm robin,
                                                   out string? error)
    {
        error = null;
        var faces = Faces(ports);
        double[] z0 = [.. ports.Select(p => p.Z0.Real)];
        var results = new ModalTerminalResult[samples.Samples.Length];
        var mats = new System.Numerics.Complex[samples.Samples.Length][,];
        for (int k = 0; k < results.Length; k++)
        {
            results[k] = ModalTerminalTransform.Solve(samples.Samples[k], faces, z0, robin: robin);
            if (results[k].Error is { } why)
            {
                error = $"At {Fmt(samples.FrequenciesHz[k] / 1e9)} GHz: {why}";
                return null;
            }
            mats[k] = results[k].S!;
        }
        return new WavePortTerminalOutcome(new PalacePortS(samples.FrequenciesHz, mats), results);
    }

    /// <summary>
    /// §5's route note, one per terminal face: <c>Port 'Left': terminals P1, P2 as modes 1 and 2 of one Palace wave port,
    /// converted to terminal S (degenerate face: Gram fit g = −0.501, residual 2e-6).</c> Over a sweep, g's range and the
    /// largest residual. <paramref name="solver"/> is the solver's display name.
    /// </summary>
    public static IEnumerable<string> RouteNotes(IReadOnlyList<Em3dPort> ports, WavePortTerminalOutcome outcome, string solver)
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
                         $"{solver} wave port, converted to terminal S ({detail}).";
        }
    }

    /// <summary>
    /// §5's checks as the run's notes (reported) and warnings (outside a bound). <paramref name="powerConvention"/> runs the
    /// check that |T_V,ii|² gives the solver's Z_PV (Palace's; EMerge forms V from its modes, so it would pass by
    /// construction); <paramref name="offFace"/> the check that no terminal reads another face's modes from the transform's
    /// V (EMerge runs its own on the V it measured).
    /// </summary>
    public static (List<string> Notes, List<string> Warnings) Checks(IReadOnlyList<Em3dPort> ports, WavePortTerminalOutcome outcome,
                                                                     bool lossless, string solver, bool powerConvention = true,
                                                                     bool offFace = true)
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
                             $"{Sci(r[k].Faces[fi].Residual)} at {At(k)}, so it has no root consistent with {solver}'s mode impedance Z_PV and " +
                             "the terminal S there is not to be trusted. Refine the mesh at the port face (a mesh region along the strips' " +
                             "edges), or run the setup on openEMS.");
        }
        int tv = Worst(x => x.TvMismatch.Max());
        if (powerConvention && r[tv].TvMismatch.Max() > PowerConventionLimit)
        {
            int i = Array.IndexOf(r[tv].TvMismatch, r[tv].TvMismatch.Max());
            warnings.Add($"Port {ports[i].Number}: the terminal voltage of its own mode does not give {solver}'s Z_PV (off by " +
                         $"{Percent(r[tv].TvMismatch[i])} at {At(tv)}). The transform assumes {solver}'s unit-power mode convention, so a " +
                         $"{solver} that changed it gives a wrong terminal S. Check the installed {solver} is a version circuitRF validates.");
        }
        int ob = Worst(x => x.MOffBlock);
        if (offFace && r[ob].MOffBlock > OffBlockLimit) warnings.Add(OffFaceWarning(r[ob].MOffBlock, At(ob)));

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
                             $"{G4(w.SigmaMax)}). The conversion from {solver}'s modes is not exact there: usually the mesh at the port " +
                             "face, which a mesh region along the strips' edges refines.");
        }
        return (notes, warnings);
    }

    /// <summary>
    /// brief-em3d-115 R-em3d115-5 — the diagnostics a disputed result is re-derived from, in <paramref name="group"/>: the modal S and
    /// V_wp [f, i, j], Z_PV and kₙ [f, Port], G [f, i, j] and K [f, Port].
    /// </summary>
    public static void AddDiagnostics(DataSet data, string group, WavePortModalSamples samples, WavePortTerminalOutcome outcome,
                                      IReadOnlyList<Em3dPort> ports)
    {
        int nf = samples.FrequenciesHz.Length, n = ports.Count;
        var fAxis = new Axis("f", outcome.S.FrequenciesHz, "Hz");
        double[] numbers = [.. ports.Select(p => (double)p.Number)];
        DataCube Matrix(Func<int, System.Numerics.Complex[,]> of, string unit = "")
        {
            var v = new System.Numerics.Complex[nf * n * n];
            for (int k = 0; k < nf; k++)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) v[(k * n + i) * n + j] = of(k)[i, j];
            return new DataCube([fAxis, new Axis("i", numbers, ""), new Axis("j", numbers, "")], v) { Unit = unit };
        }
        DataCube Vector(Func<int, int, System.Numerics.Complex> of, string unit = "")
        {
            var v = new System.Numerics.Complex[nf * n];
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

    public const string ModalSCube = "modal_S";
    public const string ModalVCube = "modal_V";
    public const string ModalZpvCube = "modal_Zpv";
    public const string ModalKnCube = "modal_kn";
    public const string ModalGCube = "modal_G";
    public const string ModalKCube = "modal_K";

    /// <summary>The off-face check's warning: <paramref name="share"/> of the largest voltage, at <paramref name="at"/>.</summary>
    public static string OffFaceWarning(double share, string at)
        => $"A terminal reads another port face's modes ({Percent(share)} of the largest voltage at {at}): " +
           "two port faces are coupled through their modes, which the transform does not model. Move the faces apart, " +
           "or check that no terminal's voltage path crosses onto another face.";

    private static string TerminalName(Em3dPort p) => p.SourceLabel ?? p.Name;

    private static string Ordinals(int n) => n == 2 ? "1 and 2" : string.Join(", ", Enumerable.Range(1, n - 1)) + $" and {n}";

    internal static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    internal static string G4(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string Signed(double v) => (v < 0 ? "−" : "+") + Math.Abs(v).ToString("0.000", CultureInfo.InvariantCulture);
    internal static string Sci(double v) => v == 0 ? "0" : v.ToString("0e0", CultureInfo.InvariantCulture);
    internal static string Percent(double v) => (100 * v).ToString("0.##", CultureInfo.InvariantCulture) + " %";
}
