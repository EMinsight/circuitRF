namespace CircuitRF.Engine.Em3d;

/// <summary>
/// 3D editor round 3 — which solid owns a region two of them overlap: <b>metal always takes precedence over
/// dielectric</b>, whatever order they were drawn in (em-3d.md §6.3a). A conductor solid and every sheet are metal.
/// <para>
/// <b>Air is not dielectric here.</b> With no booleans, an air solid drawn after a metal is how a hole is made in it — a
/// plated via's bore is exactly that (R-em3d3-5d) — so air keeps construction order against metal: an air solid drawn
/// after the first metal ranks with the metals, in its own order among them, and one drawn before every metal (the air
/// above a layout's stack) ranks with the dielectrics.
/// </para>
/// <para>
/// It is stated as a SHIFT rather than a re-numbering: the metal band's priority is its <see cref="Em3dSolid.Order"/>
/// plus the smallest amount that puts it above every dielectric, which is zero for the ordinary problem (a layout's
/// metals are generated after its substrates). So a problem that already obeyed the rule is written with the same
/// priorities it always was — the same bytes — and only a dielectric drawn after a metal changes.
/// </para>
/// Both solver writers (CSXCAD's priorities, gmsh's cut order), the face boundaries layered above every solid and the
/// renderers' paint order read this one rule; none re-derives it from <c>Order</c>.
/// </summary>
public sealed class Em3dPrecedence
{
    /// <summary>What the metal band's order is raised by.</summary>
    public int Shift { get; }

    /// <summary>The highest priority any solid or sheet takes — a port or a boundary goes above it.</summary>
    public int Max { get; }

    /// <summary>The first metal's order: an air solid drawn after it is in the metal band. Null with no metal.</summary>
    private readonly int? _firstMetal;

    private Em3dPrecedence(int shift, int max, int? firstMetal) { Shift = shift; Max = max; _firstMetal = firstMetal; }

    /// <summary>The precedence of <paramref name="problem"/>'s solids and sheets.</summary>
    public static Em3dPrecedence Of(Em3dProblem problem) => Of(problem.Solids, problem.Sheets);

    /// <summary>The precedence of a set of solids and sheets that is not (yet) a whole problem.</summary>
    public static Em3dPrecedence Of(IReadOnlyCollection<Em3dSolid> solids, IReadOnlyCollection<Em3dSheet> sheets)
    {
        int? firstMetal = null;
        foreach (var s in solids)
            if (s.Role == Em3dRole.Conductor) firstMetal = Math.Min(firstMetal ?? s.Order, s.Order);
        foreach (var sh in sheets) firstMetal = Math.Min(firstMetal ?? sh.Order, sh.Order);

        int? maxLow = null, maxHigh = null;
        foreach (var s in solids)
        {
            if (InMetalBand(s.Role, s.Order, sheet: false, firstMetal)) maxHigh = Math.Max(maxHigh ?? s.Order, s.Order);
            else maxLow = Math.Max(maxLow ?? s.Order, s.Order);
        }
        foreach (var sh in sheets) maxHigh = Math.Max(maxHigh ?? sh.Order, sh.Order);
        int shift = maxLow is { } lo && firstMetal is { } m && m <= lo ? lo + 1 - m : 0;
        int max = Math.Max(maxLow ?? 0, maxHigh is { } hi ? hi + shift : 0);
        return new Em3dPrecedence(shift, max, firstMetal);
    }

    private static bool InMetalBand(Em3dRole role, int order, bool sheet, int? firstMetal)
        => sheet || role == Em3dRole.Conductor || (role == Em3dRole.Air && firstMetal is { } m && order > m);

    /// <summary>The priority <paramref name="s"/> takes where it overlaps another solid: higher wins.</summary>
    public int Of(Em3dSolid s) => Of(s.Role, s.Order, sheet: false);

    /// <summary>The priority <paramref name="sh"/> takes — a sheet is metal.</summary>
    public int Of(Em3dSheet sh) => sh.Order + Shift;

    /// <summary>The priority of a region of the given role and construction order — for a renderer's own region records.</summary>
    public int Of(Em3dRole role, int order, bool sheet) => InMetalBand(role, order, sheet, _firstMetal) ? order + Shift : order;
}
