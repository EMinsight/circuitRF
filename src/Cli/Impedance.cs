using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Render;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf impedance &lt;layout&gt;</c> — Trace Impedance Analysis: every trace on the chosen
/// copper layers, end to end, against a target Z0 ± tolerance, with where the return path under each
/// one breaks. <c>-o report.pdf</c> writes the report the layout editor's Impedance Analysis exports.
///
/// <para><b>It owns no analysis and no page</b>, on <c>src/Cli/Authoring.cs</c>' terms: every number
/// comes out of <see cref="TraceImpedanceAnalysis.LoadLayout"/> and <c>TraceImpedanceAnalysis.Analyze</c> and every pixel of the PDF out of
/// <see cref="TraceImpedanceReportDocument.Pdf"/> — the two calls the editor's dialog makes — so a
/// board that passes headlessly passes when it is opened, and the two PDFs are the same document.</para>
///
/// <para><b>The review saved on the layout applies by default</b> (brief-impedance-2): the target, bands,
/// frequency, layers and scope the editor's dialog last saved, each overridden by its flag, so a headless
/// run reviews the traces the editor reviews. <c>--no-scope</c> drops the saved scope, <c>--width</c>
/// replaces it for the layers it names, and <c>--survey</c> lists the width classes and analyses nothing.</para>
///
/// <para><b>Accepted findings apply by default</b> (brief-impedance-5): the <c>.clay</c> is the record of
/// what the designer accepted, and a finding it covers is reported ACCEPTED with its reason and does not
/// count against its trace. <c>--ignore-accepted</c> reports as if there were none. There is deliberately no
/// <c>--accept</c>: a run's trace ids are that run's, and an acceptance is a decision made reading the
/// finding — a headless caller edits the <c>.clay</c>, whose key the file-format reference documents.</para>
///
/// <para><b>Exit codes</b>, on <c>check</c>'s convention: 0 when no trace fails — warnings are always
/// reported and still exit 0 — 1 when one fails or is unsolved or the run is refused (and, with
/// <c>--severity warning</c>, when one warns), 130 when it is cancelled. A cancelled run still writes the report for the layers that FINISHED
/// (owner, 2026-09-25) — the analysis is layer by layer precisely so that stopping a long run keeps
/// what it has done — and says on its first page that it was cancelled.</para>
/// </summary>
internal static class Impedance
{
    private sealed class Options
    {
        public string? Path;
        public string? Output;
        // Null = not given: the value saved on the layout applies, and the default where none is.
        public double? Target;
        public double? Tolerance;
        public double? Warn;
        public double? MaxFrequencyHz;
        public bool WarningsFail;
        public readonly List<string> Layers = [];
        public double? MaxWidthMicrons;
        public bool NoScope;
        public bool Survey;
        public bool IgnoreAccepted;
        public readonly List<(string Layer, List<double> Microns)> Widths = [];
        // brief-impedance-4: the selectors, as typed — a coordinate needs the layout's DBU to read.
        public readonly List<string> Regions = [];
        public readonly List<string> Nets = [];
        public readonly List<string> Picks = [];
    }

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;

        if (o.Path is null) { JsonRun.Report(CliDiagnostics.ImpedancePathRequired()); return Usage(); }
        JsonRun.InputPath = o.Path;
        if (!File.Exists(o.Path) && !Directory.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.ImpedancePathNotFound(o.Path));

        if (o.Output is { } output && !output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(CliDiagnostics.ImpedanceOutputNotPdf(output));

        if (ResolveLayout(o.Path) is not { } clay)
            return JsonRun.Fail(CliDiagnostics.ImpedanceNotALayout(o.Path, DocumentKinds.Name(DocumentKinds.Classify(o.Path))));

        // Read ONCE: the saved review and the analysis come from the same read of the file.
        TraceImpedanceAnalysis.LayoutSource source;
        try { source = TraceImpedanceAnalysis.LoadLayout(clay); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return JsonRun.Fail(CliDiagnostics.ImpedanceLayoutUnreadable(clay, ex.Message));
        }
        if (source.Technology is not { } tech)
            return o.Layers.Count > 0 || o.Widths.Count > 0
                ? JsonRun.Fail(CliDiagnostics.ImpedanceNoTechnology(clay))
                : JsonRun.Fail(CliDiagnostics.ImpedanceRefused(clay, source.Refusal ?? ""));

        // The review saved on the layout applies by default, so a headless run reports what the
        // editor reports (brief-impedance-2 R-imp2-4c); a flag overrides it, and it overrides the
        // defaults.
        var saved = source.View.ImpedanceReview;
        var copperNames = CopperLayers(tech);

        // The layer names, against the technology the layout resolves — a name that is not one of its
        // copper layers is a refusal listing the ones that are, never a silent skip. A SAVED name the
        // technology no longer has is said on stderr and skipped: the flag is the caller's, the saved
        // review is last session's.
        IReadOnlyList<LayerKey>? layers = null;
        if (o.Layers.Count > 0)
        {
            var keys = new List<LayerKey>();
            foreach (string name in o.Layers)
            {
                if (CopperLayer(tech, copperNames, name) is not { } def)
                    return JsonRun.Fail(CliDiagnostics.ImpedanceUnknownLayer(name, string.Join(", ", copperNames)));
                keys.Add(def.Key);
            }
            layers = keys;
        }
        else if (saved?.Layers is { Count: > 0 } savedLayers)
        {
            var keys = new List<LayerKey>();
            foreach (string name in savedLayers)
            {
                if (CopperLayer(tech, copperNames, name) is { } def) keys.Add(def.Key);
                else Console.Error.WriteLine($"[circuitRF] The saved review names '{name}', which is not a copper layer of this technology now; it was skipped.");
            }
            if (keys.Count > 0) layers = keys;
        }

        TraceImpedanceScope? scope = null;
        if (!o.NoScope)
        {
            scope = saved?.Scope?.Clone() ?? new TraceImpedanceScope();
            foreach (var (layerName, microns) in o.Widths)
            {
                if (CopperLayer(tech, copperNames, layerName) is not { } def)
                    return JsonRun.Fail(CliDiagnostics.ImpedanceUnknownLayer(layerName, string.Join(", ", copperNames)));
                // --width REPLACES the saved classes for that layer, for this run.
                scope.Widths.RemoveAll(w => string.Equals(w.LayerName, def.Name, StringComparison.OrdinalIgnoreCase));
                foreach (double um in microns) scope.Widths.Add(TraceWidthSelector.Around(def.Name, um));
            }

            // --region, --net and --pick each REPLACE the saved selectors of their kind for this run
            // (R-imp4-4b); a kind not given keeps what was saved, lasso polygons included.
            if (o.Regions.Count > 0)
            {
                scope.Regions.Clear();
                foreach (string text in o.Regions)
                {
                    var parts = text.Split(',');
                    if (parts.Length != 4) return JsonRun.Fail(CliDiagnostics.ImpedanceBadRegion(text));
                    var v = new long[4];
                    for (int k = 0; k < 4; k++)
                        if (Coordinate(parts[k], "--region", source.View, out v[k]) is { } refused) return refused;
                    if (v[0] == v[2] || v[1] == v[3]) return JsonRun.Fail(CliDiagnostics.ImpedanceBadRegion(text));
                    scope.Regions.Add(TraceScopeRegion.Rectangle(null, v[0], v[1], v[2], v[3]));
                }
            }
            if (o.Nets.Count > 0)
            {
                scope.Nets.Clear();
                scope.Nets.AddRange(o.Nets);
            }
            if (o.Picks.Count > 0)
            {
                scope.Picks.Clear();
                foreach (string text in o.Picks)
                {
                    // <layer>@<x>,<y>[:connected] — the layer first, because a layer name may hold a comma
                    // no more than it may hold an @.
                    string body = text;
                    var extent = TracePickExtent.Trace;
                    if (body.EndsWith(":connected", StringComparison.OrdinalIgnoreCase))
                    {
                        extent = TracePickExtent.Connected;
                        body = body[..^":connected".Length];
                    }
                    int at = body.LastIndexOf('@');
                    var xy = at > 0 ? body[(at + 1)..].Split(',') : [];
                    if (xy.Length != 2) return JsonRun.Fail(CliDiagnostics.ImpedanceBadPick(text));
                    if (CopperLayer(tech, copperNames, body[..at].Trim()) is not { } def)
                        return JsonRun.Fail(CliDiagnostics.ImpedanceUnknownLayer(body[..at].Trim(), string.Join(", ", copperNames)));
                    if (Coordinate(xy[0], "--pick", source.View, out long px) is { } rx) return rx;
                    if (Coordinate(xy[1], "--pick", source.View, out long py) is { } ry) return ry;
                    scope.Picks.Add(new TracePick(def.Name, px, py, extent));
                }
            }
        }

        var options = new TraceImpedanceOptions
        {
            TargetOhms = o.Target ?? saved?.TargetOhms ?? TraceImpedanceOptions.DefaultTargetOhms,
            TolerancePercent = o.Tolerance ?? saved?.TolerancePercent ?? TraceImpedanceOptions.DefaultTolerancePercent,
            WarningPercent = o.Warn ?? saved?.WarningPercent ?? TraceImpedanceOptions.DefaultWarningPercent,
            MaxFrequencyHz = o.MaxFrequencyHz ?? saved?.MaxFrequencyHz,
            Layers = layers,
            MaxWidthMicrons = o.MaxWidthMicrons,
            Scope = scope,
        };

        // Progress: one stderr line per stage (a layer's finding, cutting, solving), and whatever a
        // host installed beside it.
        string lastStage = "";
        var control = new RunControl
        {
            Token = RunHost.Cancellation,
            Progress = new Inline(p =>
            {
                RunHost.Observer?.Invoke(p);
                if (p.Stage != lastStage)
                {
                    lastStage = p.Stage;
                    Console.Error.WriteLine($"[circuitRF] {p.Stage}" + (p.StageTotal > 0 ? $" ({p.StageTotal} {p.StageUnit})" : ""));
                }
            }),
        };

        if (o.Survey) return RunSurvey(source, options, control, clay);

        TraceImpedanceReport report;
        try
        {
            report = TraceImpedanceAnalysis.Analyze(source, options, control);
        }
        catch (OperationCanceledException)
        {
            JsonRun.Report(CliDiagnostics.ImpedanceCancelled());
            return 130;
        }

        if (report.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.ImpedanceRefused(clay, why));
        // The exit code below counts un-accepted findings only, because the verdicts it reads are
        // computed from them (R-imp5-4b).
        if (!o.IgnoreAccepted) report = TraceImpedanceAcceptance.Apply(report, source.View.ImpedanceAcceptances);

        foreach (string note in report.Notes) Console.Error.WriteLine($"[circuitRF] {note}");
        Console.Write(Text(report));
        JsonRun.Impedance = Project(report);

        if (o.Output is { } path && report.Layers.Count > 0)
        {
            byte[] pdf = TraceImpedanceReportDocument.Pdf(report);
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, pdf);
            }
            catch (Exception ex)
            {
                return JsonRun.Fail(CliDiagnostics.ImpedanceOutputFailed(path, ex.Message));
            }
            JsonRun.AddOutput("report", path);
            Console.Error.WriteLine($"[circuitRF] Wrote {path}");
        }

        if (report.Cancelled)
        {
            JsonRun.Report(CliDiagnostics.ImpedanceCancelledPartial(report.Layers.Count, report.LayersRequested.Count));
            return 130;
        }
        return report.FailCount > 0 || report.AllTraces.Any(t => t.Verdict == TraceVerdict.Unsolved)
               || (o.WarningsFail && report.WarningCount > 0) ? 1 : 0;
    }

    /// <summary>
    /// A layout coordinate for <c>--region</c> or <c>--pick</c>: <b>every coordinate carries a unit and a
    /// bare number is refused</b>, <c>render --window</c>'s rule for the same reason — 500 could be DBU,
    /// µm or mm, three boards six orders of magnitude apart, and a region drawn at the wrong one selects
    /// nothing or everything with no error. Null on success.
    /// </summary>
    private static int? Coordinate(string text, string option, LayoutView view, out long dbu)
    {
        dbu = 0;
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || !char.IsLetter(trimmed[^1]))
        {
            var units = new List<LayoutUnit> { LayoutUnit.Um, LayoutUnit.Mm };
            if (!units.Contains(view.DisplayUnit)) units.Add(view.DisplayUnit);
            return JsonRun.Fail(CliDiagnostics.ImpedanceCoordinateNeedsUnit(option, text,
                string.Join(" or ", units.Select(u => $"'{trimmed}{LayoutUnits.AsciiSuffix(u)}'"))));
        }
        return LayoutUnits.TryParse(trimmed, LayoutUnit.Um, view.DbuPerMicron, out dbu)
            ? null
            : JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber(option, text, "a coordinate with its unit, e.g. 12.5mm"));
    }

    private sealed class Inline(Action<RunProgress> a) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => a(value);
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf impedance <layout> [--target 50] [--tol 10] [--warn 20] [--max-freq 6GHz]\n" +
            "                           [--layers \"Top Copper,Inner 2\"] [--max-width <um>]\n" +
            "                           [--width \"Top Copper=457\"]... [--no-scope] [--survey]\n" +
            "                           [--region x0,y0,x1,y1]... [--net <name>]... [--pick <layer>@<x>,<y>[:connected]]...\n" +
            "                           [--severity warning|fail] [--ignore-accepted] [-o report.pdf]\n" +
            "  <layout> is a .clay or a cell folder holding one. The review saved on the layout applies\n" +
            "  unless a flag overrides it; --survey lists the trace widths per layer and analyses nothing.\n" +
            "  --region, --net and --pick select traces and each replaces the saved selectors of its kind;\n" +
            "  every coordinate carries a unit (12.5mm, 400um, 50mil). Findings accepted in the editor are\n" +
            "  reported ACCEPTED and do not count; --ignore-accepted counts them.");
        return 1;
    }

    private static int? Parse(string[] args, Options o)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o" or "--output" when i + 1 < args.Length: o.Output = args[++i]; continue;
                case "--target" when i + 1 < args.Length:
                {
                    if (!TryOhms(args[++i], out double target) || !(target > 0))
                        return JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber("--target", args[i], "a positive number of ohms"));
                    o.Target = target;
                    continue;
                }
                case "--tol" or "--tolerance" when i + 1 < args.Length:
                {
                    if (!double.TryParse(args[++i].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double tol)
                        || !(tol > 0) || tol >= 100)
                        return JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber("--tol", args[i], "a percentage above 0 and below 100"));
                    o.Tolerance = tol;
                    continue;
                }
                case "--warn" when i + 1 < args.Length:
                {
                    if (!double.TryParse(args[++i].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double warn)
                        || !(warn > 0) || warn >= 100)
                        return JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber("--warn", args[i], "a percentage above 0 and below 100"));
                    o.Warn = warn;
                    continue;
                }
                case "--max-freq" when i + 1 < args.Length:
                {
                    // The unit is REQUIRED, the rule every CLI frequency follows: a bare 6 is 6 Hz to
                    // one reader and 6 GHz to another, and λ/20 at the wrong one softens nothing or
                    // everything.
                    string text = args[++i];
                    var (_, unit) = CircuitRF.Design.Matching.MatchValueFormat.SplitTypedValue(text);
                    if (CircuitRF.Design.Matching.MatchValueFormat.TryMatchUnit(unit, CircuitRF.Design.Matching.MatchQuantity.Frequency) is null
                        || !CircuitRF.Design.Matching.MatchValueFormat.TryParseWithUnit(
                               text, CircuitRF.Design.Matching.MatchQuantity.Frequency, "Hz", out double hz, out _)
                        || !(hz > 0))
                        return JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber("--max-freq", text, "a frequency with its unit, e.g. 6GHz"));
                    o.MaxFrequencyHz = hz;
                    continue;
                }
                case "--severity" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "warning":          o.WarningsFail = true;  break;
                        case "fail" or "error":  o.WarningsFail = false; break;
                        default: return JsonRun.Fail(CliDiagnostics.ImpedanceUnknownSeverity(args[i]));
                    }
                    continue;
                case "--layers" or "--layer" when i + 1 < args.Length:
                    o.Layers.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    continue;
                case "--max-width" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i].Replace("um", "", StringComparison.OrdinalIgnoreCase).Replace("µm", ""),
                                         NumberStyles.Float, CultureInfo.InvariantCulture, out double mw) || !(mw > 0))
                        return JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber("--max-width", args[i], "a positive width in µm"));
                    o.MaxWidthMicrons = mw;
                    continue;
                case "--width" when i + 1 < args.Length:
                {
                    // <layer>=<width>[,<width>…]; a width takes a unit suffix, and a bare number is µm
                    // as --max-width reads it.
                    string text = args[++i];
                    int eq = text.IndexOf('=');
                    var microns = new List<double>();
                    if (eq > 0)
                        foreach (string w in text[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!LayoutUnits.TryParse(w, LayoutUnit.Um, 1000, out long nm) || nm <= 0) { microns.Clear(); break; }
                            microns.Add(nm / 1000.0);
                        }
                    if (microns.Count == 0)
                        return JsonRun.Fail(CliDiagnostics.ImpedanceBadNumber("--width", text,
                            "a copper layer and one or more widths, e.g. \"Top Copper=457\" or \"Top Copper=18mil,10mil\""));
                    o.Widths.Add((text[..eq].Trim(), microns));
                    continue;
                }
                case "--region" when i + 1 < args.Length: o.Regions.Add(args[++i]); continue;
                case "--net" when i + 1 < args.Length: o.Nets.Add(args[++i]); continue;
                case "--pick" when i + 1 < args.Length: o.Picks.Add(args[++i]); continue;
                case "--no-scope": o.NoScope = true; continue;
                case "--survey": o.Survey = true; continue;
                case "--ignore-accepted": o.IgnoreAccepted = true; continue;
                default:
                    if (a.StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImpedanceUnknownOption(a)); return Usage(); }
                    if (o.Path is not null) { JsonRun.Report(CliDiagnostics.ImpedanceMultiplePaths()); return Usage(); }
                    o.Path = a;
                    continue;
            }
        }
        return null;
    }

    private static bool TryOhms(string text, out double value)
    {
        string t = text.Trim();
        foreach (string suffix in new[] { "ohms", "ohm", "Ω", "R" })
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { t = t[..^suffix.Length].Trim(); break; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The <c>.clay</c> a path means: itself, or a cell folder's primary layout view.</summary>
    private static string? ResolveLayout(string path)
    {
        var kind = DocumentKinds.Classify(path);
        if (kind == DocumentKind.Layout) return Path.GetFullPath(path);
        if (kind == DocumentKind.Cell &&
            CircuitRF.Design.Cells.CellFolder.ResolvePrimary(path, CircuitRF.Design.Cells.ViewType.Layout).ResolvedName is { Length: > 0 } name)
            return Path.Combine(CircuitRF.Design.Cells.CellFolder.SubFolderPath(path, CircuitRF.Design.Cells.ViewType.Layout), name);
        return null;
    }

    /// <summary>The names of the technology's drawing layers bound to a conductor of the stackup.</summary>
    private static List<string> CopperLayers(Technology tech)
    {
        var bound = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).SelectMany(l => l.DrawingLayers).ToHashSet();
        return [.. tech.Layers.Where(l => bound.Contains(l.Key)).Select(l => l.Name)];
    }

    /// <summary>The copper layer <paramref name="name"/> names, ignoring case, or null.</summary>
    private static LayerDef? CopperLayer(Technology tech, List<string> copperNames, string name)
    {
        var def = tech.Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        return def is not null && copperNames.Contains(def.Name) ? def : null;
    }

    // ── --survey ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The width classes per layer, and nothing solved but one typical cut per class — the
    /// headless way to choose <c>--width</c> (R-imp2-4d). Exit 0 unless it is refused or cancelled.</summary>
    private static int RunSurvey(TraceImpedanceAnalysis.LayoutSource source, TraceImpedanceOptions options,
                                 RunControl control, string clay)
    {
        TraceWidthSurvey survey;
        try { survey = TraceImpedanceAnalysis.Survey(source, options, control); }
        catch (OperationCanceledException)
        {
            JsonRun.Report(CliDiagnostics.ImpedanceCancelled());
            return 130;
        }
        if (survey.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.ImpedanceRefused(clay, why));
        foreach (string note in survey.Notes) Console.Error.WriteLine($"[circuitRF] {note}");

        string title = TraceImpedanceAnalysis.CellTitle(clay);
        var sb = new StringBuilder();
        sb.AppendLine($"Trace widths: {title} — typical Z0 is one cut at the middle of each class's longest trace");
        foreach (var layer in survey.Layers)
        {
            sb.AppendLine();
            sb.AppendLine($"{layer.Name}: {layer.Classes.Sum(c => c.TraceCount)} trace(s) in {layer.Classes.Count} width class(es)" +
                          (layer.PoursSkipped > 0 ? $", {layer.PoursSkipped} pour(s) not analysed" : ""));
            foreach (var c in layer.Classes)
            {
                string z0 = c.TypicalZ0 is { } z ? z.ToString("0.0", CultureInfo.InvariantCulture) + " Ω" : $"— ({c.TypicalRefusal})";
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {c.WidthText,-16} {c.TraceCount,4} trace(s)  {c.TotalLengthMicrons / 1000:0.###} mm  typical Z0 {z0}"));
            }
        }
        Console.Write(sb.ToString());

        JsonRun.ImpedanceSurvey = new ImpedanceSurveyJson(
            title, source.Technology?.Name ?? "", survey.SolveCount,
            [.. survey.Layers.Select(l => new ImpedanceSurveyLayerJson(l.Name, l.PoursSkipped,
                [.. l.Classes.Select(c => new ImpedanceWidthClassJson(
                    c.NominalMicrons, c.MinMicrons, c.MaxMicrons, c.TraceCount, c.TotalLengthMicrons,
                    c.TypicalZ0, c.TypicalRefusal))]))]);
        return 0;
    }

    // ── the text report (stdout) ─────────────────────────────────────────────────────────────

    private static string Text(TraceImpedanceReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Trace impedance: {r.Title} — target {r.TargetOhms:0.##} Ω ± {r.TolerancePercent:0.##} % " +
                      $"(pass {r.LowOhms:0.0}–{r.HighOhms:0.0} Ω, warning {r.WarnLowOhms:0.0}–{r.WarnHighOhms:0.0} Ω)" +
                      (r.MaxFrequencyHz is { } f ? $"; under λ/{TraceImpedanceAnalysis.ShortFraction:0} at {TraceImpedanceReport.Hz(f)} is electrically short" : ""));
        if (r.ScopeText.Length > 0) sb.AppendLine(r.ScopeText);
        foreach (var layer in r.Layers)
        {
            sb.AppendLine();
            sb.AppendLine($"{layer.Name}: {layer.Traces.Count} trace(s)" +
                          (layer.PoursSkipped > 0 ? $", {layer.PoursSkipped} pour(s) not analysed" : ""));
            foreach (var t in layer.Traces)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {t.Id,-5} {Verdict(t.Verdict),-4}  Z0 {Z(t.Z0Min)}–{Z(t.Z0Max)} Ω (avg {Z(t.Z0Mean)}), {t.InTolerance:0%} in band, {t.TypeSummary}, {r.Len(t.Length)}, {r.Pt(t.StartX, t.StartY)} → {r.Pt(t.EndX, t.EndY)} [{t.StartsAt} / {t.EndsAt}]"));
                foreach (var issue in t.Issues)
                    sb.AppendLine(issue.Accepted is { } a
                        ? $"        ✓ ACCEPTED ({a.Reason}; {a.AcceptedUtc:yyyy-MM-dd}): {issue.Text}"
                        : $"        {(issue.Fails ? '!' : '?')} {issue.Text}");
                foreach (var note in t.Notes) sb.AppendLine($"        · {note}");
            }
        }
        sb.AppendLine();
        if (r.StaleAcceptances.Count > 0)
        {
            sb.AppendLine($"Accepted on the layout but matched nothing in this run ({r.StaleAcceptances.Count}) — the trace moved, or the finding is gone:");
            foreach (var a in r.StaleAcceptances) sb.AppendLine($"  {a.LayerName}: {a.Summary} (reason: {a.Reason})");
            sb.AppendLine();
        }
        sb.AppendLine($"{r.TraceCount} trace(s) on {r.Layers.Count} layer(s): {r.PassCount} pass, {r.WarningCount} warning, {r.FailCount} fail." +
                      (r.AcceptedCount > 0 ? $" {r.AcceptedCount} finding(s) accepted." : "") +
                      (r.Cancelled ? $" Cancelled after {r.Layers.Count} of {r.LayersRequested.Count} layers." : ""));
        return sb.ToString();

        static string Z(double? z) => z is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        static string Verdict(TraceVerdict v) => v switch
        {
            TraceVerdict.Pass => "PASS", TraceVerdict.Warning => "WARN", TraceVerdict.Fail => "FAIL", _ => "—",
        };
    }

    // ── --json ───────────────────────────────────────────────────────────────────────────────

    private static ImpedanceReportJson Project(TraceImpedanceReport r)
    {
        double Um(double dbu) => dbu / r.DbuPerMicron;
        return new ImpedanceReportJson(
            r.Title, r.TechnologyName, r.TargetOhms, r.TolerancePercent, r.WarningPercent, r.MaxFrequencyHz,
            r.ScopeText, r.Cancelled, r.TraceCount, r.PassCount, r.WarningCount, r.FailCount,
            [.. r.Layers.Select(l => new ImpedanceLayerJson(
                l.Name, l.PoursSkipped, l.OutOfScope,
                [.. l.Traces.Select(t => new ImpedanceTraceJson(
                    t.Id, t.Verdict.ToString().ToLowerInvariant(),
                    [Um(t.StartX), Um(t.StartY)], [Um(t.EndX), Um(t.EndY)], t.StartsAt, t.EndsAt,
                    Um(t.Length), Um(t.WidthMin), Um(t.WidthMax),
                    t.Z0Min, t.Z0Max, t.Z0Mean, t.InTolerance, t.Configuration,
                    [.. t.Configurations.Select(c => new ImpedanceTypeJson(c.Name, c.Share))], t.References,
                    [.. t.Issues.Select(i => new ImpedanceIssueJson(
                        IssueId(i.Kind), i.Fails ? "fail" : "warning", [Um(i.X0), Um(i.Y0)], [Um(i.X1), Um(i.Y1)], i.Text,
                        i.Accepted is { } a ? new ImpedanceAcceptedJson(a.Reason, a.AcceptedUtc) : null))],
                    t.Notes))]))],
            r.AcceptedCount,
            [.. r.StaleAcceptances.Select(a => new ImpedanceStaleAcceptanceJson(
                a.Key, IssueId(a.Kind), a.LayerName, a.Summary, a.Reason, a.AcceptedUtc))]);
    }

    private static string IssueId(TraceIssueKind k) => k switch
    {
        TraceIssueKind.OutOfTolerance   => "out-of-tolerance",
        TraceIssueKind.ReturnBroken     => "return-broken",
        TraceIssueKind.PartialReference => "partial-reference",
        TraceIssueKind.ReferenceStep    => "reference-step",
        TraceIssueKind.NoReference      => "no-reference",
        _                               => "unsolved",
    };
}
