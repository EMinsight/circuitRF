// brief-em3d-100 — what drive a driven field is shown at, and the drive a PLOT asks for.
//
// THE SOLUTION'S DRIVE (R-em3d100-1). Each driven step is the field of ONE port driven, every other port terminated in its own
// Z0. Palace writes it at unit incident power in its own convention, which is PalaceDrive.IncidentPowerW in circuitRF's. An
// openEMS dump is a DFT of the response to a Gaussian pulse: its magnitude is set by how much energy the pulse has at that
// frequency, so it is referred to nothing until it is divided by the driven port's own incident wave. FieldPortDrive carries
// what a step needs for both: the port, its Z0 in the run, its reflection at the step's frequency, and — openEMS — the complex
// scale its dumps are loaded with (DumpScale), which puts them at Palace's incident wave, phase zero. That scale is the one
// expression below; nothing else in this folder divides a field by a voltage.
//
// Z0 IS THE RUN'S. It is read from the document the run kept beside its fields (brief-em3d-87's document.c3d), never from the
// document open now: a Z0 edited since the run would otherwise rescale an old field in silence. A run that kept no document,
// or (openEMS) no probe of the driven port, is drawn as written and says so (D3): its numbers are relative.
//
// THE PLOT'S DRIVE (R-em3d100-3). The problem is linear, so a drive is post-processing: an amplitude scales by √k and a power
// or energy density by k (FieldNames.DriveExponent), with k the plot's power over the solver's. FieldDriveReading is that
// factor for ONE plot's quantity, applied to that plot's own sampled values — never to the step two plots share.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>
/// brief-em3d-100 — the drive a driven step was solved at: the driven port (the result's number), its reference impedance in
/// the run (null when the run kept none), its reflection at the step's frequency (null, with <see cref="NoReflection"/>, when the
/// run recorded none there), and the complex scale the step's arrays are loaded with (1 for Palace). <see cref="Referred"/>
/// false: an openEMS run that kept no port record, drawn in relative values.
/// </summary>
public sealed record FieldPortDrive(int Port, Complex? Z0, Complex? Reflection, Complex DumpScale, bool Referred, string? NoReflection = null);

/// <summary>brief-em3d-100 — one plot's drive, for its quantity: the factor its sampled values are multiplied by, the legend's
/// line, why it cannot be shown (Accepted with no reflection), and whether the values are relative (no unit).</summary>
public sealed record FieldDriveReading(double Factor, string? Line, string? Problem, bool Relative)
{
    /// <summary>Nothing to say and nothing to scale: not a driven field, or a quantity no drive moves.</summary>
    public static readonly FieldDriveReading None = new(1, null, null, false);
}

public static class FieldDrive
{
    /// <summary>D3 — the legend of an openEMS run with no port record.</summary>
    public const string NotReferredLine = "Drive: not referred, this run kept no port record (relative values)";

    /// <summary>Below this, 1 − |Γ|² is a port that accepts nothing: Accepted is refused rather than scaled toward infinity.</summary>
    public const double MinAccepted = 1e-6;

    /// <summary>
    /// R-em3d100-1 — the complex scale an openEMS dump of a port driven with port voltage <paramref name="u"/> and current
    /// <paramref name="i"/> (FdtdPortTransform.Dft at the dump's frequency, each probe on its own time column) is loaded with.
    /// The convention: openEMS's frequency-domain dump carries a ×2 against the probes' DFT (OpenEmsFarField's header), so it is
    /// halved first, as OpenEmsPattern halves it; the probes' incident wave is v = (U + Z0·I)/2 (FdtdPortTransform: U + Z0·I is
    /// the incident-wave matrix); dividing by it puts the field at a 1 V incident wave, phase zero; and multiplying by
    /// √(2·R·P) puts it at the peak incident voltage of <see cref="PalaceDrive.IncidentPowerW"/> — Palace's own V_inc = √R —
    /// so an openEMS and a Palace plot of one structure read the same numbers. Null when the port has no incident wave there.
    /// </summary>
    public static Complex? OpenEmsDumpScale(Complex u, Complex i, Complex z0)
    {
        var vInc = (u + z0 * i) / 2;
        if (!(vInc.Magnitude > 0) || !(z0.Real > 0)) return null;
        return 0.5 / vInc * PalaceDrive.IncidentPeakVolts(z0.Real);
    }

    /// <summary>The driven port's reflection from its own U and I: Γ = (U − Z0·I)/(U + Z0·I), FdtdPortTransform's waves.</summary>
    public static Complex OpenEmsReflection(Complex u, Complex i, Complex z0) => (u - z0 * i) / (u + z0 * i);

    // ── discovery: each driven step's drive, read once with the run ─────────────────────────────

    /// <summary>
    /// brief-em3d-100 — the drive of each of a Palace run's driven steps: the port it excites (the excitation's number, or with
    /// one excitation the one port that has one), Z0 from the run's kept document (else the port's R in config.json), and Γ from
    /// port-V.csv's row at the step's frequency — read, never interpolated.
    /// </summary>
    internal static IReadOnlyList<FieldSolution> Palace(string runDir, IReadOnlyList<FieldSolution> steps)
    {
        if (steps.All(s => s.Kind != FieldProblemKind.Driven)) return steps;
        var (resistance, excited) = PalaceConfigPorts(runDir);
        var document = RunPortZ0s(runDir);
        string csv = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory, PalaceRun.PortVFile);
        var tables = new Dictionary<int, (double[] F, double[][] Inc, Complex[][] Tot)?>();
        var list = new List<FieldSolution>(steps.Count);
        foreach (var s in steps)
        {
            if (s.Kind != FieldProblemKind.Driven) { list.Add(s); continue; }
            int port = s.Excitation > 0 ? s.Excitation : excited.Count == 1 ? excited[0] : 0;
            if (port <= 0) { list.Add(s with { Drive = new FieldPortDrive(0, null, null, 1, true, null) }); continue; }
            Complex? z0 = document is not null && document.TryGetValue(port, out var dz) ? dz : resistance.TryGetValue(port, out double r) ? r : null;
            if (!tables.TryGetValue(port, out var table))
                tables[port] = table = File.Exists(csv) ? PalaceRun.ReadPortV(csv, [port], out _) : null;
            Complex? gamma = null;
            string? why = null;
            if (table is not { } t)
                why = $"Palace wrote no port voltage for port {port}, so the accepted power is not known; Incident still works.";
            else
            {
                int row = Array.FindIndex(t.F, g => Math.Abs(g - s.Timestep) <= 1e-7 * Math.Max(Math.Abs(g), Math.Abs(s.Timestep)));
                if (row < 0)
                    why = $"Palace wrote no port voltage at {G(s.Timestep)} GHz, so the accepted power there is not known; Incident still works.";
                else if (t.Inc[row][0] is var vi && vi != 0)
                    gamma = (t.Tot[row][0] - vi) / vi;
                else why = $"Palace's port voltage for port {port} at {G(s.Timestep)} GHz has no incident wave, so the accepted power is not known; Incident still works.";
            }
            list.Add(s with { Drive = new FieldPortDrive(port, z0, gamma, 1, true, why) });
        }
        return list;
    }

    /// <summary>
    /// brief-em3d-100 R-em3d100-1 — the drive of each of an openEMS run's steps: port k's own probes in <c>p&lt;k&gt;/</c>, read
    /// once per port and transformed at every dump frequency of that port (FdtdPortTransform.Dft, the function S was formed
    /// with), Z0 from the run's kept document. A port whose probes, or whose Z0, the run did not keep is not referred (D3).
    /// </summary>
    internal static IReadOnlyList<FieldSolution> OpenEms(string runDir, IReadOnlyList<FieldSolution> steps)
    {
        var document = RunPortZ0s(runDir);
        var list = new List<FieldSolution>(steps.Count);
        foreach (var port in steps.GroupBy(s => s.Excitation))
        {
            int k = port.Key;
            Complex[]? u = null, i = null;
            Complex? z0 = document is not null && document.TryGetValue(k, out var dz) ? dz : null;
            if (z0 is not null)
            {
                string dir = Path.Combine(runDir, OpenEmsRun.PortDirectory(k));
                var up = OpenEmsRun.ReadProbe(Path.Combine(dir, CsxcadWriter.VoltageProbe(k)), out _);
                var ip = OpenEmsRun.ReadProbe(Path.Combine(dir, CsxcadWriter.CurrentProbe(k)), out _);
                if (up is not null && ip is not null)
                {
                    double[] hz = [.. port.Select(s => s.FrequencyHz)];
                    u = FdtdPortTransform.Dft(up, hz);
                    i = FdtdPortTransform.Dft(ip, hz);
                }
            }
            int at = 0;
            foreach (var s in port)
            {
                FieldPortDrive drive;
                if (u is null || i is null || OpenEmsDumpScale(u[at], i[at], z0!.Value) is not { } scale)
                    drive = new FieldPortDrive(k, z0, null, 1, false);
                else drive = new FieldPortDrive(k, z0, OpenEmsReflection(u[at], i[at], z0.Value), scale, true);
                list.Add(s with { Drive = drive });
                at++;
            }
        }
        return [.. list.OrderBy(s => s.Timestep).ThenBy(s => s.Excitation)];
    }

    /// <summary>
    /// Each result port's Z0 in the document a run kept (brief-em3d-87), by the RESULT's number: the modelled ports in their
    /// document order, renumbered 1…N when any is off (C3dModelled.Renumber). Null when the run kept no readable document.
    /// </summary>
    public static Dictionary<int, Complex>? RunPortZ0s(string runDir)
    {
        string path = C3dRunDocument.PathIn(runDir);
        if (!File.Exists(path)) return null;
        C3dDocument doc;
        try { doc = C3dPersistence.Deserialize(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
        // brief-em3d-114 — a terminal is a port: each one, numbered and referenced as itself
        var modelled = doc.Ports.Where(p => p.Model)
                          .SelectMany(p => p.Terminals is { Count: > 0 } ts ? ts.Select(t => (t.Number, t.Z0)) : [(p.Number, p.Z0)])
                          .OrderBy(e => e.Number).ToList();
        bool renumbered = doc.Ports.Any(p => !p.Model);
        var map = new Dictionary<int, Complex>();
        for (int n = 0; n < modelled.Count; n++)
            if (C3dPorts.TryParseZ0(modelled[n].Z0, out var z)) map[renumbered ? n + 1 : modelled[n].Number] = z;
        return map;
    }

    /// <summary>A Palace configuration's lumped ports: each one's R, and the ports with an excitation.</summary>
    private static (Dictionary<int, double> R, List<int> Excited) PalaceConfigPorts(string runDir)
    {
        var r = new Dictionary<int, double>();
        var excited = new List<int>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "config.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("Boundaries", out var b)) return (r, excited);
            foreach (string kind in (string[])["LumpedPort", "WavePort"])
                if (b.TryGetProperty(kind, out var ports) && ports.ValueKind == JsonValueKind.Array)
                    foreach (var p in ports.EnumerateArray())
                    {
                        if (!p.TryGetProperty("Index", out var ix) || !ix.TryGetInt32(out int n)) continue;
                        if (p.TryGetProperty("R", out var rv) && rv.TryGetDouble(out double ohms)) r[n] = ohms;
                        if (p.TryGetProperty("Excitation", out var ex) && ex.ValueKind is JsonValueKind.True or JsonValueKind.Number) excited.Add(n);
                    }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return (r, excited);
    }

    // ── a plot's drive ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d100-3 — plot drive <paramref name="powerW"/> (null: the solver's own, <see cref="PalaceDrive.IncidentPowerW"/>)
    /// referred to <paramref name="to"/>, for array <paramref name="array"/> of step <paramref name="s"/>: the factor its values
    /// take (k^exponent, k the power over the solver's — over 1 − |Γ|² too, for Accepted), and the legend's line. Not a driven
    /// step, or a quantity no drive moves: <see cref="FieldDriveReading.None"/>.
    /// </summary>
    public static FieldDriveReading Read(FieldSolution s, string array, double? powerW, C3dDriveReferredTo to)
    {
        if (s.Kind != FieldProblemKind.Driven || s.Drive is not { } d) return FieldDriveReading.None;
        if (!d.Referred) return new FieldDriveReading(1, NotReferredLine, null, true);
        if (FieldNames.DriveExponent(array) is not { } x)
            return new FieldDriveReading(1, $"Drive: {FieldNames.Friendly(array)} is not referred to the drive", null, false);
        if (x == 0) return FieldDriveReading.None;
        double p = powerW is > 0 ? powerW.Value : PalaceDrive.IncidentPowerW;
        double k = p / PalaceDrive.IncidentPowerW;
        string what;
        if (to == C3dDriveReferredTo.Accepted)
        {
            if (d.Reflection is not { } g)
                return new FieldDriveReading(1, null, d.NoReflection ??
                    $"The run recorded no reflection {(d.Port > 0 ? $"at port {d.Port} " : "")}at {G(s.Timestep)} GHz, so the accepted power is not known; Incident still works.", false);
            double accepts = 1 - g.Magnitude * g.Magnitude;
            string s11 = $"|S{d.Port}{d.Port}| {(20 * Math.Log10(Math.Max(g.Magnitude, 1e-15))).ToString("0.0", CultureInfo.InvariantCulture).Replace('-', '−')} dB";
            if (!(accepts >= MinAccepted))
                return new FieldDriveReading(1, null,
                    $"Port {d.Port} accepts no power at {G(s.Timestep)} GHz ({s11}), so a power accepted there cannot be shown; Incident still works.", false);
            k /= accepts;
            what = $"{C3dDrivePower.Format(p)} accepted ({s11})";
        }
        else what = $"{C3dDrivePower.Format(p)} incident (available)";
        string z0 = d.Z0 is { } z ? $", Z₀ {C3dPorts.FormatZ0(z)} Ω" : "";
        string port = d.Port > 0 ? $" on port {d.Port}" : "";
        return new FieldDriveReading(Math.Pow(k, x), $"Drive: {what}{port}{z0}, peak", null, false);
    }

    /// <summary>Multiplies every value of <paramref name="surfaces"/> by <paramref name="factor"/> — each surface once, however
    /// often it is listed. They are this plot's own, freshly built: the step's arrays are never touched.</summary>
    public static void Apply(IEnumerable<FieldSurface> surfaces, double factor)
    {
        if (factor == 1) return;
        var seen = new HashSet<FieldSurface>(ReferenceEqualityComparer.Instance);
        foreach (var s in surfaces)
        {
            if (!seen.Add(s)) continue;
            var v = s.Values;
            for (int i = 0; i < v.Length; i++) v[i] *= factor;
        }
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
