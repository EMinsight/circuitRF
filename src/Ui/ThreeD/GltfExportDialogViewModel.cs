// brief-em3d-111 R-em3d111-3b — the Export glTF dialog: after the save dialog, one small options dialog. Assembly, Include camera,
// Include field plot (a drawn Surfaces or Faces plot, or None) and a summary line — objects, triangles, materials and the extensions the
// file will use — before anything is written. Every rule is GltfExport's; this is the dialog's state.
//
// The export is BUILT for the choices as they stand (off the UI thread, a newer build superseding an older one), so the summary is the
// file's own count; Export writes those bytes. The options are remembered per session, as Export STEP's are.

using CircuitRF.Render.Scene3D.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class GltfExportDialogViewModel : ObservableObject
{
    /// <summary>The choices the last export in this session made: Assembly, Include camera, and the plot's name.</summary>
    private static (bool Assembly, bool Camera, string? Field) s_last = (false, true, null);

    /// <summary>The field combo's first entry.</summary>
    public const string NoField = "None";

    private readonly GltfExportSource _source;
    private readonly GltfCamera? _camera;
    private readonly IReadOnlyList<GltfField> _fields;
    private CancellationTokenSource? _plan;
    private int _planGeneration;
    private readonly bool _loading;

    /// <param name="source">The scene as the view draws it, what it shows, its elaboration and document.</param>
    /// <param name="camera">The view's camera, or null for none to offer.</param>
    /// <param name="fields">The drawn Surfaces and Faces plots, each as the view colours it now.</param>
    public GltfExportDialogViewModel(string targetPath, GltfExportSource source, GltfCamera? camera, IReadOnlyList<GltfField> fields)
    {
        TargetPath = targetPath;
        _source = source;
        _camera = camera;
        _fields = fields;
        FieldChoices = [NoField, .. fields.Select(f => f.Name)];
        _loading = true;
        Assembly = s_last.Assembly;
        IncludeCamera = camera is not null && s_last.Camera;
        FieldIndex = Math.Max(0, FieldChoices.ToList().IndexOf(s_last.Field ?? NoField));
        _loading = false;
    }

    public string TargetPath { get; }
    public string Title => $"Export glTF — {Path.GetFileName(TargetPath)}";
    public IReadOnlyList<string> FieldChoices { get; }
    public bool HasCamera => _camera is not null;
    public bool HasFields => _fields.Count > 0;

    [ObservableProperty] private bool _assembly;
    [ObservableProperty] private bool _includeCamera;
    [ObservableProperty] private int _fieldIndex;

    [ObservableProperty] private bool _planning;
    [ObservableProperty] private bool _writing;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _notes = "";

    public bool Flattened { get => !Assembly; set => Assembly = !value; }
    public bool HasNotes => Notes.Length > 0;
    public bool Busy => Planning || Writing;

    /// <summary>The plot chosen, or null for None.</summary>
    public GltfField? Field => FieldIndex >= 1 && FieldIndex <= _fields.Count ? _fields[FieldIndex - 1] : null;

    /// <summary>The options as they stand.</summary>
    public GltfExportOptions Options => new() { Assembly = Assembly, Field = Field, Camera = IncludeCamera ? _camera : null };

    /// <summary>The file built for the current choices — what Export writes.</summary>
    public GltfExportResult? Current { get; private set; }

    /// <summary>Where it was written, once the export succeeded.</summary>
    public string? Written { get; private set; }

    public bool CanExport => !Busy && Error is null && Current is not null;

    partial void OnAssemblyChanged(bool value) { OnPropertyChanged(nameof(Flattened)); Replan(); }
    partial void OnIncludeCameraChanged(bool value) => Replan();
    partial void OnFieldIndexChanged(int value) => Replan();
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

    /// <summary>Builds the file for the choices as they stand: the summary line and its notes. A newer build supersedes an older one.</summary>
    public async Task PlanAsync()
    {
        int generation = ++_planGeneration;
        _plan?.Cancel();
        var cts = _plan = new CancellationTokenSource();
        var options = Options;
        Planning = true;
        try
        {
            var result = await Task.Run(() => GltfExport.Build(_source, options, cts.Token));
            if (generation != _planGeneration) return;
            Current = result;
            Summary = result.Objects == 0 && options.Field is null ? "Nothing to export: no shown object has a material." : result.Summary;
            Error = result.Objects == 0 && options.Field is null ? Summary : null;
            Notes = string.Join("\n", result.Notes);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            if (generation != _planGeneration) return;
            Current = null;
            Summary = "";
            Error = e.Message;
        }
        finally
        {
            if (generation == _planGeneration) Planning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        if (Current is not { } result) return;
        s_last = (Assembly, IncludeCamera, Field?.Name);
        Writing = true;
        try
        {
            Written = await Task.Run(() => GltfExport.Write(result, TargetPath));
            CloseRequested?.Invoke(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
        finally
        {
            Writing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _plan?.Cancel();
        CloseRequested?.Invoke(false);
    }
}
