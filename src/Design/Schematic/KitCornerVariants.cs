using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// A selected corner section's VARIANT model library, put in place of a part's own (docs/design/spice-models.md
/// §8.11). A kit's mismatch sections include a sibling of the library its parts are bound to, whose subcircuits
/// carry per-instance draws (<c>w='agauss(w, …)'</c>); selecting one must change what runs, or the kit's mismatch
/// never reaches a Monte Carlo trial.
/// </summary>
public static class KitCornerVariants
{
    /// <summary>
    /// Replaces <paramref name="library"/>'s subcircuits (in place) and <paramref name="cards"/> with the same-named
    /// definitions of every section in <paramref name="sections"/> that includes a variant of the part library at
    /// <paramref name="partLibrary"/>, and adds what only the variant defines. Returns the cards — the same list
    /// when nothing changed.
    ///
    /// <para><b>A section is a variant's when it does not include the part library itself.</b> A kit's nominal and
    /// plain process sections include the library a part is bound to (the import paired them that way —
    /// <c>PdkImporter.CanonicalLibraries</c>), so they change nothing and every nominal netlist stays byte-identical.
    /// "Includes the part library" is decided by CONTENT, not by path: a workspace keeps its own copy of a kit's
    /// netlists, so the section's file and the part's are routinely two paths to one text.</para>
    /// </summary>
    public static IReadOnlyList<SpiceModelCard> Apply(
        string partLibrary, Library library, IReadOnlyList<SpiceModelCard> cards,
        IReadOnlyList<PdkCornerSection> sections)
    {
        byte[]? own = null;
        List<SpiceModelCard>? changed = null;

        foreach (var section in sections)
        {
            if (section.Cells.Cells.Count == 0 && section.ModelCards.Count == 0) continue;

            own ??= File.ReadAllBytes(partLibrary);
            if (section.FilesRead.Any(f => SameContent(f, own))) continue;

            foreach (var variant in section.Cells.Cells)
            {
                int at = library.Cells.FindIndex(c => c.Name.Equals(variant.Name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) library.Cells[at] = variant;
                else         library.Cells.Add(variant);
            }

            foreach (var card in section.ModelCards)
            {
                changed ??= [.. cards];
                int at = changed.FindIndex(c => c.Name.Equals(card.Name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) changed[at] = card;
                else         changed.Add(card);
            }
        }
        return changed ?? cards;
    }

    private static bool SameContent(string file, byte[] bytes)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists && info.Length == bytes.Length && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes);
        }
        catch { return false; }
    }
}
