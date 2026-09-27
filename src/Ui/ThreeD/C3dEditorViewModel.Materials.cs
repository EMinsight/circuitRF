// brief-em3d-53 R-em3d53-5 — materials from the 3D editor. The editor asks; the workspace shows the picker and
// commits the new material to its file's own document, then this document takes it as ONE C3dEdit. Every entry
// point — the canvas menu's Material ▸ New Material… and Assign Material…, the object tree's, the toolbar combo's
// last item and Properties ▸ Material's — raises the same request.

using CircuitRF.Render.Scene3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The last item of every material combo: it opens the picker on a new row rather than naming a material.</summary>
    public const string NewMaterialItem = "New Material…";

    /// <summary>The toolbar's and Properties' combo items: the technology's resolved materials, then <see cref="NewMaterialItem"/>.</summary>
    public IReadOnlyList<string> MaterialChoices => [.. Materials, NewMaterialItem];

    /// <summary>
    /// Raised to show the material picker: the document objects it would assign (empty: none — the result becomes
    /// the toolbar's current material), and whether it opens on a new material rather than the list.
    /// </summary>
    public event Action<IReadOnlyList<int>, bool>? MaterialPickerRequested;

    /// <summary>A material chosen or made in the picker, not yet in an elaboration — kept current until it is.</summary>
    private string? _pendingMaterial;

    /// <summary>Asks for the picker over <paramref name="indices"/>.</summary>
    public void RequestMaterialPicker(IReadOnlyList<int> indices, bool startNew)
    {
        if (MaterialPickerRequested is null)
        {
            StatusMessage = "Materials are chosen through the workspace, which this editor is not attached to.";
            return;
        }
        MaterialPickerRequested(indices, startNew);
    }

    /// <summary>IViewer3DEditHost — the canvas menu's Assign Material….</summary>
    public void AssignMaterial(IReadOnlyList<Scene3DObject> objects)
        => RequestMaterialPicker([.. objects.Select(DocumentIndex).Where(i => i >= 0).Distinct()], startNew: false);

    /// <summary>IViewer3DEditHost — Material ▸ New Material….</summary>
    public void NewMaterial(IReadOnlyList<Scene3DObject> objects)
        => RequestMaterialPicker([.. objects.Select(DocumentIndex).Where(i => i >= 0).Distinct()], startNew: true);

    /// <summary>The picker's answer: <paramref name="material"/> onto <paramref name="indices"/> as ONE edit, or, with
    /// none, the toolbar's current material.</summary>
    public void ApplyPickedMaterial(IReadOnlyList<int> indices, string material)
    {
        _pendingMaterial = material;
        if (indices.Count == 0) { CurrentMaterial = material; return; }
        ChangeObjects(indices.Count == 1 ? $"Material of {Document.Objects[indices[0]].Name}" : $"Material of {indices.Count} objects",
                      indices, o => o.Material = material);
    }

    /// <summary>The toolbar combo's last item is a command, not a material: open the picker and keep what was current.</summary>
    partial void OnCurrentMaterialChanged(string? oldValue, string? newValue)
    {
        if (newValue != NewMaterialItem) return;
        _post(() =>
        {
            CurrentMaterial = oldValue;
            RequestMaterialPicker([], startNew: true);
        });
    }

    /// <summary>
    /// brief-em3d-53 R-em3d53-4c — a material renamed in its technology or library: every object of THIS document
    /// naming it takes the new name, as ONE C3dEdit (a wire's metal is its Material too). False when nothing named it.
    /// </summary>
    public bool RenameMaterial(string oldName, string newName)
    {
        var indices = Enumerable.Range(0, Document.Objects.Count)
            .Where(i => string.Equals(Document.Objects[i].Material, oldName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (indices.Count == 0) return false;
        ChangeObjects($"Rename material {oldName} → {newName}", indices, o => o.Material = newName);
        if (string.Equals(CurrentMaterial, oldName, StringComparison.OrdinalIgnoreCase)) _pendingMaterial = CurrentMaterial = newName;
        return true;
    }

    /// <summary>Which of this document's objects name <paramref name="name"/> — the Used-by column and Delete's refusal.</summary>
    public IReadOnlyList<string> UsesOfMaterial(string name)
        => [.. Document.Objects.Where(o => string.Equals(o.Material, name, StringComparison.OrdinalIgnoreCase))
                               .Select(o => $"3D object '{o.Name}' in {Path.GetFileName(FilePath)}")];

    /// <summary>
    /// 3D editor bugs round 2 — the design's OWN technology reference (<see cref="C3dDocument.TechRef"/>), relative to this
    /// file as a <c>.clay</c>'s is, as ONE undo entry: what the Choose a Technology dialog writes when the design resolved
    /// none and a material had nowhere to come from. The workspace's default is not touched.
    /// </summary>
    public void UseTechnology(string absolutePath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        string reference = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(dir, Path.GetFullPath(absolutePath)))!;
        EditNames($"Technology {Path.GetFileName(absolutePath)}", (doc, _) => { doc.TechRef = reference; return null; });
    }

    /// <summary>The absolute paths of the technologies this document resolved — its own first, then its instances'.</summary>
    public IReadOnlyList<string> TechnologyPaths()
    {
        var list = new List<string>();
        if (Elaboration?.TechnologyPath is { } own) list.Add(own);
        return list;
    }
}
