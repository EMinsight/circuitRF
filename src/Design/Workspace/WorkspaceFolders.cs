namespace CircuitRF.Design.Workspace;

/// <summary>
/// Workspace-root folder names that ARE design content (unlike <see cref="ReservedFolders"/>) but that
/// more than one reader has to spell the same way.
/// </summary>
public static class WorkspaceFolders
{
    /// <summary>
    /// Where an archived workspace carries the kits it was drawn with, one folder per kit. Written by
    /// the archive and read by the PCell resolver, which looks one level inside it for a kit's
    /// generator manifest — it moved here with that resolver (brief-generated-cells-2 R-gc2-1) so the
    /// two cannot drift.
    /// </summary>
    public const string Kits = "kits";
}
