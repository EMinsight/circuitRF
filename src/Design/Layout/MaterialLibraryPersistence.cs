using System.Text.Json;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout;

// ──────────────────────────────────────────────────────────────────────────────
//  .cmat — a material library (brief-em3d-53 §1a). Two keys, both ALWAYS written:
//
//    { "FormatVersion": 1, "Materials": [ { "Name": "Mould compound", "Epsr": 3.9 } ] }
//
//  `Materials` is a list of TechMaterial, read and written with the .ctech's own serializer options,
//  so a record cut from a .ctech's Materials block into a .cmat — or back — reads identically. There
//  is deliberately no DTO: one record type, one contract.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>The on-disk shape of a <c>.cmat</c>.</summary>
public sealed class CmatFile
{
    public int FormatVersion { get; set; } = MaterialLibraryPersistence.CurrentFormatVersion;
    public List<TechMaterial> Materials { get; set; } = [];

    /// <summary>brief-em3d-73 R-em3d73-3a — thermal boundary resistances between material pairs, read by the same reader
    /// and resolved with the same duplicate rule as the materials. Omitted when null, so every <c>.cmat</c> written before
    /// it round-trips byte for byte.</summary>
    public List<TechThermalInterface>? ThermalInterfaces { get; set; }
}

/// <summary>Reads and writes <c>.cmat</c> material libraries. Framework-free.</summary>
public static class MaterialLibraryPersistence
{
    public const int CurrentFormatVersion = 1;

    /// <summary>The extension, with its dot.</summary>
    public const string Extension = ".cmat";

    /// <summary>
    /// Both keys are written even for an empty library — as with a <c>.c3d</c>, that is how a
    /// <c>.cmat</c> is told from another program's file of the same extension.
    /// </summary>
    public static string Serialize(IReadOnlyList<TechMaterial> materials,
                                   IReadOnlyList<TechThermalInterface>? thermalInterfaces = null)
        => JsonSerializer.Serialize(new CmatFile
        {
            Materials = [.. materials],
            ThermalInterfaces = thermalInterfaces is null ? null : [.. thermalInterfaces],
        }, TechPersistence.JsonOpts);

    /// <summary>
    /// Writes the library. <paramref name="thermalInterfaces"/> null <b>keeps the interfaces the file at
    /// <paramref name="path"/> already holds</b> (brief-em3d-73): the Materials editor edits the materials list only, and a
    /// save of it must not delete a list it never showed.
    /// </summary>
    public static void SaveToFile(string path, IReadOnlyList<TechMaterial> materials,
                                  IReadOnlyList<TechThermalInterface>? thermalInterfaces = null)
    {
        if (thermalInterfaces is null && File.Exists(path))
        {
            try { thermalInterfaces = LoadInterfacesFromFile(path); }
            catch { /* an unreadable file has no interfaces to keep */ }
        }
        AtomicFile.WriteAllText(path, Serialize(materials, thermalInterfaces));
    }

    /// <summary>
    /// Reads a library. Throws <see cref="InvalidDataException"/> for JSON that is not a <c>.cmat</c>
    /// (no <c>FormatVersion</c> or no <c>Materials</c>) and for a newer <c>FormatVersion</c> than this
    /// build knows, naming it.
    /// </summary>
    public static List<TechMaterial> Deserialize(string json) => DeserializeFile(json).Materials;

    /// <summary>The library's thermal interfaces (brief-em3d-73), or null when it states none. Throws as
    /// <see cref="Deserialize"/> does.</summary>
    public static List<TechThermalInterface>? DeserializeInterfaces(string json) => DeserializeFile(json).ThermalInterfaces;

    /// <summary>The whole file at <paramref name="path"/>: its materials and its thermal interfaces.</summary>
    public static CmatFile LoadFileFromFile(string path) => DeserializeFile(GzipTextFile.ReadAllTextAutoGzip(path));

    public static List<TechThermalInterface>? LoadInterfacesFromFile(string path)
        => DeserializeInterfaces(GzipTextFile.ReadAllTextAutoGzip(path));

    /// <summary>The whole file: its materials and its thermal interfaces. Throws as <see cref="Deserialize"/> does.</summary>
    public static CmatFile DeserializeFile(string json)
    {
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !HasKey(doc.RootElement, "FormatVersion") || !HasKey(doc.RootElement, "Materials"))
                throw new InvalidDataException(
                    "not a circuitRF material library: a .cmat is a JSON object with a FormatVersion and a Materials list.");
        }

        var file = JsonSerializer.Deserialize<CmatFile>(json, TechPersistence.JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .cmat file.");
        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".cmat FormatVersion {file.FormatVersion} is newer than this build reads " +
                $"({CurrentFormatVersion}). Update the application.");
        file.Materials ??= [];
        return file;

        static bool HasKey(JsonElement e, string key)
        {
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    public static List<TechMaterial> LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    /// <summary>Cheap classification for a walker: does this file's JSON prefix look like a
    /// <c>.cmat</c>? False for anything unreadable.</summary>
    public static bool LooksLikeCmat(string path)
    {
        try { LoadFromFile(path); return true; }
        catch { return false; }
    }
}

/// <summary>
/// Creates an empty library (brief-em3d-53 R-em3d53-4b's <i>New Library…</i>) — the one function the
/// technology editor calls, as New Cell calls <c>CellCreate</c>.
/// </summary>
public static class MaterialLibraryCreate
{
    /// <summary>
    /// Writes an empty <c>.cmat</c> at <paramref name="path"/> and returns the reference the technology
    /// at <paramref name="ctechPath"/> should add — relative to its directory. Refuses (throws
    /// <see cref="IOException"/>) when the file already exists, since an existing library is added with
    /// <i>Add Library…</i> and is never overwritten.
    /// </summary>
    public static string Create(string path, string ctechPath)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path))
            throw new IOException($"'{path}' already exists. Use Add Library… to name an existing library.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MaterialLibraryPersistence.SaveToFile(path, []);
        return MaterialLibraries.RelativeReference(ctechPath, path);
    }
}
