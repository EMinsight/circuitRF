using System.Globalization;
using System.Text;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf review &lt;layout&gt;</c> — the Layout Review: the built-in checks a person reviewing
/// a board would run by eye, each marking the places worth a look with a sentence saying why
/// (brief-review-2 R-rev2-10).
///
/// <para><b>It owns no check</b>, on <c>src/Cli/Authoring.cs</c>' terms: every finding comes out of
/// <see cref="LayoutReview.Run"/> in <c>src/Design/Layout/Review</c>, the function the Review panel
/// calls, read through <see cref="TraceImpedanceAnalysis.LoadLayout"/> once. What is here is argument
/// parsing, refusals and reporting.</para>
///
/// <para><b>The review saved on the layout applies by default</b> — which checks run and the thresholds
/// the panel last saved — each <c>--set</c> overriding it for this run only; <c>--defaults</c> ignores
/// it. Saved acceptances apply unless <c>--ignore-accepted</c>: an accepted finding is reported ACCEPTED
/// with its reason and does not count. There is no <c>--accept</c>, on <c>impedance</c>'s terms: a
/// headless caller edits the <c>.clay</c>, whose key the file-format reference documents.</para>
///
/// <para><b>Read-only, and findings are advice.</b> It writes nothing. Exit 0 when it ran, whatever it
/// found; 1 on a refusal, or with <c>--severity warning</c> when an unaccepted warning remains; 130 on
/// cancellation. Never 2 — nothing here solves.</para>
/// </summary>
internal static class Review
{
    /// <summary>At most this many findings per check per layer, without <c>--all</c>.</summary>
    internal const int ListedPerLayer = 20;

    private sealed class Options
    {
        public string? Path;
        public string? Cell;
        public List<string>? Checks;
        public readonly List<string> Skip = [];
        public readonly List<string> Sets = [];
        public bool ListChecks;
        public bool Defaults;
        public bool IgnoreAccepted;
        public bool All;
        public bool WarningsFail;
        public ReviewTarget? Target;
    }

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;

        // Every check id named is checked before anything is read: a typo must not cost a large board's read.
        foreach (string id in (o.Checks ?? []).Concat(o.Skip))
            if (LayoutReview.FindCheck(id) is null)
                return JsonRun.Fail(CliDiagnostics.ReviewUnknownCheck(id, KnownChecks()));

        if (o.Path is null)
        {
            if (o.ListChecks) return List(null, o, new ReviewSettings { Target = o.Target });
            JsonRun.Report(CliDiagnostics.ReviewPathRequired());
            return Usage();
        }
        JsonRun.InputPath = o.Path;
        if (!File.Exists(o.Path) && !Directory.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.ReviewPathNotFound(o.Path));

        var (clay, refusal) = ResolveLayout(o);
        if (refusal is { } no) return no;

        using var generated = GeneratedCells.Prepare(clay!, mayWrite: false, out int? cellRefusal);
        if (cellRefusal is { } cellRefused) return cellRefused;

        TraceImpedanceAnalysis.LayoutSource source;
        try { source = TraceImpedanceAnalysis.LoadLayout(clay!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return JsonRun.Fail(CliDiagnostics.ReviewLayoutUnreadable(clay!, ex.Message));
        }

        if (Settings(o, source.View) is not { } settings) return 1;
        if (o.ListChecks) return List(source.View, o, settings);

        var lines = new Impedance.ProgressLines(Console.Error, Impedance.ProgressInterval);
        var control = new RunControl
        {
            Token = RunHost.Cancellation,
            Progress = new Inline(p =>
            {
                RunHost.Observer?.Invoke(p);
                lines.Report(p);
            }),
        };

        var result = LayoutReview.Run(source, settings, control);
        if (result.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.ReviewRefused(clay!, why));
        result = ReviewAcceptance.Apply(result, o.IgnoreAccepted ? [] : source.View.ReviewAcceptances);

        foreach (string d in result.Diagnostics) Console.Error.WriteLine($"[circuitRF] {d}");
        Console.Write(Text(result, clay!, o.All));
        JsonRun.Review = Project(result, clay!, settings);

        if (result.Cancelled)
        {
            JsonRun.Report(CliDiagnostics.ReviewCancelled(
                result.Checks.Count(c => c.State != ReviewCheckState.Cancelled), result.Checks.Count));
            return 130;
        }
        return o.WarningsFail && result.WarningCount > 0 ? 1 : 0;
    }

    private sealed class Inline(Action<RunProgress> a) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => a(value);
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf review <layout> [--cell <name>] [--checks id,id] [--skip id,id]\n" +
            "                        [--set <check>.<threshold>=<value>]... [--defaults] [--ignore-accepted]\n" +
            "                        [--as pcb|mmic] [--all] [--severity warning|none]\n" +
            "       circuitrf review [<layout>] [--as pcb|mmic] --list-checks\n" +
            "  <layout> is a .clay, a cell folder holding one, or a workspace with --cell. The review saved on\n" +
            "  the layout applies unless --defaults; each --set overrides it for this run, a length WITH its unit\n" +
            "  (0.3mm, 12mil). --as reviews the layout as a board or a die; the layout's saved choice applies\n" +
            "  otherwise, a board when it has none. Findings are advice: exit 0 unless --severity warning and one\n" +
            "  is unaccepted.\n" +
            "  At most " + ListedPerLayer + " findings per check per layer are listed; --all lists every one.");
        return 1;
    }

    private static int? Parse(string[] args, Options o)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--cell" when i + 1 < args.Length: o.Cell = args[++i]; continue;
                case "--checks" when i + 1 < args.Length:
                    (o.Checks ??= []).AddRange(Ids(args[++i]));
                    continue;
                case "--skip" when i + 1 < args.Length: o.Skip.AddRange(Ids(args[++i])); continue;
                case "--set" when i + 1 < args.Length: o.Sets.Add(args[++i]); continue;
                case "--list-checks": o.ListChecks = true; continue;
                case "--defaults": o.Defaults = true; continue;
                case "--ignore-accepted": o.IgnoreAccepted = true; continue;
                case "--all": o.All = true; continue;
                case "--as" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "pcb":  o.Target = ReviewTarget.Pcb; break;
                        case "mmic": o.Target = ReviewTarget.Mmic; break;
                        default: return JsonRun.Fail(CliDiagnostics.ReviewUnknownTarget(args[i]));
                    }
                    continue;
                case "--severity" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "warning": o.WarningsFail = true; break;
                        case "none":    o.WarningsFail = false; break;
                        default: return JsonRun.Fail(CliDiagnostics.ReviewUnknownSeverity(args[i]));
                    }
                    continue;
                default:
                    if (a.StartsWith('-')) { JsonRun.Report(CliDiagnostics.ReviewUnknownOption(a)); return Usage(); }
                    if (o.Path is not null) { JsonRun.Report(CliDiagnostics.ReviewMultiplePaths()); return Usage(); }
                    o.Path = a;
                    continue;
            }
        }
        return null;
    }

    private static IEnumerable<string> Ids(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string KnownChecks() => string.Join(", ", LayoutReview.Checks.Select(c => c.Id));

    /// <summary>The <c>.clay</c> the path names — a layout, a cell folder's primary layout, or a
    /// workspace's cell by <c>--cell</c> — inferred through <see cref="DocumentKinds.Classify"/> as
    /// <c>lvs</c> and <c>render</c> infer it.</summary>
    private static (string? Clay, int? Refusal) ResolveLayout(Options o)
    {
        string path = o.Path!;
        var kind = DocumentKinds.Classify(path);
        switch (kind)
        {
            case DocumentKind.Layout: return (Path.GetFullPath(path), null);
            case DocumentKind.Cell:   return CellLayout(path, kind);
            case DocumentKind.Workspace:
            {
                string root = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path))!;
                if (o.Cell is null) return (null, JsonRun.Fail(CliDiagnostics.ReviewCellRequired(path)));
                var found = CellLookup.Find(root, o.Cell);
                if (found.Count == 0)
                    return (null, JsonRun.Fail(CliDiagnostics.ReviewNoSuchCell(path, o.Cell, string.Join(", ", CellLookup.Names(root)))));
                if (found.Count > 1)
                    return (null, JsonRun.Fail(CliDiagnostics.ReviewAmbiguousCell(o.Cell, string.Join(", ", found))));
                return CellLayout(found[0], kind);
            }
            case DocumentKind.Interchange:
                return (null, JsonRun.Fail(CliDiagnostics.ReviewNotALayout(path, DocumentKinds.InterchangeFormat(path) ?? "interchange")));
            default:
                return (null, JsonRun.Fail(CliDiagnostics.ReviewNotALayout(path, DocumentKinds.Name(kind))));
        }

        static (string?, int?) CellLayout(string cell, DocumentKind kind) =>
            CellFolder.ResolvePrimary(cell, ViewType.Layout).ResolvedName is { Length: > 0 } name
                ? (Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Layout), name), null)
                : (null, JsonRun.Fail(CliDiagnostics.ReviewNotALayout(cell, "a cell folder with no layout")));
    }

    /// <summary>The settings this run uses: the layout's saved review (or the defaults), then
    /// <c>--checks</c>, <c>--skip</c> and <c>--set</c>. Null after a refusal it has reported.</summary>
    private static ReviewSettings? Settings(Options o, LayoutView view)
    {
        var settings = o.Defaults ? new ReviewSettings() : view.Review?.Clone() ?? new ReviewSettings();
        if (o.Target is { } asked) settings.Target = asked;

        // A check named that this review's target does not run would silently not run: refused, naming --as.
        var target = settings.TargetOrDefault;
        var named = (o.Checks ?? []).Concat(o.Skip)
            .Concat(o.Sets.Select(s => s.IndexOf('=') is > 0 and var eq && s.LastIndexOf('.', eq) is > 0 and var dot ? s[..dot].Trim() : ""));
        foreach (string id in named)
            if (LayoutReview.FindCheck(id) is { } def && !def.AppliesTo(target))
            {
                JsonRun.Fail(CliDiagnostics.ReviewCheckNotForTarget(id, TargetName(target),
                    string.Join(", ", LayoutReview.ChecksFor(target).Select(c => c.Id))));
                return null;
            }

        if (o.Checks is { } only)
            foreach (var c in LayoutReview.Checks) settings.SetEnabled(c.Id, only.Contains(c.Id, StringComparer.Ordinal));
        foreach (string id in o.Skip) settings.SetEnabled(id, false);

        foreach (string text in o.Sets)
        {
            int eq = text.IndexOf('=');
            int dot = eq > 0 ? text.LastIndexOf('.', eq) : -1;
            if (eq <= 0 || dot <= 0) { JsonRun.Fail(CliDiagnostics.ReviewBadSet(text)); return null; }
            string key = text[..eq].Trim(), value = text[(eq + 1)..].Trim();
            if (LayoutReview.FindThreshold(key) is not { } found)
            {
                string checkId = key[..key.LastIndexOf('.')];
                JsonRun.Fail(LayoutReview.FindCheck(checkId) is { } c
                    ? c.SettingsOwner is { } owner
                        ? CliDiagnostics.ReviewSettingsElsewhere(c.Id, owner)
                        : CliDiagnostics.ReviewUnknownThreshold(c.Id, key[(checkId.Length + 1)..], string.Join(", ", c.Thresholds.Select(t => t.Id)))
                    : CliDiagnostics.ReviewUnknownCheck(checkId, KnownChecks()));
                return null;
            }
            var (check, threshold) = found;
            double stored;
            if (threshold.Kind == ReviewThresholdKind.Length)
            {
                // A bare number is refused, render --window's rule: 0.3 could be DBU, µm or mm.
                if (value.Length == 0 || !char.IsLetter(value[^1]))
                {
                    var units = new List<LayoutUnit> { LayoutUnit.Mm, LayoutUnit.Um };
                    if (!units.Contains(view.DisplayUnit)) units.Add(view.DisplayUnit);
                    JsonRun.Fail(CliDiagnostics.ReviewSetNeedsUnit(key, value,
                        string.Join(" or ", units.Select(u => $"'{key}={value}{LayoutUnits.AsciiSuffix(u)}'"))));
                    return null;
                }
                if (!LayoutUnits.TryParse(value, LayoutUnit.Um, view.DbuPerMicron, out long dbu) || dbu < 0)
                {
                    JsonRun.Fail(CliDiagnostics.ReviewBadNumber(key, value, "a length with its unit, e.g. 0.3mm"));
                    return null;
                }
                stored = dbu;
            }
            else if (threshold.Kind == ReviewThresholdKind.Area)
            {
                // An area carries its unit squared, as a length carries its unit: a bare 2 could be mm² or µm².
                if (!ReviewText.TryParseArea(value, null, out stored))
                {
                    JsonRun.Fail(ReviewText.TryParseArea(value, LayoutUnit.Mm, out _)
                        ? CliDiagnostics.ReviewSetNeedsUnit(key, value, $"'{key}={value}mm2' or '{key}={value}um2'", "an area", "mm² or µm²")
                        : CliDiagnostics.ReviewBadNumber(key, value, "an area with its unit, e.g. 2mm2"));
                    return null;
                }
                if (stored < 0) { JsonRun.Fail(CliDiagnostics.ReviewBadNumber(key, value, "an area of zero or more")); return null; }
            }
            else if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out stored)
                     || !double.IsFinite(stored) || stored < 0)
            {
                JsonRun.Fail(CliDiagnostics.ReviewBadNumber(key, value, "a number of zero or more"));
                return null;
            }
            settings.Set(check, threshold, stored, view.DbuPerMicron);
        }
        return settings;
    }

    // ── --list-checks ───────────────────────────────────────────────────────────────────────────

    private static int List(LayoutView? view, Options o, ReviewSettings? settings = null)
    {
        var unit = view?.DisplayUnit ?? LayoutUnit.Mm;
        int dpm = view?.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron;
        settings ??= new ReviewSettings();
        var saved = o.Defaults ? null : view?.Review;
        var text = new StringBuilder();
        var rows = new List<ReviewCheckJson>();
        text.AppendLine($"Checks of a {TargetName(settings.TargetOrDefault)} review:");
        foreach (var c in LayoutReview.ChecksFor(settings.TargetOrDefault))
        {
            bool on = settings.IsEnabled(c.Id);
            text.AppendLine($"{c.Id}  {c.Title}{(on ? "" : "  (off)")}");
            text.AppendLine($"  {c.Description}");
            foreach (var t in c.Thresholds)
            {
                double value = settings.Value(c, t, dpm);
                string line = $"  {c.Id}.{t.Id}  {ReviewSettings.Display(t, value, unit, dpm)}" +
                              $"  (default {ReviewSettings.Display(t, t.DefaultStored(dpm), unit, dpm)}";
                if (saved?.Thresholds is { } st && st.TryGetValue(ReviewSettings.KeyOf(c.Id, t.Id), out double s))
                    line += $"; saved {ReviewSettings.Display(t, s, unit, dpm)}";
                text.AppendLine(line + ")");
            }
            // R-rev5-5: a check whose settings are another review's shows them read-only, with where to change them.
            string? elsewhere = c.SettingsOwner is null ? null : LayoutReview.SettingsText(c.Id, view);
            if (elsewhere is not null) text.AppendLine($"  {elsewhere}");
            rows.Add(new ReviewCheckJson(c.Id, c.Title, "listed", on ? null : "turned off", 0, 0,
                                         Thresholds(c, settings, saved, unit, dpm), c.Description, Settings: elsewhere));
        }
        Console.Write(text.ToString());
        JsonRun.ReviewChecks = rows;
        return 0;
    }

    // ── the human report ────────────────────────────────────────────────────────────────────────

    private static string Text(LayoutReviewResult r, string clay, bool all)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Layout Review of {Path.GetFileName(clay)}" + (r.TechnologyName is { } t ? $" ({t})" : "")
                    + $", reviewed as a {TargetName(r.Target)}");
        foreach (var check in r.Checks)
        {
            switch (check.State)
            {
                case ReviewCheckState.NotRun:
                    sb.AppendLine($"{check.Id}: not run — {check.NotRunReason}");
                    continue;
                case ReviewCheckState.Cancelled:
                    sb.AppendLine($"{check.Id}: cancelled");
                    continue;
            }
            int open = check.FindingCount - check.AcceptedCount;
            sb.AppendLine($"{check.Id}: {open} finding(s)" + (check.AcceptedCount > 0 ? $", {check.AcceptedCount} accepted" : "")
                        + $" — {check.Title}");
            if (check.Summary is { } summary) sb.AppendLine($"  {summary}");
            foreach (var group in r.Findings.Where(f => f.CheckId == check.Id && f.Severity == ReviewSeverity.Warning)
                                            .GroupBy(f => f.Layer))
            {
                var list = group.ToList();
                sb.AppendLine($"  {list[0].LayerName ?? "—"} ({list.Count})");
                foreach (var f in all ? list : list.Take(ListedPerLayer))
                    sb.AppendLine($"    {f.Sentence} at {Where(r, f.Marker)}"
                                + (f.Accepted is { } a ? $"  ACCEPTED: {a.Reason}" : ""));
                if (!all && list.Count > ListedPerLayer)
                    sb.AppendLine($"    … and {list.Count - ListedPerLayer} more");
            }
        }
        foreach (var stale in r.StaleAcceptances)
            sb.AppendLine($"Stale acceptance ({stale.CheckId}, {stale.LayerName}): \"{stale.Summary}\" — {stale.Reason}");
        int notRun = r.Checks.Count(c => c.State == ReviewCheckState.NotRun);
        sb.AppendLine($"{r.WarningCount} finding(s)" + (r.AcceptedCount > 0 ? $", {r.AcceptedCount} accepted" : "")
                    + (notRun > 0 ? $", {notRun} check(s) not run" : "") + (r.Cancelled ? ", cancelled" : "") + ".");
        return sb.ToString();
    }

    private static string Where(LayoutReviewResult r, Bbox b) =>
        $"({LayoutUnits.Format((b.MinX + b.MaxX) / 2, r.DisplayUnit, r.DbuPerMicron, 3)}, " +
        $"{LayoutUnits.Format((b.MinY + b.MaxY) / 2, r.DisplayUnit, r.DbuPerMicron, 3)}) {LayoutUnits.Suffix(r.DisplayUnit)}";

    // ── the --json projection ───────────────────────────────────────────────────────────────────
    //
    // A projection of LayoutReviewResult and nothing else: every count is one of its fields or derived
    // accessors, so there is no second tally to disagree with the first.

    internal static ReviewReportJson Project(LayoutReviewResult r, string clay, ReviewSettings settings)
    {
        double Um(long dbu) => (double)dbu / r.DbuPerMicron;
        return new ReviewReportJson(
            clay, r.TechnologyName, LayoutUnits.AsciiSuffix(r.DisplayUnit), r.Cancelled,
            r.WarningCount, r.AcceptedCount, r.NoteCount,
            [.. r.Checks.Select(c => new ReviewCheckJson(
                c.Id, c.Title, c.State switch
                {
                    ReviewCheckState.Ran => "ran",
                    ReviewCheckState.NotRun => "not-run",
                    _ => "cancelled",
                },
                c.NotRunReason, c.FindingCount, c.AcceptedCount,
                LayoutReview.FindCheck(c.Id) is { } def ? Thresholds(def, settings, null, r.DisplayUnit, r.DbuPerMicron) : [],
                Summary: c.Summary))],
            [.. r.Findings.Select(f => new ReviewFindingJson(
                f.CheckId, f.LayerName,
                f.Severity == ReviewSeverity.Note ? "note" : "warning",
                f.Sentence, f.MeasuredText, f.Key,
                f.Marker.IsEmpty ? null : [Um(f.Marker.MinX), Um(f.Marker.MinY), Um(f.Marker.MaxX), Um(f.Marker.MaxY)],
                f.Accepted is { } a ? new ImpedanceAcceptedJson(a.Reason, a.AcceptedUtc) : null))],
            [.. r.StaleAcceptances.Select(a => new ReviewStaleAcceptanceJson(a.Key, a.CheckId, a.LayerName, a.Summary, a.Reason, a.AcceptedUtc))],
            r.Diagnostics,
            r.Target == ReviewTarget.Mmic ? "mmic" : "pcb");
    }

    /// <summary>"PCB" or "MMIC" — how a sentence names the target.</summary>
    internal static string TargetName(ReviewTarget target) => target == ReviewTarget.Mmic ? "MMIC" : "PCB";

    private static List<ReviewThresholdJson> Thresholds(ReviewCheckDef c, ReviewSettings settings, ReviewSettings? saved,
                                                        LayoutUnit unit, int dpm)
    {
        double Out(ReviewThresholdDef t, double stored) => t.Kind == ReviewThresholdKind.Length ? stored / dpm : stored;
        return [.. c.Thresholds.Select(t =>
        {
            double value = settings.Value(c, t, dpm);
            double? s = saved?.Thresholds is { } st && st.TryGetValue(ReviewSettings.KeyOf(c.Id, t.Id), out double v) ? Out(t, v) : null;
            return new ReviewThresholdJson(t.Id, t.Title, t.Kind.ToString().ToLowerInvariant(),
                                           Out(t, value), Out(t, t.DefaultStored(dpm)),
                                           ReviewSettings.Display(t, value, unit, dpm), s);
        })];
    }
}
