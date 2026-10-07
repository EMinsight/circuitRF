using System.Windows.Input;
using Avalonia.Input;

namespace CircuitRF.Ui.ViewModels.ProjectTree;

/// <summary>
/// What the Workspace panel may do to its SELECTION — one row or several — and which key asks for
/// it. Pure rules, so the view's key handler, the multi-selection menu and the workspace's removal
/// all read one answer.
///
/// <para><b>Selecting several rows</b> is Avalonia's own <c>TreeView</c> with
/// <c>SelectionMode="Multiple"</c>: Shift+click selects a range, and the platform's command modifier
/// (⌘ on macOS, Ctrl elsewhere) adds or removes one row — the convention of every file manager on
/// each of them. A right-click on a row INSIDE the selection keeps it; on a row outside, the
/// selection becomes that row.</para>
/// </summary>
public static class ProjectTreeSelection
{
    /// <summary>
    /// Delete or Backspace — a Mac keyboard labels Backspace "delete", and Key.Delete is the
    /// forward-delete it may not even have, so taking only Key.Delete would make the feature do
    /// nothing there. Bare, or with ⌘ (the Finder's Move to Trash). Any other modifier is some other
    /// gesture and is left alone.
    /// </summary>
    public static bool IsDeleteGesture(Key key, KeyModifiers modifiers) =>
        key is Key.Delete or Key.Back && modifiers is KeyModifiers.None or KeyModifiers.Meta;

    /// <summary>
    /// The selection with every row that sits INSIDE another selected row dropped, and duplicates
    /// folded. Removing a folder removes what is in it; asking to remove both would count the inner
    /// one twice in the confirmation and then fail on it, because it is already in the Trash.
    /// </summary>
    public static IReadOnlyList<ProjectTreeNodeViewModel> TopMost(IEnumerable<ProjectTreeNodeViewModel> nodes)
    {
        var distinct = nodes
            .DistinctBy(n => Trim(n.AbsolutePath), StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct
            .Where(n => !distinct.Any(o => !ReferenceEquals(o, n) && IsUnder(n.AbsolutePath, o.AbsolutePath)))
            .ToList();
    }

    /// <summary>
    /// The remove a single row's own context menu offers — so Delete on one row does exactly what
    /// right-click ▸ Remove does, confirmation included. Null when the row has none, and for a
    /// Known File: its "Remove Reference" asks nothing first, and no key in this panel removes
    /// anything without asking.
    /// </summary>
    public static ICommand? SingleRemoveCommand(ProjectTreeNodeViewModel n) =>
        n.IsOwnCell             ? n.RemoveCellCommand
      : n.IsReferencedCell      ? n.RemoveCellReferenceCommand
      : n.IsRemovableFile       ? n.RemoveFileCommand
      : n.IsTechFile            ? n.RemoveTechnologyCommand
      : n.IsReferencedWorkspace ? n.RemoveWorkspaceReferenceCommand
      : null;

    /// <summary>
    /// A row that can go to the Trash as one of several: a cell this workspace owns, a user folder,
    /// or one of the documents the single "Remove" item covers. Everything else has a removal that
    /// is about more than the file — a technology changes what layouts MEAN, a reference edits the
    /// <c>.cws</c> — and asks its own questions, so it is removed on its own.
    /// </summary>
    public static bool IsBulkRemovable(ProjectTreeNodeViewModel n) => n.IsOwnCell || n.IsRemovableFile;

    /// <summary>
    /// Why these rows cannot be removed together, or null when they can. Names the first row that
    /// blocks it, because "some of these cannot be removed" leaves the user to find out which.
    /// </summary>
    public static string? BulkRemoveRefusal(IReadOnlyList<ProjectTreeNodeViewModel> nodes)
    {
        if (nodes.Count == 0) return "Nothing is selected.";
        if (nodes.FirstOrDefault(n => !IsBulkRemovable(n)) is not { } blocker) return null;

        string why =
            blocker.IsTechFile
                ? "a technology changes what the layouts using it mean, so it is removed on its own"
          : blocker.IsReferencedWorkspace || blocker.IsReferencedCell || blocker.IsKnownFile
                ? "it is a reference, not a file of this workspace, so it is removed on its own"
          : "it has no Remove";
        return $"'{blocker.Name}' cannot be removed with the rest of the selection — {why}.";
    }

    /// <summary>A row Open can act on: a cell (its primary view) or a document with an editor.</summary>
    public static bool CanOpen(ProjectTreeNodeViewModel n) => n.IsCell || n.IsOpenableFile;

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnder(string path, string dir) =>
        Trim(path).StartsWith(Trim(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
     || Trim(path).StartsWith(Trim(dir) + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
