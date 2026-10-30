// brief-sar-12 R-sar12-3 / R-sar12-4 — the phone SAR workflow's bands, rated power and published limits.
//
// ONE SAR FREQUENCY PER RUN (overview L6). A band sweep is a template setup and a list of bands; Create makes one ordinary
// setup per band — the template's, named "<sweep> <f> GHz", its Sar group at that one frequency (so every frequency-dependent
// material, the phantom's liquid above all, is evaluated there) and its sweep scaled by f / f_template, so each band keeps the
// template's relative bandwidth and a grid drawn from its own highest frequency. Each is then run like any other setup; nothing
// in a run knows it came from a sweep.
//
// RATED POWER. Every SAR number a run writes is per 1 W accepted (sar.md §8.4). The table multiplies by the power the device
// delivers: DrivePowerW referred to the accepted power (the default — the conducted power reaching the antenna), or to the
// incident power, when the antenna's own mismatch takes its share (|S_kk|² read from the run's Touchstone at the band). A duty
// cycle, when given, multiplies the result again: a burst transmitter's time-averaged SAR.
//
// LIMITS ARE REFERENCE VALUES (overview L1). Each is cited and dated; a number above one is coloured by the caller and the row
// says which reference it is above. Nothing here says pass or fail, and nothing claims compliance with anything.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>Which body region a limit applies to.</summary>
public enum C3dSarExposure
{
    /// <summary>The head and the trunk.</summary>
    HeadTrunk,
    /// <summary>The limbs: hands, wrists, feet and ankles (and, under the international guidelines, the whole limb).</summary>
    Limbs,
}

/// <summary>brief-sar-12 R-sar12-4 — a published exposure limit, shown beside a result as a reference value.</summary>
public sealed class C3dSarLimit
{
    public string Name { get; set; } = "";
    /// <summary>The value, W/kg.</summary>
    public double ValueWkg { get; set; }
    /// <summary>The averaging mass it is stated over, grams.</summary>
    public double MassGrams { get; set; }
    public C3dSarExposure Exposure { get; set; }
    /// <summary>Where it is published.</summary>
    public string Source { get; set; } = "";
    /// <summary>When: the publication's date, as it states it.</summary>
    public string Date { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>brief-sar-12 R-sar12-5 — a target value of the user's own (a system check's), per 1 W accepted, at a band and a
/// mass; the table shows the difference.</summary>
public sealed class C3dSarTarget
{
    public double FrequencyGHz { get; set; }
    public double MassGrams { get; set; }
    /// <summary>W/kg per 1 W accepted.</summary>
    public double PerWattWkg { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-sar-12 R-sar12-3 — a band sweep: one setup per band made from <see cref="Template"/>, and the power, duty cycle, exposure,
/// reference values and targets its result table reads.
/// </summary>
public sealed class C3dBandSweep
{
    public string Name { get; set; } = "";
    /// <summary>The setup every band's is made from: a driven openEMS setup.</summary>
    public string Template { get; set; } = "";
    /// <summary>The bands, GHz: one SAR frequency, and one setup, each.</summary>
    public List<double> BandsGHz { get; set; } = [];
    /// <summary>The device's rated (conducted) power, W. Omitted: 1 W, so the table reads per watt.</summary>
    public double? DrivePowerW { get; set; }
    /// <summary>What <see cref="DrivePowerW"/> is: the power accepted at the port (omitted) or the incident power.</summary>
    public C3dDriveReferredTo? DriveReferredTo { get; set; }
    /// <summary>The duty cycle's scaling factor, above 0 and at most 1; omitted, none.</summary>
    public double? DutyCycle { get; set; }
    /// <summary>The region the reference values are chosen for. Omitted: the head and the trunk.</summary>
    public C3dSarExposure? Exposure { get; set; }
    /// <summary>Reference values of the user's own, beside the published ones.</summary>
    public List<C3dSarLimit>? Limits { get; set; }
    /// <summary>Target values of the user's own (a system check's).</summary>
    public List<C3dSarTarget>? Targets { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>One averaged peak of one band at the rated power: per watt, at the rated power (and duty cycle), where, and which
/// reference values it is above.</summary>
public sealed record C3dBandSarValue(double MassGrams, double PerWattWkg, double AtRatedWkg, double[] CentreM, string? Object,
                                     IReadOnlyList<C3dSarLimit> Above, double? TargetPerWattWkg)
{
    /// <summary>The difference from the target, a fraction; null without one.</summary>
    public double? FromTarget => TargetPerWattWkg is { } t && t > 0 ? PerWattWkg / t - 1 : null;
}

/// <summary>One row of the band table: the band, its setup and port, the power accepted at the rated power, the duty cycle, the
/// 1 g and 10 g peaks — or why there are none.</summary>
public sealed record C3dBandSarRow(double FrequencyHz, string Setup, int Port, double? AcceptedW, double? DutyCycle,
                                   IReadOnlyList<C3dBandSarValue> Values, string? Missing);

public static class C3dBandSweeps
{
    /// <summary>The masses every band averages over: phone SAR's two.</summary>
    public static IReadOnlyList<double> Masses { get; } = [1, 10];

    private const string Fcc = "FCC Office of Engineering and Technology, OET Bulletin 65 Supplement C (Edition 01-01), 'FCC limits for " +
                               "specific absorption rate (SAR)', general population/uncontrolled exposure";
    private const string Icnirp = "ICNIRP, Guidelines for limiting exposure to electromagnetic fields (100 kHz to 300 GHz), Health Phys. " +
                                  "118(5):483-524, basic restrictions for the general public, 100 kHz to 6 GHz";

    /// <summary>R-sar12-4 — the published limits shipped as reference values, each cited and dated.</summary>
    public static IReadOnlyList<C3dSarLimit> Published { get; } =
    [
        new() { Name = "1.6 W/kg over 1 g (national regulator)", ValueWkg = 1.6, MassGrams = 1, Exposure = C3dSarExposure.HeadTrunk,
                Source = Fcc + ", partial body", Date = "June 2001" },
        new() { Name = "4.0 W/kg over 10 g, extremities (national regulator)", ValueWkg = 4.0, MassGrams = 10, Exposure = C3dSarExposure.Limbs,
                Source = Fcc + ", hands, wrists, feet and ankles", Date = "June 2001" },
        new() { Name = "2.0 W/kg over 10 g (international guidelines)", ValueWkg = 2.0, MassGrams = 10, Exposure = C3dSarExposure.HeadTrunk,
                Source = Icnirp + ", local, head and torso", Date = "March 2020" },
        new() { Name = "4.0 W/kg over 10 g, limbs (international guidelines)", ValueWkg = 4.0, MassGrams = 10, Exposure = C3dSarExposure.Limbs,
                Source = Icnirp + ", local, limbs", Date = "March 2020" },
    ];

    /// <summary>The reference values a sweep's table compares with: the published ones and the user's own, for its exposure.</summary>
    public static IReadOnlyList<C3dSarLimit> Limits(C3dBandSweep sweep)
    {
        var exposure = sweep.Exposure ?? C3dSarExposure.HeadTrunk;
        return [.. Published.Concat(sweep.Limits ?? []).Where(l => l.Exposure == exposure)];
    }

    /// <summary>The setup name a band takes: <c>Bands 1.9 GHz</c>.</summary>
    public static string SetupName(C3dBandSweep sweep, double ghz) => $"{sweep.Name} {ghz.ToString("0.####", CultureInfo.InvariantCulture)} GHz";

    /// <summary>Why <paramref name="sweep"/> cannot make its setups, as a sentence; null when it can.</summary>
    public static string? Problem(C3dBandSweep sweep)
    {
        if (string.IsNullOrWhiteSpace(sweep.Name)) return "The band sweep has no name; its setups are named after it.";
        if (sweep.BandsGHz.Count == 0) return $"The band sweep '{sweep.Name}' lists no band.";
        if (sweep.BandsGHz.Any(b => !(b > 0) || !double.IsFinite(b))) return $"The band sweep '{sweep.Name}' lists a band that is not a positive number of GHz.";
        if (sweep.BandsGHz.Distinct().Count() < sweep.BandsGHz.Count) return $"The band sweep '{sweep.Name}' lists one band twice.";
        if (sweep.DrivePowerW is { } p && !(p > 0 && double.IsFinite(p))) return $"The band sweep '{sweep.Name}' has a rated power that is not positive.";
        if (sweep.DutyCycle is { } d && !(d > 0 && d <= 1)) return $"The band sweep '{sweep.Name}' has a duty cycle outside (0, 1].";
        return null;
    }

    /// <summary>
    /// R-sar12-3 — one setup per band of <paramref name="sweep"/>, made from its template and written into <paramref name="doc"/>'s
    /// setups (a setup of the same name is replaced, so editing the template and creating again brings every band up to date).
    /// The setups' names, or why not; the document is unchanged on a refusal.
    /// </summary>
    public static (IReadOnlyList<string> Setups, string? Refusal) Create(C3dDocument doc, C3dBandSweep sweep)
    {
        var (made, refusal) = Make(doc, sweep);
        if (refusal is not null) return ([], refusal);
        var read = C3dSetups.Read(doc);
        foreach (var s in made)
        {
            if (read.FirstOrDefault(r => r.Name == s.Name) is { } old) doc.Setups[old.Index] = EmSetupPersistence.ToEmbedded(s);
            else doc.Setups.Add(EmSetupPersistence.ToEmbedded(s));
        }
        return ([.. made.Select(s => s.Name)], null);
    }

    /// <summary>The setups <see cref="Create"/> writes, made but not written: what a headless run of the sweep runs.</summary>
    public static (IReadOnlyList<EmSetup> Setups, string? Refusal) Make(C3dDocument doc, C3dBandSweep sweep)
    {
        if (Problem(sweep) is { } bad) return ([], bad);
        var read = C3dSetups.Read(doc);
        if (read.FirstOrDefault(r => r.Name == sweep.Template) is not { } t)
            return ([], $"The band sweep '{sweep.Name}' names the template '{sweep.Template}', and this 3D view has no setup of that name.");
        if (t.Setup is not { } template) return ([], t.Refusal);
        if (!template.Is3D || template.Problem3D != Em3dProblemType.Driven || Em3dSolvers.FirstWith(template.Solver3D, c => c.Sar) is null)
            return ([], $"The template '{template.Name}' is not a driven openEMS setup; SAR is computed only on the raw dump an openEMS run writes.");
        double[] hz;
        try { hz = template.ExpandFrequencies(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return ([], $"The template '{template.Name}''s sweep cannot be read as numbers ({e.Message}); give its start and stop as numbers to scale it.");
        }
        if (hz.Length == 0 || hz.Any(f => !(f > 0))) return ([], $"The template '{template.Name}''s sweep has no positive frequency.");
        double reference = template.Sar?.FrequenciesGHz is [var first, ..] ? first * 1e9 : 0.5 * (hz.Min() + hz.Max());
        var taken = new HashSet<string>(read.Select(r => r.Name), StringComparer.Ordinal);
        var made = new List<EmSetup>();
        foreach (double ghz in sweep.BandsGHz)
        {
            string name = SetupName(sweep, ghz);
            if (name == template.Name) return ([], $"The band {ghz} GHz would be named '{name}', the template's own name.");
            double r = ghz * 1e9 / reference;
            var s = template.Clone();
            s.Name = name;
            s.Frequency = Scaled(template.Frequency, template.Frequency.Expand(), r);
            s.AdditionalFrequencies = [.. template.AdditionalFrequencies.Select(seg => Scaled(seg, seg.Expand(), r))];
            var sar = template.Sar?.Clone() ?? new CemSar();
            sar.FrequenciesGHz = [ghz];
            sar.MassesGrams = [.. (sar.MassesGrams ?? []).Concat(Masses).Distinct().Order()];
            s.Sar = sar;
            made.Add(s);
        }
        return (made, null);
    }

    private static FrequencySpec Scaled(FrequencySpec f, double[] hz, double r)
    {
        string G(double v) => (v / 1e9).ToString("R", CultureInfo.InvariantCulture);
        if (f.Mode == FreqSpecMode.PointCount)
            return new FrequencySpec(G(hz.Min() * r), G(hz.Max() * r), f.NumPoints!.Value, f.Kind, "GHz", "GHz");
        double step = hz.Length > 1 ? (hz[1] - hz[0]) * r : 0;
        return new FrequencySpec(G(hz.Min() * r), G(hz.Max() * r), G(step), f.Kind, "GHz", "GHz", "GHz");
    }

    /// <summary>
    /// R-sar12-3 — the band table: one row per band and driven port, read from the records each band's run left under
    /// <paramref name="resultsRoot"/>. <paramref name="c3dPath"/> names the document the setups are embedded in (their run
    /// directories are named after it). A band not yet run is a row that says so.
    /// </summary>
    public static IReadOnlyList<C3dBandSarRow> Table(C3dDocument doc, C3dBandSweep sweep, string c3dPath, string resultsRoot)
    {
        var rows = new List<C3dBandSarRow>();
        var (setups, refusal) = Make(doc, sweep);
        if (refusal is not null) return rows;
        var limits = Limits(sweep);
        double power = sweep.DrivePowerW ?? 1;
        foreach (var setup in setups)
        {
            double f = setup.Sar!.FrequenciesGHz![0] * 1e9;
            var run = C3dSetups.ForRun(setup, c3dPath);
            var sarSolver = Em3dSolvers.FirstWith(run.Solver3D, c => c.Sar)!.Id;
            string dir = Em3dRunService.RunDirectory(resultsRoot, run, sarSolver);
            var records = SarVolumeFiles.ReadAll(dir);
            if (records.Count == 0)
            {
                rows.Add(new C3dBandSarRow(f, setup.Name, 0, null, sweep.DutyCycle, [], "not run yet"));
                continue;
            }
            foreach (var (_, r) in records)
            {
                double? accepted = sweep.DriveReferredTo == C3dDriveReferredTo.Incident
                    ? Skk(Em3dRunService.SnpBasePath(resultsRoot, run, sarSolver), r.Port, r.FrequencyHz) is { } s
                        ? power * (1 - s * s) : null
                    : power;
                if (accepted is null)
                {
                    rows.Add(new C3dBandSarRow(r.FrequencyHz, setup.Name, r.Port, null, sweep.DutyCycle, [],
                                               "no Touchstone to refer the rated power to the incident wave"));
                    continue;
                }
                var values = new List<C3dBandSarValue>();
                foreach (double m in Masses)
                {
                    string q = SarSettings.AveragedName(m);
                    if (r.PeakPerWatt(q) is not { } perWatt) continue;
                    var peak = r.Peaks.First(p => p.Quantity == q);
                    double at = perWatt * accepted.Value * (sweep.DutyCycle ?? 1);
                    var target = sweep.Targets?.FirstOrDefault(t => t.MassGrams == m && Math.Abs(t.FrequencyGHz * 1e9 - r.FrequencyHz) <= 1e-6 * r.FrequencyHz);
                    values.Add(new C3dBandSarValue(m, perWatt, at, peak.CentreM, peak.Object,
                                                   [.. limits.Where(l => l.MassGrams == m && at > l.ValueWkg)], target?.PerWattWkg));
                }
                rows.Add(new C3dBandSarRow(r.FrequencyHz, setup.Name, r.Port, accepted, sweep.DutyCycle, values,
                                           values.Count == 0 ? "no 1 g or 10 g peak: no power accepted" : null));
            }
        }
        return rows;
    }

    /// <summary>
    /// The band table as text: a heading with the rated power and duty cycle, one line per row (each peak at the rated power, per
    /// watt, where, and its difference from a target), then each reference value a number is above — named, never "fail" — and
    /// the reference values with their sources. What <c>em --bands</c> prints and what the GUI's table is built from.
    /// </summary>
    public static IReadOnlyList<string> TableText(C3dBandSweep sweep, IReadOnlyList<C3dBandSarRow> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(double v) => v.ToString("G4", inv);
        var lines = new List<string>();
        double p = sweep.DrivePowerW ?? 1;
        string referred = sweep.DriveReferredTo == C3dDriveReferredTo.Incident ? "incident" : "accepted";
        lines.Add($"Band sweep '{sweep.Name}': {N(p)} W {referred}" + (sweep.DutyCycle is { } d ? $", duty cycle {N(d)}" : "") +
                  $"; reference values for the {(sweep.Exposure == C3dSarExposure.Limbs ? "limbs" : "head and trunk")}");
        foreach (var r in rows)
        {
            string head = $"  {Layout.MaterialDispersion.Hz(r.FrequencyHz)}" + (r.Port > 0 ? $", port {r.Port}" : "");
            if (r.Missing is { } why) { lines.Add($"{head}: {why}"); continue; }
            lines.Add(head + (r.AcceptedW is { } a && sweep.DriveReferredTo == C3dDriveReferredTo.Incident ? $" ({N(a)} W accepted)" : ""));
            foreach (var v in r.Values)
            {
                string at = string.Join(", ", v.CentreM.Select(m => (m * 1e3).ToString("0.#", inv)));
                lines.Add($"    psSAR{N(v.MassGrams)}g {N(v.AtRatedWkg)} W/kg ({N(v.PerWattWkg)} W/kg per W) at ({at}) mm" +
                          (v.Object is { } o ? $", {o}" : "") +
                          (v.FromTarget is { } t ? $"; target {N(v.TargetPerWattWkg!.Value)} W/kg per W, {(t >= 0 ? "+" : "")}{(t * 100).ToString("0.0", inv)} %" : ""));
                foreach (var l in v.Above) lines.Add($"      above the reference value {l.Name}");
            }
        }
        var limits = Limits(sweep);
        if (limits.Count > 0)
        {
            lines.Add("Reference values (shown beside the result, not a pass or fail):");
            foreach (var l in limits) lines.Add($"  {l.Name}: {l.Source}, {l.Date}");
        }
        return lines;
    }

    /// <summary>|S_kk| at <paramref name="hz"/>, linear between the Touchstone's points, from the run's <c>.sNp</c> beside
    /// <paramref name="snpBase"/>; null when there is none or the frequency is outside it.</summary>
    public static double? Skk(string snpBase, int port, double hz)
    {
        string? dir = Path.GetDirectoryName(snpBase);
        if (dir is null || !Directory.Exists(dir)) return null;
        string stem = Path.GetFileName(snpBase);
        var file = Directory.EnumerateFiles(dir, stem + ".s*p")
                            .FirstOrDefault(p => RfCore.TouchstoneIO.ParsePortsFromExtension(p) is not null);
        if (file is null) return null;
        try
        {
            var snp = RfCore.TouchstoneIO.ReadFile(file, readComments: false);
            int k = port - 1;
            if (k < 0 || k >= snp.Ports) return null;
            var f = snp.Frequencies;
            for (int i = 0; i < f.Length; i++)
            {
                if (Math.Abs(f[i] - hz) <= 1e-9 * hz) return Complex.Abs(snp.Matrices[i][k, k]);
                if (i > 0 && f[i - 1] < hz && hz < f[i])
                {
                    double w = (hz - f[i - 1]) / (f[i] - f[i - 1]);
                    return Complex.Abs((1 - w) * snp.Matrices[i - 1][k, k] + w * snp.Matrices[i][k, k]);
                }
            }
            return null;
        }
        catch (Exception e) when (e is IOException or FormatException or InvalidDataException) { return null; }
    }
}
