// `circuitrf convert` — every interchange format to every other one, headlessly.
//
// WHY IT IS ONE VERB AND NOT FIVE. Every reader in src/Design/Layout/Interchange lands on the same
// neutral thing (a cell folder plus a technology) and every writer starts from it, so the N x N table
// of conversions is not N x N pieces of code: it is one import step, one export step, and a rule for
// what sits between them. DXF -> Gerber is GDSII -> board is `.clay` -> DXF; only the two ends differ.
//
// THE INTERMEDIATE IS A REAL CELL, NOT A BUFFER. An import writes cell folders — that is what the
// readers do, and re-implementing them against an in-memory model would be a second copy of the
// importer with its own bugs. So a conversion whose TARGET is `.clay` simply stops after the import,
// and every other conversion runs the import into a scratch directory it deletes afterwards
// (--keep-cells keeps it, which is the way to see what a conversion actually understood).
//
// WHAT THIS FILE REFUSES TO DO. It never guesses at a drill file's coordinate format and it never
// silently resolves an ambiguous layer mapping differently from the GUI. The GUI answers both with a
// dialog; headless, the first is a refusal that names the flag that answers it, and the second takes
// the same default the dialog pre-selects (add the layer to the technology) and says so.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Interchange.Gdstk;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Cli;

public static class LayoutConvert
{
    /// <summary>
    /// The five interchange formats. <c>internal</c> rather than private because <c>check</c> and
    /// <c>explain</c> classify a path the same way this verb does (R-aut4-11) and must not grow a
    /// second rule for it — an interchange file they could not name would read as a file circuitRF
    /// does not handle, when in fact `convert` handles it.
    /// </summary>
    internal enum Fmt { Clay, Gdsii, Dxf, Gerber, Board, Step, Gltf }

    private sealed class Options
    {
        public string? Input, Output, TechPath, Cws, KeepCells, Cell, Name;
        public Fmt? From, To;
        public bool ListCells;
        public int DbuPerMicron = 1000;

        // brief-oasis-gdstk.md §7d — which GDSII reader or writer a gdsii source or target goes through.
        // Null: not given, which is circuitRF's own (D5).
        public StreamRoute? Engine;

        /// <summary>The route a GDSII end of this conversion takes.</summary>
        public StreamRoute GdsiiRoute => Engine ?? StreamRoute.Gdsii;

        // DXF
        public DxfAcadVersion AcadVersion = DxfAcadVersion.R2018;
        public int? InsUnits;

        // Excellon
        public GerberUnit? DrillUnit;
        public GerberZeroOmission? DrillZeros;
        public int? DrillIntegerDigits, DrillDecimalDigits;
        public bool AcceptInferredDrillFormat;

        // GI4 R-gi4-10. A dialog's question becomes a flag, never a guess.
        public bool OpenArchives;

        // R-rf3-7. Coalescing raster fill is ON by default — the region is what every consumer of an
        // imported board wants, and the strokes are neither editable copper nor meshable. The flag
        // exists for the case the default cannot serve: comparing against the CAM source, or chasing
        // an import bug, where the primitives have to arrive exactly as authored.
        public bool NoCoalesce;

        // brief-em3d-68 R-em3d68-7b — STEP: what the Import STEP dialog would have asked.
        public List<(string Part, string Material)> StepMaterials = [];
        public List<string> StepParts = [];

        // brief-em3d-69 R-em3d69-5b — STEP export: the Export STEP dialog's options, one flag each.
        public bool StepAssembly, StepAsDrawn, StepThicken, StepAirBox, StepExportFlags;
        public StepExportSchema StepSchema = StepExportSchema.Ap214;
        public StepExportView StepView = StepExportView.Auto;

        // brief-em3d-111 R-em3d111-3c — glTF export: the Export glTF dialog's options, one flag each.
        public bool GltfAssembly, GltfFlags;
        public string? GltfField, GltfRegion;
    }

    public static int Run(string[] args)
    {
        var o = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o" or "--output" when i + 1 < args.Length: o.Output = args[++i]; break;
                case "--from" when i + 1 < args.Length:
                    if (ParseFormat(args[++i]) is not { } f) return BadFormat(args[i]);
                    o.From = f; break;
                case "--to" when i + 1 < args.Length:
                    if (ParseFormat(args[++i]) is not { } t) return BadFormat(args[i]);
                    o.To = t; break;
                case "--cell" when i + 1 < args.Length: o.Cell = args[++i]; break;
                case "--name" when i + 1 < args.Length: o.Name = args[++i]; break;
                case "--list-cells": o.ListCells = true; break;
                case "--tech" when i + 1 < args.Length: o.TechPath = args[++i]; break;
                case "--workspace" when i + 1 < args.Length: o.Cws = args[++i]; break;
                case "--keep-cells" when i + 1 < args.Length: o.KeepCells = args[++i]; break;
                case "--engine" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "native": o.Engine = StreamRoute.Gdsii; break;
                        case "gdstk": o.Engine = StreamRoute.GdsiiGdstk; break;
                        default: return JsonRun.Fail(CliDiagnostics.ConvertUnknownEngine(args[i]));
                    }
                    break;
                case "--dbu" when i + 1 < args.Length && int.TryParse(args[i + 1], out int dbu):
                    o.DbuPerMicron = Math.Max(1, dbu); i++; break;

                case "--dxf-version" when i + 1 < args.Length:
                    switch (args[++i].ToUpperInvariant())
                    {
                        case "AC1015" or "R2000": o.AcadVersion = DxfAcadVersion.R2000; break;
                        case "AC1018" or "R2004": o.AcadVersion = DxfAcadVersion.R2004; break;
                        case "AC1032" or "R2018": o.AcadVersion = DxfAcadVersion.R2018; break;
                        default:
                            return JsonRun.Fail(CliDiagnostics.ConvertUnknownDxfVersion(args[i]));
                    }
                    break;
                case "--dxf-units" when i + 1 < args.Length && int.TryParse(args[i + 1], out int iu):
                    o.InsUnits = iu; i++; break;

                case "--drill-units" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "mm" or "metric" or "millimetres" or "millimeters": o.DrillUnit = GerberUnit.Millimetres; break;
                        case "in" or "inch" or "inches": o.DrillUnit = GerberUnit.Inches; break;
                        default: return JsonRun.Fail(CliDiagnostics.ConvertUnknownDrillUnit(args[i]));
                    }
                    break;
                case "--drill-zeros" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "leading": o.DrillZeros = GerberZeroOmission.Leading; break;
                        case "trailing": o.DrillZeros = GerberZeroOmission.Trailing; break;
                        default: return JsonRun.Fail(CliDiagnostics.ConvertUnknownDrillZeros(args[i]));
                    }
                    break;
                case "--drill-format" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split(':');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out int id) || !int.TryParse(parts[1], out int dd))
                        return JsonRun.Fail(CliDiagnostics.ConvertBadDrillFormat(args[i]));
                    o.DrillIntegerDigits = id; o.DrillDecimalDigits = dd; break;
                }
                case "--accept-inferred-drill-format": o.AcceptInferredDrillFormat = true; break;
                case "--open-archives": o.OpenArchives = true; break;
                case "--no-coalesce": o.NoCoalesce = true; break;
                case "--material" when i + 1 < args.Length:
                {
                    string m = args[++i];
                    int eq = m.IndexOf('=');
                    if (eq <= 0 || eq == m.Length - 1) return JsonRun.Fail(CliDiagnostics.ConvertBadMaterialMapping(m));
                    o.StepMaterials.Add((m[..eq], m[(eq + 1)..]));
                    break;
                }
                case "--part" when i + 1 < args.Length: o.StepParts.Add(args[++i]); break;
                case "--assembly": o.StepAssembly = o.StepExportFlags = true; break;
                case "--as-drawn": o.StepAsDrawn = o.StepExportFlags = true; break;
                case "--thicken-sheets": o.StepThicken = o.StepExportFlags = true; break;
                case "--include-airbox": o.StepAirBox = o.StepExportFlags = true; break;
                case "--gltf-assembly": o.GltfAssembly = o.GltfFlags = true; break;
                case "--gltf-field" when i + 1 < args.Length: o.GltfField = args[++i]; o.GltfFlags = true; break;
                case "--region" when i + 1 < args.Length: o.GltfRegion = args[++i]; o.GltfFlags = true; break;
                case "--schema" when i + 1 < args.Length:
                    o.StepExportFlags = true;
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "ap214": o.StepSchema = StepExportSchema.Ap214; break;
                        case "ap242": o.StepSchema = StepExportSchema.Ap242; break;
                        default: return JsonRun.Fail(CliDiagnostics.ConvertUnknownSchema(args[i]));
                    }
                    break;
                case "--view" when i + 1 < args.Length:
                    o.StepExportFlags = true;
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "3d": o.StepView = StepExportView.ThreeD; break;
                        case "layout": o.StepView = StepExportView.Layout; break;
                        default: return JsonRun.Fail(CliDiagnostics.ConvertUnknownView(args[i]));
                    }
                    break;

                default:
                    if (a.StartsWith('-')) { JsonRun.Report(CliDiagnostics.ConvertUnknownOption(a)); return Usage(); }
                    if (o.Input is not null) { JsonRun.Report(CliDiagnostics.ConvertMultipleInputs()); return Usage(); }
                    o.Input = a;
                    break;
            }
        }

        if (o.Input is null) return Usage();
        JsonRun.InputPath = o.Input;
        if (!File.Exists(o.Input) && !Directory.Exists(o.Input))
            return JsonRun.Fail(CliDiagnostics.ConvertInputNotFound(o.Input));

        // brief-oasis-gdstk.md §7d — --engine picks a GDSII reader or writer, so it needs a GDSII end, and
        // the gdstk one needs the worker. Both are refused here, before anything is read or written.
        if (o.Engine is { } engine)
        {
            bool gdsiiEnd = (o.From ?? DetectSource(o.Input)) == Fmt.Gdsii
                            || (o.Output is not null && (o.To ?? DetectTarget(o.Output)) == Fmt.Gdsii);
            if (!gdsiiEnd) return JsonRun.Fail(CliDiagnostics.ConvertEngineNeedsGdsii());
            if (engine.Unavailable() is { } missing) return JsonRun.Fail(CliDiagnostics.ConvertEngineUnavailable(missing));
        }

        // The source format is inferable from what the path IS — a folder is a Gerber file set, and a
        // file with no telling extension is classified by CONTENT through the same classifier the
        // Gerber import itself uses, so `convert` and the import can never disagree about what a file
        // is. --from overrides all of it.
        // brief-em3d-111 R-em3d111-3c — glTF is written, never read; a glTF target is a 3D view exported by GltfExport.
        if (o.From == Fmt.Gltf || (o.From is null && IsGltf(o.Input))) return JsonRun.Fail(CliDiagnostics.ConvertGltfImport(o.Input));
        bool gltfTarget = o.To == Fmt.Gltf || (o.To is null && o.Output is not null && IsGltf(o.Output));
        if (gltfTarget) return ExportGltf(o);
        if (o.GltfFlags) return JsonRun.Fail(CliDiagnostics.ConvertGltfFlagsWithoutGltf());

        bool stepSource = o.From == Fmt.Step || (o.From is null && DetectSource(o.Input) == Fmt.Step);
        if (stepSource)
        {
            if (o.StepExportFlags) return JsonRun.Fail(CliDiagnostics.ConvertStepExportFlagsWithoutStep());
            return ImportStep(o);
        }
        if (o.StepMaterials.Count > 0 || o.StepParts.Count > 0) return JsonRun.Fail(CliDiagnostics.ConvertStepFlagsWithoutStep());

        // brief-em3d-69 R-em3d69-5a — a STEP TARGET: a .c3d, a .clay or a cell folder exports straight through StepExport;
        // an interchange source is imported into a scratch cell first, exactly as every other target does.
        bool stepTarget = o.To == Fmt.Step || (o.To is null && o.Output is not null && DetectTarget(o.Output) == Fmt.Step);
        if (stepTarget) return ExportStep(o);
        if (o.StepExportFlags) return JsonRun.Fail(CliDiagnostics.ConvertStepExportFlagsWithoutStep());

        Fmt from = o.From ?? DetectSource(o.Input) ?? Fmt.Clay;
        if (o.From is null && DetectSource(o.Input) is null)
            return JsonRun.Fail(CliDiagnostics.ConvertSourceUnrecognised(Path.GetFileName(o.Input)));

        if (o.ListCells) return ListCells(o, from);

        if (o.Output is null)
        {
            JsonRun.Report(CliDiagnostics.ConvertOutputRequired());
            return Usage();
        }

        Fmt? to = o.To ?? DetectTarget(o.Output);
        if (to is null)
            return JsonRun.Fail(CliDiagnostics.ConvertTargetUnrecognised(o.Output));

        if (from == Fmt.Clay && to == Fmt.Clay)
            return JsonRun.Fail(CliDiagnostics.ConvertClayToClay());

        // R-aut12-4. A clay target is a DIRECTORY — the import writes one cell folder per structure
        // and a technology beside them, which is why `--to clay` simply stops after the import. A
        // file-shaped path used to become a DIRECTORY of that name with the real `.clay` two levels
        // inside it, and the extension is also how a caller SAYS clay, so the inference stays and the
        // shape is refused with the command that answers it. Refusing beats collapsing to one file:
        // an import that produced a hierarchy has no single file to collapse to, and a rule that
        // works only for the flat case is a rule that fails silently on the other one.
        if (to == Fmt.Clay && ClayDirectoryRefusal(o.Output!) is { } clayRefusal)
            return clayRefusal;

        Console.Error.WriteLine($"[circuitRF] {RouteName(o, from)} -> {RouteName(o, to.Value)}");

        string? scratch = null;
        try
        {
            var src = LoadSource(o, from, to.Value, ref scratch);
            if (src is null) return 1;

            // R-gc2-2/3: exporting a `.clay` exports the parts it places — rebuilt into the
            // workspace's folder where missing, or refused. An importer produces no generated cells.
            int? cellRefusal = null;
            using var generated = from == Fmt.Clay
                ? GeneratedCells.Prepare(src.View, Path.GetFullPath(o.Input!), mayWrite: true, out cellRefusal)
                : null;
            if (cellRefusal is { } refused) return refused;

            // The minted technology, when it is somewhere the caller will still find it. It is not
            // optional context: the cells the import produced reference it by relative path, so a
            // caller that took the cells and not this has a design whose layers resolve to nothing.
            // `em` reports BOTH its files for the same reason (cli.md §8.2).
            if (scratch is null && _mintedTechPath is { } minted)
                JsonRun.AddOutput("ctech", minted);

            // --to clay: the import IS the conversion. The cells and the technology are already on
            // disk, in the folder the user named, and there is nothing left to write.
            if (to == Fmt.Clay)
            {
                Console.Error.WriteLine($"[circuitRF] wrote {src.CreatedCellDirs.Count} cell(s) and a technology to {o.Output}");
                foreach (var d in src.CreatedCellDirs) { Console.WriteLine(d); JsonRun.AddOutput("cell", d); }
                return 0;
            }

            return Export(o, to.Value, src);
        }
        catch (OperationCanceledException)
        {
            // Only the gdstk route reads the token; its worker is killed and nothing is written or created.
            Console.Error.WriteLine("[circuitRF] cancelled; nothing was written");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            JsonRun.Note(CliDiagnostics.ConvertFailed(ex.Message));
            return 1;
        }
        finally
        {
            if (scratch is not null && Directory.Exists(scratch))
                try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// R-aut12-4: whether the <c>--to clay</c> output path names a DIRECTORY, which is the only shape
    /// this target has. An existing directory passes whatever it is called — a caller with a folder
    /// called <c>cells.clay</c> already has the thing that would be written into.
    /// </summary>
    /// <summary>
    /// brief-em3d-68 R-em3d68-7 — <c>.step</c> → a NEW <c>.c3d</c>. Argument checks, refusals and reporting only: every
    /// decision is <see cref="StepImport.Import"/>'s, the function the Import STEP dialog calls (a source scan holds it).
    /// </summary>
    private static int ImportStep(Options o)
    {
        if (o.ListCells) return JsonRun.Fail(CliDiagnostics.ConvertStepListCells());
        if (o.Output is null)
        {
            JsonRun.Report(CliDiagnostics.ConvertOutputRequired());
            return Usage();
        }
        if (!string.Equals(Path.GetExtension(o.Output), C3dPersistence.Extension, StringComparison.OrdinalIgnoreCase)
            || o.To is not null)
            return JsonRun.Fail(CliDiagnostics.ConvertStepTarget(o.Output));

        var kernel = GeometryKernel.Shared;
        if (!kernel.Capability.Available)
            return JsonRun.Fail(CliDiagnostics.ConvertFailed(GeometryKernel.NeedsKernel("Import STEP", kernel.Capability)));

        Console.Error.WriteLine("[circuitRF] STEP -> 3d");
        var options = new StepImportOptions { Materials = o.StepMaterials, Parts = o.StepParts, TechPath = o.TechPath };
        StepImportResult result;
        try
        {
            result = StepImport.Import(o.Input!, o.Output, options, kernel, control: RunHost.Control);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[circuitRF] cancelled; nothing was written");
            return 130;
        }
        catch (StepImportException e)
        {
            return JsonRun.Fail(e.Diagnostic);
        }
        catch (GeometryKernelException e)
        {
            return JsonRun.Fail(CliDiagnostics.ConvertFailed(e.Message));
        }
        Report(result.Notes);
        Console.Error.WriteLine($"[circuitRF] wrote {result.Objects.Count} Step object(s) to {o.Output}" +
                                (result.Created ? $" and copied the file to {result.CopiedPath}" : $"; {result.CopiedPath} was already there"));
        string c3d = Path.GetFullPath(o.Output);
        Console.WriteLine(c3d);
        JsonRun.AddOutput("3d", c3d);
        JsonRun.AddOutput("step", result.CopiedPath);
        return 0;
    }

    /// <summary>
    /// brief-em3d-69 R-em3d69-5 — <c>.c3d</c> / <c>.clay</c> / cell folder / any interchange source → one STEP file. Argument
    /// checks, the interchange import and reporting only: what is in the file is <see cref="StepExport.Export"/>'s, the
    /// function the Export STEP dialog calls (a source scan holds it).
    /// </summary>
    private static int ExportStep(Options o)
    {
        if (o.ListCells) return JsonRun.Fail(CliDiagnostics.ConvertListCellsNotApplicable());
        var kernel = GeometryKernel.Shared;
        if (!kernel.Capability.Available)
            return JsonRun.Fail(CliDiagnostics.ConvertFailed(GeometryKernel.NeedsKernel("Export STEP", kernel.Capability)));

        string input = Path.GetFullPath(o.Input!);
        bool direct = Path.GetExtension(input).ToLowerInvariant() is ".c3d" or ".clay"
                      || (o.From is null && Directory.Exists(input) && DocumentKinds.LooksLikeCellFolder(input));
        var options = new StepExportOptions
        {
            Assembly = o.StepAssembly, AsDrawn = o.StepAsDrawn, ThickenSheets = o.StepThicken, IncludeAirBox = o.StepAirBox,
            Schema = o.StepSchema, View = o.StepView, TechPath = direct ? o.TechPath : null,
            WorkspaceCws = o.Cws is { } w ? Path.GetFullPath(w) : null,
        };

        string? scratch = null;
        try
        {
            string source = input;
            if (!direct)
            {
                // Every other source imports into a scratch cell, as every other target does; the layout it lands on is the
                // source, and its minted technology is what that layout references.
                Fmt from = o.From ?? DetectSource(input) ?? Fmt.Clay;
                if (o.From is null && DetectSource(input) is null)
                    return JsonRun.Fail(CliDiagnostics.ConvertSourceUnrecognised(Path.GetFileName(input)));
                Console.Error.WriteLine($"[circuitRF] {Name(from)} -> STEP");
                var src = LoadSource(o, from, Fmt.Step, ref scratch);
                if (src is null) return 1;
                var primary = CellFolder.ResolvePrimary(src.CellDir, ViewType.Layout);
                if (primary.ResolvedName is not { } file) { JsonRun.Report(CliDiagnostics.ConvertCellHasNoLayout(Path.GetFileName(src.CellDir))); return 1; }
                source = Path.Combine(CellFolder.SubFolderPath(src.CellDir, ViewType.Layout), file);
            }
            else Console.Error.WriteLine("[circuitRF] -> STEP");

            var result = StepExport.Export(source, o.Output!, options, kernel, RunHost.Control);
            Report(result.Notes);
            Console.Error.WriteLine($"[circuitRF] {result.Plan.Summary}");
            Console.WriteLine(result.Path);
            JsonRun.AddOutput("step", result.Path);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[circuitRF] cancelled; nothing was written");
            return 130;
        }
        catch (StepExportException e)
        {
            return JsonRun.Fail(e.Diagnostic);
        }
        catch (GeometryKernelException e)
        {
            return JsonRun.Fail(CliDiagnostics.ConvertFailed(e.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return JsonRun.Fail(CliDiagnostics.ConvertFailed(ex.Message));
        }
        finally
        {
            if (scratch is not null && Directory.Exists(scratch))
                try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// brief-em3d-111 R-em3d111-3c — <c>.c3d</c> → one <c>.glb</c>. Argument checks and reporting only: what is in the file is
    /// <see cref="CircuitRF.Render.Scene3D.Export.GltfExport"/>'s, the function the Export glTF dialog calls (GltfConvert).
    /// </summary>
    private static int ExportGltf(Options o)
    {
        if (o.ListCells) return JsonRun.Fail(CliDiagnostics.ConvertListCellsNotApplicable());
        if (o.StepExportFlags) return JsonRun.Fail(CliDiagnostics.ConvertStepExportFlagsWithoutStep());
        if (o.Output is null)
        {
            JsonRun.Report(CliDiagnostics.ConvertOutputRequired());
            return Usage();
        }
        if (!CircuitRF.Render.Scene3D.Export.GltfExport.IsGltfExtension(o.Output)) return JsonRun.Fail(CliDiagnostics.ConvertGltfBinaryOnly(o.Output));
        string input = Path.GetFullPath(o.Input!);
        var kind = DocumentKinds.Classify(input);
        if (kind != DocumentKind.ThreeD) return JsonRun.Fail(CliDiagnostics.ConvertGltfSource(o.Input!, DocumentKinds.Name(kind)));
        return GltfConvert.Export(input, o.Output, o.GltfAssembly, o.GltfField, o.GltfRegion);
    }

    /// <summary>A glTF path, binary or not (<c>.gltf</c> is refused as a target by name, D1).</summary>
    private static bool IsGltf(string path) => Path.GetExtension(path).ToLowerInvariant() is ".glb" or ".gltf";

    private static int? ClayDirectoryRefusal(string output)
    {
        if (Directory.Exists(output)) return null;
        if (File.Exists(output)) return JsonRun.Fail(CliDiagnostics.ConvertClayTargetIsAFile(output));
        if (Path.GetExtension(output).Length == 0) return null;

        // The same path with the extension taken off — a directory beside where the caller meant,
        // named the way they were already naming it, so the refusal can be acted on by editing one
        // token rather than by rethinking the command.
        string suggestion = Path.Combine(
            Path.GetDirectoryName(output) is { Length: > 0 } d ? d : "",
            Path.GetFileNameWithoutExtension(output));
        return JsonRun.Fail(CliDiagnostics.ConvertClayTargetIsADirectory(
            output, suggestion.Length > 0 ? suggestion : "cells"));
    }

    // ── What a source resolved to ─────────────────────────────────────────────────────────────────

    private sealed record Source(
        string CellDir,
        LayoutView View,
        Technology? Tech,
        int DbuPerMicron,
        string CellName,
        IReadOnlyList<string> CreatedCellDirs);

    private static Source? LoadSource(Options o, Fmt from, Fmt to, ref string? scratch)
    {
        if (from == Fmt.Clay) return LoadClay(o);

        // Everything else imports. The cells land where the user asked when clay is the target, and
        // in a scratch directory otherwise — one that is deleted on the way out unless --keep-cells
        // named somewhere to leave it.
        string staging;
        if (to == Fmt.Clay) staging = Path.GetFullPath(o.Output!);
        else if (o.KeepCells is { } keep) staging = Path.GetFullPath(keep);
        else
        {
            staging = Path.Combine(Path.GetTempPath(), "circuitrf-convert-" + Guid.NewGuid().ToString("N")[..12]);
            scratch = staging;
        }
        Directory.CreateDirectory(staging);

        Technology? destTech = null;
        if (o.TechPath is { } tp)
        {
            try { destTech = TechPersistence.LoadFromFile(tp); }
            catch (Exception ex)
            {
                JsonRun.Report(CliDiagnostics.ConvertTechnologyUnreadable(tp, ex.Message));
                return null;
            }
        }

        string name = ImportName(o.Input!);

        // An EMPTY technology, never null, when the user named none. Every importer reconciles the
        // file's layers against the destination technology and reports the ones it would ADD; handed a
        // null destination it has nothing to compare against and adds nothing, so the layers arrive
        // with numeric keys and no names — and a re-export then names its Gerber files from a
        // synthetic suffix instead of the technology's own. An empty technology makes every source
        // layer an unmatched row, which is exactly what it is.
        destTech ??= new Technology { Name = name };

        return from switch
        {
            Fmt.Gdsii => ImportGdsii(o, staging, destTech, name),
            Fmt.Dxf   => ImportDxf(o, staging, destTech, name),
            Fmt.Board => ImportBoard(o, staging, destTech, name),
            Fmt.Gerber => ImportGerber(o, staging, destTech),
            _ => null,
        };
    }

    private static Source? LoadClay(Options o)
    {
        string clay = Path.GetFullPath(o.Input!);
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay); }
        catch (Exception ex)
        {
            JsonRun.Report(CliDiagnostics.ConvertLayoutUnreadable(clay, ex.Message));
            return null;
        }

        // <cell>/layout/<name>.clay — the cell folder is two levels up, and every export walks the
        // hierarchy from it rather than from the file, because instances are cell references.
        string layoutDir = Path.GetDirectoryName(clay)!;
        string cellDir = Path.GetDirectoryName(layoutDir)!;

        Technology? tech;
        if (o.TechPath is { } tp)
        {
            try { tech = TechPersistence.LoadFromFile(tp); }
            catch (Exception ex) { JsonRun.Report(CliDiagnostics.ConvertTechnologyUnreadable(tp, ex.Message)); return null; }
        }
        else
        {
            // The same walk-up the GUI does and the `em` verb does: the .clay's own TechRef first,
            // then the nearest ancestor workspace's default. --workspace overrides the walk.
            string? cws = o.Cws is { } w ? Path.GetFullPath(w) : null;
            var (res, own) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, cws, new TechnologyCache());
            foreach (var d in res.Diagnostics)
            { Console.Error.WriteLine($"warning: {d}"); JsonRun.Note(CliDiagnostics.EmSetupWarning(d)); }
            if (res.ResolvedPath is { } rp) Console.Error.WriteLine($"[circuitRF] technology: {rp}");
            else Console.Error.WriteLine(
                "[circuitRF] no technology resolved — layer names, Gerber suffixes and the stackup " +
                (own is null ? "are unavailable (no workspace above this layout)." : "are unavailable."));
            tech = res.Tech;
        }

        return new Source(cellDir, view, tech, view.DbuPerMicron,
            Path.GetFileNameWithoutExtension(clay), [cellDir]);
    }

    // ── The four importers, each reduced to the same Source ───────────────────────────────────────

    private static Source? ImportGdsii(Options o, string staging, Technology? destTech, string name)
    {
        GdsiiImport.ImportResult r;
        try
        {
            r = StreamInterchange.Import(o.GdsiiRoute, o.Input!, staging, destTech, o.DbuPerMicron,
                preferSourceResolution: true, token: RunHost.Cancellation);
        }
        catch (GdstkException ex)
        {
            // The worker's own sentence: it names the file, and a crash, hang or refusal created nothing.
            JsonRun.Report(ex.Diagnostic);
            return null;
        }
        Report(r.Messages);
        if (r.Cancelled) return Refused();

        string? cellDir = PickCell(o.Cell, r.CreatedCellDirs,
            r.TopLevelCellDirs.Count > 0 ? r.TopLevelCellDirs[0] : null, "GDSII structure");
        if (cellDir is null) return null;

        var tech = MintTechnology(staging, name, destTech, r.LayersToAdd, stackup: null, r.CreatedCellDirs);
        return Finish(cellDir, tech, r.CreatedCellDirs);
    }

    private static Source? ImportDxf(Options o, string staging, Technology? destTech, string name)
    {
        using var stream = File.OpenRead(o.Input!);
        var r = DxfImport.Import(stream, staging, destTech, o.DbuPerMicron,
            resolveUnits: o.InsUnits is { } iu ? _ => iu : null);
        Report(r.Messages);
        if (r.Cancelled) return Refused();

        // Model space is the drawing itself; a BLOCK is a definition something else places. The
        // drawing is what a conversion means unless --cell says otherwise.
        string? top = r.CellNameByBlockName.TryGetValue(DxfReader.ModelSpaceName, out var modelCell)
            ? r.CreatedCellDirs.FirstOrDefault(d => string.Equals(Path.GetFileName(d), modelCell, StringComparison.OrdinalIgnoreCase))
            : null;

        string? cellDir = PickCell(o.Cell, r.CreatedCellDirs, top, "DXF block");
        if (cellDir is null) return null;

        var tech = MintTechnology(staging, name, destTech, r.LayersToAdd, stackup: null, r.CreatedCellDirs);
        var src = Finish(cellDir, tech, r.CreatedCellDirs);

        // "$MODEL" is DxfReader's own name for the drawing itself, not something anyone typed, and a
        // Gerber set called $MODEL.gbr is a file set nobody wants. The drawing's real name is the
        // file's.
        return src is not null && cellDir == top
            ? src with { CellName = Path.GetFileNameWithoutExtension(o.Input!) }
            : src;
    }

    private static Source? ImportBoard(Options o, string staging, Technology? destTech, string name)
    {
        using var stream = File.OpenRead(o.Input!);
        var r = PcbImport.Import(stream, staging, name, destTech, o.DbuPerMicron,
            coalesceRasterFill: !o.NoCoalesce);
        Report(r.Messages);
        if (r.Cancelled) return Refused();

        string? cellDir = PickCell(o.Cell, r.CreatedCellDirs, r.BoardCellDir, "footprint");
        if (cellDir is null) return null;

        var tech = MintTechnology(staging, name, destTech, r.LayersToAdd, r.Stackup, r.CreatedCellDirs, r.ViaEntries);
        return Finish(cellDir, tech, r.CreatedCellDirs);
    }

    private static Source? ImportGerber(Options o, string staging, Technology? destTech)
    {
        // A folder is the whole set. A single file is one layer — and unlike the GUI, which asks
        // whether the folder was meant, a command line already said which it meant by what it typed.
        var files = Directory.Exists(o.Input!)
            ? GerberImportEntry.FilesIn(Path.GetFullPath(o.Input!))
            : [Path.GetFullPath(o.Input!)];

        string importName = Directory.Exists(o.Input!)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(o.Input!)))
            : Path.GetFileNameWithoutExtension(o.Input!);

        var r = GerberImport.Import(files, staging, importName, destTech, o.DbuPerMicron,
            resolveDrillFormat: (fileName, inferred, crossCheck, _) => ResolveDrillFormat(o, fileName, inferred, crossCheck),
            offerArchive: archives => OfferArchive(o, archives),
            coalesceRasterFill: !o.NoCoalesce);
        Report(r.Messages);
        if (r.Cancelled) return Refused();
        if (r.CellDir is null) { JsonRun.Report(CliDiagnostics.ConvertNoCell()); return null; }

        // Gerber import mints its own .ctech and points the .clay at it (R-L4g-8), so there is nothing
        // for MintTechnology to do here — this is the one importer that already did it.
        return Finish(r.CellDir, r.Technology, r.CreatedCellDirs);
    }

    /// <summary>
    /// The drill-format prompt, answered by flags. R-L4h-6's dialog exists because a drill file read at
    /// the wrong scale is the worst silent failure available here — leading vs trailing zero suppression
    /// differ by four orders of magnitude on identical text — so the headless answer is a REFUSAL that
    /// prints the inference and the evidence, not a guess. Returning null cancels the whole import.
    /// </summary>
    private static GerberImport.DrillFormatChoice? ResolveDrillFormat(
        Options o, string fileName, DrillFormatInference inferred, DrillExtentsCheck crossCheck)
    {
        var over = new DrillFormatOverride(o.DrillUnit, o.DrillIntegerDigits, o.DrillDecimalDigits, o.DrillZeros);
        bool anyOverride = o.DrillUnit is not null || o.DrillZeros is not null ||
                           o.DrillIntegerDigits is not null || o.DrillDecimalDigits is not null;

        // A flag is a statement about the RUN, not about one file, so it settles every drill file in
        // the set at once — which is also what stops the same refusal being printed once per file.
        if (anyOverride) return new GerberImport.DrillFormatChoice(over, ApplyToAll: true);
        if (o.AcceptInferredDrillFormat) return new GerberImport.DrillFormatChoice(null, ApplyToAll: true);

        Console.Error.WriteLine($"error: {fileName} does not state its coordinate format, and the inference had to guess.");
        JsonRun.Note(CliDiagnostics.ConvertDrillFormatUnstated(fileName, inferred.ToString() ?? ""));
        Console.Error.WriteLine($"       Inferred: {inferred}");
        foreach (var e in inferred.Evidence) Console.Error.WriteLine($"       {e}");
        if (!crossCheck.Agrees) Console.Error.WriteLine($"       {crossCheck.Report}");
        Console.Error.WriteLine("       Accept it with --accept-inferred-drill-format, or state it:");
        Console.Error.WriteLine("         --drill-units mm|inch  --drill-format <int>:<dec>  --drill-zeros leading|trailing");
        return null;
    }

    /// <summary>
    /// GI4 R-gi4-10's offer, answered by a flag. A folder whose only artwork is inside an archive is a
    /// dead end, and the GUI offers to look inside — so headless this is a REFUSAL NAMING THE FLAG
    /// THAT ANSWERS IT, exactly as an unstated drill coordinate format already is. A dialog's question
    /// becomes a flag, never a guess: unpacking somebody's archive because it happened to be sitting
    /// there is precisely the surprise R-L4g-3 forbids.
    /// </summary>
    private static bool OfferArchive(Options o, IReadOnlyList<string> archives)
    {
        if (o.OpenArchives) return true;

        string names = string.Join(", ", archives.Select(Path.GetFileName));
        Console.Error.WriteLine($"error: this folder holds no Gerber artwork of its own, only {names}.");
        JsonRun.Note(CliDiagnostics.ConvertArchiveNotOpened(names));
        Console.Error.WriteLine("       circuitRF does not unpack an archive unless asked. Look inside it with:");
        Console.Error.WriteLine("         --open-archives");
        return false;
    }

    // ── Between the two halves ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the technology an import DECLARED and points every cell it created at it.
    ///
    /// <para>The GUI grafts an import's layers onto the open workspace's technology, live; headless
    /// there is no workspace and no open technology, so the layers would otherwise be dropped on the
    /// floor and the export would name every layer by number. Writing a `.ctech` beside the cells is
    /// what Gerber import already does for itself (R-L4g-8), for the same reason, and it means an
    /// intermediate kept with --keep-cells opens in the application as a real design.</para>
    /// </summary>
    /// <param name="viaEntries">The import's <see cref="StackupKind.Via"/> entries, appended even when
    /// the whole stackup below was refused. Additive, unlike a stackup: a via entry declares a drill
    /// and cannot invalidate a substrate, so the rule protecting a declared stackup does not reach it.
    /// This is what makes an imported via's span survive the round trip out again.</param>
    /// <summary>The `.ctech` the last import minted, or null. See <see cref="MintTechnology"/> for
    /// why it is held rather than reported at the point it is written.</summary>
    private static string? _mintedTechPath;

    private static Technology MintTechnology(
        string staging, string name, Technology? destTech,
        IReadOnlyList<LayerDef> layersToAdd, Stackup? stackup, IReadOnlyList<string> cellDirs,
        IReadOnlyList<StackupLayer>? viaEntries = null)
    {
        var tech = destTech is null
            ? new Technology { Name = name }
            : TechPersistence.Clone(destTech);

        foreach (var def in layersToAdd)
            if (!tech.Layers.Any(l => l.Key == def.Key)) tech.Layers.Add(def);

        // Never replace a stackup that is already there — the same rule board import follows in the
        // GUI: what was recovered is reported, and nothing already declared is overwritten.
        if (stackup is not null && tech.Stackup.Layers.Count == 0) tech.Stackup = stackup;

        // Keyed on the DRAWING LAYER, which is what selects an entry (R-via-3) — the same dedup rule
        // WorkspaceViewModel.ApplyImportToTechnology applies, so the two paths mint the same file.
        foreach (var entry in viaEntries ?? [])
            if (!tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Via
                                              && l.DrawingLayers.Any(k => entry.DrawingLayers.Contains(k))))
                tech.Stackup.Layers.Add(entry);

        string techPath = Path.Combine(staging, name + ".ctech");
        TechPersistence.SaveToFile(techPath, tech);
        Console.Error.WriteLine($"[circuitRF] technology: {techPath} ({tech.Layers.Count} layer(s))");

        // Recorded rather than reported here: whether this file SURVIVES is the caller's question,
        // and only Run knows the answer (it is deleted with the scratch directory unless the target
        // was clay or --keep-cells named somewhere). Reporting a path that is about to be removed
        // would be worse than reporting nothing.
        _mintedTechPath = techPath;

        foreach (var cellDir in cellDirs)
        {
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
            if (primary.ResolvedName is not { } file) continue;
            string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            string clay = Path.Combine(layoutDir, file);
            var view = LayoutPersistence.LoadFromFile(clay);
            view.TechRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(layoutDir, techPath));
            LayoutPersistence.SaveToFile(clay, view);
        }

        return tech;
    }

    private static Source? Finish(string cellDir, Technology? tech, IReadOnlyList<string> createdCellDirs)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (primary.ResolvedName is not { } file)
        {
            JsonRun.Report(CliDiagnostics.ConvertCellHasNoLayout(Path.GetFileName(cellDir)));
            return null;
        }

        string clay = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), file);
        var view = LayoutPersistence.LoadFromFile(clay);
        return new Source(cellDir, view, tech, view.DbuPerMicron,
            Path.GetFileNameWithoutExtension(file), createdCellDirs);
    }

    // ── The four writers ──────────────────────────────────────────────────────────────────────────

    private static int Export(Options o, Fmt to, Source src)
    {
        string output = Path.GetFullPath(o.Output!);
        string? dir = to == Fmt.Gerber ? output : Path.GetDirectoryName(output);
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);

        string stem = o.Name ?? src.CellName;
        Console.Error.WriteLine($"[circuitRF] cell: {src.CellName}  ({src.DbuPerMicron} DBU/µm)");

        switch (to)
        {
            case Fmt.Gdsii:
            {
                var plan = GdsiiExport.Analyze(src.CellDir, src.Tech, src.DbuPerMicron, src.View);
                foreach (var u in plan.UnresolvedInstanceReferences)
                    Console.Error.WriteLine($"warning: unresolved instance reference '{u}' — not written.");
                if (!plan.CanWrite)
                {
                    Console.Error.WriteLine("error: values do not fit GDSII — nothing written.");
                    JsonRun.Note(CliDiagnostics.ConvertGdsiiCoordinateOverflow());
                    foreach (var c in plan.CoordinateOverflowOffenders) Console.Error.WriteLine($"       {c}");
                    return 1;
                }
                GdsiiExportSummary summary;
                try { summary = StreamInterchange.Write(o.GdsiiRoute, output, plan, token: RunHost.Cancellation); }
                catch (GdstkException ex) { return JsonRun.Fail(ex.Diagnostic); }
                JsonRun.AddOutput("gdsii", output);
                // What the gdstk route adds to say (a port flag it cannot carry, the worker's notes). The native
                // writer's own diagnostics repeat the plan's counts, which are reported just below.
                if (o.GdsiiRoute.UsesGdstk()) Report(summary.Diagnostics);
                Note(plan.CurvedShapesFlattened, "curved shape", "flattened to polygons");
                Note(plan.HolesKeyholed, "hole", "keyholed");
                Note(plan.BitmapsSkipped, "bitmap", "skipped — GDSII carries no raster");
                Note(plan.ViaPadsSkipped, "via", "exported as a barrel with no landing pad");
                foreach (var r in plan.LayerRenumberings)
                {
                    Console.Error.WriteLine($"note: {r}");
                    JsonRun.Note(CliDiagnostics.ConvertNote(r));
                }
                if (plan.HasVias) Console.Error.WriteLine(
                    "note: this design has vias, and GDSII carries no drill table — geometry only, not a PCB deliverable.");
                Console.WriteLine(output);
                return 0;
            }

            case Fmt.Dxf:
            {
                var plan = DxfExport.Analyze(src.CellDir, src.Tech, src.DbuPerMicron, src.View);
                foreach (var u in plan.UnresolvedInstanceReferences)
                    Console.Error.WriteLine($"warning: unresolved instance reference '{u}' — not written.");
                var opts = new DxfExportOptions(
                    InsUnits: o.InsUnits ?? DxfUnits.DefaultPromptUnits,
                    AcadVersion: o.AcadVersion);
                var summary = DxfExport.Write(output, plan, opts);
                JsonRun.AddOutput("dxf", output);
                foreach (var d in summary.Diagnostics) Console.Error.WriteLine($"note: {d}");
                Note(summary.BitmapsSkipped, "bitmap", "skipped — DXF carries no raster");
                Note(summary.MixedArcCubicApproximated, "mixed arc/cubic edge", "approximated");
                Note(summary.NonAsciiTextEscaped, "text string", "escaped for the chosen DXF version");
                if (plan.HasVias) Console.Error.WriteLine(
                    "note: this design has vias, and DXF carries no drill table — geometry only, not a PCB deliverable.");
                Console.WriteLine(output);
                return 0;
            }

            case Fmt.Board:
            {
                var plan = PcbExport.Analyze(src.CellDir, src.Tech, src.DbuPerMicron, src.View);
                if (!plan.CanWrite)
                {
                    Console.Error.WriteLine($"error: {plan.Refusal}");
                    JsonRun.Note(CliDiagnostics.ConvertBoardRefused(plan.Refusal ?? ""));
                    return 1;
                }
                var summary = PcbExport.Write(output, plan);
                JsonRun.AddOutput("board", output);
                foreach (var n in summary.Notes) Console.Error.WriteLine($"note: {n}");
                foreach (var l in summary.UnmappedLayerNames)
                    Console.Error.WriteLine($"warning: layer '{l}' has no board-layer mapping — written to a general drawing layer.");
                Note(plan.CellsFlattenedForLackOfPins, "cell", "flattened into board geometry: it declares no pins, so it is not a footprint");
                Note(plan.UnresolvedInstanceReferences, "instance reference", "unresolved — not written");
                Note(summary.PinsWithNoArtwork, "pin", "has no artwork on any copper layer");
                Console.WriteLine(output);
                return 0;
            }

            case Fmt.Gerber:
            {
                var cache = new TechnologyCache();
                var plan = GerberExport.Analyze(src.CellDir, src.Tech, src.DbuPerMicron, src.View,
                    resolveTechAt: (techRef, cellLayoutDir) =>
                        TechnologyResolver.ResolveForDocument(techRef, cellLayoutDir, o.Cws, cache).Resolution);

                foreach (var d in plan.Diagnostics)
                { Console.Error.WriteLine($"error: {d}"); JsonRun.Note(CliDiagnostics.ConvertGerberDiagnostic(d)); }
                if (plan.ExceedsHierarchyCeiling)
                {
                    // Recorded only: plan.Diagnostics above already said what is wrong, on stderr.
                    JsonRun.Note(CliDiagnostics.ConvertGerberHierarchyCeiling());
                    return 1;
                }

                if (plan.RequiresMappingConfirmation)
                {
                    // The GUI resolves this with the shared layer-mapping dialog. There is no honest
                    // headless default: a sub-cell drawn against a DIFFERENT technology has layers
                    // whose numbers mean something else, and picking for the user would silently move
                    // copper between layers.
                    Console.Error.WriteLine(
                        "error: this design instantiates cells from another technology, and the layer mapping " +
                        "has to be confirmed. Open it in circuitRF and export once, or flatten the design first.");
                    JsonRun.Note(CliDiagnostics.ConvertGerberCrossTechnology());
                    foreach (var k in plan.PendingCrossTechMappings.Keys) Console.Error.WriteLine($"       {k}");
                    return 1;
                }

                var result = GerberExport.Write(output, stem, plan);
                Note(plan.LabelsConvertedToGeometry, "label", "converted to geometry — Gerber has no text");
                Note(plan.PortLabelsOmitted, "port label", "omitted: a marker, not artwork");
                Note(plan.BitmapsOmitted, "bitmap", "omitted — Gerber carries no raster");
                Note(plan.PathsAsRegion, "path", "written as a region: its end style is not a round cap");
                Note(plan.UnpairedDrillCircles, "bare circle", "on a drill layer drilled a hole with no pad — 'Convert to Via' pairs them");
                if (plan.LabelsConvertedToGeometry > 0 && !LayoutTextOutline.HasEmbeddedTypefaces)
                    Console.Error.WriteLine(
                        "note: labels were flattened with the platform default typeface — the embedded faces the " +
                        "application draws with need a running app, so this glyph artwork differs from the GUI's.");
                Console.Error.WriteLine(
                    $"[circuitRF] {result.FilesWritten.Count} file(s), " +
                    $"{result.DrillToolsDefined} drill tool(s), {result.DrillHitsWritten} hit(s)");
                foreach (var f in result.FilesWritten) { Console.WriteLine(f); JsonRun.AddOutput("gerber", f); }
                return 0;
            }

            default:
                return JsonRun.Fail(CliDiagnostics.ConvertTargetUnsupported(Name(to)));
        }
    }

    // ── --list-cells ──────────────────────────────────────────────────────────────────────────────

    private static int ListCells(Options o, Fmt from)
    {
        if (from == Fmt.Clay)
        {
            return JsonRun.Fail(CliDiagnostics.ConvertListCellsNotApplicable());
        }

        string staging = Path.Combine(Path.GetTempPath(), "circuitrf-list-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            string? scratch = staging;
            var src = LoadSource(o, from, Fmt.Gdsii /* anything but Clay: import into scratch */, ref scratch);
            if (src is null) return 1;
            foreach (var d in src.CreatedCellDirs)
            {
                Console.WriteLine(Path.GetFileName(d));
                JsonRun.Note(CliDiagnostics.ConvertCellListed(Path.GetFileName(d)));
            }
            return 0;
        }
        finally
        {
            if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch { /* best effort */ }
        }
    }

    // ── Small shared pieces ───────────────────────────────────────────────────────────────────────

    private static string? PickCell(string? wanted, IReadOnlyList<string> created, string? preferred, string what)
    {
        if (wanted is not null)
        {
            var hit = created.FirstOrDefault(d => string.Equals(Path.GetFileName(d), wanted, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
            JsonRun.Report(CliDiagnostics.ConvertCellNotFound(
                wanted, string.Join(", ", created.Select(Path.GetFileName))));
            return null;
        }

        if (preferred is not null) return preferred;
        if (created.Count == 1) return created[0];

        JsonRun.Report(CliDiagnostics.ConvertCellAmbiguous(created.Count, what));
        return null;
    }

    private static Source? Refused()
    {
        JsonRun.Report(CliDiagnostics.ConvertNothingConverted());
        return null;
    }

    /// <summary>
    /// The import's own messages. On stderr with the `note: ` prefix they have always had — the
    /// prefix is a channel convention rather than part of the message — and in the `--json` document
    /// as coded diagnostics, because they are how a caller learns what a conversion DROPPED
    /// (brief-automation-4-check-and-explain.md's convert carry-through).
    /// </summary>
    private static void Report(IReadOnlyList<string> messages)
    {
        foreach (var m in messages)
        {
            Console.Error.WriteLine($"note: {m}");
            JsonRun.Note(CliDiagnostics.ConvertNote(m));
        }
    }

    private static void Note(int n, string noun, string what)
    {
        if (n <= 0) return;
        string text = $"{n} {noun}{(n == 1 ? "" : "s")} {what}.";
        Console.Error.WriteLine($"note: {text}");
        JsonRun.Note(CliDiagnostics.ConvertNote(text));
    }

    private static string ImportName(string input) =>
        Directory.Exists(input)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)))
            : Path.GetFileNameWithoutExtension(input);

    private static Fmt? ParseFormat(string s) => s.ToLowerInvariant() switch
    {
        "clay" or "layout" or "circuitrf" => Fmt.Clay,
        "gds" or "gdsii" or "gds2" => Fmt.Gdsii,
        "dxf" => Fmt.Dxf,
        "gerber" or "rs274x" or "excellon" => Fmt.Gerber,
        "board" or "kicad_pcb" => Fmt.Board,   // the extension is a data format; the bare product name is not ours to use
        "step" or "stp" => Fmt.Step,
        "gltf" or "glb" => Fmt.Gltf,
        _ => null,
    };

    private static int BadFormat(string s) => JsonRun.Fail(CliDiagnostics.ConvertUnknownFormat(s));

    /// <summary><see cref="Name"/>, naming the gdstk route on a GDSII end that takes it.</summary>
    private static string RouteName(Options o, Fmt f) => f == Fmt.Gdsii ? o.GdsiiRoute.DisplayName() : Name(f);

    internal static string Name(Fmt f) => f switch
    {
        Fmt.Clay => "clay", Fmt.Gdsii => "GDSII", Fmt.Dxf => "DXF",
        Fmt.Gerber => "Gerber", Fmt.Step => "STEP", Fmt.Gltf => "glTF", _ => "board",
    };

    internal static Fmt? DetectSource(string path)
    {
        if (Directory.Exists(path)) return Fmt.Gerber;
        if (ByExtension(path) is { } byExt) return byExt;

        // No telling extension. Gerber and Excellon files are named however the toolchain that wrote
        // them felt like, so the answer comes from the content — through the classifier the import
        // itself uses, never a second rule. A STEP file says so on its first line (ISO 10303-21's
        // header), which StepImport reads — the one line of STEP text read outside the worker.
        if (StepImport.LooksLikeStep(path)) return Fmt.Step;
        try
        {
            var kind = GerberFileClassifier.Classify(path).Kind;
            if (kind is GerberFileKind.Artwork or GerberFileKind.Drill or GerberFileKind.JobFile) return Fmt.Gerber;
        }
        catch (IOException) { /* fall through to "cannot tell" */ }
        return null;
    }

    private static Fmt? DetectTarget(string path) => ByExtension(path);

    private static Fmt? ByExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".clay" => Fmt.Clay,
        ".gds" or ".gdsii" or ".gds2" => Fmt.Gdsii,
        ".dxf" => Fmt.Dxf,
        ".kicad_pcb" => Fmt.Board,
        ".step" or ".stp" => Fmt.Step,
        _ => null,
    };

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf convert <input> -o <output> [--from f] [--to f] [--cell name]");
        Console.Error.WriteLine("       formats: clay | gdsii | dxf | gerber | board | step | gltf");
        Console.Error.WriteLine("       --engine native|gdstk  the GDSII reader or writer for a gdsii source or target (default native)");
        Console.Error.WriteLine("       step source (-o <new>.c3d):  --material <part>=<name> (repeatable)  --part <path> (repeatable)  --tech <path.ctech>");
        Console.Error.WriteLine("       step target (-o <file>.step; from a .c3d, .clay, cell folder or any format above):");
        Console.Error.WriteLine("              --assembly  --as-drawn  --thicken-sheets  --include-airbox  --schema ap214|ap242  --view 3d|layout  --tech <path.ctech>");
        Console.Error.WriteLine("       gltf target (-o <file>.glb; from a .c3d):  --gltf-assembly  --gltf-field <plot>  --region <name>");
        Console.Error.WriteLine("       --no-coalesce  keep a painted pour's individual strokes");
        JsonRun.Note(CliDiagnostics.ConvertUsage());
        return 1;
    }
}
