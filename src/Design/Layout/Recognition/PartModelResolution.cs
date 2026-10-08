// A part's model — brief-artsch-4-parts-and-parts-table.md R-as4-6; overview D10.
//
// A part number from the bill of materials becomes a measured model in one of two ways: the workspace's
// part library (`.crlib`, the library railRF reads, through PartLibraryIo) attaches a Touchstone file to
// it, or a workspace `.sNp` file's NAME begins with it. Anything else is the ideal R, L or C. A file
// with other than two ports is not used and the row says so: a part is a two-port here, port 2 grounded
// for a shunt part (AS-6), and a four-port file wired as one would be silently wrong.
//
// The class is PartModelResolver rather than PartModelResolution because railRF already has a record of
// that name (src/Design/RailRf/PartLibrary.cs) — the resolution of one part number in one library, which
// this calls — and a code file importing both namespaces would not compile.

using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What a part number resolved to.</summary>
/// <param name="Model">SnP when a two-port file was found.</param>
/// <param name="FilePath">The file, full path, for SnP.</param>
/// <param name="Source">Which route found it: <see cref="PartEvidenceSource.Library"/> or
/// <see cref="PartEvidenceSource.File"/>; null for Ideal.</param>
/// <param name="Notes">What a reader should know: other files that also matched, a file refused.</param>
public sealed record PartModelChoice(
    PartModelKind Model, string? FilePath, PartEvidenceSource? Source, IReadOnlyList<string> Notes)
{
    public static PartModelChoice Ideal(IReadOnlyList<string>? notes = null) => new(PartModelKind.Ideal, null, null, notes ?? []);
}

/// <summary>Resolves part numbers against one workspace — its libraries and its Touchstone files, each
/// listed once.</summary>
public sealed class PartModelResolver
{
    private readonly string? _root;
    private readonly Lazy<IReadOnlyList<PartLibrary>> _libraries;
    private readonly Lazy<IReadOnlyList<string>> _touchstones;

    /// <summary>How deep the workspace walk goes — a workspace is cells in folders, not a disk.</summary>
    public const int MaxDepth = 6;

    /// <param name="workspaceRoot">The workspace folder, or null — every part is then Ideal.</param>
    public PartModelResolver(string? workspaceRoot)
    {
        _root = workspaceRoot is { Length: > 0 } r && Directory.Exists(r) ? Path.GetFullPath(r) : null;
        _libraries = new(() => [.. Files("*" + PartLibraryIo.Extension).Select(Load).OfType<PartLibrary>()]);
        _touchstones = new(() => [.. Files("*.*").Where(f => RfCore.TouchstoneIO.ParsePortsFromExtension(f) is > 0)]);
    }

    /// <summary>The model for <paramref name="partNumber"/>.</summary>
    public PartModelChoice Resolve(string? partNumber)
    {
        if (partNumber is not { Length: > 0 } pn || _root is null) return PartModelChoice.Ideal();
        pn = pn.Trim();
        var notes = new List<string>();

        // ── the part library first: the file attached to the part's row ───────────────────────────
        foreach (var lib in _libraries.Value)
        {
            var hit = lib.ResolveModel(pn);
            if (hit is not { Source: PartModelSource.AttachedFile, FilePath: { } file } || !PartLibrary.IsTouchstone(file)) continue;
            if (TwoPort(file, notes)) return new PartModelChoice(PartModelKind.SnP, file, PartEvidenceSource.Library, notes);
        }

        // ── then a Touchstone file whose name begins with the part number ─────────────────────────
        var named = _touchstones.Value
            .Where(f => Path.GetFileName(f).StartsWith(pn, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f).Length).ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (named.Count == 0) return PartModelChoice.Ideal(notes);
        if (named.Count > 1)
            notes.Add($"also named for {pn}: {string.Join(", ", named.Skip(1).Select(Path.GetFileName))}");
        return TwoPort(named[0], notes)
            ? new PartModelChoice(PartModelKind.SnP, named[0], PartEvidenceSource.File, notes)
            : PartModelChoice.Ideal(notes);
    }

    /// <summary>Whether <paramref name="file"/> is a two-port Touchstone file; a note says why not.</summary>
    public static bool TwoPort(string file, List<string> notes)
    {
        if (!File.Exists(file))
        {
            notes.Add($"{Path.GetFileName(file)} is not on disk, so the ideal part is used");
            return false;
        }
        if (!RfCore.TouchstoneIO.TryGetPortCount(file, out int ports, out string? error))
        {
            notes.Add($"{Path.GetFileName(file)} could not be read ({error}), so the ideal part is used");
            return false;
        }
        if (ports == 2) return true;
        notes.Add($"{Path.GetFileName(file)} has {ports} port{(ports == 1 ? "" : "s")}, not 2, so it is not used and the ideal part is");
        return false;
    }

    private IEnumerable<string> Files(string pattern)
    {
        if (_root is null) return [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = MaxDepth, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        try
        {
            return Directory.EnumerateFiles(_root, pattern, options)
                            // .generated-cells and every other dot-folder is a cache, not the user's files.
                            .Where(f => !Path.GetRelativePath(_root, f).Split('/', '\\').SkipLast(1).Any(d => d.StartsWith('.')))
                            .OrderBy(f => f, StringComparer.Ordinal)
                            .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static PartLibrary? Load(string path)
    {
        try { return PartLibraryIo.LoadFromFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                       or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
