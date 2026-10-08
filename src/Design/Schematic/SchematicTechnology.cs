// A schematic's technology — brief-artsch-6-emit-and-target-cell.md R-as6-1, overview D20.
//
// Until AS-6 a schematic always took the workspace default (MicrostripSubstrateInjection's walk), so a
// schematic modelling an imported board whose cell carries its OWN .ctech computed every MLIN on the wrong
// substrate with nothing said. A .csch may now name its own technology (TechRef, relative to the .csch, the
// .clay's spelling) and a .cnl may say `technology "<path>"` (relative to the .cnl): resolved FIRST, the
// workspace default only when the document names none — the order a layout already uses
// (TechnologyResolver). Every stackup injection is handed what this returns, so there is one resolver and
// not four copies of it.
//
// A named technology that does not resolve is an ERROR, never a quiet fall back to the workspace default:
// the default is exactly the wrong substrate the reference exists to avoid.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Schematic;

/// <summary>Where a schematic's technology came from.</summary>
public enum SchematicTechSource
{
    /// <summary>The document's own reference — a <c>.csch</c>'s <c>TechRef</c>, a <c>.cnl</c>'s
    /// <c>technology</c> statement.</summary>
    DocumentRef,

    /// <summary>The nearest ancestor workspace's default technology — the document names none.</summary>
    WorkspaceDefault,

    /// <summary>Nothing: no reference and no workspace default.</summary>
    None,
}

/// <summary>A schematic's (or a netlist's) resolved technology.</summary>
/// <param name="Technology">The technology, or null.</param>
/// <param name="Path">The <c>.ctech</c> it was read from — or, for a reference that failed, the path it
/// named.</param>
/// <param name="Source">Which walk produced it.</param>
/// <param name="Error">Why a reference the document NAMES does not resolve, or null. A document without one
/// never has an error here: no technology at all is a normal state.</param>
public sealed record SchematicTechResolution(
    Technology? Technology, string? Path, SchematicTechSource Source, string? Error)
{
    /// <summary>How the walk is described — <c>explain</c>'s wording.</summary>
    public string Walk => Source switch
    {
        SchematicTechSource.DocumentRef      => "the document's own technology reference, relative to its own directory",
        SchematicTechSource.WorkspaceDefault => "the workspace's DefaultTechRef — the document names none",
        _                                    => "nothing resolved: no technology reference and no workspace default",
    };
}

/// <summary>R-as6-1's one resolver.</summary>
public static class SchematicTechnology
{
    /// <summary>The technology of <paramref name="model"/>: its <c>TechRef</c>, then the workspace default.</summary>
    public static SchematicTechResolution Resolve(SchematicEditModel model)
        => Resolve(model.SchematicDirectory, model.TechRef);

    /// <summary>The technology a document in <paramref name="directory"/> naming <paramref name="techRef"/>
    /// takes — a <c>.csch</c>'s <c>TechRef</c> or a <c>.cnl</c>'s <c>technology</c> statement, each relative
    /// to its own document (an absolute path is taken as it is).</summary>
    public static SchematicTechResolution Resolve(string? directory, string? techRef)
    {
        if (techRef is { Length: > 0 })
        {
            if (directory is null && !System.IO.Path.IsPathRooted(techRef))
                return new SchematicTechResolution(null, null, SchematicTechSource.DocumentRef,
                    $"The technology '{techRef}' is relative and the document has not been saved, so there is nothing to resolve it against.");

            string path = Core.RefPath.Resolve(directory ?? "", techRef);
            if (!File.Exists(path))
                return new SchematicTechResolution(null, path, SchematicTechSource.DocumentRef,
                    $"The technology '{techRef}' does not resolve: there is no file at {path}.");
            try
            {
                return new SchematicTechResolution(TechPersistence.LoadFromFile(path), path, SchematicTechSource.DocumentRef, null);
            }
            catch (Exception ex)
            {
                return new SchematicTechResolution(null, path, SchematicTechSource.DocumentRef,
                    $"The technology '{techRef}' could not be read ({ex.Message}).");
            }
        }

        // No reference: exactly the walk every schematic has always taken, byte for byte.
        string? defaultPath = MicrostripSubstrateInjection.ResolveWorkspaceTechnologyPath(directory);
        if (defaultPath is null) return new SchematicTechResolution(null, null, SchematicTechSource.None, null);
        return new SchematicTechResolution(MicrostripSubstrateInjection.ResolveWorkspaceTechnology(directory),
                                           defaultPath, SchematicTechSource.WorkspaceDefault, null);
    }

    /// <summary>The technology of <paramref name="model"/>, or null — what a readout or an injection wants.</summary>
    public static Technology? Of(SchematicEditModel model) => Resolve(model).Technology;

    /// <summary>The <c>.ctech</c> path <paramref name="model"/> resolves, or null — what a comparison wants.</summary>
    public static string? PathOf(SchematicEditModel model) => Resolve(model) is { Error: null, Path: { } p } ? p : null;

    /// <summary>
    /// What relative references in the netlist a schematic extracts to resolve against: its own workspace's root,
    /// else its own folder — <c>SchematicCircuit.ReferenceBaseOf</c>, and where Simulate writes that netlist.
    /// </summary>
    public static string? NetlistBaseOf(SchematicEditModel model) =>
        model.SchematicDirectory is not { } dir ? null : WorkspaceRootFinder.WorkspaceDirOf(dir) ?? dir;

    /// <summary>
    /// The <c>technology</c> statement of the netlist <paramref name="model"/> extracts to: its <c>TechRef</c>
    /// RELATIVE to <see cref="NetlistBaseOf"/> — the base every other relative reference in that text resolves
    /// against — or null without one. A schematic with no folder keeps its <c>TechRef</c> as written.
    /// </summary>
    public static string? NetlistRef(SchematicEditModel model)
    {
        if (model.TechRef is not { Length: > 0 } techRef) return null;
        if (NetlistBaseOf(model) is not { } netlistBase || Resolve(model).Path is not { } path) return techRef;
        return StoredRef(path, netlistBase);
    }

    /// <summary>A netlist's <c>technology</c> statement written relative to <paramref name="fromDir"/>, restated
    /// relative to <paramref name="toDir"/> — for a netlist written somewhere other than where its references were
    /// made relative to (Simulate's scratch folder when no workspace is open).</summary>
    public static string? Rebase(string? technology, string? fromDir, string toDir)
    {
        if (technology is not { Length: > 0 } || fromDir is null || System.IO.Path.IsPathRooted(technology)) return technology;
        return StoredRef(Core.RefPath.Resolve(fromDir, technology), toDir);
    }

    /// <summary>A technology file as a document in <paramref name="documentDirectory"/> stores it: relative, with
    /// forward slashes — the <c>.clay</c>'s spelling.</summary>
    public static string StoredRef(string technologyPath, string documentDirectory)
    {
        string rel = System.IO.Path.GetRelativePath(documentDirectory, technologyPath);
        return System.IO.Path.IsPathRooted(rel) ? technologyPath.Replace('\\', '/') : Core.RefPath.ToStored(rel)!;
    }
}
