using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>
/// A placed vendor part's circuit, read from its own byte block (brief vendorlib-3 R-vl3-2).
///
/// <para><b>Still the one SPICE path, with a smaller input.</b> The block goes through
/// <see cref="SpiceModelPeek.FromText"/>, which is the reader and the translations a file-based
/// SpiceModel goes through, and on into <see cref="SpiceModelNetlist.Build"/> unchanged — so the cell
/// names it emits and the claimed-cell rule are the file path's own. What is gone is the whole-file
/// parse: a library file holds thousands of parts and a run needs one.</para>
///
/// <para><b>On demand, per file.</b> The walk asks the index for the one file a placed part names
/// (<see cref="VendorLibraryIndex.ScanFile"/>), never for the whole library, so a headless run that
/// places three parts scans three files' headers.</para>
/// </summary>
public static class VendorPartSource
{
    /// <summary>One placed part, resolved: which library, which folder, which file, which entry.</summary>
    /// <param name="WorkspaceRoot">The folder of the <c>.cws</c> the library was looked up in.</param>
    public sealed record Resolved(
        string WorkspaceRoot, string LibraryName, string LibraryRoot, string FilePath, VendorLibraryEntry Entry);

    /// <summary>
    /// Resolves a placed part's <c>File</c> (<c>vendorlib://…</c>) and <c>Name</c> to its index entry,
    /// or null with a <paramref name="refusal"/> naming the part and the library. The two reasons a
    /// library itself does not resolve stay distinct (brief vendorlib-1 §1), and a part the library no
    /// longer holds is a third, never a silent skip (R-vl3-2c).
    /// </summary>
    public static Resolved? Resolve(string? file, string? part, string? schematicDir, out string? refusal)
    {
        refusal = null;
        if (!VendorLibraryRefs.TryParse(file, out string library, out string rel))
        {
            refusal = $"'{file}' is not a vendor-library reference (vendorlib://<library>/<file>).";
            return null;
        }

        string? wsRoot = WorkspaceRootFinder.WorkspaceDirOf(schematicDir);
        if (wsRoot is null)
        {
            refusal = $"'{part}' comes from the vendor library '{library}', and this document belongs to no " +
                      "workspace to look the library up in.";
            return null;
        }

        string? root = VendorLibraryRefs.ResolveLibraryRoot(wsRoot, library, out string? why);
        if (root is null)
        {
            refusal = $"'{part}' cannot be read. {why}";
            return null;
        }

        string want = (part ?? "").Trim();
        if (want.Length == 0)
        {
            refusal = $"No part is chosen from {rel} in the vendor library '{library}'.";
            return null;
        }

        var entries = VendorLibraryIndex.ScanFile(root, rel);
        if (entries is null)
        {
            refusal = $"The vendor library '{library}' no longer holds {rel}, so '{want}' cannot be read.";
            return null;
        }

        var entry = entries.FirstOrDefault(e => e.Part.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            refusal = $"The vendor library '{library}' no longer holds '{want}' in {rel}.";
            return null;
        }

        string path = Path.GetFullPath(Path.Combine(root, CircuitRF.Core.RefPath.ToNative(entry.File)));
        return new Resolved(wsRoot, library, root, path, entry);
    }

    /// <summary>
    /// The index entries of the one file a <c>vendorlib://</c> reference names, from the library's
    /// LAST FINISHED index — what the Parameter Editor's part picker lists (R-vl3-1a). Reads no file
    /// and parses nothing. Null while no index of the library has finished (one is started), and null
    /// with <paramref name="reason"/> set when the library itself does not resolve.
    /// </summary>
    /// <param name="libraryRoot">The library's folder, or null when it does not resolve.</param>
    public static IReadOnlyList<VendorLibraryEntry>? IndexedFileEntries(
        string? file, string? schematicDir, out string? libraryRoot, out string? reason)
    {
        libraryRoot = null;
        reason = null;
        if (!VendorLibraryRefs.TryParse(file, out string library, out string rel))
        {
            reason = $"'{file}' is not a vendor-library reference (vendorlib://<library>/<file>).";
            return null;
        }
        string? wsRoot = WorkspaceRootFinder.WorkspaceDirOf(schematicDir);
        if (wsRoot is null)
        {
            reason = $"This part comes from the vendor library '{library}', and this document belongs to no " +
                     "workspace to look the library up in.";
            return null;
        }
        libraryRoot = VendorLibraryRefs.ResolveLibraryRoot(wsRoot, library, out reason);
        if (libraryRoot is null) return null;

        if (VendorLibraryIndex.Latest(libraryRoot) is not { } build)
        {
            _ = VendorLibraryIndex.StartBuild(libraryRoot);
            return null;
        }
        string want = rel.Replace('\\', '/').TrimStart('/');
        return [.. build.Entries.Where(e => string.Equals(e.File, want, StringComparison.OrdinalIgnoreCase))];
    }

    // The block's reading, keyed on the file and the part and dropped when the file's time or length
    // changes — the symbol resolver asks for it on every model rebuild, and the block cannot change
    // without the file changing.
    private sealed record CacheEntry(DateTime Mtime, long Length, SpiceModelFile File);
    private static readonly Dictionary<(string, string), CacheEntry> _cache = new();
    private static readonly Lock _gate = new();

    /// <summary>
    /// The part's own block, read alone and translated (<see cref="SpiceModelPeek.FromText"/>). Never
    /// throws: a block that cannot be read comes back with <see cref="SpiceModelFile.Error"/> set.
    /// </summary>
    public static SpiceModelFile Read(Resolved part)
    {
        ArgumentNullException.ThrowIfNull(part);
        DateTime mtime; long length;
        try
        {
            var info = new FileInfo(part.FilePath);
            (mtime, length) = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex)
        {
            return SpiceModelFile.Empty with { Error = $"{part.Entry.File} could not be read: {ex.Message}" };
        }

        var key = (part.FilePath, part.Entry.Part.ToUpperInvariant());
        lock (_gate)
            if (_cache.TryGetValue(key, out var hit) && hit.Mtime == mtime && hit.Length == length)
                return hit.File;

        SpiceModelFile file;
        try
        {
            string text = VendorLibraryIndex.ReadBlock(part.LibraryRoot, part.Entry);
            file = SpiceModelPeek.FromText(text, Path.GetDirectoryName(part.FilePath), part.Entry.File);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SpiceModelFile.Empty with { Error = $"{part.Entry.File} could not be read: {ex.Message}" };
        }

        lock (_gate) _cache[key] = new CacheEntry(mtime, length, file);
        return file;
    }

    /// <summary>Drops every cached block reading — with the other SpiceModel caches.</summary>
    public static void InvalidateAll() { lock (_gate) _cache.Clear(); }
}
