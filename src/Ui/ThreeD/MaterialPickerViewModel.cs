using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One list the 3D view's Materials dialog edits: a technology's own materials, or one library's, as its file
/// holds them now (an open document's unsaved state included), and why it cannot be edited, if it cannot. <paramref name="BuiltIn"/>:
/// a library shipped inside circuitRF, whose rows carry the built-in mark.</summary>
public sealed record MaterialSourceSeed(string Label, string? LibraryPath, IReadOnlyList<TechMaterial> Materials, string? ReadOnlyReason,
                                        bool BuiltIn = false);

/// <summary>
/// brief-em3d-53 R-em3d53-5 — the 3D editor's Materials dialog, behind Assign Material… and New Material….
///
/// <para>Materials editor redesign (2026-09-29): it hosts <b>the Materials editor itself</b> — the same list and form a
/// <c>.cmat</c> document shows — over every material the technology resolves: its own list and each library's. So a
/// material's properties can be read and changed where it is assigned, a material can be duplicated under a new name,
/// and a new one is made with every property in view. Until then Assign listed names only, and New showed one blank row
/// of a table too wide for the dialog, its thermal values collapsed below it.</para>
///
/// <para>It edits COPIES. Nothing reaches a file until OK, and then each list that changed is handed to that file's own
/// document as ONE undo entry (<see cref="ChangedLists"/>), the file becoming an unsaved change, exactly as a new material
/// always was; Cancel discards every edit. Two gestures are its file's editor's, not this dialog's: deleting a material
/// (where its uses are listed) and renaming one that already existed (where every file naming it is renamed with it).</para>
/// </summary>
public sealed partial class MaterialPickerViewModel : ObservableObject
{
    private readonly List<(MaterialSourceSeed Seed, List<TechMaterial> Working, string Before)> _lists = [];
    private readonly HashSet<TechMaterial> _existing = new(ReferenceEqualityComparer.Instance);

    /// <param name="seeds">The technology's own list first, then each library it names.</param>
    /// <param name="technologyLabel">The technology's file name, for the header.</param>
    /// <param name="startNew">New Material…: <see cref="Begin"/> makes a new material and selects it.</param>
    /// <param name="current">The material to select: the one object's, or the editor's current one.</param>
    /// <param name="objectCount">How many objects OK assigns the selected material to; 0 makes it the current material.</param>
    public MaterialPickerViewModel(IReadOnlyList<MaterialSourceSeed> seeds, string technologyLabel, bool startNew, string? current,
                                   int objectCount, string? refusal)
    {
        var sources = new List<MaterialListSource>();
        foreach (var seed in seeds)
        {
            var working = MaterialLibraryPersistence.Deserialize(MaterialLibraryPersistence.Serialize(seed.Materials));
            foreach (var m in working) _existing.Add(m);
            _lists.Add((seed, working, MaterialLibraryPersistence.Serialize(working)));
            sources.Add(new MaterialListSource(seed.Label, () => working, (mutate, _) => mutate(), seed.LibraryPath)
            {
                ReadOnlyReason = seed.ReadOnlyReason,
                IsBuiltIn = seed.BuiltIn,
            });
        }
        Table = new MaterialsTableViewModel(sources)
        {
            CanDelete = false,
            OffersBuiltIns = true,
            RenameRefusal = row => _existing.Contains(row.Material)
                ? $"'{row.Name}' already exists and files name it: rename it in {row.SourceLabel}'s own editor, which renames it everywhere it is used. A new or duplicated material is named here."
                : null,
        };
        // brief-em3d-108 R-em3d108-1c — every appearance edit, a drag's included, is offered to the open 3D views as a preview.
        Table.AppearanceEdited += (row, appearance) => AppearancePreview?.Invoke(row.Source?.LibraryPath, row.Name, appearance);
        // M9: a new material goes to the technology's first library when it names one, else to its own list.
        if (sources.Skip(1).FirstOrDefault(s => s.ReadOnlyReason is null) is { } library) Table.TargetSource = library;
        if (current is not null) Table.Select(current);

        TechnologyLabel = technologyLabel;
        StartNew = startNew;
        ObjectCount = objectCount;
        Refusal = refusal;
    }

    /// <summary>
    /// brief-em3d-108 R-em3d108-1c — an appearance edited in the dialog (a slider's preview, or a value written to the copy): the
    /// library the material's list is (null: the technology's own), its name, and the appearance it shows now. The workspace pushes it
    /// to every open 3D view on this technology as a preview; Cancel takes every preview away, OK writes the edit through the files'
    /// own documents (and the views' display-only reload shows it).
    /// </summary>
    public event Action<string?, string, CircuitRF.Design.Layout.TechAppearance?>? AppearancePreview;

    /// <summary>The Materials editor, over copies of the technology's lists.</summary>
    public MaterialsTableViewModel Table { get; }

    public string TechnologyLabel { get; }
    public bool StartNew { get; }
    public int ObjectCount { get; }

    public string Header => $"Materials of {TechnologyLabel}";

    public string Explanation => (ObjectCount switch
    {
        0 => "Choose the material new objects are made of, ",
        1 => "Choose the material for the selected object, ",
        var n => $"Choose the material for the {n} selected objects, ",
    }) + "or edit any material — changes go to the file each belongs to when you press OK, as an unsaved change there. Cancel discards them.";

    public string OkText => ObjectCount switch
    {
        0 => "Use Material",
        1 => "Assign",
        var n => $"Assign to {n}",
    };

    /// <summary>Why OK was refused, or why the lists are empty.</summary>
    [ObservableProperty] private string? _refusal;

    /// <summary>Called once the dialog is showing: New Material… makes its new material now, so the caret lands in its name.</summary>
    public void Begin()
    {
        if (StartNew) Table.Add();
    }

    /// <summary>Validates OK: a material chosen, and no list with an error. Null when it may close.</summary>
    public string? Accept()
    {
        if (Table.SelectedRow is not { } row) return "Choose a material, or ＋ New to make one.";
        // A built-in material is no material of this technology until it is copied into one of its lists.
        if (row.IsBuiltIn && !Table.AdoptBuiltIn(row, null, $"Use built-in material {row.Name}"))
            return Table.Refusal ?? $"'{row.Name}' could not be copied into this technology.";
        foreach (var (seed, working, _) in _lists)
            if (MaterialValidation.Validate(working).FirstOrDefault(p => p.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error) is { } p)
                return $"{seed.Label}: {p.Message}";
        return null;
    }

    /// <summary>The material the objects take.</summary>
    public string ChosenName => Table.SelectedRow?.Name ?? "";

    /// <summary>The lists this dialog changed, each whole, to be committed to its file's document.</summary>
    public IReadOnlyList<(MaterialSourceSeed Seed, List<TechMaterial> Materials)> ChangedLists
        => [.. _lists.Where(l => MaterialLibraryPersistence.Serialize(l.Working) != l.Before).Select(l => (l.Seed, l.Working))];
}
