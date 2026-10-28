// The Review panel (brief-review-6): the surface a reviewer works in, on the Impedance panel's terms — it
// follows the ACTIVE layout, holds no result of its own (the result lives on the layout's view model,
// LayoutEditorViewModel.Review.cs) and computes nothing: every count, sentence and measured value it shows
// is LayoutReview.Run's.
//
// Top to bottom: the checks (enable, thresholds inline in the layout's display unit, a Reset; the impedance
// row read-only with a button to the Impedance panel), Run / Cancel and the per-check progress line, and the
// results as a tree flattened into one virtualised list — check (its count, or not run and why) → layer →
// finding. Selecting a finding zooms the canvas to its marker; selecting copper on the canvas selects the
// findings its markers touch. Accept… asks for a reason, as the Impedance panel's does.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Layout.Review;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Layout.Review;

public sealed partial class ReviewPanelViewModel : ObservableObject
{
    // ── the layout ───────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private LayoutEditorViewModel? _editor;

    public bool IsLayoutActive => Editor is not null;

    /// <summary>Opens the Impedance panel on a layout — installed by the workspace (R-rev6-2's impedance row).</summary>
    public Action<LayoutEditorViewModel>? OpenImpedancePanel { get; set; }

    private CancellationTokenSource? _runCts;

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
            oldValue.SelectionChanged -= OnCanvasSelectionChanged;
            oldValue.ShowReviewMarkers = false;
        }
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditorPropertyChanged;
            newValue.SelectionChanged += OnCanvasSelectionChanged;
            newValue.ShowReviewMarkers = IsShown;
        }
        LoadChecks();
        RebuildResults();
        OnPropertyChanged(nameof(IsLayoutActive));
        RefreshCommands();
    }

    private bool _isShown;

    /// <summary>Set by the view as it is attached and detached: markers draw only while the panel is open (R-rev6-5).</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown == value) return;
            _isShown = value;
            if (Editor is { } vm) vm.ShowReviewMarkers = value;
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LayoutEditorViewModel.ReviewResult):
                RebuildResults();
                break;
            case nameof(LayoutEditorViewModel.IsReviewStale):
                OnPropertyChanged(nameof(IsStale));
                break;
            case nameof(LayoutEditorViewModel.DisplayUnit):
            case nameof(LayoutEditorViewModel.Technology):
                LoadChecks();
                break;
        }
    }

    // ── the checks and their thresholds (R-rev6-2) ───────────────────────────────────────────

    public ObservableCollection<ReviewCheckSettingRow> Checks { get; } = [];

    /// <summary>What the review reviews — a board or a die — saved on the layout. Only that target's checks are
    /// listed and run: a PCB check on a die would only say what a die lacks.</summary>
    public ReviewTarget Target => Editor?.ReviewSettings.TargetOrDefault ?? ReviewTarget.Pcb;

    /// <summary>The switch's PCB half.</summary>
    public bool IsPcb
    {
        get => Target == ReviewTarget.Pcb;
        set { if (value) SetTarget(ReviewTarget.Pcb); else KeepSwitch(); }
    }

    /// <summary>The switch's MMIC half.</summary>
    public bool IsMmic
    {
        get => Target == ReviewTarget.Mmic;
        set { if (value) SetTarget(ReviewTarget.Mmic); else KeepSwitch(); }
    }

    /// <summary>A click on the half already chosen unchecks that toggle; the switch always has one half on, so the
    /// toggles are told again after the click has finished.</summary>
    private void KeepSwitch() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(IsPcb));
        OnPropertyChanged(nameof(IsMmic));
    });

    internal void SetTarget(ReviewTarget target)
    {
        if (Editor is { } vm && target != Target)
            vm.EditReviewSettings(s => s.Target = target == ReviewTarget.Pcb ? null : target);
        LoadChecks();
        OnPropertyChanged(nameof(ResultTargetText));
        OnPropertyChanged(nameof(HasResultTargetText));
    }

    private void LoadChecks()
    {
        Checks.Clear();
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(IsPcb));
        OnPropertyChanged(nameof(IsMmic));
        if (Editor is not { } vm) return;
        var settings = vm.ReviewSettings;
        foreach (var def in LayoutReview.ChecksFor(settings.TargetOrDefault))
            Checks.Add(new ReviewCheckSettingRow(this, def, settings.IsEnabled(def.Id),
                                                 LayoutReview.SettingsText(def.Id, vm.Model)));
        foreach (var c in Checks) c.Refresh(settings, vm.Model.DisplayUnit, vm.Model.DbuPerMicron);
        RefreshCommands();
    }

    internal void SetEnabled(ReviewCheckSettingRow row, bool enabled)
    {
        Editor?.EditReviewSettings(s => s.SetEnabled(row.Def.Id, enabled));
        RefreshCommands();
    }

    /// <summary>Writes one threshold, stored units — only that one; its default removes it.</summary>
    internal void SetThreshold(ReviewCheckSettingRow check, ReviewThresholdDef threshold, double stored)
    {
        if (Editor is not { } vm) return;
        vm.EditReviewSettings(s => s.Set(check.Def, threshold, stored, vm.Model.DbuPerMicron));
        check.Refresh(vm.ReviewSettings, vm.Model.DisplayUnit, vm.Model.DbuPerMicron, keepText: true);
    }

    /// <summary>Every threshold of a check back to its default.</summary>
    internal void Reset(ReviewCheckSettingRow check)
    {
        if (Editor is not { } vm) return;
        vm.EditReviewSettings(s =>
        {
            foreach (var t in check.Def.Thresholds) s.Set(check.Def, t, t.DefaultStored(vm.Model.DbuPerMicron), vm.Model.DbuPerMicron);
        });
        check.Refresh(vm.ReviewSettings, vm.Model.DisplayUnit, vm.Model.DbuPerMicron);
    }

    internal void OpenImpedance()
    {
        if (Editor is { } vm) OpenImpedancePanel?.Invoke(vm);
    }

    // ── the run (R-rev6-3) ───────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _runCheckText = "";
    [ObservableProperty] private string _runCountText = "";
    [ObservableProperty] private string _runStageText = "";
    [ObservableProperty] private double _runProgress;

    partial void OnIsRunningChanged(bool value) => RefreshCommands();

    private bool CanRun() => IsLayoutActive && !IsRunning && Checks.Any(c => c.IsEnabled);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        if (Editor is not { } vm || IsRunning) return;
        var cts = _runCts = new CancellationTokenSource();
        IsRunning = true;
        RunCheckText = "Reading the board…";
        RunCountText = RunStageText = "";
        RunProgress = 0;
        // Progress<T> is created on the UI thread, so every report arrives on it.
        var control = new RunControl { Token = cts.Token, MinReportIntervalMs = 60, Progress = new Progress<RunProgress>(OnProgress) };
        try { await vm.RunReviewAsync(control); }
        finally
        {
            IsRunning = false;
            _runCts = null;
            cts.Dispose();
        }
    }

    private bool CanCancel() => IsRunning;

    /// <summary>Stops the run; the checks that finished are kept.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_runCts is not { IsCancellationRequested: false } cts) return;
        cts.Cancel();
        RunStageText = "Cancelling — keeping the checks that finished…";
    }

    // LayoutReview.Run's stage, "Reviewing: <id> (i of n)", and a check's own, "Reviewing: <id> — <stage>".
    private static readonly Regex CheckStage = new(@"^Reviewing: (?<id>[\w-]+) \((?<i>\d+) of (?<n>\d+)\)$");
    private static readonly Regex SubStage = new(@"^Reviewing: (?<id>[\w-]+) — (?<what>.*)$");
    private int _runIndex, _runCount;

    internal void OnProgress(RunProgress p)
    {
        if (!IsRunning || _runCts is not { IsCancellationRequested: false }) return;
        if (CheckStage.Match(p.Stage) is { Success: true } m)
        {
            _runIndex = int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture);
            _runCount = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            RunCheckText = LayoutReview.FindCheck(m.Groups["id"].Value)?.Title ?? m.Groups["id"].Value;
            RunCountText = $"Check {_runIndex} of {_runCount}";
            RunStageText = "";
            RunProgress = (_runIndex - 1.0) / Math.Max(1, _runCount);
        }
        else if (SubStage.Match(p.Stage) is { Success: true } s)
        {
            RunStageText = s.Groups["what"].Value;
            double within = p.StageTotal > 0 ? (double)p.StageCompleted / p.StageTotal : 0;
            RunProgress = (_runIndex - 1.0 + within) / Math.Max(1, _runCount);
        }
        else if (p.Stage.Length > 0) RunCheckText = p.Stage;
    }

    // ── the results (R-rev6-4) ───────────────────────────────────────────────────────────────

    /// <summary>The tree, flattened: a check row, then — expanded — its layer rows, each followed — expanded — by
    /// its findings. One list so it virtualises however many findings a check has.</summary>
    public ReviewRowCollection Rows { get; } = [];

    public LayoutReviewResult? Result => Editor?.ReviewResult;
    public bool HasResults => Result is not null;
    public bool IsStale => Editor?.IsReviewStale == true;
    public string StaleText => "The layout has changed since this review. Run again to review the artwork as it is now.";

    /// <summary>Said when the result held is the other kind of review than the switch now shows.</summary>
    public string ResultTargetText => Result is { } r && r.Target != Target
        ? $"These results are a {Name(r.Target)} review. Run to review as {Name(Target)}." : "";

    public bool HasResultTargetText => ResultTargetText.Length > 0;

    private static string Name(ReviewTarget t) => t == ReviewTarget.Mmic ? "MMIC" : "PCB";

    /// <summary>"3 checks with findings (41 findings, 6 accepted), 1 not run." — the Messages line's counts.</summary>
    public string SummaryText
    {
        get
        {
            if (Result is not { } r) return "";
            string v = ReviewRunReport.Verdict(r).Replace(" — see the Review panel.", ".");
            v = v.StartsWith("Review: ", StringComparison.Ordinal) ? v["Review: ".Length..] : v;
            return v.Length > 0 ? char.ToUpperInvariant(v[0]) + v[1..] : v;
        }
    }

    /// <summary>Show the accepted findings (R-rev6-6). Hidden, they are still counted on their check's row.</summary>
    [ObservableProperty] private bool _showAccepted = true;
    partial void OnShowAcceptedChanged(bool value) => ApplyRows();

    // Which groups the reviewer had open, kept across a re-applied acceptance and a re-run.
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>A layer group with more findings than this opens collapsed — its count is the first thing to read.</summary>
    internal const int OpenLayerAtMost = 100;

    internal bool IsOpen(string groupKey, bool byDefault) =>
        byDefault ? !_collapsed.Contains(groupKey) : _expanded.Contains(groupKey);

    internal void SetOpen(string groupKey, bool byDefault, bool open)
    {
        if (open == byDefault) { _collapsed.Remove(groupKey); _expanded.Remove(groupKey); }
        else (byDefault ? _collapsed : _expanded).Add(groupKey);
        ApplyRows();
    }

    private void RebuildResults()
    {
        CancelAccept();
        StaleAcceptances.Clear();
        foreach (var a in Result?.StaleAcceptances ?? [])
            StaleAcceptances.Add(new ReviewStaleAcceptanceRow(a, row => Editor?.RemoveReviewAcceptances([row.Acceptance])));
        OnPropertyChanged(nameof(HasStaleAcceptances));
        ApplyRows();
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ResultTargetText));
        OnPropertyChanged(nameof(HasResultTargetText));
        RefreshCommands();
    }

    private void ApplyRows()
    {
        var keepKey = (SelectedRow as ReviewFindingResultRow)?.Finding.Key;
        var rows = new List<ReviewResultRow>();
        if (Result is { } r)
        {
            var byCheck = r.Findings.Where(f => f.Severity == ReviewSeverity.Warning)
                                    .GroupBy(f => f.CheckId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var check in r.Checks)
            {
                var findings = byCheck.GetValueOrDefault(check.Id) ?? [];
                var row = new ReviewCheckResultRow(this, check);
                rows.Add(row);
                if (!row.IsExpanded) continue;
                foreach (var layer in findings.GroupBy(f => f.LayerName ?? ""))
                {
                    var all = layer.ToList();
                    var shown = ShowAccepted ? all : all.Where(f => !f.IsAccepted).ToList();
                    if (shown.Count == 0) continue;
                    var layerRow = new ReviewLayerResultRow(this, check.Id, layer.Key, all.Count, all.Count(f => f.IsAccepted));
                    rows.Add(layerRow);
                    if (layerRow.IsExpanded) rows.AddRange(shown.Select(f => new ReviewFindingResultRow(f)));
                }
            }
        }
        _rowsReplacing = true;
        try { Rows.ReplaceAll(rows); }
        finally { _rowsReplacing = false; }
        SelectedRow = keepKey is null ? null : Rows.OfType<ReviewFindingResultRow>().FirstOrDefault(f => f.Finding.Key == keepKey);
        SetSelectedRows(_selectedRows.OfType<ReviewFindingResultRow>()
            .Select(s => Rows.OfType<ReviewFindingResultRow>().FirstOrDefault(f => f.Finding.Key == s.Finding.Key))
            .OfType<ReviewResultRow>().ToList());
    }

    private bool _rowsReplacing;

    // ── selection and cross-probing (R-rev6-5) ───────────────────────────────────────────────

    [ObservableProperty] private ReviewResultRow? _selectedRow;

    // Set while the canvas's selection is being shown in the list, so it does not zoom the canvas back.
    private bool _probingFromCanvas;

    partial void OnSelectedRowChanged(ReviewResultRow? value)
    {
        if (_rowsReplacing) return;
        var finding = (value as ReviewFindingResultRow)?.Finding;
        if (Editor is not { } vm) return;
        vm.SelectedReviewFinding = finding;
        if (finding is not null && !_probingFromCanvas) vm.ZoomToReviewFinding(finding);
    }

    /// <summary>Raised to put several rows in the list's selection — the canvas's copper selected the findings
    /// its markers touch. The view applies it; it comes back through <see cref="SetSelectedRows"/>.</summary>
    public event Action<IReadOnlyList<ReviewResultRow>>? SelectRowsRequested;

    private void OnCanvasSelectionChanged()
    {
        if (!IsShown || Editor is not { } vm || vm.ReviewResult is null) return;
        var touched = vm.ReviewFindingsTouchingSelection();
        if (touched.Count == 0) return;
        // Open the groups they are in, so the rows exist to select.
        foreach (var f in touched)
        {
            SetOpenQuiet(ReviewCheckResultRow.GroupKey(f.CheckId), true, true);
            int inLayer = vm.ReviewResult.Findings.Count(x => x.CheckId == f.CheckId && x.LayerName == f.LayerName);
            SetOpenQuiet(ReviewLayerResultRow.GroupKey(f.CheckId, f.LayerName ?? ""), inLayer <= OpenLayerAtMost, true);
        }
        if (!ShowAccepted && touched.Any(f => f.IsAccepted)) ShowAccepted = true;
        ApplyRows();
        var keys = touched.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var rows = Rows.OfType<ReviewFindingResultRow>().Where(f => keys.Contains(f.Finding.Key)).Cast<ReviewResultRow>().ToList();
        _probingFromCanvas = true;
        try
        {
            SelectedRow = rows.FirstOrDefault();
            SetSelectedRows(rows);
            SelectRowsRequested?.Invoke(rows);
        }
        finally { _probingFromCanvas = false; }
    }

    private void SetOpenQuiet(string key, bool byDefault, bool open)
    {
        if (open == byDefault) { _collapsed.Remove(key); _expanded.Remove(key); }
        else (byDefault ? _collapsed : _expanded).Add(key);
    }

    // ── accepting (R-rev6-6) ─────────────────────────────────────────────────────────────────

    private IReadOnlyList<ReviewResultRow> _selectedRows = [];
    private List<ReviewFinding> _accepting = [];

    /// <summary>Every selected row — the list selects several, so one reason can accept several findings.</summary>
    public void SetSelectedRows(IReadOnlyList<ReviewResultRow> rows)
    {
        _selectedRows = rows;
        BeginAcceptCommand.NotifyCanExecuteChanged();
        UnacceptCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<ReviewFinding> SelectedFindings => _selectedRows.OfType<ReviewFindingResultRow>().Select(r => r.Finding);

    [ObservableProperty] private bool _isAccepting;

    /// <summary>Required: an empty reason is refused, not saved.</summary>
    [ObservableProperty] private string _acceptReason = "";

    partial void OnAcceptReasonChanged(string value)
    {
        ConfirmAcceptCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(AcceptReasonMissing));
    }

    public bool AcceptReasonMissing => string.IsNullOrWhiteSpace(AcceptReason);

    /// <summary>"Accept 2 findings of Via in pad" — what the reason is for.</summary>
    [ObservableProperty] private string _acceptTargetText = "";

    /// <summary>Said when an impedance finding is among those being accepted: where its acceptance is kept.</summary>
    [ObservableProperty] private string _acceptNoteText = "";

    private bool CanBeginAccept() => !IsAccepting && SelectedFindings.Any(f => !f.IsAccepted);

    [RelayCommand(CanExecute = nameof(CanBeginAccept))]
    private void BeginAccept()
    {
        _accepting = [.. SelectedFindings.Where(f => !f.IsAccepted)];
        if (_accepting.Count == 0) return;
        var titles = _accepting.Select(f => LayoutReview.FindCheck(f.CheckId)?.Title ?? f.CheckId).Distinct().ToList();
        AcceptTargetText = $"Accept {(_accepting.Count == 1 ? "1 finding" : $"{_accepting.Count} findings")} of {string.Join(", ", titles)}";
        AcceptNoteText = _accepting.Any(LayoutReview.IsAcceptedElsewhere)
            ? "An impedance finding is accepted in the Impedance review: the Impedance panel shows it accepted too." : "";
        AcceptReason = "";
        IsAccepting = true;
        BeginAcceptCommand.NotifyCanExecuteChanged();
    }

    private bool CanConfirmAccept() => IsAccepting && !AcceptReasonMissing;

    [RelayCommand(CanExecute = nameof(CanConfirmAccept))]
    private void ConfirmAccept()
    {
        if (!CanConfirmAccept() || Editor is not { } vm) return;
        var findings = _accepting;
        string reason = AcceptReason;
        CancelAccept();
        vm.AcceptReviewFindings(findings, reason);
    }

    [RelayCommand]
    private void CancelAccept()
    {
        _accepting = [];
        IsAccepting = false;
        AcceptReason = "";
        AcceptNoteText = "";
        BeginAcceptCommand.NotifyCanExecuteChanged();
        ConfirmAcceptCommand.NotifyCanExecuteChanged();
    }

    private bool CanUnaccept() => SelectedFindings.Any(f => f.IsAccepted);

    [RelayCommand(CanExecute = nameof(CanUnaccept))]
    private void Unaccept() => Editor?.UnacceptReviewFindings([.. SelectedFindings]);

    /// <summary>Acceptances that matched nothing in this run, each with a Remove.</summary>
    public ObservableCollection<ReviewStaleAcceptanceRow> StaleAcceptances { get; } = [];
    public bool HasStaleAcceptances => StaleAcceptances.Count > 0;

    private void RefreshCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One check in the checks list: enable, title, thresholds inline, a Reset; the impedance row's
/// settings read-only with a button to the Impedance panel.</summary>
public sealed partial class ReviewCheckSettingRow : ObservableObject
{
    private readonly ReviewPanelViewModel _panel;
    private bool _loading;

    public ReviewCheckSettingRow(ReviewPanelViewModel panel, ReviewCheckDef def, bool enabled, string? settingsText)
    {
        _panel = panel;
        Def = def;
        _isEnabled = enabled;
        SettingsText = settingsText ?? "";
        Thresholds = [.. def.Thresholds.Select(t => new ReviewThresholdRow(this, t))];
    }

    public ReviewCheckDef Def { get; }
    public string Title => Def.Title;
    public string Description => Def.Description;
    public IReadOnlyList<ReviewThresholdRow> Thresholds { get; }
    public bool HasThresholds => Thresholds.Count > 0;

    /// <summary>A check whose settings are another panel's (impedance): shown read-only.</summary>
    public bool IsElsewhere => Def.SettingsOwner is not null;
    public string SettingsText { get; }

    [ObservableProperty] private bool _isEnabled;
    partial void OnIsEnabledChanged(bool value) { if (!_loading) _panel.SetEnabled(this, value); }

    /// <summary>A threshold differs from its default — the row's Reset is offered.</summary>
    [ObservableProperty] private bool _isModified;

    [RelayCommand]
    private void Reset() => _panel.Reset(this);

    [RelayCommand]
    private void OpenImpedance() => _panel.OpenImpedance();

    internal void Edited(ReviewThresholdRow row, double stored) => _panel.SetThreshold(this, row.Def, stored);

    internal void Refresh(ReviewSettings settings, CircuitRF.Design.Layout.LayoutUnit unit, int dbuPerMicron, bool keepText = false)
    {
        _loading = true;
        try
        {
            IsEnabled = settings.IsEnabled(Def.Id);
            foreach (var t in Thresholds) t.Refresh(settings.Value(Def, t.Def, dbuPerMicron), unit, dbuPerMicron, keepText);
            IsModified = Thresholds.Any(t => t.IsModified);
        }
        finally { _loading = false; }
    }
}

/// <summary>One threshold: an editable value in the layout's display unit, its unit beside it, marked when it is
/// not the default.</summary>
public sealed partial class ReviewThresholdRow(ReviewCheckSettingRow check, ReviewThresholdDef def) : ObservableObject
{
    private bool _loading;
    private CircuitRF.Design.Layout.LayoutUnit _unit = CircuitRF.Design.Layout.LayoutUnit.Um;
    private int _dbuPerMicron = CircuitRF.Design.Layout.LayoutUnits.DefaultDbuPerMicron;

    public ReviewThresholdDef Def { get; } = def;
    public string Title => Def.Title;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _unitText = "";
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private bool _isInvalid;

    /// <summary>"Default 0.25 mm" — the tooltip, and what Reset returns to.</summary>
    [ObservableProperty] private string _defaultText = "";

    partial void OnTextChanged(string value)
    {
        if (_loading) return;
        IsInvalid = !TryParse(value, out double stored);
        if (!IsInvalid) check.Edited(this, stored);
    }

    /// <summary>A length in the layout's unit (a typed unit is honoured); an area in that unit squared (likewise),
    /// stored in mm²; any other kind a plain number. At least 0.</summary>
    internal bool TryParse(string text, out double stored)
    {
        stored = 0;
        text = (text ?? "").Trim().Replace(',', '.');
        if (Def.Kind == ReviewThresholdKind.Length)
        {
            if (!CircuitRF.Design.Layout.LayoutUnits.TryParse(text, _unit, _dbuPerMicron, out long dbu) || dbu < 0) return false;
            stored = dbu;
            return true;
        }
        if (Def.Kind == ReviewThresholdKind.Area)
            return ReviewText.TryParseArea(text, _unit, out stored) && stored >= 0;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out stored) && stored >= 0 && double.IsFinite(stored);
    }

    internal void Refresh(double stored, CircuitRF.Design.Layout.LayoutUnit unit, int dbuPerMicron, bool keepText)
    {
        _unit = unit;
        _dbuPerMicron = dbuPerMicron;
        _loading = true;
        try
        {
            if (!keepText) Text = Format(stored);
            UnitText = Def.Kind switch
            {
                ReviewThresholdKind.Length  => CircuitRF.Design.Layout.LayoutUnits.Suffix(unit),
                ReviewThresholdKind.Area    => CircuitRF.Design.Layout.LayoutUnits.Suffix(unit) + "²",
                ReviewThresholdKind.Heights => "× h",
                _                           => "",
            };
            double def = Def.DefaultStored(dbuPerMicron);
            IsModified = stored != def;
            IsInvalid = false;
            DefaultText = $"Default {ReviewSettings.Display(Def, def, unit, dbuPerMicron)}";
        }
        finally { _loading = false; }
    }

    private string Format(double stored) => Def.Kind switch
    {
        ReviewThresholdKind.Length => CircuitRF.Design.Layout.LayoutUnits.Format((long)Math.Round(stored), _unit, _dbuPerMicron, 4),
        ReviewThresholdKind.Area   => ReviewText.AreaNumber(stored, _unit),
        _                          => stored.ToString("0.###", CultureInfo.InvariantCulture),
    };
}

/// <summary>One row of the results list.</summary>
public abstract class ReviewResultRow : ObservableObject;

/// <summary>The results list, replaced in ONE reset rather than one notification per row: a check with
/// thousands of findings must not post thousands of changes to the list it fills.</summary>
public sealed class ReviewRowCollection : ObservableCollection<ReviewResultRow>
{
    public void ReplaceAll(IEnumerable<ReviewResultRow> rows)
    {
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>A check: its title and count — "12 findings, 3 accepted", "No findings", or not run and why.</summary>
public sealed partial class ReviewCheckResultRow : ReviewResultRow
{
    private readonly ReviewPanelViewModel _panel;

    public ReviewCheckResultRow(ReviewPanelViewModel panel, ReviewCheckRow check)
    {
        _panel = panel;
        Check = check;
        IsExpanded = panel.IsOpen(GroupKey(check.Id), byDefault: true);
    }

    public static string GroupKey(string checkId) => checkId;

    public ReviewCheckRow Check { get; }
    public string Title => Check.Title;
    public bool IsNotRun => Check.State == ReviewCheckState.NotRun;
    public bool IsCancelled => Check.State == ReviewCheckState.Cancelled;
    public bool HasChildren => !IsNotRun && Check.FindingCount > 0;

    public string CountText => Check.State switch
    {
        ReviewCheckState.NotRun => "not run",
        ReviewCheckState.Cancelled when Check.FindingCount == 0 => "cancelled",
        _ when Check.FindingCount == 0 => "No findings",
        _ => (Check.FindingCount == 1 ? "1 finding" : $"{Check.FindingCount:N0} findings")
             + (Check.AcceptedCount > 0 ? $", {Check.AcceptedCount:N0} accepted" : "")
             + (Check.State == ReviewCheckState.Cancelled ? " (cancelled part-way)" : ""),
    };

    public bool HasFindings => !IsNotRun && Check.FindingCount - Check.AcceptedCount > 0;

    /// <summary>The not-run sentence, or the check's summary line (impedance: its scope and how many passed).</summary>
    public string DetailText => Check.NotRunReason ?? Check.Summary ?? "";
    public bool HasDetail => DetailText.Length > 0;

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => _panel.SetOpen(GroupKey(Check.Id), true, !IsExpanded);
}

/// <summary>One layer's findings under a check: the layer, its count and accepted count. A finding of the board
/// rather than of a layer (an edge-stitching gap) is under "Board".</summary>
public sealed partial class ReviewLayerResultRow : ReviewResultRow
{
    private readonly ReviewPanelViewModel _panel;
    private readonly string _checkId;
    private readonly bool _byDefault;

    public ReviewLayerResultRow(ReviewPanelViewModel panel, string checkId, string layerName, int count, int accepted)
    {
        _panel = panel;
        _checkId = checkId;
        LayerName = layerName;
        _byDefault = count <= ReviewPanelViewModel.OpenLayerAtMost;
        IsExpanded = panel.IsOpen(GroupKey(checkId, layerName), _byDefault);
        CountText = (count == 1 ? "1 finding" : $"{count:N0} findings") + (accepted > 0 ? $", {accepted:N0} accepted" : "");
    }

    public static string GroupKey(string checkId, string layerName) => $"{checkId}|{layerName}";

    public string LayerName { get; }
    public string Title => LayerName.Length > 0 ? LayerName : "Board";
    public string CountText { get; }

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => _panel.SetOpen(GroupKey(_checkId, LayerName), _byDefault, !IsExpanded);
}

/// <summary>One finding: its sentence and measured value, and — accepted — the reason.</summary>
public sealed class ReviewFindingResultRow(ReviewFinding finding) : ReviewResultRow
{
    public ReviewFinding Finding { get; } = finding;
    public string Sentence => Finding.Sentence;
    public string MeasuredText => Finding.MeasuredText ?? "";
    public bool HasMeasured => MeasuredText.Length > 0;
    public bool IsAccepted => Finding.IsAccepted;
    public double TextOpacity => IsAccepted ? 0.6 : 1;

    /// <summary>"ACCEPTED — reason · 2026-10-28", or empty.</summary>
    public string AcceptedText => Finding.Accepted is { } a
        ? $"ACCEPTED — {a.Reason} · {a.AcceptedUtc.ToLocalTime():yyyy-MM-dd}" : "";
}

/// <summary>A saved acceptance that matched nothing in this run: its layer and the finding's sentence when it was
/// accepted, its reason, and a Remove.</summary>
public sealed partial class ReviewStaleAcceptanceRow(ReviewAcceptance acceptance, Action<ReviewStaleAcceptanceRow> remove)
{
    public ReviewAcceptance Acceptance { get; } = acceptance;
    public string Text { get; } = (acceptance.LayerName.Length > 0 ? $"{acceptance.LayerName}: " : "") + acceptance.Summary;
    public string ReasonText { get; } = $"Reason: {acceptance.Reason} · accepted {acceptance.AcceptedUtc.ToLocalTime():yyyy-MM-dd}";

    [RelayCommand]
    private void Remove() => remove(this);
}
