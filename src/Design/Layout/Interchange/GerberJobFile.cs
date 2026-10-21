// .gbrjob job file (docs/sonnet-briefs/brief-L4c-gerber-export.md §2, R-L4c-2) — the X2 answer to
// "which files belong together," §8's own stated hard part of Gerber. A minimal but valid job-file JSON
// (Ucamco's own extension) listing every file in the export set with its FileFunction/FilePolarity.
// Serialized via System.Text.Json rather than hand-built strings so path escaping (backslashes on
// Windows, quotes) is never a source of a malformed file.

using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

using System.Globalization;

namespace CircuitRF.Design.Layout.Interchange;

public static class GerberJobFile
{
    public sealed record FileAttribute(string Path, string? FileFunction, string FilePolarity = "Positive");

    private sealed class JobFileModel
    {
        public JobFileHeader Header { get; set; } = new();
        public List<JobFileEntry> FilesAttributes { get; set; } = [];
    }

    private sealed class JobFileHeader
    {
        public JobFileGenSoftware GenerationSoftware { get; set; } = new();
        public string CreationDate { get; set; } = "";
    }

    private sealed class JobFileGenSoftware
    {
        public string Vendor { get; set; } = "circuitRF";
        public string Application { get; set; } = "circuitRF";
        public string Version { get; set; } = "";
    }

    private sealed class JobFileEntry
    {
        public string Path { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FileFunction { get; set; }

        public string FilePolarity { get; set; } = "Positive";
    }

    public static void Write(Stream stream, IReadOnlyList<FileAttribute> files, DateTime creationTimeUtc, string version)
    {
        var model = new JobFileModel
        {
            Header = new JobFileHeader
            {
                GenerationSoftware = new JobFileGenSoftware { Version = version },
                // Invariant for the same reason as GerberWriter's %TF.CreationDate: ':' in a custom
                // date format is the culture's time separator, so this field silently stopped being
                // ISO-8601 under a Finnish locale.
                CreationDate = creationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            },
            FilesAttributes = files.Select(f => new JobFileEntry
            {
                Path = f.Path,
                FileFunction = f.FileFunction,
                FilePolarity = f.FilePolarity,
            }).ToList(),
        };

        JsonSerializer.Serialize(stream, model, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Reading (brief-L4g-gerber-import-orchestration.md R-L4g-5 rung 0, R-L4g-9) ───────────────
    //
    // The job file is the single most valuable file in a set, because it is the only one that settles
    // SET MEMBERSHIP and LAYER IDENTITY together — it says which files belong to this board at all,
    // which is something no individual file can say about itself. It also carries the board's layer
    // count, its overall thickness and a full material stackup.
    //
    // Read TOLERANTLY, and never fail the import over it: a job file that parses badly, names a file
    // that is not there, or omits half its optional fields is still worth every field it does carry.
    // Property names are matched case-insensitively for the same reason GerberReadResult's attribute
    // dictionary is: the same file set spells a file function "Soldermask" in the artwork and
    // "SolderMask" in its own job file.

    /// <summary>One file the job file claims as part of this board.</summary>
    public sealed record JobFileEntryRead(string Path, string? FileFunction, string? FilePolarity);

    /// <summary>One entry of the job file's <c>MaterialStackup</c>, top to bottom. <see cref="Type"/>
    /// is the format's own word — <c>Copper</c>, <c>Dielectric</c>, <c>SolderMask</c>, <c>Legend</c>,
    /// <c>SolderPaste</c>. Every measurement is nullable because every one of them is optional in the
    /// format and a real export may omit it (R-L4g-9).</summary>
    public sealed record JobStackupEntry(
        string Type,
        string? Name,
        string? Material,
        double? ThicknessMm,
        double? DielectricConstant,
        double? LossTangent);

    public sealed record JobFileContents(
        IReadOnlyList<JobFileEntryRead> Files,
        IReadOnlyList<JobStackupEntry> MaterialStackup,
        int? LayerNumber,
        double? BoardThicknessMm,
        IReadOnlyList<string> Diagnostics);

    /// <summary>Reads a <c>.gbrjob</c>. Returns null only when the text is not JSON at all — every
    /// other shortfall comes back as an empty list plus a diagnostic.</summary>
    public static JobFileContents? Read(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return ReadLayerList(json);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var diagnostics = new List<string>();
            var files = new List<JobFileEntryRead>();
            var stackup = new List<JobStackupEntry>();

            if (Property(root, "FilesAttributes") is { ValueKind: JsonValueKind.Array } fileArray)
                foreach (var entry in fileArray.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (Str(entry, "Path") is not { Length: > 0 } path) continue;
                    files.Add(new JobFileEntryRead(path, Str(entry, "FileFunction"), Str(entry, "FilePolarity")));
                }
            else
                diagnostics.Add("The job file names no files (no FilesAttributes), so it settled no layer identities.");

            if (Property(root, "MaterialStackup") is { ValueKind: JsonValueKind.Array } stackArray)
                foreach (var entry in stackArray.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    string type = Str(entry, "Type") ?? "";
                    if (type.Length == 0) continue;
                    stackup.Add(new JobStackupEntry(
                        type,
                        Str(entry, "Name") ?? Str(entry, "Notes"),
                        Str(entry, "Material"),
                        Num(entry, "Thickness"),
                        Num(entry, "DielectricConstant"),
                        Num(entry, "LossTangent")));
                }

            int? layerNumber = null;
            double? boardThickness = null;
            if (Property(root, "GeneralSpecs") is { ValueKind: JsonValueKind.Object } specs)
            {
                if (Num(specs, "LayerNumber") is { } n) layerNumber = (int)Math.Round(n);
                boardThickness = Num(specs, "BoardThickness");
            }

            return new JobFileContents(files, stackup, layerNumber, boardThickness, diagnostics);
        }
    }

    // ── The layer list ─────────────────────────────────────────────────────────────────────────
    //
    // An older companion of a Gerber set, in braces rather than JSON: one block per layer naming its file and its
    // type —
    //
    //     {Layers
    //         {Layer  1  {File BOARD.TOP} … {Type Top}    }
    //         {Layer  2  {File BOARD.GND} … {Type Inner}  }
    //         {Layer  3  {File BOARD.BOT} … {Type Bottom} }
    //         {Layer  4  {File BOARD.SMT} … {Type Other}  }
    //
    // Its types say which files are copper and on which side, top to bottom in layer order, and nothing about the
    // rest. A set carrying one names its files by extension alone (.TOP, .GND, .BOT, .SMT, .SSB …), so without it
    // the copper was unidentified, the stackup came out EMPTY and the mask and silkscreen layers had no side of the
    // board to belong to (designer report). Read by its structure, never by the program that wrote it.

    private static readonly Regex LayerBlock = new(@"\{\s*Layer\s+(\d+)\b(.*?)(?=\{\s*Layer\s+\d+\b|\z)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex LayerFile = new(@"\{\s*File\s+([^}\r\n]+?)\s*\}", RegexOptions.CultureInvariant);
    private static readonly Regex LayerType = new(@"\{\s*Type\s+([^}\r\n]+?)\s*\}", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="head"/> is a brace-structured layer list: a <c>{Layers</c> block whose
    /// <c>{Layer n</c> entries name a <c>{File …}</c> and a <c>{Type …}</c>.</summary>
    public static bool LooksLikeLayerList(string head) =>
        Regex.IsMatch(head, @"\{\s*Layers\b", RegexOptions.CultureInvariant)
        && LayerBlock.Matches(head).Any(m => LayerFile.IsMatch(m.Value) && LayerType.IsMatch(m.Value));

    /// <summary>A layer list read as a job file: each layer's file, copper given its place in the stack
    /// (<c>Copper,L2,Inr</c>) and everything else no function at all, which leaves it to the rungs below. Null when
    /// the text is not a layer list either.</summary>
    public static JobFileContents? ReadLayerList(string text)
    {
        if (!LooksLikeLayerList(text)) return null;
        var layers = LayerBlock.Matches(text)
            .Select(m => (Number: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                          File: LayerFile.Match(m.Value) is { Success: true } f ? f.Groups[1].Value.Trim() : null,
                          Side: LayerType.Match(m.Value) is { Success: true } t ? CopperSide(t.Groups[1].Value) : null))
            .Where(l => l.File is { Length: > 0 })
            .OrderBy(l => l.Number)
            .ToList();

        var copper = layers.Where(l => l.Side is not null).ToList();
        var files = layers.Select(l => new JobFileEntryRead(l.File!,
            l.Side is { } side ? $"Copper,L{copper.IndexOf(l) + 1},{side}" : null, null)).ToList();
        var diagnostics = new List<string>();
        if (copper.Count == 0) diagnostics.Add("The layer list names no copper layer, so it settled no layer identities.");
        return new JobFileContents(files, [], copper.Count > 0 ? copper.Count : null, null, diagnostics);
    }

    /// <summary>A layer type's copper side, in FileFunction's words — or null for a layer that is not copper.</summary>
    private static string? CopperSide(string type) => type.Trim().ToLowerInvariant() switch
    {
        "top" => "Top",
        "bottom" or "bot" => "Bot",
        "inner" or "internal" or "plane" or "signal" => "Inr",
        _ => null,
    };

    /// <summary>Case-insensitive property lookup — <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
    /// is ordinal, and this format is written by many hands.</summary>
    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var property in obj.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    private static string? Str(JsonElement obj, string name) =>
        Property(obj, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>A measurement, which the format writes either as a bare number or — in some
    /// exports — as an object carrying its own <c>Value</c>. Both are read; anything else is absent.</summary>
    private static double? Num(JsonElement obj, string name)
    {
        if (Property(obj, name) is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out double d) ? d : null,
            JsonValueKind.String => double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture, out double s) ? s : null,
            JsonValueKind.Object => Num(value, "Value"),
            _ => null,
        };
    }
}
