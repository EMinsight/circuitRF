// brief-em3d-95 R-em3d95-3 — the PATH half of a pasted instance's reference, shared by the layout paste
// (LayoutFragment.RebaseInstances) and the 3D paste (C3dFragment). Lifted out of LayoutFragment unchanged, so a placed cell
// pasted into a .clay and one pasted into a .c3d take exactly the same reference — from the user's side the two views
// behave alike, and there is one rule to change rather than two to keep in step.

namespace CircuitRF.Design.Layout;

/// <summary>A copied instance's <c>CellRef</c>, captured in base-independent forms and recomputed for the destination.</summary>
public static class CellRefRebase
{
    /// <summary>
    /// The reference a pasted instance takes (LayoutFragment.RebaseInstances documents the order):
    /// <list type="number">
    /// <item>relative to <paramref name="destBaseDir"/>, from the source's absolute cell directory;</item>
    /// <item>else the source-workspace-relative directory under <paramref name="destWorkspaceRootDir"/>, absolute;</item>
    /// <item>else the source's absolute cell directory itself;</item>
    /// <item>else (broken at copy time) the original reference, unchanged.</item>
    /// </list>
    /// A <c>ws://</c> reference is base-independent already and is kept verbatim.
    /// </summary>
    public static string Rebase(string cellRef, string? sourceCellDir, string? workspaceRelativeDir, string destBaseDir,
                                string? destWorkspaceRootDir)
    {
        if (Workspace.ExternalCellRef.IsExternalRef(cellRef)) return cellRef;

        if (sourceCellDir is { Length: > 0 } && destBaseDir is { Length: > 0 })
        {
            try
            {
                return CircuitRF.Core.RefPath.ToStored(
                    Path.GetRelativePath(Path.GetFullPath(destBaseDir), Path.GetFullPath(sourceCellDir)));
            }
            catch
            {
                // Fall through to the base-independent forms below.
            }
        }

        if (workspaceRelativeDir is { Length: > 0 } && destWorkspaceRootDir is { Length: > 0 })
        {
            try { return CircuitRF.Core.RefPath.Resolve(destWorkspaceRootDir, workspaceRelativeDir); }
            catch
            {
                // Fall through to the plain absolute fallback below.
            }
        }

        return sourceCellDir is { Length: > 0 } ? sourceCellDir : cellRef;
    }

    /// <summary>
    /// The two base-independent forms a copy carries for one reference: the cell directory it resolves to from
    /// <paramref name="baseDir"/> (null when it resolves to nothing there), and that directory relative to
    /// <paramref name="workspaceRootDir"/> (null with no workspace, or no directory).
    /// </summary>
    public static (string? CellDir, string? WorkspaceRelativeDir) Capture(string cellRef, string? baseDir, string? workspaceRootDir)
    {
        string? dir = Workspace.ExternalCellRef.ResolveCellDir(cellRef, baseDir, out _, out bool exists);
        if (dir is null || !exists) return (null, null);
        string? relative = null;
        if (workspaceRootDir is { Length: > 0 } root)
        {
            try { relative = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(dir))); }
            catch { /* leave null — the absolute form still covers it */ }
        }
        return (dir, relative);
    }
}
