// brief-emw-2 R-emw2-4 — what a wave port's S is referred to, and how circuitRF moves it to what the .sNp states. Shared by
// every solver that refers a wave port's S to the port's own mode at unit power (Palace, and EMerge from EMW-2): the
// reference-plane shift in the mode's own normalisation, then the renormalisation from the mode's impedance Z_PV to the
// port's Z0, and the run's note saying both. Moved here out of Palace's partial (R-em3d23-2d) so the two solvers share one.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;
using NumFlat;
using RfCore;

namespace CircuitRF.Design.Em3d;

internal static class WavePortReferences
{
    private static string Fmt(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

    /// <summary>
    /// R-em3d23-2d — <paramref name="s"/> with every port moved from <paramref name="from"/>[k][i] (per frequency, per port)
    /// to its stated Z0: the whole matrix per frequency (RfCore's <see cref="RFNetwork.SToS"/>), never an element at a time.
    /// </summary>
    public static Complex[][,] Renormalise(Complex[][,] s, IReadOnlyList<Em3dPort> ports, IReadOnlyList<Complex[]> from)
    {
        int n = ports.Count;
        var target = ports.Select(p => p.Z0).ToArray();
        var mats = new Complex[s.Length][,];
        for (int k = 0; k < s.Length; k++)
        {
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = s[k][i, j];
            var r = RFNetwork.SToS(m, from[k], target);
            mats[k] = new Complex[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) mats[k][i, j] = r[i, j];
        }
        return mats;
    }

    /// <summary>
    /// Palace's S with every wave port moved from its mode's impedance (<paramref name="modeZ"/>[k][w], <paramref name="wave"/>'s
    /// order) to its stated Z0. A lumped port is already at its Z0 (its resistance), so its reference is unchanged.
    /// </summary>
    public static PalacePortS Renormalise(PalacePortS s, IReadOnlyList<Em3dPort> ports, IReadOnlyList<Em3dPort> wave, Complex[][] modeZ)
    {
        var at = wave.Select(w => ports.ToList().IndexOf(w)).ToArray();
        var from = new Complex[s.S.Length][];
        for (int k = 0; k < s.S.Length; k++)
        {
            from[k] = [.. ports.Select(p => p.Z0)];
            for (int w = 0; w < wave.Count; w++) from[k][at[w]] = modeZ[k][w];
        }
        return s with { S = Renormalise(s.S, ports, from) };
    }

    /// <summary>
    /// The reference-plane shift, in the mode's own normalisation: S′ᵢⱼ = Sᵢⱼ·e^(γᵢdᵢ)·e^(γⱼdⱼ), dᵢ port i's
    /// <see cref="Em3dReferencePlane.ShiftM"/> (positive into the structure) and γᵢ = αᵢ + jβᵢ its mode's propagation constant
    /// at that frequency (<paramref name="gamma"/>[k][i]; 0 for a lumped port, which moves nothing). It must come BEFORE the
    /// renormalisation: renormalising mixes incident and reflected waves, and the shift is exact only on a matched line.
    /// </summary>
    public static Complex[][,] Shift(Complex[][,] s, IReadOnlyList<Em3dPort> ports, IReadOnlyList<Complex[]> gamma)
    {
        int n = ports.Count;
        var mats = new Complex[s.Length][,];
        for (int k = 0; k < s.Length; k++)
        {
            var e = new Complex[n];
            for (int i = 0; i < n; i++) e[i] = Complex.Exp(gamma[k][i] * ports[i].ReferencePlane.ShiftM);
            mats[k] = new Complex[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) mats[k][i, j] = s[k][i, j] * e[i] * e[j];
        }
        return mats;
    }

    /// <summary>What each wave port's S was referred to, and what it is now. <paramref name="source"/> says where the mode's
    /// impedance came from, in parentheses after Z_PV.</summary>
    public static string Note(string solver, string source, IReadOnlyList<Em3dPort> wave, IReadOnlyList<Complex[]> modeZ)
    {
        var parts = wave.Select((p, w) =>
        {
            var re = modeZ.Select(z => z[w].Real).ToList();
            string range = Fmt(re.Min()) == Fmt(re.Max()) ? $"{Fmt(re[0])} Ω" : $"{Fmt(re.Min())} to {Fmt(re.Max())} Ω";
            return $"port {p.Number}'s mode impedance is {range} over the sweep, renormalised to {Fmt(p.Z0.Real)} Ω";
        });
        return $"{solver} refers a wave port's S-parameters to the port's own mode, normalised to unit power — that is, to the " +
               $"mode's impedance Z_PV ({source}), which varies with frequency. The .sNp states one real reference per port, " +
               $"so the wave ports were renormalised: {string.Join("; ", parts)}.";
    }

    /// <summary>One line per wave port whose reference plane moved: how far, and that the mode's β de-embedded it.</summary>
    public static IEnumerable<string> ShiftNotes(IEnumerable<Em3dPort> wave)
        => wave.Where(p => p.ReferencePlane.ShiftM != 0).Select(p =>
               $"Port {p.Number}'s reference plane is {Fmt(Math.Abs(p.ReferencePlane.ShiftM) * 1e3)} mm " +
               $"{(p.ReferencePlane.ShiftM > 0 ? "into" : "out of")} the structure: its line was de-embedded with the mode's β.");
}
