// Where a technology built from a kit lands in a workspace, and when it becomes the default
// (brief-gdf-3 R-gdf3-3). The GUI's Import PDK and `circuitrf import pdk --technology` both call this, so
// the two cannot come to disagree about the folder, the file name or the default rule.

using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Layout.TechImport;

public static class KitTechnologyInstall
{
    /// <summary>The folder a workspace keeps its technologies in — where File ▸ Import ▸ Technology writes.</summary>
    public static string TechDirectory(string workspaceRoot) => Path.Combine(workspaceRoot, "tech");

    /// <summary>The <c>.ctech</c> a kit's technology is written to: named after the kit's module.</summary>
    public static string PathFor(string workspaceRoot, string module)
        => Path.Combine(TechDirectory(workspaceRoot), module + ".ctech");

    /// <summary>
    /// Writes <paramref name="tech"/> to <paramref name="path"/>, which must not exist. <b>Never over an
    /// existing file</b>: someone may have edited that technology, and a headless caller cannot ask, so an
    /// existing file is a refusal naming it. The GUI asks first and merges, exactly as File ▸ Import ▸
    /// Technology does, and so never calls this for an existing file.
    /// </summary>
    public static bool TryWriteNew(string path, Technology tech, out string? refusal)
    {
        if (File.Exists(path))
        {
            refusal = $"'{path}' already exists and was left as it is; a technology is never written over " +
                      "one of the same name. Remove or rename it to build the kit's technology again.";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TechPersistence.SaveToFile(path, tech);
        refusal = null;
        return true;
    }

    /// <summary>
    /// Makes <paramref name="techPath"/> the workspace's default technology <b>only if it has none</b>, and
    /// says whether it did. A workspace that already names one keeps it: the kit's technology is added
    /// beside it, never swapped in under the designs already drawn against it.
    /// </summary>
    public static bool MakeDefaultIfNone(string cwsPath, string techPath)
    {
        CwsFile cws;
        try   { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch { return false; }
        if (cws.DefaultTechRef is { Length: > 0 }) return false;

        cws.DefaultTechRef = Core.RefPath.ToStored(Path.GetRelativePath(Path.GetDirectoryName(cwsPath)!, techPath));
        return WorkspacePersistence.SaveToFileAtomic(cwsPath, cws);
    }
}
