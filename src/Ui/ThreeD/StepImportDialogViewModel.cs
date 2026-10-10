// brief-em3d-68 — the Import STEP dialog: the file's parts as a table (name, colour, solid or not, the match and why, a
// material), the units line, the counts, and OK. Every rule is StepImport's; this is the table's state.
//
// brief-em3d-128 — a product of several solids is a HEADER row with its solids indented beneath it: its check box and its
// material combo act on every solid under it. Above the table, the group the import gathers in (empty: none).
//
// THE READ IS THE DIALOG'S FIRST STATE (R-em3d68-4c). It opens reading, with a progress line and Cancel; Cancel kills
// the worker and closes with nothing changed. Nothing is written until OK, which hands the plan to the editor.

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Engine;
using CircuitRF.Render.Scene3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One row: a product of one solid, or one solid of a product of several.</summary>
public sealed partial class StepImportRow : ObservableObject
{
    private readonly StepImportDialogViewModel _owner;
    private bool _loading;

    internal StepImportRow(StepImportDialogViewModel owner, StepImportPart part)
    {
        _owner = owner;
        Part = part;
        _loading = true;
        Name = part.Name;
        Import = part.Import;
        Material = part.Material ?? StepImportDialogViewModel.NoMaterial;
        _loading = false;
    }

    public StepImportPart Part { get; }

    public string Path => Part.Solid is { } k ? $"{Part.Path} #{k}" : Part.Path;
    public string ProductName => Part.Solid is not null ? Part.SolidName
                               : Part.ProductName.Length > 0 ? Part.ProductName : "(unnamed)";

    /// <summary>A solid of a product of several sits indented under its product's header row.</summary>
    public bool IsSolid => Part.Solid is not null;
    public Avalonia.Thickness Indent => IsSolid ? new Avalonia.Thickness(14, 0, 6, 0) : new Avalonia.Thickness(0, 0, 6, 0);

    /// <summary>The part's colour, or a transparent swatch with <see cref="ColourText"/> <i>none</i> (or <i>mixed</i>).</summary>
    public IBrush Swatch => Part.Colour is { } hex && Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
    public string ColourText => Part.Colour ?? (Part.Mixed ? "mixed" : "none");
    public string ColourTip => Part.Mixed
        ? "Mixed: its faces carry several colours, so it has none to match by. Map all of this colour maps this row alone."
        : "Map all of this colour: every part of this colour takes this row's material.";

    /// <summary>Only a closed solid can be imported (R-em3d68-4b); the others are listed, unchecked and disabled.</summary>
    public bool CanImport => Part.Closed;
    public string SolidText => Part.Closed ? "solid" : "not a solid";
    public string SolidTip => Part.Closed ? $"{Part.Faces} faces" : $"Not imported: {Part.Why}.";

    public string MatchText => Part.MatchText;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _import;
    [ObservableProperty] private string _material = StepImportDialogViewModel.NoMaterial;

    partial void OnNameChanged(string value)
    {
        Part.Name = value.Trim();
        if (!_loading) _owner.RowsChanged();
    }

    partial void OnImportChanged(bool value)
    {
        Part.Import = value && Part.Closed;
        if (!_loading) _owner.RowsChanged();
    }

    partial void OnMaterialChanged(string value)
    {
        if (_loading) return;
        Part.Material = value == StepImportDialogViewModel.NoMaterial ? null : value;
        Part.Match = Part.Material is null ? StepMatch.Unmatched : StepMatch.Chosen;
        OnPropertyChanged(nameof(MatchText));
        _owner.RowsChanged();
    }

    /// <summary>R-em3d68-3c — every part sharing this row's colour takes its material: a connector's brass parts in one gesture.</summary>
    [RelayCommand]
    private void MapAllOfThisColour() => _owner.MapColour(this);

    internal void Reload()
    {
        _loading = true;
        Material = Part.Material ?? StepImportDialogViewModel.NoMaterial;
        Import = Part.Import;
        _loading = false;
        OnPropertyChanged(nameof(MatchText));
    }
}

/// <summary>brief-em3d-128 R-em3d128-1b — a product of several solids: its name and <i>n solids</i>, a check box for every
/// solid under it, and a material combo that sets every solid under it as chosen.</summary>
public sealed partial class StepImportProductRow : ObservableObject
{
    private readonly StepImportDialogViewModel _owner;
    private bool _loading;

    internal StepImportProductRow(StepImportDialogViewModel owner, StepImportPart first)
    {
        _owner = owner;
        Path = first.Path;
        ProductName = first.ProductName.Length > 0 ? first.ProductName : "(unnamed)";
        SolidsText = $"{first.Solids} solids";
        Reload();
    }

    public string Path { get; }
    public string ProductName { get; }
    public string SolidsText { get; }

    /// <summary>The solids under it, as their rows.</summary>
    internal List<StepImportRow> Members { get; } = [];

    public bool CanImport => Members.Any(m => m.CanImport);

    /// <summary>Every closed solid checked (true), none (false), or some (null).</summary>
    [ObservableProperty] private bool? _import;

    /// <summary>The solids' material when they agree; blank when they do not.</summary>
    [ObservableProperty] private string? _material;

    partial void OnImportChanged(bool? value)
    {
        if (_loading || value is null) return;
        _owner.SetProductImport(this, value.Value);
    }

    partial void OnMaterialChanged(string? value)
    {
        if (_loading || value is null) return;
        _owner.SetProductMaterial(this, value == StepImportDialogViewModel.NoMaterial ? null : value);
    }

    internal void Reload()
    {
        _loading = true;
        var closed = Members.Where(m => m.CanImport).ToList();
        Import = closed.Count == 0 || closed.All(m => !m.Part.Import) ? false : closed.All(m => m.Part.Import) ? true : null;
        var mats = Members.Select(m => m.Material).Distinct().ToList();
        Material = mats.Count == 1 ? mats[0] : null;
        _loading = false;
    }
}

public sealed partial class StepImportDialogViewModel : ObservableObject
{
    /// <summary>The combo's first entry: no material (R-em3d68-3b — drawn, ignored by the solver, named by <c>check</c>).</summary>
    public const string NoMaterial = "(no material)";

    private readonly Func<RunControl, StepImportPlan> _read;
    private readonly Func<StepImportPlan, string?> _names;
    private readonly Func<StepImportPlan, string?>? _group;
    private readonly IReadOnlySet<string>? _onlyParts;
    private CancellationTokenSource? _cts;

    /// <param name="read">Reads the file (off the UI thread): the editor's <see cref="C3dEditorViewModel.ReadStep"/>.</param>
    /// <param name="names">Why the checked rows' names cannot be used, or null: <see cref="StepImport.NamesRefusal"/> on the document.</param>
    /// <param name="onlyParts">R-em3d68-5d — a reload's new parts: only these rows, all unchecked.</param>
    /// <param name="group">Why the group's name cannot be used, or null: <see cref="StepImport.GroupRefusal"/> on the document.</param>
    public StepImportDialogViewModel(string sourcePath, Func<RunControl, StepImportPlan> read, Func<StepImportPlan, string?> names,
                                     IReadOnlySet<string>? onlyParts = null, Func<StepImportPlan, string?>? group = null)
    {
        SourcePath = sourcePath;
        _read = read;
        _names = names;
        _onlyParts = onlyParts;
        _group = group;
    }

    public string SourcePath { get; }
    public string Title => $"Import STEP — {System.IO.Path.GetFileName(SourcePath)}";

    /// <summary>One row per importable object: a product of one solid, or a solid of a product of several.</summary>
    public ObservableCollection<StepImportRow> Rows { get; } = [];

    /// <summary>brief-em3d-128 — the header rows, one per product of several solids.</summary>
    public ObservableCollection<StepImportProductRow> Products { get; } = [];

    /// <summary>What the table draws, in order: each header row followed by its solids, and the other rows between.</summary>
    public ObservableCollection<object> Table { get; } = [];

    /// <summary>The combo's entries: <see cref="NoMaterial"/>, then the technology's materials.</summary>
    public ObservableCollection<string> Materials { get; } = [NoMaterial];

    public StepImportPlan? Plan { get; private set; }

    [ObservableProperty] private bool _reading = true;
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _unitsLine = "";
    [ObservableProperty] private string _countsLine = "";
    [ObservableProperty] private string? _coarseNote;
    [ObservableProperty] private string? _noMaterials;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string? _namesError;

    /// <summary>R-em3d128-2c — the group the import gathers in; empty is no group.</summary>
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private string? _groupError;

    /// <summary>The group field shows when the file has more than one row: one object is never grouped.</summary>
    public bool ShowGroup => Plan is { Parts.Count: > 1 } && _onlyParts is null;

    public bool HasNotes => Notes.Length > 0;

    /// <summary>OK: read, nothing refused, at least one row checked, every checked name usable.</summary>
    public bool CanAccept => !Reading && Error is null && Plan is not null && NamesError is null && GroupError is null && Plan.Parts.Any(p => p.Import);

    partial void OnReadingChanged(bool value) => Notify();
    partial void OnErrorChanged(string? value) => Notify();
    partial void OnNamesErrorChanged(string? value) => Notify();
    partial void OnGroupErrorChanged(string? value) => Notify();

    partial void OnGroupNameChanged(string value)
    {
        if (Plan is not { } plan) return;
        plan.Group = value.Trim();
        RowsChanged();
    }
    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));

    private void Notify()
    {
        OnPropertyChanged(nameof(CanAccept));
        AcceptCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised with true on OK, false on Cancel: the window closes.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Reads the file; the table fills when it answers. A refusal (not STEP, a unit it cannot resolve) is shown and
    /// OK stays disabled.</summary>
    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var progress = new Progress<RunProgress>(p => Progress = p.Stage is { Length: > 0 } s ? $"{s}…" : "Reading…");
        var control = new RunControl { Token = _cts.Token, Progress = progress };
        Progress = $"Reading {System.IO.Path.GetFileName(SourcePath)}…";
        Reading = true;
        try
        {
            var plan = await Task.Run(() => _read(control));
            Fill(plan);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is GeometryKernelException or StepImportException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
        finally
        {
            Reading = false;
            Progress = "";
        }
    }

    private void Fill(StepImportPlan plan)
    {
        if (_onlyParts is { } only)
        {
            foreach (var p in plan.Parts) p.Import = false;
            plan.Group = null;              // a reload's new parts join the document loose; the file's group is already there
        }
        Plan = plan;
        foreach (string m in plan.Materials) Materials.Add(m);
        foreach (var p in plan.Parts.Where(p => _onlyParts is null || _onlyParts.Contains(p.Path)))
        {
            var row = new StepImportRow(this, p);
            if (p.Solid is not null)
            {
                if (Products.LastOrDefault() is not { } header || header.Path != p.Path)
                {
                    header = new StepImportProductRow(this, p);
                    Products.Add(header);
                    Table.Add(header);
                }
                header.Members.Add(row);
            }
            Rows.Add(row);
            Table.Add(row);
        }
        foreach (var h in Products) h.Reload();
        GroupName = plan.Group ?? "";
        OnPropertyChanged(nameof(ShowGroup));
        UnitsLine = plan.UnitsLine;
        NoMaterials = plan.NoMaterials;
        var notes = new List<string>(plan.Healing);
        if (plan.Pmi > 0) notes.Add($"The file carries {plan.Pmi} dimensions, tolerances or datums; none is imported.");
        Notes = string.Join("\n", notes);
        RowsChanged();
    }

    /// <summary>A row changed: the counts, the drawing-budget note and the names check follow.</summary>
    internal void RowsChanged()
    {
        if (Plan is not { } plan) return;
        var chosen = plan.Parts.Where(p => p.Import).ToList();
        long triangles = chosen.Sum(p => p.Triangles);
        int solids = plan.Parts.Count(p => p.Closed);
        CountsLine = string.Create(CultureInfo.InvariantCulture,
            $"{plan.Parts.Count} row{(plan.Parts.Count == 1 ? "" : "s")} ({solids} solid), {chosen.Count} checked: " +
            $"{chosen.Sum(p => p.Faces):N0} faces, {triangles:N0} triangles to draw.");
        // R-em3d68-4c — above the viewport's budget the scene draws what does not fit as its bounding box; never a refusal.
        CoarseNote = triangles > Scene3DFramePlan.DefaultTriangleBudget
            ? string.Create(CultureInfo.InvariantCulture,
                $"That is more than the viewport draws at once ({Scene3DFramePlan.DefaultTriangleBudget:N0} triangles): the parts that do not fit are drawn as their bounding boxes. The solvers tessellate on their own.")
            : null;
        NamesError = chosen.Count == 0 ? null : _names(plan);
        GroupError = chosen.Count > 1 ? _group?.Invoke(plan) : null;
        foreach (var h in Products) h.Reload();
        Notify();
    }

    internal void MapColour(StepImportRow row)
    {
        if (Plan is not { } plan) return;
        StepImport.MapColour(plan, row.Part, row.Part.Material);
        ReloadRows();
    }

    /// <summary>R-em3d128-1b — a header row's combo: every solid of its product, chosen.</summary>
    internal void SetProductMaterial(StepImportProductRow header, string? material)
    {
        if (Plan is not { } plan) return;
        StepImport.SetProductMaterial(plan, header.Path, material);
        ReloadRows();
    }

    /// <summary>R-em3d128-1b — a header row's check box: every closed solid of its product.</summary>
    internal void SetProductImport(StepImportProductRow header, bool import)
    {
        if (Plan is not { } plan) return;
        StepImport.SetProductImport(plan, header.Path, import);
        ReloadRows();
    }

    private void ReloadRows()
    {
        foreach (var r in Rows) r.Reload();
        RowsChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept()
    {
        if (!CanAccept) return;
        CloseRequested?.Invoke(true);
    }

    /// <summary>Cancel: a read in flight is killed; nothing was written either way.</summary>
    [RelayCommand]
    private void Cancel()
    {
        CancelRead();
        CloseRequested?.Invoke(false);
    }

    /// <summary>Kills a read in flight; the window closing any way but OK calls it.</summary>
    public void CancelRead() => _cts?.Cancel();
}
