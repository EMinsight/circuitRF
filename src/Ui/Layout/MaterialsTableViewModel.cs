using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// brief-em3d-53 R-em3d53-4 — <b>the one Materials table</b>, with two homes: a <c>.cmat</c> document
/// (<see cref="MaterialsEditorViewModel"/>) and the technology editor's Materials tab. It edits ONE
/// list of <see cref="TechMaterial"/> it is handed and commits each gesture through the host, which puts
/// it on that file's own undo stack as one entry.
///
/// <para><b>No material rule lives here</b>: problems come from <see cref="MaterialValidation"/>, the Role
/// column from <see cref="C3dMaterialRole"/>, so the table cannot say one thing while <c>check</c> or
/// Simulate says another. Library rows (the technology tab only) are shown read-only, grouped by the file
/// they came from — an edit to a shared library belongs on that file's stack (M4).</para>
/// </summary>
public sealed partial class MaterialsTableViewModel : ObservableObject
{
    private readonly Func<List<TechMaterial>> _list;
    private readonly Action<Action, string> _commit;

    /// <param name="list">The editable list — read each time, since a snapshot undo replaces it.</param>
    /// <param name="commit">Runs a mutation of the list as ONE undo entry of the host, with its description.</param>
    /// <param name="ownSource">What the Source column says for an editable row ("this technology", "this library").</param>
    public MaterialsTableViewModel(Func<List<TechMaterial>> list, Action<Action, string> commit, string ownSource)
    {
        _list = list;
        _commit = commit;
        OwnSource = ownSource;
        Rebuild();
    }

    /// <summary>What the Source column says for a row of the list being edited.</summary>
    public string OwnSource { get; }

    /// <summary>The whole table is read-only — a file in a referenced workspace not toggled editable, or a
    /// read-only file (§1h). <see cref="ReadOnlyReason"/> says why, naming the file.</summary>
    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private string? _readOnlyReason;

    /// <summary>The last refused gesture's sentence (a name with '@', a delete of a material in use); cleared by
    /// the next committed one.</summary>
    [ObservableProperty] private string? _refusal;

    /// <summary>Own rows, then each library's rows (read-only) in their source's order.</summary>
    public ObservableCollection<MaterialRowViewModel> Rows { get; } = [];

    [ObservableProperty] private MaterialRowViewModel? _selectedRow;

    /// <summary>Library records to show read-only below the own rows (the technology tab only).</summary>
    public IReadOnlyList<LibraryMaterial> LibraryRows { get; private set; } = [];

    /// <summary>Where each material is used — stackup entries, bodies, open 3D objects — for the Used-by
    /// column and for Delete's refusal. Null answers nothing.</summary>
    public Func<string, IReadOnlyList<string>>? UsedBy { get; set; }

    /// <summary>
    /// The host's cross-file rename (R-em3d53-4c): given the old and new name, renames the material in the
    /// list AND every open file that names it, one entry on each file's own stack, and returns a sentence
    /// listing unopened files it did not rewrite (or null). Null: the rename stays within this list.
    /// </summary>
    public Func<string, string, string?>? RenameAcross { get; set; }

    /// <summary>Raised after every committed gesture of this table (and on <see cref="Rebuild"/>).</summary>
    public event Action? Changed;

    /// <summary>The material rules' findings for this list, warnings and errors only — what the header counts.</summary>
    public IReadOnlyList<TechProblem> Problems =>
        [.. MaterialValidation.Validate(_list()).Where(p => p.Severity != CircuitRF.Diagnostics.DiagnosticSeverity.Info)];

    public bool HasRows => Rows.Count > 0;

    /// <summary>Re-projects the rows from the host's current list — after an undo, a redo or a reload.
    /// Keeps the selection by name.</summary>
    public void Rebuild(IReadOnlyList<LibraryMaterial>? libraryRows = null)
    {
        if (libraryRows is not null) LibraryRows = libraryRows;
        string? selected = SelectedRow?.Name;
        Rows.Clear();
        foreach (var m in _list()) Rows.Add(new MaterialRowViewModel(this, m, null));
        foreach (var lm in LibraryRows) Rows.Add(new MaterialRowViewModel(this, lm.Material, lm.SourcePath));
        SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.Name, selected, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasRows));
        Changed?.Invoke();
    }

    /// <summary>Selects the row named <paramref name="name"/> (Open Library's "select the row").</summary>
    public void Select(string name)
        => SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    // ── the one commit path ───────────────────────────────────────────────────

    internal bool Edit(Action mutate, string description)
    {
        if (IsReadOnly) { Refusal = ReadOnlyReason ?? "This file is read-only."; return false; }
        Refusal = null;
        _commit(mutate, description);
        Rebuild();
        return true;
    }

    // ── actions ───────────────────────────────────────────────────────────────

    /// <summary>A new material stating nothing — <c>Material1</c>, <c>Material2</c>… — so its Role reads
    /// "states nothing" until it is given σ₂₀ or εr.</summary>
    [RelayCommand]
    public void Add()
    {
        string name = FreshName("Material");
        if (Edit(() => _list().Add(new TechMaterial { Name = name }), $"Add material {name}")) Select(name);
    }

    [RelayCommand]
    public void Duplicate()
    {
        if (SelectedRow is not { } row) return;
        string name = FreshName(row.Name + " copy");
        var copy = TechPersistenceClone(row.Material);
        copy.Name = name;
        if (Edit(() => _list().Add(copy), $"Duplicate material {row.Name}")) Select(name);
    }

    /// <summary>Deletes the selected own row — refused, listing every use, while anything names it (R-em3d53-4c).</summary>
    [RelayCommand]
    public void Delete()
    {
        if (SelectedRow is not { IsLibrary: false } row) return;
        var uses = UsedBy?.Invoke(row.Name) ?? [];
        if (uses.Count > 0)
        {
            Refusal = $"'{row.Name}' is in use, so it was not deleted: {string.Join("; ", uses)}. Change those first.";
            return;
        }
        var m = row.Material;
        Edit(() => _list().Remove(m), $"Delete material {row.Name}");
    }

    /// <summary>Renames an own row. '@' and a name already in the list are refused; a rename to or from
    /// <c>Air</c> warns that the role changes with the name (§1g).</summary>
    public bool Rename(MaterialRowViewModel row, string newName)
    {
        newName = newName.Trim();
        if (row.IsLibrary || string.Equals(newName, row.Name, StringComparison.Ordinal)) return false;
        if (MaterialValidation.NameRefusal(newName) is { } why) { Refusal = why; return false; }
        if (_list().Any(m => !ReferenceEquals(m, row.Material) && string.Equals(m.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            Refusal = $"A material named '{newName}' already exists here.";
            return false;
        }
        string old = row.Name;
        bool airChange = string.Equals(old, "Air", StringComparison.OrdinalIgnoreCase)
                      != string.Equals(newName, "Air", StringComparison.OrdinalIgnoreCase);
        string? note = null;
        if (RenameAcross is { } across)
        {
            if (IsReadOnly) { Refusal = ReadOnlyReason ?? "This file is read-only."; return false; }
            note = across(old, newName);
            Rebuild();
        }
        else if (!Edit(() => row.Material.Name = newName, $"Rename material {old} → {newName}")) return false;
        if (airChange)
            note = (note is null ? "" : note + " ") +
                   "A material named Air is air whatever it states, so this rename changed its role.";
        Refusal = note;
        Select(newName);
        return true;
    }

    /// <summary><c>Material1</c>, <c>Material2</c>… for Add; <c>Gold copy</c>, <c>Gold copy 2</c>… for Duplicate —
    /// the first not already a name here or in a library shown.</summary>
    private string FreshName(string stem)
    {
        var names = new HashSet<string>(_list().Select(m => m.Name).Concat(LibraryRows.Select(l => l.Material.Name)),
                                         StringComparer.OrdinalIgnoreCase);
        bool copy = stem.EndsWith(" copy", StringComparison.Ordinal);
        if (copy && !names.Contains(stem)) return stem;
        for (int i = copy ? 2 : 1; ; i++)
        {
            string name = copy ? $"{stem} {i}" : $"{stem}{i}";
            if (!names.Contains(name)) return name;
        }
    }

    /// <summary>A record copied through the one serializer — bit for bit, unknown keys included.</summary>
    internal static TechMaterial TechPersistenceClone(TechMaterial m)
        => MaterialLibraryPersistence.Deserialize(MaterialLibraryPersistence.Serialize([m]))[0];

    // ── numbers (§1e, §1f) ────────────────────────────────────────────────────

    /// <summary>A value's display: round-trip, so what is shown parses back to the same bits. Null is blank.</summary>
    public static string Show(double? v) => v is { } d ? d.ToString("R", CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// Parses what was typed: blank is NULL (not stated, §1e); a number in the app's own spelling
    /// (<c>41e6</c>), or with an SI prefix the expression engine knows (<c>41M</c>). False for anything else.
    /// </summary>
    public static bool TryParse(string? text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        string t = text.Trim();
        if (NumericText.TryParseDouble(t, out double plain) && double.IsFinite(plain))
        {
            value = plain;
            return true;
        }
        int split = t.Length;
        while (split > 0 && char.IsLetter(t[split - 1])) split--;
        if (split == 0 || split == t.Length) return false;
        string suffix = t[split..];
        if (suffix.Length != 1 || Units.Scale(suffix) is not { } scale) return false;
        if (!NumericText.TryParseDouble(t[..split], out double mantissa)) return false;
        value = mantissa * scale;
        return double.IsFinite(value.Value);
    }
}

/// <summary>One row of the Materials table — one <see cref="TechMaterial"/>. Each field commits on its own, and
/// <b>only the field edited is written</b>: an untouched value is copied bit for bit (§1f).</summary>
public sealed partial class MaterialRowViewModel : ObservableObject
{
    private readonly MaterialsTableViewModel _table;

    internal MaterialRowViewModel(MaterialsTableViewModel table, TechMaterial material, string? librarySource)
    {
        _table = table;
        Material = material;
        LibrarySource = librarySource;
        _nameText = material.Name;
    }

    public TechMaterial Material { get; }

    /// <summary>The library this row came from, or null for a row of the list being edited.</summary>
    public string? LibrarySource { get; }

    public bool IsLibrary => LibrarySource is not null;
    public bool IsEditable => !IsLibrary && !_table.IsReadOnly;
    public string Name => Material.Name;

    /// <summary>The Source column: this file, or the library a row came from.</summary>
    public string SourceLabel => LibrarySource is { } s ? Path.GetFileName(MaterialLibraries.Display(s)) : _table.OwnSource;
    public string? SourceTip => LibrarySource is { } s ? MaterialLibraries.Display(s) : null;

    /// <summary>§1g — a material named Air is air whatever it states; the row is marked.</summary>
    public bool IsAirByName => string.Equals(Material.Name, "Air", StringComparison.OrdinalIgnoreCase);

    public C3dImpliedRole ImpliedRole => C3dMaterialRole.Implied(Material);
    public string Role => ImpliedRole switch
    {
        C3dImpliedRole.Conductor  => "Conductor",
        C3dImpliedRole.Dielectric => "Dielectric",
        C3dImpliedRole.Air        => "Air",
        C3dImpliedRole.Ambiguous  => "Ambiguous",
        _                         => "States nothing",
    };
    public string RoleReason => C3dMaterialRole.Reason(ImpliedRole);

    /// <summary>Where it is used; the tooltip lists them.</summary>
    public IReadOnlyList<string> Uses => _table.UsedBy?.Invoke(Material.Name) ?? [];
    public string UsedByText => Uses.Count switch { 0 => "", 1 => Uses[0], var n => $"{n} uses" };
    public string? UsedByTip => Uses.Count > 1 ? string.Join("\n", Uses) : null;

    /// <summary>A σ(T) or k(T) table is carried and never edited here (placeholders no solver reads).</summary>
    public bool HasTemperatureTables => Material.SigmaVsTemp is { Count: > 0 } || Material.ThermalKVsTemp is { Count: > 0 };
    public string TemperatureTablesNote => HasTemperatureTables ? "has a σ(T) or k(T) table — preserved" : "";

    // ── Name ──────────────────────────────────────────────────────────────────

    private string _nameText;
    public string NameText
    {
        get => _nameText;
        set
        {
            if (!SetProperty(ref _nameText, value ?? "")) return;
            if (!_table.Rename(this, _nameText)) SetProperty(ref _nameText, Material.Name, nameof(NameText));
        }
    }

    // ── numeric fields ────────────────────────────────────────────────────────

    public string EpsrText    { get => MaterialsTableViewModel.Show(Material.Epsr);    set => Set(value, Material.Epsr,    v => Material.Epsr = v,    "εr"); }
    public string TanDText    { get => MaterialsTableViewModel.Show(Material.TanD);    set => Set(value, Material.TanD,    v => Material.TanD = v,    "tanδ"); }
    public string MurText     { get => MaterialsTableViewModel.Show(Material.Mur);     set => Set(value, Material.Mur,     v => Material.Mur = v,     "μr"); }
    public string Sigma20Text { get => MaterialsTableViewModel.Show(Material.Sigma20); set => Set(value, Material.Sigma20, v => Material.Sigma20 = v, "σ₂₀"); }
    public string Alpha20Text { get => MaterialsTableViewModel.Show(Material.Alpha20); set => Set(value, Material.Alpha20, v => Material.Alpha20 = v, "α₂₀"); }
    public string ThermalKText     { get => MaterialsTableViewModel.Show(Material.ThermalK);     set => Set(value, Material.ThermalK,     v => Material.ThermalK = v,     "thermal conductivity"); }
    public string DensityText      { get => MaterialsTableViewModel.Show(Material.DensityKgM3);  set => Set(value, Material.DensityKgM3,  v => Material.DensityKgM3 = v,  "density"); }
    public string SpecificHeatText { get => MaterialsTableViewModel.Show(Material.SpecificHeat); set => Set(value, Material.SpecificHeat, v => Material.SpecificHeat = v, "specific heat"); }

    /// <summary>A library row is edited in its own library's document, never through a technology (M4).</summary>
    private bool Refuse()
    {
        if (!IsLibrary) return false;
        _table.Refusal = $"'{Material.Name}' comes from {SourceLabel}: open that library to edit it, so the change is on its own undo and Save.";
        OnPropertyChanged(string.Empty);
        return true;
    }

    private void Set(string? text, double? current, Action<double?> write, string field)
    {
        if (Refuse()) return;
        if (!MaterialsTableViewModel.TryParse(text, out double? v))
        {
            _table.Refusal = $"'{text}' is not a number. Leave the field empty for \"not stated\".";
            OnPropertyChanged(string.Empty);
            return;
        }
        if (v == current && v.HasValue == current.HasValue) return;
        string name = Material.Name;
        _table.Edit(() => write(v), v is null ? $"Clear {field} of {name}" : $"Set {field} of {name}");
    }

    // ── the εr tensor (§1e: unchecking removes the key) ─────────────────────────

    public bool IsAnisotropic
    {
        get => Material.EpsrTensor is not null;
        set
        {
            if (value == IsAnisotropic || Refuse()) return;
            string name = Material.Name;
            double e = Material.Epsr ?? 1;
            if (!_table.Edit(() => Material.EpsrTensor = value ? [e, e, e] : null,
                             value ? $"Make {name} anisotropic" : $"Make {name} isotropic"))
                OnPropertyChanged();
        }
    }

    public string TensorXxText { get => Tensor(0); set => SetTensor(0, value); }
    public string TensorYyText { get => Tensor(1); set => SetTensor(1, value); }
    public string TensorZzText { get => Tensor(2); set => SetTensor(2, value); }

    private string Tensor(int i) => Material.EpsrTensor is { Length: 3 } t ? MaterialsTableViewModel.Show(t[i]) : "";

    private void SetTensor(int i, string? text)
    {
        if (Material.EpsrTensor is not { Length: 3 } t || Refuse()) return;
        if (!MaterialsTableViewModel.TryParse(text, out double? v) || v is null)
        {
            _table.Refusal = "Each tensor component needs a number; untick Anisotropic to remove the tensor.";
            OnPropertyChanged(string.Empty);
            return;
        }
        if (t[i] == v.Value) return;
        string name = Material.Name;
        _table.Edit(() => { var copy = (double[])t.Clone(); copy[i] = v.Value; Material.EpsrTensor = copy; },
                    $"Set εr {"xyz"[i]}{"xyz"[i]} of {name}");
    }

    // ── text fields ───────────────────────────────────────────────────────────

    public string ColorText
    {
        get => Material.Color ?? "";
        set
        {
            string? v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v == Material.Color || Refuse()) return;
            if (v is not null && !MaterialValidation.IsColour(v))
            {
                _table.Refusal = $"'{v}' is not a colour: write #rrggbb, or leave it empty for the 3D view's own palette.";
                OnPropertyChanged();
                return;
            }
            string name = Material.Name;
            _table.Edit(() => Material.Color = v, v is null ? $"Clear the colour of {name}" : $"Colour {name}");
        }
    }

    public string SourceText
    {
        get => Material.Source ?? "";
        set
        {
            string? v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (v == Material.Source || Refuse()) return;
            string name = Material.Name;
            _table.Edit(() => Material.Source = v, $"Edit the source of {name}");
        }
    }
}
