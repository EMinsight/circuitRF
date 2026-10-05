// A wBond's workspace material library — the .cmat behind the Inspector's "New Material…" and the metals it lists.
//
// THE INSTANCE NAMES ITS LIBRARY. `MaterialLibrary` on the placed component is the reference, relative to the schematic,
// so a run that cannot find the file can say which instance asked for it (ComponentModelFactory.ApplyMaterialLibrary). It
// is written only when a library material is CHOSEN; until then the Inspector lists the library this would reuse.
//
// WHICH LIBRARY IS REUSED, in order: the one the instance already names; a .cmat the workspace technology references; any
// other .cmat in the workspace (two folders deep, results/ and hidden folders skipped); and only then a new
// tech/workspace-materials.cmat. A PRISTINE copy of circuitRF's generic-materials.cmat is never reused: it is the shipped
// library's stand-in, kept byte-identical so Add Generic Materials keeps recognising it, and a user metal added to it would
// make it a file circuitRF refuses to replace. A library the technology does not name yet is ADDED to it, so a 3D or
// thermal run in the same workspace sees the same metals.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Workspace;
using CircuitRF.WBond;

namespace CircuitRF.Design.Schematic;

/// <summary>Finds, creates and reads the workspace <c>.cmat</c> a placed wBond takes custom wire metals from.</summary>
public static class WBondMaterialLibrary
{
    /// <summary>Where a new library is written, relative to the workspace root.</summary>
    public const string NewLibraryRelativePath = "tech/workspace-materials.cmat";

    /// <summary>What <see cref="LocateOrCreate"/> found or made.</summary>
    /// <param name="Path">The library, absolute.</param>
    /// <param name="Created">True when this call wrote it.</param>
    /// <param name="TechnologyToRegisterIn">The workspace <c>.ctech</c> that does not name it yet, or null.</param>
    public sealed record Located(string Path, bool Created, string? TechnologyToRegisterIn);

    /// <summary>The library whose metals the Inspector lists for <paramref name="comp"/>: the one it names, else the
    /// one <see cref="LocateOrCreate"/> would reuse. Null when there is neither. Creates nothing.</summary>
    public static string? ForListing(EditableComponent comp, string? schematicDirectory)
        => WBondPlacement.ResolveMaterialLibrary(comp, schematicDirectory) ?? FindReusable(schematicDirectory);

    /// <summary>The metals <paramref name="path"/> offers a wire, or empty when it cannot be read.</summary>
    public static IReadOnlyList<WireMaterial> ConductorsOf(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        try { return WireMaterialLibrary.ReadFile(path).Conductors; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or InvalidDataException) { return []; }
    }

    /// <summary>
    /// The library a new wire material goes in: reused when one exists (see the file header for the order), written
    /// empty at <see cref="NewLibraryRelativePath"/> otherwise. Throws <see cref="InvalidOperationException"/> when the
    /// schematic belongs to no workspace — there is nowhere a library could live that a run would find again.
    /// </summary>
    public static Located LocateOrCreate(EditableComponent comp, string? schematicDirectory)
    {
        ArgumentNullException.ThrowIfNull(comp);
        if (WBondPlacement.ResolveMaterialLibrary(comp, schematicDirectory) is { } own && File.Exists(own))
            return new Located(own, false, TechnologyNotNaming(own, schematicDirectory));
        if (FindReusable(schematicDirectory) is { } reused)
            return new Located(reused, false, TechnologyNotNaming(reused, schematicDirectory));

        string root = WorkspaceRoot(schematicDirectory)
            ?? throw new InvalidOperationException(
                $"wBond '{comp.InstanceName}' is not in a saved workspace, so there is nowhere to keep a material " +
                "library a run could find again. Save the schematic into a workspace, then choose New Material… again.");

        string path = Path.GetFullPath(Path.Combine(root, NewLibraryRelativePath));
        bool created = !File.Exists(path);
        if (created)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            MaterialLibraryPersistence.SaveToFile(path, []);
        }
        return new Located(path, created, TechnologyNotNaming(path, schematicDirectory));
    }

    /// <summary>Adds <paramref name="cmatPath"/> to the libraries <paramref name="ctechPath"/> names, on disk. For a
    /// technology that is not open in an editor; one that is takes the reference through its own (undoable) edit.</summary>
    public static void RegisterInTechnology(string ctechPath, string cmatPath)
    {
        var tech = TechPersistence.LoadOwnFromFile(ctechPath);
        string reference = MaterialLibraries.RelativeReference(ctechPath, cmatPath);
        (tech.MaterialLibraries ??= []).Add(reference);
        TechPersistence.SaveToFile(ctechPath, tech);
    }

    // ── the search ───────────────────────────────────────────────────────────────────────────

    /// <summary>The library New Material… would reuse, or null when it would create one.</summary>
    public static string? FindReusable(string? schematicDirectory)
    {
        if (WorkspaceRoot(schematicDirectory) is not { } root) return null;

        if (WorkspaceTechnology(schematicDirectory, root) is { } ctech)
            foreach (string path in LibrariesNamedBy(ctech))
                if (Reusable(path, root)) return path;

        foreach (string path in WorkspaceLibraries(root))
            if (Reusable(path, root)) return path;

        return null;
    }

    private static string? WorkspaceRoot(string? schematicDirectory)
        => WorkspaceRootFinder.FindAncestorCws(schematicDirectory) is { } cws ? Path.GetDirectoryName(Path.GetFullPath(cws)) : null;

    /// <summary>The workspace's technology when it is a file INSIDE the workspace — the only kind this may write.</summary>
    private static string? WorkspaceTechnology(string? schematicDirectory, string root)
        => MicrostripSubstrateInjection.ResolveWorkspaceTechnologyPath(schematicDirectory) is { } p
           && File.Exists(p) && IsInside(Path.GetFullPath(p), root)
            ? Path.GetFullPath(p) : null;

    private static IEnumerable<string> LibrariesNamedBy(string ctechPath)
    {
        List<string> references;
        try { references = TechPersistence.LoadOwnFromFile(ctechPath).MaterialLibraries ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or InvalidDataException) { yield break; }
        foreach (string reference in references)
        {
            string? path = null;
            try { path = Path.GetFullPath(MaterialLibraries.ResolvePath(ctechPath, reference)); }
            catch (Exception ex) when (ex is ArgumentException or IOException) { }
            if (path is not null) yield return path;
        }
    }

    private static string? TechnologyNotNaming(string cmatPath, string? schematicDirectory)
    {
        if (WorkspaceRoot(schematicDirectory) is not { } root) return null;
        if (WorkspaceTechnology(schematicDirectory, root) is not { } ctech) return null;
        return LibrariesNamedBy(ctech).Any(p => SamePath(p, cmatPath)) ? null : ctech;
    }

    /// <summary>Every <c>.cmat</c> at most two folders below the root, ordinal order, skipping <c>results</c> and hidden
    /// folders. Bounded because the Inspector asks on every refresh.</summary>
    private static IEnumerable<string> WorkspaceLibraries(string root)
    {
        var found = new List<string>();
        void Scan(string dir, int depth)
        {
            try { found.AddRange(Directory.EnumerateFiles(dir, "*.cmat")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            if (depth == 2) return;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (string child in children)
            {
                string name = Path.GetFileName(child);
                if (name.StartsWith('.') || name.Equals("results", StringComparison.OrdinalIgnoreCase)) continue;
                Scan(child, depth + 1);
            }
        }
        Scan(root, 0);
        return found.Select(Path.GetFullPath).OrderBy(p => p, StringComparer.Ordinal);
    }

    private static bool Reusable(string path, string root)
        => File.Exists(path) && IsInside(path, root) && !IsPristineGeneric(path);

    /// <summary>A byte-for-byte copy of the shipped generic library — never written to (see the file header).</summary>
    public static bool IsPristineGeneric(string path)
    {
        if (!Path.GetFileName(path).Equals(MaterialLibraries.GenericFileName, StringComparison.OrdinalIgnoreCase)) return false;
        try { return File.ReadAllText(path) == MaterialLibraries.GenericRawJson(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsInside(string path, string root)
    {
        string rel = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(rel) && !rel.StartsWith("..", StringComparison.Ordinal);
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                         OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
