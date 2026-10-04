// brief-em3d-105 R-em3d105-3 — "what does this object look like", answered in ONE place (overview rule 3). The realistic view
// (106), the CPU mirror `render` draws with (110), the glTF writer (111), `explain` and the Inspector (108) all take this answer;
// none of them re-derives a default.
//
// THE ORDER, PER FIELD (highest first):
//
//    1. the object's own statement, then what its Like names
//    2. each instance it sits in, innermost first — its statement, then what its Like names
//    3. the material: its own fields, then what its Like names
//    4. (BaseColor only) the material's display Color
//    5. the role's default (BaseColor: the scene's palette colour, which the caller passes in)
//
// A Like takes the named material's statements exactly as step 3-4 would read them for that material — its own fields, its
// Like, its Color — so `{ "Like": "Gold" }` on a copper object looks like gold. NOTHING IS DERIVED FROM εr, σ OR ANY OTHER
// PHYSICAL VALUE: an optical index is not √εr (overview §1c), and the only link to the material record's other fields is
// step 4. Design, not Render, because the glTF writer and `explain` need it and Design draws nothing.

using System.Collections.Concurrent;
using CircuitRF.Design.Layout;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD.Appearance;

/// <summary>R-em3d105-3b — the roles an appearance is resolved for. Air, ports and boundaries have none.</summary>
public enum AppearanceRole { Conductor, Dielectric, Via, Wire, Body, Sheet }

/// <summary>A colour in LINEAR light, each channel 0–1.</summary>
public readonly record struct AppearanceColour(double R, double G, double B)
{
    public static readonly AppearanceColour White = new(1, 1, 1);

    /// <summary>An sRGB 8-bit colour, decoded to linear light (IEC 61966-2-1's curve).</summary>
    public static AppearanceColour FromSrgb(byte r, byte g, byte b) => new(Linear(r), Linear(g), Linear(b));

    private static double Linear(byte c)
    {
        double s = c / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>brief-em3d-108 — back to sRGB 8-bit, as a <c>.cmat</c> spells a colour (<c>#rrggbb</c>): what the Inspector and the
    /// Materials editor show a resolved colour as. <see cref="FromSrgb"/>'s inverse to the byte.</summary>
    public (byte R, byte G, byte B) ToSrgb() => (Encode(R), Encode(G), Encode(B));

    public string ToHex()
    {
        var (r, g, b) = ToSrgb();
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static byte Encode(double c)
    {
        c = Math.Clamp(c, 0, 1);
        double s = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(s * 255), 0, 255);
    }
}

/// <summary>An appearance with every field concrete: what a renderer draws and the scene's slot table holds. Equal values are
/// the same slot.</summary>
public readonly record struct AppearanceValues(
    AppearanceColour BaseColor, double Metallic, double Roughness, double Transmission, double Ior,
    double Clearcoat, double ClearcoatRoughness, AppearanceColour AttenuationColor, double AttenuationDistance);

/// <summary>R-em3d105-3a — the resolver's answer: the values, and for each field (keyed by its <see cref="TechAppearance"/>
/// key) where it came from: <c>object</c>, <c>instance 'U1'</c>, <c>material 'Copper' (generic-materials.cmat)</c>,
/// <c>Like 'Gold'</c>, <c>role default</c>.</summary>
public sealed record ResolvedAppearance(AppearanceValues Values, IReadOnlyDictionary<string, string> Provenance);

/// <summary>One instance's appearance statement on the way down to an object: its path and what it states.</summary>
public sealed record AppearanceInstanceStatement(string Instance, TechAppearance Appearance);

/// <summary>
/// R-em3d105-2 — what a <c>.c3d</c> says about one object's look on top of its material: the object's own statement (an
/// operation's for its result) and each enclosing instance's, <b>innermost first</b>. Read from the elaboration's provenance.
/// </summary>
public sealed record AppearanceOverride(TechAppearance? Object, IReadOnlyList<AppearanceInstanceStatement> Instances)
{
    /// <summary>The option an elaboration's provenance answers: null for an object nothing in the document restyles.</summary>
    public static Func<string, AppearanceOverride?> Of(IReadOnlyDictionary<string, C3dProvenance> provenance)
        => name => provenance.TryGetValue(name, out var p) && (p.Appearance is not null || p.InstanceAppearances.Count > 0)
            ? new AppearanceOverride(p.Appearance, p.InstanceAppearances)
            : null;
}

/// <summary>One question: the technology the object's material is looked up in, the material's name, the object's role, the
/// material's own role (which decides a <see cref="AppearanceRole.Body"/>'s or a <see cref="AppearanceRole.Sheet"/>'s defaults),
/// the scene's palette colour for it (sRGB), and the document's override.</summary>
public readonly record struct AppearanceRequest(
    Technology? Technology, string? Material, AppearanceRole Role, Em3dRole MaterialRole,
    (byte R, byte G, byte B) Palette, AppearanceOverride? Override = null);

/// <summary>R-em3d105-3 — the only place a look is decided.</summary>
public static class AppearanceResolver
{
    // ── R-em3d105-3d — the role defaults, one table ──────────────────────────────────────────────

    public const double ConductorMetallic = 1, ConductorRoughness = 0.30;
    public const double ViaMetallic = 1, ViaRoughness = 0.25;
    public const double WireMetallic = 1, WireRoughness = 0.20;
    public const double DielectricMetallic = 0, DielectricRoughness = 0.50, DielectricIor = 1.5;
    /// <summary>glTF's own defaults for the fields no role states.</summary>
    public const double DefaultIor = 1.5, DefaultTransmission = 0, DefaultClearcoat = 0, DefaultClearcoatRoughness = 0;
    public const double DefaultAttenuationDistance = double.PositiveInfinity;

    /// <summary>What a provenance says of a role default.</summary>
    public const string RoleDefault = "role default";

    /// <summary>The (metallic, roughness, ior) a role takes when nothing states them. A body or a sheet takes its material's
    /// role's: a conductor's, else a dielectric's.</summary>
    public static (double Metallic, double Roughness, double Ior) Defaults(AppearanceRole role, Em3dRole materialRole) => role switch
    {
        AppearanceRole.Conductor => (ConductorMetallic, ConductorRoughness, DefaultIor),
        AppearanceRole.Via       => (ViaMetallic, ViaRoughness, DefaultIor),
        AppearanceRole.Wire      => (WireMetallic, WireRoughness, DefaultIor),
        AppearanceRole.Dielectric => (DielectricMetallic, DielectricRoughness, DielectricIor),
        _ => materialRole == Em3dRole.Conductor ? (ConductorMetallic, ConductorRoughness, DefaultIor)
                                                : (DielectricMetallic, DielectricRoughness, DielectricIor),
    };

    // ── the cache (R-em3d105-3e) ─────────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, ResolvedAppearance> Cache = new(StringComparer.Ordinal);
    private const int CacheCap = 4096;
    private static long _resolutions;

    /// <summary>How many appearances have been worked out in this process — a cache hit does not count. 108's counter.</summary>
    public static long Resolutions => Interlocked.Read(ref _resolutions);

    /// <summary>
    /// <paramref name="request"/>'s appearance. Pure and deterministic; cached on everything it reads — the statements the walk
    /// finds (their CONTENTS, so a material edited in place is a different key), the role, the material's role and the palette.
    /// </summary>
    public static ResolvedAppearance Resolve(AppearanceRequest request)
    {
        var chain = Chain(request);
        string key = $"{request.Role}|{request.MaterialRole}|{request.Palette.R},{request.Palette.G},{request.Palette.B}|" +
                     string.Join("\u001f", chain.Select(s => s.Label + "\u001e" + s.Appearance.Describe()));
        if (Cache.TryGetValue(key, out var hit)) return hit;
        Interlocked.Increment(ref _resolutions);
        var resolved = Compute(request, chain);
        if (Cache.Count >= CacheCap) Cache.Clear();
        Cache[key] = resolved;
        return resolved;
    }

    /// <summary>The role default alone — what an object takes when the scene's slot table is full (R-em3d105-4).</summary>
    public static AppearanceValues RoleDefaultOf(AppearanceRole role, Em3dRole materialRole, (byte R, byte G, byte B) palette)
        => Resolve(new AppearanceRequest(null, null, role, materialRole, palette)).Values;

    /// <summary>
    /// brief-em3d-108 R-em3d108-1c — <paramref name="tech"/> as the resolver would read it with each material named in
    /// <paramref name="appearances"/> carrying that appearance (null: none) instead of its own: what a 3D view previews a Materials
    /// dialog's unsaved edit with. Only what the resolver reads is carried (the name, the own and library materials, where each came
    /// from), so it is a technology for resolving a look and nothing else. A name the technology does not define is ignored; a
    /// material another's <c>Like</c> names changes that one's look too, exactly as it will once the edit is saved.
    /// </summary>
    public static Technology WithAppearances(Technology tech, IReadOnlyDictionary<string, TechAppearance?> appearances)
    {
        var wanted = new Dictionary<string, TechAppearance?>(appearances, StringComparer.OrdinalIgnoreCase);
        TechMaterial Over(TechMaterial m)
        {
            if (!wanted.TryGetValue(m.Name, out var a)) return m;
            var copy = MaterialLibraryPersistence.Deserialize(MaterialLibraryPersistence.Serialize([m]))[0];
            copy.Appearance = a?.Clone();
            return copy;
        }
        return new Technology
        {
            Name = tech.Name,
            Materials = [.. tech.Materials.Select(Over)],
            LibraryMaterials = [.. tech.LibraryMaterials.Select(l => wanted.ContainsKey(l.Material.Name) ? l with { Material = Over(l.Material) } : l)],
            ResolvedLibraryPaths = tech.ResolvedLibraryPaths,
        };
    }

    // ── the walk ─────────────────────────────────────────────────────────────────────────────────

    private readonly record struct Statement(TechAppearance Appearance, string Label);

    /// <summary>Every statement that may answer a field, highest first. A material's display Color is a statement of BaseColor
    /// alone.</summary>
    private static List<Statement> Chain(AppearanceRequest r)
    {
        var chain = new List<Statement>();
        var tech = r.Technology;
        if (r.Override is { } o)
        {
            if (o.Object is { } own) { chain.Add(new(own, "object")); Like(own.Like); }
            foreach (var i in o.Instances)
            {
                chain.Add(new(i.Appearance, $"instance '{i.Instance}'"));
                Like(i.Appearance.Like);
            }
        }
        if (tech?.FindMaterial(r.Material) is { } m)
            Material(m, $"material '{m.Name}' ({SourceOf(tech, m.Name)})", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return chain;

        void Like(string? name)
        {
            if (tech?.FindMaterial(name) is { } target)
                Material(target, $"Like '{target.Name}'", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        // A material's statements: its own fields, then its Like's (a cycle is check's refusal; here it just stops), then its Color.
        void Material(TechMaterial m, string label, HashSet<string> seen)
        {
            if (!seen.Add(m.Name)) return;
            if (m.Appearance is { } a) chain.Add(new(a, label));
            if (m.Appearance?.Like is { } next && tech!.FindMaterial(next) is { } target)
                Material(target, $"Like '{target.Name}'", seen);
            if (m.Color is not null) chain.Add(new(new TechAppearance { BaseColor = m.Color }, label + " Color"));
        }
    }

    /// <summary>Where a technology's material came from, as a provenance names it: the library's file name, or the technology.</summary>
    private static string SourceOf(Technology tech, string name)
        => tech.LibrarySourceOf(name) is { } path
            ? Path.GetFileName(path.StartsWith(MaterialLibraries.ShippedPrefix, StringComparison.Ordinal) ? path[MaterialLibraries.ShippedPrefix.Length..] : path)
            : $"technology '{tech.Name}'";

    private static ResolvedAppearance Compute(AppearanceRequest r, List<Statement> chain)
    {
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
        var (metallic, roughness, ior) = Defaults(r.Role, r.MaterialRole);

        // A value check refuses is not a statement: it falls through, so the view never draws what check would not accept.
        AppearanceColour Colour(string key, Func<TechAppearance, string?> get, AppearanceColour fallback, string fallbackLabel)
        {
            foreach (var s in chain)
                if (MaterialValidation.ParseColour(get(s.Appearance)) is { } c)
                {
                    provenance[key] = s.Label;
                    return AppearanceColour.FromSrgb(c.R, c.G, c.B);
                }
            provenance[key] = fallbackLabel;
            return fallback;
        }
        double Number(string key, Func<TechAppearance, double?> get, double lo, double hi, double fallback, bool loOpen = false)
        {
            foreach (var s in chain)
                if (get(s.Appearance) is { } v && double.IsFinite(v) && (loOpen ? v > lo : v >= lo) && v <= hi)
                {
                    provenance[key] = s.Label;
                    return v;
                }
            provenance[key] = RoleDefault;
            return fallback;
        }

        var values = new AppearanceValues(
            BaseColor: Colour(nameof(TechAppearance.BaseColor), a => a.BaseColor,
                              AppearanceColour.FromSrgb(r.Palette.R, r.Palette.G, r.Palette.B), RoleDefault + " (the scene's palette)"),
            Metallic: Number(nameof(TechAppearance.Metallic), a => a.Metallic, 0, 1, metallic),
            Roughness: Number(nameof(TechAppearance.Roughness), a => a.Roughness, 0, 1, roughness),
            Transmission: Number(nameof(TechAppearance.Transmission), a => a.Transmission, 0, 1, DefaultTransmission),
            Ior: Number(nameof(TechAppearance.Ior), a => a.Ior, 1, 3, ior),
            Clearcoat: Number(nameof(TechAppearance.Clearcoat), a => a.Clearcoat, 0, 1, DefaultClearcoat),
            ClearcoatRoughness: Number(nameof(TechAppearance.ClearcoatRoughness), a => a.ClearcoatRoughness, 0, 1, DefaultClearcoatRoughness),
            AttenuationColor: Colour(nameof(TechAppearance.AttenuationColor), a => a.AttenuationColor, AppearanceColour.White, RoleDefault),
            AttenuationDistance: Number(nameof(TechAppearance.AttenuationDistance), a => a.AttenuationDistance, 0, double.PositiveInfinity,
                                        DefaultAttenuationDistance, loOpen: true));
        return new ResolvedAppearance(values, provenance);
    }
}
