// `circuitrf recognize <path>` — a board's artwork as a circuit, headlessly (brief-artsch-7-cli-and-mcp.md;
// docs/design/cli.md §26; docs/design/artwork-to-schematic.md).
//
// THIS FILE OWNS NO RECOGNITION, and it must never start (R-as7-1). Every decision — ground, vias, ports,
// parts, lines, names, the drawing, the target's rules — is ArtworkRecognition's (src/Design/Layout/
// Recognition), the function the GUI's Create Schematic from Artwork calls. What is here is argument parsing,
// refusals and reporting, on Authoring.cs' terms: an operation that lives only in a verb is not a capability,
// and a verb that re-implements one diverges from it silently. A comment-stripped source scan holds it: src/Cli
// names nothing in that namespace but the entry point, its options, its result and PartsTableCsv.
//
// READ-ONLY BY DEFAULT (R-as7-3). With neither -o nor --into nothing is written: the report and the parts table
// go to stdout. That is the first call an agent makes — review the parts, then write.
//
// THE TARGETS ARE WRITTEN LAST (R-as7-6), after the recognition and the emit have finished, so a cancelled run
// (130) or a refused one (1) has written nothing. Never 2: nothing here solves a circuit.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using RfCore.Export;

namespace CircuitRF.Cli;

internal static class Recognize
{
    private sealed class Options
    {
        public string? Path, Cell, Output, Into, PartsOut, Parts, Bom, Placement, Region, Ground, GroundAt;
        public bool Replace;
        public PlacementOrigin? PlacementOrigin;
        public LayoutUnit? PlacementUnit;
        public ViaPolicy? Vias;
        public CoplanarReading? Coplanar;
        public double? CoplanarFactor;
        public (string Text, string Unit, double Hz)? Start, Stop;
        public int? Npts;
    }

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;
        if (o.Path is null) { JsonRun.Report(CliDiagnostics.RecognizePathRequired()); return Usage(); }
        JsonRun.InputPath = o.Path;
        if (!File.Exists(o.Path) && !Directory.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.RecognizePathNotFound(o.Path));

        var (clay, refusal) = ResolveLayout(o);
        if (refusal is { } no) return no;

        try
        {
            return Recognise(o, clay!);
        }
        catch (OperationCanceledException)
        {
            JsonRun.Report(CliDiagnostics.RecognizeCancelled());
            return 130;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf recognize <file.clay | cell folder | workspace --cell N>\n" +
            "                 [-o circuit.cnl] [--into new:<name> | --into artwork] [--replace]\n" +
            "                 [--parts-out parts.csv] [--parts parts.csv] [--bom file] [--placement file]\n" +
            "                 [--placement-origin symbol|body|pin1] [--placement-unit mm|mil|in]\n" +
            "                 [--region x0,y0,x1,y1] [--ground <net> | --ground-at x,y] [--vias model|ground]\n" +
            "                 [--coplanar auto|microstrip|gcpw] [--coplanar-factor k]\n" +
            "                 [--start f] [--stop f] [--npts n]\n" +
            "  Writes nothing unless -o, --into or --parts-out is given. Coordinates carry a unit (um, mm, mil).");
        return 1;
    }

    // ── arguments ────────────────────────────────────────────────────────────

    private static int? Parse(string[] args, Options o)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool hasValue = i + 1 < args.Length;
            int? Bad(string expected) => JsonRun.Fail(CliDiagnostics.RecognizeBadValue(a, args[i], expected));
            switch (a)
            {
                case "-o" or "--output" when hasValue: o.Output    = args[++i]; continue;
                case "--cell"            when hasValue: o.Cell      = args[++i]; continue;
                case "--into"            when hasValue: o.Into      = args[++i];
                    if (o.Into != "artwork" && !(o.Into.StartsWith("new:", StringComparison.Ordinal) && o.Into.Length > 4))
                        return Bad("new:<name> or artwork");
                    continue;
                case "--replace":                       o.Replace   = true; continue;
                case "--parts-out"       when hasValue: o.PartsOut  = args[++i]; continue;
                case "--parts"           when hasValue: o.Parts     = args[++i]; continue;
                case "--bom"             when hasValue: o.Bom       = args[++i]; continue;
                case "--placement"       when hasValue: o.Placement = args[++i]; continue;
                case "--region"          when hasValue: o.Region    = args[++i]; continue;
                case "--ground"          when hasValue: o.Ground    = args[++i]; continue;
                case "--ground-at"       when hasValue: o.GroundAt  = args[++i]; continue;

                case "--placement-origin" when hasValue:
                    o.PlacementOrigin = args[++i].ToLowerInvariant() switch
                    {
                        "symbol" => PlacementOrigin.SymbolOrigin,
                        "body"   => PlacementOrigin.BodyCentre,
                        "pin1"   => PlacementOrigin.PinOne,
                        _        => null,
                    };
                    if (o.PlacementOrigin is null) return Bad("symbol, body or pin1");
                    continue;
                case "--placement-unit" when hasValue:
                    o.PlacementUnit = args[++i].ToLowerInvariant() switch
                    {
                        "mm"           => LayoutUnit.Mm,
                        "mil"          => LayoutUnit.Mil,
                        "in" or "inch" => LayoutUnit.Inch,
                        _              => null,
                    };
                    if (o.PlacementUnit is null) return Bad("mm, mil or in");
                    continue;
                case "--vias" when hasValue:
                    o.Vias = args[++i].ToLowerInvariant() switch
                    {
                        "model"  => ViaPolicy.Model,
                        "ground" => ViaPolicy.Ground,
                        _        => null,
                    };
                    if (o.Vias is null) return Bad("model or ground");
                    continue;
                case "--coplanar" when hasValue:
                    o.Coplanar = args[++i].ToLowerInvariant() switch
                    {
                        "auto"       => CoplanarReading.Auto,
                        "microstrip" => CoplanarReading.Microstrip,
                        "gcpw"       => CoplanarReading.Gcpw,
                        _            => null,
                    };
                    if (o.Coplanar is null) return Bad("auto, microstrip or gcpw");
                    continue;
                case "--coplanar-factor" when hasValue:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double k) || !(k > 0))
                        return Bad("a positive number of substrate heights");
                    o.CoplanarFactor = k;
                    continue;
                case "--npts" when hasValue:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 2)
                        return Bad("a whole number of points, at least 2");
                    o.Npts = n;
                    continue;
                case "--start" when hasValue:
                    if (Frequency(args[++i]) is not { } start) return Bad("a frequency with its unit (Hz, kHz, MHz, GHz)");
                    o.Start = start;
                    continue;
                case "--stop" when hasValue:
                    if (Frequency(args[++i]) is not { } stop) return Bad("a frequency with its unit (Hz, kHz, MHz, GHz)");
                    o.Stop = stop;
                    continue;

                default:
                    if (a.StartsWith('-')) { JsonRun.Report(CliDiagnostics.RecognizeUnknownOption(a)); return Usage(); }
                    if (o.Path is not null) { JsonRun.Report(CliDiagnostics.RecognizeMultiplePaths()); return Usage(); }
                    o.Path = a;
                    continue;
            }
        }

        if (o.Output is { } output && !output.EndsWith(".cnl", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(CliDiagnostics.RecognizeOutputNotCnl(output));
        return null;
    }

    private static readonly Regex FrequencyText =
        new(@"^\s*([0-9]*\.?[0-9]+(?:[eE][+-]?[0-9]+)?)\s*(Hz|kHz|MHz|GHz|THz)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A frequency with its unit, the unit spelled as an analysis line spells it. A bare number is not one.</summary>
    private static (string Text, string Unit, double Hz)? Frequency(string text)
    {
        var m = FrequencyText.Match(text);
        if (!m.Success) return null;
        string unit = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "hz" => "Hz", "khz" => "kHz", "mhz" => "MHz", "ghz" => "GHz", _ => "THz",
        };
        double scale = unit switch { "Hz" => 1, "kHz" => 1e3, "MHz" => 1e6, "GHz" => 1e9, _ => 1e12 };
        return (m.Groups[1].Value, unit, double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * scale);
    }

    // ── which layout (R-as7-2) ───────────────────────────────────────────────

    private static (string? Clay, int? Refusal) ResolveLayout(Options o)
    {
        string path = o.Path!;
        var kind = DocumentKinds.Classify(path);
        if (o.Cell is not null && kind != DocumentKind.Workspace)
            return (null, JsonRun.Fail(CliDiagnostics.RecognizeBadValue("--cell", o.Cell, "a cell name, and only with a workspace path")));

        switch (kind)
        {
            case DocumentKind.Layout:
                return (Path.GetFullPath(path), null);
            case DocumentKind.Cell:
                return LayoutOf(Path.GetFullPath(path));
            case DocumentKind.Workspace:
            {
                string root = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path))!;
                if (o.Cell is null) return (null, JsonRun.Fail(CliDiagnostics.RecognizeCellRequired(path)));
                var found = CellLookup.Find(root, o.Cell);
                if (found.Count == 0)
                    return (null, JsonRun.Fail(CliDiagnostics.RecognizeNoSuchCell(path, o.Cell, Join(CellLookup.Names(root)))));
                if (found.Count > 1)
                    return (null, JsonRun.Fail(CliDiagnostics.RecognizeAmbiguousCell(o.Cell, Join(found))));
                return LayoutOf(found[0]);
            }
            case DocumentKind.Interchange:
                return (null, JsonRun.Fail(CliDiagnostics.RecognizeNoLayout(path, DocumentKinds.InterchangeFormat(path) ?? "interchange")));
            default:
                return (null, JsonRun.Fail(CliDiagnostics.RecognizeNoLayout(path, DocumentKinds.Name(kind))));
        }
    }

    /// <summary>A cell folder's primary layout — <c>CellFolder.ResolvePrimary</c>'s answer and no other.</summary>
    private static (string? Clay, int? Refusal) LayoutOf(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (primary.ResolvedName is { Length: > 0 } name)
            return (Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name), null);
        if (primary.State == PrimaryState.NoView)
            return (null, JsonRun.Fail(CliDiagnostics.RecognizeNoLayout(cellDir, "a cell folder with no layout view")));
        string dir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.clay").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList() : [];
        return (null, JsonRun.Fail(CliDiagnostics.RecognizeLayoutAmbiguous(cellDir, Join(files!))));
    }

    private static string Join(IReadOnlyList<string> items) => items.Count == 0 ? "(none)" : string.Join(", ", items);

    // ── the run ──────────────────────────────────────────────────────────────

    private static int Recognise(Options o, string clay)
    {
        var input = RecognitionInput.FromFile(clay);
        int dbu = input.View.DbuPerMicron;

        // ── the companions, read with their own refusals (an unstated origin names --placement-origin) ──
        if (o.Placement is { } placementPath)
        {
            if (!File.Exists(placementPath)) return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--placement", placementPath));
            if (PlacementFile.ReadFile(placementPath, dbu, o.PlacementOrigin, o.PlacementUnit) is not { } placement)
                return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--placement", placementPath));
            input = input with { Placement = placement };
        }
        if (o.Bom is { } bomPath)
        {
            if (!File.Exists(bomPath) || BomFile.ReadFile(bomPath) is not { } bom)
                return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--bom", bomPath));
            input = input with { Bom = bom };
        }
        if (o.Parts is { } partsPath)
        {
            if (!File.Exists(partsPath)) return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--parts", partsPath));
            input = input with { PartsCsvPath = Path.GetFullPath(partsPath) };
        }

        // ── scope and options: each absent flag is the dialog's default ─────────────────────────────
        if (o.Region is { } region)
        {
            var (r, refusal) = Coordinates(region, "--region", 4, dbu);
            if (refusal is { } no) return no;
            input = input with { Scope = RecognitionScope.Rectangle(r![0], r[1], r[2], r[3]) };
        }
        var options = input.Options;
        if (o.GroundAt is { } at)
        {
            var (p, refusal) = Coordinates(at, "--ground-at", 2, dbu);
            if (refusal is { } no) return no;
            options = options with { GroundAt = (p![0], p[1]) };
        }
        if (o.Ground is { } net)               options = options with { GroundNet = net };
        if (o.Vias is { } vias)                options = options with { Vias = vias };
        if (o.Coplanar is { } coplanar)        options = options with { Coplanar = coplanar };
        if (o.CoplanarFactor is { } factor)    options = options with { CoplanarGapFactor = factor };
        if (o.Stop is { } top)                 options = options with { TopFrequencyHz = top.Hz };
        input = input with { Options = options };

        var emit = new RecognitionEmitOptions { Sweep = Sweep(o, input) };

        // ── recognise and emit; --into writes the schematic, and only after everything else succeeded ─
        RecognitionResult result;
        RecognitionCircuit? circuit;
        string? schematic = null, cell = null;
        bool checkpoint = false;
        if (o.Into is { } into)
        {
            var (target, refusal) = Target(o, into, clay);
            if (refusal is { } no) return no;
            Console.Error.WriteLine($"[circuitRF] recognising {clay}");
            var run = ArtworkRecognition.Run(input, target!, new RecognitionRunOptions { Emit = emit }, RunHost.Control);
            if (run.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.RecognizeRefused(why));
            (result, circuit, schematic, cell, checkpoint) = (run.Result, run.Circuit, run.SchematicPath, run.CellDir, run.CheckpointTaken);
        }
        else
        {
            Console.Error.WriteLine($"[circuitRF] recognising {clay}");
            (result, circuit) = ArtworkRecognition.Circuit(input, emit, RunHost.Control);
            if (circuit is null) return JsonRun.Fail(CliDiagnostics.RecognizeRefused(result.Refusal ?? "The artwork could not be read."));
        }

        // ── the other two writes ────────────────────────────────────────────────────────────────────
        string? netlist = null, partsOut = null;
        if (o.Output is { } output)
        {
            netlist = Path.GetFullPath(output);
            if (Write(netlist, () => File.WriteAllText(netlist, circuit!.CnlText(Path.GetDirectoryName(netlist)!))) is { } failed)
                return failed;
        }
        if (o.PartsOut is { } partsTarget)
        {
            partsOut = Path.GetFullPath(partsTarget);
            if (Write(partsOut, () => PartsTableCsv.WriteFile(partsOut, result.Parts)) is { } failed) return failed;
        }

        // ── the report: stdout is the result ────────────────────────────────────────────────────────
        var text = new StringBuilder();
        foreach (string line in result.Report.Lines()) text.Append(line).Append('\n');
        text.Append('\n').Append(PartsTableCsv.Write(result.Parts));
        if (schematic is not null) { text.Append('\n').Append("schematic: ").Append(schematic).Append('\n'); JsonRun.AddOutput("schematic", schematic); }
        if (netlist is not null)   { text.Append("netlist: ").Append(netlist).Append('\n');   JsonRun.AddOutput("netlist", netlist); }
        if (partsOut is not null)  { text.Append("parts: ").Append(partsOut).Append('\n');    JsonRun.AddOutput("parts", partsOut); }
        Console.Write(text.ToString());

        JsonRun.Recognize = new RecognizeReportJson(
            clay, input.TechnologyPath, o.Region ?? "whole", circuit!.TestBench.Instances.Count,
            [.. result.Report.Findings.Select(f => new RecognizeFindingJson(
                f.Class.ToString(), f.Count, f.Sentence,
                [.. f.Anchors.Select(a => new RecognizeAnchorJson(a.X, a.Y, a.Layer is { } l ? $"{l.Layer}/{l.Datatype}" : null))]))],
            [.. result.Parts.Rows.OrderBy(r => r.Refdes, PartsTable.NaturalOrder).Select(r =>
                (IReadOnlyDictionary<string, string>)PartsTableCsv.Columns.ToDictionary(c => c, c => PartsTableCsv.Cell(result.Parts, r, c)))],
            schematic, cell, netlist, partsOut, checkpoint);
        return 0;
    }

    /// <summary>Where <c>--into</c> writes. The rules are <see cref="RecognitionTarget"/>'s; what is here is only the
    /// one question the GUI asks and a build machine cannot be asked — replace? — answered by <c>--replace</c>.</summary>
    private static (RecognitionTarget? Target, int? Refusal) Target(Options o, string into, string clay)
    {
        if (into == "artwork")
        {
            if (RecognitionTarget.ArtworkCellOffered(clay)) return (RecognitionTarget.ArtworkCell, null);
            if (RecognitionTarget.CellOf(clay) is { } artworkCell)
            {
                if (!RecognitionTarget.IsReplaceable(artworkCell))
                    return (null, JsonRun.Fail(CliDiagnostics.RecognizeArtworkHasSchematic(Path.GetFileName(artworkCell))));
                return o.Replace
                    ? (RecognitionTarget.Replace(artworkCell), null)
                    : (null, JsonRun.Fail(CliDiagnostics.RecognizeReplaceRequired(Path.GetFileName(artworkCell))));
            }
            return (RecognitionTarget.ArtworkCell, null);   // a loose layout: the run's own refusal says so
        }

        string name = into[4..];
        string cellDir = RecognitionTarget.NewCellDir(clay, name);
        if (!Directory.Exists(cellDir)) return (RecognitionTarget.NewCell(name), null);
        if (RecognitionTarget.IsReplaceable(cellDir))
            return o.Replace
                ? (RecognitionTarget.Replace(cellDir), null)
                : (null, JsonRun.Fail(CliDiagnostics.RecognizeReplaceRequired(name)));
        // A hand-drawn schematic, or a folder that is not a recognised cell: the run refuses it whatever the flags.
        return (o.Replace ? RecognitionTarget.Replace(cellDir) : RecognitionTarget.NewCell(name), null);
    }

    /// <summary>The S-parameter sweep: null (the run's own default — the EM setup's, else D15's) unless a flag
    /// changes it; a flag changes only its own field.</summary>
    private static FrequencySpec? Sweep(Options o, RecognitionInput input)
    {
        if (o.Start is null && o.Stop is null && o.Npts is null) return null;
        var basis = input.EmSetup?.Frequency ?? RecognitionEmitOptions.DefaultSweep;
        var (startText, startUnit) = o.Start is { } s ? (s.Text, s.Unit) : (basis.StartExpr, basis.StartUnit);
        var (stopText, stopUnit)   = o.Stop  is { } e ? (e.Text, e.Unit) : (basis.StopExpr, basis.StopUnit);
        return new FrequencySpec(startText, stopText, o.Npts ?? basis.NumPoints ?? RecognitionEmitOptions.DefaultSweep.NumPoints!.Value,
                                 basis.Kind, startUnit, stopUnit);
    }

    /// <summary><paramref name="count"/> comma-separated lengths, each with its unit — <c>render --window</c>'s
    /// spelling and its bare-number refusal — in the layout's DBU.</summary>
    private static (long[]? Values, int? Refusal) Coordinates(string text, string option, int count, int dbuPerMicron)
    {
        string expected = count == 4 ? "x0,y0,x1,y1, each with a unit" : "x,y, each with a unit";
        var parts = text.Split(',');
        if (parts.Length != count) return (null, JsonRun.Fail(CliDiagnostics.RecognizeBadValue(option, text, expected)));
        var values = new long[count];
        for (int i = 0; i < count; i++)
        {
            string p = parts[i].Trim();
            if (p.Length == 0 || !char.IsLetter(p[^1]))
                return (null, JsonRun.Fail(CliDiagnostics.RecognizeCoordinateNeedsUnit(option, p)));
            if (!LayoutUnits.TryParse(p, LayoutUnit.Um, dbuPerMicron, out values[i]))
                return (null, JsonRun.Fail(CliDiagnostics.RecognizeBadValue(option, text, expected)));
        }
        return (values, null);
    }

    private static int? Write(string path, Action write)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            write();
            Console.Error.WriteLine($"[circuitRF] Wrote {path}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return JsonRun.Fail(CliDiagnostics.RecognizeOutputFailed(path, ex.Message));
        }
    }
}
