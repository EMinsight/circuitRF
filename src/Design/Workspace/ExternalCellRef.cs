using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Workspace;

/// <summary>
/// The <c>ws://</c> cell-reference scheme — a cell in ANOTHER workspace, addressed by an alias the
/// referencing document's own <c>.cws</c> resolves (MW2 R-mw2-2/-3/-4).
///
/// <code>
/// CellRef:   ws://RfFrontEnd/cells/Amp
/// .cws:      ReferencedWorkspaces: [ { Alias: "RfFrontEnd", Path: "../rf-front-end/.cws" } ]
/// </code>
///
/// <para><b>Why an alias rather than the raw <c>../../Other/cells/Amp</c> the path machinery already
/// produces.</b> <c>workspace-and-project-tree.md</c> §5A R37 names this exact decision and warns
/// against answering it by accident. Five reasons, in the order they matter: relocating the other
/// project is one <c>.cws</c> edit instead of a rewrite of every document that referenced it; it
/// reuses the shape <c>pdk://</c> already has, and a reference that states its own kind cannot be
/// mistaken for a typo, which is what lets a repair flow say anything useful; it NAMES the other
/// workspace, which the technology check and the kit walk-up both need and which a raw path can only
/// infer — an inference that fails silently when the path is stale; the <c>.cws</c> already has the
/// slot beside its referenced libraries; and the Project Tree already knows how to draw a referenced
/// sub-tree and mark an unresolvable one.</para>
///
/// <para><b>A raw <c>../../Other/cells/Amp</c> is not blessed and not rejected</b> (R-mw2-5). It
/// resolves today by accident — every producing site writes <c>Path.GetRelativePath</c> and every
/// reading site does a plain <c>Path.Combine</c> — and removing that would break the LIBRARY case,
/// which legitimately points outside the workspace. So it goes on resolving, nothing here ever
/// writes one, and it is not a documented feature.</para>
///
/// <para><b>Every path-shaped cell reference resolves through <see cref="ResolveCellDir"/>.</b> The
/// two forms differ only in how the absolute cell folder is arrived at, and a call site that splits
/// them itself is a call site that will be missed when a third form arrives — which is the trap
/// <c>CellSymbolResolver.NeedsNoBaseDirectory</c> already records for the virtual forms.</para>
/// </summary>
public static class ExternalCellRef
{
    /// <summary>Marks a cell reference as addressed through a workspace alias.</summary>
    public const string Scheme = "ws://";

    /// <summary>True when this reference names a cell in a referenced workspace.</summary>
    public static bool IsExternalRef(string? cellRef) =>
        cellRef is not null && cellRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits <c>ws://alias/rel/path</c> into its alias and the workspace-relative remainder. The
    /// alias runs to the FIRST separator and the path takes the rest — the same split
    /// <c>PdkKitRegistry.TryParse</c> makes, and for the same reason: the remainder is the other
    /// workspace's own spelling and circuitRF does not get to constrain it.
    /// </summary>
    public static bool TryParse(string? cellRef, out string alias, out string workspaceRelPath)
    {
        alias = workspaceRelPath = "";
        if (!IsExternalRef(cellRef)) return false;

        string rest = cellRef![Scheme.Length..];
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;

        alias            = rest[..slash];
        workspaceRelPath = rest[(slash + 1)..];
        return true;
    }

    /// <summary>The stored form for one cell of one referenced workspace. Separators are <c>/</c>,
    /// the same convention <c>WorkspaceRefs.ToStoredRef</c> normalises to and for the same
    /// reason — it only fails when a workspace crosses platforms, i.e. never on the machine that
    /// wrote it.</summary>
    public static string RefFor(string alias, string workspaceRelPath) =>
        $"{Scheme}{alias}/{workspaceRelPath.Replace('\\', '/').TrimStart('/')}";

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// The absolute cell folder <paramref name="cellRef"/> names, for EITHER form — null when it
    /// cannot be worked out at all (no base directory for a relative reference, an alias the
    /// referencing workspace does not declare, a malformed path).
    ///
    /// <para><b>A reference that resolves to nothing is retried through the owning root's
    /// <c>.cmoves</c></b> (TM2 R-tm2-8). A reference that still resolves to nothing after that comes
    /// back spelling the path the user actually wrote, so <c>NotFound</c> names the place the file
    /// names — the existing reported and repairable state, unchanged.</para>
    /// </summary>
    public static string? ResolveCellDir(string? cellRef, string? baseDir)
        => ResolveCellDir(cellRef, baseDir, out _);

    /// <summary>
    /// The same answer, and — when it was reached through a forwarding record — WHICH record
    /// (TM2 R-tm2-11). The cell resolves, the symbol is right and the drawing is right; what the
    /// caller needs on top of that is that <b>the file on disk says something that is no longer
    /// true</b>, which is not something resolution may swallow.
    ///
    /// <para><b>The order is not negotiable</b> (R-tm2-8): resolve as before; if the folder EXISTS
    /// that is the answer and no record is read; only then consult <c>.cmoves</c>. Existence before
    /// redirect is what makes the mechanism safe when a NEW cell is later created at the old path —
    /// the new cell wins, the redirect never fires, and the reference means what it says.</para>
    ///
    /// <para><b>The redirect lives here and nowhere else</b>, which is why <c>src/Cli</c> gets it for
    /// free: this type's own rule is that a call site which splits the reference forms itself is a
    /// call site that will be missed, and a second resolution rule for moved cells would be exactly
    /// that mistake with a new name.</para>
    /// </summary>
    public static string? ResolveCellDir(string? cellRef, string? baseDir, out MoveRedirectHit? redirect)
        => ResolveCellDir(cellRef, baseDir, out redirect, out _);

    /// <summary>
    /// The same answer again, and whether the folder it names is THERE.
    ///
    /// <para><b>This exists so the existence question is asked once per resolution rather than
    /// twice.</b> Step 2 of the order above has to ask it, and every caller that cares was asking it
    /// again immediately afterwards — so handing the answer back is what keeps TM2 free on the common
    /// path rather than one round trip per referenced component per edit dearer (SL4 R-sl4-6's
    /// measurement, which is a gate). A caller that does not care keeps the shorter overload.</para>
    ///
    /// <para>True whenever the returned folder exists — including when it was reached THROUGH a
    /// forwarding record, since a redirect only ever accepts a candidate that is actually there.</para>
    /// </summary>
    public static string? ResolveCellDir(
        string? cellRef, string? baseDir, out MoveRedirectHit? redirect, out bool folderExists)
        => ResolveCellDir(cellRef, baseDir, null, out redirect, out folderExists);

    /// <summary>
    /// The same answer, with one more step for a LOOSE document: when the reference names nothing
    /// even after <c>.cmoves</c>, it is looked for in <paramref name="hostWorkspaceRoot"/> — the
    /// workspace the document is being worked on and run from (<see cref="FindInHostWorkspace"/>).
    ///
    /// <para><b>Last, never first.</b> A path that resolves is what the file says, and it wins even
    /// when the host also holds a cell of that name; a run says so (<c>NetExtractor</c>'s note). The
    /// host is only asked about a reference that would otherwise be Not Found.</para>
    /// </summary>
    public static string? ResolveCellDir(
        string? cellRef, string? baseDir, string? hostWorkspaceRoot,
        out MoveRedirectHit? redirect, out bool folderExists)
    {
        redirect = null;
        folderExists = false;
        if (string.IsNullOrEmpty(cellRef)) return null;

        string? direct;
        if (IsExternalRef(cellRef))
        {
            direct = ResolveExternal(cellRef!, baseDir);
        }
        else
        {
            if (baseDir is null) return null;
            try { direct = CircuitRF.Core.RefPath.Resolve(baseDir, cellRef); }
            catch { return null; }
        }

        if (direct is null) return null;

        // Step 2. The common path, and the ONLY filesystem question this adds — asked through
        // CellStat so it is counted like every other one the resolution path makes, and cached on the
        // same terms, which is what keeps the caller's own existence check a cache hit rather than a
        // second round trip (SL4 R-sl4-6/-7, and TM2 gate 8).
        if (CellStat.DirectoryExists(direct, cache: true)) { folderExists = true; return direct; }

        // Step 3, and only now.
        if (MoveRedirects.Resolve(direct) is not { } hit)
        {
            // Step 4: a loose document's host workspace, for a reference nothing else answered.
            if (FindInHostWorkspace(cellRef, baseDir, hostWorkspaceRoot)?.CellDir is { } hosted)
            {
                folderExists = true;
                return hosted;
            }
            return direct;
        }

        redirect     = hit;
        folderExists = true;
        return hit.ResolvedDir;
    }

    // ── A loose document's host workspace ─────────────────────────────────────

    /// <summary>What <see cref="FindInHostWorkspace"/> found: the one cell it would use, or null when
    /// several cells share the name; <see cref="Candidates"/> lists every one it considered.</summary>
    public sealed record HostCellMatch(string? CellDir, IReadOnlyList<string> Candidates);

    /// <summary>
    /// The cell <paramref name="cellRef"/> names in <paramref name="hostWorkspaceRoot"/>, for a
    /// document that belongs to NO workspace of its own — a Known File kept outside the workspace
    /// folder. Null when the document has a workspace of its own (its references are never
    /// re-read against another), when there is no host, or when the host has no such cell.
    ///
    /// <para><b>Why a loose document needs this.</b> A cell reference is relative to the document
    /// holding it, and one written from inside a cell reads <c>../../FET</c>. In a loose file that
    /// path names nothing, so every instance drew Not Found even though the workspace the file was
    /// opened in has a cell called <c>FET</c>.</para>
    ///
    /// <para><b>Two steps, and the first is the common case.</b> The leading <c>../</c> segments are
    /// dropped and the rest is read against the host root, which is where a reference written from
    /// a cell at the root points (<c>../../footprints/C0402</c> → <c>footprints/C0402</c>). Failing
    /// that, the host is searched by cell NAME through <see cref="CellLookup"/>. Two cells of that
    /// name are an AMBIGUITY, reported with no cell chosen: picking the first would run a design
    /// against a cell nobody picked.</para>
    ///
    /// <para><b>The answer depends on the host</b>, so one loose file run from two workspaces can use
    /// two different cells. That is the point of it, and a run says which cell it used.</para>
    /// </summary>
    public static HostCellMatch? FindInHostWorkspace(string? cellRef, string? baseDir, string? hostWorkspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(hostWorkspaceRoot) || string.IsNullOrEmpty(cellRef) || baseDir is null)
            return null;
        // A ws:// reference names its workspace, a virtual one is not a path, and a rooted one names
        // one place on purpose: none of them is re-read against the host.
        if (cellRef.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(cellRef)) return null;
        if (WorkspaceRootFinder.WorkspaceDirOf(baseDir) is not null) return null;

        string root = WorkspaceRootFinder.Normalize(hostWorkspaceRoot);
        string[] rest = [.. cellRef.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                                   .SkipWhile(seg => seg is "." or "..")];
        if (rest.Length == 0) return null;

        try
        {
            string anchored = Path.GetFullPath(Path.Combine([root, .. rest]));
            if (!WorkspaceRootFinder.IsOutside(anchored, root) && CellLookup.LooksLikeCellFolder(anchored))
                return new HostCellMatch(anchored, [anchored]);
        }
        catch { /* a name the OS will not parse: fall through to the name search */ }

        var found = CellsNamed(root, rest[^1]);
        return found.Count switch
        {
            0 => null,
            1 => new HostCellMatch(found[0], found),
            _ => new HostCellMatch(null, found),
        };
    }

    /// <summary><see cref="CellLookup.Find"/>, memoised: it walks the host's tree, and a loose
    /// document's references are resolved on every edit. Bounded by <see cref="CellStat.Freshness"/>
    /// so a cell created a moment ago is found, and dropped with every other memo here.</summary>
    private static IReadOnlyList<string> CellsNamed(string root, string name)
    {
        var key = (root, name.ToLowerInvariant());
        lock (_memoGate)
            if (_namedMemo.TryGetValue(key, out var memo) && DateTime.UtcNow - memo.Taken <= CellStat.Freshness)
                return memo.Found;

        var found = CellLookup.Find(root, name);
        lock (_memoGate) _namedMemo[key] = (DateTime.UtcNow, found);
        return found;
    }

    private static readonly Dictionary<(string Root, string Name), (DateTime Taken, IReadOnlyList<string> Found)>
        _namedMemo = [];

    /// <summary>
    /// The workspace ROOT a <c>ws://</c> reference's alias resolves to — the directory holding the
    /// other <c>.cws</c> — or null when the alias is not declared by the workspace
    /// <paramref name="baseDir"/> belongs to. This is what §3's technology check and §5's marking
    /// both ask, and it is the reason the alias names the workspace explicitly.
    /// </summary>
    public static string? ResolveAliasWorkspaceRoot(string? cellRef, string? baseDir)
    {
        if (!TryParse(cellRef, out string alias, out _)) return null;
        return WorkspaceRootForAlias(WorkspaceRootFinder.WorkspaceDirOf(baseDir), alias);
    }

    /// <summary>
    /// The workspace root <paramref name="alias"/> names, as declared by the <c>.cws</c> in
    /// <paramref name="referencingWorkspaceRoot"/>. Null when the workspace declares no such alias,
    /// or when the alias's stored path does not name an existing <c>.cws</c>.
    /// </summary>
    public static string? WorkspaceRootForAlias(string? referencingWorkspaceRoot, string alias)
    {
        if (string.IsNullOrEmpty(referencingWorkspaceRoot) || string.IsNullOrEmpty(alias)) return null;
        return AliasMapFor(referencingWorkspaceRoot!).GetValueOrDefault(alias);
    }

    private static string? ResolveExternal(string cellRef, string? baseDir)
    {
        if (!TryParse(cellRef, out string alias, out string rel)) return null;

        string? otherRoot = WorkspaceRootForAlias(WorkspaceRootFinder.WorkspaceDirOf(baseDir), alias);
        if (otherRoot is null) return null;

        try
        {
            return CircuitRF.Core.RefPath.Resolve(otherRoot, rel);
        }
        catch { return null; }
    }

    // ── Producing one ─────────────────────────────────────────────────────────

    /// <summary>
    /// The stored reference for <paramref name="cellAbsDir"/> as written into a document in
    /// <paramref name="baseDir"/>: <c>ws://alias/…</c> when the cell lives inside a workspace
    /// <paramref name="baseDir"/>'s own workspace REFERENCES, and the ordinary relative path
    /// otherwise.
    ///
    /// <para>Every producing site routes through here rather than calling
    /// <c>Path.GetRelativePath</c> itself, so the alias form is written in exactly the case it is
    /// meant for and nowhere else. A cell in a referenced LIBRARY keeps its relative path: a library
    /// is not a workspace, it brings no technology and no kit set, and rewriting those references
    /// would be the second convention R37 warns against arriving by a different door.</para>
    /// </summary>
    public static string MakeCellRef(string baseDir, string cellAbsDir)
    {
        string? ownRoot = WorkspaceRootFinder.WorkspaceDirOf(baseDir);
        string abs;
        try { abs = Path.GetFullPath(cellAbsDir); }
        catch { return cellAbsDir; }

        if (ownRoot is not null && WorkspaceRootFinder.IsOutside(abs, ownRoot))
        {
            // The DEEPEST matching referenced workspace wins, and the tie is broken by alias so the
            // answer never depends on dictionary order. Two referenced workspaces can nest (a project
            // inside a delivery folder that is itself a workspace), and the innermost is the one the
            // cell actually belongs to — the same "nearest ancestor" rule the .cws walk-up uses.
            (string Alias, string Root)? best = null;
            foreach (var (alias, otherRoot) in AliasMapFor(ownRoot))
            {
                if (otherRoot is null || WorkspaceRootFinder.IsOutside(abs, otherRoot)) continue;
                if (best is null
                    || otherRoot.Length > best.Value.Root.Length
                    || (otherRoot.Length == best.Value.Root.Length
                        && string.CompareOrdinal(alias, best.Value.Alias) < 0))
                    best = (alias, otherRoot);
            }
            if (best is { } hit)
                return RefFor(hit.Alias, Path.GetRelativePath(hit.Root, abs));
        }

        try { return Path.GetRelativePath(baseDir, abs); }
        catch { return cellAbsDir; }
    }

    // ── The alias table, memoised ─────────────────────────────────────────────
    //
    // Asked per cell instance per render, exactly as the workspace walk-up above it is (R-mw1-6),
    // so it is memoised on the same terms and dropped at the same moments — WorkspaceRootFinder's
    // InvalidateCache clears both, because a .cws appearing, disappearing or being rewritten changes
    // the answer to both questions and nothing else would notice.

    private static readonly Dictionary<string, Dictionary<string, string?>> _aliasMemo =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _memoGate = new();

    /// <summary>Alias → other workspace ROOT (null when the alias's stored path does not resolve to
    /// an existing <c>.cws</c>, which is R-mw2-11's "broken" state rather than an absent alias).</summary>
    internal static Dictionary<string, string?> AliasMapFor(string workspaceRoot)
    {
        string key = WorkspaceRootFinder.Normalize(workspaceRoot);

        lock (_memoGate)
            if (_aliasMemo.TryGetValue(key, out var memo)) return memo;

        var map = ReadAliasMap(key);

        lock (_memoGate) _aliasMemo[key] = map;
        return map;
    }

    private static Dictionary<string, string?> ReadAliasMap(string workspaceRoot)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(workspaceRoot)) return map;

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(Path.Combine(workspaceRoot, ".cws")); }
        catch { return map; }   // no .cws, or one this build cannot read — no aliases, not an error

        foreach (var entry in cws.ReferencedWorkspaces ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Alias) || map.ContainsKey(entry.Alias)) continue;

            string? root = ResolveOtherRoot(workspaceRoot, entry.Path);

            // RC-9 R-rc9-8/-15/-16: a PINNED alias resolves to the version this design was verified
            // against, not to whatever that workspace contains today. That is the whole feature — with
            // an unpinned reference, editing a cell in the library and coming back shows the new cell;
            // with a pinned one it deliberately does not.
            //
            // WHEN IT CANNOT BE HONOURED THE ALIAS RESOLVES TO NULL, which is the same state a moved
            // or deleted project produces: cells drawn as Not Found, repairable, reported by
            // WorkspacePins.Survey with the reason. Falling back to the current content would be the
            // silent wrong answer the pin exists to prevent, and it would be invisible — the design
            // would simulate, and against something else.
            if (root is not null && entry.Pin is { Length: > 0 } pin)
                root = Revision.WorkspacePins.ResolvePinned(root, pin, out _);

            map[entry.Alias] = root;
        }
        return map;
    }

    /// <summary>
    /// The other workspace's ROOT from its stored <c>.cws</c> path — relative resolves against the
    /// referencing workspace root, rooted stays as it is, and <c>/</c> converts back to the platform
    /// separator. That is <c>WorkspaceRefs.Resolve</c>'s rule, repeated here in three lines rather
    /// than called: that helper lives in <c>src/Ui</c>, on the far side of the firewall, and a
    /// headless <c>circuitrf convert</c> or EM run has to resolve these references too.
    ///
    /// <para>Null when no <c>.cws</c> is there, which is what makes a moved or deleted project a
    /// reported state rather than a reference that half-resolves.</para>
    /// </summary>
    private static string? ResolveOtherRoot(string workspaceRoot, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;
        // ${NAME} from the environment (R-sl1-5), so one site template resolves on every machine.
        // An unset token is a BROKEN reference, not an empty expansion (R-sl1-7) — PathTokens returns
        // null and the caller reports it naming the token.
        if (PathTokens.ExpandOrNull(storedPath) is not { } storedPathExpanded) return null;
        try
        {
            string native = CircuitRF.Core.RefPath.ToNative(storedPathExpanded);
            string abs = Path.IsPathRooted(native)
                ? native
                : Path.GetFullPath(Path.Combine(workspaceRoot, native));
            // The entry names the .cws itself (R-mw2-4); tolerate one naming the folder, since a
            // hand-edited .cws is a supported way to repair one and the two are one keystroke apart.
            if (File.Exists(abs))
                return WorkspaceRootFinder.Normalize(Path.GetDirectoryName(abs));
            if (Directory.Exists(abs) && File.Exists(Path.Combine(abs, ".cws")))
                return WorkspaceRootFinder.Normalize(abs);
            return null;
        }
        catch { return null; }
    }

    /// <summary>Forgets the memoised alias tables. Called by
    /// <see cref="WorkspaceRootFinder.InvalidateCache"/>, which is already invoked wherever the
    /// resolvers that depend on it are invalidated.</summary>
    public static void InvalidateCache()
    {
        lock (_memoGate) { _aliasMemo.Clear(); _namedMemo.Clear(); }
        // TM2 R-tm2-10: the forwarding records a moved reference resolves through, and the walk-up
        // that finds the root holding them, are memoised on exactly the same terms and dropped here
        // rather than on a lifecycle of their own — a third memo that had to be invalidated
        // separately would be the one that went stale.
        MoveRedirects.InvalidateCache();
    }
}
