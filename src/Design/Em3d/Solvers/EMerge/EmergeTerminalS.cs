// brief-emw-3 R-emw3-5 — an EMerge run with terminal wave ports: the arrays its driver writes beside S, the modal sample the
// conversion reads, formed from the face's own modes, and the checks only EMerge's run makes.
//
// The 2D mode solve alone is enough (R-emw1-4): a terminal's voltage is U = T_V·C with C = W⁻¹(1 + S), T_V each path's
// integral of each mode and W the E-overlap EMerge's S projection uses, so the conversion needs no 3D field and runs under
// the adaptive sweep. EMerge's modes carry 1 W, so the transform takes V / √2 (R-emw1-5). The conversion itself, its route
// note and the checks Palace's run also makes are WavePortTerminalS's, with ModalRobinForm.SharedWavenumber.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;
using NumFlat;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>What a driven run with a terminal group writes besides S, per requested frequency, over every wave-port entry in
/// port order: T_V, W and the power Gram [f][i, j] (block-diagonal by face), and the measured V [f][entry, excitation] where
/// the run solved that frequency directly (null otherwise: an adaptive sweep).</summary>
public sealed record EmergeModalArrays(Complex[][,] TV, Complex[][,] W, Complex[][,] Gp, Complex[][,]? V);

public static class EmergeTerminalS
{
    public const string TVFile = "TV.npy", WFile = "W.npy", GpFile = "Gp.npy", VFile = "V.npy";

    /// <summary>The largest |V_measured/√2 − V_formed| over a face's largest voltage above which the run warns. EMW-1 measured
    /// 1–2e-4 on faces of one material and of one mode, and 4.5e-3 on the Coupled Microstrip's two-mode faces.</summary>
    public const double MeasuredVLimit = 0.01;

    /// <summary>The power Gram of each face's modes, ½∫eₙ × hₘ* · n, in the EMerge group beside the conversion's diagnostics.</summary>
    public const string ModalGpCube = "modal_Gp";

    /// <summary>The arrays in <paramref name="runDir"/>, checked against <paramref name="frequencies"/> and
    /// <paramref name="ports"/>; null with a sentence when one is missing or the shapes disagree. V is optional.</summary>
    public static EmergeModalArrays? Read(string runDir, int frequencies, int ports, out string? error)
    {
        error = null;
        try
        {
            Complex[][,]? Load(string file, bool optional)
            {
                string path = Path.Combine(runDir, file);
                if (optional && !File.Exists(path)) return null;
                var (v, shape) = PlainNpy.ReadComplex(path);
                if (shape is not [var f, var a, var b] || f != frequencies || a != ports || b != ports)
                    throw new InvalidDataException($"EMerge's {file} is [{string.Join(", ", shape)}] where [{frequencies}, {ports}, {ports}] was expected.");
                var m = new Complex[f][,];
                for (int k = 0; k < f; k++)
                {
                    m[k] = new Complex[ports, ports];
                    for (int i = 0; i < ports; i++)
                        for (int j = 0; j < ports; j++) m[k][i, j] = v[(k * ports + i) * ports + j];
                }
                return m;
            }
            return new EmergeModalArrays(Load(TVFile, false)!, Load(WFile, false)!, Load(GpFile, false)!, Load(VFile, true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"EMerge's terminal-port arrays could not be read: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// One frequency's modal sample as §5 forms it: <c>ModalS</c> = S, each face's rows of V = T_V·W⁻¹·(1 + S) / √2,
    /// Z_PV = |T_V,kk|² / 2 and kₙ = β = γ / j, all from the face's own modes.
    /// </summary>
    public static ModalTerminalSample Sample(Complex[,] modalS, Complex[,] tv, Complex[,] w, IReadOnlyList<Complex> gamma,
                                             IReadOnlyList<IReadOnlyList<int>> faces)
    {
        int n = modalS.GetLength(0);
        return new ModalTerminalSample(modalS, FormedV(modalS, tv, w, faces),
                                       [.. Enumerable.Range(0, n).Select(i => tv[i, i].Magnitude * tv[i, i].Magnitude / 2)],
                                       [.. gamma.Select(g => -Complex.ImaginaryOne * g)]);
    }

    /// <summary>Each face's rows of T_V·W⁻¹·(1 + S) / √2: the terminal voltages the face's modes give for every excitation,
    /// in the transform's normalisation.</summary>
    public static Complex[,] FormedV(Complex[,] modalS, Complex[,] tv, Complex[,] w, IReadOnlyList<IReadOnlyList<int>> faces)
    {
        int n = modalS.GetLength(0);
        var v = new Complex[n, n];
        foreach (var face in faces)
        {
            int k = face.Count;
            var tvf = new Mat<Complex>(k, k);
            var wf = new Mat<Complex>(k, k);
            var pf = new Mat<Complex>(k, n);
            for (int a = 0; a < k; a++)
            {
                for (int b = 0; b < k; b++) { tvf[a, b] = tv[face[a], face[b]]; wf[a, b] = w[face[a], face[b]]; }
                for (int j = 0; j < n; j++) pf[a, j] = modalS[face[a], j] + (face[a] == j ? Complex.One : Complex.Zero);
            }
            var block = tvf * wf.Inverse() * pf;
            for (int a = 0; a < k; a++)
                for (int j = 0; j < n; j++) v[face[a], j] = block[a, j] / Math.Sqrt(2);
        }
        return v;
    }

    /// <summary>
    /// R-emw3-5 — the checks EMerge's run makes in place of the power-convention and off-face checks Palace's makes, which a V
    /// formed from the modes passes by construction. Where the run measured V (<paramref name="arrays"/>' V, at frequencies
    /// it solved directly): per terminal face the largest |V_measured/√2 − V_formed| over its largest voltage (a note; a
    /// warning above <see cref="MeasuredVLimit"/>), and the part of the measured V no face's own modes explain (a warning above
    /// <see cref="WavePortTerminalS.OffBlockLimit"/>). Without a measured V, a note that neither was made. <paramref name="modalS"/>
    /// is the raw modal S, before any reference plane is moved.
    /// </summary>
    public static (List<string> Notes, List<string> Warnings) MeasuredChecks(IReadOnlyList<Em3dPort> ports, double[] frequenciesHz,
                                                                             Complex[][,] modalS, EmergeModalArrays arrays)
    {
        var notes = new List<string>();
        var warnings = new List<string>();
        if (arrays.V is not { } measured)
        {
            notes.Add("The terminal voltages were not checked against the field, nor whether a terminal reads another port face's " +
                      "modes: EMerge's adaptive sweep solved no requested frequency directly, so no voltage was measured in the field. " +
                      "A run with EMerge.AdaptiveSweep off makes both checks.");
            return (notes, warnings);
        }
        var faces = WavePortTerminalS.Faces(ports);
        string At(int k) => $"{WavePortTerminalS.Fmt(frequenciesHz[k] / 1e9)} GHz";
        var perFace = new List<string>();
        foreach (var face in faces.Where(f => f.Count > 1))
        {
            double worst = 0;
            int at = 0;
            for (int k = 0; k < frequenciesHz.Length; k++)
            {
                var formed = FormedV(modalS[k], arrays.TV[k], arrays.W[k], faces);
                double vMax = 0, diff = 0;
                foreach (int i in face)
                    for (int j = 0; j < ports.Count; j++)
                    {
                        var m = measured[k][i, j] / Math.Sqrt(2);
                        vMax = Math.Max(vMax, m.Magnitude);
                        diff = Math.Max(diff, (m - formed[i, j]).Magnitude);
                    }
                double share = vMax > 0 ? diff / vMax : 0;
                if (share > worst) (worst, at) = (share, k);
            }
            string label = ports[face[0]].FaceGroupLabel ?? ports[face[0]].Name;
            perFace.Add($"port '{label}' {WavePortTerminalS.Percent(worst)} (at {At(at)})");
            if (worst > MeasuredVLimit)
                warnings.Add($"Port '{label}': the terminal voltages measured in EMerge's field disagree with the face's modes by " +
                             $"{WavePortTerminalS.Percent(worst)} at {At(at)}, so the terminal S is not to be trusted. Refine the mesh " +
                             "at the port face (a mesh region along the strips' edges).");
        }
        notes.Add("Terminal voltages measured in EMerge's field against those the faces' modes give (the largest difference over " +
                  $"the face's largest voltage): {string.Join("; ", perFace)}.");
        double off = 0;
        int offAt = 0;
        for (int k = 0; k < frequenciesHz.Length; k++)
        {
            var v = new Complex[ports.Count, ports.Count];
            for (int i = 0; i < ports.Count; i++)
                for (int j = 0; j < ports.Count; j++) v[i, j] = measured[k][i, j] / Math.Sqrt(2);
            double share = ModalTerminalTransform.OffFaceResidual(v, modalS[k], faces);
            if (share > off) (off, offAt) = (share, k);
        }
        if (off > WavePortTerminalS.OffBlockLimit) warnings.Add(WavePortTerminalS.OffFaceWarning(off, At(offAt)));
        return (notes, warnings);
    }

    /// <summary>The power Gram per frequency as a cube [f, i, j] over the ports, for <see cref="ModalGpCube"/>.</summary>
    public static DataCube GramCube(double[] frequenciesHz, IReadOnlyList<Em3dPort> ports, Complex[][,] gp)
    {
        int nf = frequenciesHz.Length, n = ports.Count;
        double[] numbers = [.. ports.Select(p => (double)p.Number)];
        var v = new Complex[nf * n * n];
        for (int k = 0; k < nf; k++)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) v[(k * n + i) * n + j] = gp[k][i, j];
        return new DataCube([new Axis("f", frequenciesHz, "Hz"), new Axis("i", numbers, ""), new Axis("j", numbers, "")], v);
    }

    /// <summary>Why a run that asked for fields saved none for its terminal ports.</summary>
    public static string FieldsNote(IReadOnlyList<Em3dPort> ports)
        => "No field was saved for the terminal ports " +
           string.Join(", ", ports.Where(p => p.FaceGroup is not null).Select(p => p.Number.ToString(CultureInfo.InvariantCulture))) +
           ": EMerge excites a terminal face by its modes, and a terminal's own excitation is a mixture of them circuitRF does " +
           "not form." + (ports.Any(p => p.FaceGroup is null) ? " The other ports' fields are saved." : "");
}
