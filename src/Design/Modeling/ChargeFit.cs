// The charge constituents, Qg and Qd, from the intrinsic capacitances (brief-modelcomposer-8 R-mc8-1 … R-mc8-4;
// overview D4, D7, D13).
//
//   1. The training data are C11, C12 (Qg's row) and C21, C22 (Qd's) at each intrinsic S point, averaged over MC-4's
//      flatness band with flagged frequencies left out; a point whose diagonal flatness is poor weighs less. A capacitance
//      table (MC-20) arrives through the same intrinsic cubes, at one frequency and flat, so each of its points weighs one.
//      One trap state, as for the currents. With S data at two or more temperatures an ANN charge takes T as an input (MC-9
//      R-mc9-1, [X10] eqs. 1.1 and 1.5), each point at its ambient — its junction temperature with self-heating.
//   2. Both charges are zero at the anchor — the arbitrary constant of [P1] §6.3.4.
//   3. Each charge is an ANN trained on its two partial derivatives by the adjoint method (the default, [P1] §6.3.6), a
//      table from the line integrals of [P1] eqs. 6.30/6.31 over an interpolant of the capacitances, or an equation
//      fitted to its derivatives. Or, as an advanced option, both come from [I11]'s drift decomposition.
//   4. The report: each capacitance's RMS error against the model's ∂Q/∂v, a table's two-path difference.
//
// With no S, Y or capacitance data (MC-18) nothing is fitted: the charges are the Linear template at the capacitances the
// settings type (zero by default), and typed values beside such data are refused rather than let discard it.
//
// The result goes to the .cmc's Steps.Charge; arrays to <name>.cmc-cache/charge/. Cancelled, nothing is written.

using System.Globalization;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using CircuitRF.Engine.Modeling.Ann;
using RfCore.Data;

namespace CircuitRF.Design.Modeling;

/// <summary>A fitted charge step: what the document records, the arrays it names, and the settings it was fitted with.</summary>
public sealed record ChargeFitResult(ChargeFitSettings Settings, ChargeFitRecord Record,
                                     IReadOnlyDictionary<string, DataCube> Arrays);

/// <summary>
/// The capacitances a charge fit trains on and check (a) reads (R-mc8-1): at each intrinsic point, the four elements of
/// the capacitance matrix (<c>Cij = ∂Qi/∂vj</c>, <c>i, j</c> = gate, drain) and the point's weight; the points' hull.
/// </summary>
public sealed class ChargeData
{
    public required IReadOnlyList<(double Vgs, double Vds)> X { get; init; }
    public required double[] C11 { get; init; }
    public required double[] C12 { get; init; }
    public required double[] C21 { get; init; }
    public required double[] C22 { get; init; }
    public required double[] Weight { get; init; }
    public required TrainingDomain Domain { get; init; }
    /// <summary>The lowest temperature of the points, °C — their only one when <see cref="T"/> is null.</summary>
    public double TemperatureC { get; init; } = 25;
    /// <summary>Each point's temperature, °C, when the points span two or more (MC-9); null at one temperature.</summary>
    public double[]? T { get; init; }

    public bool HasT => T is not null;

    /// <summary>Whether the points enclose an area of the bias plane. Fewer than three non-collinear points do not: the
    /// domain's polygon is then the points' extremes (one point, or a segment's two ends), which only an equation charge,
    /// global by construction, can be fitted over (MC-17).</summary>
    public bool SpansArea => Domain.Polygon.Count >= 3;

    /// <summary>MC-10: each point's trap states for <c>Qd</c> — null for a pulsed point beyond its trap-setting bias — when
    /// pulsed S-parameters give <c>Qd</c> its trap inputs; null when the charges are trap-independent.</summary>
    public double[]?[]? Phi { get; init; }
    /// <summary>The mapping <see cref="Phi"/> came from.</summary>
    public TrapMapping? Traps { get; init; }

    /// <summary><c>vgs, vds</c>, and <c>T</c> when the points span temperatures.</summary>
    public IReadOnlyList<string> Inputs => HasT ? [ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds, ComposedCoreProgram.T] : ChargeFit.VoltageInputs;

    /// <summary>The same points with their temperatures dropped — what a temperature-independent charge is fitted to.</summary>
    public ChargeData Isothermal() => !HasT ? this : new()
    {
        X = X, C11 = C11, C12 = C12, C21 = C21, C22 = C22, Weight = Weight, TemperatureC = TemperatureC,
        Domain = Domain with { Box = [] }, Phi = Phi, Traps = Traps,
    };

    /// <summary>The same points as trap-independent charges see them.</summary>
    public ChargeData Untrapped() => Phi is null ? this : new()
    {
        X = X, C11 = C11, C12 = C12, C21 = C21, C22 = C22, Weight = Weight, TemperatureC = TemperatureC, T = T, Domain = Domain,
    };

    /// <summary>The span of <see cref="T"/>, °C.</summary>
    public (double Lo, double Hi) TSpan => HasT ? (T!.Min(), T!.Max()) : (TemperatureC, TemperatureC);

    /// <summary>Data at scattered points, every point weighing one unless <paramref name="weight"/> says otherwise.</summary>
    public static ChargeData Of(IReadOnlyList<(double Vgs, double Vds)> x, double[] c11, double[] c12, double[] c21,
                                double[] c22, double[]? weight = null, double temperatureC = 25) => new()
    {
        X = x, C11 = c11, C12 = c12, C21 = c21, C22 = c22, Weight = weight ?? [.. x.Select(_ => 1.0)],
        Domain = DomainOf(x), TemperatureC = temperatureC,
    };

    /// <summary>The points' hull; for points that span no area, their extremes (see <see cref="SpansArea"/>).</summary>
    private static TrainingDomain DomainOf(IReadOnlyList<(double Vgs, double Vds)> x)
    {
        try { return CurrentFit.Hull([.. x.Select(p => new[] { p.Vgs, p.Vds })]); }
        catch (ModelingException)
        {
            // Collinear (or coincident) points: their lexicographic first and last are the segment's two ends.
            var ordered = x.Distinct().OrderBy(p => p.Vgs).ThenBy(p => p.Vds).ToList();
            return new TrainingDomain(ordered.Count <= 1 ? ordered : [ordered[0], ordered[^1]], []);
        }
    }

    /// <summary>A charge's row: <c>(C11, C12)</c> for <c>Qg</c>, <c>(C21, C22)</c> for <c>Qd</c>.</summary>
    public (double[] C1, double[] C2) Row(bool gate) => gate ? (C11, C12) : (C21, C22);

    internal double[][] Points => [.. X.Select(p => new[] { p.Vgs, p.Vds })];

    /// <summary>Each point over <see cref="Inputs"/>.</summary>
    internal double[][] PointsWithT => HasT ? [.. X.Select((p, k) => new[] { p.Vgs, p.Vds, T![k] })] : Points;

    /// <summary>The temperature a point's capacitances were measured at, °C.</summary>
    internal double TemperatureOf(int k) => T?[k] ?? TemperatureC;
}

public static class ChargeFit
{
    public const string StepName = "Charge";
    public const string CacheFolder = "charge";

    private static readonly IReadOnlyList<string> Inputs = [ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds];
    internal static IReadOnlyList<string> VoltageInputs => Inputs;

    /// <summary>
    /// A network that joins each extrinsic pin to its intrinsic one through 1 µΩ: what a model built from the steps
    /// carries until the network step's own is exported (MC-11), so check (c) drives the core itself.
    /// </summary>
    internal const string IntrinsicNetwork =
        "define intrinsic (P1 P2 P3 P4 P5 P6)\nR:R1 P1 P4 R=1e-6\nR:R2 P2 P5 R=1e-6\nR:R3 P3 P6 R=1e-6\nend\n";

    // ── The document ──────────────────────────────────────────────────────────

    /// <summary>The step's settings in <paramref name="doc"/>, or the defaults.</summary>
    public static ChargeFitSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is { } s ? ChargeFitSettings.Parse(s, $"Steps.{StepName}.Settings") : ChargeFitSettings.Default;

    /// <summary>The step's recorded result, or null when it has not been run.</summary>
    public static ChargeFitRecord? Record(CmcDocument doc) =>
        Step(doc)?["Result"] is { } r ? ChargeFitRecord.Parse(r, $"Steps.{StepName}.Result") : null;

    /// <summary>
    /// The last fit in one line (MC-15 R-mc15-1): <c>Qg: ANN 2×8 · Qd: Table · C error 1.2 %</c> — each charge's method and the
    /// largest of the capacitance errors, % of each capacitance's range. Null before a fit.
    /// </summary>
    public static string? Summary(CmcDocument doc)
    {
        if (Record(doc) is not { } r) return null;
        if (r.Typed is { } typed) return $"typed · {typed.Text}";
        var s = Settings(doc);
        var parts = new List<string>
        {
            $"Qg: {CurrentFit.MethodText(r.Qg.Method, s.Qg.Hidden)}",
            $"Qd: {CurrentFit.MethodText(r.Qd.Method, s.Qd.Hidden)}",
        };
        if (r.Errors.Count > 0) parts.Add($"C error {G(100 * r.Errors.Values.Max(e => e.Relative))} %");
        return string.Join(" · ", parts);
    }

    /// <summary>The block's keys checked and parsed — <see cref="CmcFile.Validate"/>'s call.</summary>
    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        CmcExtrinsic.Only(o, $"Steps.{StepName}", ["Network", "Settings", "Result", CmcStamps.DataKey]);
        Settings(doc);
        Record(doc);
    }

    /// <summary>The document with <paramref name="settings"/> as the step's settings; a recorded result is kept.</summary>
    public static CmcDocument WithSettings(CmcDocument doc, ChargeFitSettings settings)
    {
        var steps = StepsCopy(doc);
        var o = steps[StepName] as JsonObject ?? new JsonObject();
        steps[StepName] = o;
        o["Settings"] = settings.ToJson();
        return doc with { Steps = steps };
    }

    /// <summary>The document with <paramref name="result"/> recorded, through the network it was computed with.</summary>
    public static CmcDocument Apply(CmcDocument doc, ChargeFitResult result)
    {
        var steps = StepsCopy(doc);
        steps[StepName] = new JsonObject
        {
            ["Network"] = NetworkFit.Digest(CmcExtrinsic.Network(doc)),
            ["Settings"] = result.Settings.ToJson(),
            ["Result"] = result.Record.ToJson(),
        };
        return doc with { Steps = steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? CmcExtrinsic.Obj(n, $"Steps.{StepName}") : null;

    private static JsonObject StepsCopy(CmcDocument doc) =>
        doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();

    /// <summary>
    /// Reads the <c>.cmc</c>, fits with its settings, writes the arrays and the document — what the window and the
    /// <c>model</c> verb both call. Cancelled, nothing is written.
    /// </summary>
    public static ChargeFitResult FitFile(string cmcPath, RunControl? rc = null)
    {
        var doc = CmcFile.Read(cmcPath);
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        var intrinsic = new IntrinsicExtraction(cmcPath).Run(doc, data, rc: rc);
        var settings = Settings(doc);
        var current = settings.Drift is null || !HasData(doc, intrinsic) ? null : CurrentFit.Load(cmcPath, doc)
            ?? throw new ModelingException(StepName, "the drift charge reads the fitted Id; fit the currents first.");
        var result = Run(doc, data, intrinsic, settings, current, rc);
        rc?.ThrowIfCancellationRequested();
        CurrentFit.WriteArrays(cmcPath, CacheFolder, result.Record.Arrays, result.Arrays);
        CmcFile.Write(cmcPath, CmcStamps.Stamp(Apply(doc, result), StepName, CmcStamps.DataDigest(cmcPath, doc)));
        return result;
    }

    /// <summary>Whether the record's arrays are in the cache (<see cref="CurrentFit.HasArrays"/>).</summary>
    public static bool HasArrays(string cmcPath, CmcDocument doc) =>
        Record(doc) is { } record && CurrentFit.ArraysPresent(cmcPath, CacheFolder, record.Arrays);

    /// <summary>The recorded step and its arrays from the cache; null when there is no record, or its arrays are not in the
    /// cache (derived data: run the step again).</summary>
    public static ChargeFitResult? Load(string cmcPath, CmcDocument doc) =>
        Record(doc) is { } record && CurrentFit.ReadArrays(cmcPath, CacheFolder, record.Arrays) is { } arrays
            ? new ChargeFitResult(Settings(doc), record, arrays)
            : null;

    // ── Training data (R-mc8-1) ───────────────────────────────────────────────

    /// <summary>
    /// Refuses <paramref name="data"/> when it holds fewer points than the chosen methods need (R-mc17-2): an ANN or Table
    /// charge, or the drift decomposition, needs <see cref="SurfaceMinimum"/> over an area of the bias plane; an equation
    /// needs as many targets as free parameters, which its fit checks once it knows them. Never a switch of method (R-mc17-3).
    /// </summary>
    internal static void Require(ChargeData data, ChargeFitSettings settings)
    {
        if (settings.Drift is not null)
        {
            if (data.X.Count < SurfaceMinimum || !data.SpansArea)
                throw new ModelingException(StepName, $"{data.X.Count} usable S point(s); the drift charge needs at least {SurfaceMinimum} across the bias plane — the Linear equation fits from one.");
            return;
        }
        foreach (var m in new[] { settings.Qg.Method, settings.Qd.Method }.Distinct())
            if (Refusal(m, data.X.Count, data.SpansArea) is { } why) throw new ModelingException(StepName, why);
        if (data.Phi is not null && settings.Qd.Method == ChargeMethod.ANN && data.Phi.Count(r => r is not null) is var used and < SurfaceMinimum)
            throw new ModelingException(StepName, $"{used} S point(s) with a trap state; Qd needs at least {SurfaceMinimum}.");
    }

    /// <summary>What an ANN or Table charge (and the drift decomposition) needs: this many points over an area of the bias
    /// plane. An equation needs as many targets as free parameters instead — two per point (R-mc17-2).</summary>
    public const int SurfaceMinimum = 4;

    /// <summary>
    /// Why <paramref name="method"/> cannot be fitted to <paramref name="points"/> usable S points, or null when it can
    /// (R-mc17-2) — the refusal a fit gives and the window's tooltip on the method. An equation is checked against its own
    /// parameter count when it is fitted.
    /// </summary>
    public static string? Refusal(ChargeMethod method, int points, bool spansArea = true)
    {
        if (method == ChargeMethod.Equation) return null;
        string a = method == ChargeMethod.ANN ? "an" : "a";
        if (points < SurfaceMinimum)
            return $"{points} usable S point(s); {a} {method} charge needs at least {SurfaceMinimum} — the Linear equation fits from one.";
        return spansArea ? null
            : $"{points} usable S points span no area of the bias plane; {a} {method} charge needs both voltages to vary — the Linear equation fits from one.";
    }

    /// <summary>The usable S points of the document's charge data and whether they span an area, or null when there are
    /// none to count — what the window's method tooltips read (R-mc17-6).</summary>
    public static (int Points, bool SpansArea)? Usable(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                                       IntrinsicResult intrinsic)
    {
        try
        {
            var cd = Gather(doc, data, intrinsic, Settings(doc), []);
            return (cd.X.Count, cd.SpansArea);
        }
        catch (ModelingException) { return null; }
    }

    /// <summary>The sets the capacitances come from: those the settings name, or every set with intrinsic capacitances.</summary>
    internal static List<CmcDataSet> Chosen(CmcDocument doc, IntrinsicResult intrinsic, ChargeFitSettings settings) =>
        settings.DataSets.Count > 0
            ? [.. settings.DataSets.Select(n => doc.DataSets.FirstOrDefault(s => s.Name == n)
                                               ?? throw new ModelingException(n, "the charge step names it, and the document has no such data set."))]
            : doc.DataSets.Where(s => intrinsic.Sets.TryGetValue(s.Name, out var d) && d.Contains("C11")).ToList();

    /// <summary>The capacitances of the chosen sets at their intrinsic points, with each point's flatness weight. Whether
    /// there are enough for the chosen methods is the fit's question (<see cref="Require"/>).</summary>
    public static ChargeData Gather(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic,
                                    ChargeFitSettings settings, List<string> report)
    {
        var chosen = Chosen(doc, intrinsic, settings);
        if (chosen.Count == 0)
            throw new ModelingException(StepName, "there is no S, Y or capacitance data set to take the capacitances from.");
        // MC-10 R-mc10-5: with traps, Qd takes the trap states when pulsed S-parameters give every enabled state a level.
        var trapSettings = TrapFit.Settings(doc);
        TrapMapping? traps = null;
        if (!trapSettings.Modelled) CurrentFit.TrapCondition(chosen, StepName);
        else if (!chosen.Any(s => s.Kind == DataSetKind.PulsedSParams))
            report.Add("Qd is trap-independent: there are no pulsed S-parameters.");
        else if (trapSettings.Shifted)
            report.Add("Qd is trap-independent: the shift coupling's states reach Id alone.");
        else
        {
            traps = TrapFit.Map(doc, data, intrinsic, trapSettings, chosen, "pulsed S-parameters", requireLevels: false);
            var missing = traps.States.Where(st => traps.Levels[st].Count == 0).ToList();
            if (missing.Count > 0)
            {
                report.Add($"Qd is trap-independent: no pulsed S point gives {string.Join(", ", missing.Select(st => TrapFit.Label(trapSettings.Model, st)))} a trap level.");
                traps = null;
            }
            else
            {
                report.AddRange(traps.Report.Select(l => $"Qd: {l}"));
            }
        }
        var temps = CurrentFit.Temperatures(chosen, data);
        double t = temps.Count > 0 ? temps[0] : 25.0;
        bool hasT = temps.Count >= 2;
        if (!hasT && ThermalFit.Plan(doc, data).HasTemperatureInput)
            report.Add("the charges are temperature-independent: the S data is at one temperature.");
        var heating = ThermalFit.Heating(doc);
        var tk = new List<double>();

        int iC11 = Index("C11"), iC22 = Index("C22");
        var x = new List<(double, double)>();
        var c = new List<double>[4] { [], [], [], [] };
        var w = new List<double>();
        var phi = new List<double[]?>();
        int poor = 0;
        foreach (var set in chosen)
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds) || !ds.Contains("C11"))
                throw new ModelingException(set.Name, "the charges read its intrinsic capacitances, and it has no intrinsic S, Y or capacitance data.");
            if (DataImport.CapacitanceSource(set) is ("table" or "circuit set") and var spelling)
                report.Add($"{set.Name}: the capacitances are its {spelling}.");
            double[] f = ds["C11"].Axes[1].Values, band = ds["Band"].RealValues, flagged = ds["Flagged"].RealValues;
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues, ex = ds["Excluded"].RealValues;
            double[] flat = ds["Flatness"].RealValues;
            double[] igc = ds["Ig"].RealValues, idc = ds["Id"].RealValues;
            var pts = data.TryGetValue(set.Name, out var nd) && nd.Points.Count == vgs.Length ? nd.Points : null;
            double[][] cij = [ds["C11"].RealValues, ds["C12"].RealValues, ds["C21"].RealValues, ds["C22"].RealValues];
            int nf = f.Length, nq = IntrinsicQuantities.Names.Count;
            for (int p = 0; p < vgs.Length; p++)
            {
                if (ex[p] != 0 || !double.IsFinite(vgs[p]) || !double.IsFinite(vds[p])) continue;
                var sum = new double[4];
                int count = 0;
                for (int k = 0; k < nf; k++)
                {
                    if (f[k] < band[0] || f[k] > band[1] || flagged[p * nf + k] != 0) continue;
                    for (int i = 0; i < 4; i++) sum[i] += cij[i][p * nf + k];
                    count++;
                }
                if (count == 0) continue;
                double tAmb = CurrentFit.AmbientOf(set, pts?[p]);
                // With self-heating a CW S point sits at its DC junction temperature ([X10] makes the charges functions of Tj).
                tk.Add(hasT && heating.SelfHeating && !double.IsNaN(idc[p])
                    ? tAmb + heating.RthAt(tAmb) * CurrentFit.Power(vgs[p], vds[p], double.IsNaN(igc[p]) ? 0 : igc[p], idc[p])
                    : tAmb);
                x.Add((vgs[p], vds[p]));
                phi.Add(traps?.At(set.Name, p));
                for (int i = 0; i < 4; i++) c[i].Add(sum[i] / count);
                double worst = Math.Max(Finite(flat[p * nq + iC11]), Finite(flat[p * nq + iC22]));
                double weight = worst <= settings.FlatnessLimit ? 1.0 : Math.Pow(settings.FlatnessLimit / worst, 2);
                if (weight < 1) poor++;
                w.Add(weight);
            }
        }
        if (poor > 0) report.Add($"{poor} point(s) with flatness above {G(100 * settings.FlatnessLimit)} % down-weighted by (limit/flatness)².");
        if (x.Count == 0) throw new ModelingException(StepName, "0 usable S point(s): every one is excluded, or flagged across its flatness band.");
        var cd = ChargeData.Of(x, [.. c[0]], [.. c[1]], [.. c[2]], [.. c[3]], [.. w], t);
        if (hasT)
            cd = new ChargeData
            {
                X = cd.X, C11 = cd.C11, C12 = cd.C12, C21 = cd.C21, C22 = cd.C22, Weight = cd.Weight, TemperatureC = t, T = [.. tk],
                Domain = cd.Domain with { Box = [new(ComposedCoreProgram.T, (tk.Min(), tk.Max()))] },
            };
        if (traps is null) return cd;
        return new ChargeData
        {
            X = cd.X, C11 = cd.C11, C12 = cd.C12, C21 = cd.C21, C22 = cd.C22, Weight = cd.Weight, TemperatureC = t, T = cd.T,
            Domain = cd.Domain, Phi = [.. phi], Traps = traps,
        };

        static int Index(string name) => IntrinsicQuantities.Names.ToList().IndexOf(name);
        static double Finite(double v) => double.IsFinite(v) ? v : 0;
    }

    /// <summary>The anchor: the stated one, or (0, 0); a point outside the data's hull moves to the data point nearest it.
    /// Points that span no area have no hull to be outside of, and only an equation — defined everywhere — is fitted to
    /// them, so the anchor stays where it was stated.</summary>
    internal static (double Vgs, double Vds) Anchor(ChargeData data, ChargeFitSettings settings, List<string> report)
    {
        var want = settings.Anchor ?? (0, 0);
        if (!data.SpansArea || Inside(data.Domain.Polygon, want)) return want;
        var near = data.X.MinBy(p => Math.Pow(p.Vgs - want.Vgs, 2) + Math.Pow(p.Vds - want.Vds, 2));
        report.Add($"anchor ({G(want.Vgs)}, {G(want.Vds)}) V is outside the data; charges are zero at ({G(near.Vgs)}, {G(near.Vds)}) V.");
        return near;
    }

    internal static bool Inside(IReadOnlyList<(double Vgs, double Vds)> poly, (double Vgs, double Vds) p)
    {
        int sign = 0;
        for (int k = 0; k < poly.Count; k++)
        {
            var a = poly[k]; var b = poly[(k + 1) % poly.Count];
            double cross = (b.Vgs - a.Vgs) * (p.Vds - a.Vds) - (b.Vds - a.Vds) * (p.Vgs - a.Vgs);
            int s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    // ── The fit ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fits both charges (R-mc8-1 … R-mc8-4) from the open document's data and its intrinsic result.
    /// <paramref name="current"/> is the current step's result, which the drift decomposition needs. Throws
    /// <see cref="ModelingException"/> for a refusal and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static ChargeFitResult Run(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic,
                                      ChargeFitSettings settings, CurrentFitResult? current = null, RunControl? rc = null)
    {
        rc?.ThrowIfCancellationRequested();
        var chosen = Chosen(doc, intrinsic, settings);
        if (chosen.Count == 0) return Typed(settings);
        if (settings.Typed is not null)
            throw new ModelingException(StepName,
                $"typed capacitances and S data ({string.Join(", ", chosen.Select(c => c.Name))}) are both given; typing would discard the data. Clear the typed values to fit to it.");
        var report = new List<string>();
        return Run(Gather(doc, data, intrinsic, settings, report), settings, current, report, rc);
    }

    // ── Typed charges (MC-18) ─────────────────────────────────────────────────

    /// <summary>What the step, the export and <c>model explain</c> say of a model whose charges are all zero (R-mc18-2).</summary>
    public const string ZeroLine = "the charges are zero: S-parameters and harmonic balance see the currents alone.";

    /// <summary>Whether the document has data the charges could be fitted to — S, Y or capacitances at an intrinsic point.
    /// Without it the step writes the typed charges (R-mc18-1).</summary>
    public static bool HasData(CmcDocument doc, IntrinsicResult intrinsic) => Chosen(doc, intrinsic, Settings(doc)).Count > 0;

    /// <summary>
    /// The charges the settings type (R-mc18-1): MC-17's Linear constituents at <see cref="TypedCharges.Matrix"/>, zero at the
    /// anchor like every fitted charge. Nothing is fitted, so there are no arrays, no errors and no points.
    /// </summary>
    public static ChargeFitResult Typed(ChargeFitSettings settings)
    {
        var typed = settings.Typed ?? new TypedCharges();
        var (c11, c12, c21, c22) = typed.Matrix;
        var anchor = settings.Anchor ?? (0, 0);
        var linear = ChargeTemplates.Find(ChargeTemplates.Linear)!;
        Constituent Charge(bool gate, string k1, double v1, string k2, double v2) =>
            Constituent.Equation($"({linear.Expression(gate)})-Qanchor", Inputs,
                                 [(k1, v1), (k2, v2), ("Qanchor", v1 * anchor.Vgs + v2 * anchor.Vds)]);
        var report = new List<string> { $"no S data: the charges are typed — {typed.Text}." };
        if (typed.IsZero) report.Add(ZeroLine);
        var record = new ChargeFitRecord
        {
            Qg = Charge(true, "C11", c11, "C12", c12), Qd = Charge(false, "C21", c21, "C22", c22), Anchor = anchor,
            Typed = typed, Report = report,
        };
        return new ChargeFitResult(settings, record, new Dictionary<string, DataCube>(StringComparer.Ordinal));
    }

    /// <summary>The points a typed model's check reads its model over and drives at: the intrinsic I–V points the current
    /// step trains on, with no capacitances — there are none measured.</summary>
    internal static ChargeData TypedRegion(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic)
    {
        var sets = CurrentFit.ChosenIv(doc, CurrentFit.Settings(doc));
        var x = new List<(double, double)>();
        foreach (var set in sets)
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds) || !ds.Contains("Vgsi")) continue;
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues;
            double[]? ex = ds.Contains("Excluded") ? ds["Excluded"].RealValues : null;
            for (int p = 0; p < vgs.Length; p++)
                if ((ex is null || ex[p] == 0) && double.IsFinite(vgs[p]) && double.IsFinite(vds[p])) x.Add((vgs[p], vds[p]));
        }
        if (x.Count == 0) throw new ModelingException("Check", "there is no intrinsic I–V point to read the typed charges at.");
        var zero = new double[x.Count];
        var temps = CurrentFit.Temperatures(sets, data);
        return ChargeData.Of(x, zero, zero, zero, zero, temperatureC: temps.Count > 0 ? temps[0] : 25.0);
    }

    /// <summary>Fits both charges to <paramref name="data"/> directly.</summary>
    public static ChargeFitResult Run(ChargeData data, ChargeFitSettings settings, CurrentFitResult? current = null,
                                      RunControl? rc = null) => Run(data, settings, current, [], rc);

    private static ChargeFitResult Run(ChargeData data, ChargeFitSettings settings, CurrentFitResult? current,
                                       List<string> report, RunControl? rc)
    {
        rc?.ThrowIfCancellationRequested();
        Require(data, settings);
        // The model carries the currents' trap block, so a Qd mapped under other trap settings would read the wrong lanes.
        if (data.Traps is { } mapped && current?.Record.Traps is { } fitted
            && (fitted.Model != mapped.Block.Model || !fitted.States.SequenceEqual(mapped.States)))
            throw new ModelingException(StepName,
                $"the currents were fitted with {fitted.Model} ({string.Join(", ", fitted.States)}) and the trap step now sets {mapped.Block.Model} ({string.Join(", ", mapped.States)}); fit the currents again.");
        var anchor = Anchor(data, settings, report);
        var arrays = new Dictionary<string, DataCube>(StringComparer.Ordinal);
        var paths = new Dictionary<string, double>(StringComparer.Ordinal);
        Constituent qg, qd;
        double? cdsSpread = null;
        // T is an input of an ANN charge only; any other method is fitted temperature-independent over every point.
        ChargeData For(string what)
        {
            if (!data.HasT) return data;
            report.Add($"{what} is temperature-independent: only an ANN charge takes T; it is fitted over every temperature's points.");
            return data.Isothermal();
        }
        if (settings.Drift is { } drift)
        {
            if (current is null) throw new ModelingException(StepName, "the drift charge reads the fitted Id; fit the currents first.");
            (qg, qd, cdsSpread) = FitDrift(For("the drift charge").Untrapped(), settings, drift, current, anchor, arrays, report, rc);
        }
        else
        {
            // Only an ANN Qd takes the trap states (MC-10); the gate charge never does.
            if (data.Phi is not null && settings.Qd.Method != ChargeMethod.ANN)
                report.Add($"Qd ({settings.Qd.Method}) is trap-independent: only an ANN charge takes the trap states.");
            var qdData = settings.Qd.Method == ChargeMethod.ANN ? data : For($"Qd ({settings.Qd.Method})").Untrapped();
            qg = FitOne("Qg", true, settings.Qg, settings.Qg.Method == ChargeMethod.ANN ? data.Untrapped() : For($"Qg ({settings.Qg.Method})").Untrapped(),
                        settings, anchor, arrays, paths, report, rc);
            qd = FitOne("Qd", false, settings.Qd, qdData, settings, anchor, arrays, paths, report, rc);
        }

        var traps = qd.Inputs.Any(i => i.StartsWith("phi", StringComparison.Ordinal)) ? data.Traps!.Block : null;
        var program = ComposedCoreProgram.Compile(Model(qg, qd, arrays, current, data.TemperatureC, traps), "charge fit");
        if (settings.Drift is null && ChargeTemplates.IsLinear(settings.Qg.Template) && ChargeTemplates.IsLinear(settings.Qd.Template)
            && settings.Qg.Method == ChargeMethod.Equation && settings.Qd.Method == ChargeMethod.Equation)
            report.Add(CircuitSet(qg, qd));
        var errors = Errors(program, data);
        foreach (var (k, e) in errors) report.Add($"{k}: RMS error {G(100 * e.Relative)} % of its range.");
        string key = arrays.Count > 0 ? CurrentFit.ArraysKey(arrays, out _) : "";
        var record = new ChargeFitRecord
        {
            Qg = qg, Qd = qd, Arrays = key, Anchor = anchor, Errors = errors, PathDifference = paths, CdsSpread = cdsSpread,
            Traps = traps, Points = data.X.Count, Report = report,
        };
        return new ChargeFitResult(settings, record, arrays);
    }

    private static Constituent FitOne(string name, bool gate, ChargeConstituentSettings s, ChargeData data, ChargeFitSettings settings,
                                      (double Vgs, double Vds) anchor, Dictionary<string, DataCube> arrays,
                                      Dictionary<string, double> paths, List<string> report, RunControl? rc)
    {
        rc?.ThrowIfCancellationRequested();
        var (c1, c2) = data.Row(gate);
        switch (s.Method)
        {
            case ChargeMethod.ANN:
            {
                // The adjoint method ([P1] §6.3.6, [X03]): ∂Q/∂vgs → Ci1 and ∂Q/∂vds → Ci2, with Q = 0 at the anchor.
                rc?.BeginStage($"Training {name}");
                var (problem, inputs, domain) = data.HasT || data.Phi is not null ? AdjointExtra(data, c1, c2, anchor) : (new AnnTrainingProblem(2)
                {
                    Derivatives = new AnnDerivativeData(data.Points, [0, 1], [.. c1.Select((v, k) => new[] { v, c2[k] })],
                                                        [.. data.Weight.Select(w => new[] { w, w })]),
                    Anchor = new AnnAnchor([anchor.Vgs, anchor.Vds], 0),
                }, data.Inputs, data.Domain);
                var trained = AnnTrainer.Train(problem, AnnSettings(s.Hidden, s.Activation, s.MaxIterations, settings, settings.HeldOutFraction), rc);
                foreach (string warning in trained.Warnings) report.Add($"{name}: {warning}");
                if (trained.Derivatives is { HeldOutCount: > 0 } e)
                    report.Add($"{name}: held-out ∂Q/∂v error {G(100 * e.HeldOutRelative)} % over {e.HeldOutCount} point(s).");
                foreach (var (k, v) in trained.Arrays(name)) arrays[k] = v;
                return Constituent.Ann(name, inputs, domain, s.Activation);
            }
            case ChargeMethod.Table:
                return FitTable(name, s, c1, c2, data, settings, anchor, arrays, paths, report, rc);
            default:
                return FitEquation(name, gate, s, c1, c2, data, settings, anchor, report, rc);
        }
    }

    /// <summary>
    /// The adjoint problem with inputs beyond the voltages: T (MC-9) and, for a trapped <c>Qd</c>, the trap states (MC-10;
    /// a pulsed point beyond its trap-setting bias is left out). The capacitances constrain ∂Q/∂v at each point's own
    /// temperature and trap state; the charge's zero is fixed at the anchor bias for EVERY value of each further input, by
    /// ∂Q/∂x = 0 at nine values along it (the others at their middles) and the value 0 at the middle — a charge that moved
    /// with T or a trap state at the anchor would carry a displacement current the data never measured. Returns the
    /// problem, its inputs, and the domain with each further input's interval in its box.
    /// </summary>
    private static (AnnTrainingProblem Problem, IReadOnlyList<string> Inputs, TrainingDomain Domain) AdjointExtra(
        ChargeData data, double[] c1, double[] c2, (double Vgs, double Vds) anchor)
    {
        var states = data.Phi is null ? [] : data.Traps!.States;
        IReadOnlyList<string> inputs = [.. data.Inputs, .. states];
        int d = inputs.Count;
        var x = new List<double[]>();
        var targets = new List<double[]>();
        var weights = new List<double[]>();
        var points = data.PointsWithT;
        for (int k = 0; k < data.X.Count; k++)
        {
            if (data.Phi is not null && data.Phi[k] is null) continue;
            var p = points[k];
            x.Add([.. p, .. data.Phi?[k] ?? []]);
            targets.Add([c1[k], c2[k], .. new double[d - 2]]);
            weights.Add([data.Weight[k], data.Weight[k], .. new double[d - 2]]);
        }
        var box = new List<KeyValuePair<string, (double Lo, double Hi)>>();
        var mid = new double[d];
        for (int j = 2; j < d; j++)
        {
            double lo = x.Min(p => p[j]), hi = x.Max(p => p[j]);
            if (!(hi > lo)) (lo, hi) = (lo - 0.5, hi + 0.5);
            box.Add(new(inputs[j], (lo, hi)));
            mid[j] = 0.5 * (lo + hi);
        }
        double mean = weights.Average(w => w[0]);
        for (int j = 2; j < d; j++)
        {
            var (lo, hi) = box[j - 2].Value;
            for (int k = 0; k <= 8; k++)
            {
                var p = (double[])mid.Clone();
                (p[0], p[1], p[j]) = (anchor.Vgs, anchor.Vds, lo + (hi - lo) * k / 8.0);
                x.Add(p);
                targets.Add(new double[d]);
                var w = new double[d];
                w[j] = mean;
                weights.Add(w);
            }
        }
        var at = (double[])mid.Clone();
        (at[0], at[1]) = (anchor.Vgs, anchor.Vds);
        var problem = new AnnTrainingProblem(d)
        {
            Derivatives = new AnnDerivativeData(x, [.. Enumerable.Range(0, d)], targets, weights),
            Anchor = new AnnAnchor(at, 0),
        };
        return (problem, inputs, data.Domain with { Box = box });
    }

    private static AnnTrainingSettings AnnSettings(IReadOnlyList<int> hidden, AnnActivation activation, int iterations,
                                                   ChargeFitSettings s, double heldOut) =>
        new() { Hidden = hidden, Activation = activation, MaxIterations = iterations, Restarts = s.Restarts, Seed = s.Seed,
                HeldOutFraction = heldOut };

    /// <summary>
    /// [P1] eqs. 6.30/6.31: each capacitance interpolated over the scattered points, both paths integrated onto the hull's
    /// bounding grid, the chosen one (or their mean) kept as an MC-2 table. The paths' difference is recorded.
    /// </summary>
    private static Constituent FitTable(string name, ChargeConstituentSettings s, double[] c1, double[] c2, ChargeData data,
                                        ChargeFitSettings settings, (double Vgs, double Vds) anchor,
                                        Dictionary<string, DataCube> arrays, Dictionary<string, double> paths,
                                        List<string> report, RunControl? rc)
    {
        rc?.BeginStage($"Interpolating {name}");
        var f1 = Interpolant(c1, s.Interpolant, data, AnnSettings(s.Hidden, s.Activation, s.MaxIterations, settings, 0.0), rc);
        var f2 = Interpolant(c2, s.Interpolant, data, AnnSettings(s.Hidden, s.Activation, s.MaxIterations, settings, 0.0), rc);

        var poly = data.Domain.Polygon;
        double[] vgs = Regrid.Axis(poly.Min(p => p.Vgs), poly.Max(p => p.Vgs), s.GridStep);
        double[] vds = Regrid.Axis(poly.Min(p => p.Vds), poly.Max(p => p.Vds), s.GridStep);
        double typical = Math.Sqrt(c1.Concat(c2).Average(v => v * v));
        rc?.BeginStage($"Integrating {name}", vgs.Length + vds.Length, "line(s)");
        var (a, b) = LineIntegral.OntoGrid(f1, f2, anchor, vgs, vds, 1e-7 * typical * s.GridStep, rc);

        double[] values = s.Path switch
        {
            IntegrationPath.A => a,
            IntegrationPath.B => b,
            _ => [.. a.Select((v, k) => 0.5 * (v + b[k]))],
        };
        double range = values.Max() - values.Min();
        double diff = range > 0 ? Math.Sqrt(a.Select((v, k) => (v - b[k]) * (v - b[k])).Average()) / range : 0;
        paths[name] = diff;
        report.Add($"{name}: paths A and B differ by {G(100 * diff)} % of its range.");

        IReadOnlyList<double[]> sites = [vgs, vds];
        var coefficients = TensorSplineFit.Fit(sites, values, s.Smoothing);
        foreach (var (k, v) in ComposedArrays.Table(name, Inputs, sites, values, coefficients)) arrays[k] = v;
        return Constituent.Table(name, Inputs, data.Domain, s.Smoothing);
    }

    /// <summary>A smooth function of one capacitance over the scattered points: a radial-basis interpolant through them, or a
    /// network trained on them and continued linearly outside the hull.</summary>
    internal static Func<double, double, double> Interpolant(double[] c, RegridSource source, ChargeData data,
                                                             AnnTrainingSettings ann, RunControl? rc)
    {
        if (source == RegridSource.RBF) return Regrid.Rbf(data.X, c);
        var trained = AnnTrainer.Train(new AnnTrainingProblem(2) { Values = new AnnValueData(data.Points, c, data.Weight) }, ann, rc);
        // Two inputs, so the domain's polygon alone: data at two or more temperatures carries a T box the network has no input for.
        var linear = new GuidedExtrapolation(trained.Network, data.Domain with { Box = [] }, Inputs, 0.0, null);
        return (vgs, vds) =>
        {
            Span<double> p = [vgs, vds];
            Span<double> g = stackalloc double[2];
            return linear.Evaluate(p, g);
        };
    }

    /// <summary>A template or the user's expression fitted to the row's two capacitances — its partial derivatives — then
    /// shifted by a constant <c>Qanchor</c> so it is zero at the anchor.</summary>
    private static Constituent FitEquation(string name, bool gate, ChargeConstituentSettings s, double[] c1, double[] c2,
                                           ChargeData data, ChargeFitSettings settings, (double Vgs, double Vds) anchor,
                                           List<string> report, RunControl? rc)
    {
        string expression;
        IReadOnlyList<TemplateParameter> parameters;
        if (s.Template is { } t)
        {
            var tpl = ChargeTemplates.Find(t)
                ?? throw new ModelingException(name, $"'{t}' is not a template here; the templates are {string.Join(", ", ChargeTemplates.Names)}.");
            expression = tpl.Expression(gate);
            parameters = tpl.Parameters(gate, new ChargeTemplateData([.. data.X.Select(p => p.Vgs)], [.. data.X.Select(p => p.Vds)], c1, c2));
        }
        else
        {
            expression = s.Expression ?? throw new ModelingException(name, $"a {name} equation names a template or an Expression.");
            parameters = [];
        }
        parameters = CurrentFit.Overrides(name, parameters, s.Parameters);
        // R-mc17-2: each point gives the charge two targets, its two capacitances.
        int free = parameters.Count(p => !p.Lock), targets = 2 * data.X.Count;
        if (targets < free)
            throw new ModelingException(name,
                $"{data.X.Count} usable S point(s) give {name} {targets} target(s), and its equation has {free} free parameter(s) — the Linear equation fits from one.");

        var x = data.Points;
        rc?.BeginStage($"Fitting {name}", 0, "evaluation(s)");
        var fit = EquationFit.Fit([new EquationTerm(expression, x, c1, data.Weight, 0), new EquationTerm(expression, x, c2, data.Weight, 1)],
                                  parameters, s.Algorithm, settings.Seed, name, rc);
        double q0 = EquationFit.Function(expression, fit.Values, name)(anchor.Vgs, anchor.Vds).F;
        if (ChargeTemplates.IsLinear(s.Template)) report.Add(LinearLine(name, fit.Values, c1, c2));
        return Constituent.Equation($"({expression})-Qanchor", Inputs,
                                    [.. fit.Values.Select(kv => (kv.Key, kv.Value)), ("Qanchor", q0)]);
    }

    /// <summary>A linear charge's report line (R-mc17-2): <c>Qg: Linear from 2 bias point(s) — C11 120 fF ± 3 fF, C12 −20 fF ± 0.5 fF</c>,
    /// each value with the RMS of the points about it — what a constant matrix cannot follow — once there is more than one.</summary>
    private static string LinearLine(string name, IReadOnlyList<KeyValuePair<string, double>> values, double[] c1, double[] c2)
    {
        string Value(int k, double[] measured)
        {
            var (key, v) = (values[k].Key, values[k].Value);
            string text = $"{key} {ConservationCheck.Si(v, "F")}";
            return measured.Length < 2 ? text : $"{text} ± {ConservationCheck.Si(Math.Sqrt(measured.Average(m => (m - v) * (m - v))), "F")}";
        }
        return $"{name}: Linear from {c1.Length} bias point(s) — {Value(0, c1)}, {Value(1, c2)}.";
    }

    /// <summary>The circuit set of two linear charges (R-mc17-1), in <see cref="IntrinsicQuantities"/>' convention:
    /// <c>Cgs = C11 + C12</c>, <c>Cgd = −C12</c>, <c>Cds = C22 + C12</c>, <c>Cm = C21 − C12</c>.</summary>
    private static string CircuitSet(Constituent qg, Constituent qd)
    {
        double P(Constituent c, string key) => c.Parameters.First(kv => kv.Key == key).Value;
        double c11 = P(qg, "C11"), c12 = P(qg, "C12"), c21 = P(qd, "C21"), c22 = P(qd, "C22");
        return $"Cgs {ConservationCheck.Si(c11 + c12, "F")}, Cgd {ConservationCheck.Si(-c12, "F")}, " +
               $"Cds {ConservationCheck.Si(c22 + c12, "F")}, Cm {ConservationCheck.Si(c21 - c12, "F")}.";
    }

    /// <summary>
    /// What the <c>.cmodel</c>'s provenance and <c>model explain</c> say of linear charges (R-mc17-5):
    /// <c>Qg, Qd: Linear from 1 bias point(s)</c> — or the one charge that is — so such a model is never mistaken for a
    /// nonlinear one; typed charges (R-mc18-4): <c>Qg, Qd: typed (Cgs 300 fF, Cgd 50 fF, Cds 80 fF, Cm 0 F)</c>. Null when
    /// neither charge is linear.
    /// </summary>
    public static string? LinearProvenance(ChargeFitSettings settings, ChargeFitRecord record)
    {
        // R-mc18-4: typed charges say so, with the values typed.
        if (record.Typed is { } typed) return $"Qg, Qd: typed ({typed.Text})";
        if (settings.Drift is not null) return null;
        var linear = new[] { ("Qg", settings.Qg), ("Qd", settings.Qd) }
            .Where(c => c.Item2.Method == ChargeMethod.Equation && ChargeTemplates.IsLinear(c.Item2.Template))
            .Select(c => c.Item1).ToList();
        return linear.Count == 0 ? null : $"{string.Join(", ", linear)}: Linear from {record.Points} bias point(s)";
    }

    // ── The drift decomposition (R-mc8-3) ─────────────────────────────────────

    private static (Constituent Qg, Constituent Qd, double CdsSpread) FitDrift(
        ChargeData data, ChargeFitSettings settings, DriftChargeSettings drift, CurrentFitResult current,
        (double Vgs, double Vds) anchor, Dictionary<string, DataCube> arrays, List<string> report, RunControl? rc)
    {
        var channel = Channel(current, data.TemperatureC);
        int n = data.X.Count;
        var zero = Enumerable.Range(0, n).Where(k => Math.Abs(data.X[k].Vds) <= drift.VdsZero).ToArray();
        if (zero.Length < 3)
            throw new ModelingException(StepName,
                $"{zero.Length} point(s) at |vds| ≤ {G(drift.VdsZero)} V; the depletion charges are fitted there and need at least 3.");
        var ann = AnnSettings(drift.Hidden, AnnActivation.Tanh, drift.MaxIterations, settings, 0.0);
        var box = new Dictionary<string, (double, double)>(StringComparer.Ordinal);

        // The depletion charges at vds = 0 ([I11] §III, [P1] eq. 6.43): QGS(vgs) on Cgs = C11 + C12, QGD(vgd) on Cgd = −C12.
        var qgs = Train1D(DriftCharges.Qgs, [.. zero.Select(k => data.X[k].Vgs)], [.. zero.Select(k => data.C11[k] + data.C12[k])],
                          [.. zero.Select(k => data.Weight[k])], anchor.Vgs, ann, arrays, box, report, rc);
        var qgd = Train1D(DriftCharges.Qgd, [.. zero.Select(k => data.X[k].Vgs - data.X[k].Vds)], [.. zero.Select(k => -data.C12[k])],
                          [.. zero.Select(k => data.Weight[k])], anchor.Vgs - anchor.Vds, ann, arrays, box, report, rc);

        // The drift charge on what the depletion charges leave ([I11] eq. 4). With R1 = C11 − QGS' − QGD' and
        // R2 = C12 + QGD', the decomposition says R1 = ∂Qdr/∂vgd + τ·gm and R2 = −∂Qdr/∂vgd + τ·gds, so exactly
        // τ = (R1 + R2)/(gm + gds) and ∂Qdr/∂vgd = (R1·gds − R2·gm)/(gm + gds). A point weighs (gm + gds)² relative to
        // the largest — the conditioning of that solve — and Qdrift = 0 along Id = 0 is imposed as values.
        var vgd = new double[n]; var id = new double[n]; var gm = new double[n]; var gds = new double[n];
        for (int k = 0; k < n; k++)
        {
            vgd[k] = data.X[k].Vgs - data.X[k].Vds;
            (id[k], gm[k], gds[k]) = channel(data.X[k].Vgs, data.X[k].Vds);
        }
        double denMax = Enumerable.Range(0, n).Max(k => Math.Abs(gm[k] + gds[k]));
        if (!(denMax > 0)) throw new ModelingException(StepName, "the fitted Id has no conductance over the data; the drift charge needs one.");
        var dx = new List<double[]>(); var dt = new List<double[]>(); var dw = new List<double[]>();
        for (int k = 0; k < n; k++)
        {
            double den = gm[k] + gds[k];
            double ww = data.Weight[k] * Math.Pow(den / denMax, 2);
            if (!(ww > 1e-12)) continue;
            double r1 = data.C11[k] - Slope(qgs, data.X[k].Vgs) - Slope(qgd, vgd[k]);
            double r2 = data.C12[k] + Slope(qgd, vgd[k]);
            dx.Add([vgd[k], id[k]]);
            dt.Add([(r1 * gds[k] - r2 * gm[k]) / den, (r1 + r2) / den]);
            dw.Add([ww, ww]);
        }
        if (dx.Count < 4) throw new ModelingException(StepName, $"{dx.Count} point(s) carry current; the drift charge needs at least 4.");
        double vgdLo = vgd.Min(), vgdHi = vgd.Max(), idLo = Math.Min(0, id.Min()), idHi = Math.Max(0, id.Max());
        double[][] line = [.. Enumerable.Range(0, 21).Select(j => new[] { vgdLo + (vgdHi - vgdLo) * j / 20.0, 0.0 })];
        rc?.BeginStage("Training Qdrift");
        var qdr = AnnTrainer.Train(new AnnTrainingProblem(2)
        {
            Values = new AnnValueData(line, new double[line.Length]),
            Derivatives = new AnnDerivativeData(dx, [0, 1], dt, dw),
        }, ann, rc);
        foreach (string w in qdr.Warnings) report.Add($"Qdrift: {w}");
        foreach (var (k, v) in qdr.Arrays(DriftCharges.Qdrift)) arrays[k] = v;
        box[DriftCharges.DriftVgd] = (vgdLo, vgdHi);
        box[DriftCharges.DriftId] = (idLo, idHi);

        // QDS(vds) on Cds = C22 + C12 = QDS' + λ·∂Qdr/∂vds at every point — a 1-D fit, so the spread of Cds across vgs at a
        // fixed vds is what it cannot represent.
        var cds = new double[n];
        Span<double> g = stackalloc double[2];
        for (int k = 0; k < n; k++)
        {
            qdr.Network.Evaluate([vgd[k], id[k]], g);
            cds[k] = data.C22[k] + data.C12[k] - drift.Lambda * (-g[0] + g[1] * gds[k]);
        }
        var qds = Train1D(DriftCharges.Qds, [.. data.X.Select(p => p.Vds)], cds, data.Weight, anchor.Vds, ann, arrays, box, report, rc);
        double miss = 0, size = 0;
        for (int k = 0; k < n; k++)
        {
            miss += Math.Pow(Slope(qds, data.X[k].Vds) - cds[k], 2);
            size += cds[k] * cds[k];
        }
        double spread = size > 0 ? Math.Sqrt(miss / size) : 0;
        report.Add($"Cds varies {G(100 * spread)} % across vgs at fixed vds; QDS(vds) cannot follow that.");

        var domain = new TrainingDomain(data.Domain.Polygon,
            [.. DriftCharges.BoxKeys.Select(k => new KeyValuePair<string, (double Lo, double Hi)>(k, box[k]))]);
        return (Constituent.Drift(true, domain), Constituent.Drift(false, domain, drift.Lambda), spread);
    }

    /// <summary>A 1-D charge piece by adjoint training on its capacitance, zero at <paramref name="zeroAt"/>.</summary>
    private static AnnTrainingResult Train1D(string name, double[] v, double[] c, double[] w, double zeroAt, AnnTrainingSettings ann,
                                             Dictionary<string, DataCube> arrays, Dictionary<string, (double, double)> box,
                                             List<string> report, RunControl? rc)
    {
        double lo = v.Min(), hi = v.Max();
        if (!(hi > lo)) throw new ModelingException(StepName, $"{name}'s points do not vary; a 1-D charge needs a range.");
        rc?.BeginStage($"Training {name}");
        var trained = AnnTrainer.Train(new AnnTrainingProblem(1)
        {
            Derivatives = new AnnDerivativeData([.. v.Select(x => new[] { x })], [0], [.. c.Select(y => new[] { y })],
                                                [.. w.Select(x => new[] { x })]),
            Anchor = new AnnAnchor([zeroAt], 0),
        }, ann, rc);
        foreach (string warning in trained.Warnings) report.Add($"{name}: {warning}");
        foreach (var (k, a) in trained.Arrays(name)) arrays[k] = a;
        box[name] = (lo, hi);
        return trained;
    }

    private static double Slope(AnnTrainingResult piece, double v)
    {
        Span<double> g = stackalloc double[1];
        piece.Network.Evaluate([v], g);
        return g[0];
    }

    /// <summary>The fitted channel current as the core evaluates it — <c>IdRF</c> when dispersive — with <c>gm</c>, <c>gds</c>.</summary>
    internal static Func<double, double, (double Id, double Gm, double Gds)> Channel(CurrentFitResult current, double temperatureC)
    {
        var zero = Constituent.Equation("0", Inputs);
        var program = ComposedCoreProgram.Compile(Model(zero, zero, new Dictionary<string, DataCube>(), current, temperatureC), "current fit");
        return (vgs, vds) =>
        {
            // At a DC operating point: T at the temperature given, every trap state at its DC value.
            Span<double> s = stackalloc double[RuntimeEvaluator.MaxInputs];
            Span<double> g = stackalloc double[RuntimeEvaluator.MaxInputs];
            program.DcSeeds(vgs, vds, temperatureC, s);
            double id = program.Channel(s[..program.Width], g);
            return (id, g[0], g[1]);
        };
    }

    // ── The model and the report (R-mc8-4) ────────────────────────────────────

    /// <summary>
    /// A model holding these charges and, when given, the fitted currents (equations of zero otherwise), inside
    /// <see cref="IntrinsicNetwork"/> at one finger — what the conservation checks run.
    /// </summary>
    internal static ComposedModel Model(Constituent qg, Constituent qd, IReadOnlyDictionary<string, DataCube> chargeArrays,
                                        CurrentFitResult? current, double temperatureC, ModelTraps? traps = null)
    {
        var all = new Dictionary<string, DataCube>(chargeArrays, StringComparer.Ordinal);
        var zero = Constituent.Equation("0", Inputs);
        Constituent id = zero, ig = Constituent.GateEquations("0", "0", Inputs);
        Constituent? idRf = null;
        ModelExtrapolation? extrapolation = null;
        ModelDispersion? dispersion = null;
        if (current is not null)
        {
            var r = current.Record;
            (id, ig, idRf) = (r.Id, r.Ig, r.IdRF);
            foreach (var (k, v) in current.Arrays) all[k] = v;
            extrapolation = r.Gx.Count > 0 ? ModelExtrapolation.Local(r.Gx) : null;
            dispersion = r.Tau is { } tau ? new ModelDispersion(tau) : null;
        }
        return new ComposedModel
        {
            BasePeriphery = new BasePeriphery(1, "1 um"),
            Temperature = TemperatureOf([id, ig, qg, qd, idRf], temperatureC),
            Network = new ExtrinsicNetwork(IntrinsicNetwork, []),
            Core = new CoreConstituents(id, ig, qg, qd, idRf) { Dispersion = dispersion },
            Traps = current?.Record.Traps ?? traps,
            Extrapolation = extrapolation,
            Arrays = all,
        };
    }

    /// <summary>
    /// <c>Single</c> at <paramref name="t0"/> when no constituent reads T; otherwise <c>Range</c> over every training
    /// interval of T — or, when only equations read it, a range wide enough never to clamp.
    /// </summary>
    internal static ModelTemperature TemperatureOf(IEnumerable<Constituent?> constituents, double t0)
    {
        var withT = constituents.Where(c => c is not null && c.Inputs.Contains(ComposedCoreProgram.T)).ToList();
        if (withT.Count == 0) return ModelTemperature.Single(t0);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var c in withT)
            if (c!.Domain?.Box.FirstOrDefault(b => b.Key == ComposedCoreProgram.T) is { Key: not null } b)
                (lo, hi) = (Math.Min(lo, b.Value.Lo), Math.Max(hi, b.Value.Hi));
        return lo < hi ? ModelTemperature.Range(lo, hi) : ModelTemperature.Range(-273.15, 1000);
    }

    /// <summary>A fitted step's model, as <see cref="Model(Constituent, Constituent, IReadOnlyDictionary{string, DataCube}, CurrentFitResult?, double)"/>.</summary>
    public static ComposedModel Model(ChargeFitResult charges, CurrentFitResult? current, double temperatureC = 25) =>
        Model(charges.Record.Qg, charges.Record.Qd, charges.Arrays, current, temperatureC, charges.Record.Traps);

    /// <summary>The capacitance matrix of a model's charges at a point: <c>(C11, C12, C21, C22)</c>.</summary>
    public static (double C11, double C12, double C21, double C22) Capacitances(ComposedCoreProgram program, double vgs, double vds,
                                                                                double temperatureC = 25)
    {
        Span<double> s = stackalloc double[RuntimeEvaluator.MaxInputs];
        program.DcSeeds(vgs, vds, temperatureC, s);
        return Capacitances(program, s);
    }

    /// <summary>The capacitance matrix at explicit seeds (a pulsed point's own trap states, MC-10).</summary>
    internal static (double C11, double C12, double C21, double C22) Capacitances(ComposedCoreProgram program, ReadOnlySpan<double> seeds)
    {
        Span<double> gg = stackalloc double[RuntimeEvaluator.MaxInputs], gd = stackalloc double[RuntimeEvaluator.MaxInputs];
        program.Charges(seeds[..program.Width], gg, gd);
        return (gg[0], gg[1], gd[0], gd[1]);
    }

    private static Dictionary<string, CapacitanceErrorRecord> Errors(ComposedCoreProgram program, ChargeData data)
    {
        double[][] measured = [data.C11, data.C12, data.C21, data.C22];
        var sums = new double[4];
        var seeds = new double[RuntimeEvaluator.MaxInputs];
        foreach (var (k, p) in data.X.Select((p, k) => (k, p)))
        {
            // Each point at its own temperature and, when Qd is trapped, its own trap states (its DC ones when it has none).
            program.DcSeeds(p.Vgs, p.Vds, data.TemperatureOf(k), seeds);
            if (data.Phi?[k] is { } own) own.CopyTo(seeds, program.TrapLane);
            var (c11, c12, c21, c22) = Capacitances(program, seeds);
            double[] model = [c11, c12, c21, c22];
            for (int i = 0; i < 4; i++) sums[i] += Math.Pow(model[i] - measured[i][k], 2);
        }
        var errors = new Dictionary<string, CapacitanceErrorRecord>(StringComparer.Ordinal);
        for (int i = 0; i < 4; i++)
        {
            double rms = Math.Sqrt(sums[i] / data.X.Count), range = measured[i].Max() - measured[i].Min();
            errors[ChargeFitRecord.Capacitances[i]] = new CapacitanceErrorRecord(rms, range > 0 ? rms / range : 0);
        }
        return errors;
    }

    /// <summary>The span a C–V slice sweeps over the measured <paramref name="values"/>: theirs, or a volt either side of a
    /// single level — the flat lines of linear charges through their one bias (R-mc17-6).</summary>
    public static (double Lo, double Hi) SliceSpan(IReadOnlyList<double> values)
    {
        double lo = values.Min(), hi = values.Max();
        return hi > lo ? (lo, hi) : (lo - 1, hi + 1);
    }

    /// <summary>
    /// The model's four capacitances along one voltage with the other fixed (R-mc8-4) — the 1-D slices the window draws
    /// against the data (MC-15): <paramref name="sweepGate"/> sweeps <c>vgs</c> at <c>vds = fixedValue</c>, else the
    /// reverse. One cube per capacitance over the swept axis.
    /// </summary>
    public static DataSet Slice(ComposedCoreProgram program, bool sweepGate, double fixedValue, double lo, double hi, int points = 101)
    {
        var v = Enumerable.Range(0, points).Select(k => lo + (hi - lo) * k / (points - 1)).ToArray();
        var c = new double[4][];
        for (int i = 0; i < 4; i++) c[i] = new double[points];
        for (int k = 0; k < points; k++)
        {
            var (c11, c12, c21, c22) = sweepGate ? Capacitances(program, v[k], fixedValue) : Capacitances(program, fixedValue, v[k]);
            (c[0][k], c[1][k], c[2][k], c[3][k]) = (c11, c12, c21, c22);
        }
        var axis = new Axis(sweepGate ? ComposedCoreProgram.Vgs : ComposedCoreProgram.Vds, v, "V");
        var ds = new DataSet();
        for (int i = 0; i < 4; i++) ds.Add(ChargeFitRecord.Capacitances[i], new DataCube([axis], c[i]));
        return ds;
    }

    private static string G(double v) => v.ToString("G3", CultureInfo.InvariantCulture);
}
