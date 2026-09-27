// The Impedance panel (brief-impedance-3): the dockable replacement for the modal Impedance Analysis
// dialog, on the DRC panel's terms — it follows the ACTIVE layout, holds no result of its own (the
// report lives on the layout's view model, LayoutEditorViewModel.TraceImpedance.cs), and every string
// in a result row is one the PDF prints, read from the report through its own formatters.
//
// Top to bottom: Settings (target, tolerance, warning band, highest frequency), Layers, Traces (brief
// 2's width classes, from a background survey), the Scope line, Run / Cancel and progress, the results
// (verdict tiles, a filter, one row per trace expandable to its findings), and Export PDF. Settings and
// scope are saved on the layout AS THEY ARE EDITED (R-imp3-1c), through the one SaveImpedanceReview.
// Accepted findings (brief-impedance-5): Accept… on selected finding rows asks for a reason and applies
// at once, through the layout's AcceptImpedanceFindings — no re-run.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Layout.Impedance;

/// <summary>Which result rows the panel lists (R-imp3-2b). <see cref="Accepted"/> is appended, not
/// inserted, so a stored number keeps its meaning.</summary>
public enum ImpedanceResultFilter { All, WarningsAndFailures, Failures, Accepted }

public sealed partial class ImpedancePanelViewModel : ObservableObject
{
    // ── the layout ───────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private LayoutEditorViewModel? _editor;

    /// <summary>True when a layout document is active — otherwise the panel says why it is empty.</summary>
    public bool IsLayoutActive => Editor is not null;

    private bool _loading;
    private CancellationTokenSource? _surveyCts;
    private CancellationTokenSource? _runCts;

    /// <summary>Follows the active layout (the DRC panel's <c>SetActiveLayout</c>), reloading the
    /// settings saved on it and surveying its trace widths.</summary>
    public void SetEditor(LayoutEditorViewModel? vm)
    {
        if (ReferenceEquals(vm, Editor)) return;
        Editor = vm;
    }

    partial void OnEditorChanged(LayoutEditorViewModel? oldValue, LayoutEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditorPropertyChanged;
            oldValue.ShowImpedanceScope = false;
        }
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditorPropertyChanged;
            newValue.ShowImpedanceScope = IsShown;
        }
        _surveyCts?.Cancel();

        LoadSettings();
        RebuildResults();
        OnPropertyChanged(nameof(IsLayoutActive));
        RefreshCommands();
        RequestSurvey();
    }

    // The survey flattens the artwork on the UI thread and solves one cut per width class, so it runs
    // only while the panel is ON SCREEN: the panel follows every layout activation whether or not
    // anyone has it open, and a closed panel must cost nothing.
    private bool _isShown;
    private bool _surveyPending;

    /// <summary>Set by the view as it is attached to and detached from the window.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown == value) return;
            _isShown = value;
            // The scope is drawn on the canvas only while the panel is on screen (R-imp4-1d).
            if (Editor is { } vm) vm.ShowImpedanceScope = value;
            if (value && _surveyPending) RequestSurvey();
        }
    }

    private void RequestSurvey()
    {
        _surveyPending = Editor is not null;
        if (_surveyPending && IsShown)
        {
            _surveyPending = false;
            _ = SurveyAsync();
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LayoutEditorViewModel.ImpedanceReport):
                RebuildResults();
                RebuildSelectorRows();
                break;
            case nameof(LayoutEditorViewModel.ImpedanceScopeVersion):
                if (!_editingSelectors) RebuildSelectorRows();
                UpdateScopeText();
                break;
            case nameof(LayoutEditorViewModel.ImpedanceScopeTool):
                OnPropertyChanged(nameof(IsDrawingRegion));
                OnPropertyChanged(nameof(IsPicking));
                break;
            case nameof(LayoutEditorViewModel.IsImpedanceStale):
                OnPropertyChanged(nameof(IsStale));
                break;
            case nameof(LayoutEditorViewModel.Technology):
                LoadSettings();
                RequestSurvey();
                break;
        }
    }

    // ── settings ─────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _targetText = "50";
    [ObservableProperty] private string _toleranceText = "10";
    [ObservableProperty] private string _warningText = "20";
    [ObservableProperty] private string _frequencyText = "";

    /// <summary>"Pass 45.0–55.0 Ω · Warning 40.0–60.0 Ω", or the last valid band.</summary>
    [ObservableProperty] private string _bandText = "";

    /// <summary>Why Run cannot go, or empty.</summary>
    [ObservableProperty] private string _validationText = "";

    public bool HasValidation => ValidationText.Length > 0;
    partial void OnValidationTextChanged(string value) => OnPropertyChanged(nameof(HasValidation));

    partial void OnTargetTextChanged(string value) => SettingEdited();
    partial void OnToleranceTextChanged(string value) => SettingEdited();
    partial void OnWarningTextChanged(string value) => SettingEdited();
    partial void OnFrequencyTextChanged(string value) => SettingEdited();

    public ObservableCollection<ImpedanceLayerRow> Layers { get; } = [];

    /// <summary>True when the technology binds no drawing layer to a conductor.</summary>
    public bool HasNoLayers => IsLayoutActive && Layers.Count == 0;

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            foreach (var l in Layers) l.PropertyChanged -= OnLayerRowChanged;
            Layers.Clear();
            WidthGroups.Clear();
            _widthRows.Clear();
            _surveyed = false;
            _survey = null;
            SurveyText = "";

            var saved = Editor?.SavedImpedanceReview ?? new TraceImpedanceReview();
            TargetText = saved.TargetOhms.ToString("0.##", CultureInfo.InvariantCulture);
            ToleranceText = saved.TolerancePercent.ToString("0.##", CultureInfo.InvariantCulture);
            WarningText = saved.WarningPercent.ToString("0.##", CultureInfo.InvariantCulture);
            FrequencyText = saved.MaxFrequencyHz is { } hz ? TraceImpedanceReport.Hz(hz) : "";

            foreach (var choice in Editor?.TraceImpedanceLayers() ?? [])
            {
                var row = new ImpedanceLayerRow(choice,
                    choice.HasCopper && (saved.Layers is null ||
                                         saved.Layers.Contains(choice.Name, StringComparer.OrdinalIgnoreCase)));
                row.PropertyChanged += OnLayerRowChanged;
                Layers.Add(row);
            }
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(HasNoLayers));
        RebuildSelectorRows();
        Validate();
        UpdateScopeText();
    }

    private void OnLayerRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ImpedanceLayerRow.IsChecked)) return;
        ShowGroups();
        SettingEdited();
    }

    [RelayCommand]
    private void AllLayers()
    {
        foreach (var l in Layers) if (l.HasCopper) l.IsChecked = true;
    }

    [RelayCommand]
    private void NoLayers()
    {
        foreach (var l in Layers) l.IsChecked = false;
    }

    /// <summary>R-imp3-1c: every edit is saved on the layout as it is made — a field that does not parse
    /// keeps what was saved rather than writing a half-typed value.</summary>
    private void SettingEdited()
    {
        if (_loading) return;
        bool ok = Validate();
        UpdateScopeText();
        if (ok && Editor is { } vm) vm.SaveImpedanceReview(CurrentReview());
    }

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>The highest frequency, in Hz: null when the field is blank (the rule is off). A bare
    /// number is GHz, the unit a board's highest frequency is nearly always said in; a typed unit is
    /// honoured.</summary>
    internal static bool TryFrequency(string? text, out double? hz)
    {
        hz = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!CircuitRF.Design.Matching.MatchValueFormat.TryParseWithUnit(
                text.Replace(',', '.'), CircuitRF.Design.Matching.MatchQuantity.Frequency, "GHz", out double v, out _)
            || !(v > 0))
            return false;
        hz = v;
        return true;
    }

    /// <summary>The pass band, and whether Run can go.</summary>
    private bool Validate()
    {
        string? problem = null;
        if (!TryNumber(TargetText, out double target) || !(target > 0))
            problem = "Enter the target impedance in ohms, e.g. 50.";
        else if (!TryNumber(ToleranceText, out double tol) || !(tol > 0) || tol >= 100)
            problem = "Enter the tolerance as a percentage above 0 and below 100, e.g. 10.";
        else if (!TryNumber(WarningText, out double warn) || !(warn > tol) || warn >= 100)
            problem = string.Create(CultureInfo.InvariantCulture,
                $"Enter the warning band as a percentage wider than the tolerance (± {tol:0.##} %) and below 100, e.g. 20.");
        else if (!TryFrequency(FrequencyText, out _))
            problem = "Enter the highest frequency with its unit, e.g. 6 GHz, or leave it blank.";
        else
        {
            BandText = string.Create(CultureInfo.InvariantCulture,
                $"Pass {target * (1 - tol / 100):0.0}–{target * (1 + tol / 100):0.0} Ω · " +
                $"Warning {target * (1 - warn / 100):0.0}–{target * (1 + warn / 100):0.0} Ω");
            if (IsLayoutActive && !Layers.Any(l => l.IsChecked)) problem = "Tick at least one layer.";
        }

        ValidationText = problem ?? "";
        RefreshCommands();
        return problem is null;
    }

    /// <summary>
    /// The review this panel says: the settings, the layers ticked (null when every layer with copper
    /// is), and the widths ticked — or, before the survey has finished, the saved scope untouched.
    /// </summary>
    internal TraceImpedanceReview CurrentReview()
    {
        TryNumber(TargetText, out double target);
        TryNumber(ToleranceText, out double tolerance);
        TryNumber(WarningText, out double warning);
        TryFrequency(FrequencyText, out double? maxFrequency);
        var enabled = Layers.Where(l => l.HasCopper).ToList();
        var scope = CurrentScope();
        return new TraceImpedanceReview
        {
            TargetOhms = target,
            TolerancePercent = tolerance,
            WarningPercent = warning,
            MaxFrequencyHz = maxFrequency,
            Layers = enabled.All(l => l.IsChecked) ? null : [.. enabled.Where(l => l.IsChecked).Select(l => l.Name)],
            Scope = scope is { IsEmpty: false } ? scope : null,
        };
    }

    /// <summary>The widths ticked (or, before the survey, the saved widths) with the selectors as the
    /// layout holds them — the selectors are edited only through the layout's EditImpedanceScope.</summary>
    private TraceImpedanceScope? CurrentScope()
    {
        if (Editor is not { } vm) return null;
        var saved = vm.ImpedanceScope;
        var scope = _surveyed ? TraceWidthRows.ScopeOf(_widthRows.Where(r => r.IsChecked).Select(r => r.Row)) : saved.Clone();
        if (_surveyed)
        {
            scope.Regions = [.. saved.Regions];
            scope.Picks = [.. saved.Picks];
            scope.Nets = [.. saved.Nets];
        }
        return scope;
    }

    // ── the trace widths (brief 2's survey) and the scope line ───────────────────────────────

    public ObservableCollection<ImpedanceWidthGroup> WidthGroups { get; } = [];
    private readonly List<ImpedanceWidthRow> _widthRows = [];
    private bool _surveyed;
    private TraceWidthSurvey? _survey;

    [ObservableProperty] private bool _isSurveying;
    [ObservableProperty] private string _surveyText = "";

    /// <summary>The scope in the report's words, live as it is edited (R-imp3-1b).</summary>
    [ObservableProperty] private string _scopeText = "";

    [RelayCommand]
    private Task Survey() => SurveyAsync();

    /// <summary>The survey, in the background; retargeting the panel cancels it.</summary>
    private async Task SurveyAsync()
    {
        if (Editor is not { } vm) return;
        _surveyCts?.Cancel();
        var cts = _surveyCts = new CancellationTokenSource();
        IsSurveying = true;
        SurveyText = "Surveying the trace widths…";
        var control = new RunControl
        {
            Token = cts.Token,
            MinReportIntervalMs = 60,
            Progress = new Progress<RunProgress>(p =>
            {
                if (!cts.IsCancellationRequested && p.Stage.Length > 0) SurveyText = $"Surveying the trace widths — {p.Stage}…";
            }),
        };
        TraceWidthSurvey? survey;
        try { survey = await vm.SurveyTraceWidthsAsync(control); }
        catch (OperationCanceledException) { return; }
        finally { if (ReferenceEquals(_surveyCts, cts)) IsSurveying = false; }
        if (cts.IsCancellationRequested || !ReferenceEquals(vm, Editor)) return;

        if (survey is null || survey.Refusal is not null)
        {
            SurveyText = survey?.Refusal ?? "This layout has no technology, so there are no traces to survey.";
            return;
        }
        BuildWidthRows(survey);
    }

    private void BuildWidthRows(TraceWidthSurvey survey)
    {
        _survey = survey;
        _surveyed = true;
        SurveyText = "";
        foreach (var r in _widthRows) r.PropertyChanged -= OnWidthRowChanged;
        WidthGroups.Clear();
        _widthRows.Clear();

        var rows = TraceWidthRows.Merge(survey, Editor?.SavedImpedanceReview?.Scope);
        foreach (var layer in Layers)
        {
            var mine = rows.Where(r => string.Equals(r.LayerName, layer.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0 || !layer.HasCopper) continue;
            var group = new ImpedanceWidthGroup(layer.Name);
            foreach (var row in mine)
            {
                var vmRow = new ImpedanceWidthRow(row, Editor!);
                group.Rows.Add(vmRow);
                _widthRows.Add(vmRow);
            }
            WidthGroups.Add(group);
        }
        // A row on a layer this panel does not list is kept as it is, unseen, never dropped.
        foreach (var row in rows)
            if (!_widthRows.Any(r => ReferenceEquals(r.Row, row))) _widthRows.Add(new ImpedanceWidthRow(row, Editor!));
        foreach (var r in _widthRows) r.PropertyChanged += OnWidthRowChanged;

        if (WidthGroups.Count == 0) SurveyText = "No traces were found on the copper layers.";
        ShowGroups();
        RebuildSelectorRows();
        UpdateScopeText();
    }

    private void OnWidthRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImpedanceWidthRow.IsChecked)) SettingEdited();
    }

    /// <summary>A layer's widths are listed while the layer is ticked.</summary>
    private void ShowGroups()
    {
        foreach (var g in WidthGroups)
            g.IsVisible = Layers.FirstOrDefault(l => string.Equals(l.Name, g.LayerName, StringComparison.OrdinalIgnoreCase))
                               ?.IsChecked == true;
    }

    private void UpdateScopeText()
    {
        if (Editor is not { } vm || _survey is not { } survey || !_surveyed)
        {
            ScopeText = "";
            return;
        }
        var ticked = Layers.Where(l => l.IsChecked).Select(l => l.Name).ToList();
        ScopeText = TraceImpedanceAnalysis.DescribeScope(survey, ticked, CurrentScope(), vm.Model.DbuPerMicron, vm.DisplayUnit);
    }

    // ── the selectors: regions, picks, nets (brief-impedance-4) ──────────────────────────────

    public ObservableCollection<ImpedanceRegionRow> Regions { get; } = [];
    public ObservableCollection<ImpedancePickRow> Picks { get; } = [];
    public ObservableCollection<ImpedanceNetRow> NetRows { get; } = [];
    private readonly List<ImpedanceNetRow> _allNets = [];

    public bool HasRegions => Regions.Count > 0;
    public bool HasPicks => Picks.Count > 0;

    /// <summary>R-imp4-3a: the Nets section exists only when copper on the layout carries a net — absent,
    /// not empty, otherwise. A saved net the artwork no longer has keeps it on screen.</summary>
    public bool HasNets => _allNets.Count > 0;

    /// <summary>Traces no net selector can choose, counted so the gap is visible (R-imp4-3b).</summary>
    [ObservableProperty] private string _netlessText = "";

    [ObservableProperty] private string _netFilterText = "";
    partial void OnNetFilterTextChanged(string value) => FilterNets();

    public bool IsDrawingRegion => Editor?.ImpedanceScopeTool is ImpedanceScopeTool.Rectangle or ImpedanceScopeTool.Lasso;

    /// <summary>The Pick traces toggle: armed while checked; Escape on the canvas unchecks it.</summary>
    public bool IsPicking
    {
        get => Editor?.ImpedanceScopeTool == ImpedanceScopeTool.Pick;
        set
        {
            if (Editor is not { } vm || value == IsPicking) return;
            vm.ArmImpedanceScopeTool(value ? ImpedanceScopeTool.Pick : ImpedanceScopeTool.None);
        }
    }

    [ObservableProperty] private ImpedanceRegionRow? _selectedRegion;
    partial void OnSelectedRegionChanged(ImpedanceRegionRow? value)
    {
        if (Editor is { } vm) vm.SelectedImpedanceRegion = value is null ? -1 : Regions.IndexOf(value);
    }

    // Set while this panel itself edits the scope, so the layout's change notice does not rebuild the
    // rows under the control being typed into.
    private bool _editingSelectors;

    private void EditSelectors(Action<TraceImpedanceScope> edit)
    {
        if (Editor is not { } vm) return;
        _editingSelectors = true;
        try { vm.EditImpedanceScope(edit); }
        finally { _editingSelectors = false; }
    }

    [RelayCommand]
    private void AddRectangleRegion() => Editor?.ArmImpedanceScopeTool(ImpedanceScopeTool.Rectangle);

    [RelayCommand]
    private void AddLassoRegion() => Editor?.ArmImpedanceScopeTool(ImpedanceScopeTool.Lasso);

    internal void DeleteRegion(ImpedanceRegionRow? row)
    {
        int i = row is null ? -1 : Regions.IndexOf(row);
        if (i < 0) return;
        Editor?.EditImpedanceScope(s => { if (i < s.Regions.Count) s.Regions.RemoveAt(i); });
    }

    private void DeletePick(ImpedancePickRow? row)
    {
        if (row is null) return;
        Editor?.EditImpedanceScope(s => s.Picks.Remove(row.Pick));
    }

    internal void RenameRegion(ImpedanceRegionRow row, string name)
    {
        int i = Regions.IndexOf(row);
        if (i < 0) return;
        string? trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        EditSelectors(s => { if (i < s.Regions.Count) s.Regions[i] = s.Regions[i] with { Name = trimmed }; });
        UpdateScopeText();
    }

    private void NetTicked()
    {
        EditSelectors(s => s.Nets = [.. _allNets.Where(n => n.IsChecked).Select(n => n.Name)]);
        UpdateScopeText();
    }

    /// <summary>The three lists, from the scope the layout holds and the latest survey and report.</summary>
    private void RebuildSelectorRows()
    {
        var vm = Editor;
        var scope = vm?.ImpedanceScope ?? new TraceImpedanceScope();
        var missing = vm?.ImpedanceReport?.PicksWithoutCopper ?? [];

        var selected = SelectedRegion is { } sel ? Regions.IndexOf(sel) : -1;
        Regions.Clear();
        for (int i = 0; i < scope.Regions.Count; i++) Regions.Add(new ImpedanceRegionRow(this, scope.Regions[i], i));
        SelectedRegion = selected >= 0 && selected < Regions.Count ? Regions[selected] : null;

        Picks.Clear();
        foreach (var p in scope.Picks)
            Picks.Add(new ImpedancePickRow(p, missing.Contains(p), vm?.FormatPoint(p.X, p.Y) ?? "", DeletePick));

        foreach (var n in _allNets) n.PropertyChanged -= OnNetRowChanged;
        _allNets.Clear();
        var surveyed = _survey?.Nets ?? [];
        foreach (string name in surveyed)
            _allNets.Add(new ImpedanceNetRow(name, scope.Nets.Contains(name, StringComparer.OrdinalIgnoreCase), missing: false));
        foreach (string name in scope.Nets)
            if (!surveyed.Contains(name, StringComparer.OrdinalIgnoreCase))
                _allNets.Add(new ImpedanceNetRow(name, true, missing: _surveyed));
        foreach (var n in _allNets) n.PropertyChanged += OnNetRowChanged;
        FilterNets();

        int netless = _survey?.Layers.Where(l => Layers.Any(r => r.IsChecked && string.Equals(r.Name, l.Name, StringComparison.OrdinalIgnoreCase)))
                                     .Sum(l => l.NetlessTraces) ?? 0;
        NetlessText = surveyed.Count == 0 || netless == 0 ? ""
            : $"{(netless == 1 ? "1 trace carries" : $"{netless} traces carry")} no net (or two) on the copper under it, so no net can choose {(netless == 1 ? "it" : "them")}.";

        OnPropertyChanged(nameof(HasRegions));
        OnPropertyChanged(nameof(HasPicks));
        OnPropertyChanged(nameof(HasNets));
    }

    private void OnNetRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImpedanceNetRow.IsChecked)) NetTicked();
    }

    private void FilterNets()
    {
        NetRows.Clear();
        foreach (var n in _allNets)
            if (NetFilterText.Length == 0 || n.Name.Contains(NetFilterText.Trim(), StringComparison.OrdinalIgnoreCase))
                NetRows.Add(n);
    }

    // ── the run ──────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _runLayerText = "";
    [ObservableProperty] private string _runLayerCountText = "";
    [ObservableProperty] private string _runStageText = "";
    [ObservableProperty] private double _runLayerProgress;
    [ObservableProperty] private double _runOverallProgress;

    partial void OnIsRunningChanged(bool value) => RefreshCommands();

    private bool CanRun() => IsLayoutActive && !IsRunning && ValidationText.Length == 0 && Layers.Any(l => l.IsChecked);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        if (Editor is not { } vm || IsRunning || !Validate()) return;
        var review = CurrentReview();
        vm.SaveImpedanceReview(review);
        var chosen = Layers.Where(l => l.IsChecked).ToList();

        var cts = _runCts = new CancellationTokenSource();
        IsRunning = true;
        RunLayerText = "Reading the copper…";
        RunLayerCountText = "";
        RunStageText = "";
        RunLayerProgress = 0;
        RunOverallProgress = 0;

        // Progress<T> is created on the UI thread, so every report arrives on it.
        var control = new RunControl
        {
            Token = cts.Token,
            Total = chosen.Count,
            MinReportIntervalMs = 60,
            Progress = new Progress<RunProgress>(OnProgress),
        };
        var options = new TraceImpedanceOptions
        {
            TargetOhms = review.TargetOhms,
            TolerancePercent = review.TolerancePercent,
            WarningPercent = review.WarningPercent,
            MaxFrequencyHz = review.MaxFrequencyHz,
            Layers = [.. chosen.Select(l => l.Choice.Key)],
            Scope = review.Scope,
        };
        try { await vm.RunTraceImpedanceAsync(options, control); }
        finally
        {
            IsRunning = false;
            _runCts = null;
            cts.Dispose();
        }
    }

    private bool CanCancel() => IsRunning;

    /// <summary>Stops the run; the layers that finished are kept.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_runCts is not { IsCancellationRequested: false } cts) return;
        cts.Cancel();
        RunStageText = "Cancelling — keeping the layers that finished…";
    }

    private static readonly Regex StageShape = new(@"^(?<layer>.*) \((?<i>\d+) of (?<n>\d+)\): (?<what>.*)$");

    private void OnProgress(RunProgress p)
    {
        if (!IsRunning || _runCts is not { IsCancellationRequested: false }) return;
        var m = StageShape.Match(p.Stage);
        if (!m.Success)
        {
            if (p.Stage.Length > 0) RunLayerText = p.Stage + "…";
            return;
        }
        int i = int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture);
        int n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        string what = m.Groups["what"].Value;
        double stage = what == "solving" && p.StageTotal > 0 ? (double)p.StageCompleted / p.StageTotal
                     : what == "cutting" ? 0.05 : 0;

        RunLayerText = m.Groups["layer"].Value;
        RunLayerCountText = $"Layer {i} of {n}";
        RunLayerProgress = stage;
        RunOverallProgress = (i - 1 + stage) / Math.Max(1, n);
        RunStageText = what == "solving" && p.StageTotal > 0
            ? $"Solving cross-sections — {p.StageCompleted:N0} of {p.StageTotal:N0}"
            : what.Length > 0 ? char.ToUpperInvariant(what[0]) + what[1..] + "…" : "";
    }

    // ── the results ──────────────────────────────────────────────────────────────────────────

    /// <summary>The rows the filter shows; a trace's findings and notes follow it when it is expanded.</summary>
    public ObservableCollection<ImpedanceResultRow> Rows { get; } = [];
    private List<ImpedanceTraceResultRow> _traceRows = [];

    public TraceImpedanceReport? Report => Editor?.ImpedanceReport;
    public bool HasResults => Report is not null;
    public bool IsStale => Editor?.IsImpedanceStale == true;
    public string StaleText => TraceImpedanceReport.StaleText + ". Run again to review the artwork as it is now.";

    public int PassCount => Report?.PassCount ?? 0;
    public int WarningCount => Report?.WarningCount ?? 0;
    public int FailCount => Report?.FailCount ?? 0;
    public int AcceptedCount => Report?.AcceptedCount ?? 0;
    public bool HasAccepted => AcceptedCount > 0;
    public int OutOfScopeCount => Report?.OutOfScopeCount ?? 0;
    public bool HasOutOfScope => OutOfScopeCount > 0;

    /// <summary>The report's own scope sentence, and a cancelled run said to be one.</summary>
    public string ResultSummaryText => Report is not { } r ? ""
        : r.ScopeText + (r.Cancelled ? $" Cancelled after {r.Layers.Count} of {r.LayersRequested.Count} layers." : "");

    /// <summary>What an empty list means under the current filter.</summary>
    public string EmptyRowsText => !HasResults ? "" : Rows.Count > 0 ? ""
        : Filter switch
        {
            ImpedanceResultFilter.Failures => "No trace fails.",
            ImpedanceResultFilter.WarningsAndFailures => "No trace warns or fails.",
            ImpedanceResultFilter.Accepted => "No finding has been accepted.",
            _ => "No traces were found in the scope.",
        };

    [ObservableProperty] private ImpedanceResultFilter _filter = ImpedanceResultFilter.WarningsAndFailures;

    public bool FilterAll
    {
        get => Filter == ImpedanceResultFilter.All;
        set { if (value) Filter = ImpedanceResultFilter.All; }
    }

    public bool FilterWarningsAndFailures
    {
        get => Filter == ImpedanceResultFilter.WarningsAndFailures;
        set { if (value) Filter = ImpedanceResultFilter.WarningsAndFailures; }
    }

    public bool FilterFailures
    {
        get => Filter == ImpedanceResultFilter.Failures;
        set { if (value) Filter = ImpedanceResultFilter.Failures; }
    }

    public bool FilterAccepted
    {
        get => Filter == ImpedanceResultFilter.Accepted;
        set { if (value) Filter = ImpedanceResultFilter.Accepted; }
    }

    partial void OnFilterChanged(ImpedanceResultFilter value)
    {
        OnPropertyChanged(nameof(FilterAll));
        OnPropertyChanged(nameof(FilterWarningsAndFailures));
        OnPropertyChanged(nameof(FilterFailures));
        OnPropertyChanged(nameof(FilterAccepted));
        ApplyFilter();
    }

    /// <summary>Whether a trace of <paramref name="v"/> is listed under <paramref name="filter"/>. A trace
    /// that could not be solved at all was not reviewed, so it is listed with the failures.</summary>
    internal static bool Shows(ImpedanceResultFilter filter, TraceRun t) => filter switch
    {
        ImpedanceResultFilter.Failures => t.Verdict is TraceVerdict.Fail or TraceVerdict.Unsolved,
        ImpedanceResultFilter.WarningsAndFailures => t.Verdict is not TraceVerdict.Pass,
        ImpedanceResultFilter.Accepted => t.AcceptedCount > 0,
        _ => true,
    };

    /// <summary>Whether an expanded trace lists <paramref name="child"/> under <paramref name="filter"/>:
    /// the warning and failure filters hide accepted findings (R-imp5-3b), Accepted lists only them.</summary>
    internal static bool ShowsChild(ImpedanceResultFilter filter, ImpedanceResultRow child) => filter switch
    {
        ImpedanceResultFilter.WarningsAndFailures or ImpedanceResultFilter.Failures => child.Issue?.Accepted is null,
        ImpedanceResultFilter.Accepted => child.Issue?.Accepted is not null,
        _ => true,
    };

    [ObservableProperty] private ImpedanceResultRow? _selectedRow;

    partial void OnSelectedRowChanged(ImpedanceResultRow? value)
    {
        Editor?.SelectImpedance(value?.Trace, value?.Issue);
        ZoomToSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanZoomToSelected() => SelectedRow is not null && Editor is not null;

    /// <summary>R-imp3-2d: a trace row frames the trace, a finding its stretch — the DRC panel's
    /// <c>ZoomToSelectedViolationCommand</c>, on a double-click for the same reason.</summary>
    [RelayCommand(CanExecute = nameof(CanZoomToSelected))]
    private void ZoomToSelected()
    {
        if (SelectedRow is { } row && Editor is { } vm) vm.ZoomToImpedance(row.Trace, row.Issue);
    }

    private void RebuildResults()
    {
        // A re-applied acceptance rebuilds every row; the traces the reviewer had open stay open.
        var expanded = _traceRows.Where(t => t.IsExpanded).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var t in _traceRows) t.PropertyChanged -= OnTraceRowChanged;
        _traceRows = [];
        if (Report is { } r)
            foreach (var layer in r.Layers)
                foreach (var t in layer.Traces)
                {
                    var row = new ImpedanceTraceResultRow(r, layer, t) { IsExpanded = expanded.Contains(t.Id) };
                    row.PropertyChanged += OnTraceRowChanged;
                    _traceRows.Add(row);
                }
        ApplyFilter();
        CancelAccept();
        StaleAcceptances.Clear();
        foreach (var a in Report?.StaleAcceptances ?? [])
            StaleAcceptances.Add(new ImpedanceStaleAcceptanceRow(a, row => Editor?.RemoveImpedanceAcceptances([row.Acceptance])));
        OnPropertyChanged(nameof(HasStaleAcceptances));

        OnPropertyChanged(nameof(Report));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(PassCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(FailCount));
        OnPropertyChanged(nameof(AcceptedCount));
        OnPropertyChanged(nameof(HasAccepted));
        OnPropertyChanged(nameof(OutOfScopeCount));
        OnPropertyChanged(nameof(HasOutOfScope));
        OnPropertyChanged(nameof(ResultSummaryText));
        RefreshCommands();
    }

    private void OnTraceRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImpedanceTraceResultRow.IsExpanded)) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keep = SelectedRow;
        Rows.Clear();
        foreach (var t in _traceRows)
        {
            if (!Shows(Filter, t.Trace)) continue;
            Rows.Add(t);
            if (t.IsExpanded) foreach (var child in t.Children) if (ShowsChild(Filter, child)) Rows.Add(child);
        }
        SelectedRow = keep is not null && Rows.Contains(keep) ? keep : null;
        SetSelectedRows(_selectedRows.Where(Rows.Contains).ToList());
        OnPropertyChanged(nameof(EmptyRowsText));
    }

    // ── accepting findings (brief-impedance-5 R-imp5-3) ─────────────────────────────────────

    private IReadOnlyList<ImpedanceResultRow> _selectedRows = [];
    private List<ImpedanceFindingResultRow> _accepting = [];

    /// <summary>Every selected row — the list selects several, so one reason can accept several findings.
    /// Set by the view from the list's selection.</summary>
    public void SetSelectedRows(IReadOnlyList<ImpedanceResultRow> rows)
    {
        _selectedRows = rows;
        BeginAcceptCommand.NotifyCanExecuteChanged();
        UnacceptCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<ImpedanceFindingResultRow> SelectedFindings => _selectedRows.OfType<ImpedanceFindingResultRow>();

    /// <summary>The reason box is open, asking why for <see cref="AcceptTargetText"/>.</summary>
    [ObservableProperty] private bool _isAccepting;

    /// <summary>The reason being typed. Required: an empty one is refused, not saved (R-imp5-1d).</summary>
    [ObservableProperty] private string _acceptReason = "";

    partial void OnAcceptReasonChanged(string value)
    {
        ConfirmAcceptCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(AcceptReasonMissing));
    }

    public bool AcceptReasonMissing => string.IsNullOrWhiteSpace(AcceptReason);

    /// <summary>"Accept 1 finding on T3" — what the reason is for.</summary>
    [ObservableProperty] private string _acceptTargetText = "";

    private bool CanBeginAccept() => !IsAccepting && SelectedFindings.Any(f => f.Issue!.Accepted is null);

    [RelayCommand(CanExecute = nameof(CanBeginAccept))]
    private void BeginAccept()
    {
        _accepting = [.. SelectedFindings.Where(f => f.Issue!.Accepted is null)];
        if (_accepting.Count == 0) return;
        var ids = _accepting.Select(f => f.Trace.Id).Distinct().ToList();
        AcceptTargetText = $"Accept {(_accepting.Count == 1 ? "1 finding" : $"{_accepting.Count} findings")} on {string.Join(", ", ids)}";
        AcceptReason = "";
        IsAccepting = true;
        BeginAcceptCommand.NotifyCanExecuteChanged();
    }

    private bool CanConfirmAccept() => IsAccepting && !AcceptReasonMissing;

    [RelayCommand(CanExecute = nameof(CanConfirmAccept))]
    private void ConfirmAccept()
    {
        if (!CanConfirmAccept() || Editor is not { } vm) return;
        var findings = _accepting.Select(f => (f.Trace, f.Issue!)).ToList();
        string reason = AcceptReason;
        CancelAccept();
        vm.AcceptImpedanceFindings(findings, reason);
    }

    [RelayCommand]
    private void CancelAccept()
    {
        _accepting = [];
        IsAccepting = false;
        AcceptReason = "";
        BeginAcceptCommand.NotifyCanExecuteChanged();
        ConfirmAcceptCommand.NotifyCanExecuteChanged();
    }

    private bool CanUnaccept() => SelectedFindings.Any(f => f.Issue!.Accepted is not null);

    [RelayCommand(CanExecute = nameof(CanUnaccept))]
    private void Unaccept() =>
        Editor?.RemoveImpedanceAcceptances([.. SelectedFindings.Select(f => f.Issue!.Accepted).OfType<TraceImpedanceAcceptance>()]);

    /// <summary>Acceptances that matched nothing in the last run (R-imp5-3c), each with a remove button.</summary>
    public ObservableCollection<ImpedanceStaleAcceptanceRow> StaleAcceptances { get; } = [];
    public bool HasStaleAcceptances => StaleAcceptances.Count > 0;

    // ── export ───────────────────────────────────────────────────────────────────────────────

    /// <summary>R-imp3-4a: disabled with no results; allowed on stale ones, which the PDF says are.</summary>
    public bool CanExport => HasResults && !IsRunning;

    /// <summary>The file name the save picker suggests.</summary>
    public string SuggestedPdfName => Editor?.CurrentLayoutPath is { Length: > 0 } p
        ? TraceImpedanceAnalysis.CellTitle(p) + " impedance.pdf" : "impedance.pdf";

    /// <summary>Writes the held report to <paramref name="pdfPath"/> — the view asks where.</summary>
    public Task<bool> ExportPdfAsync(string pdfPath) =>
        Editor is { } vm ? vm.ExportTraceImpedancePdfAsync(pdfPath) : Task.FromResult(false);

    private void RefreshCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ZoomToSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanExport));
    }
}

/// <summary>One copper layer the panel offers.</summary>
public sealed partial class ImpedanceLayerRow(LayoutEditorViewModel.TraceImpedanceLayerChoice choice, bool isChecked)
    : ObservableObject
{
    public LayoutEditorViewModel.TraceImpedanceLayerChoice Choice { get; } = choice;
    public string Name => Choice.Name;
    public bool HasCopper => Choice.HasCopper;
    public IBrush Swatch { get; } = new SolidColorBrush(Color.FromRgb(choice.Color.R, choice.Color.G, choice.Color.B));

    [ObservableProperty] private bool _isChecked = isChecked;
}

/// <summary>One layer's width classes, listed while the layer is ticked.</summary>
public sealed partial class ImpedanceWidthGroup(string layerName) : ObservableObject
{
    public string LayerName { get; } = layerName;
    public ObservableCollection<ImpedanceWidthRow> Rows { get; } = [];
    [ObservableProperty] private bool _isVisible = true;
}

/// <summary>
/// One width class a reviewer ticks: width · count · total length · typical Z0 — or, for a saved width
/// the artwork no longer has, the width struck through and "no traces at this width now" (R-imp2-3c).
/// </summary>
public sealed partial class ImpedanceWidthRow : ObservableObject
{
    public ImpedanceWidthRow(TraceWidthRow row, LayoutEditorViewModel vm)
    {
        Row = row;
        _isChecked = row.Ticked;
        if (row.Class is not { } c)
        {
            WidthText = vm.FormatMicrons(row.Selector.NominalMicrons);
            CountText = "";
            DetailText = "no traces at this width now";
            return;
        }
        WidthText = c.MaxMicrons - c.MinMicrons < 0.05
            ? vm.FormatMicrons(c.NominalMicrons)
            : $"{vm.FormatMicrons(c.MinMicrons)}–{vm.FormatMicrons(c.MaxMicrons)}";
        CountText = c.TraceCount == 1 ? "1 trace" : $"{c.TraceCount} traces";
        DetailText = $"{vm.FormatMicrons(c.TotalLengthMicrons)} · typical Z₀ " +
                     (c.TypicalZ0 is { } z ? z.ToString("0.0", CultureInfo.InvariantCulture) + " Ω" : "—");
    }

    public TraceWidthRow Row { get; }
    public bool Missing => Row.Missing;
    public double WidthOpacity => Missing ? 0.6 : 1;
    public TextDecorationCollection? WidthDecorations => Missing ? TextDecorations.Strikethrough : null;
    public string WidthText { get; }
    public string CountText { get; }
    public string DetailText { get; }

    [ObservableProperty] private bool _isChecked;
}

/// <summary>One row of the results list: a trace, or one of its findings or notes.</summary>
public abstract class ImpedanceResultRow : ObservableObject
{
    protected ImpedanceResultRow(TraceRun trace) => Trace = trace;
    public TraceRun Trace { get; }

    /// <summary>The finding this row is, or null for a trace row or a note.</summary>
    public virtual TraceIssue? Issue => null;
}

/// <summary>
/// A trace: id, layer, verdict, width, Z0 min–max, % in band, type — every string the PDF's table
/// prints, through the report's own formatters (R-imp3-2c).
/// </summary>
public sealed partial class ImpedanceTraceResultRow : ImpedanceResultRow
{
    public ImpedanceTraceResultRow(TraceImpedanceReport report, TraceLayerResult layer, TraceRun trace) : base(trace)
    {
        LayerName = layer.Name;
        VerdictText = TraceImpedanceReport.VerdictText(trace.Verdict);
        WidthText = $"{report.WidthText(trace)} {report.Unit}";
        Z0Text = $"{TraceImpedanceReport.OhmsText(trace.Z0Min)}–{TraceImpedanceReport.OhmsText(trace.Z0Max)} Ω";
        InBandText = $"{TraceImpedanceReport.InToleranceText(trace)} in band";
        Children =
        [
            .. trace.Issues.Select(i => (ImpedanceResultRow)new ImpedanceFindingResultRow(trace, i)),
            .. trace.Notes.Select(n => (ImpedanceResultRow)new ImpedanceNoteResultRow(trace, n)),
        ];
    }

    public string Id => Trace.Id;
    public string LayerName { get; }
    public string VerdictText { get; }
    public string WidthText { get; }
    public string Z0Text { get; }
    public string InBandText { get; }
    public string TypeText => Trace.TypeSummary;
    public bool IsPass => Trace.Verdict == TraceVerdict.Pass;
    public bool IsWarning => Trace.Verdict == TraceVerdict.Warning;
    public bool IsFail => Trace.Verdict is TraceVerdict.Fail or TraceVerdict.Unsolved;

    public IReadOnlyList<ImpedanceResultRow> Children { get; }
    public bool HasChildren => Children.Count > 0;

    /// <summary>"3 findings" — what expanding shows.</summary>
    public string ChildCountText => (Trace.Issues.Count switch
    {
        0 => Trace.Notes.Count == 0 ? "" : "notes",
        1 => "1 finding",
        int n => $"{n} findings",
    }) + (Trace.AcceptedCount > 0 ? $" ({Trace.AcceptedCount} accepted)" : "");

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>One finding, with its severity, in the sentence the PDF's findings list prints — and, when it
/// is accepted, marked so with the reason (R-imp5-2b): an accepted finding is never removed.</summary>
public sealed class ImpedanceFindingResultRow(TraceRun trace, TraceIssue issue) : ImpedanceResultRow(trace)
{
    public override TraceIssue? Issue => issue;
    public string SeverityText => TraceImpedanceReport.SeverityText(issue);
    public bool IsAccepted => issue.Accepted is not null;
    public bool Fails => issue.Fails && !IsAccepted;
    public bool Warns => !issue.Fails && !IsAccepted;
    public double TextOpacity => IsAccepted ? 0.6 : 1;
    public string Text => issue.Text;

    /// <summary>"ACCEPTED — reason · 2026-09-26", or empty.</summary>
    public string AcceptedText => issue.Accepted is { } a
        ? $"ACCEPTED — {a.Reason} · {a.AcceptedUtc.ToLocalTime():yyyy-MM-dd}" : "";
}

/// <summary>A saved acceptance that matched nothing in the last run (R-imp5-3c): its layer, the finding's
/// text when it was accepted, its reason, and a remove button.</summary>
public sealed partial class ImpedanceStaleAcceptanceRow(TraceImpedanceAcceptance acceptance, Action<ImpedanceStaleAcceptanceRow> remove)
{
    public TraceImpedanceAcceptance Acceptance { get; } = acceptance;
    public string Text { get; } = $"{acceptance.LayerName}: {acceptance.Summary}";
    public string ReasonText { get; } = $"Reason: {acceptance.Reason} · accepted {acceptance.AcceptedUtc.ToLocalTime():yyyy-MM-dd}";

    [RelayCommand]
    private void Remove() => remove(this);
}

/// <summary>One note on a trace — said about it, not a fault.</summary>
public sealed class ImpedanceNoteResultRow(TraceRun trace, string note) : ImpedanceResultRow(trace)
{
    public string Text { get; } = note;
}

/// <summary>One region in the Scope list: its name (editable) and its shape.</summary>
public sealed partial class ImpedanceRegionRow : ObservableObject
{
    private readonly ImpedancePanelViewModel _panel;

    public ImpedanceRegionRow(ImpedancePanelViewModel panel, TraceScopeRegion region, int index)
    {
        _panel = panel;
        _name = region.Name ?? "";
        Placeholder = $"Region {index + 1}";
        ShapeText = region.VertexCount == 4 && IsAxisAligned(region) ? "rectangle" : $"lasso, {region.VertexCount} vertices";
    }

    public string Placeholder { get; }
    public string ShapeText { get; }

    [RelayCommand]
    private void Delete() => _panel.DeleteRegion(this);

    [ObservableProperty] private string _name;
    partial void OnNameChanged(string value) => _panel.RenameRegion(this, value);

    private static bool IsAxisAligned(TraceScopeRegion r)
    {
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            if (r.Xy[2 * i] != r.Xy[2 * j] && r.Xy[2 * i + 1] != r.Xy[2 * j + 1]) return false;
        }
        return true;
    }
}

/// <summary>One pick: its layer, point and extent — and "no copper here now" when the last run found
/// none under it (R-imp4-2d).</summary>
public sealed partial class ImpedancePickRow(TracePick pick, bool missing, string pointText, Action<ImpedancePickRow> delete)
{
    [RelayCommand]
    private void Delete() => delete(this);

    public TracePick Pick { get; } = pick;
    public string Text { get; } = $"{pick.LayerName} {pointText}" +
                                  (pick.Extent == TracePickExtent.Connected ? " · connected" : "");
    public bool Missing { get; } = missing;
}

/// <summary>One net the artwork carries, ticked when it is in the scope; a saved net the artwork no
/// longer carries is kept, ticked, and said to be missing.</summary>
public sealed partial class ImpedanceNetRow(string name, bool isChecked, bool missing) : ObservableObject
{
    public string Name { get; } = name;
    public bool Missing { get; } = missing;
    [ObservableProperty] private bool _isChecked = isChecked;
}
