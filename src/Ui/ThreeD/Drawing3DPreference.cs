// 3D vector copy and drawing export (2026-09-27) — Export Drawing…'s last choices, per USER, in AppPreferences: which
// views, how hidden edges are drawn, legend, text, page and format. Not the sections — a cut's position belongs to one
// model. The shape of Snap3DPreference, test seam included.

using System.Text.Json.Serialization;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.ThreeD;

/// <summary>What Export Drawing… remembers. Every field is a name, not an ordinal, so a reordered enum reads back.</summary>
public sealed class Drawing3DChoices
{
    [JsonPropertyName("views")] public List<string>? Views { get; set; }
    [JsonPropertyName("hidden")] public string? Hidden { get; set; }
    [JsonPropertyName("legend")] public bool? Legend { get; set; }
    [JsonPropertyName("text_as_outlines")] public bool? TextAsOutlines { get; set; }
    [JsonPropertyName("omit_hidden")] public bool? OmitHidden { get; set; }
    [JsonPropertyName("page")] public string? Page { get; set; }
    [JsonPropertyName("landscape")] public bool? Landscape { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
}

public static class Drawing3DPreference
{
    internal static Drawing3DChoices? TestOverrideStore;
    internal static bool TestOverrideActive;

    /// <summary>The last choices, or null before the first export.</summary>
    public static Drawing3DChoices? Preferred
    {
        get => TestOverrideActive ? TestOverrideStore : AppPreferencesIo.Load().Drawing3D;
        set
        {
            if (TestOverrideActive) { TestOverrideStore = value; return; }
            AppPreferencesIo.Update(p => p.Drawing3D = value);
        }
    }
}
