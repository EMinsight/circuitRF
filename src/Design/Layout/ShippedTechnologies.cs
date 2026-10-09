using System.Linq;
using System.Reflection;

namespace CircuitRF.Design.Layout;

/// <summary>
/// docs/sonnet-briefs/brief-misc-termg-units-technologies.md §3 (R-misc-6/7/8/9): the four
/// technologies circuitRF ships out of the box — real, authored <c>.ctech</c> files (see
/// <c>src/Design/resources/technologies/</c>), embedded into the assembly as plain .NET
/// <c>EmbeddedResource</c> assets and parsed at runtime through the SAME <see cref="TechPersistence"/>
/// reader a user's own <c>.ctech</c> files go through — never transcribed into C# object
/// initializers, which would create a second representation of the same authored content that
/// would inevitably drift from it (R-misc-6's own reasoning). A workspace never references the
/// embedded copy at runtime — <see cref="CircuitRF.Design.Workspace.WorkspaceCreate"/> writes the chosen entry's own bytes into
/// the new workspace's own <c>tech/</c> folder as a real, independently-editable file (R-misc-8);
/// this class is read ONLY at workspace-creation time (and by the "ship" gate test).
///
/// It lives in <c>src/Design</c>, with its <c>EmbeddedResource</c> items, because
/// <c>circuitrf new workspace</c> creates a workspace with no display attached
/// (brief-automation-3-authoring-verbs.md R-aut3-4). Moving the class without its resources would
/// have left it compiling, enumerating nothing, and reporting nothing.
///
/// Deliberately plain .NET <c>EmbeddedResource</c>, not Avalonia's <c>AvaloniaResource</c>/
/// <c>AssetLoader</c> — this whole namespace is framework-free by design (no Avalonia reference),
/// and <c>AssetLoader.Open</c> throws with no live Avalonia platform, which is exactly the
/// constraint <c>SkiaFonts.cs</c>'s own note already names for embedded fonts. <c>Assembly.
/// GetManifestResourceStream</c> has no such requirement — it works identically in the desktop app
/// and in this project's headless xunit tests.
/// </summary>
public static class ShippedTechnologies
{
    /// <summary>File-stem id of the default shipped technology (owner's choice, R-misc-11) — the
    /// New Workspace dialog's combobox opens pre-selected on this entry.</summary>
    public const string DefaultId = "pcb-2layer_RO4350B_20mil_1oz";

    private const string ResourceSuffix = ".ctech";
    private const string ResourceFolder = ".resources.technologies.";

    private static readonly Lazy<IReadOnlyList<ShippedTechnologyEntry>> _entries = new(Discover);

    /// <summary>Every shipped technology, sorted by file-stem id for a stable, deterministic order —
    /// never enumeration order, which .NET does not guarantee across runtimes/platforms.</summary>
    public static IReadOnlyList<ShippedTechnologyEntry> All => _entries.Value;

    /// <summary>Parses one shipped technology's embedded bytes through the normal
    /// <see cref="TechPersistence.Deserialize"/> reader — the exact function a user's own
    /// <c>.ctech</c> file loads through, so a malformed shipped technology fails the SAME way (and
    /// is caught by the SAME "ship" gate test) a malformed user file would be caught by its own
    /// round-trip tests.</summary>
    public static Technology Load(ShippedTechnologyEntry entry)
    {
        var asm = typeof(ShippedTechnologies).Assembly;
        using var stream = asm.GetManifestResourceStream(entry.ResourceName)
            ?? throw new InvalidOperationException($"Embedded technology resource \"{entry.ResourceName}\" not found.");
        using var reader = new StreamReader(stream);
        // brief-em3d-53 R-em3d53-1c: a shipped technology's library references resolve against the
        // resources beside it — the same spelling that resolves beside a workspace's copy on disk.
        return TechPersistence.Deserialize(reader.ReadToEnd(), MaterialLibraries.Shipped,
            MaterialLibraries.ShippedPrefix + entry.Id + ResourceSuffix);
    }

    /// <summary>Convenience overload keyed by file-stem id (e.g. <see cref="DefaultId"/>).</summary>
    public static Technology Load(string id)
    {
        var entry = All.FirstOrDefault(e => e.Id == id)
            ?? throw new InvalidOperationException($"No shipped technology named \"{id}\".");
        return Load(entry);
    }

    /// <summary>Raw, still-authored JSON bytes for one entry — what
    /// <see cref="CircuitRF.Design.Workspace.WorkspaceCreate"/> writes verbatim into the new workspace's own
    /// <c>tech/</c> folder (R-misc-8: "a real file," not a re-serialization through
    /// <see cref="TechPersistence.Serialize"/>, which would be a harmless but pointless
    /// round-trip — the shipped bytes ARE already exactly what should land on disk).</summary>
    public static string LoadRawJson(ShippedTechnologyEntry entry)
    {
        var asm = typeof(ShippedTechnologies).Assembly;
        using var stream = asm.GetManifestResourceStream(entry.ResourceName)
            ?? throw new InvalidOperationException($"Embedded technology resource \"{entry.ResourceName}\" not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<ShippedTechnologyEntry> Discover()
    {
        var asm = typeof(ShippedTechnologies).Assembly;
        var list = new List<ShippedTechnologyEntry>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(ResourceSuffix, StringComparison.Ordinal)) continue;
            // MSBuild's default embedded-resource naming is <RootNamespace>.<folder.path>.<FileName> —
            // folder separators become dots and a dot in the FILE name stays a dot, so the stem is
            // everything after the folder path, not after the last dot: "pcb-6layer_FR-4_1.6mm_1oz"
            // read by its last dot was the id "6mm_1oz".
            string withoutExt = name[..^ResourceSuffix.Length];
            int folder = withoutExt.IndexOf(ResourceFolder, StringComparison.Ordinal);
            string id = folder >= 0 ? withoutExt[(folder + ResourceFolder.Length)..]
                      : withoutExt[(withoutExt.LastIndexOf('.') + 1)..];
            list.Add(new ShippedTechnologyEntry(id, name));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }
}

/// <summary>One technology shipped inside the assembly. <see cref="Id"/> is the file stem (e.g.
/// <c>"pcb-2layer_RO4350B_20mil_1oz"</c>) — stable, filesystem-safe, and what
/// <see cref="ShippedTechnologies.DefaultId"/> and the New Workspace combobox key off.
/// <see cref="ResourceName"/> is the raw embedded-resource manifest name, internal to how the entry
/// is actually loaded.</summary>
public sealed record ShippedTechnologyEntry(string Id, string ResourceName);
