// brief-em3d-94 — materials from the 3D view. R-em3d94-1: the hover's material lines know what the scene does not (the
// material's own record, the active thermal setup, why an object has no values). R-em3d94-2: Edit Material… on a material's
// header in the By-material tree and on an object's row, which the workspace answers by opening the file the material is
// defined in (EditMaterialRequested, the SetupAnalysesRequested pattern: this view model holds no workspace type).

using CircuitRF.Design.Layout;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>Edit Material…: the material's name, and the technology file whose resolution defines it (the document's, or a
    /// placed cell's own). Set by the workspace, which opens the <c>.cmat</c> or the technology's Materials tab on its row.</summary>
    public Action<string, string?>? EditMaterialRequested { get; set; }

    /// <summary>Why Edit Material… is disabled on a design that resolves no technology.</summary>
    public const string EditMaterialNoTechnology =
        "This 3D view resolves no technology, so its materials are defined nowhere yet. Give it one with 3D ▸ Materials….";

    /// <summary>The material <paramref name="name"/> of the tree's scene object, and the technology it was built from: the
    /// elaboration's record, else (an object it refused) the document's technology and the name the object states.</summary>
    private (string Material, string? TechnologyPath)? MaterialOfRow(C3dTreeItem item)
    {
        if (Elaboration?.MaterialOrigins.TryGetValue(item.Name, out var origin) == true) return (origin.Material, origin.TechnologyPath);
        if (item.ObjectIndex >= 0 && item.ObjectIndex < Document.Objects.Count && item.OperandPath is null && item.FeaturePath is null
            && C3dValidation.EffectiveMaterial(Document.Objects[item.ObjectIndex]) is { Length: > 0 } m)
            return (m, Elaboration?.TechnologyPath);
        return null;
    }

    /// <summary>An object row's Edit Material 'X'… — naming the other technology when the row is a placed cell's part whose
    /// material is that cell's — or null for a row with no material.</summary>
    private Viewer3DMenuItem? EditMaterialItem(C3dTreeItem item)
    {
        if (MaterialOfRow(item) is not { } found) return null;
        string elsewhere = found.TechnologyPath is { } p && !SameFile(p, Elaboration?.TechnologyPath) ? $" (in {Path.GetFileName(p)})" : "";
        return EditMaterialItem($"Edit Material '{found.Material}'{elsewhere}…", found.Material, found.TechnologyPath);
    }

    private Viewer3DMenuItem EditMaterialItem(string header, string material, string? technologyPath)
        => technologyPath is null
            ? new Viewer3DMenuItem(header, Enabled: false, Tip: EditMaterialNoTechnology)
            : new Viewer3DMenuItem(header, () => EditMaterialRequested?.Invoke(material, technologyPath),
                                   Tip: "Open the file this material is defined in — its technology, or the library the technology names — on its row.");

    /// <summary>A material header's Edit Material…, in the document's technology; null for any other header.</summary>
    private Viewer3DMenuItem? EditMaterialItem(C3dTreeGroup group)
        => group.MaterialName is { } m ? EditMaterialItem("Edit Material…", m, Elaboration?.TechnologyPath) : null;

    private static bool SameFile(string a, string? b)
        => b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>R-em3d94-1 — the hover's side of a scene object: its material's own record, the active thermal setup, and why an
    /// object drawn as a wireframe has no values.</summary>
    private MaterialHoverContext? HoverContextOf(Scene3DObject o)
    {
        if (Elaboration is not { } e) return null;
        if (o.Wireframe)
        {
            string? stated = Document.Objects.FirstOrDefault(x => x.Name == o.Name) is { } obj ? C3dValidation.EffectiveMaterial(obj) : null;
            return new MaterialHoverContext(Unstated: stated is { Length: > 0 }
                ? $"'{stated}': its technology does not define it, so it is not in the solve"
                : MaterialHover.NoMaterial);
        }
        if (o.Material is not { Length: > 0 } material) return null;
        var (tech, name) = ThermalMaterials.Source(e, o.Name, material);
        MaterialHoverThermal? thermal = null;
        if (ActiveSetup is { IsThermal: true } setup && o.Role != CircuitRF.Engine.Em3d.Em3dRole.Air)
            thermal = new MaterialHoverThermal(setup.OperatingTempC ?? CircuitRF.Design.Layout.Em.EmSetup.DefaultOperatingTempC,
                                               setup.Thermal?.Zth is not null || setup.Thermal?.Pulse is not null,
                                               ThermalMaterials.For(e, o.Name, material)?.Material);
        return new MaterialHoverContext(tech?.FindMaterial(name), thermal);
    }
}
