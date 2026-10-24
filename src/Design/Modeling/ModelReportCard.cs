// The Model Validation Report's text: its scorecard, where the model is supported by data, how it was fitted, the data it
// was fitted to and checked against, and what each test ran at.
//
// Two readers, and the order serves the one who did not build the model (the designer, perhaps at another company):
//
//   Scorecard  one row per test, graded Green (very good to excellent), Yellow (usable, with something the designer should
//              know) or Red (does not reasonably match the measurement, or does not converge), each with what it means.
//              The grades come from the validation's own tolerances, so a model developer who tightens them tightens the
//              card: within the tolerance is Green, within WarnFactor of it Yellow, beyond Red. Convergence grades apart.
//   Validity   the bias, temperature, frequency and power the model's data covers — where a design may lean on it — and
//              what happens outside.
//   Methods    each constituent's fit: an equation (and which template), a table (its grid), an ANN (its layers); traps,
//              self-heating and the extrinsic network.
//   Data       every data set: what it was used for, its points, its bias values, its ambient, its frequencies.
//   Tests      per section, the conditions that reproduce it — bias, terminations, frequency, temperature, Rth, the solver's
//              harmonic count, tolerance and oversampling — and its testbench.
//
// The report writes it as report.json (this record) and summary.txt beside report.npy; the PDF draws it from report.json.
// It carries no clock time, so the window's PDF and the verb's are the same bytes.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine;

namespace CircuitRF.Design.Modeling;

/// <summary>A test's grade, for the model's user.</summary>
public enum Grade { Green, Yellow, Red }

/// <summary>One scorecard row: the section, the test, its result, its grade and what the grade means for a design.</summary>
public sealed record ScoreRow(string Section, string Test, string Result, Grade Grade, string Meaning);

/// <summary>One data set as the report lists it.</summary>
public sealed record CoverageRow(string Set, string Kind, string UsedFor, int Points, int Excluded, string Vgs, string Vds,
                                 string AmbientC, string Other);

/// <summary>One section's text: what it ran at, what it found, and the testbenches that reproduce it.</summary>
public sealed record SectionText(string Name, IReadOnlyList<string> Conditions, IReadOnlyList<string> Lines, IReadOnlyList<string> Testbenches);

/// <summary>The report's text — <c>report.json</c>.</summary>
public sealed record ReportCard
{
    public string Model { get; init; } = "";
    public string Document { get; init; } = "";
    public Grade Overall => Scores.Count == 0 ? Grade.Green : Scores.Max(s => s.Grade);
    public IReadOnlyList<ScoreRow> Scores { get; init; } = [];
    public IReadOnlyList<string> Validity { get; init; } = [];
    public IReadOnlyList<string> Methods { get; init; } = [];
    public IReadOnlyList<string> Simulation { get; init; } = [];
    public IReadOnlyList<CoverageRow> Coverage { get; init; } = [];
    public IReadOnlyList<SectionText> Sections { get; init; } = [];

    public const string FileName = "report.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ReportCard Read(string path) =>
        JsonSerializer.Deserialize<ReportCard>(File.ReadAllText(path), Json) ?? new ReportCard();

    /// <summary>What each grade means, in the words the card opens with.</summary>
    public static string Legend(Grade g) => g switch
    {
        Grade.Green => "very good to excellent",
        Grade.Yellow => "usable, with something specific the designer should know",
        _ => "does not reasonably match the measurement, or does not converge",
    };

    /// <summary>The card as plain text — <c>summary.txt</c>.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append("MODEL VALIDATION REPORT\n");
        sb.Append($"Model: {Model}\nDocument: {Document}\n\n");
        sb.Append($"SCORECARD — overall {Overall.ToString().ToUpperInvariant()}\n");
        foreach (var g in Enum.GetValues<Grade>()) sb.Append($"  {g}: {Legend(g)}\n");
        sb.Append('\n');
        foreach (var r in Scores)
            sb.Append($"  [{r.Grade.ToString().ToUpperInvariant(),-6}] {r.Section} · {r.Test}: {r.Result}\n           {r.Meaning}\n");
        Block(sb, "WHERE THE MODEL IS SUPPORTED BY DATA", Validity);
        Block(sb, "HOW THE MODEL WAS FITTED", Methods);
        Block(sb, "SIMULATOR SETTINGS", Simulation);
        sb.Append("\nDATA\n");
        foreach (var c in Coverage)
            sb.Append($"  {c.Set} ({c.Kind}) — {c.UsedFor}; {c.Points} point(s)" + (c.Excluded > 0 ? $", {c.Excluded} excluded" : "") +
                      $"; Vgs {c.Vgs}; Vds {c.Vds}; ambient {c.AmbientC}" + (c.Other.Length > 0 ? $"; {c.Other}" : "") + "\n");
        foreach (var s in Sections)
        {
            sb.Append($"\n== {s.Name} ==\n");
            if (s.Conditions.Count > 0)
            {
                sb.Append("Conditions\n");
                foreach (string c in s.Conditions) sb.Append($"  {c}\n");
            }
            if (s.Lines.Count > 0)
            {
                sb.Append("Results\n");
                foreach (string l in s.Lines) sb.Append($"  {l}\n");
            }
            if (s.Testbenches.Count > 0) sb.Append($"Testbenches: {string.Join(", ", s.Testbenches.Select(t => "testbenches/" + t))}\n");
        }
        return sb.ToString();
    }

    private static void Block(StringBuilder sb, string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        sb.Append($"\n{title}\n");
        foreach (string l in lines) sb.Append($"  {l}\n");
    }
}

/// <summary>The coverage plot's marker for each group, as the plot draws it and the page names it.</summary>
public static class ReportMarkers
{
    public static string Symbol(string group) => group switch
    {
        "IV fit" => "●", "CV fit" => "■", "Checked" => "◆", "Large signal" => "▲", _ => "★",
    };
}

/// <summary>The grades (the scorecard's rules) and the card's text, built from a report's sections and its model.</summary>
public static class ReportGrades
{
    /// <summary>A measure within this multiple of its tolerance is Yellow; beyond it, Red.</summary>
    public const double WarnFactor = 2;
    /// <summary>The share of points that may fail to converge and still be Yellow; above it, Red.</summary>
    public const double ConvergenceWarn = 0.05;
    /// <summary>The thermal check: |model − data| / data for Rth, Green and Yellow limits.</summary>
    public const double RthGreen = 0.10, RthYellow = 0.25;
    /// <summary>The charge check: RMS difference, % of each charge's range, Green and Yellow limits.</summary>
    public const double ChargeGreen = 2, ChargeYellow = 5;
    /// <summary>A PA class's loadpull disc: the share of loads HB converged at, Green and Yellow limits.</summary>
    public const double DiscGreen = 0.95, DiscYellow = 0.75;

    public static Grade Worst(params Grade[] grades) => grades.Length == 0 ? Grade.Green : grades.Max();

    /// <summary>A measure against its tolerance: within, Green; within <see cref="WarnFactor"/> of it, Yellow; beyond, Red.</summary>
    public static Grade Of(double value, double tolerance) =>
        !double.IsFinite(value) ? Grade.Red : value <= tolerance ? Grade.Green : value <= WarnFactor * tolerance ? Grade.Yellow : Grade.Red;

    /// <summary>Points that did not converge: none, Green; up to <see cref="ConvergenceWarn"/>, Yellow; more, Red.</summary>
    public static Grade Convergence(int failed, int count) =>
        failed <= 0 ? Grade.Green : count > 0 && failed <= ConvergenceWarn * count ? Grade.Yellow : Grade.Red;

    /// <summary>A banded measure: at or below <paramref name="green"/>, Green; at or below <paramref name="yellow"/>, Yellow.</summary>
    public static Grade Banded(double value, double green, double yellow) =>
        !double.IsFinite(value) ? Grade.Red : value <= green ? Grade.Green : value <= yellow ? Grade.Yellow : Grade.Red;

    /// <summary>A validation item's row: its worst measure, and its convergence.</summary>
    public static ScoreRow Row(string section, ValidationItem item)
    {
        var grades = item.Metrics.Select(m => Of(m.Value, m.Tolerance)).Append(Convergence(item.Failed, item.Count)).ToArray();
        var grade = Worst(grades);
        string result = string.Join(" · ", item.Metrics.Select(m => $"{m.Name} {G(m.Value)}{Unit(m.Unit)} (tolerance {G(m.Tolerance)}{Unit(m.Unit)})"));
        if (item.Failed > 0) result += $" · {item.Failed} of {item.Count} did not converge";
        string what = item.Kind switch
        {
            ValidationKind.DcIv or ValidationKind.PulsedIv => "the drain current",
            ValidationKind.SParams => "small-signal gain and matching",
            ValidationKind.PowerSweep => "power, gain and efficiency against drive",
            ValidationKind.TwoTone => "intermodulation",
            _ => "power and efficiency over the measured loads",
        };
        string meaning = grade switch
        {
            Grade.Green => $"The model reproduces {what} within tolerance over this data.",
            Grade.Yellow when Convergence(item.Failed, item.Count) == Grade.Yellow && grades.SkipLast(1).All(g => g == Grade.Green)
                => $"Matches within tolerance, but the solve failed at a few points — check convergence near those biases in a design.",
            Grade.Yellow => $"Within {G(WarnFactor)}× tolerance: usable for {what}, with a visible error — see the plots for where it is largest.",
            _ when Convergence(item.Failed, item.Count) == Grade.Red => "The solve failed at many points — do not rely on the model under these conditions.",
            _ => $"Beyond {G(WarnFactor)}× tolerance — {what} under these conditions is not reliable.",
        };
        return new ScoreRow(section, $"{item.Kind switch { ValidationKind.DcIv => "DC I–V", ValidationKind.PulsedIv => "pulsed I–V", ValidationKind.SParams => "S-parameters", ValidationKind.PowerSweep => "power sweep", ValidationKind.TwoTone => "two-tone", _ => "loadpull" }} '{item.Set}'",
                            result, grade, meaning);
    }

    // ── Validity, methods, data ───────────────────────────────────────────────

    /// <summary>The sets the I–V fit trained on, the sets the C–V fit took its capacitances from (none when the charges were
    /// typed or not fitted), and the sets a test checked.</summary>
    internal static (IReadOnlySet<string> Iv, IReadOnlySet<string> Cv, IReadOnlySet<string> Checked) Roles(ModelValidation.Context ctx, IReadOnlySet<string> validated)
    {
        var doc = ctx.Doc;
        HashSet<string> iv = Safe(() => CurrentFit.ChosenIv(doc, CurrentFit.Settings(doc)).Select(s => s.Name).ToHashSet()) ?? [];
        var record = ChargeFit.Record(doc);
        var settings = ChargeFit.Settings(doc);
        HashSet<string> cv = record is null || record.Typed is not null ? []
            : settings.DataSets.Count > 0 ? settings.DataSets.ToHashSet()
            : doc.DataSets.Where(s => s.Kind is DataSetKind.SParams or DataSetKind.PulsedSParams or DataSetKind.SimulatedTables).Select(s => s.Name).ToHashSet();
        return (iv, cv, validated);
    }

    /// <summary>
    /// The card's front matter for <paramref name="ctx"/>'s model and document: where the data supports the model, how each
    /// part was fitted, the solver settings the tests share, and every data set with what it was used for.
    /// <paramref name="validated"/> names the sets a test compared against.
    /// </summary>
    internal static (IReadOnlyList<string> Validity, IReadOnlyList<string> Methods, IReadOnlyList<string> Simulation, IReadOnlyList<CoverageRow> Coverage)
        FrontMatter(ModelValidation.Context ctx, IReadOnlySet<string> validated, int maxHarm)
    {
        var doc = ctx.Doc;
        var model = ctx.Model;
        var (currentSets, chargeSets, _) = Roles(ctx, validated);
        var chargeRecord = ChargeFit.Record(doc);
        var chargeSettings = ChargeFit.Settings(doc);

        // ── Data ──
        var coverage = new List<CoverageRow>();
        foreach (var set in doc.DataSets)
        {
            var uses = new List<string>();
            if (currentSets.Contains(set.Name)) uses.Add("I–V fit");
            if (chargeSets.Contains(set.Name)) uses.Add("C–V fit");
            if (validated.Contains(set.Name)) uses.Add("checked");
            string used = uses.Count > 0 ? string.Join(", ", uses) : "not used";
            var c = set.Conditions;
            if (ctx.Data.TryGetValue(set.Name, out var nd) && nd.Count > 0)
            {
                var kept = nd.Points.Where(p => !p.Excluded).ToList();
                var t = kept.Select(p => double.IsFinite(p.AmbientC) ? p.AmbientC : c.AmbientC ?? double.NaN);
                var other = new List<string>();
                if (nd.FrequenciesHz is { Length: > 0 } f) other.Add($"{ModelValidation.GHz(f[0])} – {ModelValidation.GHz(f[^1])} GHz ({f.Length} pts)");
                if (c.Quiescent is { } q) other.Add($"quiescent Vgs {G(q.Vgs)} V, Vds {G(q.Vds)} V");
                if (c.PulseWidth is { } w) other.Add($"pulse {G(w * 1e6)} µs");
                other.Add($"{ModelValidation.Plane(c.ReferencePlane ?? ReferencePlane.Probe)} plane");
                coverage.Add(new CoverageRow(set.Name, KindText(set.Kind), used, kept.Count, nd.Count - kept.Count,
                    ModelValidation.Values(kept.Select(p => p.Vgs), "V"), ModelValidation.Values(kept.Select(p => p.Vds), "V"),
                    ModelValidation.Values(t, "°C"), string.Join("; ", other)));
            }
            else if (c.LargeSignal is { } ls)
            {
                string other = $"{ls.Measurement switch { LargeSignalMeasurement.PowerSweep => "power sweep", LargeSignalMeasurement.TwoTone => "two-tone", _ => "loadpull" }}" +
                               (ls.FrequencyHz is { } f0 ? $" at {ModelValidation.GHz(f0)} GHz" : "");
                coverage.Add(new CoverageRow(set.Name, KindText(set.Kind), used, 0, 0, ls.Vgs is { } g ? $"{G(g)} V" : "—",
                    ls.Vds is { } d ? $"{G(d)} V" : "—", c.AmbientC is { } a ? $"{G(a)} °C" : "—", other));
            }
            else coverage.Add(new CoverageRow(set.Name, KindText(set.Kind), used, 0, 0, "—", "—", "—", "did not import"));
        }

        // ── Validity ──
        var validity = new List<string>();
        var ivPoints = doc.DataSets.Where(s => currentSets.Contains(s.Name) && ctx.Data.ContainsKey(s.Name))
                                   .SelectMany(s => ctx.Data[s.Name].Points.Where(p => !p.Excluded)).ToList();
        if (model.Core.Id.Domain is { } dom)
            validity.Add($"Drain current: trained over intrinsic {Box(dom)}.");
        else if (ivPoints.Count > 0)
            validity.Add($"Drain current: an equation fitted to {ivPoints.Count} point(s) over Vgs {Span(ivPoints.Select(p => p.Vgs))} V, " +
                         $"Vds {Span(ivPoints.Select(p => p.Vds))} V (at the data's plane). An equation evaluates anywhere; only this range is backed by data.");
        if (ivPoints.Where(p => double.IsFinite(p.Id)).Select(p => (P: p.Vds * p.Id, p)).OrderByDescending(x => x.P).FirstOrDefault() is { p: not null } hot && hot.P > 0)
            validity.Add($"Highest dissipation in the I–V data: {G(hot.P)} W at Vgs {G(hot.p.Vgs)} V, Vds {G(hot.p.Vds)} V" +
                         (doc.BasePeriphery is { } bp ? $" ({bp.Fingers} × {bp.UnitWidth})." : "."));
        if (chargeRecord?.Typed is { } typed)
            validity.Add($"Charges: typed constant capacitances ({typed.Text}) — no C–V data was fitted, so capacitance does not change with bias.");
        else if (model.Core.Qg.Domain is { } qd)
            validity.Add($"Charges: trained over intrinsic {Box(qd)}.");
        else if (model.Provenance.Charges.Length > 0)
            validity.Add($"Charges: {model.Provenance.Charges}.");
        validity.Add(model.Temperature.Mode == TemperatureMode.Single
            ? $"Temperature: the model holds one ambient, {G(model.Temperature.T0)} °C; it does not track a change of ambient" +
              (model.Thermal is null ? "." : ", though it heats itself (self-heating).")
            : $"Temperature: ambient {G(model.Temperature.Tmin)} … {G(model.Temperature.Tmax)} °C.");
        var sF = doc.DataSets.Where(s => validated.Contains(s.Name) && ctx.Data.TryGetValue(s.Name, out var x) && x.FrequenciesHz is { Length: > 0 })
                             .SelectMany(s => ctx.Data[s.Name].FrequenciesHz!).ToList();
        if (sF.Count > 0) validity.Add($"Small-signal behaviour checked from {ModelValidation.GHz(sF.Min())} to {ModelValidation.GHz(sF.Max())} GHz.");
        var large = doc.DataSets.Where(s => s.Kind == DataSetKind.LargeSignal && validated.Contains(s.Name)).ToList();
        validity.Add(large.Count > 0
            ? $"Large signal checked against measurement: {string.Join("; ", large.Select(LargeText))}."
            : "No large-signal measurement was compared: the PA sections' power, efficiency and convergence are the model's predictions, not comparisons.");
        if (model.Extrapolation is { Gx.Count: > 0 } ex)
            validity.Add("Outside the training range, a table or network continues from the nearest edge of its domain with an added " +
                         (ex.Mode == ExtrapolationMode.Local
                             ? "conductance of ten times its own at that edge, at most "
                             : "conductance ") +
                         $"({string.Join(", ", ex.Gx.Select(kv => $"Gx {kv.Key} {G(kv.Value)} S"))}): the model stays smooth and convergent there, " +
                         "but its values are an extrapolation, not a prediction backed by data.");
        validity.Add($"Base periphery {model.BasePeriphery.Fingers} × {model.BasePeriphery.UnitWidth}; other sizes scale through the extrinsic network's periphery rules.");

        // ── Methods ──
        var currentRecord = CurrentFit.Record(doc);
        var currentSettings = CurrentFit.Settings(doc);
        var methods = new List<string>
        {
            $"Drain current Id: {Describe(model, model.Core.Id, Same(currentRecord?.Id, model.Core.Id) ? currentSettings.Id.Template : null)}" +
            Error(currentRecord, "Id", model.Core.Id),
            $"Gate current Ig: {Describe(model, model.Core.Ig, Same(currentRecord?.Ig, model.Core.Ig) ? currentSettings.Ig.Template : null)}" +
            Error(currentRecord, "Ig", model.Core.Ig),
        };
        if (model.Core.IdRF is { } rf)
            methods.Add($"RF drain current IdRF (DC/RF dispersion, τ {G(model.Core.Dispersion?.Tau ?? double.NaN)} s): {Describe(model, rf, null)}");
        methods.Add($"Gate charge Qg: {Describe(model, model.Core.Qg, Same(chargeRecord?.Qg, model.Core.Qg) ? chargeSettings.Qg.Template : null)}");
        methods.Add($"Drain charge Qd: {Describe(model, model.Core.Qd, Same(chargeRecord?.Qd, model.Core.Qd) ? chargeSettings.Qd.Template : null)}");
        if (model.Provenance.Charges.Length > 0) methods.Add($"Charges: {model.Provenance.Charges}.");
        methods.Add(model.Traps is { } traps
            ? $"Traps: {traps.Model} model, {traps.States.Count} state(s) ({string.Join(", ", traps.States)}), " +
              (traps.Shifted ? "acting as a gate-voltage shift" : "trained into the drain current")
            : "Traps: none — no dispersion between DC and pulsed or RF behaviour beyond what the currents carry.");
        methods.Add(model.Thermal is { } th
            ? $"Self-heating: Rth {G(th.Rth)} °C/W at {G(th.Tref)} °C" + (th.RthSlope != 0 ? $", changing {G(th.RthSlope)} °C/W per °C" : "") +
              $", {th.Poles.Count} thermal pole(s), Cth {G(th.Poles.Sum(p => p.Cth))} J/°C"
            : "Self-heating: none — the model is isothermal.");
        var net = CmcExtrinsic.Network(doc);
        methods.Add(net?.Schematic is { } sch
            ? $"Extrinsic network: {Path.GetFileName(sch)}" + (net.Fit is { } fit ? $", element values fitted by {FitText(fit.Method)}" : "") + "."
            : "Extrinsic network: none — the data is at the intrinsic plane.");

        var simulation = new List<string>
        {
            $"Single-tone tests (power sweep, loadpull, PA classes): {ValidationBench.HbText(maxHarm)}.",
            $"Two-tone tests: {ValidationBench.TwoToneText(maxHarm)}.",
            $"DC tests: Newton, residual tolerance {G(AnalysisSettings.Default.NonlinearAbsTol)}, at most {AnalysisSettings.Default.NonlinearMaxIter} iterations.",
            "Every test is an ordinary .cnl in testbenches/ — circuitrf dc, sparam, hb or lp runs it as written.",
        };
        return (validity, methods, simulation, coverage);
    }

    /// <summary>A constituent's method in a reader's words.</summary>
    internal static string Describe(ComposedModel model, Constituent c, string? template)
    {
        string inputs = $"inputs {string.Join(", ", c.Inputs)}";
        switch (c.Method)
        {
            case Constituent.EquationMethod:
                string body = template is not null ? $"the {template} equation"
                    : c.Expressions.Count == 1 ? $"a user equation, {Short(c.Expressions[0].Value)}"
                    : $"user equations ({string.Join(", ", c.Expressions.Select(e => e.Key))})";
                return $"equation — {body}, {c.Parameters.Count} fitted parameter(s); {inputs}.";
            case Constituent.AnnMethod:
                var layers = c.Expressions.Select(e => (e.Key, Hidden: Hidden(model, e.Value))).ToList();
                string net = string.Join("; ", layers.Select(l => (layers.Count > 1 ? $"{l.Key} " : "") + LayersText(l.Hidden)));
                return $"ANN (neural network) — {net}, {(c.Activation ?? AnnActivation.Tanh).ToString().ToLowerInvariant()} activation" +
                       (c.Transform is not null ? ", fitted in asinh(I/I0) so small currents count" : "") + $"; {inputs}.";
            case Constituent.TableMethod:
                var grid = c.Expressions.Select(e => model.Arrays.TryGetValue($"{e.Value}.values", out var v)
                    ? string.Join(" × ", v.Axes.Select(a => $"{a.Length} {a.Name}")) : "?").Distinct();
                return $"table — cubic spline over a {string.Join(", ", grid)} grid" +
                       (c.Smoothing is > 0 ? $", smoothing λ {G(c.Smoothing.Value)}" : ", interpolating") + $"; {inputs}.";
            case Constituent.DriftMethod:
                return $"drift-charge decomposition (each piece an ANN); {inputs}.";
            default:
                return $"{c.Method}; {inputs}.";
        }
    }

    /// <summary>An ANN's hidden layer widths, read from its weight matrices (every layer but the output).</summary>
    private static IReadOnlyList<int> Hidden(ComposedModel model, string name)
    {
        var widths = new List<int>();
        for (int k = 1; model.Arrays.TryGetValue($"{name}.W{k}", out var w); k++) widths.Add(w.Axes[0].Length);
        return widths.Count > 0 ? [.. widths.Take(widths.Count - 1)] : [];
    }

    private static string LayersText(IReadOnlyList<int> hidden) =>
        hidden.Count == 0 ? "no hidden layer"
        : hidden.All(h => h == hidden[0]) ? $"{hidden.Count} hidden layer(s) of {hidden[0]} neurons"
        : $"{hidden.Count} hidden layers of {string.Join(", ", hidden)} neurons";

    private static bool Same(Constituent? a, Constituent b) =>
        a is not null && a.Method == b.Method && a.Expressions.SequenceEqual(b.Expressions);

    private static string Error(CurrentFitRecord? r, string key, Constituent c)
    {
        if (r is null || !r.Errors.TryGetValue(key, out var e) || !Same(key == "Id" ? r.Id : r.Ig, c)) return "";
        return e.HeldOutCount > 0
            ? $" Held-out error {G(100 * e.HeldOutRelative)} % of range ({e.HeldOutCount} of {e.TrainCount + e.HeldOutCount} points held out)."
            : $" Training error {G(100 * e.TrainRelative)} % of range over {e.TrainCount} points.";
    }

    private static string Box(TrainingDomain d)
    {
        var parts = new List<string>
        {
            $"Vgs {Span(d.Polygon.Select(p => p.Vgs))} V", $"Vds {Span(d.Polygon.Select(p => p.Vds))} V",
        };
        foreach (var (k, (lo, hi)) in d.Box)
            parts.Add(k == ComposedCoreProgram.T ? $"T {Span([lo, hi])} °C" : $"trap state {k} {Span([lo, hi])} V");
        return string.Join(", ", parts) + $" (the hull of the training points, {d.Polygon.Count} vertices)";
    }

    private static string Span(IEnumerable<double> v)
    {
        var l = ModelValidation.Snapped(v).ToList();
        return l.Count == 0 ? "—" : $"{G(l.Min())} … {G(l.Max())}";
    }

    private static string LargeText(CmcDataSet s)
    {
        var c = s.Conditions.LargeSignal!;
        return $"'{s.Name}' ({c.Measurement switch { LargeSignalMeasurement.PowerSweep => "power sweep", LargeSignalMeasurement.TwoTone => "two-tone", _ => "loadpull" }}" +
               (c.FrequencyHz is { } f ? $", {ModelValidation.GHz(f)} GHz" : "") +
               (c.Vgs is { } g && c.Vds is { } d ? $", Vgs {G(g)} V, Vds {G(d)} V" : "") + ")";
    }

    private static string KindText(DataSetKind k) => k switch
    {
        DataSetKind.DcIv => "DC I–V", DataSetKind.PulsedIv => "pulsed I–V", DataSetKind.SParams => "S-parameters",
        DataSetKind.PulsedSParams => "pulsed S-parameters", DataSetKind.SimulatedTables => "simulated tables",
        DataSetKind.LargeSignal => "large signal", _ => "passive S-parameters",
    };

    private static string FitText(NetworkFitMethod m) => m switch
    {
        NetworkFitMethod.ColdFet => "cold-FET extraction",
        NetworkFitMethod.MultiBias => "multi-bias optimisation",
        _ => "cold-FET extraction, then multi-bias optimisation",
    };

    private static string Short(string s) => s.Length <= 60 ? s : s[..57] + "…";

    private static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch (ModelingException) { return null; }
    }

    private static string Unit(string u) => u.Length == 0 ? "" : " " + u;

    internal static string G(double v) => ModelValidation.G(v);
}
