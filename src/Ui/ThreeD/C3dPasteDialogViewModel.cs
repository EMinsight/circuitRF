// brief-em3d-95 R-em3d95-3 / -3a / -4 — the ONE Paste dialog: a Variables section (a pasted name this view already has:
// Reuse, or Rename with a live-checked name), a Boundaries section (a pasted boundary or symmetry plane landing where one
// already is: Keep this document's, or Replace), and a Materials section (a material this technology lacks: a tick to create
// it). Each section shows only when it has a row; a paste with none shows no dialog at all. It decides nothing itself — it
// turns its rows into C3dPasteChoices, and C3dFragment.Apply does the rest.

using System.Collections.ObjectModel;
using CircuitRF.Design.ThreeD;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>How the Paste dialog was left (Cancel is a null result).</summary>
public enum C3dPasteDialogAnswer { Paste, PasteWithoutCreating, ChooseTechnology }

public sealed record C3dPasteDialogResult(C3dPasteDialogAnswer Answer, C3dPasteChoices Choices);

/// <summary>One variable conflict: Reuse (the default) or Rename to <see cref="NewName"/>.</summary>
public sealed partial class C3dPasteVariableRow(C3dVariableConflict conflict, C3dPasteDialogViewModel owner) : ObservableObject
{
    public string Name => conflict.Name;
    public string Here => conflict.Here;
    public string Copy => conflict.Copy;
    public string? Note => conflict.FromParameter ? "A cell parameter in the copy's view: it becomes a plain VAR here." : null;
    public bool HasNote => Note is not null;

    [ObservableProperty] private bool _rename;
    [ObservableProperty] private string _newName = conflict.Suggested;
    [ObservableProperty] private string? _error;

    public bool Reuse { get => !Rename; set => Rename = !value; }

    partial void OnRenameChanged(bool value)
    {
        OnPropertyChanged(nameof(Reuse));
        owner.Validate();
    }

    partial void OnNewNameChanged(string value) => owner.Validate();
}

/// <summary>One boundary or symmetry-plane conflict: Keep this document's (the default) or Replace with the pasted one.</summary>
public sealed partial class C3dPasteBoundaryRow(C3dBoundaryConflict conflict) : ObservableObject
{
    public string Key => conflict.Key;
    public string What => conflict.What;
    public string Here => conflict.Here;
    public string Copy => conflict.Copy;

    [ObservableProperty] private bool _replace;

    public bool Keep { get => !Replace; set => Replace = !value; }

    partial void OnReplaceChanged(bool value) => OnPropertyChanged(nameof(Keep));
}

/// <summary>One missing material, ticked to be created (the default).</summary>
public sealed partial class C3dPasteMaterialRow(C3dMissingMaterial material) : ObservableObject
{
    public string Name => material.Name;
    public string Summary => material.Summary;

    [ObservableProperty] private bool _create = true;
}

public sealed partial class C3dPasteDialogViewModel : ObservableObject
{
    private readonly Func<string, bool> _nameTaken;
    private readonly HashSet<string> _carried;

    /// <param name="nameTaken">Whether the target 3D view already has the name (a VAR or its cell's parameter).</param>
    /// <param name="carried">Every variable name the copy carries (a rename may not take one).</param>
    /// <param name="materialDestination">Where a created material goes (a technology's own list, or its writable library).</param>
    /// <param name="cannotCreate">Why no material can be created here; only Paste Without Creating is then offered.</param>
    public C3dPasteDialogViewModel(C3dPastePlan plan, Func<string, bool> nameTaken, IEnumerable<string> carried,
                                   string? materialDestination, string? cannotCreate, bool canChooseTechnology)
    {
        _nameTaken = nameTaken;
        _carried = carried.ToHashSet(StringComparer.Ordinal);
        foreach (var v in plan.Variables) Variables.Add(new C3dPasteVariableRow(v, this));
        foreach (var b in plan.Boundaries) Boundaries.Add(new C3dPasteBoundaryRow(b));
        foreach (var m in plan.Materials) Materials.Add(new C3dPasteMaterialRow(m));
        MaterialDestination = materialDestination is null ? null : $"Created in {materialDestination}.";
        CannotCreateReason = cannotCreate;
        CanChooseTechnology = canChooseTechnology && Materials.Count > 0;
        Validate();
    }

    public ObservableCollection<C3dPasteVariableRow> Variables { get; } = [];
    public ObservableCollection<C3dPasteBoundaryRow> Boundaries { get; } = [];
    public ObservableCollection<C3dPasteMaterialRow> Materials { get; } = [];

    public bool HasVariables => Variables.Count > 0;
    public bool HasBoundaries => Boundaries.Count > 0;
    public bool HasMaterials => Materials.Count > 0;

    public string? MaterialDestination { get; }
    public string? CannotCreateReason { get; }
    public bool CanCreate => CannotCreateReason is null;
    public bool CanChooseTechnology { get; }

    /// <summary>Paste is offered unless there are materials to create and nowhere to create them.</summary>
    public bool ShowPaste => !HasMaterials || CanCreate;

    /// <summary>Paste Without Creating is offered only when there is a material to create.</summary>
    public bool ShowPasteWithoutCreating => HasMaterials;

    /// <summary>Why the paste was refused (a cycle a Reuse closes): shown, and the dialog stays open.</summary>
    [ObservableProperty] private string? _refusal;

    [ObservableProperty] private bool _isValid;

    /// <summary>Each Rename row's name, checked live: a legal identifier, unused here, and not another row's.</summary>
    public void Validate()
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        bool ok = true;
        foreach (var row in Variables)
        {
            string? error = null;
            if (row.Rename)
            {
                string n = row.NewName.Trim();
                error = C3dResolver.ValidateName(n) is { } bad ? bad
                      : _nameTaken(n) ? $"'{n}' is already a name in this 3D view."
                      : _carried.Contains(n) ? $"'{n}' is another pasted variable."
                      : !chosen.Add(n) ? $"'{n}' is another row's new name."
                      : null;
            }
            row.Error = error;
            ok &= error is null;
        }
        IsValid = ok;
        Refusal = null;
    }

    /// <summary>The answers, for Apply: each Rename row's name, each Replace row's key, and each ticked material when
    /// <paramref name="create"/>.</summary>
    public C3dPasteChoices Choices(bool create)
    {
        var c = new C3dPasteChoices();
        foreach (var v in Variables) c.Variables[v.Name] = v.Rename ? v.NewName.Trim() : null;
        foreach (var b in Boundaries.Where(b => b.Replace)) c.ReplaceBoundaries.Add(b.Key);
        if (create && CanCreate) foreach (var m in Materials.Where(m => m.Create)) c.CreateMaterials.Add(m.Name);
        return c;
    }
}
