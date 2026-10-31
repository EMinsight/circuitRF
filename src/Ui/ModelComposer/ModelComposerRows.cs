// The rows the Model Composer window lists (brief-modelcomposer-14 R-mc14-2, R-mc14-6, R-mc14-8, R-mc14-9): the step
// rail, the data sets, the network values and the passive proposals; and MC-15's method pickers, trap states, typed numbers,
// validation items and export symbols. Each is a projection of the document the view model
// rebuilds after an edit or a run; a settable cell hands its text back to the view model, which makes the edit — one undo
// step — so a row never holds a second copy of what the document says.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Design.Modeling;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace CircuitRF.Ui.ModelComposer;

/// <summary>
/// One row of the Data step's needs list (brief MC-30 D5): a kind of data, lit when the document's data sets hold it, with
/// what it gives the model — or, dim, what would light it. Derived on every refresh and never stored; not a button.
/// </summary>
public sealed record NeedRow(string Data, string Gives, bool Lit, string Missing)
{
    public string Detail => Lit ? Gives : Missing;
    public MaterialIconKind Icon => Lit ? MaterialIconKind.CheckCircleOutline : MaterialIconKind.CircleOutline;
    public double Opacity => Lit ? 1.0 : 0.5;
}

/// <summary>One step on the rail: its name, its glyph, whether it can be opened yet and why not, and a stale marker.</summary>
public sealed partial class ModelStepItem(string name, MaterialIconKind icon) : ObservableObject
{
    public string Name { get; } = name;
    public MaterialIconKind Icon { get; } = icon;

    [ObservableProperty] private bool _isEnabled = true;
    /// <summary>One sentence, shown as the tooltip of a disabled step; empty when the step can be opened.</summary>
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private bool _isSelected;
    /// <summary>The step has its result in the document (or, for Check and Validate, from this session) and is not stale.</summary>
    [ObservableProperty] private bool _isDone;

    public string Tip => !IsEnabled ? Reason : IsStale ? $"{Name}: stale, run it again" : IsDone ? $"{Name}: done" : Name;

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(Tip));
    partial void OnReasonChanged(string value) => OnPropertyChanged(nameof(Tip));
    partial void OnIsStaleChanged(bool value) => OnPropertyChanged(nameof(Tip));
    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(Tip));
}

/// <summary>One data set in the list (R-mc14-6). Read-only: a set's conditions are stated when it is imported.
/// <see cref="Capacitances"/> says where the charges read the set's capacitances (R-mc20-5), empty when it has none.</summary>
public sealed record DataSetRow(string Name, string Kind, string Ambient, string Plane, string Quiescent, string PrePulse, string Points,
                                string Capacitances = "")
{
    public bool HasCapacitances => Capacitances.Length > 0;

    /// <summary>The row's second line. A passive block has no quiescent point, so its row stops at the plane, and leaves
    /// out a condition the set does not state.</summary>
    public string Summary => Kind == nameof(DataSetKind.PassiveSParams)
        ? string.Join(" · ", new[] { Kind, Ambient, Plane }.Where(x => x != "—"))
        : $"{Kind} · {Ambient} · {Plane} · Q {Quiescent} {PrePulse}";

    public static DataSetRow From(CmcDataSet set, NormalisedData? data)
    {
        var c = set.Conditions;
        return new DataSetRow(
            set.Name,
            set.Kind.ToString(),
            c.AmbientC is { } t ? $"{ModelValueFormat.G(t)} °C" : "—",
            c.ReferencePlane?.ToString().ToLowerInvariant() ?? "—",
            c.Quiescent is { } q ? $"{ModelValueFormat.G(q.Vgs)}, {ModelValueFormat.G(q.Vds)} V" : "—",
            c.PrePulse is { } p ? $"{ModelValueFormat.G(p.Vgs)}, {ModelValueFormat.G(p.Vds)} V" : "",
            data is null ? "—" : data.Count.ToString(CultureInfo.InvariantCulture),
            DataImport.CapacitanceSource(set) is { } source ? $"capacitances: {source}" : "");
    }
}

/// <summary>
/// One network variable in the values table (R-mc14-8): value, lock, bounds, tie and scaling rule, and after a fit its
/// ± error and identifiability. Edits go back through <see cref="Commit"/>, which the view model owns.
/// </summary>
public sealed partial class NetworkValueRow : ObservableObject
{
    private readonly Action<NetworkValueRow, string, string> _commit;
    private bool _loading;

    public NetworkValueRow(string name, NetworkElementKind kind, Action<NetworkValueRow, string, string> commit)
    {
        Name = name;
        Kind = kind;
        _commit = commit;
    }

    public string Name { get; }
    public NetworkElementKind Kind { get; }
    public string Unit => ModelValueFormat.UnitOf(Kind);

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private bool _lock;
    [ObservableProperty] private string _min = "";
    [ObservableProperty] private string _max = "";
    [ObservableProperty] private string _tie = "";
    [ObservableProperty] private string _scaling = "";
    /// <summary>The last fit's ± standard error, as a value; empty before a fit or for a held value.</summary>
    [ObservableProperty] private string _error = "";
    /// <summary>True when the last fit left this value poorly determined.</summary>
    [ObservableProperty] private bool _poor;
    /// <summary>The correlated partner and its correlation, or the bound it sits at — the poor marker's tooltip.</summary>
    [ObservableProperty] private string _poorTip = "";

    /// <summary>Sets every cell from the document without committing anything back.</summary>
    public void Load(string value, bool locked, string min, string max, string tie, string scaling, string error, bool poor, string poorTip)
    {
        _loading = true;
        try
        {
            Value = value; Lock = locked; Min = min; Max = max; Tie = tie; Scaling = scaling;
            Error = error; Poor = poor; PoorTip = poorTip;
        }
        finally { _loading = false; }
    }

    partial void OnValueChanged(string value) => Commit(nameof(Value), value);
    partial void OnLockChanged(bool value) => Commit(nameof(Lock), value ? "true" : "false");
    partial void OnMinChanged(string value) => Commit(nameof(Min), value);
    partial void OnMaxChanged(string value) => Commit(nameof(Max), value);
    partial void OnTieChanged(string value) => Commit(nameof(Tie), value);
    partial void OnScalingChanged(string value) => Commit(nameof(Scaling), value);

    private void Commit(string field, string text)
    {
        if (!_loading) _commit(this, field, text);
    }
}

/// <summary>One passive topology proposal (R-mc14-9): its rank, title, score and the fitted values, for its thumbnail.</summary>
public sealed record ProposalRow(int Rank, string Topology, string Title, string Score, SchematicModel? Drawing);

/// <summary>
/// One constituent's method picker (brief-modelcomposer-15 R-mc15-1) — the same control for every constituent: the
/// method, an ANN's layers, activation and iterations, a table's grid and smoothing (and a charge table's path), an
/// equation's template or expression and its fitted parameters. Edits go back through the view model, one undo step each.
/// </summary>
public sealed partial class MethodPickerRow : ObservableObject
{
    /// <summary>The template list's last item: the user's own expression, which the document spells as no template.</summary>
    public const string Custom = "Custom";

    private readonly Action<MethodPickerRow, string, string> _commit;
    private readonly Func<string, TemplateInfo?> _templateInfo;
    private bool _loading;

    public MethodPickerRow(string name, IReadOnlyList<string> methods, IReadOnlyList<string> templates, bool charge,
                           Action<MethodPickerRow, string, string> commit, Func<string, TemplateInfo?>? template = null)
    {
        Name = name;
        Methods = methods;
        Choices = [.. methods.Select(m => new MethodChoice(m))];
        Templates = [.. templates, Custom];
        IsCharge = charge;
        _commit = commit;
        _templateInfo = template ?? (_ => null);
    }

    public string Name { get; }
    public IReadOnlyList<string> Methods { get; }
    /// <summary>The methods as the picker lists them, each with the reason it cannot be fitted as its tooltip (R-mc17-6).</summary>
    public IReadOnlyList<MethodChoice> Choices { get; }
    public IReadOnlyList<string> Templates { get; }
    public IReadOnlyList<string> Activations { get; } = Enum.GetNames<CircuitRF.Core.Devices.Composed.AnnActivation>();
    public IReadOnlyList<string> Paths { get; } = Enum.GetNames<IntegrationPath>();
    public bool IsCharge { get; }
    /// <summary>The gate current: its equation is two branch expressions, IgGs and IgGd.</summary>
    public bool IsGate => Name == "Ig";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnn), nameof(IsTable), nameof(IsEquation), nameof(ShowsPath))]
    private string _method = "";
    [ObservableProperty] private string _hidden = "";
    [ObservableProperty] private string _activation = "";
    [ObservableProperty] private string _iterations = "";
    [ObservableProperty] private string _gridStep = "";
    [ObservableProperty] private string _smoothing = "";
    [ObservableProperty] private string _path = "";
    /// <summary>The template's name, or <see cref="Custom"/> for the user's own expression.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom), nameof(TemplateExpression), nameof(ShowsTemperature), nameof(TemperatureWatermark))]
    private string _template = Custom;
    [ObservableProperty] private string _expression = "";
    [ObservableProperty] private string _expressionGd = "";
    /// <summary>The template parameters made linear in T, comma-separated; empty is the template's default, "none" is none.</summary>
    [ObservableProperty] private string _temperature = "";
    /// <summary>The last fit's equation parameters, one "name = value" line each; empty before a fit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FittedText), nameof(HasFitted))]
    private IReadOnlyList<string> _fitted = [];

    public bool IsAnn => Method == "ANN";
    public bool IsTable => Method == "Table";
    public bool IsEquation => Method == "Equation";
    /// <summary>A charge table is the line integral of the capacitances, along path A, B or their mean.</summary>
    public bool ShowsPath => IsCharge && IsTable;
    /// <summary>The user's own expression: its text boxes show, the template's law does not.</summary>
    public bool IsCustom => Template == Custom;
    /// <summary>The chosen template's law, as the fit reads it — what the expression boxes hold for a custom one.</summary>
    public string TemplateExpression => IsCustom ? "" : _templateInfo(Template)?.Expression ?? "";
    /// <summary>A current template's temperature coefficients; charge templates have none.</summary>
    public bool ShowsTemperature => !IsCharge && !IsCustom;
    public string TemperatureWatermark => _templateInfo(Template)?.Scale is { } scale ? $"{scale} when the data spans temperatures" : "none";
    /// <summary>What the first expression box is for: Id, Qg or Qd — or the gate's IgGs branch, IgGd having a box of its own.</summary>
    public string ExpressionWatermark => $"{(IsGate ? "IgGs" : Name)} = your expression in vgs, vds and parameters";
    /// <summary>The fitted parameters as one selectable block, so they copy together.</summary>
    public string FittedText => string.Join(Environment.NewLine, Fitted);
    public bool HasFitted => Fitted.Count > 0;

    /// <summary>Sets every field from the document without committing anything back.</summary>
    public void Load(string method, string hidden, string activation, string iterations, string grid, string smoothing, string path,
                     string template, string expression, string expressionGd, IReadOnlyList<string> fitted, string temperature = "")
    {
        _loading = true;
        try
        {
            Method = method; Hidden = hidden; Activation = activation; Iterations = iterations; GridStep = grid; Smoothing = smoothing;
            Path = path; Template = template.Length == 0 ? Custom : template; Expression = expression; ExpressionGd = expressionGd;
            Fitted = fitted; Temperature = temperature;
        }
        finally { _loading = false; }
    }

    /// <summary>The picker's selection: <see cref="Method"/>'s choice.</summary>
    public MethodChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Name == Method);
        set { if (value is not null) Method = value.Name; }
    }

    /// <summary>Sets each method's tooltip: <paramref name="refusal"/>'s reason, or none.</summary>
    public void SetTips(Func<string, string?> refusal)
    {
        foreach (var c in Choices) c.Tip = refusal(c.Name);
    }

    partial void OnMethodChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedChoice));
        Commit(nameof(Method), value);
    }
    partial void OnHiddenChanged(string value) => Commit(nameof(Hidden), value);
    partial void OnActivationChanged(string value) => Commit(nameof(Activation), value);
    partial void OnIterationsChanged(string value) => Commit(nameof(Iterations), value);
    partial void OnGridStepChanged(string value) => Commit(nameof(GridStep), value);
    partial void OnSmoothingChanged(string value) => Commit(nameof(Smoothing), value);
    partial void OnPathChanged(string value) => Commit(nameof(Path), value);
    partial void OnTemplateChanged(string value) => Commit(nameof(Template), value == Custom ? "" : value);
    partial void OnTemperatureChanged(string value) => Commit(nameof(Temperature), value);
    partial void OnExpressionChanged(string value) => Commit(nameof(Expression), value);
    partial void OnExpressionGdChanged(string value) => Commit(nameof(ExpressionGd), value);

    private void Commit(string field, string text)
    {
        if (!_loading && text is not null) _commit(this, field, text);
    }
}

/// <summary>What a picker shows of a template: its law (both branches for the gate) and the parameter its current scales by.</summary>
public sealed record TemplateInfo(string Expression, string? Scale);

/// <summary>One method in a picker: its name and, when the data cannot support it, the refusal a fit would give.</summary>
public sealed partial class MethodChoice(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private string? _tip;
}

/// <summary>One trap state's row (R-mc15-5): its toggle — or, when the data cannot support it, the one-line reason in its
/// place — and the trap-setting level it was trained from when there was only one.</summary>
public sealed partial class TrapStateRow(string state, string label, Action<TrapStateRow, bool> toggle) : ObservableObject
{
    public string State { get; } = state;
    public string Label { get; } = label;

    [ObservableProperty] private bool _isOn;
    /// <summary>Why the data cannot support the state; empty when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Supported))]
    private string _reason = "";
    /// <summary>"from 3 V" when one trap-setting level trained it; empty otherwise.</summary>
    [ObservableProperty] private string _level = "";

    public bool Supported => Reason.Length == 0;
    private bool _loading;

    public void Load(bool on, string reason, string level)
    {
        _loading = true;
        try { IsOn = on; Reason = reason; Level = level; }
        finally { _loading = false; }
    }

    partial void OnIsOnChanged(bool value) { if (!_loading) toggle(this, value); }
}

/// <summary>
/// One shift-coupling strength (brief-modelcomposer-19 R-mc19-6): its value, its lock — a locked strength is typed and not
/// fitted — and the last fit's ± when it was fitted. Typing a value locks it; clearing it hands it back to the fit.
/// </summary>
public sealed partial class StrengthRow(string name, string state, Action<StrengthRow, string, string> commit) : ObservableObject
{
    public string Name { get; } = name;
    /// <summary>The state it belongs to, in words.</summary>
    public string State { get; } = state;
    public string Tip => $"the {State} strength, V/V: the gate-voltage shift per volt of the state away from its DC value";
    private bool _loading;

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private bool _lock;
    /// <summary>"± 0.0004" after a fit; empty for a typed strength or before a fit.</summary>
    [ObservableProperty] private string _error = "";

    public void Load(string value, bool locked, string error)
    {
        _loading = true;
        try { Value = value; Lock = locked; Error = error; }
        finally { _loading = false; }
    }

    partial void OnValueChanged(string value) { if (!_loading) commit(this, nameof(Value), value); }
    partial void OnLockChanged(bool value) { if (!_loading) commit(this, nameof(Lock), value ? "true" : "false"); }
}

/// <summary>One named number the user types (a trap time constant, a validation tolerance, a report option).</summary>
public sealed partial class ParameterRow(string name, string unit, string tip, Action<ParameterRow, string> commit) : ObservableObject
{
    public string Name { get; } = name;
    public string Unit { get; } = unit;
    public string Tip { get; } = tip;
    private bool _loading;

    [ObservableProperty] private string _value = "";

    public void Load(string value)
    {
        _loading = true;
        try { Value = value; }
        finally { _loading = false; }
    }

    partial void OnValueChanged(string value) { if (!_loading) commit(this, value); }
}

/// <summary>One validation item (R-mc15-7): the data set, what kind of comparison it is, its tolerance and its result line.</summary>
public sealed partial class ValidationRow(string set, string kind, string tolerance) : ObservableObject
{
    public string Set { get; } = set;
    public string Kind { get; } = kind;
    public string Tolerance { get; } = tolerance;

    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool? _passed;
}

/// <summary>One symbol a model can be exported with (R-mc15-8): its value, label, pin count, and the symbol reference its
/// glyph is drawn from (a <c>composedfet://</c> one, which a <c>PaletteGlyphControl</c> resolves with no document).</summary>
public sealed record SymbolRow(string Value, string Label, int Pins, string GlyphRef)
{
    public string PinsText => $"{Pins} pins";
}
