using CircuitRF.Core.Pdk;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf import pdk &lt;kit-dir&gt; --into &lt;workspace&gt; [--name N] [--allow-code]</c> — File ▸
/// Import PDK with no window (brief-gdf-1 R-gdf1-4b).
///
/// <para><b>It adds NO import logic.</b> <see cref="PdkWorkspaceImport.Import"/> reads the kit, installs
/// its parts, records the <c>.cws</c> reference and declares the kit's parametric cells — the calls the
/// GUI's own import makes, in the same order. What is here is argument parsing, the sentences, and the
/// one decision the GUI makes in a dialog: whether the kit's code may run.</para>
///
/// <para><b>Kit code runs only with consent</b> (gdsfactory series D2). A decision recorded at the
/// keyboard, <c>--trust-kit</c>, or <c>--allow-code</c> — which grants it for this one process and is
/// never recorded. Without any of them the import still succeeds, and says which generators were not
/// started and why. Nothing here ever prompts.</para>
/// </summary>
internal static class ImportPdk
{
    public const string UsageText =
        "       circuitrf import pdk <kit-folder> --into <workspace> [--name N] [--allow-code]";

    /// <summary>
    /// How the generators find an interpreter. A seam for the consent test: it counts the interpreters
    /// asked for, which is the step every started generator takes and a refused one never reaches.
    /// Null is the resolver's own discovery.
    /// </summary>
    internal static Func<PCellGeneratorManifest, string, PythonInterpreter?>? InterpreterFinder { get; set; }

    public static int Run(string[] args)
    {
        string? source = null, into = null, name = null;
        bool allowCode = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--into" when i + 1 < args.Length: into = args[++i]; break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--allow-code": allowCode = true; break;
                default:
                    if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImportUnknownOption(args[i])); return Usage(); }
                    if (source is not null) { JsonRun.Report(CliDiagnostics.ImportMultipleSources()); return Usage(); }
                    source = args[i];
                    break;
            }
        }

        if (source is null) { JsonRun.Report(CliDiagnostics.ImportSourceRequired()); return Usage(); }
        JsonRun.InputPath = source;
        if (!Directory.Exists(source))
            return File.Exists(source)
                ? JsonRun.Fail(CliDiagnostics.ImportPdkRefused(
                    $"'{source}' is a file. import pdk takes the kit's folder; unpack an archive first."))
                : JsonRun.Fail(CliDiagnostics.ImportSourceNotFound(source));
        if (into is null) { JsonRun.Report(CliDiagnostics.ImportPdkIntoRequired()); return Usage(); }

        string full = Path.GetFullPath(into);
        if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
        if ((Directory.Exists(full) ? WorkspaceRootFinder.WorkspaceDirOf(full) : null) is not { } wsRoot)
            return JsonRun.Fail(CliDiagnostics.ImportPdkNotAWorkspace(into));
        string cwsPath = Path.Combine(wsRoot, WorkspacePersistence.FileName);

        var result = PdkWorkspaceImport.Import(Path.GetFullPath(source), cwsPath, name);
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
                  "this one import; Settings ▸ General ▸ Generated Artwork records a decision."));
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
        Console.Error.WriteLine("Usage: circuitrf import pdk <kit-folder> --into <workspace> [--name N] [--allow-code]");
        return 1;
    }
}
