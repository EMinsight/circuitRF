using CircuitRF.Design.Cells;

namespace CircuitRF.Ui.ViewModels.ProjectTree;

/// <summary>
/// Which document a double-click on a CELL opens. A cell's <c>.ccell</c> is rarely what the user is
/// after — its views are — so the cell opens as its primary schematic, else its primary layout, else
/// its primary 3D view, and only as its <c>.ccell</c> when it has none of the three. The symbol is
/// deliberately not in the list: it is edited far less often than any of the three, and a cell with
/// nothing but a symbol is a leaf part whose parameters are the thing to open.
/// </summary>
public static class CellOpenOrder
{
    /// <summary>The views tried, in order.</summary>
    public static readonly IReadOnlyList<ViewType> Order = [ViewType.Schematic, ViewType.Layout, ViewType.ThreeD];

    /// <summary>
    /// The first view in <see cref="Order"/> that has any file, and the file to open; null when the
    /// cell opens as its <c>.ccell</c>. The file is the view's primary when one resolves, and
    /// otherwise the alphabetically first of its files — several files with no primary named (or a
    /// named primary that is missing) is still a view the user can open, not a reason to skip it.
    /// </summary>
    public static (ViewType View, string Path)? Resolve(string cellDir)
    {
        foreach (var view in Order)
        {
            var dir = CellFolder.SubFolderPath(cellDir, view);
            var pr  = CellFolder.ResolvePrimary(cellDir, view);
            if (pr.State is PrimaryState.SoleFile or PrimaryState.NamedPresent && pr.ResolvedName is { } name)
                return (view, System.IO.Path.Combine(dir, name));
            if (pr.State is PrimaryState.NoPrimary or PrimaryState.MissingNamedPrimary
                && Directory.GetFiles(dir, "*" + CellFolder.ViewExtension(view))
                            .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault() is { } first)
                return (view, first);
        }
        return null;
    }
}
