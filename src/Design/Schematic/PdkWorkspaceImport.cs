using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// File ▸ Import PDK after its picker closes, and a referenced kit's load on a workspace open — the
/// sequence both perform, framework-free, so the GUI and <c>circuitrf import pdk</c> run ONE copy of it
/// (brief-gdf-1 R-gdf1-4a). An operation that lives only in a view model is not a capability.
///
/// <para>Three steps, kept separate because the application runs them on different threads and shows a
/// progress stage between them: <see cref="Read"/> (the kit off disk), <see cref="Install"/> (its parts,
/// translated in memory) and <see cref="Commit"/> (what the workspace records — the <c>.cws</c>
/// reference and the kit's parametric-cell declaration). <see cref="Import"/> is the three in order.
/// What a caller does with the result — a palette, messages, a registry — stays the caller's.</para>
///
/// <para><b>Diagnostics are returned, never posted</b> (this project's rule): every sentence the GUI
/// raises as a message is a value here.</para>
/// </summary>
public static class PdkWorkspaceImport
{
    /// <summary>What happened to the kit's parametric-cell library, when it has one.</summary>
    /// <param name="Package">The package found inside the kit.</param>
    /// <param name="AlsoFound">Further cell packages the kit holds, which were passed over.</param>
    /// <param name="Folder">The workspace folder holding the declaration, or null when it could not be written.</param>
    /// <param name="Created">True when this call wrote it; false when it was already declared.</param>
    /// <param name="Problem">Why nothing could be written, or null.</param>
    public sealed record PCellDeclaration(
        KitPCellPackage Package,
        IReadOnlyList<KitPCellPackage> AlsoFound,
        string? Folder,
        bool Created,
        string? Problem)
    {
        /// <summary>
        /// What to tell the user about this declaration, or null when there is nothing to say — it was
        /// already declared, so nothing changed. A <see cref="Folder"/> of null makes it a warning.
        /// </summary>
        public string? Sentence(string kitName)
        {
            if (Folder is null)
                return $"'{kitName}' ships a parametric-cell library ({Package.PackageName}), but circuitRF " +
                       $"could not record it: {Problem} Its parts still place and draw; only their layout " +
                       $"artwork needs it.";
            if (!Created) return null;

            string extra = AlsoFound.Count > 0
                ? $" It also holds {AlsoFound.Count} other cell package(s) " +
                  $"({string.Join(", ", AlsoFound.Take(3).Select(p => p.PackageName))}" +
                  $"{(AlsoFound.Count > 3 ? ", …" : "")}); edit that folder to register one instead."
                : "";
            return $"'{kitName}' offers {Package.CellModuleCount} parametric layout cell(s) " +
                   $"({Package.PackageName}). circuitRF recorded how to run them in " +
                   $"'{Path.GetFileName(Folder)}'.{extra}";
        }
    }

    /// <summary>The sentence for a kit whose cell library could not even be looked for.</summary>
    public static string PCellExamineProblem(string kitName, string problem)
        => $"'{kitName}': its parametric-cell library could not be examined: {problem}";

    /// <summary>The sentence for a kit whose <c>.cws</c> reference could not be written.</summary>
    public static string RecordProblemSentence(string problem)
        => "the kit was loaded but could not be recorded in this workspace, so it will not be there " +
           $"next time: {problem}";

    /// <summary>What <see cref="Commit"/> wrote and what it could not.</summary>
    /// <param name="RecordProblem">Why the <c>.cws</c> reference could not be written, or null.</param>
    /// <param name="PCells">The kit's parametric cells, or null when it ships none — the ordinary case.</param>
    /// <param name="PCellProblem">Why the kit's cell library could not be examined, or null.</param>
    public sealed record CommitResult(string? RecordProblem, PCellDeclaration? PCells, string? PCellProblem);

    /// <summary>The whole import, for a caller with no stages to show.</summary>
    /// <param name="Report">The importer's own findings — the import report.</param>
    /// <param name="Outcome">The installed parts, or null when installing threw (<see cref="InstallProblem"/>).</param>
    /// <param name="Commit">What the workspace recorded, or null when there was nothing to record.</param>
    public sealed record ImportResult(
        PdkImportReport Report,
        PdkPartInstaller.InstallOutcome? Outcome,
        string? InstallProblem,
        CommitResult? Commit);

    /// <summary>
    /// A referenced kit, read off disk on a workspace open. <see cref="Missing"/> means the folder is
    /// not there; <see cref="Refusal"/> is a reason it was deliberately not read.
    /// </summary>
    public readonly record struct ReferencedRead(
        string KitPath,
        PdkImportReport? Report,
        PdkPartInstaller.InstallOutcome? Outcome,
        string? Refusal,
        bool Missing);

    // ── the import ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The kit at <paramref name="kitPath"/>, read. A pure function of what is on disk — safe on a worker
    /// thread. <paramref name="kitName"/> replaces the name the kit's folder gives it.
    /// </summary>
    public static PdkImportReport Read(string kitPath, string? kitName = null)
        => PdkImporter.Import(kitPath, kitName);

    /// <summary>
    /// The report's parts, translated and held in memory, against the model libraries
    /// <paramref name="cwsPath"/>'s workspace references. Writes nothing and registers nothing — safe on
    /// a worker thread. A null <paramref name="cwsPath"/> installs as with no workspace open.
    /// </summary>
    public static PdkPartInstaller.InstallOutcome Install(PdkImportReport report, string? cwsPath)
        => PdkPartInstaller.Install(report, libraryRoots: LibraryRootsOf(cwsPath));

    /// <summary>
    /// Records an installed kit in its workspace: the <c>.cws</c> reference, and its parametric-cell
    /// library's declaration when it ships one. Never throws for either — a kit that loaded stays loaded
    /// when one of these could not be written, and the result says which.
    /// </summary>
    public static CommitResult Commit(string cwsPath, PdkImportReport report, PdkPartInstaller.InstallOutcome outcome)
    {
        string wsRoot = Path.GetDirectoryName(Path.GetFullPath(cwsPath))!;

        // Registered under the name the INSTALLER settled on, never the report's own: every part
        // reference was built from that one, and a report carrying no name falls back to a default.
        string? recordProblem = RecordReference(cwsPath, outcome.KitName, report.RootPath,
                                                outcome.Settings, outcome.CornerAxes);
        var (pcells, pcellProblem) = DeclarePCellLibrary(outcome.KitName, report.RootPath, wsRoot);
        return new CommitResult(recordProblem, pcells, pcellProblem);
    }

    /// <summary>
    /// <see cref="Read"/>, <see cref="Install"/> and <see cref="Commit"/>, in the order File ▸ Import PDK
    /// runs them. Nothing is committed for a report that could not be read at all.
    /// </summary>
    public static ImportResult Import(string kitPath, string cwsPath, string? kitName = null)
    {
        var report = Read(kitPath, kitName);
        if (report.Status == PdkImportStatus.Failed) return new ImportResult(report, null, null, null);

        PdkPartInstaller.InstallOutcome outcome;
        try { outcome = Install(report, cwsPath); }
        catch (Exception ex) { return new ImportResult(report, null, ex.Message, null); }

        return new ImportResult(report, outcome, null, Commit(cwsPath, report, outcome));
    }

    // ── what an import says ───────────────────────────────────────────────────────────────────────

    /// <summary>The import's tally — a count of what was and was not recognised, not a verdict.</summary>
    public static string TallySentence(PdkImportReport report)
        => $"{report.KitName}: " +
           $"{PdkPartInstaller.Plural(report.Parts.Count, "part", "parts")}, " +
           $"{PdkPartInstaller.Plural(report.Supported.Count(), "file", "files")} read, " +
           $"{report.KnownGaps.Count()} recognised but unsupported, " +
           $"{report.Unrecognized.Count()} unrecognised.";

    /// <summary>
    /// The LAST line of an import, and the one that answers the only question that matters: can anything
    /// be placed now? <c>Placeable</c> false is a warning, and the sentence carries its own reason, so "no
    /// parts" never has to be worked out from the lines above.
    /// </summary>
    public static (bool Placeable, string Sentence) PlaceabilitySentence(
        PdkImportReport report, PdkPartInstaller.InstallOutcome? outcome, bool workspaceOpen)
    {
        int placeable = outcome?.Items.Count ?? 0;

        if (placeable > 0)
        {
            string omitted = outcome!.OmittedNotPlaceable > 0
                ? $" {outcome.OmittedNotPlaceable} further part(s) have no symbol and are internal to the " +
                  "kit; they are listed in the import report but kept out of the palette."
                : "";
            return (true, $"\"{report.KitName}\": {placeable} part(s) available to place " +
                          $"from the Library palette " +
                          $"({PdkPartInstaller.Plural(outcome.IconsFound, "icon", "icons")}, " +
                          $"{outcome.SymbolsInstalled} symbol(s) installed).{omitted}");
        }

        string reason =
            outcome is null                       ? "the palette could not be updated"
            : !workspaceOpen                      ? "no workspace is open"
            : outcome.OmittedNotPlaceable > 0     ? "no part has a symbol circuitRF can read"
            : report.Parts.Count == 0             ? "the kit declares no parts"
            :                                       "no part could be installed";
        return (false, $"\"{report.KitName}\": no parts are available to place — {reason}. " +
                       "The import report lists what was found.");
    }

    // ── a workspace open ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads one referenced kit off disk. A pure function of what is on disk — touches no registry and
    /// writes no file, which is the only reason reading several kits at once is safe.
    ///
    /// <para>The recorded settings are handed back in, so the library discovery and variant choices are
    /// READ rather than re-derived — which is what keeps an open both fast and repeatable.</para>
    /// </summary>
    public static ReferencedRead ReadReferenced(
        CwsPdkRef r, string workspaceRootDir, IReadOnlyList<string> libraryRoots)
    {
        ArgumentNullException.ThrowIfNull(r);
        string kitPath = WorkspaceRefs.Resolve(r.Path, workspaceRootDir);
        if (!Directory.Exists(kitPath))
            return new ReferencedRead(kitPath, null, null, null, Missing: true);

        // A reader change moves pins, and wires attached to them silently disconnect. Refused and
        // reported rather than applied — the upgrade is the user's to ask for.
        if (r.TranslationVersion != 0 && r.TranslationVersion != DsnSymbolReader.TranslationVersion)
            return new ReferencedRead(kitPath, null, null,
                $"'{r.Provider}' was translated by an older reader (version {r.TranslationVersion}; " +
                $"this build uses {DsnSymbolReader.TranslationVersion}). It was NOT re-translated: " +
                $"pin positions could move and disconnect wires. Re-import it from File ▸ Manage Libraries " +
                $"when you are ready.",
                Missing: false);

        // The kit is read under the name the workspace RECORDED, which a placed part's reference
        // carries — a kit imported under its own name and one renamed on import read back the same.
        var report  = PdkImporter.Import(kitPath, r.Provider);
        var outcome = PdkPartInstaller.Install(report, r.Settings, libraryRoots);
        return new ReferencedRead(kitPath, report, outcome, null, Missing: false);
    }

    /// <summary>What <see cref="LoadReferenced"/> made available, and what it could not.</summary>
    /// <param name="Manifests">Each loaded kit's settled settings, for a device-provider resolver.</param>
    /// <param name="Loaded">The kits whose parts are now registered.</param>
    /// <param name="Problems">One sentence per kit that could not be loaded.</param>
    public sealed record LoadResult(
        IReadOnlyList<(string Kit, DeviceWorkerManifest Manifest)> Manifests,
        IReadOnlyList<string> Loaded,
        IReadOnlyList<string> Problems);

    /// <summary>
    /// Every kit <paramref name="workspaceRootDir"/>'s <c>.cws</c> references, read and its parts
    /// registered in <see cref="PdkKitRegistry"/> — what a workspace open does, for a caller with no
    /// window. <b>Writes nothing</b>: settings an open would record are only used, never stored, so a
    /// headless run leaves the workspace exactly as it found it.
    /// </summary>
    public static LoadResult LoadReferenced(string workspaceRootDir)
    {
        var manifests = new List<(string, DeviceWorkerManifest)>();
        var loaded    = new List<string>();
        var problems  = new List<string>();

        string cwsPath = Path.Combine(workspaceRootDir, WorkspacePersistence.FileName);
        if (!File.Exists(cwsPath)) return new LoadResult(manifests, loaded, problems);

        List<CwsPdkRef> refs;
        try { refs = WorkspacePersistence.LoadFromFile(cwsPath).PdkRefs ?? []; }
        catch (Exception ex)
        {
            problems.Add($"{cwsPath} could not be read, so none of its kits were loaded: {ex.Message}");
            return new LoadResult(manifests, loaded, problems);
        }

        var libraryRoots = LibraryRootsIn(workspaceRootDir, refs);
        foreach (var r in refs.Where(x => !x.IsLibraryOnly))
        {
            try
            {
                var read = ReadReferenced(r, workspaceRootDir, libraryRoots);
                if (read.Missing) { problems.Add($"'{r.Provider}': the kit folder '{read.KitPath}' does not exist."); continue; }
                if (read.Refusal is { } refusal) { problems.Add(refusal); continue; }

                var outcome = read.Outcome!;
                PdkKitRegistry.SetKit(workspaceRootDir, r.Provider, outcome.Parts ?? [], outcome.OsdiModels);
                if (PdkPartInstaller.ManifestFrom(outcome.Settings, read.KitPath, r.Provider) is { } m)
                    manifests.Add((r.Provider, m));
                loaded.Add(r.Provider);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"'{r.Provider}' could not be read: {ex.Message}");
            }
        }
        return new LoadResult(manifests, loaded, problems);
    }

    // ── what the workspace records ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a kit in <c>.cws</c>: where it is, and what circuitRF settled about it. Never its
    /// translated content — that is the vendor's, and is rebuilt on every open. Returns why it could not
    /// be written, or null.
    ///
    /// <para>Written immediately rather than at the next save, so a workspace that is closed without
    /// saving still remembers a kit that was just imported — the import is not an edit to a document the
    /// user might reasonably discard.</para>
    /// </summary>
    public static string? RecordReference(string cwsPath, string kitName, string kitPath,
                                          JsonNode? settings, IReadOnlyList<PdkCornerAxis>? corners)
    {
        try
        {
            string wsRoot = Path.GetDirectoryName(Path.GetFullPath(cwsPath))!;
            var cws  = File.Exists(cwsPath) ? WorkspacePersistence.LoadFromFile(cwsPath) : new CwsFile();
            var refs = cws.PdkRefs ?? [];
            // The corners this kit already had recorded, so a re-record that has not re-derived them
            // (a settings-only write on open) never silently drops them.
            var keptCorners = refs.FirstOrDefault(r =>
                string.Equals(r.Provider, kitName, StringComparison.OrdinalIgnoreCase))?.Corners;
            refs.RemoveAll(r => string.Equals(r.Provider, kitName, StringComparison.OrdinalIgnoreCase));
            refs.Add(new CwsPdkRef
            {
                Path               = WorkspaceRefs.ToStoredRef(kitPath, wsRoot),
                Provider           = kitName,
                TranslationVersion = DsnSymbolReader.TranslationVersion,
                Settings           = settings,
                Corners            = ToStoredCorners(corners) ?? keptCorners,
            });
            cws.PdkRefs = refs;
            return WorkspacePersistence.SaveToFileAtomic(cwsPath, cws)
                ? null
                : $"'{Path.GetDirectoryName(cwsPath)}' is read-only on this machine.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Makes a kit's own parametric cells reachable, if it has any: the package found structurally
    /// (<see cref="KitPCellLibrary"/>) and its declaration written into the WORKSPACE, where it is
    /// ordinary, editable text. <b>Writes a declaration; runs nothing</b> — whether the kit's code may run
    /// is decided by consent, later, by whoever starts the generators.
    ///
    /// <para>(null, null) when the kit has no cell library — the ordinary case for a kit whose layouts
    /// are fixed artwork — and never fatal: a declaration that could not be written costs that kit's
    /// layout cells, not the import.</para>
    /// </summary>
    public static (PCellDeclaration? Declaration, string? Problem) DeclarePCellLibrary(
        string kitName, string kitPath, string workspaceRootDir)
    {
        try
        {
            var pkg = KitPCellLibrary.Find(kitPath, out var alsoFound);
            if (pkg is null) return (null, null);

            // kitPath is passed so the declaration is anchored on the kit rather than written out as an
            // absolute path, so repairing a moved kit repairs its layout cells too.
            string? dir = KitPCellLibrary.EnsureDeclared(
                workspaceRootDir, kitName, pkg, out string? problem, out bool created, kitRoot: kitPath);
            return (new PCellDeclaration(pkg, alsoFound, dir, created, problem), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// The referenced model-library packages, resolved. Handed to discovery so a part kit can find the
    /// models even when it no longer sits beside them.
    /// </summary>
    public static IReadOnlyList<string> LibraryRootsIn(string workspaceRootDir, IEnumerable<CwsPdkRef> refs)
    {
        ArgumentNullException.ThrowIfNull(refs);

        return [.. refs.Where(r => r.IsLibraryOnly)
                       .Select(r => WorkspaceRefs.Resolve(r.Path, workspaceRootDir))
                       .Where(Directory.Exists)];
    }

    /// <summary>
    /// The recorded form of a kit's corner axes, or null when it declares none — which is the
    /// overwhelming majority of kits, and the case whose <c>.cws</c> must stay byte-identical.
    /// </summary>
    public static List<CwsCornerAxis>? ToStoredCorners(IReadOnlyList<PdkCornerAxis>? axes)
        => axes is null || axes.Count == 0
            ? null
            : [.. axes.Select(a => new CwsCornerAxis
              {
                  AxisId      = a.AxisId,
                  DisplayName = a.DisplayName,
                  Options     = [.. a.Options],
              })];

    private static IReadOnlyList<string> LibraryRootsOf(string? cwsPath)
    {
        if (cwsPath is null || !File.Exists(cwsPath)) return [];
        try
        {
            return LibraryRootsIn(Path.GetDirectoryName(Path.GetFullPath(cwsPath))!,
                                  WorkspacePersistence.LoadFromFile(cwsPath).PdkRefs ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
