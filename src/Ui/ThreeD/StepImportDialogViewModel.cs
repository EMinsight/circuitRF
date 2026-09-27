// brief-em3d-68 — the Import STEP dialog: the file's parts as a table (name, colour, solid or not, the match and why, a
// material), the units line, the counts, and OK. Every rule is StepImport's; this is the table's state.
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

/// <summary>One row: a part of the file.</summary>
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

    public string Path => Part.Path;
    public string ProductName => Part.ProductName.Length > 0 ? Part.ProductName : "(unnamed)";

    /// <summary>The part's colour, or a transparent swatch with <see cref="ColourText"/> <i>none</i>.</summary>
    public IBrush Swatch => Part.Colour is { } hex && Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
    public string ColourText => Part.Colour ?? "none";

    /// <summary>Only a closed solid can be imported (R-em3d68-4b); the others are listed, unchecked and disabled.</summary>
    public bool CanImport => Part.Solid;
    public string SolidText => Part.Solid ? "solid" : "not a solid";
    public string SolidTip => Part.Solid ? $"{Part.Faces} faces" : $"Not imported: {Part.Why}.";

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
        Part.Import = value && Part.Solid;
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
        _loading = false;
        OnPropertyChanged(nameof(MatchText));
    }
}

public sealed partial class StepImportDialogViewModel : ObservableObject
{
    /// <summary>The combo's first entry: no material (R-em3d68-3b — drawn, ignored by the solver, named by <c>check</c>).</summary>
    public const string NoMaterial = "(no material)";

    private readonly Func<RunControl, StepImportPlan> _read;
    private readonly Func<StepImportPlan, string?> _names;
    private readonly IReadOnlySet<string>? _onlyParts;
    private CancellationTokenSource? _cts;

    /// <param name="read">Reads the file (off the UI thread): the editor's <see cref="C3dEditorViewModel.ReadStep"/>.</param>
    /// <param name="names">Why the checked rows' names cannot be used, or null: <see cref="StepImport.NamesRefusal"/> on the document.</param>
    /// <param name="onlyParts">R-em3d68-5d — a reload's new parts: only these rows, all unchecked.</param>
    public StepImportDialogViewModel(string sourcePath, Func<RunControl, StepImportPlan> read, Func<StepImportPlan, string?> names,
                                     IReadOnlySet<string>? onlyParts = null)
    {
        SourcePath = sourcePath;
        _read = read;
        _names = names;
        _onlyParts = onlyParts;
    }

    public string SourcePath { get; }
    public string Title => $"Import STEP — {System.IO.Path.GetFileName(SourcePath)}";

    public ObservableCollection<StepImportRow> Rows { get; } = [];

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

    public bool HasNotes => Notes.Length > 0;

    /// <summary>OK: read, nothing refused, at least one row checked, every checked name usable.</summary>
    public bool CanAccept => !Reading && Error is null && Plan is not null && NamesError is null && Plan.Parts.Any(p => p.Import);

    partial void OnReadingChanged(bool value) => Notify();
    partial void OnErrorChanged(string? value) => Notify();
    partial void OnNamesErrorChanged(string? value) => Notify();
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
            foreach (var p in plan.Parts) p.Import = false;
        Plan = plan;
        foreach (string m in plan.Materials) Materials.Add(m);
        foreach (var p in plan.Parts.Where(p => _onlyParts is null || _onlyParts.Contains(p.Path))) Rows.Add(new StepImportRow(this, p));
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
        int solids = plan.Parts.Count(p => p.Solid);
        CountsLine = string.Create(CultureInfo.InvariantCulture,
            $"{plan.Parts.Count} part{(plan.Parts.Count == 1 ? "" : "s")} ({solids} solid), {chosen.Count} checked: " +
            $"{chosen.Sum(p => p.Faces):N0} faces, {triangles:N0} triangles to draw.");
        // R-em3d68-4c — above the viewport's budget the scene draws what does not fit as its bounding box; never a refusal.
        CoarseNote = triangles > Scene3DFramePlan.DefaultTriangleBudget
            ? string.Create(CultureInfo.InvariantCulture,
                $"That is more than the viewport draws at once ({Scene3DFramePlan.DefaultTriangleBudget:N0} triangles): the parts that do not fit are drawn as their bounding boxes. The solvers tessellate on their own.")
            : null;
        NamesError = chosen.Count == 0 ? null : _names(plan);
        Notify();
    }

    internal void MapColour(StepImportRow row)
    {
        if (Plan is not { } plan) return;
        StepImport.MapColour(plan, row.Part, row.Part.Material);
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
