using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One material the picker lists: the name, what it implies, and the file it comes from.</summary>
public sealed record MaterialChoice(string Name, string Role, string Source);

/// <summary>Where a new material is saved: the technology's own list (<see cref="LibraryPath"/> null) or a library.</summary>
public sealed record MaterialSaveTarget(string Label, string? LibraryPath)
{
    public override string ToString() => Label;
}

/// <summary>
/// brief-em3d-53 R-em3d53-5 — the 3D editor's material picker. <b>Assign</b> lists the technology's resolved
/// materials with their role and source; <b>New</b> hosts the same Materials table on one new row, with a
/// <i>Save to</i> selector (M9: the technology's first library if it names one, else its own list). It decides
/// nothing about files: the workspace commits the answer to that file's own document.
/// </summary>
public sealed partial class MaterialPickerViewModel : ObservableObject
{
    private readonly HashSet<string> _taken;
    private readonly List<TechMaterial> _newRows;

    public MaterialPickerViewModel(Technology tech, string technologyLabel, bool startNew, string? current, string? refusal)
    {
        Choices = [.. tech.ResolvedMaterials.Select(m => new MaterialChoice(m.Name,
            C3dMaterialRole.Reason(C3dMaterialRole.Implied(m)),
            tech.LibrarySourceOf(m.Name) is { } lib ? Path.GetFileName(MaterialLibraries.Display(lib)) : technologyLabel))];
        SelectedChoice = Choices.FirstOrDefault(c => string.Equals(c.Name, current, StringComparison.OrdinalIgnoreCase));
        _taken = new HashSet<string>(tech.ResolvedMaterials.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        int n = 1;
        while (_taken.Contains($"Material{n}")) n++;
        _newRows = [new TechMaterial { Name = $"Material{n}" }];
        NewTable = new MaterialsTableViewModel(() => _newRows, (mutate, _) => mutate(), technologyLabel);

        Targets = [.. tech.ResolvedLibraryPaths.Select(p => new MaterialSaveTarget($"Library {Path.GetFileName(MaterialLibraries.Display(p))}", p)),
                   new MaterialSaveTarget($"Technology {technologyLabel} (its own list)", null)];
        SelectedTarget = Targets[0];
        IsNew = startNew || Choices.Count == 0;
        Refusal = refusal;
    }

    public IReadOnlyList<MaterialChoice> Choices { get; }
    [ObservableProperty] private MaterialChoice? _selectedChoice;

    /// <summary>True: making a new material; false: choosing one.</summary>
    [ObservableProperty] private bool _isNew;

    /// <summary>The one new row, in the same table the Materials editor uses.</summary>
    public MaterialsTableViewModel NewTable { get; }

    public IReadOnlyList<MaterialSaveTarget> Targets { get; }
    [ObservableProperty] private MaterialSaveTarget? _selectedTarget;

    /// <summary>Why the list is empty (a technology that did not resolve), or what OK refused.</summary>
    [ObservableProperty] private string? _refusal;

    /// <summary>The new row, once accepted.</summary>
    public TechMaterial NewMaterial => _newRows[0];

    /// <summary>Validates OK: a choice, or a new row with a free, legal name. Null when it may close.</summary>
    public string? Accept()
    {
        if (!IsNew)
            return SelectedChoice is null ? "Choose a material, or make a new one." : null;
        var m = NewMaterial;
        if (MaterialValidation.NameRefusal(m.Name) is { } why) return why;
        if (_taken.Contains(m.Name)) return $"'{m.Name}' is already a material of this technology.";
        if (SelectedTarget is null) return "Choose where the material is saved.";
        return null;
    }

    /// <summary>The material the objects take.</summary>
    public string ChosenName => IsNew ? NewMaterial.Name : SelectedChoice?.Name ?? "";
}
