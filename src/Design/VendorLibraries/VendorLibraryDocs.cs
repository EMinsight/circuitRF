namespace CircuitRF.Design.VendorLibraries;

/// <summary>
/// The documentation a vendor library came with — what its Help button opens.
///
/// <para><b>Found by what a file IS, never by its name.</b> A manufacturer ships its model notes, its
/// terms of use and a read-me beside the model files, under names that differ from one edition to the
/// next, so nothing here looks for a particular name: a document is a file whose type is a document
/// (PDF, HTML, Markdown, rich text, a word-processor file, an internet shortcut) — or a plain-text
/// file the index found no part in, which is how a read-me shipped as <c>.txt</c> beside <c>.lib</c>
/// files is told from a model file.</para>
///
/// <para><b>Nearest the root first</b>: a top-level read-me is the one a user means; a datasheet three
/// folders down is one of many. Within one depth, PDFs, then web pages, then the rest, then by name.
/// Reads no file — the walk is a directory listing and the index already in memory.</para>
/// </summary>
public static class VendorLibraryDocs
{
    /// <summary>How many documents are offered at most; a library shipping a datasheet per series
    /// would otherwise turn the Help button into a list nobody reads.</summary>
    public const int MaxDocuments = 20;

    private static readonly Dictionary<string, int> Rank = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = 0, [".html"] = 1, [".htm"] = 1, [".md"] = 2, [".rtf"] = 3, [".doc"] = 3, [".docx"] = 3,
        [".url"] = 4, [".txt"] = 5,
    };

    /// <summary>
    /// The documents under <paramref name="libraryRoot"/>, best first, as absolute paths. Empty for a
    /// library that came with none, or a folder that is not there.
    /// </summary>
    /// <param name="build">The library's index, so a <c>.txt</c> holding parts is not offered as a
    /// document. Without one, no <c>.txt</c> is offered at all.</param>
    public static IReadOnlyList<string> Find(string libraryRoot, VendorLibraryBuild? build)
    {
        if (!Directory.Exists(libraryRoot)) return [];

        var withParts = new HashSet<string>(
            (build?.Entries ?? []).Select(e => e.File), StringComparer.OrdinalIgnoreCase);

        var found = new List<(int Depth, int Rank, string Rel, string Abs)>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(libraryRoot, "*", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        foreach (string abs in files)
        {
            string rel = Path.GetRelativePath(libraryRoot, abs).Replace('\\', '/');
            if (rel.Split('/').Any(s => s.StartsWith('.'))) continue;
            if (!Rank.TryGetValue(Path.GetExtension(abs), out int rank)) continue;
            if (rank == Rank[".txt"] && (build is null || withParts.Contains(rel))) continue;
            found.Add((rel.Count(c => c == '/'), rank, rel, abs));
        }

        return [.. found.OrderBy(f => f.Depth).ThenBy(f => f.Rank)
                        .ThenBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                        .Take(MaxDocuments).Select(f => f.Abs)];
    }
}
