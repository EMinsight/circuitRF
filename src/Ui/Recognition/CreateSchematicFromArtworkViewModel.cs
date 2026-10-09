// Design ▸ Create Schematic from Artwork… — the dialog's state. brief-artsch-8-gui-command.md R-as8-2 … R-as8-4;
// overview D2, D4, D10, D11, D15, D16.
//
// ── IT DECIDES NOTHING ───────────────────────────────────────────────────────────────────────────────
//
// Every option here is a field of a record the CLI fills from its flags (RecognitionOptions, RecognitionScope, the
// sweep RecognitionSweep composes, RecognitionTarget), and both surfaces hand them to the same function through
// IArtworkRecognitionRunner. A rule that lived only in this file would be a rule `circuitrf recognize` does not apply.
//
// ── EDITS SURVIVE A RE-RUN ───────────────────────────────────────────────────────────────────────────
//
// Recognition re-runs (debounced, cancellable) whenever the scope, an option or a companion file changes, and the
// parts table is rebuilt from what comes back. A table edit is therefore never stored on a row: it is kept as the
// override a parts CSV would carry (R-as4-8), written as CSV text, and laid over every later recognition by the
// recognition's own CSV reader — the contract the CLI's --parts reads.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Recognition;

/// <summary>One report class, one line (R-as8-2's report strip): its sentence and, where it has any, its anchors.</summary>
public sealed partial class RecognitionReportLineViewModel : ObservableObject
{
    public RecognitionReportLineViewModel(RecognitionFinding finding, IReadOnlyList<string> anchorTexts)
    {
        Finding = finding;
        AnchorTexts = anchorTexts;
    }

    public RecognitionFinding Finding { get; }
    public string Text => Finding.Sentence;
    public int Count => Finding.Count;
    public IReadOnlyList<string> AnchorTexts { get; }
    public bool HasAnchors => AnchorTexts.Count > 0;
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>The dialog.</summary>
public sealed partial class CreateSchematicFromArtworkViewModel : ObservableObject
{
    private readonly RecognitionInput _base;
    private readonly IArtworkRecognitionRunner _runner;
    private readonly Action<Action> _post;
    private readonly TimeSpan _debounce;
    private readonly IReadOnlyList<long[]> _selection;
    private readonly FrequencySpec _basis;
    private readonly Dictionary<string, Dictionary<string, string>> _edits = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PartsTableRowViewModel> _rows = [];
    private CancellationTokenSource? _cts;
    private PartsTable _table = PartsTable.Empty;
    private IReadOnlyList<string>? _modelFiles;
    private bool _constructing = true;

    /// <param name="baseInput">The layout as the CLI reads it (<see cref="IArtworkRecognitionRunner.Load"/>); its
    /// <see cref="RecognitionInput.ClayPath"/> must be set — a scratch layout never reaches here.</param>
    /// <param name="selectionRings">The layout editor's selection outline, DBU; empty when nothing is selected.</param>
    /// <param name="runner">The recognition; the real one when null.</param>
    /// <param name="post">How a result from the background reaches the UI thread; immediate when null.</param>
    /// <param name="debounce">How long a burst of changes is let settle before recognising.</param>
    public CreateSchematicFromArtworkViewModel(
        RecognitionInput baseInput, IReadOnlyList<long[]> selectionRings,
        IArtworkRecognitionRunner? runner = null, Action<Action>? post = null, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(baseInput);
        _base = baseInput;
        ClayPath = Path.GetFullPath(baseInput.ClayPath ?? throw new ArgumentException("The layout must be saved.", nameof(baseInput)));
        _runner = runner ?? ArtworkRecognitionRunner.Instance;
        _post = post ?? (a => a());
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _selection = selectionRings ?? [];

        ArtworkCellName = Path.GetFileName(RecognitionTarget.CellOf(ClayPath) ?? Path.GetFileNameWithoutExtension(ClayPath));
        ArtworkCellOffered = RecognitionTarget.ArtworkCellOffered(ClayPath);
        SelectionAvailable = RecognitionScope.Polygons(_selection) is { IsWhole: false };

        _basis = RecognitionSweep.Basis(baseInput.EmSetup);
        _newCellName = RecognitionTarget.DefaultCellName(ClayPath);
        _scopeSelection = SelectionAvailable;
        _startText = $"{_basis.StartExpr} {_basis.StartUnit}";
        _stopText = $"{_basis.StopExpr} {_basis.StopUnit}";
        _pointsText = (_basis.NumPoints ?? RecognitionEmitOptions.DefaultSweep.NumPoints!.Value).ToString(CultureInfo.InvariantCulture);
        _coplanarFactorText = RecognitionOptions.DefaultCoplanarGapFactor.ToString(CultureInfo.InvariantCulture);
        RefreshTarget();
        _constructing = false;
        ScheduleRecognition();
    }

    // ── the artwork ──────────────────────────────────────────────────────────────────────────────────

    public string ClayPath { get; }
    public string ArtworkCellName { get; }
    public string Title => $"Create Schematic from Artwork — {ArtworkCellName}";

    // ── target (R-as8-2, D4) ─────────────────────────────────────────────────────────────────────────

    /// <summary>Whether <i>This cell's schematic</i> is offered: the artwork cell has no schematic view (D4).</summary>
    public bool ArtworkCellOffered { get; }

    [ObservableProperty] private bool _targetArtworkCell;
    [ObservableProperty] private string _newCellName;
    [ObservableProperty] private string _nameError = "";
    [ObservableProperty] private bool _isReplace;

    public bool TargetNewCell
    {
        get => !TargetArtworkCell;
        set => TargetArtworkCell = !value;
    }

    /// <summary>Create, or Replace when the name names a cell this command wrote (R-as8-2).</summary>
    public string CreateButtonText => IsReplace ? "Replace" : "Create";

    /// <summary>The Replace button's flyout.</summary>
    public string ReplaceConfirmText => $"Replaces {NewCellName.Trim()}'s schematic; a checkpoint is taken first.";

    partial void OnTargetArtworkCellChanged(bool value)
    {
        OnPropertyChanged(nameof(TargetNewCell));
        RefreshTarget();
    }

    partial void OnNewCellNameChanged(string value) => RefreshTarget();

    partial void OnIsReplaceChanged(bool value) => OnPropertyChanged(nameof(CreateButtonText));

    /// <summary>Live: <see cref="NameValidator"/>, and whether the name is a cell this command may replace.</summary>
    private void RefreshTarget()
    {
        OnPropertyChanged(nameof(ReplaceConfirmText));
        if (TargetArtworkCell) { NameError = ""; IsReplace = false; CreateCommand.NotifyCanExecuteChanged(); return; }
        string name = NewCellName.Trim();
        string dir = RecognitionTarget.NewCellDir(ClayPath, name);
        bool exists = name.Length > 0 && (Directory.Exists(dir) || File.Exists(dir));
        IsReplace = exists && NameValidator.Validate(name) is null && RecognitionTarget.IsReplaceable(dir);
        NameError = NameValidator.Validate(name) is { } bad ? bad
                  : exists && !IsReplace ? $"A cell named '{name}' already exists and was not created from artwork; choose another name."
                  : "";
        CreateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The target as the run takes it.</summary>
    public RecognitionTarget Target =>
        TargetArtworkCell ? RecognitionTarget.ArtworkCell
        : IsReplace ? RecognitionTarget.Replace(RecognitionTarget.NewCellDir(ClayPath, NewCellName.Trim()))
        : RecognitionTarget.NewCell(NewCellName.Trim());

    // ── scope (D11) ──────────────────────────────────────────────────────────────────────────────────

    public bool SelectionAvailable { get; }
    [ObservableProperty] private bool _scopeSelection;

    public bool ScopeWhole
    {
        get => !ScopeSelection;
        set => ScopeSelection = !value;
    }

    partial void OnScopeSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(ScopeWhole));
        ScheduleRecognition();
    }

    // ── options ──────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<string> GroundOptions { get; } = ["Auto", "Pick on layout"];
    public static IReadOnlyList<string> ViaOptions { get; } = ["Model as VIAGND", "Plain GND"];
    public static IReadOnlyList<string> CoplanarOptions { get; } = ["Auto", "Microstrip", "GCPW"];

    [ObservableProperty] private int _groundIndex;
    [ObservableProperty] private (long X, long Y)? _groundPoint;
    [ObservableProperty] private int _viaIndex;
    [ObservableProperty] private int _coplanarIndex;
    [ObservableProperty] private string _coplanarFactorText;
    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _stopText;
    [ObservableProperty] private string _pointsText;

    /// <summary>Set by the window: arms the layout editor's point pick and hands the point back (Ground ▸ Pick on layout).</summary>
    public Action<Action<long, long>>? PickGroundRequested { get; set; }

    /// <summary>Where the picked ground is, as the layout displays it; empty until one is picked.</summary>
    public string GroundPointText => GroundPoint is { } p
        ? $"({_table.Format.Length(p.X)}, {_table.Format.Length(p.Y)})" : "";

    partial void OnGroundIndexChanged(int value)
    {
        if (value == 1 && GroundPoint is null) PickGround();
        else ScheduleRecognition();
    }

    partial void OnGroundPointChanged((long X, long Y)? value)
    {
        OnPropertyChanged(nameof(GroundPointText));
        ScheduleRecognition();
    }

    [RelayCommand]
    private void PickGround() => PickGroundRequested?.Invoke((x, y) => _post(() =>
    {
        GroundPoint = (x, y);
        if (GroundIndex != 1) GroundIndex = 1;
    }));

    partial void OnViaIndexChanged(int value) => ScheduleRecognition();
    partial void OnCoplanarIndexChanged(int value) => ScheduleRecognition();
    partial void OnCoplanarFactorTextChanged(string value) => ScheduleRecognition();
    partial void OnStartTextChanged(string value) => ScheduleRecognition();
    partial void OnStopTextChanged(string value) => ScheduleRecognition();
    partial void OnPointsTextChanged(string value) => ScheduleRecognition();

    // ── companion files ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The three origins, as railRF's import dialog lists and spells them — the same control (R-as8-2).</summary>
    public static IReadOnlyList<(PlacementOrigin Origin, string Label)> PlacementOrigins { get; } =
    [
        (PlacementOrigin.SymbolOrigin, "the footprint's symbol origin"),
        (PlacementOrigin.BodyCentre,   "the part body's centre"),
        (PlacementOrigin.PinOne,       "pin 1"),
    ];

    public static IReadOnlyList<string> PlacementOriginLabels { get; } = [.. PlacementOrigins.Select(o => o.Label)];

    [ObservableProperty] private string _bomPath = "";
    [ObservableProperty] private string _placementPath = "";

    /// <summary>The origin choice: −1 is NOTHING CHOSEN, which is where it starts — never a guess (railRF's rule).</summary>
    [ObservableProperty] private int _placementOriginIndex = -1;

    [ObservableProperty] private bool _placementNeedsOrigin;
    [ObservableProperty] private string _companionError = "";

    private BomTable? _bom;
    private PlacementTable? _placement;

    /// <summary>Set by the window: a file picker for the BOM… and Placement… buttons.</summary>
    public Func<string, Task<string?>>? PickFileAsync { get; set; }

    [RelayCommand]
    private async Task PickBom()
    {
        if (PickFileAsync is { } pick && await pick("Bill of materials") is { } path) BomPath = path;
    }

    [RelayCommand]
    private async Task PickPlacement()
    {
        if (PickFileAsync is { } pick && await pick("Placement file") is { } path) PlacementPath = path;
    }

    partial void OnBomPathChanged(string value)
    {
        _bom = value.Trim().Length == 0 ? null : BomFile.ReadFile(value.Trim());
        RefreshCompanionError();
        ScheduleRecognition();
    }

    partial void OnPlacementPathChanged(string value)
    {
        PlacementOriginIndex = -1;
        ReadPlacement();
        ScheduleRecognition();
    }

    partial void OnPlacementOriginIndexChanged(int value)
    {
        ReadPlacement();
        ScheduleRecognition();
    }

    private void ReadPlacement()
    {
        string path = PlacementPath.Trim();
        PlacementOrigin? origin = PlacementOriginIndex >= 0 && PlacementOriginIndex < PlacementOrigins.Count
            ? PlacementOrigins[PlacementOriginIndex].Origin : null;
        // Whether the FILE states its origin, read with none given — so the choice stays on screen once made.
        var stated = path.Length == 0 ? null : PlacementFile.ReadFile(path, _base.View.DbuPerMicron);
        PlacementNeedsOrigin = stated is { OriginEvidence: PlacementOriginEvidence.Unstated };
        _placement = PlacementNeedsOrigin && origin is not null
            ? PlacementFile.ReadFile(path, _base.View.DbuPerMicron, origin)
            : stated;
        RefreshCompanionError();
    }

    private void RefreshCompanionError()
    {
        var errors = new List<string>();
        if (BomPath.Trim().Length > 0 && _bom is null) errors.Add($"'{Path.GetFileName(BomPath.Trim())}' could not be read.");
        if (PlacementPath.Trim().Length > 0 && _placement is null) errors.Add($"'{Path.GetFileName(PlacementPath.Trim())}' could not be read.");
        CompanionError = string.Join(" ", errors);
    }

    // ── the parts table (R-as8-3) ────────────────────────────────────────────────────────────────────

    /// <summary>The rows the filter and the sort leave, in order.</summary>
    public ObservableCollection<PartsTableRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _sortColumn = "Refdes";
    [ObservableProperty] private bool _sortAscending = true;
    [ObservableProperty] private PartsTableRowViewModel? _selectedRow;

    /// <summary>Set by the window: draws a part on the layout (rings and what to bring on screen), or clears it (null).</summary>
    public Action<IReadOnlyList<long[]>?, Bbox>? PartHighlighted { get; set; }

    /// <summary>Set by the window: a Touchstone file picker for the Model cell's Browse….</summary>
    public Func<Task<string?>>? BrowseModelAsync { get; set; }

    /// <summary>The recognised table with the held edits laid over it.</summary>
    public PartsTable Table => _table;

    partial void OnFilterTextChanged(string value) => RefreshRows();

    [RelayCommand]
    private void Sort(string column)
    {
        if (SortColumn == column) SortAscending = !SortAscending;
        else { SortColumn = column; SortAscending = true; }
        RefreshRows();
    }

    partial void OnSelectedRowChanged(PartsTableRowViewModel? value)
    {
        RefreshLearn();
        if (value is null) { PartHighlighted?.Invoke(null, Bbox.Empty); return; }
        var rings = ArtworkCrossProbe.PartRings(value.Row, _placement is { Refusal: null } p ? p : null);
        PartHighlighted?.Invoke(rings, ArtworkCrossProbe.Extent(rings));
    }

    private void RefreshRows()
    {
        string filter = FilterText.Trim();
        IEnumerable<PartsTableRowViewModel> rows = _rows.Where(r => r.Matches(filter));
        Comparison<PartsTableRowViewModel> by = SortColumn is "X" or "Y"
            ? (a, b) => (SortColumn == "X" ? a.Row.X : a.Row.Y).CompareTo(SortColumn == "X" ? b.Row.X : b.Row.Y)
            : (a, b) => PartsTable.NaturalCompare(a.SortKey(SortColumn), b.SortKey(SortColumn));
        var sorted = rows.ToList();
        sorted.Sort((a, b) => { int c = by(a, b); return SortAscending ? c : -c; });

        string? keep = SelectedRow?.BoardRefdes;
        Rows.Clear();
        foreach (var r in sorted) Rows.Add(r);
        var reselect = keep is null ? null : Rows.FirstOrDefault(r => string.Equals(r.BoardRefdes, keep, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(reselect, SelectedRow)) SelectedRow = reselect;
    }

    /// <summary>A cell edit, held as the CSV override it is (R-as8-2), under the designator the board gave the part.
    /// The Model cell's Browse… asks for a file first; a designator another part already has is refused.</summary>
    private void OnRowEdited(PartsTableRowViewModel row, string column, string text)
    {
        string key = row.BoardRefdes;
        if (column == "Model")
        {
            if (text == PartsTableRowViewModel.BrowseModel) { _ = BrowseModelFor(row); return; }
            if (text == "Ideal") { SetEdit(key, "Model", "Ideal"); SetEdit(key, "ModelFile", ""); }
            else { SetEdit(key, "Model", "SnP"); SetEdit(key, "ModelFile", text); }
            return;
        }
        if (column == "Refdes")
        {
            string name = text.Trim();
            if (name.Length == 0 || string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                if (_edits.TryGetValue(key, out var cells)) cells.Remove("Refdes");
                Status = "";
            }
            else if (_rows.Any(r => !ReferenceEquals(r, row) && (string.Equals(r.Refdes, name, StringComparison.OrdinalIgnoreCase)
                                                             || string.Equals(r.RefdesText.Trim(), name, StringComparison.OrdinalIgnoreCase))))
                Status = $"{name} is another part's designator; {row.Refdes} was not renamed.";
            else
            {
                SetEdit(key, "Refdes", name);
                Status = "";
            }
            RefreshLearn();
            return;
        }
        SetEdit(key, column, text);
    }

    // ── Learn these glyphs (brief-artsch-10 R-as10-5) ─────────────────────────────────────────────

    private bool CanLearnGlyphs() => SelectedRow is { } row && SilkscreenText.Lesson(row.Corrected) is not null;

    /// <summary>Whether Learn These Glyphs is offered: the selected part's designator came from the silkscreen and has
    /// been corrected.</summary>
    public bool CanLearn => CanLearnGlyphs();

    private void RefreshLearn()
    {
        LearnGlyphsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanLearn));
    }

    /// <summary>
    /// The selected row's corrected designator, taught: each silkscreen glyph the correction says is another character
    /// is kept as a template of that character in the per-user state directory and used on every later recognition.
    /// Nothing is written to the workspace.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLearnGlyphs))]
    private void LearnGlyphs()
    {
        if (SelectedRow is not { } row || SilkscreenText.Lesson(row.Corrected) is not { } lesson) return;
        try
        {
            int added = GlyphTemplates.Learn(AppDataRoot.SubDir(GlyphTemplates.TaughtFolder), lesson);
            Status = added == 0
                ? $"Those glyphs were already learned."
                : $"Learned {string.Join(", ", lesson.Select(l => $"'{l.Char}'"))} from {row.RefdesText.Trim()}; recognising again.";
            ScheduleRecognition();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The glyphs could not be saved: {ex.Message}";
        }
    }

    private async Task BrowseModelFor(PartsTableRowViewModel row)
    {
        if (BrowseModelAsync is { } browse && await browse() is { Length: > 0 } file)
        {
            SetEdit(row.BoardRefdes, "Model", "SnP");
            SetEdit(row.BoardRefdes, "ModelFile", _table.StoredPath(Path.GetFullPath(file)));
        }
        ScheduleRecognition();   // re-reads the row, so the cell shows what was applied rather than "Browse…"
    }

    private void SetEdit(string refdes, string column, string text)
    {
        if (!_edits.TryGetValue(refdes, out var cells)) _edits[refdes] = cells = new(StringComparer.Ordinal);
        cells[column] = text;
    }

    /// <summary>
    /// The held edits as the parts CSV that carries them — null when there are none. Only edited rows are written, and
    /// on each the cells the user did not touch are written as the table already has them, so nothing but the edit
    /// changes (an empty Value would CLEAR the value — the CSV's own rule). X and Y are written so a corrected
    /// designator finds its part (the CSV's rename rule).
    /// </summary>
    public string? PartsCsvText
    {
        get
        {
            if (_edits.Count == 0) return null;
            string[] columns = ["Refdes", "Kind", "Value", "Model", "ModelFile", "X", "Y"];
            var sb = new StringBuilder(string.Join(",", columns)).Append('\n');
            foreach (var (key, cells) in _edits.OrderBy(e => e.Key, PartsTable.NaturalOrder))
            {
                var row = _table.Rows.FirstOrDefault(r => string.Equals(r.BoardRefdes, key, StringComparison.OrdinalIgnoreCase));
                string Current(string column) => row is null ? "" : PartsTableCsv.Cell(_table, row, column);
                string refdes = cells.TryGetValue("Refdes", out var r) ? r : row?.Refdes ?? key;
                string kind = cells.TryGetValue("Kind", out var k) ? k : Current("Kind");
                string value = cells.TryGetValue("Value", out var v) ? v
                             : row is not null && SameDimension(row, kind) ? Current("Value") : "";
                string model = cells.TryGetValue("Model", out var m) ? m : Current("Model");
                string file = cells.TryGetValue("ModelFile", out var f) ? f : Current("ModelFile");
                sb.Append(string.Join(",", new[] { refdes, kind, value, model, file, Current("X"), Current("Y") }.Select(Escape))).Append('\n');
            }
            return sb.ToString();
        }
    }

    private static bool SameDimension(PartRow row, string kindText)
    {
        var kind = PartsTable.ParseKind(kindText) ?? row.Kind;
        return (kind == PartKind.Unknown ? PartKind.C : kind) == row.GeneratedKind;
    }

    private static string Escape(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;

    /// <summary>Set by the window: a save picker (Export Parts…) and an open picker (Import Parts…), CSV.</summary>
    public Func<Task<string?>>? PickExportPathAsync { get; set; }
    public Func<Task<string?>>? PickImportPathAsync { get; set; }

    [RelayCommand]
    private async Task ExportParts()
    {
        if (PickExportPathAsync is not { } pick || await pick() is not { Length: > 0 } path) return;
        try
        {
            PartsTableCsv.WriteFile(path, _table);
            Status = $"Wrote {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The parts table could not be written: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportParts()
    {
        if (PickImportPathAsync is not { } pick || await pick() is not { Length: > 0 } path) return;
        Import(path);
    }

    /// <summary>
    /// Import Parts…: every editable cell the file changes becomes a held edit, read by the CSV reader itself — a
    /// refused file changes nothing and says why.
    /// </summary>
    public void Import(string path)
    {
        var read = PartsTableCsv.ReadFile(path, _table);
        if (read.Refusal is { } why) { Status = why; return; }
        foreach (var edited in read.Table!.Rows)
        {
            string key = edited.BoardRefdes;
            if (_table.Rows.FirstOrDefault(r => string.Equals(r.BoardRefdes, key, StringComparison.OrdinalIgnoreCase)) is not { } was) continue;
            if (!string.Equals(edited.Refdes, was.Refdes, StringComparison.Ordinal)) SetEdit(key, "Refdes", edited.Refdes);
            foreach (string column in new[] { "Kind", "Value", "Model", "ModelFile" })
            {
                string after = PartsTableCsv.Cell(read.Table, edited, column);
                if (after != PartsTableCsv.Cell(_table, was, column)) SetEdit(key, column, after);
            }
        }
        var said = read.Notes.Concat(read.NotOnBoard.Count > 0 ? [$"Not on the board: {string.Join(", ", read.NotOnBoard)}."] : []);
        Status = string.Join(" ", said);
        ScheduleRecognition();
    }

    // ── the report strip ─────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<RecognitionReportLineViewModel> ReportLines { get; } = [];

    /// <summary>The last recognition's report, for Messages after a Create.</summary>
    public RecognitionReport? LastReport { get; private set; }

    // ── recognition: debounced, cancellable ─────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isRecognising;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _progressText = "";

    /// <summary>The recognition in flight (or the last one) — what a test awaits.</summary>
    public Task Recognition { get; private set; } = Task.CompletedTask;

    /// <summary>Why recognition is waiting for the user, or null. An unstated placement origin is asked, never guessed.</summary>
    public string? Blocked
    {
        get
        {
            if (PlacementNeedsOrigin && PlacementOriginIndex < 0)
                return $"'{Path.GetFileName(PlacementPath.Trim())}' does not state its coordinate origin; choose it to recognise.";
            if (CompanionError.Length > 0) return CompanionError;
            if (GroundIndex == 1 && GroundPoint is null) return "Click the ground copper on the layout (Pick…), or set Ground to Auto.";
            if (RecognitionSweep.Parse(StartText) is null) return "Start needs a frequency with its unit (100 MHz).";
            if (RecognitionSweep.Parse(StopText) is null) return "Stop needs a frequency with its unit (6 GHz).";
            if (!int.TryParse(PointsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 2)
                return "Points needs a whole number, at least 2.";
            if (CoplanarIndex == 0 && !(double.TryParse(CoplanarFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out double k) && k > 0))
                return "The coplanar factor needs a positive number of substrate heights.";
            return null;
        }
    }

    /// <summary>Starts a recognition after the debounce, cancelling any in flight.</summary>
    public void ScheduleRecognition()
    {
        if (_constructing) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        CreateCommand.NotifyCanExecuteChanged();
        if (Blocked is { } why)
        {
            IsRecognising = false;
            Status = why;
            Recognition = Task.CompletedTask;
            return;
        }
        var input = BuildInput();
        var emit = BuildEmit();
        IsRecognising = true;
        Status = "";
        Recognition = Recognise(input, emit, cts);
    }

    private async Task Recognise(RecognitionInput input, RecognitionEmitOptions emit, CancellationTokenSource cts)
    {
        try
        {
            if (_debounce > TimeSpan.Zero) await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);
            var control = new RunControl
            {
                Token = cts.Token,
                Progress = new Progress<RunProgress>(p => _post(() => { if (!cts.IsCancellationRequested) ProgressText = p.Stage; })),
            };
            var (result, _) = await Task.Run(() => _runner.Preview(input, emit, control), cts.Token).ConfigureAwait(false);
            _post(() => { if (!cts.IsCancellationRequested) Apply(result); });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _post(() => { if (!cts.IsCancellationRequested) { IsRecognising = false; Status = $"Recognition failed: {ex.Message}"; } });
        }
    }

    private void Apply(RecognitionResult result)
    {
        IsRecognising = false;
        ProgressText = "";
        _table = result.Parts;
        LastReport = result.Report;
        _modelFiles ??= ModelFiles(_table.BaseDirectory);

        _rows.Clear();
        foreach (var row in _table.Rows) _rows.Add(new PartsTableRowViewModel(_table, row, _modelFiles, OnRowEdited));
        RefreshRows();

        ReportLines.Clear();
        foreach (var f in result.Report.Findings)
            ReportLines.Add(new RecognitionReportLineViewModel(f, [.. f.Anchors.Select(a => AnchorText(_table, a))]));

        Status = result.Refusal ?? "";
        OnPropertyChanged(nameof(GroundPointText));
        CreateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>An anchor as the layout displays coordinates.</summary>
    public static string AnchorText(PartsTable table, RecognitionAnchor a) =>
        $"({table.Format.Length(a.X)}, {table.Format.Length(a.Y)})" + (a.Layer is { } l ? $" on {l.Layer}/{l.Datatype}" : "");

    private static IReadOnlyList<string> ModelFiles(string? root) =>
        root is null ? [] : [.. new PartModelResolver(root).TwoPortFiles().Select(f =>
        {
            string rel = Path.GetRelativePath(root, f);
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? f : rel.Replace('\\', '/');
        })];

    /// <summary>The input the run is handed — the CLI's, field for field (D2).</summary>
    public RecognitionInput BuildInput()
    {
        var options = _base.Options with
        {
            Vias = ViaIndex == 1 ? ViaPolicy.Ground : ViaPolicy.Model,
            Coplanar = CoplanarIndex switch { 1 => CoplanarReading.Microstrip, 2 => CoplanarReading.Gcpw, _ => CoplanarReading.Auto },
            CoplanarGapFactor = double.TryParse(CoplanarFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out double k) && k > 0
                ? k : RecognitionOptions.DefaultCoplanarGapFactor,
            GroundAt = GroundIndex == 1 ? GroundPoint : null,
            TopFrequencyHz = StatedStop() is { } stop ? stop.Hz : null,
        };
        return _base with
        {
            Scope = ScopeSelection && SelectionAvailable ? RecognitionScope.Polygons(_selection) : RecognitionScope.Whole,
            Options = options,
            Placement = _placement,
            Bom = _bom,
            PartsCsvText = PartsCsvText,
        };
    }

    /// <summary>The emit options: the sweep composed as the CLI composes its flags — a field left at the basis is unstated.</summary>
    public RecognitionEmitOptions BuildEmit()
    {
        var start = RecognitionSweep.Parse(StartText) is { } s && !(s.Text == _basis.StartExpr && s.Unit == _basis.StartUnit) ? s : (RecognitionFrequency?)null;
        int? npts = int.TryParse(PointsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    && n != (_basis.NumPoints ?? RecognitionEmitOptions.DefaultSweep.NumPoints) ? n : null;
        return new RecognitionEmitOptions { Sweep = RecognitionSweep.Compose(_base.EmSetup, start, StatedStop(), npts) };
    }

    private RecognitionFrequency? StatedStop() =>
        RecognitionSweep.Parse(StopText) is { } e && !(e.Text == _basis.StopExpr && e.Unit == _basis.StopUnit) ? e : null;

    // ── create (R-as8-4) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Raised on the UI thread when a schematic was written.</summary>
    public event Action<RecognitionRun>? Created;

    private bool CanCreate() => !IsCreating && Blocked is null && (TargetArtworkCell || NameError.Length == 0);

    [ObservableProperty] private bool _isCreating;

    partial void OnIsCreatingChanged(bool value) => CreateCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        _cts?.Cancel();
        IsRecognising = false;
        var input = BuildInput();
        var target = Target;
        var options = new RecognitionRunOptions { Emit = BuildEmit() };
        IsCreating = true;
        Status = TargetArtworkCell ? $"Writing {ArtworkCellName}'s schematic…" : $"Writing {NewCellName.Trim()}…";
        try
        {
            var run = await Task.Run(() => _runner.Run(input, target, options, null)).ConfigureAwait(false);
            _post(() =>
            {
                IsCreating = false;
                if (run.Refusal is { } why) { Status = why; return; }
                Status = "";
                LastReport = run.Report;
                Created?.Invoke(run);
            });
        }
        catch (Exception ex)
        {
            _post(() => { IsCreating = false; Status = $"The schematic could not be created: {ex.Message}"; });
        }
    }

    /// <summary>Closing the dialog: stops a recognition in flight and takes the part off the layout.</summary>
    public void Close()
    {
        _cts?.Cancel();
        PartHighlighted?.Invoke(null, Bbox.Empty);
    }
}
