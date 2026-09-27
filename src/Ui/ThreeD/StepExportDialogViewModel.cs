// brief-em3d-69 R-em3d69-4 — the Export STEP dialog: after the save dialog, one small options dialog. Flattened or As assembly,
// Precedence applied or As drawn, Thicken sheets, Include air box and the schema; the summary line states the solid, face and
// sheet counts before anything is written. Every rule is StepExport's; this is the dialog's state.
//
// THE OPTIONS ARE REMEMBERED PER SESSION (R-em3d69-4a): a static, so the next export in this process opens on the last one's
// choices and a restart opens on the defaults. The export runs on RunControl with Cancel (R-em3d69-4c); a cancelled export
// writes nothing, because StepExport writes a temporary file and renames it into place only on success.

using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class StepExportDialogViewModel : ObservableObject
{
    /// <summary>The choices the last export in this session made (R-em3d69-4a).</summary>
    private static StepExportOptions s_last = new();

    /// <summary>The schema combo's entries, in <see cref="StepExportSchema"/> order.</summary>
    public static IReadOnlyList<string> Schemas { get; } = ["AP214 (the widest read)", "AP242"];

    private readonly Func<StepExportOptions, StepExportPlan> _plan;
    private readonly Func<StepExportPlan, RunControl, StepExportResult> _write;
    private readonly StepExportOptions _source;
    private CancellationTokenSource? _cts;
    private int _planGeneration;
    private bool _loading;

    /// <param name="source">What the export reads — the document and its path fields; the dialog sets only the choices.</param>
    /// <param name="plan">Plans the export (off the UI thread): <see cref="StepExport.Plan"/>.</param>
    /// <param name="write">Writes it (off the UI thread): <see cref="StepExport.Write"/>.</param>
    public StepExportDialogViewModel(string targetPath, StepExportOptions source, Func<StepExportOptions, StepExportPlan> plan,
                                     Func<StepExportPlan, RunControl, StepExportResult> write)
    {
        TargetPath = targetPath;
        _source = source;
        _plan = plan;
        _write = write;
        _loading = true;
        Assembly = s_last.Assembly;
        AsDrawn = s_last.AsDrawn;
        ThickenSheets = s_last.ThickenSheets;
        IncludeAirBox = s_last.IncludeAirBox;
        SchemaIndex = (int)s_last.Schema;
        _loading = false;
    }

    public string TargetPath { get; }
    public string Title => $"Export STEP — {Path.GetFileName(TargetPath)}";

    [ObservableProperty] private bool _assembly;
    [ObservableProperty] private bool _asDrawn;
    [ObservableProperty] private bool _thickenSheets;
    [ObservableProperty] private bool _includeAirBox;
    [ObservableProperty] private int _schemaIndex;

    [ObservableProperty] private bool _planning;
    [ObservableProperty] private bool _writing;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _notes = "";

    public bool Flattened { get => !Assembly; set => Assembly = !value; }
    public bool PrecedenceApplied { get => !AsDrawn; set => AsDrawn = !value; }
    public bool HasNotes => Notes.Length > 0;
    public bool Busy => Planning || Writing;

    /// <summary>What was written, once the export succeeded.</summary>
    public StepExportResult? Result { get; private set; }

    private StepExportPlan? _current;

    public bool CanExport => !Busy && Error is null && _current is not null;

    /// <summary>The options as they stand: the source's fields and the dialog's choices.</summary>
    public StepExportOptions Options => _source with
    {
        Assembly = Assembly, AsDrawn = AsDrawn, ThickenSheets = ThickenSheets, IncludeAirBox = IncludeAirBox,
        Schema = (StepExportSchema)Math.Clamp(SchemaIndex, 0, 1),
    };

    partial void OnAssemblyChanged(bool value) { OnPropertyChanged(nameof(Flattened)); Replan(); }
    partial void OnAsDrawnChanged(bool value) { OnPropertyChanged(nameof(PrecedenceApplied)); Replan(); }
    partial void OnThickenSheetsChanged(bool value) => Replan();
    partial void OnIncludeAirBoxChanged(bool value) => Replan();
    partial void OnSchemaIndexChanged(int value) => Replan();
    partial void OnPlanningChanged(bool value) => Notify();
    partial void OnWritingChanged(bool value) => Notify();
    partial void OnErrorChanged(string? value) => Notify();
    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));

    private void Notify()
    {
        OnPropertyChanged(nameof(Busy));
        OnPropertyChanged(nameof(CanExport));
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised with true once the file is written, false on Cancel: the window closes.</summary>
    public event Action<bool>? CloseRequested;

    private void Replan()
    {
        if (!_loading) _ = PlanAsync();
    }

    /// <summary>Plans for the choices as they stand: the summary line, or the refusal. A newer plan supersedes an older one.</summary>
    public async Task PlanAsync()
    {
        int generation = ++_planGeneration;
        var options = Options;
        Planning = true;
        Progress = "Elaborating…";
        try
        {
            var plan = await Task.Run(() => _plan(options));
            if (generation != _planGeneration) return;
            _current = plan;
            Summary = plan.Summary;
            Notes = string.Join("\n", plan.Notes);
            Error = null;
        }
        catch (Exception e) when (e is StepExportException or GeometryKernelException or IOException or UnauthorizedAccessException)
        {
            if (generation != _planGeneration) return;
            _current = null;
            Summary = "";
            Error = e.Message;
        }
        finally
        {
            if (generation == _planGeneration)
            {
                Planning = false;
                Progress = "";
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        if (_current is not { } plan) return;
        s_last = Options;
        _cts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token = _cts.Token,
            Progress = new Progress<RunProgress>(p => Progress = p.Stage is { Length: > 0 } s ? $"{s}…" : "Writing…"),
        };
        Writing = true;
        Progress = "Writing the STEP file…";
        try
        {
            Result = await Task.Run(() => _write(plan, control));
            CloseRequested?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            CloseRequested?.Invoke(false);
        }
        catch (Exception e) when (e is StepExportException or GeometryKernelException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
        finally
        {
            Writing = false;
            Progress = "";
        }
    }

    /// <summary>Cancel: an export in flight is killed and writes nothing.</summary>
    [RelayCommand]
    private void Cancel()
    {
        CancelWrite();
        CloseRequested?.Invoke(false);
    }

    /// <summary>Kills an export in flight; the window closing any way but a finished export calls it.</summary>
    public void CancelWrite() => _cts?.Cancel();
}
