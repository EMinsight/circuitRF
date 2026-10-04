using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Layout;

// ── Appearance (brief-em3d-105) ──────────────────────────────────────────────────────────────────
//
// What a material, or one object, LOOKS like in the realistic 3D view: glTF 2.0's metallic-roughness parameters (overview D2),
// so the glTF writer (brief 111) writes them through unchanged. One record type, stated on a material (.cmat / a .ctech's
// Materials block) and as an override on a .c3d object or instance, and resolved in one place (AppearanceResolver).
//
// NOTHING HERE IS PHYSICAL. No solver reads it, the solver-equality comparison ignores it (MaterialLibraries.SameValues), a
// run's kept document never holds it (C3dPersistence.SerializeForRun), and a run's manifest hashes a .cmat / .ctech without it
// (C3dRunDocument), so an appearance edit never marks a result out of date. And nothing is derived FROM a physical value: an
// optical index is not √εr (overview §1c).

/// <summary>
/// brief-em3d-105 R-em3d105-1 — how a material (or an object) looks. Every field is optional; null is NOT STATED, and an
/// unstated field comes from the next statement down (<c>AppearanceResolver</c>'s order).
/// </summary>
public sealed class TechAppearance
{
    /// <summary>A metal's reflectance, a dielectric's body colour: <c>#rrggbb</c>, sRGB.</summary>
    public string? BaseColor { get; set; }

    /// <summary>0–1: 1 for a metal, 0 for a dielectric; in between only for blending.</summary>
    public double? Metallic { get; set; }

    /// <summary>0–1: 0 a mirror, 1 matte.</summary>
    public double? Roughness { get; set; }

    /// <summary>0–1: how see-through (glass, quartz, a thin laminate).</summary>
    public double? Transmission { get; set; }

    /// <summary>1.0–3.0: the OPTICAL index — a visual value, never derived from εr.</summary>
    public double? Ior { get; set; }

    /// <summary>0–1: a glossy layer over the body (solder mask, a glossy laminate).</summary>
    public double? Clearcoat { get; set; }

    /// <summary>0–1: the clear coat's own roughness.</summary>
    public double? ClearcoatRoughness { get; set; }

    /// <summary>The tint light picks up passing through: <c>#rrggbb</c>, sRGB.</summary>
    public string? AttenuationColor { get; set; }

    /// <summary>How far light travels before taking <see cref="AttenuationColor"/>, metres (SI, as every <c>.cmat</c> number
    /// is); positive.</summary>
    public double? AttenuationDistance { get; set; }

    /// <summary>A material's name: take that material's resolved appearance first, then this record's own fields over it — how
    /// a plated part is spelled without restating four numbers.</summary>
    public string? Like { get; set; }

    /// <summary>Keys this build does not read, kept and written back — as a material's own are.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    /// <summary>The ten keys, in file order — what <c>reference</c> and the resolver's provenance name.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        nameof(BaseColor), nameof(Metallic), nameof(Roughness), nameof(Transmission), nameof(Ior), nameof(Clearcoat),
        nameof(ClearcoatRoughness), nameof(AttenuationColor), nameof(AttenuationDistance), nameof(Like),
    ];

    /// <summary>True when it states nothing (and keeps no unread key).</summary>
    [JsonIgnore]
    public bool IsEmpty => BaseColor is null && Metallic is null && Roughness is null && Transmission is null && Ior is null
                        && Clearcoat is null && ClearcoatRoughness is null && AttenuationColor is null && AttenuationDistance is null
                        && Like is null && Unread is not { Count: > 0 };

    /// <summary>A copy that shares nothing mutable.</summary>
    public TechAppearance Clone() => new()
    {
        BaseColor = BaseColor, Metallic = Metallic, Roughness = Roughness, Transmission = Transmission, Ior = Ior,
        Clearcoat = Clearcoat, ClearcoatRoughness = ClearcoatRoughness, AttenuationColor = AttenuationColor,
        AttenuationDistance = AttenuationDistance, Like = Like,
        Unread = Unread is null ? null : new Dictionary<string, JsonElement>(Unread),
    };

    /// <summary>
    /// <paramref name="over"/>'s stated fields, and <paramref name="under"/>'s where it states none — the more specific statement
    /// wins field by field (R-em3d105-2d). Null when both are. What flattening an instance writes onto each part.
    /// </summary>
    public static TechAppearance? Over(TechAppearance? over, TechAppearance? under)
    {
        if (under is null) return over?.Clone();
        if (over is null) return under.Clone();
        var r = under.Clone();
        r.BaseColor = over.BaseColor ?? r.BaseColor;
        r.Metallic = over.Metallic ?? r.Metallic;
        r.Roughness = over.Roughness ?? r.Roughness;
        r.Transmission = over.Transmission ?? r.Transmission;
        r.Ior = over.Ior ?? r.Ior;
        r.Clearcoat = over.Clearcoat ?? r.Clearcoat;
        r.ClearcoatRoughness = over.ClearcoatRoughness ?? r.ClearcoatRoughness;
        r.AttenuationColor = over.AttenuationColor ?? r.AttenuationColor;
        r.AttenuationDistance = over.AttenuationDistance ?? r.AttenuationDistance;
        r.Like = over.Like ?? r.Like;
        if (over.Unread is { Count: > 0 })
        {
            r.Unread ??= [];
            foreach (var (k, v) in over.Unread) r.Unread[k] = v;
        }
        return r;
    }

    // ── one field at a time (brief-em3d-108: the Materials editor and the Inspector write a field, never the record) ──────────

    /// <summary>The ten keys' kinds: a colour (<c>#rrggbb</c>), a material's name (<c>Like</c>), or a number.</summary>
    public static bool IsColourKey(string key) => key is nameof(BaseColor) or nameof(AttenuationColor);
    public static bool IsNameKey(string key) => key is nameof(Like);

    /// <summary>What <paramref name="key"/> states: a string for a colour or <c>Like</c>, a double for a number, null when not stated.</summary>
    public object? Get(string key) => key switch
    {
        nameof(BaseColor) => BaseColor, nameof(Metallic) => Metallic, nameof(Roughness) => Roughness,
        nameof(Transmission) => Transmission, nameof(Ior) => Ior, nameof(Clearcoat) => Clearcoat,
        nameof(ClearcoatRoughness) => ClearcoatRoughness, nameof(AttenuationColor) => AttenuationColor,
        nameof(AttenuationDistance) => AttenuationDistance, nameof(Like) => Like,
        _ => throw new ArgumentException($"'{key}' is not an appearance key", nameof(key)),
    };

    /// <summary>
    /// <paramref name="appearance"/> with <paramref name="key"/> stating <paramref name="value"/> (null: not stated) and every other
    /// field, unread keys included, as it was — a copy. Null when the result states nothing, which is how a record whose last field
    /// was reset is written: as no record at all.
    /// </summary>
    public static TechAppearance? With(TechAppearance? appearance, string key, object? value)
    {
        var r = appearance?.Clone() ?? new TechAppearance();
        string? text = value as string;
        double? number = value switch { double d => d, float f => f, int i => i, null => null, _ => null };
        switch (key)
        {
            case nameof(BaseColor): r.BaseColor = text; break;
            case nameof(Metallic): r.Metallic = number; break;
            case nameof(Roughness): r.Roughness = number; break;
            case nameof(Transmission): r.Transmission = number; break;
            case nameof(Ior): r.Ior = number; break;
            case nameof(Clearcoat): r.Clearcoat = number; break;
            case nameof(ClearcoatRoughness): r.ClearcoatRoughness = number; break;
            case nameof(AttenuationColor): r.AttenuationColor = text; break;
            case nameof(AttenuationDistance): r.AttenuationDistance = number; break;
            case nameof(Like): r.Like = string.IsNullOrWhiteSpace(text) ? null : text.Trim(); break;
            default: throw new ArgumentException($"'{key}' is not an appearance key", nameof(key));
        }
        return r.IsEmpty ? null : r;
    }

    /// <summary>The stated fields as one line, in file order (<c>Roughness=0.1;Like=Gold</c>) — a cache key, and how
    /// <c>explain</c> spells an override. Numbers round-trip.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        void Add(string key, object? v)
        {
            if (v is null) return;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(key).Append('=').Append(v is double d ? d.ToString("R", CultureInfo.InvariantCulture) : v.ToString());
        }
        Add(nameof(BaseColor), BaseColor); Add(nameof(Metallic), Metallic); Add(nameof(Roughness), Roughness);
        Add(nameof(Transmission), Transmission); Add(nameof(Ior), Ior); Add(nameof(Clearcoat), Clearcoat);
        Add(nameof(ClearcoatRoughness), ClearcoatRoughness); Add(nameof(AttenuationColor), AttenuationColor);
        Add(nameof(AttenuationDistance), AttenuationDistance); Add(nameof(Like), Like);
        return sb.ToString();
    }
}
