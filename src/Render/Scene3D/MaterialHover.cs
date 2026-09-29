// brief-em3d-94 R-em3d94-1 — the hover's material lines, by what the object IS to the solve. A conductor has no use for an εr
// or a tanδ, and a lossless dielectric's σ = 0 says nothing, so neither is shown. The role is the one the elaborator decided
// (Scene3DObject.Role, which a Role override on the object already won), never re-derived from the material: the hover must
// agree with the solve. Pure, and beside the scene model, so a headless label could print the same lines.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

/// <summary>An active thermal setup's side of the hover: the temperature k is read at, whether the setup is transient or pulsed
/// (when ρ and c matter), and the record the thermal run reads k, ρ and c from (null when no record states k).</summary>
public sealed record MaterialHoverThermal(double TempC, bool Transient, TechMaterial? Record);

/// <summary>What the view's owner knows about an object that the scene does not: the material's own record (to say it states
/// nothing), the thermal setup, and why an object with no values has none.</summary>
public sealed record MaterialHoverContext(TechMaterial? Record = null, MaterialHoverThermal? Thermal = null, string? Unstated = null);

public static class MaterialHover
{
    /// <summary>What an object with no material is said to be when its owner gives no reason.</summary>
    public const string NoMaterial = "No material: the solver ignores it";

    /// <summary>The material lines for a scene object, or none for what is not a solid or a sheet (a port, a face).</summary>
    public static IReadOnlyList<string> Lines(Scene3DObject o, double? tempC, MaterialHoverContext? context = null)
    {
        if (o.Kind is Scene3DKind.Port or Scene3DKind.Boundary || o.Tint) return [];
        if (o.MaterialValues is null && !o.Wireframe && context?.Unstated is null) return [];
        return Lines(o.Kind, o.Role, o.MaterialValues, tempC, context);
    }

    /// <summary>
    /// The lines for an object of <paramref name="kind"/> and <paramref name="role"/> made of <paramref name="values"/>, σ read at
    /// <paramref name="tempC"/> (null: no temperature is said). A conductor (a wire, a via, a sheet) shows σ, and μr only when it is
    /// not 1; a dielectric εr (or its tensor) and tanδ, σ only when it is above 0 (a lossy substrate), μr only when it is not 1; air
    /// says <c>Air</c>. With a thermal setup active every solid adds k, and ρ and c when the setup is transient or pulsed.
    /// </summary>
    public static IReadOnlyList<string> Lines(Scene3DKind kind, Em3dRole? role, Em3dMaterial? values, double? tempC,
                                              MaterialHoverContext? context = null)
    {
        var r = role ?? kind switch
        {
            Scene3DKind.Air => Em3dRole.Air,
            Scene3DKind.Dielectric => Em3dRole.Dielectric,
            Scene3DKind.Conductor or Scene3DKind.Wire or Scene3DKind.Via or Scene3DKind.Sheet => Em3dRole.Conductor,
            _ => (Em3dRole?)null,
        };
        if (values is null) return [context?.Unstated ?? NoMaterial];
        if (r == Em3dRole.Air) return ["Air"];
        if (r is null) return [$"{values.Name}: {context?.Unstated ?? "its role is not known"}"];
        if (context?.Record is { } own && MaterialLibraries.Describe(own) == "states nothing")
            return [$"{values.Name}: states nothing (solved as {(r == Em3dRole.Conductor ? "a conductor with σ 0" : "a dielectric with εr 1")})"];

        string at = tempC is { } t ? $" at {G4(t)} °C" : "";
        var parts = new List<string>();
        if (r == Em3dRole.Conductor) parts.Add($"σ {G3(values.SigmaSm)} S/m{at}");
        else
        {
            parts.Add(values.EpsrTensor is [var xx, var yy, var zz] ? $"εr ({G4(xx)}, {G4(yy)}, {G4(zz)})" : $"εr {G4(values.Epsr)}");
            parts.Add($"tanδ {G3(values.TanD)}");
            if (values.SigmaSm > 0) parts.Add($"σ {G3(values.SigmaSm)} S/m{at}");
        }
        if (values.Mur != 1) parts.Add($"μr {G4(values.Mur)}");
        var lines = new List<string> { $"{values.Name}: {string.Join(", ", parts)}" };
        if (context?.Thermal is { } th) lines.AddRange(ThermalLines(th));
        return lines;
    }

    /// <summary>k at the setup's temperature (or the stated tensor); ρ and c when the setup is transient or pulsed.</summary>
    private static IEnumerable<string> ThermalLines(MaterialHoverThermal th)
    {
        var m = th.Record;
        if (m?.ThermalKTensor is [var kx, var ky, var kz]) yield return $"k ({G4(kx)}, {G4(ky)}, {G4(kz)}) W/(m·K)";
        else if (m is not null && ThermalProperties.ThermalKAt(m, th.TempC) is { } k) yield return $"k {G4(k.Value)} W/(m·K) at {G4(th.TempC)} °C";
        else yield return "k not stated (a thermal run refuses it)";
        if (!th.Transient) yield break;
        string rho = m?.DensityKgM3 is { } d ? $"ρ {G4(d)} kg/m³" : "ρ not stated";
        string c = m?.SpecificHeat is { } cp ? $"c {G4(cp)} J/(kg·K)" : "c not stated";
        yield return $"{rho}, {c}";
    }

    private static string G4(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
    private static string G3(double v) => v.ToString("G3", CultureInfo.InvariantCulture);
}
