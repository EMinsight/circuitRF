// brief-em3d-84 R-em3d84-2 — "has the model changed since this run?" asked in one place. A finished run keeps the document it
// solved beside its fields (R-em3d49-5b); the 3D editor's stale banner and `circuitrf render --field`'s stale note both compare
// that file with the document as it is now, through C3dPersistence.SerializeForRun — which leaves the field plots out, so adding
// or editing a plot never makes a run stale (R-em3d83-2).
//
// brief-em3d-87 — and every OTHER file the run was solved from. A 3D view is rarely one file: it places layouts (and through
// them sub-cells and .wBond designs), nests other 3D views, and resolves a technology and its material libraries; a
// circuit-driven thermal run also reads a schematic and its sub-cells. Beside `document.c3d`, a run keeps `inputs.json`: each
// of those files (relative to the workspace when inside it) and its SHA-256. Both are written HERE, by the run services — the
// GUI's Simulate and `circuitrf em` go through the same door, so the two cannot diverge — and hashed when the problem is
// elaborated, not when the solve ends, so an edit made during a long solve still makes its result stale.
//
// Each file is hashed as what it CONTRIBUTES, so a display edit is never a model edit: a .c3d through SerializeForRun (its field
// plots out), a .csch through its own extraction (a moved label is no change, and a sub-cell's edit names the sub-cell, not
// every parent above it), anything else by its bytes. A file is re-hashed only when its time or size moved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.ThreeD;

/// <summary>brief-em3d-87 — one input of a run: its stored path and its hash.</summary>
/// <param name="Path">Relative to the workspace root, <c>/</c>-separated, when the file is inside it; else absolute.</param>
public sealed record C3dRunInput(string Path, string Sha256);

/// <summary>
/// brief-em3d-87 R-em3d87-3 — what changed since a run: the document itself (as a run sees it) and/or named input files.
/// </summary>
/// <param name="Written">When the run kept its document.</param>
/// <param name="DocumentChanged">The document being edited is not the one the run solved.</param>
/// <param name="Changed">Each input that no longer hashes as it did, by file name.</param>
/// <param name="Missing">Each input that is no longer there, by file name.</param>
public sealed record C3dRunStaleness(DateTime Written, bool DocumentChanged, IReadOnlyList<string> Changed, IReadOnlyList<string> Missing)
{
    public bool Stale => DocumentChanged || Changed.Count > 0 || Missing.Count > 0;

    /// <summary>What moved on, as a sentence's subject: <c>the model</c>, <c>'Board.clay'</c>, <c>the model and 'Board.clay'</c>,
    /// <c>'a.clay' and 'b.cmat' (now missing)</c>. Null when nothing did.</summary>
    public string? What
    {
        get
        {
            var parts = new List<string>();
            if (DocumentChanged) parts.Add("the model");
            parts.AddRange(Changed.Select(f => $"'{f}'"));
            parts.AddRange(Missing.Select(f => $"'{f}' (now missing)"));
            return parts.Count switch
            {
                0 => null,
                1 => parts[0],
                _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
            };
        }
    }

    /// <summary>"has" or "have", agreeing with <see cref="What"/>.</summary>
    public string Has => (DocumentChanged ? 1 : 0) + Changed.Count + Missing.Count == 1 ? "has" : "have";
}

/// <summary>
/// brief-em3d-87 R-em3d87-2 — a run's inputs, taken when its problem was elaborated: the document as the run sees it and every
/// file the elaboration (and, for a circuit-driven thermal run, the circuit) read, each hashed. <see cref="KeepIn"/> writes them
/// beside the run's result once the run has succeeded.
/// </summary>
public sealed class C3dRunInputs
{
    private C3dRunInputs(string documentText, IReadOnlyList<C3dRunInput> files)
    {
        DocumentText = documentText;
        Files = files;
    }

    /// <summary>The document as the run solved it (<see cref="C3dPersistence.SerializeForRun"/>).</summary>
    public string DocumentText { get; }

    /// <summary>Every input file, as stored, in path order.</summary>
    public IReadOnlyList<C3dRunInput> Files { get; }

    /// <summary>
    /// <paramref name="document"/> (at <paramref name="documentPath"/>) and <paramref name="filesRead"/> hashed now. A path that
    /// is not a file is left out: nothing was read from it.
    /// </summary>
    public static C3dRunInputs Take(C3dDocument document, string documentPath, IEnumerable<string> filesRead)
    {
        string? root = C3dRunDocument.WorkspaceRootOf(documentPath);
        var files = new SortedDictionary<string, C3dRunInput>(StringComparer.Ordinal);
        foreach (string f in filesRead)
        {
            if (!File.Exists(f)) continue;
            string full = Path.GetFullPath(f);
            if (C3dRunDocument.HashOf(full) is not { } hash) continue;
            string stored = C3dRunDocument.Store(full, root);
            files[stored] = new C3dRunInput(stored, hash);
        }
        return new C3dRunInputs(C3dPersistence.SerializeForRun(document), [.. files.Values]);
    }

    /// <summary>These inputs with <paramref name="more"/>'s files added (a circuit's, for a circuit-driven thermal run).</summary>
    public C3dRunInputs With(C3dRunInputs more)
        => new(DocumentText, [.. Files.Concat(more.Files).GroupBy(f => f.Path, StringComparer.Ordinal).Select(g => g.First()).OrderBy(f => f.Path, StringComparer.Ordinal)]);

    /// <summary>Writes <c>document.c3d</c> and <c>inputs.json</c> into <paramref name="runDirectory"/>.</summary>
    public void KeepIn(string runDirectory)
    {
        Directory.CreateDirectory(runDirectory);
        Cells.AtomicFile.WriteAllText(C3dRunDocument.PathIn(runDirectory), DocumentText);
        Cells.AtomicFile.WriteAllText(C3dRunDocument.InputsPathIn(runDirectory),
            JsonSerializer.Serialize(new C3dRunDocument.Manifest(C3dRunDocument.ManifestVersion, [.. Files]), C3dRunDocument.Json));
    }
}

public static class C3dRunDocument
{
    /// <summary>The file a run's directory keeps the document it solved in.</summary>
    public const string FileName = "document.c3d";

    /// <summary>brief-em3d-87 — the file a run's directory keeps its other inputs' hashes in.</summary>
    public const string InputsFileName = "inputs.json";

    internal const int ManifestVersion = 1;

    internal sealed record Manifest(int Version, List<C3dRunInput> Files);

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Where <paramref name="runDirectory"/> keeps the document it solved.</summary>
    public static string PathIn(string runDirectory) => Path.Combine(runDirectory, FileName);

    /// <summary>Where <paramref name="runDirectory"/> keeps its input manifest.</summary>
    public static string InputsPathIn(string runDirectory) => Path.Combine(runDirectory, InputsFileName);

    /// <summary>True when <paramref name="solvedText"/> — the run's kept document — is not the document <paramref name="now"/>
    /// would be run as. brief-em3d-98 — with <paramref name="setup"/>, compared as that setup's run sees it on BOTH sides:
    /// the kept document holds every setup, and another setup's edit is no change to this one's model.</summary>
    public static bool IsStale(string solvedText, C3dDocument now, string? setup = null)
    {
        string current = C3dPersistence.SerializeForRun(now, setup);
        return solvedText != current && Normalised(solvedText, setup) != current;
    }

    private static readonly Dictionary<(string Text, string? Setup), string> NormalisedTexts = [];

    /// <summary>
    /// brief-em3d-98 R-em3d98-1 — a kept document as it would be kept today. A record written before a property was classified
    /// as display still holds it (a run kept before this brief holds every object's <c>Hidden</c>), so it is read back and put
    /// through <see cref="C3dPersistence.SerializeForRun"/> again — only when the plain comparison has already failed, so the
    /// common equal case costs nothing. Text that does not read as a document is compared as it is.
    /// </summary>
    private static string Normalised(string solvedText, string? setup)
    {
        lock (NormalisedTexts)
            if (NormalisedTexts.TryGetValue((solvedText, setup), out var n)) return n;
        string normal;
        try { normal = C3dPersistence.SerializeForRun(C3dPersistence.Deserialize(solvedText), setup); }
        catch (Exception e) when (e is not OutOfMemoryException) { normal = solvedText; }
        lock (NormalisedTexts)
        {
            if (NormalisedTexts.Count >= 64) NormalisedTexts.Clear();
            NormalisedTexts[(solvedText, setup)] = normal;
        }
        return normal;
    }

    /// <summary>
    /// Whether the run in <paramref name="runDirectory"/> solved a different model from <paramref name="now"/> (at
    /// <paramref name="nowPath"/>), and what moved on: the document itself, and each input file (R-em3d87-3). Null when the run
    /// kept no document (a run made before R-em3d49-5b): there is nothing to compare, so nothing is claimed. A run kept before
    /// its inputs were (brief 87) is compared on its document alone.
    /// <para>brief-em3d-98 — <paramref name="setup"/> is the embedded setup the run is of (<c>""</c> for an external
    /// <c>.cem</c>'s): the document is then compared as that setup's run sees it, so another setup's edit is no change. Null
    /// compares the whole document.</para>
    /// </summary>
    public static C3dRunStaleness? Check(string runDirectory, C3dDocument now, string nowPath, string? setup = null)
    {
        string f = PathIn(runDirectory);
        if (!File.Exists(f)) return null;
        string solved;
        DateTime written;
        try { solved = ReadCached(f); written = File.GetLastWriteTime(f); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var changed = new List<string>();
        var missing = new List<string>();
        if (ReadManifest(runDirectory) is { } manifest)
        {
            string top = Path.GetFullPath(nowPath);
            string? root = WorkspaceRootOf(top);
            foreach (var input in manifest.Files)
            {
                string full = Resolve(input.Path, root);
                // The document itself is compared as it is being edited (unsaved edits included), below.
                if (string.Equals(full, top, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(full)) { missing.Add(Path.GetFileName(full)); continue; }
                if (HashOf(full) != input.Sha256) changed.Add(Path.GetFileName(full));
            }
        }
        return new C3dRunStaleness(written, IsStale(solved, now, setup), changed, missing);
    }

    // ── the manifest's paths and hashes ──────────────────────────────────────────────────────────

    /// <summary>The workspace root <paramref name="documentPath"/> is inside (its ancestor <c>.cws</c>'s folder), or null.</summary>
    internal static string? WorkspaceRootOf(string documentPath)
        => WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(Path.GetFullPath(documentPath))) is { } cws
            ? Path.GetDirectoryName(Path.GetFullPath(cws)) : null;

    /// <summary><paramref name="full"/> as the manifest stores it: relative to <paramref name="root"/> when inside it.</summary>
    internal static string Store(string full, string? root)
    {
        if (root is null) return full;
        string rel = Path.GetRelativePath(root, full);
        return Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full : rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string Resolve(string stored, string? root)
        => Path.IsPathRooted(stored) || root is null ? stored : Path.GetFullPath(Path.Combine(root, stored.Replace('/', Path.DirectorySeparatorChar)));

    private static C3dRunDocument.Manifest? ReadManifest(string runDirectory)
    {
        string f = InputsPathIn(runDirectory);
        if (!File.Exists(f)) return null;
        try { return JsonSerializer.Deserialize<Manifest>(ReadCached(f), Json); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static readonly Dictionary<string, (DateTime Time, long Length, string Value)> Texts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (DateTime Time, long Length, string Hash)> Hashes = new(StringComparer.Ordinal);

    /// <summary>A run file's text, read once per version: the editor asks on every edit.</summary>
    private static string ReadCached(string path)
    {
        var info = new FileInfo(path);
        lock (Texts)
            if (Texts.TryGetValue(path, out var t) && t.Time == info.LastWriteTimeUtc && t.Length == info.Length) return t.Value;
        string text = File.ReadAllText(path);
        lock (Texts) Texts[path] = (info.LastWriteTimeUtc, info.Length, text);
        return text;
    }

    /// <summary>
    /// <paramref name="path"/>'s hash as a run input — what it contributes (see the file's note) — recomputed only when its time
    /// or size moved. Null when it cannot be read.
    /// </summary>
    public static string? HashOf(string path)
    {
        FileInfo info;
        try { info = new FileInfo(path); if (!info.Exists) return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        lock (Hashes)
            if (Hashes.TryGetValue(path, out var h) && h.Time == info.LastWriteTimeUtc && h.Length == info.Length) return h.Hash;
        string hash;
        try { hash = Sha(Contribution(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        lock (Hashes) Hashes[path] = (info.LastWriteTimeUtc, info.Length, hash);
        return hash;
    }

    /// <summary>What <paramref name="path"/> contributes to a run, as bytes; its raw bytes when it cannot be read as its kind.</summary>
    private static byte[] Contribution(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            // brief-em3d-98 — a placed 3D view's own setups are no part of the parent's run: an edit to one changes nothing here
            if (ext == ".c3d") { var child = C3dPersistence.LoadFromFile(path); child.Setups = []; return Encoding.UTF8.GetBytes(C3dPersistence.SerializeForRun(child)); }
            if (ext == ".csch") return Encoding.UTF8.GetBytes(SchematicCircuit.OwnCnlTextOf(path));
        }
        catch (Exception e) when (e is not (IOException or UnauthorizedAccessException or OutOfMemoryException)) { /* its bytes, below */ }
        return File.ReadAllBytes(path);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
