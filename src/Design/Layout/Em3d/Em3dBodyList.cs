// brief-dfr31-1 R-dfr31-1-4 — the bodies of a static problem as a terminal picker lists them: each by its net (or its
// generated name), the layers it is on and how many objects it holds, and the name a terminal row writes for it.
//
// Built from the conductors the run itself builds (a 3D view's elaboration, a .cem's layout solids), so a row the picker
// fills resolves to exactly that body when the run resolves it (Em3dConductorNames).

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>
/// brief-dfr31-1 — one row of the terminal picker. <paramref name="Token"/> is what a row writes: the body's net when it
/// carries one, else its generated name (<c>body:</c> and its largest member), which resolves to that member and so to the
/// whole body.
/// </summary>
public sealed record Em3dBodyChoice(string Name, string Token, IReadOnlyList<string> Layers, IReadOnlyList<string> Members,
                                    IReadOnlyList<string> Nets)
{
    /// <summary>What the picker shows: the name, its layers and its size.</summary>
    public string Label => $"{Name} — {(Layers.Count == 0 ? "drawn" : string.Join(", ", Layers))} — " +
                           (Members.Count == 1 ? "1 object" : $"{Members.Count} objects");

    /// <summary>True when <paramref name="name"/>, as a terminal row would state it, names this body: its token, a member,
    /// a net, or a member's path suffix.</summary>
    public bool IsNamedBy(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        if (name == Token || name == Name || Nets.Contains(name, StringComparer.Ordinal)) return true;
        if (name.StartsWith(Em3dBodies.GeneratedPrefix, StringComparison.Ordinal)) name = name[Em3dBodies.GeneratedPrefix.Length..];
        return Members.Any(m => m == name || m.EndsWith("/" + name, StringComparison.Ordinal));
    }

    public override string ToString() => Token;
}

public static class Em3dBodyList
{
    /// <summary>The bodies of a 3D view's elaboration, as a terminal of one of its setups sees them (what is not modelled
    /// left out).</summary>
    public static IReadOnlyList<Em3dBodyChoice> Of(CircuitRF.Design.ThreeD.C3dDocument document, CircuitRF.Design.ThreeD.C3dElaboration drawn)
    {
        var e = CircuitRF.Design.ThreeD.C3dModelled.Filter(document, drawn);
        return Of(e.Solids, e.Sheets, e.CylinderSheets, n => e.ObjectNets.TryGetValue(n, out var s) ? s : [], e.Origins);
    }

    /// <summary>The bodies of a layout's solids (a <c>.cem</c>'s).</summary>
    public static IReadOnlyList<Em3dBodyChoice> Of(Em3dLayoutSolidsResult r)
        => r.Ok ? Of(r.Solids, r.Sheets, null, n => r.ObjectNets.TryGetValue(n, out var s) ? s : [], r.Origins) : [];

    public static IReadOnlyList<Em3dBodyChoice> Of(IReadOnlyList<Em3dSolid> solids, IReadOnlyList<Em3dSheet> sheets,
                                                   IReadOnlyList<Em3dCylinderSheet>? cylinderSheets,
                                                   Func<string, IEnumerable<string>> netsOf,
                                                   IReadOnlyDictionary<string, Em3dObjectOrigin> origins)
    {
        var grouping = Em3dBodies.Group(solids, sheets, cylinderSheets, netsOf);
        return [.. grouping.Bodies.Select(b => new Em3dBodyChoice(
            b.Name, b.Generated ? b.Name : b.Nets[0],
            [.. b.Members.Select(m => origins.GetValueOrDefault(m)?.StackupEntry).OfType<string>().Distinct(StringComparer.Ordinal)],
            b.Members, b.Nets))];
    }
}
