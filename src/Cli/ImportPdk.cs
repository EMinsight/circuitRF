using CircuitRF.Core.Pdk;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Layout.TechImport;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf import pdk &lt;kit-dir | kit.zip&gt; --into &lt;workspace&gt; [--name N] [--allow-code]</c> — File ▸
/// Import PDK with no window (brief-gdf-1 R-gdf1-4b).
///
/// <para><b>It adds NO import logic.</b> <see cref="PdkWorkspaceImport.Import"/> reads the kit, installs
/// its parts, records the <c>.cws</c> reference and declares the kit's parametric cells — the calls the
/// GUI's own import makes, in the same order — and since brief-gdf-6, the very call the GUI makes, folder
/// routing included. An archive is unpacked by <see cref="KitArchive"/>, the code the GUI unpacks with. What
/// is here is argument parsing, the sentences, and the one decision the GUI makes in a dialog: whether the
/// kit's code may run.</para>
///
/// <para><b>Kit code runs only with consent</b> (gdsfactory series D2). A decision recorded at the
/// keyboard, <c>--trust-kit</c>, or <c>--allow-code</c> — which grants it for this one process and is
/// never recorded. Without any of them the import still succeeds, and says which generators were not
/// started and why. Nothing here ever prompts.</para>
/// </summary>
internal static class ImportPdk
{
    public const string UsageText =
        "       circuitrf import pdk <kit-folder|kit.zip> --into <workspace> [--name N] [--allow-code]\n" +
        "       circuitrf import pdk --module <m> --python <interp> --into <workspace> [--allow-code] [--technology]";

    /// <summary>
    /// How the generators find an interpreter. A seam for the consent test: it counts the interpreters
    /// asked for, which is the step every started generator takes and a refused one never reaches.
    /// Null is the resolver's own discovery.
    /// </summary>
    internal static Func<PCellGeneratorManifest, string, PythonInterpreter?>? InterpreterFinder { get; set; }

    public static int Run(string[] args)
    {
        string? source = null, into = null, name = null, module = null;
        bool allowCode = false, technology = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--into" when i + 1 < args.Length: into = args[++i]; break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--allow-code": allowCode = true; break;
                case "--technology": technology = true; break;
                case "--module" when i + 1 < args.Length: module = args[++i]; break;
                default:
                    if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImportUnknownOption(args[i])); return Usage(); }
                    if (source is not null) { JsonRun.Report(CliDiagnostics.ImportMultipleSources()); return Usage(); }
                    source = args[i];
                    break;
            }
        }

        // A gdsfactory kit is a Python package, named by module (brief-gdf-2 R-gdf2-5c).
        if (module is not null)
        {
            if (source is not null) { JsonRun.Report(CliDiagnostics.ImportMultipleSources()); return Usage(); }
            return RunGdsfactory(module, null, into, allowCode, technology);
        }

        if (source is null) { JsonRun.Report(CliDiagnostics.ImportSourceRequired()); return Usage(); }
        JsonRun.InputPath = source;
        bool archive = File.Exists(source) && KitArchive.IsArchive(source);
        if (!Directory.Exists(source) && !archive)
            return File.Exists(source)
                ? JsonRun.Fail(CliDiagnostics.ImportPdkRefused(
                    $"'{source}' is a file. import pdk takes the kit's folder, or a .zip archive of one."))
                : JsonRun.Fail(CliDiagnostics.ImportSourceNotFound(source));
        if (into is null) { JsonRun.Report(CliDiagnostics.ImportPdkIntoRequired()); return Usage(); }

        // A kit folder's process files are a different source with a different reader (File ▸ Import ▸
        // Technology); --technology builds a gdsfactory kit's, which states its process as data. Checked
        // before anything is written, so a flag that cannot apply writes nothing.
        if (technology && !archive && GdsfactoryKitDiscovery.FindSourceTree(source) is null)
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(
                $"--technology builds a gdsfactory kit's technology (--module, or a folder holding a gdsfactory " +
                $"kit's source tree), and '{source}' holds none."));

        if (name is not null && PdkWorkspaceImport.KitNameProblem(name) is { } nameProblem)
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused($"--name '{name}': {nameProblem}"));

        string full = Path.GetFullPath(into);
        if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
        if ((Directory.Exists(full) ? WorkspaceRootFinder.WorkspaceDirOf(full) : null) is not { } wsRoot)
            return JsonRun.Fail(CliDiagnostics.ImportPdkNotAWorkspace(into));
        string cwsPath = Path.Combine(wsRoot, WorkspacePersistence.FileName);

        // An archive is unpacked into the workspace and imported as the folder it became — what the GUI
        // does (D4). Never over an existing folder: nothing here can ask, and someone may have edited it.
        if (archive)
        {
            string destination = KitArchive.DestinationFor(source, wsRoot);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(
                    $"'{Path.GetFileName(source)}' is already unpacked in this workspace, at '{destination}'. " +
                    "Import that folder, or remove it to unpack the archive again."));
            try { source = KitArchive.ExtractInto(Path.GetFullPath(source), wsRoot, overwrite: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return JsonRun.Fail(CliDiagnostics.ImportPdkRefused($"the archive could not be unpacked: {ex.Message}"));
            }
            Console.WriteLine($"unpacked\t{source}");
            JsonRun.AddOutput("kit", source);
        }

        var result = PdkWorkspaceImport.Import(Path.GetFullPath(source), cwsPath, name);

        // The folder decides, in the shared function, whether it is one kit or two (D2).
        int exit = result.OnlyGdsfactory ? 0 : ReportOrdinary(result, wsRoot, cwsPath, allowCode);
        if (result.Gdsfactory is { } tree)
            exit = Math.Max(exit, RunGdsfactory(tree.Module, tree, into, allowCode, technology));
        else if (technology)
            // Only reachable for an archive, whose contents could not be looked at before it was unpacked.
            exit = JsonRun.Fail(CliDiagnostics.ImportPdkRefused(
                $"--technology builds a gdsfactory kit's technology, and '{source}' holds none; no technology was built."));
        return exit;
    }

    /// <summary>The ordinary half of an import: what <see cref="PdkWorkspaceImport.Import"/> read, installed and
    /// recorded, said as the import report dialog says it.</summary>
    private static int ReportOrdinary(PdkWorkspaceImport.ImportResult result, string wsRoot, string cwsPath, bool allowCode)
    {
        var report = result.Report;

        // The report's own findings, each at its own level — the import report dialog's content.
        foreach (var f in report.Findings)
            Say(f.Severity switch
            {
                PdkFindingSeverity.Blocker => DiagnosticSeverity.Error,
                PdkFindingSeverity.Warning => DiagnosticSeverity.Warning,
                _                          => DiagnosticSeverity.Info,
            }, string.IsNullOrEmpty(f.SuggestedAction) ? f.Summary : $"{f.Summary} {f.SuggestedAction}");

        if (report.Status == PdkImportStatus.Failed)
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(
                $"{report.KitName} could not be read; nothing was recorded."));
        if (!PdkWorkspaceImport.IsRecordable(report))
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(
                $"circuitRF recognised nothing it can use in '{report.RootPath}'; nothing was recorded."));
        if (result.NameProblem is { } derived)
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(PdkWorkspaceImport.DerivedNameSentence(
                report.KitName, derived, "--name gives it one.")));
        if (result.InstallProblem is { } installProblem)
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(
                $"{report.KitName}: its parts could not be installed: {installProblem}"));

        var outcome = result.Outcome!;
        var commit  = result.Commit!;

        // What the install worked out is neutral status; what it could not do is a warning — the levels
        // the application posts them at.
        foreach (string n in outcome.Notes ?? []) Say(DiagnosticSeverity.Info, n);
        foreach (string d in outcome.Diagnostics) Say(DiagnosticSeverity.Warning, d);

        if (commit.RecordProblem is { } recordProblem)
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(PdkWorkspaceImport.RecordProblemSentence(recordProblem)));

        // A run in this same process (serve) must read the kit set this import just changed.
        WorkspaceKits.Forget(wsRoot);

        Console.WriteLine($"{outcome.KitName}\t{report.RootPath}");
        Console.WriteLine(PdkWorkspaceImport.TallySentence(report));
        var (placeable, sentence) = PdkWorkspaceImport.PlaceabilitySentence(report, outcome, workspaceOpen: true);
        if (placeable) Console.WriteLine(sentence);
        else           Say(DiagnosticSeverity.Warning, sentence);
        if (report.CornerAxes.Count > 0)
            Console.WriteLine($"corners: {string.Join("; ", report.CornerAxes.Select(a => $"{a.DisplayName} ({a.Options.Count})"))}");
        if (report.LayerTechnology is { } tech)
            Console.WriteLine($"layer technology: {tech.RelativePath}");
        JsonRun.AddOutput("cws", cwsPath);

        if (commit.PCellProblem is { } examine)
            Say(DiagnosticSeverity.Warning, PdkWorkspaceImport.PCellExamineProblem(outcome.KitName, examine));
        if (commit.PCells is { } pcells)
        {
            if (pcells.Sentence(outcome.KitName) is { } said)
                Say(pcells.Folder is null ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info, said);
            if (pcells.Folder is { } folder)
            {
                JsonRun.AddOutput("pcells", folder);
                StartGenerators(wsRoot, folder, allowCode);
            }
        }
        return 0;
    }

    /// <summary>
    /// Starts the kit's declared generators when consent allows — and only this kit's — so the import
    /// can say how many cells they offer. Without consent nothing is launched, and the sentence says so.
    /// </summary>
    private static void StartGenerators(string wsRoot, string folder, bool allowCode)
    {
        string key = PCellTrustStore.Normalize(folder);
        var policy = GeneratedCells.Trust();
        var decision = allowCode ? PCellTrustDecision.Allowed : policy(folder);
        if (decision != PCellTrustDecision.Allowed)
        {
            JsonRun.Report(CliDiagnostics.ImportPdkGeneratorsNotStarted(decision == PCellTrustDecision.Denied
                ? $"The kit's layout generators in '{folder}' were not started: this machine has recorded that " +
                  "they may not run. --allow-code runs them for this one import."
                : $"The kit's layout generators in '{folder}' were not started: nobody has allowed this kit's " +
                  "code to run on this machine, and a headless import cannot ask. --allow-code runs them for " +
                  "this one import; opening the workspace in circuitRF asks, and records the answer."));
            return;
        }

        var reports = new List<string>();
        using var resolver = new PCellWorkerResolver(
            wsRoot, InterpreterFinder, report: reports.Add,
            // Every OTHER kit in the workspace is left alone: this import grants nothing to them.
            trust: dir => PCellTrustStore.Normalize(dir) == key ? PCellTrustDecision.Allowed : PCellTrustDecision.Unknown);

        int started = resolver.KnownGeneratorIds.Count(id =>
            resolver.KitDirectoryByGeneratorId.TryGetValue(id, out string? d) && PCellTrustStore.Normalize(d) == key);
        foreach (string r in reports.Where(r => r.Contains(folder, StringComparison.Ordinal)))
            Say(DiagnosticSeverity.Warning, r);
        Say(DiagnosticSeverity.Info, $"The kit's layout generators offer {started} cell(s).");
    }

    /// <summary>
    /// The gdsfactory import: the interpreter (<c>--python</c>, else this user's recorded choice), the
    /// kit found by files alone, the declaration written, and — only with consent — the kit described,
    /// so the report says what the palette will offer. Every step is <see cref="GdsfactoryKitImport"/>'s,
    /// the function the application's File ▸ Import PDK calls.
    /// </summary>
    private static int RunGdsfactory(string module, GdsfactoryKit? tree, string? into, bool allowCode, bool technology)
    {
        JsonRun.InputPath = tree?.InstalledDirectory ?? module;
        if (into is null) { JsonRun.Report(CliDiagnostics.ImportPdkIntoRequired()); return Usage(); }

        string full = Path.GetFullPath(into);
        if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
        if ((Directory.Exists(full) ? WorkspaceRootFinder.WorkspaceDirOf(full) : null) is not { } wsRoot)
            return JsonRun.Fail(CliDiagnostics.ImportPdkNotAWorkspace(into));

        if (GeneratedCells.InterpreterFor(module) is not { } interpreter)
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(
                $"No Python environment is chosen for the gdsfactory kit '{module}' on this machine. " +
                $"{GeneratedCells.PythonFlag} <interpreter> names the one that has it installed."));

        GdsfactoryKit? kit;
        string? refusal;
        if (tree is null) kit = GdsfactoryKitImport.ResolveModule(interpreter, module, out refusal);
        else
        {
            var env = GdsfactoryKitImport.CheckEnvironment(interpreter, tree.Display, out _, out refusal);
            kit = env is not null && GdsfactoryKitImport.Fits(tree, env, interpreter, out refusal) ? tree : null;
        }
        if (kit is null) return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(refusal ?? $"'{module}' could not be found."));

        string? folder = GdsfactoryKitImport.Declare(wsRoot, kit, out string? problem, out bool created);
        if (folder is null)
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused($"{kit.Display}: its declaration could not be written: {problem}"));

        Console.WriteLine($"{kit.Module}\t{kit.InstalledDirectory}");
        Say(DiagnosticSeverity.Info, created
            ? $"{kit.Display} is declared in '{folder}'."
            : $"{kit.Display} was already declared in '{folder}'; it was left as it is.");
        JsonRun.AddOutput("pcells", folder);

        // Consent keys on the kit's installed directory with links resolved (R-gdf2-2a); --allow-code
        // grants it for this one process and is never recorded.
        string trustKey = PCellTrustStore.ResolvedDirectory(kit.InstalledDirectory);
        var decision = allowCode ? PCellTrustDecision.Allowed : GeneratedCells.Trust()(trustKey);
        if (decision != PCellTrustDecision.Allowed)
        {
            JsonRun.Report(CliDiagnostics.ImportPdkGeneratorsNotStarted(
                $"{kit.Display} was not run, so what it offers is not reported" +
                (technology ? " and its technology was not built" : "") + ": " +
                (decision == PCellTrustDecision.Denied
                    ? "this machine has recorded that its code may not run. "
                    : "nobody has allowed its code to run on this machine, and a headless import cannot ask. ") +
                "--allow-code runs it for this one import."));
            // Asked for a technology and none was written: that is not success.
            return technology ? 1 : 0;
        }

        if (GdsfactoryKitImport.ReadReport(interpreter, kit, out string? reportProblem) is not { } report)
            Say(DiagnosticSeverity.Warning, $"{kit.Display} could not be described: {reportProblem}");
        else
        {
            Console.WriteLine(report.Tally());
            if (report.Problem is { } fatal) Say(DiagnosticSeverity.Warning, fatal);
            foreach (var (reason, cells) in report.NotOffered.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                foreach (var cell in cells)
                    Say(DiagnosticSeverity.Info, $"not offered ({reason}): {string.Join(": ", cell)}");
            foreach (var (cell, names) in report.ParametersNotOffered.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Say(DiagnosticSeverity.Info, $"{cell}: {names.Count} parameter(s) not offered: {string.Join(", ", names)}");
            foreach (var (cell, n) in report.PlacementPorts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Say(DiagnosticSeverity.Info, $"{cell}: {n} placement port(s) not made pins");

            // The kit's own drawings, made into the symbols the palette mounts — the install Import PDK runs.
            if (report.Problem is null && report.Symbols.Count + report.Glyphs.Count > 0)
            {
                var symbols = KitGlyphSymbol.Install(folder, report);
                Console.WriteLine(symbols.Tally());
                foreach (var path in symbols.Built.Values.Concat(symbols.Copied.Values).Order(StringComparer.Ordinal))
                    JsonRun.AddOutput("symbol", path);
                foreach (var (cell, text, warning) in symbols.Lines)
                    Say(warning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info, $"{cell}: {text}");
                Say(DiagnosticSeverity.Info, KitGlyphSymbol.NoModelNote);
            }
        }

        return technology ? BuildTechnology(interpreter, kit, wsRoot) : 0;
    }

    /// <summary>
    /// <c>--technology</c> (brief-gdf-3 R-gdf3-3b): the kit's stack and material cards, read by
    /// <see cref="GdsfactoryKitImport.ReadTechnology"/>, converted by <see cref="GdsfactoryStackConverter"/>
    /// through the one technology builder, and written by <see cref="KitTechnologyInstall"/> — every step the
    /// application's own Import PDK takes. Never over an existing file, since nothing here can ask; the
    /// workspace's default only when it has none.
    /// </summary>
    private static int BuildTechnology(string interpreter, GdsfactoryKit kit, string wsRoot)
    {
        if (GdsfactoryKitImport.ReadTechnology(interpreter, kit, out var layerTable, out string? problem) is not { } doc)
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused($"{kit.Display}: its technology could not be read: {problem}"));
        if (doc.Stack.Count == 0)
        {
            foreach (string p in doc.Problems) Say(DiagnosticSeverity.Warning, p);
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused($"{kit.Display} states no layer stack, so no technology was built."));
        }

        var result = GdsfactoryStackConverter.Convert(doc, layerTable, kit.Module);
        string path = KitTechnologyInstall.PathFor(wsRoot, kit.Module);
        if (!KitTechnologyInstall.TryWriteNew(path, result.Technology, out string? refusal))
            return JsonRun.Fail(CliDiagnostics.ImportPdkRefused(refusal!));

        Console.WriteLine(result.Summary);
        foreach (var n in result.Notes)
            Say(n.Severity == TechnologyNoteSeverity.Warning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info, n.Text);
        JsonRun.AddOutput("technology", path);

        string cwsPath = Path.Combine(wsRoot, WorkspacePersistence.FileName);
        Say(DiagnosticSeverity.Info, KitTechnologyInstall.MakeDefaultIfNone(cwsPath, path)
            ? $"'{Path.GetFileName(path)}' is now the workspace's default technology; it had none."
            : $"'{Path.GetFileName(path)}' was added beside the workspace's default technology, which is unchanged.");
        return 0;
    }

    private static void Say(DiagnosticSeverity severity, string text)
    {
        var d = CliDiagnostics.ImportPdkFinding(severity, text);
        string channel = severity switch
        {
            DiagnosticSeverity.Error   => "error: ",
            DiagnosticSeverity.Warning => "warning: ",
            _                          => "[circuitRF] ",
        };
        Console.Error.WriteLine(channel + d.Render());
        JsonRun.Note(d);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf import pdk <kit-folder|kit.zip> --into <workspace> [--name N] [--allow-code]");
        Console.Error.WriteLine("       circuitrf import pdk --module <m> --python <interp> --into <workspace> [--allow-code] [--technology]");
        return 1;
    }
}
