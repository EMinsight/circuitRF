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
    public static string Serialize(IReadOnlyList<TechMaterial> materials)
        => JsonSerializer.Serialize(new CmatFile { Materials = [.. materials] }, TechPersistence.JsonOpts);

    public static void SaveToFile(string path, IReadOnlyList<TechMaterial> materials)
        => AtomicFile.WriteAllText(path, Serialize(materials));

    /// <summary>
    /// Reads a library. Throws <see cref="InvalidDataException"/> for JSON that is not a <c>.cmat</c>
    /// (no <c>FormatVersion</c> or no <c>Materials</c>) and for a newer <c>FormatVersion</c> than this
    /// build knows, naming it.
    /// </summary>
    public static List<TechMaterial> Deserialize(string json)
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
        return file.Materials ?? [];

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
