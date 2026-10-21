// A board's two surfaces, role by role — brief-bsp-1 R-bsp1-3/R-bsp1-4 (series overview D2).
//
// A footprint draws on surface roles only: copper, soldermask, paste, silkscreen, courtyard and
// assembly. Each role has a layer on the TOP surface and one on the BOTTOM surface, FOUND in the
// technology rather than declared — the same rules LandPatternLayers has always used for the front,
// and by the same code, with the side as a parameter. A bottom-side placement moves each role's top
// layer to its bottom layer; every other layer (inner copper, drill, outline, any drawing layer that
// is not a surface role) stays exactly where it is.

using System.Runtime.CompilerServices;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>The six roles a footprint draws on.</summary>
public enum SurfaceRole { Copper, Soldermask, Paste, Silkscreen, Courtyard, Assembly }

/// <summary>The layer each <see cref="SurfaceRole"/> resolved to on ONE surface; null where the
/// technology declares nothing for it.</summary>
public sealed record SurfaceRoleLayers(
    LayerKey? Copper,
    LayerKey? Soldermask,
    LayerKey? Paste,
    LayerKey? Silkscreen,
    LayerKey? Courtyard,
    LayerKey? Assembly)
{
    public LayerKey? this[SurfaceRole role] => role switch
    {
        SurfaceRole.Copper     => Copper,
        SurfaceRole.Soldermask => Soldermask,
        SurfaceRole.Paste      => Paste,
        SurfaceRole.Silkscreen => Silkscreen,
        SurfaceRole.Courtyard  => Courtyard,
        _                      => Assembly,
    };
}

/// <summary>
/// The surface roles of one technology on both sides, and the layer swap a bottom-side placement
/// applies. Built once per <see cref="Technology"/> INSTANCE and cached on it; the technology is
/// never mutated (technologies are shared through <c>TechnologyCache</c>).
/// </summary>
public sealed class SurfaceRoles
{
    private static readonly ConditionalWeakTable<Technology, SurfaceRoles> _cache = new();

    /// <summary>What a layer becomes on the other surface: a key, or null when its role has no layer
    /// there (the shape is OMITTED). A layer absent from this map is not a surface role and maps to
    /// itself.</summary>
    private readonly Dictionary<LayerKey, (LayerKey? Other, SurfaceRole Role)> _swap = new();

    public SurfaceRoleLayers Top    { get; }
    public SurfaceRoleLayers Bottom { get; }

    /// <summary>
    /// A value identity for the swap this technology applies — equal for two technology instances
    /// whose roles resolve to the same layers. The bottom-view cache keys on it rather than on the
    /// technology instance, so the layout editor's draw technology (a clone that changes whenever a
    /// layer is hidden) and the technology it was cloned from share one bottom view, and a renderer
    /// compile-cache entry with it.
    /// </summary>
    internal string Signature { get; }

    /// <summary>False when there is no bottom copper (a single-sided board, or not a board at all):
    /// a bottom placement is then a REFUSAL — there is nothing to solder the part to.</summary>
    public bool HasBottomCopper => Top.Copper is not null && Bottom.Copper is not null;

    private SurfaceRoles(SurfaceRoleLayers top, SurfaceRoleLayers bottom)
    {
        Top = top;
        Bottom = bottom;

        var roles = Enum.GetValues<SurfaceRole>();
        // Pairs first, omissions second, and the first role to claim a layer keeps it — so a layer
        // that plays two roles (a technology naming one layer both courtyard and assembly) is swapped
        // when either role has a twin, rather than omitted because the other does not.
        foreach (var role in roles)
            if (top[role] is { } t && bottom[role] is { } b && t != b)
            {
                _swap.TryAdd(t, (b, role));
                _swap.TryAdd(b, (t, role));
            }
        foreach (var role in roles)
        {
            if (role == SurfaceRole.Copper) continue;   // a missing copper twin is the refusal, not an omission
            if (top[role] is { } t && bottom[role] is null) _swap.TryAdd(t, (null, role));
            if (bottom[role] is { } b && top[role] is null) _swap.TryAdd(b, (null, role));
        }

        Signature = string.Join(";", _swap.OrderBy(kv => kv.Key.Layer).ThenBy(kv => kv.Key.Datatype)
            .Select(kv => $"{kv.Key.Layer}/{kv.Key.Datatype}>{(kv.Value.Other is { } o ? $"{o.Layer}/{o.Datatype}" : "-")}"));
    }

    /// <summary>The roles of <paramref name="technology"/>, cached on the instance.</summary>
    public static SurfaceRoles For(Technology technology)
    {
        ArgumentNullException.ThrowIfNull(technology);
        return _cache.GetValue(technology, static t => new SurfaceRoles(ResolveSide(t, PlacementSide.Top),
                                                                         ResolveSide(t, PlacementSide.Bottom)));
    }

    /// <summary>
    /// R-bsp1-4. The layer <paramref name="key"/> moves to on the other surface: a role's top layer
    /// to that role's bottom layer and back. Every layer that is not a surface role — inner copper,
    /// drill, outline — maps to itself. <b>Null when the role has no layer on the other surface</b>:
    /// the shape is omitted, never relocated onto another layer (R-fp1-3a).
    /// </summary>
    public LayerKey? Swap(LayerKey key)
        => _swap.TryGetValue(key, out var e) ? e.Other : key;

    /// <summary>The role whose shapes on <paramref name="key"/> a flip omits, or null when a flip keeps
    /// them (swapped, or not a surface role).</summary>
    public SurfaceRole? OmittedRole(LayerKey key)
        => _swap.TryGetValue(key, out var e) && e.Other is null ? e.Role : null;

    /// <summary>The words a sentence uses for <paramref name="role"/>.</summary>
    public static string Describe(SurfaceRole role) => role switch
    {
        SurfaceRole.Copper     => "copper",
        SurfaceRole.Soldermask => "soldermask",
        SurfaceRole.Paste      => "paste",
        SurfaceRole.Silkscreen => "silkscreen",
        SurfaceRole.Courtyard  => "courtyard",
        _                      => "assembly",
    };

    // ── Resolution — LandPatternLayers' rules, generalised to a side ─────────────────────────────

    /// <summary>One optional role's aliases and words. <see cref="Words"/> are matched against a layer's
    /// name only when the name also says it is on the side being resolved.</summary>
    private sealed record RoleSpec(string FrontAlias, string BackAlias, string[] Words, string Purpose);

    private static readonly RoleSpec MaskSpec      = new("F.Mask",  "B.Mask",  ["soldermask", "solder mask"], "soldermask");
    private static readonly RoleSpec PasteSpec     = new("F.Paste", "B.Paste", ["paste"], "paste");
    private static readonly RoleSpec SilkSpec      = new("F.SilkS", "B.SilkS", ["silkscreen", "silk"], "silkscreen");
    private static readonly RoleSpec CourtyardSpec = new("F.CrtYd", "B.CrtYd", ["courtyard", "assembly"], "courtyard");
    private static readonly RoleSpec AssemblySpec  = new("F.Fab",   "B.Fab",   ["fabrication"], "assembly");

    private static SurfaceRoleLayers ResolveSide(Technology t, PlacementSide side)
    {
        var copper = ResolveCopper(t, side, PCellLayerSelection.Default, []);
        var roles = new SurfaceRoleLayers(
            copper,
            ResolveOptional(t, side, MaskSpec),
            ResolveOptional(t, side, PasteSpec),
            ResolveOptional(t, side, SilkSpec),
            ResolveOptional(t, side, CourtyardSpec),
            ResolveOptional(t, side, AssemblySpec));
        if (side == PlacementSide.Top) return roles;

        // A bottom role that resolved to the TOP role's own layer is not a second surface — a
        // technology tagging both mask layers with one Purpose and no side in either name would
        // otherwise "swap" the mask onto itself and report nothing missing.
        var top = ResolveSide(t, PlacementSide.Top);
        LayerKey? Distinct(LayerKey? b, LayerKey? tk) => b is { } k && k == tk ? null : b;
        return new SurfaceRoleLayers(
            Distinct(roles.Copper, top.Copper), Distinct(roles.Soldermask, top.Soldermask),
            Distinct(roles.Paste, top.Paste), Distinct(roles.Silkscreen, top.Silkscreen),
            Distinct(roles.Courtyard, top.Courtyard), Distinct(roles.Assembly, top.Assembly));
    }

    /// <summary>
    /// The copper of one surface of a BOARD. Top: an explicit <c>F.Cu</c> alias, else the topmost
    /// stackup conductor provided it sits on a laminate — <see cref="LandPatternLayers"/>' rule,
    /// whose remarks give the reasoning, unchanged. Bottom: <c>B.Cu</c>, else the LAST stackup
    /// conductor's drawing layer, provided it is not the top one and a laminate sits on it.
    /// </summary>
    internal static LayerKey? ResolveCopper(
        Technology technology, PlacementSide side, PCellLayerSelection layerSelection, List<string> diagnostics)
    {
        string alias = side == PlacementSide.Top ? "F.Cu" : "B.Cu";
        foreach (var layer in technology.Layers)
            if (string.Equals(layer.Interchange?.PcbLayerName, alias, StringComparison.OrdinalIgnoreCase))
                return layer.Key;

        var conductors = technology.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (side == PlacementSide.Bottom)
        {
            if (conductors.Count < 2 || conductors[^1].DrawingLayers.Count == 0) return null;
            var bottom = conductors[^1];
            int bottomIndex = technology.Stackup.Layers.IndexOf(bottom);
            var above = bottomIndex > 0 ? technology.Stackup.Layers[bottomIndex - 1] : null;
            return above is { Kind: StackupKind.Dielectric } && above.Epsr > 1.0 ? bottom.DrawingLayers[0] : null;
        }

        var top = conductors.Count > 0 ? conductors[0] : null;
        if (top is null || top.DrawingLayers.Count == 0)
        {
            diagnostics.Add(
                $"technology '{technology.Name}' declares no front-copper layer, so there is nothing to put " +
                "lands on. A land pattern needs a top conductor with a drawing layer, or a layer aliased F.Cu.");
            return null;
        }

        int topIndex = technology.Stackup.Layers.IndexOf(top);
        var beneath = topIndex >= 0 && topIndex + 1 < technology.Stackup.Layers.Count
            ? technology.Stackup.Layers[topIndex + 1]
            : null;

        bool onLaminate = beneath is { Kind: StackupKind.Dielectric } && beneath.Epsr > 1.0;
        if (!onLaminate)
        {
            string what = beneath is null
                ? "nothing beneath it"
                : beneath.Kind == StackupKind.Dielectric
                    ? $"the dielectric '{beneath.Name}' (relative permittivity {beneath.Epsr:G4})"
                    : $"'{beneath.Name}', which is not a dielectric";
            diagnostics.Add(
                $"technology '{technology.Name}' has no front-copper layer a part can be soldered to: its topmost " +
                $"conductor '{top.Name}' sits on {what}, so it is not a laminate surface. A chip land pattern " +
                "needs a board front copper (F.Cu); this technology is not a board technology.");
            return null;
        }

        // The signal-layer override is honoured the same way every other PCell honours it, and by
        // the same function, so a land pattern and an MLIN on one design never disagree about which
        // conductor is the top one.
        return SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
    }

    internal static LayerKey? ResolveMask(Technology t)      => ResolveOptional(t, PlacementSide.Top, MaskSpec);
    internal static LayerKey? ResolveSilk(Technology t)      => ResolveOptional(t, PlacementSide.Top, SilkSpec);
    internal static LayerKey? ResolveCourtyard(Technology t) => ResolveOptional(t, PlacementSide.Top, CourtyardSpec);
    internal static LayerKey? ResolveAssembly(Technology t)  => ResolveOptional(t, PlacementSide.Top, AssemblySpec);

    /// <summary>
    /// An optional role on one side: the board-format alias first — the technology author's own
    /// statement — then <see cref="LayerDef.Purpose"/>, then the layer's own NAME. Name matching
    /// requires the layer to say it is on the side being resolved, because a technology declares
    /// both sides and putting a top-side land pattern's silkscreen on Silk Bottom is a defect that
    /// renders perfectly. A bottom Purpose match needs a back side word in the name too: a purpose
    /// carries no side, and the top has always taken the first one.
    /// </summary>
    private static LayerKey? ResolveOptional(Technology technology, PlacementSide side, RoleSpec spec)
    {
        bool front = side == PlacementSide.Top;
        string pcbAlias = front ? spec.FrontAlias : spec.BackAlias;

        foreach (var layer in technology.Layers)
            if (string.Equals(layer.Interchange?.PcbLayerName, pcbAlias, StringComparison.OrdinalIgnoreCase))
                return layer.Key;

        foreach (var layer in technology.Layers)
            if (layer.Purpose is { Length: > 0 } p &&
                string.Equals(p.Trim(), spec.Purpose, StringComparison.OrdinalIgnoreCase) &&
                (front || IsBackSide(layer.Name ?? "")))
                return layer.Key;

        foreach (var layer in technology.Layers)
        {
            string name = layer.Name ?? "";
            if (string.Equals(name, pcbAlias, StringComparison.OrdinalIgnoreCase)) return layer.Key;
            if (!(front ? IsFrontSide(name) : IsBackSide(name))) continue;
            foreach (string w in spec.Words)
                if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return layer.Key;
        }

        return null;
    }

    private static bool IsFrontSide(string name)
        => name.StartsWith("F.", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("top", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("front", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] BackWords = ["bottom", "bot", "btm", "back"];

    /// <summary>A back side word as a WHOLE word — "bot" is a side, "Robot" is not.</summary>
    private static bool IsBackSide(string name)
    {
        if (name.StartsWith("B.", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var word in name.Split([' ', '_', '-', '.', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries))
            foreach (var back in BackWords)
                if (string.Equals(word, back, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
