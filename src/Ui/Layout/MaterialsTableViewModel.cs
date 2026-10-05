using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Theming;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Render.Scene3D.Look;
using CircuitRF.Ui.Appearance;

namespace CircuitRF.Ui.Layout;

/// <summary>One list of materials the table edits — a technology's own, or one library's — and how an edit of it is
/// committed. The 3D view's Materials dialog hands the table several (the technology's and each library's); a
/// <c>.cmat</c> document and the technology editor's tab hand it one.</summary>
public sealed class MaterialListSource(string label, Func<List<TechMaterial>> list, Action<Action, string> commit, string? libraryPath = null)
{
    /// <summary>What the Source line says for its rows: "this technology", "generic-materials.cmat".</summary>
    public string Label { get; } = label;

    /// <summary>The library file it is, or null for a technology's own list.</summary>
    public string? LibraryPath { get; } = libraryPath;

    /// <summary>The list — read each time, since a snapshot undo replaces it.</summary>
    public Func<List<TechMaterial>> List { get; } = list;

    /// <summary>Runs a mutation of the list as ONE undo entry of its host, with its description.</summary>
    public Action<Action, string> Commit { get; } = commit;

    /// <summary>Why it cannot be edited (a shipped library, a read-only file), or null.</summary>
    public string? ReadOnlyReason { get; init; }

    /// <summary>The list is a library shipped inside circuitRF: its rows carry the built-in mark.</summary>
    public bool IsBuiltIn { get; init; }

    public override string ToString() => Label;
}

/// <summary>
/// brief-em3d-53 R-em3d53-4 — <b>the one Materials editor</b>, with three homes: a <c>.cmat</c> document
/// (<see cref="MaterialsEditorViewModel"/>), the technology editor's Materials tab, and the 3D view's Materials dialog.
/// It edits the <see cref="TechMaterial"/> lists it is handed (<see cref="Sources"/>) and commits each gesture through
/// the list's host, which puts it on that file's own undo stack as one entry.
///
/// <para><b>No material rule lives here</b>: problems come from <see cref="MaterialValidation"/>, the Role
/// from <see cref="C3dMaterialRole"/>, so the editor cannot say one thing while <c>check</c> or
/// Simulate says another. Library rows (the technology tab only) are shown read-only, grouped by the file
/// they came from — an edit to a shared library belongs on that file's stack (M4), which <see cref="OpenLibrary"/>
/// opens.</para>
///
/// <para>Materials editor redesign (2026-09-29): a list and one form for the selected material, every property the record
/// carries on it — εr and its tensor, σ₂₀ and α₂₀, k, density and specific heat, k's tensor, the σ(T) and k(T) tables, the
/// colour and the source — rather than a wide table whose thermal half sat collapsed below it.</para>
/// </summary>
public sealed partial class MaterialsTableViewModel : ObservableObject
{
    /// <param name="list">The editable list — read each time, since a snapshot undo replaces it.</param>
    /// <param name="commit">Runs a mutation of the list as ONE undo entry of the host, with its description.</param>
    /// <param name="ownSource">What the Source line says for an editable row ("this technology", "this library").</param>
    /// <param name="libraryPath">brief-em3d-108 — the <c>.cmat</c> the list is, when it is one: where an appearance's provenance
    /// says a material comes from.</param>
    public MaterialsTableViewModel(Func<List<TechMaterial>> list, Action<Action, string> commit, string ownSource, string? libraryPath = null)
        : this([new MaterialListSource(ownSource, list, commit, libraryPath)]) { }

    /// <summary>A table over several lists — the 3D view's Materials dialog: the technology's own and each library's.</summary>
    public MaterialsTableViewModel(IReadOnlyList<MaterialListSource> sources)
    {
        if (sources.Count == 0) throw new ArgumentException("at least one list", nameof(sources));
        Sources = sources;
        OwnSource = sources[0].Label;
        _targetSource = sources.FirstOrDefault(s => s.ReadOnlyReason is null) ?? sources[0];
        Rebuild();
    }

    /// <summary>The lists this table edits, in the order their rows are listed.</summary>
    public IReadOnlyList<MaterialListSource> Sources { get; }

    /// <summary>Where Add and Duplicate write — chosen in the dialog when there is more than one list. A built-in material is
    /// copied here too, the first time it is edited or assigned.</summary>
    [ObservableProperty] private MaterialListSource _targetSource;

    partial void OnTargetSourceChanged(MaterialListSource value)
    {
        foreach (var r in Rows)
            if (r.IsBuiltIn) r.Rebind(r.Material);
    }

    // ── the materials built into circuitRF ─────────────────────────────────────

    private bool _offersBuiltIns;

    /// <summary>Whether the list offers the materials shipped inside circuitRF (its Built-in toggle). Every host does since
    /// 2026-10-04 — the 3D view's Materials dialog, the technology editor's tab, and a <c>.cmat</c> document, where adopting one
    /// copies it into the library.</summary>
    public bool OffersBuiltIns
    {
        get => _offersBuiltIns;
        init
        {
            _offersBuiltIns = value;
            RecountBuiltIns(Rows.Where(r => !r.IsBuiltIn).Select(r => r.Name));   // the constructor's Rebuild ran before this
        }
    }

    /// <summary>Lists the built-in materials below the rest — those not already listed under the same name. Each is shown as
    /// it ships; editing or assigning one copies it into <see cref="TargetSource"/>, where it is the design's own. Off when the table
    /// is made; a host that remembers it sets it.</summary>
    [ObservableProperty] private bool _showBuiltIns;

    partial void OnShowBuiltInsChanged(bool value)
    {
        Rebuild();
        OnPropertyChanged(nameof(HasBuiltInsToShow));
        OnPropertyChanged(nameof(BuiltInsTip));
    }

    private void RecountBuiltIns(IEnumerable<string> listed)
    {
        var named = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
        BuiltInsNotListed = OffersBuiltIns ? BuiltInNames.Value.Count(n => !named.Contains(n)) : 0;
    }

    /// <summary>The built-in materials' names, read once: <see cref="Rebuild"/> asks on every edit.</summary>
    private static readonly Lazy<string[]> BuiltInNames = new(() => [.. MaterialLibraries.LoadGeneric().Select(m => m.Name)]);

    /// <summary>How many built-in materials the toggle would add — those no list here already names. Zero (a technology naming
    /// the generic library, or that library itself) greys the toggle out, its tooltip saying why: a toggle that adds nothing
    /// read as broken (owner-reported).</summary>
    [ObservableProperty] private int _builtInsNotListed;

    public bool HasBuiltInsToShow => BuiltInsNotListed > 0 || ShowBuiltIns;

    public string BuiltInsTip => HasBuiltInsToShow
        ? $"Show the {BuiltInsNotListed} material{(BuiltInsNotListed == 1 ? "" : "s")} built into circuitRF that these lists do not already have. Editing or assigning one saves a copy to the list new materials go to."
        : "Every material built into circuitRF is already listed here (as generic-materials.cmat), so there is nothing more to show.";

    partial void OnBuiltInsNotListedChanged(int value)
    {
        OnPropertyChanged(nameof(HasBuiltInsToShow));
        OnPropertyChanged(nameof(BuiltInsTip));
    }

    /// <summary>Where a built-in material is copied when it is edited or assigned: <see cref="TargetSource"/>, else the first
    /// list that can be written; null when none can.</summary>
    public MaterialListSource? BuiltInTarget
        => TargetSource.ReadOnlyReason is null ? TargetSource : Sources.FirstOrDefault(s => s.ReadOnlyReason is null);

    /// <summary>
    /// Copies the built-in <paramref name="row"/> into <see cref="BuiltInTarget"/> — after <paramref name="mutate"/>, the edit
    /// that asked for it — as ONE entry. The row's record is this table's own fresh copy of the shipped one, re-read on every
    /// <see cref="Rebuild"/>, so it is the record added; the rebuild then lists it as the target's row, and the built-in row
    /// of that name is gone.
    /// </summary>
    internal bool AdoptBuiltIn(MaterialRowViewModel row, Action? mutate, string description)
    {
        if (BuiltInTarget is not { } target)
        {
            Refusal = $"'{row.Name}' is built into circuitRF, and none of these lists can be written to take a copy of it.";
            return false;
        }
        var m = row.Material;
        return Edit(target, () => { mutate?.Invoke(); target.List().Add(m); }, description);
    }

    public bool HasSeveralSources => Sources.Count > 1;

    /// <summary>Filters the list by name, role or source; the rows it hides are hidden, not removed.</summary>
    [ObservableProperty] private string _filterText = "";

    partial void OnFilterTextChanged(string value)
    {
        foreach (var r in Rows) r.RefreshShown();
    }

    /// <summary>The Materials chapter — where the '?' of every Materials editor goes (the .cmat document, the 3D view's
    /// Materials dialog, and the technology editor's Materials tab). Registered in DocAnchors, so the docs build fails if the
    /// page stops being emitted.</summary>
    public const string HelpPage = "reference/materials.html";

    /// <summary>Whether Delete is offered — the button and the row's context menu. Every host offers it now; the 3D view's
    /// dialog used to withhold it.</summary>
    public bool CanDelete { get; init; } = true;

    /// <summary>Opens a library row's own document on that row (the technology tab). Null: not offered.</summary>
    public Action<MaterialRowViewModel>? OpenLibrary { get; set; }

    /// <summary>Why a row's name may not be edited here, or null. The 3D view's dialog refuses renaming a material that
    /// already existed: a rename there would leave every file naming it pointing at nothing.</summary>
    public Func<MaterialRowViewModel, string?>? RenameRefusal { get; set; }

    /// <summary>Raised after Add or Duplicate: the view puts the caret in the new row's name, selected, to be typed over.</summary>
    public event Action? NameFocusRequested;

    /// <summary>What the Source column says for a row of the list being edited.</summary>
    public string OwnSource { get; }

    /// <summary>The whole table is read-only — a file in a referenced workspace not toggled editable, or a
    /// read-only file (§1h). <see cref="ReadOnlyReason"/> says why, naming the file.</summary>
    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private string? _readOnlyReason;

    partial void OnIsReadOnlyChanged(bool value)
    {
        foreach (var r in Rows) r.Rebind(r.Material);
    }

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
    /// Asked before every delete, with the uses <see cref="UsedBy"/> found: the host adds what only it can see (files that are
    /// not open), warns when anything still names the material, and answers whether to go ahead. Null: a material in use is
    /// refused, listing its uses (the technology editor's Materials tab).
    /// </summary>
    public Func<string, IReadOnlyList<string>, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>
    /// The host's cross-file rename (R-em3d53-4c): given the old and new name, renames the material in the
    /// list AND every open file that names it, one entry on each file's own stack, and returns a sentence
    /// listing unopened files it did not rewrite (or null). Null: the rename stays within this list.
    /// </summary>
    public Func<string, string, string?>? RenameAcross { get; set; }

    /// <summary>Raised after every committed gesture of this table (and on <see cref="Rebuild"/>).</summary>
    public event Action? Changed;

    /// <summary>The material rules' findings for these lists, warnings and errors only — what the header counts.</summary>
    public IReadOnlyList<TechProblem> Problems =>
        [.. Sources.SelectMany(src => MaterialValidation.Validate(src.List()))
                   .Where(p => p.Severity != CircuitRF.Diagnostics.DiagnosticSeverity.Info)];

    public bool HasRows => Rows.Count > 0;

    /// <summary>
    /// Re-projects the rows from the host's current list — after every committed field, an undo, a redo or a
    /// reload. Keeps the selection by name (by position when a rename took the name away).
    ///
    /// <para><b>The rows are reconciled IN PLACE, never cleared and re-added.</b> Every field commits on
    /// LostFocus, and each commit reaches here twice (the host's snapshot command rebuilds on Execute, then
    /// <see cref="Edit"/> does). Clearing the collection destroyed every row's container on each pass, so
    /// moving from one text box to the next after typing made the whole table flash, swapped the detail
    /// panel's DataContext, and threw away the box focus had just moved into. A row view model is re-pointed
    /// at the snapshot's record instead (<see cref="MaterialRowViewModel.Rebind"/>); only a change in the
    /// number of rows, or an own row turning into a library row, adds, removes or replaces a container.</para>
    /// </summary>
    public void Rebuild(IReadOnlyList<LibraryMaterial>? libraryRows = null)
    {
        if (libraryRows is not null) LibraryRows = libraryRows;
        var selectedRow = SelectedRow;
        string? selected = selectedRow?.Name;
        int selectedIndex = selectedRow is null ? -1 : Rows.IndexOf(selectedRow);
        int countBefore = Rows.Count;

        var wanted = new List<(TechMaterial Material, MaterialListSource? Own, string? Library, bool BuiltIn)>();
        foreach (var src in Sources)
            foreach (var m in src.List()) wanted.Add((m, src, null, false));
        foreach (var lm in LibraryRows) wanted.Add((lm.Material, null, lm.SourcePath, false));
        RecountBuiltIns(wanted.Select(w => w.Material.Name));
        if (OffersBuiltIns && ShowBuiltIns)
        {
            // A fresh copy each time: an adopted record belongs to its list from then on, and an undo must not find it edited here.
            var listed = new HashSet<string>(wanted.Select(w => w.Material.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var m in MaterialLibraries.LoadGeneric())
                if (listed.Add(m.Name)) wanted.Add((m, null, null, true));
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            var (m, own, library, builtIn) = wanted[i];
            if (i >= Rows.Count) Rows.Add(new MaterialRowViewModel(this, m, own, library, builtIn));
            else if (ReferenceEquals(Rows[i].Source, own) && Rows[i].IsBuiltIn == builtIn
                     && string.Equals(Rows[i].LibrarySource, library, StringComparison.OrdinalIgnoreCase))
                Rows[i].Rebind(m);
            else Rows[i] = new MaterialRowViewModel(this, m, own, library, builtIn);
        }
        while (Rows.Count > wanted.Count) Rows.RemoveAt(Rows.Count - 1);

        SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.Name, selected, StringComparison.OrdinalIgnoreCase))
                   ?? (selectedIndex >= 0 && Rows.Count == countBefore && selectedIndex < Rows.Count ? Rows[selectedIndex] : null);
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasRows));
        Changed?.Invoke();
    }

    /// <summary>Selects the row named <paramref name="name"/> (Open Library's "select the row", Edit Material…). A filter that
    /// hides that row is cleared: the view scrolls the selection into view, and a hidden row cannot be seen there.</summary>
    public void Select(string name)
    {
        var row = Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row is { IsShown: false }) FilterText = "";
        SelectedRow = row;
    }

    // ── brief-em3d-108 R-em3d108-1 — appearance ─────────────────────────────────

    /// <summary>A material's appearance was edited — previewed while a slider drags, or written: the row and the appearance it now
    /// shows. The 3D view's Materials dialog forwards it to every open 3D view that draws the material (R-em3d108-1c).</summary>
    public event Action<MaterialRowViewModel, TechAppearance?>? AppearanceEdited;

    internal void RaiseAppearanceEdited(MaterialRowViewModel row, TechAppearance? appearance) => AppearanceEdited?.Invoke(row, appearance);

    /// <summary>
    /// <paramref name="m"/>'s look, resolved by the one resolver over these lists as a technology would hold them (a library list's
    /// rows as that library's), with <paramref name="preview"/> standing for its own appearance while a slider drags. The role is the
    /// one the material implies; the palette colour a 3D view would draw it in is the role's neutral one, since no scene is open here.
    /// </summary>
    internal ResolvedAppearance ResolveAppearance(TechMaterial m, TechAppearance? preview, bool previewing)
    {
        var own = new List<TechMaterial>();
        var libraries = new List<LibraryMaterial>();
        foreach (var src in Sources)
            foreach (var x in src.List())
                if (src.LibraryPath is { } lib) libraries.Add(new LibraryMaterial(x, lib));
                else own.Add(x);
        libraries.AddRange(LibraryRows);
        string name = OwnSource.EndsWith(" (the technology's own)", StringComparison.Ordinal) ? OwnSource[..^" (the technology's own)".Length] : OwnSource;
        Technology tech = new() { Name = name, Materials = own, LibraryMaterials = libraries };
        if (previewing) tech = AppearanceResolver.WithAppearances(tech, new Dictionary<string, TechAppearance?> { [m.Name] = preview });
        var implied = C3dMaterialRole.Implied(m);
        bool conductor = implied == C3dImpliedRole.Conductor;
        var palette = C3dMaterialRole.ImpliedColour(implied);
        return AppearanceResolver.Resolve(new AppearanceRequest(tech, m.Name, conductor ? AppearanceRole.Conductor : AppearanceRole.Dielectric,
                                                                conductor ? CircuitRF.Engine.Em3d.Em3dRole.Conductor : CircuitRF.Engine.Em3d.Em3dRole.Dielectric,
                                                                palette));
    }

    /// <summary>The names a material's <c>Like</c> may take: every material these lists and the libraries shown hold.</summary>
    internal IReadOnlyList<string> AllMaterialNames
        => [.. Sources.SelectMany(src => src.List()).Select(m => m.Name).Concat(LibraryRows.Select(l => l.Material.Name))
                      .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The environment the swatch is lit by: the default studio (the realistic view's own default), prefiltered once a process.</summary>
    internal static PrefilteredEnvironment SwatchEnvironment => EnvironmentPrefilter.Studio(C3dLook.DefaultStudio);

    // ── the one commit path ───────────────────────────────────────────────────

    internal bool Edit(MaterialListSource source, Action mutate, string description)
    {
        if (IsReadOnly) { Refusal = ReadOnlyReason ?? "This file is read-only."; return false; }
        if (source.ReadOnlyReason is { } why) { Refusal = why; return false; }
        Refusal = null;
        source.Commit(mutate, description);
        Rebuild();
        return true;
    }

    // ── actions ───────────────────────────────────────────────────────────────

    /// <summary>A new material stating nothing — <c>Material1</c>, <c>Material2</c>… — so its Role reads
    /// "states nothing" until it is given σ₂₀ or εr. Written to <see cref="TargetSource"/>.</summary>
    [RelayCommand]
    public void Add()
    {
        string name = FreshName("Material");
        var target = TargetSource;
        if (Edit(target, () => target.List().Add(new TechMaterial { Name = name }), $"Add material {name}"))
        {
            Select(name, target);
            NameFocusRequested?.Invoke();
        }
    }

    /// <summary>A copy of the selected material — own or library, every value and table — under a new name, written to
    /// <see cref="TargetSource"/> and selected with its name ready to be typed over.</summary>
    [RelayCommand]
    public void Duplicate()
    {
        if (SelectedRow is not { } row) return;
        string name = FreshName(row.Name + " copy");
        var copy = TechPersistenceClone(row.Material);
        copy.Name = name;
        var target = TargetSource;
        if (Edit(target, () => target.List().Add(copy), $"Duplicate material {row.Name}"))
        {
            Select(name, target);
            NameFocusRequested?.Invoke();
        }
    }

    /// <summary>Deletes the selected own row. With <see cref="ConfirmDelete"/> the host warns about every use and the delete
    /// goes ahead if confirmed; without it, a material in use is refused, listing every use (R-em3d53-4c).</summary>
    [RelayCommand]
    public async Task Delete()
    {
        if (!CanDelete || SelectedRow is not { IsLibrary: false, IsBuiltIn: false, Source: { } source } row) return;
        string name = row.Name;
        var uses = UsedBy?.Invoke(name) ?? [];
        if (ConfirmDelete is { } confirm)
        {
            if (!await confirm(name, uses)) return;
        }
        else if (uses.Count > 0)
        {
            Refusal = $"'{name}' is in use, so it was not deleted: {string.Join("; ", uses)}. Change those first.";
            return;
        }
        // The list may have changed while the warning was up (an undo, a reload): delete the record only if it is still there.
        var m = row.Material;
        if (!source.List().Contains(m)) return;
        Edit(source, () => source.List().Remove(m), $"Delete material {name}");
    }

    /// <summary>The row's context menu: selects it, then deletes it as the Delete button does.</summary>
    internal Task DeleteRow(MaterialRowViewModel row)
    {
        SelectedRow = row;
        return Delete();
    }

    /// <summary>Renames an own row. '@', a name already in these lists, and a rename <see cref="RenameRefusal"/> refuses are
    /// refused; a rename to or from <c>Air</c> warns that the role changes with the name (§1g).</summary>
    public bool Rename(MaterialRowViewModel row, string newName)
    {
        newName = newName.Trim();
        if (row.IsLibrary || row.Source is not { } source || string.Equals(newName, row.Name, StringComparison.Ordinal)) return false;
        if (RenameRefusal?.Invoke(row) is { } refused) { Refusal = refused; return false; }
        if (MaterialValidation.NameRefusal(newName) is { } why) { Refusal = why; return false; }
        if (Sources.SelectMany(src => src.List())
                   .Any(m => !ReferenceEquals(m, row.Material) && string.Equals(m.Name, newName, StringComparison.OrdinalIgnoreCase)))
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
        else if (!Edit(source, () => row.Material.Name = newName, $"Rename material {old} → {newName}")) return false;
        if (airChange)
            note = (note is null ? "" : note + " ") +
                   "A material named Air is air whatever it states, so this rename changed its role.";
        Refusal = note;
        Select(newName, source);
        return true;
    }

    /// <summary>Selects the row named <paramref name="name"/> in <paramref name="source"/>.</summary>
    private void Select(string name, MaterialListSource source)
        => SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Source, source) && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                      ?? Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>Material1</c>, <c>Material2</c>… for Add; <c>Gold copy</c>, <c>Gold copy 2</c>… for Duplicate —
    /// the first not already a name here or in a library shown.</summary>
    private string FreshName(string stem)
    {
        var names = new HashSet<string>(Sources.SelectMany(src => src.List()).Select(m => m.Name).Concat(LibraryRows.Select(l => l.Material.Name)),
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

    internal MaterialRowViewModel(MaterialsTableViewModel table, TechMaterial material, MaterialListSource? source, string? librarySource,
                                  bool builtIn = false)
    {
        _table = table;
        Material = material;
        Source = source;
        LibrarySource = librarySource;
        IsBuiltIn = builtIn;
        _nameText = material.Name;
        PickColorCommand = new AsyncRelayCommand<Window?>(PickColorAsync);
        OpenLibraryCommand = new RelayCommand(() => _table.OpenLibrary?.Invoke(this), () => CanOpenLibrary);
        DeleteCommand = new AsyncRelayCommand(() => _table.DeleteRow(this));
        SigmaTable = new TemperatureTableViewModel(this, "σ(T)", "S/m", m => m.SigmaVsTemp, (m, t) => m.SigmaVsTemp = t, m => m.Sigma20, "σ₂₀");
        KTable = new TemperatureTableViewModel(this, "k(T)", "W/(m·K)", m => m.ThermalKVsTemp, (m, t) => m.ThermalKVsTemp = t, m => m.ThermalK, "k");
        _isShown = Matches(table.FilterText);
    }

    /// <summary>The record this row shows — re-pointed by <see cref="Rebind"/> when a snapshot replaces the list.</summary>
    public TechMaterial Material { get; private set; }

    /// <summary>Re-points the row at <paramref name="material"/> (the same position in a list a commit, an undo or a
    /// redo replaced) and re-reads every binding. A binding whose value did not change writes nothing to its
    /// control, so the row stays still; the container is the table's and survives.</summary>
    internal void Rebind(TechMaterial material)
    {
        Material = material;
        _nameText = material.Name;
        SigmaTable.Reload();
        KTable.Reload();
        _appearance?.Reload();
        OnPropertyChanged(string.Empty);
        RefreshShown();
    }

    // ── brief-em3d-108 R-em3d108-1a/b — Appearance ───────────────────────────────

    private AppearanceEditorViewModel? _appearance;

    /// <summary>The Appearance section: the nine fields, Like and a reset per field, through the one appearance editor.</summary>
    public AppearanceEditorViewModel Appearance => _appearance ??= new AppearanceEditorViewModel(new RowAppearanceHost(this));

    /// <summary>A slider's value not yet written: the appearance the swatch and the open 3D views show meanwhile.</summary>
    private (TechAppearance? Appearance, bool Active) _appearancePreview;

    /// <summary>The look the swatch shows: the record's, or a drag's.</summary>
    internal ResolvedAppearance ResolvedLook => _table.ResolveAppearance(Material, _appearancePreview.Appearance, _appearancePreview.Active);

    private (AppearanceValues Values, byte[] Pixels)? _swatch;

    /// <summary>R-em3d108-1b — the swatch: 96 × 96 opaque RGBA8 rows, a sphere shaded by the realistic view's own reference under the
    /// default studio. Redrawn only when the look it shows changed.</summary>
    public byte[] SwatchPixels
    {
        get
        {
            var values = ResolvedLook.Values;
            if (_swatch is { } s && s.Values == values) return s.Pixels;
            var pixels = AppearanceSwatch.Rgba(values, MaterialsTableViewModel.SwatchEnvironment);
            _swatch = (values, pixels);
            return pixels;
        }
    }

    private sealed class RowAppearanceHost(MaterialRowViewModel row) : IAppearanceHost
    {
        public IReadOnlyList<TechAppearance?> Stated => [row.Material.Appearance];
        public IReadOnlyList<ResolvedAppearance?> Resolved => [row.ResolvedLook];
        public IReadOnlyList<string> LikeChoices
            => [.. row._table.AllMaterialNames.Where(n => !string.Equals(n, row.Material.Name, StringComparison.OrdinalIgnoreCase))];
        public string? ReadOnlyReason => row.IsEditable ? null : row.ReadOnlyNote ?? "This material is read-only here.";

        public void Preview(string key, object? value)
        {
            row._appearancePreview = (TechAppearance.With(row.Material.Appearance, key, value), true);
            row.OnPropertyChanged(nameof(SwatchPixels));
            row.OnPropertyChanged(nameof(ListSwatchColor));
            row._table.RaiseAppearanceEdited(row, row._appearancePreview.Appearance);
        }

        public void EndPreview()
        {
            if (!row._appearancePreview.Active) return;
            row._appearancePreview = default;
            row.OnPropertyChanged(nameof(SwatchPixels));
            row.OnPropertyChanged(nameof(ListSwatchColor));
            row._table.RaiseAppearanceEdited(row, row.Material.Appearance);
        }

        public string? Commit(string key, object? value)
        {
            row._appearancePreview = default;
            if (row.Refuse()) return row._table.Refusal;
            var next = key.Length == 0 ? null : TechAppearance.With(row.Material.Appearance, key, value);
            if (MaterialValidation.AppearanceFaults(next).FirstOrDefault() is { } fault)
            {
                row.OnPropertyChanged(nameof(SwatchPixels));
                row.OnPropertyChanged(nameof(ListSwatchColor));
                return $"The appearance's {fault}.";
            }
            string name = row.Material.Name;
            string what = key.Length == 0 ? $"Clear the appearance of {name}"
                        : value is null ? $"Reset {key} of {name}" : $"Set {key} of {name}";
            var material = row.Material;
            row.Edit(() => material.Appearance = next, what);
            row.OnPropertyChanged(nameof(SwatchPixels));
            row.OnPropertyChanged(nameof(ListSwatchColor));
            row._table.RaiseAppearanceEdited(row, row.Material.Appearance);
            return null;
        }
    }

    /// <summary>The list this row belongs to, or null for a library row shown read-only.</summary>
    public MaterialListSource? Source { get; }

    /// <summary>The library this row came from, or null for a row of a list being edited.</summary>
    public string? LibrarySource { get; }

    public bool IsLibrary => LibrarySource is not null;

    /// <summary>A material built into circuitRF, listed by the Built-in toggle: shown as it ships, and copied into the dialog's
    /// target list the first time it is edited or assigned.</summary>
    public bool IsBuiltIn { get; }

    /// <summary>The built-in mark beside the name: a built-in row, or a row of a library shipped inside circuitRF.</summary>
    public bool ShowsBuiltInMark => IsBuiltIn || Source?.IsBuiltIn == true
                                 || LibrarySource?.StartsWith(MaterialLibraries.ShippedPrefix, StringComparison.Ordinal) == true;

    public bool IsEditable => IsBuiltIn
        ? !_table.IsReadOnly && _table.BuiltInTarget is not null
        : !IsLibrary && !_table.IsReadOnly && Source?.ReadOnlyReason is null;
    public string Name => Material.Name;

    /// <summary>Whether the name may be typed over: an editable row the host does not refuse renaming. A built-in keeps its name.</summary>
    public bool IsNameEditable => !IsBuiltIn && IsEditable && _table.RenameRefusal?.Invoke(this) is null;

    /// <summary>Why the name is fixed here, for its tooltip.</summary>
    public string NameTip => IsBuiltIn
        ? "A built-in material keeps its name: Duplicate it to make a copy under another."
        : (IsEditable ? _table.RenameRefusal?.Invoke(this) : null)
          ?? "The name a stackup entry, body or 3D object names it by. '@' is reserved.";

    /// <summary>Where the row comes from: this file, the list it belongs to, the library it came from, or circuitRF itself.</summary>
    public string SourceLabel => IsBuiltIn ? "built-in"
        : LibrarySource is { } s ? Path.GetFileName(MaterialLibraries.Display(s)) : Source?.Label ?? _table.OwnSource;
    public string? SourceTip => IsBuiltIn ? $"Built into circuitRF ({MaterialLibraries.GenericFileName})"
        : LibrarySource is { } s ? MaterialLibraries.Display(s) : Source?.LibraryPath;

    /// <summary>Why the form is read-only, or null: a library row, a read-only list, a read-only file. A built-in row says where
    /// an edit of it goes.</summary>
    public string? ReadOnlyNote => IsBuiltIn
        ? _table.BuiltInTarget is { } target
            ? $"'{Material.Name}' is built into circuitRF. Editing or assigning it saves a copy to {target.Label}, where it is this design's own."
            : $"'{Material.Name}' is built into circuitRF, and none of these lists can be written to take a copy of it."
        : IsLibrary
        ? $"'{Material.Name}' comes from {SourceLabel}, so it is edited in that library, where the change is on its own undo and Save."
        : Source?.ReadOnlyReason ?? (_table.IsReadOnly ? _table.ReadOnlyReason ?? "This file is read-only." : null);

    public bool HasReadOnlyNote => ReadOnlyNote is not null;

    /// <summary>A library row can be opened in its own document when the host offers it.</summary>
    public bool CanOpenLibrary => IsLibrary && _table.OpenLibrary is not null;
    public string OpenLibraryText => $"Edit in {SourceLabel}";
    public IRelayCommand OpenLibraryCommand { get; }

    /// <summary>Whether the row has a context menu at all: only where the host offers deleting.</summary>
    public bool OffersDelete => _table.CanDelete;

    /// <summary>Whether the context menu's Delete is enabled: a row of a list being edited, not a library's or a built-in.</summary>
    public bool CanBeDeleted => _table.CanDelete && !IsBuiltIn && IsEditable;
    public IAsyncRelayCommand DeleteCommand { get; }

    /// <summary>The list's filter: a row is shown when its name, role or source contains the text.</summary>
    [ObservableProperty] private bool _isShown;

    internal void RefreshShown() => IsShown = Matches(_table.FilterText);

    private bool Matches(string? filter)
        => string.IsNullOrWhiteSpace(filter)
        || Material.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
        || Role.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
        || SourceLabel.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The reason as a sentence under the name: "Conductor — it states σ₂₀ and no εr."</summary>
    public string RoleSentence => RoleReason.Length == 0 ? "" : char.ToUpperInvariant(RoleReason[0]) + RoleReason[1..] + ".";

    /// <summary>The list's second line: the role, and where it comes from.</summary>
    public string Subtitle => $"{Role} · {SourceLabel}";

    /// <summary>What a thermal run will read, in one line — or what is missing for it.</summary>
    public string ThermalSummary
    {
        get
        {
            if (IsAirByName) return "Air is not meshed by a thermal run, so it needs no thermal values.";
            if (ThermalProperties.ThermalKAt(Material, 25) is not { } k)
                return "A thermal run refuses a solid made of this material until k or a k(T) table is stated.";
            return string.Create(CultureInfo.InvariantCulture, $"A thermal run reads k = {k.Value:G4} W/(m·K) at 25 °C") +
                   (Material.ThermalKVsTemp is { Count: > 0 } ? ", from the k(T) table." : ".") +
                   (Material.DensityKgM3 is null || Material.SpecificHeat is null ? " A thermal impedance (Z_th) also needs density and specific heat." : "");
        }
    }

    /// <summary>Where it is used; the tooltip lists them.</summary>
    public IReadOnlyList<string> Uses => _table.UsedBy?.Invoke(Material.Name) ?? [];

    /// <summary>The detail's second line — where it comes from AND where it is used, as ONE line for every material, so the
    /// form below never moves as the selection changes (a separate "Used by" line, shown only for a used material, moved it).
    /// One use is named; several name the first and count the rest ("2 uses" read as nonsense); none says so.</summary>
    public string SourceAndUsesText => $"From {SourceLabel} · " + Uses.Count switch
    {
        0 => "not used.",
        1 => $"used by {Uses[0]}.",
        var n => $"used by {Uses[0]} and {n - 1} more.",
    };

    /// <summary>The line's tooltip: the source's full path, then every use.</summary>
    public string? SourceAndUsesTip
    {
        get
        {
            var parts = new List<string>();
            if (SourceTip is { Length: > 0 } s) parts.Add(s);
            if (Uses.Count > 0) parts.Add("Used by:\n" + string.Join("\n", Uses.Select(u => "  " + u)));
            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }
    }

    /// <summary>The σ(T) and k(T) tables, each edited point by point.</summary>
    public TemperatureTableViewModel SigmaTable { get; }
    public TemperatureTableViewModel KTable { get; }

    /// <summary>A σ(T) or k(T) table is stated.</summary>
    public bool HasTemperatureTables => Material.SigmaVsTemp is { Count: > 0 } || Material.ThermalKVsTemp is { Count: > 0 };

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

    /// <summary>A library row is edited in its own library's document, never through a technology (M4); a read-only
    /// list is not edited at all.</summary>
    internal bool Refuse()
    {
        if (IsBuiltIn)
        {
            if (IsEditable) return false;
            _table.Refusal = ReadOnlyNote;
        }
        else if (IsLibrary)
            _table.Refusal = $"'{Material.Name}' comes from {SourceLabel}: open that library to edit it, so the change is on its own undo and Save.";
        else if (Source?.ReadOnlyReason is { } why) _table.Refusal = why;
        else return false;
        OnPropertyChanged(string.Empty);
        return true;
    }

    /// <summary>Commits a mutation of this row's record through its list.</summary>
    internal bool Edit(Action mutate, string description)
        => IsBuiltIn ? _table.AdoptBuiltIn(this, mutate, description + " (a copy of the built-in material)")
                     : Source is { } source && _table.Edit(source, mutate, description);

    /// <summary>A refused gesture of this row, shown where the table shows its refusals.</summary>
    internal void Refused(string why)
    {
        _table.Refusal = why;
        OnPropertyChanged(string.Empty);
    }

    private void Set(string? text, double? current, Action<double?> write, string field)
    {
        // The no-change test comes FIRST: leaving a field pushes its text back even when nothing was typed, and a
        // library row's read-only box would otherwise raise the refusal (and re-read the row) on every focus move.
        bool parsed = MaterialsTableViewModel.TryParse(text, out double? v);
        if (parsed && v == current && v.HasValue == current.HasValue) return;
        if (Refuse()) return;
        if (!parsed)
        {
            _table.Refusal = $"'{text}' is not a number. Leave the field empty for \"not stated\".";
            OnPropertyChanged(string.Empty);
            return;
        }
        string name = Material.Name;
        Edit(() => write(v), v is null ? $"Clear {field} of {name}" : $"Set {field} of {name}");
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
            if (!Edit(() => Material.EpsrTensor = value ? [e, e, e] : null,
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
        if (Material.EpsrTensor is not { Length: 3 } t) return;
        bool parsed = MaterialsTableViewModel.TryParse(text, out double? v) && v is not null;
        if (parsed && t[i] == v!.Value) return;
        if (Refuse()) return;
        if (!parsed)
        {
            _table.Refusal = "Each tensor component needs a number; untick Anisotropic to remove the tensor.";
            OnPropertyChanged(string.Empty);
            return;
        }
        string name = Material.Name;
        Edit(() => { var copy = (double[])t.Clone(); copy[i] = v!.Value; Material.EpsrTensor = copy; },
                    $"Set εr {"xyz"[i]}{"xyz"[i]} of {name}");
    }

    // ── the thermal k tensor: the εr tensor's twin, seeded from ThermalK ───────────

    public bool IsThermalAnisotropic
    {
        get => Material.ThermalKTensor is not null;
        set
        {
            if (value == IsThermalAnisotropic || Refuse()) return;
            string name = Material.Name;
            double k0 = ThermalProperties.ThermalKAt(Material, 25)?.Value ?? 0;
            if (value && !(k0 > 0))
            {
                _table.Refusal = $"State k for {name} first: the tensor refines it, and a bond wire or effective block still reads it.";
                OnPropertyChanged(string.Empty);
                return;
            }
            if (!Edit(() => Material.ThermalKTensor = value ? [k0, k0, k0] : null,
                             value ? $"Make k of {name} anisotropic" : $"Make k of {name} isotropic"))
                OnPropertyChanged();
        }
    }

    public string KTensorXxText { get => KTensor(0); set => SetKTensor(0, value); }
    public string KTensorYyText { get => KTensor(1); set => SetKTensor(1, value); }
    public string KTensorZzText { get => KTensor(2); set => SetKTensor(2, value); }

    private string KTensor(int i) => Material.ThermalKTensor is { Length: 3 } t ? MaterialsTableViewModel.Show(t[i]) : "";

    private void SetKTensor(int i, string? text)
    {
        if (Material.ThermalKTensor is not { Length: 3 } t) return;
        bool parsed = MaterialsTableViewModel.TryParse(text, out double? v) && v is > 0;
        if (parsed && t[i] == v!.Value) return;
        if (Refuse()) return;
        if (!parsed)
        {
            _table.Refusal = "Each k tensor component needs a positive number; untick Anisotropic k to remove the tensor.";
            OnPropertyChanged(string.Empty);
            return;
        }
        string name = Material.Name;
        Edit(() => { var copy = (double[])t.Clone(); copy[i] = v!.Value; Material.ThermalKTensor = copy; },
                    $"Set k {"xyz"[i]}{"xyz"[i]} of {name}");
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
            Edit(() => Material.Color = v, v is null ? $"Clear the colour of {name}" : $"Colour {name}");
        }
    }

    /// <summary>The list's swatch: the ordinary-view colour when one is stated, else the realistic view's resolved base colour —
    /// never a colour the material does not have. Until 2026-10-04 it fell back to one stand-in colour per role, so every metal
    /// with no stated colour read as gold, silver included (owner-reported).</summary>
    public Avalonia.Media.Color ListSwatchColor
    {
        get
        {
            if (Material.Color is { } c && Rgba.TryParseHex(c, out var rgba)) return new Avalonia.Media.Color(255, rgba.R, rgba.G, rgba.B);
            var (r, g, b) = ResolvedLook.Values.BaseColor.ToSrgb();
            return Avalonia.Media.Color.FromRgb(r, g, b);
        }
    }

    /// <summary>The Colour column's swatch — transparent while no colour is stated (the 3D view's own palette).</summary>
    public Avalonia.Media.Color SwatchColor
        => Material.Color is { } c && Rgba.TryParseHex(c, out var rgba)
            ? new Avalonia.Media.Color(255, rgba.R, rgba.G, rgba.B)
            : Avalonia.Media.Colors.Transparent;

    /// <summary>Opens the application's one colour picker (<see cref="Views.Dialogs.ColorPickerDialog"/>, the one
    /// the Layers tab opens) seeded with the stated colour, or mid-grey when none is stated.</summary>
    public IAsyncRelayCommand<Window?> PickColorCommand { get; }

    private async Task PickColorAsync(Window? owner)
    {
        if (owner is null || Refuse()) return;
        var seed = Material.Color is { } c && Rgba.TryParseHex(c, out var rgba) ? rgba : new Rgba(0x80, 0x80, 0x80);
        ApplyPickedColour(await new Views.Dialogs.ColorPickerDialog(seed).ShowDialog<Rgba?>(owner));
    }

    /// <summary>The picker's answer, committed through the same path as a typed colour, as <c>#rrggbb</c> (a
    /// material's colour carries no alpha). Null (Cancel) and the colour already stated change nothing.</summary>
    internal void ApplyPickedColour(Rgba? picked)
    {
        if (picked is not { } p) return;
        ColorText = p.ToHex();
    }

    public string SourceText
    {
        get => Material.Source ?? "";
        set
        {
            string? v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (v == Material.Source || Refuse()) return;
            string name = Material.Name;
            Edit(() => Material.Source = v, $"Edit the source of {name}");
        }
    }
}
