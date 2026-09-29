using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Revision;

namespace CircuitRF.Ui.Archive;

/// <summary>
/// What kind of thing an optional archive entry is — the three branches the Archive Workspace dialog
/// shows, and the three the writer treats differently.
/// </summary>
public enum ArchiveOptionKind
{
    /// <summary>A referenced kit FOLDER (a <c>.cws</c> LibraryRef or PdkRef pointing outside the workspace).</summary>
    Kit,

    /// <summary>A file referenced from inside the workspace but living outside it — a Known File, a
    /// bitmap underlay, a Touchstone a component names.</summary>
    ExternalFile,

    /// <summary>A file under the workspace's own <c>results/</c> folder.</summary>
    Result,

    /// <summary>
    /// Cells this workspace instances BY REFERENCE out of another workspace (MW2 R-mw2-16), with the
    /// sub-cells they instantiate and enough of that workspace's spine for the alias to keep
    /// resolving. Ticked by default: unlike a kit, this is the user's own design content, and an
    /// archive that arrives with its instances drawing placeholders is the failure the row exists to
    /// prevent.
    /// </summary>
    ReferencedWorkspace,

    /// <summary>
    /// The workspace's own <c>.generated-cells</c> — only the cells some layout still references
    /// (brief generated-cells-1). Ticked by default: a recipient without the kit, and every
    /// command-line run, cannot rebuild them, so for them this is the only copy of the artwork.
    /// </summary>
    GeneratedCells,
}

/// <summary>
/// One file inside a group row, and where it lands relative to that row's own folder in the archive.
/// </summary>
/// <param name="SourcePath">Absolute path on this machine.</param>
/// <param name="RelativePath">
/// '/'-separated, relative to the row's <see cref="ArchiveOption.ArchivePath"/>. <b>This is the
/// deliverable</b> for a SPICE deck: the copy preserves the relative offsets the original was written
/// against, so every <c>.include</c> inside it resolves after the copy exactly as it did before, and
/// repointing has to rewrite only the entry point.
/// </param>
public sealed record ArchiveMember(string SourcePath, string RelativePath);

/// <summary>
/// One thing the user can tick in the Archive Workspace dialog.
///
/// <para>An option is never <i>required</i> — everything a workspace cannot be read without (its
/// cells, its <c>.cws</c>, its technologies, every loose file beside them) is archived
/// unconditionally and never appears here. This list is only the material whose bulk or provenance
/// makes it a judgement call.</para>
/// </summary>
public sealed class ArchiveOption
{
    public required ArchiveOptionKind Kind { get; init; }

    /// <summary>
    /// What the dialog's row is CALLED — a kit's own name, a file's name. Never a full path: the
    /// tree trims a long row from the END, which is precisely the half of a path that identifies it.
    /// The path goes in <see cref="Detail"/>, where the tooltip shows it in full.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>The row's tooltip — where the thing actually is. Falls back to <see cref="SourcePath"/>.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Absolute path of the file or folder to copy.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Path INSIDE the zip, always '/'-separated and always relative to the archive root.</summary>
    public required string ArchivePath { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>
    /// For a directory row, the EXACT files it copies — empty meaning "everything under
    /// <see cref="SourcePath"/>", which is what a kit folder is.
    ///
    /// <para>A SPICE deck's row is a subtree rooted at the deepest common ancestor of its include
    /// closure, and that folder routinely holds a great deal the deck never reads. Copying the folder
    /// would archive a whole model directory to carry three files out of it.</para>
    /// </summary>
    public IReadOnlyList<ArchiveMember> Members { get; init; } = [];

    /// <summary>
    /// Size in bytes, or -1 when not measured yet. A kit folder is measured lazily (walking a vendor
    /// kit of tens of thousands of files must not hold up the dialog); a plain file is measured on
    /// the spot, which costs one <c>FileInfo</c>.
    /// </summary>
    public long SizeBytes { get; set; } = -1;

    /// <summary>Ticked when the dialog opens. See <see cref="WorkspaceArchiveScanner"/> for the defaults.</summary>
    public bool Selected { get; set; }

    /// <summary>
    /// Group heading for a <see cref="ArchiveOptionKind.Result"/> — "Data Displays", "Touchstone",
    /// "Analysis", or "Other". Empty for the other kinds, which are their own branch.
    /// </summary>
    public string Group { get; init; } = "";

    /// <summary>Stable identity for tests and for the writer's own bookkeeping.</summary>
    public string Id => $"{Kind}:{ArchivePath}";
}

/// <summary>
/// Everything <c>Archive Workspace…</c> needs to know about one workspace: what it will always
/// include, and what it is asking the user about.
/// </summary>
public sealed class WorkspaceArchivePlan
{
    /// <summary>Absolute workspace root (the folder holding <c>.cws</c>).</summary>
    public required string WorkspaceDir { get; init; }

    /// <summary>
    /// Workspace-relative paths ('/'-separated) archived unconditionally — the <c>.cws</c>, every
    /// cell, every technology, every loose file that is not under <c>results/</c>. The owner's rule:
    /// "It is assumed that all the cells will be archived. The user expects that."
    /// </summary>
    public List<string> AlwaysIncluded { get; init; } = [];

    /// <summary>The optional material, in dialog order.</summary>
    public List<ArchiveOption> Options { get; init; } = [];

    public IEnumerable<ArchiveOption> Kits          => Options.Where(o => o.Kind == ArchiveOptionKind.Kit);
    public IEnumerable<ArchiveOption> ExternalFiles => Options.Where(o => o.Kind == ArchiveOptionKind.ExternalFile);
    public IEnumerable<ArchiveOption> Results       => Options.Where(o => o.Kind == ArchiveOptionKind.Result);
    public IEnumerable<ArchiveOption> ReferencedWorkspaces =>
        Options.Where(o => o.Kind == ArchiveOptionKind.ReferencedWorkspace);

    /// <summary>The one generated-cells row, or null when the workspace has no live generated cells.</summary>
    public ArchiveOption? GeneratedCells => Options.FirstOrDefault(o => o.Kind == ArchiveOptionKind.GeneratedCells);

    /// <summary>How many of the offered generated cells a KIT's script drew rather than a built-in
    /// generator — the ones a recipient without that kit cannot redraw.</summary>
    public int KitGeneratedCellCount { get; set; }

    /// <summary>True when some layout could not be read, so the generated-cells row carries every
    /// cell rather than only the live ones.</summary>
    public bool GeneratedCellsLiveSetIncomplete { get; set; }

    /// <summary>
    /// The sentence the dialog and the log show when the generated artwork is unticked while kit
    /// cells are placed and a referenced kit is not travelling either — null otherwise.
    ///
    /// <para>Which kit drew which cell is not known here (a kit's generator ids come from running its
    /// scripts), so "a referenced kit is left out" is the test. A kit that lives inside the workspace
    /// has no row and always travels.</para>
    /// </summary>
    public string? GeneratedCellsWarning =>
        GeneratedCells is { Selected: false } && KitGeneratedCellCount > 0 && Kits.Any(k => !k.Selected)
            ? $"The generated PCell artwork is not included, and {KitGeneratedCellCount} placed kit cell(s) "
            + "need a kit this archive does not include — unless the recipient has that kit, they will see "
            + "empty placeholders for those parts, and a command-line run will see nothing at all."
            : null;

    /// <summary>Bytes of the always-included material, measured during the scan.</summary>
    public long AlwaysIncludedBytes { get; set; }

    /// <summary>Paths the scan deliberately left out, for the message log — temp and OS clutter.</summary>
    public List<string> SkippedPaths { get; init; } = [];

    // ── The history (RC-8, docs/design/revision-control.md §9A) ───────────────

    /// <summary>
    /// Whether this workspace has a history of its own to offer. False on a workspace circuitRF has
    /// never recorded, and on one sitting inside somebody else's version-controlled folder — where
    /// there is no repository at the workspace root and R-rc0-5 means circuitRF would not carry that
    /// one anyway.
    /// </summary>
    public bool HistoryAvailable { get; set; }

    /// <summary>
    /// <b>Whether the history travels — off by default, and the default is a safety property rather
    /// than a preference</b> (R-rc8-1, R-rc8-2, §9A.1).
    ///
    /// <para>Every other failure the architecture guards against is a LOSS: a restore point not taken,
    /// one thinned early, a history switched off. Those are bad, and the design itself is ordinary
    /// files on disk and survives all of them. <b>Including a history in an archive risks the opposite
    /// kind of failure, and it is unrecoverable.</b> The history holds every earlier version of every
    /// file kept — including files that are no longer in the workspace at all — so a designer who
    /// imported one customer's artwork, finished with it, deleted it, and then archived the workspace
    /// for a DIFFERENT customer would ship that artwork. Nothing in the visible file tree would show
    /// it, and nobody would find out until someone else did.</para>
    ///
    /// <para><b>When a default can be wrong in two directions, it points away from the irreversible
    /// one.</b> That is the whole of it — and including a history is otherwise genuinely the better
    /// handover (R-rc8-3). The point is only that it must be chosen, not inherited.</para>
    ///
    /// <para><b>There is deliberately no third state</b> (R-rc8-11, §9A.4). "The last N versions" and
    /// "from this date forward" are history rewriting, and worse, they invite exactly the wrong
    /// belief: a user who chose them to avoid sending something will assume the something is gone. A
    /// guarantee that is nearly true is worse than no guarantee, because the near-truth is what people
    /// act on.</para>
    /// </summary>
    public bool IncludeHistory { get; set; }

    /// <summary>
    /// What including it adds, computed once the box is ticked — never before, because computing it
    /// means packing the repository (R-rc8-9) and an archive that leaves history out must cost exactly
    /// what it costs today.
    /// </summary>
    public HistoryArchiveSummary? History { get; set; }

    /// <summary>Sum of what is currently ticked, plus the unconditional material — and the history
    /// when it is travelling, whose figure is post-pack (R-rc8-9) and is therefore the one the archive
    /// actually spends.</summary>
    public long SelectedBytes =>
        AlwaysIncludedBytes
      + Options.Where(o => o.Selected && o.SizeBytes > 0).Sum(o => o.SizeBytes)
      + (IncludeHistory ? History?.Bytes ?? 0 : 0);

    /// <summary>Human-readable size, the way a file manager writes one.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "…";
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = bytes / 1024.0;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v >= 100 ? $"{v:0} {units[u]}" : $"{v:0.0} {units[u]}";
    }
}
