using CircuitRF.Design.Workspace;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  WorkspaceRenameRepoint — the references a workspace rename invalidates, and the
//  ones it can repair.
//
//  A workspace's name IS its folder name: `.cws` is a fixed filename and CwsFile
//  carries no name field. So renaming one is `Directory.Move`, and nothing inside it
//  notices — every intra-workspace reference (a CellRef, a layout's or a 3D view's
//  TechRef, a .ccell's primaries, the .cws's own OpenDocuments) is relative and travels
//  with the folder. A .c3d is therefore not read here, and needs no row.
//
//  What DOES notice is another workspace pointing IN. Four fields can, and all four
//  live in that other workspace's own `.cws`:
//     ReferencedWorkspaces[].Path   the alias table's target .cws
//     LibraryRefs[]                 a library folder
//     KnownFiles[]                  a bookmarked file or folder
//     PdkRefs[].Path                a kit folder
//  Each is stored workspace-relative when it is inside that workspace and absolute
//  otherwise (WorkspaceRefs.ToStoredRef), and BOTH forms break on a rename: relative
//  because two sibling projects address each other through the renamed segment,
//  absolute because the absolute path moved.
//
//  The ALIAS itself does not break, which is the part worth knowing: CwsWorkspaceRef
//  .Alias merely defaults to the folder name when the reference is created and is
//  independent of it thereafter, so every `ws://alias/…` reference inside the other
//  workspace's DOCUMENTS keeps resolving once the one Path line here is repaired.
//  That is exactly what CwsFile.ReferencedWorkspaces' own doc comment promises
//  ("relocating the other project is one edit here").
//
//  Rewriting another open workspace's `.cws` from this process is safe because
//  WriteWorkspaceFile re-reads the file before every save — its own comment says so:
//  "Load existing .cws to preserve KnownFiles + LibraryRefs (authoritative on disk)."
//  A workspace nobody has open cannot be reached at all, which the caller has to SAY
//  rather than implying a clean rename.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>How many stored references in one <c>.cws</c> were repointed, per field.</summary>
public readonly record struct WorkspaceRepointResult(
    int Aliases, int Libraries, int KnownFiles, int Kits)
{
    public int Total => Aliases + Libraries + KnownFiles + Kits;
}

public static class WorkspaceRenameRepoint
{
    /// <summary>
    /// True when <paramref name="otherWorkspaceRoot"/>'s <c>.cws</c> holds at least one reference
    /// that resolves into <paramref name="targetRoot"/>. Asked BEFORE the rename, to name the
    /// workspaces in the confirmation.
    /// </summary>
    public static bool References(string otherWorkspaceRoot, string targetRoot)
        => Rewrite(otherWorkspaceRoot, targetRoot, newRoot: null, apply: false).Total > 0;

    /// <summary>
    /// Repoints every reference in <paramref name="otherWorkspaceRoot"/>'s <c>.cws</c> that resolved
    /// into <paramref name="oldRoot"/> so it names <paramref name="newRoot"/> instead, and saves.
    /// Returns what was changed; a result of zero means the file was not written at all.
    /// </summary>
    public static WorkspaceRepointResult Repoint(string otherWorkspaceRoot, string oldRoot, string newRoot)
        => Rewrite(otherWorkspaceRoot, oldRoot, newRoot, apply: true);

    /// <param name="newRoot">Null COUNTS matches without producing a replacement — what
    /// <see cref="References"/> asks. Passing the old root as the new one would count nothing, since
    /// every rewrite would come back byte-identical.</param>
    private static WorkspaceRepointResult Rewrite(
        string otherWorkspaceRoot, string oldRoot, string? newRoot, bool apply)
    {
        string cwsPath = Path.Combine(otherWorkspaceRoot, ".cws");
        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch { return default; }

        int aliases = 0, libs = 0, known = 0, kits = 0;

        if (cws.ReferencedWorkspaces is { } refs)
            foreach (var r in refs)
                if (Repointed(r.Path, otherWorkspaceRoot, oldRoot, newRoot, out var p))
                { if (p is not null) r.Path = p; aliases++; }

        if (cws.LibraryRefs is { Count: > 0 })
            for (int i = 0; i < cws.LibraryRefs.Count; i++)
                if (Repointed(cws.LibraryRefs[i], otherWorkspaceRoot, oldRoot, newRoot, out var p))
                { if (p is not null) cws.LibraryRefs[i] = p; libs++; }

        if (cws.KnownFiles is { Count: > 0 })
            for (int i = 0; i < cws.KnownFiles.Count; i++)
                if (Repointed(cws.KnownFiles[i], otherWorkspaceRoot, oldRoot, newRoot, out var p))
                { if (p is not null) cws.KnownFiles[i] = p; known++; }

        if (cws.PdkRefs is { } pdks)
            foreach (var k in pdks)
                if (Repointed(k.Path, otherWorkspaceRoot, oldRoot, newRoot, out var p))
                { if (p is not null) k.Path = p; kits++; }

        var result = new WorkspaceRepointResult(aliases, libs, known, kits);
        if (apply && result.Total > 0)
        {
            try { WorkspacePersistence.SaveToFileAtomic(cwsPath, cws); }
            catch { return default; }
        }
        return result;
    }

    /// <summary>
    /// True when this reference resolves into <paramref name="oldRoot"/> — that is, when the rename
    /// breaks it.
    ///
    /// <para>A <c>${NAME}</c>-tokenised ref (SL1 R-sl1-6) is deliberately neither counted nor
    /// rewritten: the token is a machine's own answer to where a shared tree lives, and substituting
    /// an absolute path for it would silently un-share the reference on every other machine.</para>
    /// </summary>
    /// <param name="replacement">The new stored form, or null when <paramref name="newRoot"/> was
    /// null (counting only) or the form is unchanged.</param>
    private static bool Repointed(
        string? storedRef, string owningRoot, string oldRoot, string? newRoot, out string? replacement)
    {
        replacement = null;
        if (storedRef is not { Length: > 0 }) return false;
        if (storedRef.Contains("${", StringComparison.Ordinal)) return false;

        string abs;
        try   { abs = WorkspaceRefs.Resolve(storedRef, owningRoot); }
        catch { return false; }

        string old = Normalize(oldRoot);
        string cur = Normalize(abs);

        string tail;
        if (string.Equals(cur, old, StringComparison.OrdinalIgnoreCase))
            tail = "";
        else if (cur.StartsWith(old + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            tail = cur[(old.Length + 1)..];
        else
            return false;

        if (newRoot is null) return true;

        string moved = tail.Length == 0 ? newRoot : Path.Combine(newRoot, tail);

        // Re-stored through the same rule that wrote it: relative while it is inside the workspace
        // that holds the reference, absolute otherwise. A rename can cross that boundary in principle
        // (a workspace nested inside another), so the form is re-decided rather than patched.
        string restored = WorkspaceRefs.ToStoredRef(moved, owningRoot);
        if (!string.Equals(restored, storedRef, StringComparison.Ordinal)) replacement = restored;
        return true;
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
