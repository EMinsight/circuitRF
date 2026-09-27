using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The layout editor's Impedance Analysis (round 8): target Z0, ± tolerance, ± warning band, an
/// optional highest frequency and the copper layers,
/// then Export — a save picker, then the run with a per-layer progress bar and Cancel. A cancelled
/// run still writes the report for the layers it finished (owner, 2026-09-25).
/// </summary>
/// <remarks>
/// The window only asks and reports. The run is <see cref="LayoutEditorViewModel.ExportTraceImpedanceAsync"/>,
/// which is <see cref="TraceImpedanceAnalysis"/> and the PDF composer `circuitrf impedance` also calls.
/// The settings, the layers and the trace widths ticked are SAVED ON THE LAYOUT when the window closes
/// (brief-impedance-2 R-imp2-3), so a re-run — here or through `circuitrf impedance` — reviews what
/// this one reviewed. The survey and the scope are <see cref="TraceImpedanceAnalysis.Survey(IReadOnlyList{LayoutShape},
/// Technology, int, TraceImpedanceOptions, RunControl?)"/> and <see cref="TraceWidthRows"/>, in
/// src/Design: this window only lays them out.
/// </remarks>
public partial class TraceImpedanceAnalysisDialog : Window
{
    private readonly LayoutEditorViewModel? _vm;
    private readonly List<(LayoutEditorViewModel.TraceImpedanceLayerChoice Choice, CheckBox Box)> _layers = [];
    private CancellationTokenSource? _cts;
    private bool _running;

    // The TRACES section: the survey's rows, each with its box (null for a row on a layer this window
    // does not list, which is kept as it was), and one group per layer, shown while the layer is ticked.
    private readonly List<(TraceWidthRow Row, CheckBox? Box)> _rows = [];
    private readonly Dictionary<string, Control> _groups = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _surveyCts;
    private bool _surveyed;

    public TraceImpedanceAnalysisDialog()
    {
        InitializeComponent();
    }

    public TraceImpedanceAnalysisDialog(LayoutEditorViewModel vm) : this()
    {
        _vm = vm;
        var saved = vm.SavedImpedanceReview ?? new TraceImpedanceReview();
        TargetBox.Text = saved.TargetOhms.ToString("0.##", CultureInfo.InvariantCulture);
        ToleranceBox.Text = saved.TolerancePercent.ToString("0.##", CultureInfo.InvariantCulture);
        WarningBox.Text = saved.WarningPercent.ToString("0.##", CultureInfo.InvariantCulture);
        FrequencyBox.Text = saved.MaxFrequencyHz is { } hz ? TraceImpedanceReport.Hz(hz) : "";

        foreach (var choice in vm.TraceImpedanceLayers())
        {
            var swatch = new Border
            {
                Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(3),
                Background = new SolidColorBrush(Color.FromRgb(choice.Color.R, choice.Color.G, choice.Color.B)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock { Text = choice.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, label } };
            if (!choice.HasCopper)
                content.Children.Add(new TextBlock { Text = "no copper", FontSize = 10, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            var box = new CheckBox
            {
                Content = content,
                IsChecked = choice.HasCopper && (saved.Layers is null ||
                                                 saved.Layers.Contains(choice.Name, StringComparer.OrdinalIgnoreCase)),
                IsEnabled = choice.HasCopper,
                MinHeight = 26,
            };
            box.IsCheckedChanged += (_, _) => { Validate(); ShowGroups(); };
            LayerList.Children.Add(box);
            _layers.Add((choice, box));
        }
        if (_layers.Count == 0)
            LayerList.Children.Add(new TextBlock
            {
                Text = "This layout's technology binds no drawing layer to a conductor of its stackup.",
                FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
        Validate();
    }

    // ── the trace widths ─────────────────────────────────────────────────────────────────────

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = SurveyAsync();
    }

    /// <summary>The survey, in the background; closing the window cancels it.</summary>
    private async Task SurveyAsync()
    {
        if (_vm is null) return;
        _surveyCts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token = _surveyCts.Token,
            MinReportIntervalMs = 60,
            Progress = new Progress<RunProgress>(p =>
            {
                if (!_surveyed && p.Stage.Length > 0) SurveyText.Text = $"Surveying the trace widths — {p.Stage}…";
            }),
        };
        TraceWidthSurvey? survey;
        try { survey = await _vm.SurveyTraceWidthsAsync(control); }
        catch (OperationCanceledException) { return; }
        finally { _surveyCts.Dispose(); _surveyCts = null; }

        if (survey is null || survey.Refusal is not null)
        {
            SurveyBar.IsVisible = false;
            SurveyText.Text = survey?.Refusal ?? "This layout has no technology, so there are no traces to survey.";
            return;
        }
        BuildRows(survey);
    }

    private void BuildRows(TraceWidthSurvey survey)
    {
        _surveyed = true;
        SurveyProgress.IsVisible = false;
        TraceList.Children.Clear();
        _rows.Clear();
        _groups.Clear();

        var rows = TraceWidthRows.Merge(survey, _vm?.SavedImpedanceReview?.Scope);
        foreach (var (choice, _) in _layers)
        {
            var mine = rows.Where(r => string.Equals(r.LayerName, choice.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0 || !choice.HasCopper) continue;

            var group = new StackPanel { Spacing = 1 };
            group.Children.Add(new TextBlock { Text = choice.Name, FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.8 });
            foreach (var row in mine)
            {
                var box = new CheckBox { Content = RowContent(row), IsChecked = row.Ticked, MinHeight = 22 };
                group.Children.Add(box);
                _rows.Add((row, box));
            }
            TraceList.Children.Add(group);
            _groups[choice.Name] = group;
        }
        // A row on a layer this window does not list is kept as it is, unseen, never dropped.
        foreach (var row in rows)
            if (!_rows.Any(r => ReferenceEquals(r.Row, row))) _rows.Add((row, null));

        if (_groups.Count == 0)
            TraceList.Children.Add(new TextBlock
            {
                Text = "No traces were found on the copper layers.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
        ShowGroups();
    }

    /// <summary>width · count · total length · typical Z0 — or, for a saved width the artwork no longer
    /// has, the width struck through and "no traces at this width now" (R-imp2-3c).</summary>
    private Control RowContent(TraceWidthRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,70,*"), VerticalAlignment = VerticalAlignment.Center };
        void Cell(int col, string text, double opacity = 1, TextDecorationCollection? deco = null) =>
            grid.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12, Opacity = opacity, TextDecorations = deco,
                VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = col,
            });

        if (row.Class is not { } c)
        {
            Cell(0, _vm!.FormatMicrons(row.Selector.NominalMicrons), 0.6, TextDecorations.Strikethrough);
            Cell(1, "", 0.6);
            Cell(2, "no traces at this width now", 0.6);
            return grid;
        }
        string width = c.MaxMicrons - c.MinMicrons < 0.05
            ? _vm!.FormatMicrons(c.NominalMicrons)
            : $"{_vm!.FormatMicrons(c.MinMicrons)}–{_vm.FormatMicrons(c.MaxMicrons)}";
        Cell(0, width);
        Cell(1, c.TraceCount == 1 ? "1 trace" : $"{c.TraceCount} traces", 0.75);
        Cell(2, $"{_vm.FormatMicrons(c.TotalLengthMicrons)} · typical Z₀ " +
                (c.TypicalZ0 is { } z ? z.ToString("0.0", CultureInfo.InvariantCulture) + " Ω" : "—"), 0.75);
        return grid;
    }

    /// <summary>A layer's widths are listed while the layer is ticked.</summary>
    private void ShowGroups()
    {
        foreach (var (choice, box) in _layers)
            if (_groups.TryGetValue(choice.Name, out var group)) group.IsVisible = box.IsChecked == true;
    }

    /// <summary>
    /// The review this window says: the settings, the layers ticked (null when every layer with copper
    /// is), and the widths ticked — or, when the survey has not finished, the saved scope untouched.
    /// </summary>
    private TraceImpedanceReview CurrentReview()
    {
        TryNumber(TargetBox.Text, out double target);
        TryNumber(ToleranceBox.Text, out double tolerance);
        TryNumber(WarningBox.Text, out double warning);
        TryFrequency(FrequencyBox.Text, out double? maxFrequency);
        var enabled = _layers.Where(l => l.Choice.HasCopper).ToList();
        var scope = _surveyed
            ? TraceWidthRows.ScopeOf(_rows.Where(r => r.Box is null ? r.Row.Ticked : r.Box.IsChecked == true).Select(r => r.Row))
            : _vm?.SavedImpedanceReview?.Scope?.Clone();
        return new TraceImpedanceReview
        {
            TargetOhms = target,
            TolerancePercent = tolerance,
            WarningPercent = warning,
            MaxFrequencyHz = maxFrequency,
            Layers = enabled.All(l => l.Box.IsChecked == true) ? null
                   : [.. enabled.Where(l => l.Box.IsChecked == true).Select(l => l.Choice.Name)],
            Scope = scope is { IsEmpty: false } ? scope : null,
        };
    }

    // ── the inputs ───────────────────────────────────────────────────────────────────────────

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>The highest frequency, in Hz: null when the field is blank (the rule is off). A bare
    /// number is GHz, the unit a board's highest frequency is nearly always said in; a typed unit is
    /// honoured.</summary>
    private static bool TryFrequency(string? text, out double? hz)
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

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => Validate();

    /// <summary>The pass band, and whether Export can go.</summary>
    private bool Validate()
    {
        if (BandText is null || ExportButton is null) return false;
        string? problem = null;
        if (!TryNumber(TargetBox.Text, out double target) || !(target > 0))
            problem = "Enter the target impedance in ohms, e.g. 50.";
        else if (!TryNumber(ToleranceBox.Text, out double tol) || !(tol > 0) || tol >= 100)
            problem = "Enter the tolerance as a percentage above 0 and below 100, e.g. 10.";
        else if (!TryNumber(WarningBox.Text, out double warn) || !(warn > tol) || warn >= 100)
            problem = string.Create(CultureInfo.InvariantCulture,
                $"Enter the warning band as a percentage wider than the tolerance (± {tol:0.##} %) and below 100, e.g. 20.");
        else if (!TryFrequency(FrequencyBox.Text, out _))
            problem = "Enter the highest frequency with its unit, e.g. 6 GHz, or leave it blank.";
        else
        {
            BandText.Text = string.Create(CultureInfo.InvariantCulture,
                $"Pass {target * (1 - tol / 100):0.0}–{target * (1 + tol / 100):0.0} Ω · " +
                $"Warning {target * (1 - warn / 100):0.0}–{target * (1 + warn / 100):0.0} Ω");
            if (!_layers.Any(l => l.Box.IsChecked == true)) problem = "Tick at least one layer.";
        }

        ValidationText.Text = problem ?? "";
        ValidationText.IsVisible = problem is not null;
        ExportButton.IsEnabled = problem is null && !_running;
        return problem is null;
    }

    private void OnAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var (choice, box) in _layers) if (choice.HasCopper) box.IsChecked = true;
    }

    private void OnNoneClick(object? sender, RoutedEventArgs e)
    {
        foreach (var (_, box) in _layers) box.IsChecked = false;
    }

    // ── the run ──────────────────────────────────────────────────────────────────────────────

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _running || !Validate()) return;
        var review = CurrentReview();
        var chosen = _layers.Where(l => l.Box.IsChecked == true).ToList();

        string suggested = _vm.CurrentLayoutPath is { Length: > 0 } p
            ? TraceImpedanceAnalysis.CellTitle(p) + " impedance.pdf" : "impedance.pdf";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Impedance Analysis",
            SuggestedFileName = suggested,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF report") { Patterns = ["*.pdf"] }],
        });
        if (file is null) return;

        _surveyCts?.Cancel();
        _running = true;
        _cts = new CancellationTokenSource();
        TargetCard.IsEnabled = false;
        LayersCard.IsEnabled = false;
        TracesCard.IsEnabled = false;
        ExportButton.IsEnabled = false;
        ProgressCard.IsVisible = true;
        FootText.Text = "Cancel stops the run and still writes the layers that finished.";
        LayerText.Text = "Reading the copper…";
        LayerCountText.Text = "";
        StageText.Text = "";
        LayerBar.Value = 0;
        OverallBar.Value = 0;

        // Progress<T> is created on the UI thread, so every report arrives on it.
        var control = new RunControl
        {
            Token = _cts.Token,
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

        try
        {
            await _vm.ExportTraceImpedanceAsync(file.Path.LocalPath, options, control);
        }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
        }
        Close(true);
    }

    private static readonly Regex StageShape = new(@"^(?<layer>.*) \((?<i>\d+) of (?<n>\d+)\): (?<what>.*)$");

    private void OnProgress(RunProgress p)
    {
        if (!_running) return;
        var m = StageShape.Match(p.Stage);
        if (!m.Success)
        {
            LayerText.Text = p.Stage.Length > 0 ? p.Stage + "…" : LayerText.Text;
            return;
        }
        int i = int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture);
        int n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        string what = m.Groups["what"].Value;
        double stage = what == "solving" && p.StageTotal > 0 ? (double)p.StageCompleted / p.StageTotal
                     : what == "cutting" ? 0.05 : 0;

        LayerText.Text = m.Groups["layer"].Value;
        LayerCountText.Text = $"Layer {i} of {n}";
        LayerBar.Value = stage;
        OverallBar.Value = (i - 1 + stage) / Math.Max(1, n);
        StageText.Text = what == "solving" && p.StageTotal > 0
            ? $"Solving cross-sections — {p.StageCompleted:N0} of {p.StageTotal:N0}"
            : char.ToUpperInvariant(what[0]) + what[1..] + "…";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_running && _cts is { } cts)
        {
            if (cts.IsCancellationRequested) return;
            cts.Cancel();
            CancelButton.IsEnabled = false;
            StageText.Text = "Cancelling — writing the report for the layers that finished…";
            return;
        }
        Close(false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing the window mid-run is a Cancel: the run finishes its current point, writes what it
        // has, and the dialog closes itself when that is done.
        if (_running)
        {
            e.Cancel = true;
            OnCancelClick(this, new RoutedEventArgs());
        }
        else
        {
            // Closing — after Export or on Cancel — saves the review on the layout (R-imp2-4a); a
            // field that does not parse keeps what was saved rather than writing a half-typed value.
            _surveyCts?.Cancel();
            if (_vm is not null && Validate()) _vm.SaveImpedanceReview(CurrentReview());
        }
        base.OnClosing(e);
    }
}
