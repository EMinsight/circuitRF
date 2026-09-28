// brief-em3d-74 — which material record a thermal run reads k from, and what it is not asked of. ONE rule, read by the run
// (ThermalRunService) and by check (C3dThermal.Setup), so a setup check passes is a setup the run meshes.
//
// THE LOOK-THROUGH (brief 73's handover). A workspace's own .ctech may carry a record written before thermal properties
// existed — "Gold" with Sigma20 and no ThermalK — and TechModel.FindMaterial answers with that record, shadowing the
// library's Gold that states k. Refusing such a design would make every existing workspace unusable for thermal until
// its owner copied numbers by hand, while the number the user would copy is exactly the library's. So: the technology's
// own record when it states k; else a same-name record of the technology's libraries, then of the shipped generic
// library, that states k — and the run says which one it used. Only a material stating k nowhere is refused.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Thermal;

/// <summary>A material's thermal record: where its k comes from, and the note saying so when it is not the technology's
/// own record.</summary>
public sealed record ThermalMaterialRecord(TechMaterial Material, string? LookThroughNote);

public static class ThermalMaterials
{
    private static readonly Lazy<IReadOnlyList<TechMaterial>> Generic = new(() =>
    {
        try { return MaterialLibraries.LoadGeneric(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Text.Json.JsonException) { return []; }
    });

    /// <summary>True when a solid of <paramref name="material"/> (an elaborated name, <c>Gold@85</c> allowed) or role is not
    /// meshed: air never is (overview §1b).</summary>
    public static bool NotMeshed(Em3dRole role, string material)
    {
        if (role == Em3dRole.Air) return true;
        string baseName = BaseName(material);
        return string.Equals(baseName, Em3dGenerator.AirMaterial, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(baseName, C3dProblemAssembly.VacuumMaterial, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An elaborated material name without its <c>@…</c> suffix.</summary>
    public static string BaseName(string material) => material.Split('@')[0];

    /// <summary>
    /// The record <paramref name="name"/>'s k is read from, or null when no record of the name states k. Null too when the
    /// technology does not define the name at all (the elaboration has already refused that).
    /// </summary>
    public static ThermalMaterialRecord? Find(Technology? tech, string name)
    {
        var own = tech?.FindMaterial(name);
        if (own is null) return null;
        if (StatesK(own)) return new(own, null);
        foreach (var lm in tech!.LibraryMaterials)
            if (string.Equals(lm.Material.Name, name, StringComparison.OrdinalIgnoreCase) && StatesK(lm.Material))
                return new(lm.Material, Note(name, "its technology's material library"));
        foreach (var gm in Generic.Value)
            if (string.Equals(gm.Name, name, StringComparison.OrdinalIgnoreCase) && StatesK(gm))
                return new(gm, Note(name, "circuitRF's shipped generic material library"));
        return null;
    }

    /// <summary>
    /// brief-em3d-76 — the record solid <paramref name="solid"/>'s k is read from: its OWN technology's (a placed layout's
    /// materials are that layout's technology's, not the document's), by the name it has there; <see cref="Find"/>'s
    /// look-through applies. Null when no record states k.
    /// </summary>
    public static ThermalMaterialRecord? For(C3dElaboration e, string solid, string elaboratedMaterial)
        => e.SolidMaterials.TryGetValue(solid, out var own) ? Find(own.Technology, own.Material) : Find(e.Technology, BaseName(elaboratedMaterial));

    /// <summary>The technology and material name <paramref name="solid"/> was built from, as <see cref="For"/> reads them.</summary>
    public static (Technology? Technology, string Material) Source(C3dElaboration e, string solid, string elaboratedMaterial)
        => e.SolidMaterials.TryGetValue(solid, out var own) ? (own.Technology, own.Material) : (e.Technology, BaseName(elaboratedMaterial));

    /// <summary>
    /// brief-em3d-77 R-em3d77-3c — the record a bond wire's σ(T) and k(T) are read from: <see cref="For"/>'s (the technology, its
    /// libraries, the shipped library — em-3d.md §4.1a's order) when it states both; else a metal a <c>.wBond</c> defines only in
    /// its own list, which states no k, is read from the shipped library's record of the same name, and
    /// <paramref name="note"/> says so. Null when no record states both.
    /// </summary>
    public static ThermalMaterialRecord? ForWire(C3dElaboration e, string solid, string elaboratedMaterial, out string? note)
    {
        note = null;
        var rec = For(e, solid, elaboratedMaterial);
        if (rec is not null && ThermalProperties.SigmaAt(rec.Material, 20) is not null) { note = rec.LookThroughNote; return rec; }
        string name = BaseName(elaboratedMaterial);
        foreach (var gm in Generic.Value)
            if (string.Equals(gm.Name, name, StringComparison.OrdinalIgnoreCase) && StatesK(gm) && ThermalProperties.SigmaAt(gm, 20) is not null)
            {
                note = $"Wire metal '{name}' states no thermal conductivity where the wire is defined; the thermal run reads its σ(T) and k(T) " +
                       "from the same-name record in circuitRF's shipped generic material library.";
                return new(gm, note);
            }
        return null;
    }

    public static bool StatesK(TechMaterial m) => m.ThermalK is not null || m.ThermalKVsTemp is { Count: > 0 };

    private static string Note(string name, string where)
        => $"Material '{name}' states no thermal conductivity in the technology; the thermal run reads it from the same-name record " +
           $"in {where}. Add ThermalK to the technology's own record to use a different value.";
}
