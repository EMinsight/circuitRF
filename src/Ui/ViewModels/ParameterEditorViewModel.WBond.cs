using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The wBond panel of the Parameter Editor — the counterpart of the SnP panel, and for the same
/// reason: a wBond's interesting parameters are not text, and three of them were showing as text
/// nobody could read or act on (owner, 2026-08-16).
///
/// <h3>What was wrong, and what each row is</h3>
/// <list type="bullet">
/// <item><b><c>Design</c> was "gibberish".</b> Correctly so — it is the whole wirebond design, base64
///   of its JSON (§5.0/WB17b), and it is documented as a HIDDEN parameter. It was never meant to be a
///   row. It is replaced here by the one-line summary the symbol body already carries: how many
///   arrays, how many wires, how much wire.</item>
/// <item><b><c>Arrays</c> is bookkeeping, not a value.</b> It records the array list this instance's
///   wiring was drawn against, so a re-import that REORDERS the arrays can be reported instead of
///   silently re-pointing every wire (§9.2/WB35a — pin order IS array order, so a reorder moves every
///   pin while its name moves to a different row). Also documented hidden. It is now maintained
///   automatically by the array editor below, which is the only thing that should ever write it.</item>
/// <item><b><c>SymbolPitch</c> and <c>RefPin</c></b> get the controls SnP's equivalents have.</item>
/// </list>
///
/// <h3>The array editor is the point</h3>
/// <para>"There's no way to add new arrays" — there is now, and it is the schematic-side half of the
/// workflow the owner wants: declare the arrays (and their names, which ARE the pin names) on the
/// symbol, then draw the wires into them somewhere with geometry.</para>
///
/// <para><b>A new array arrives carrying one default wire</b>, exactly as a freshly-dropped wBond does
/// (<see cref="WBondEmbedding.DefaultDesign"/>). It is not an empty array, and that is deliberate:
/// an empty one is refused by <c>WBondDesign.Validate</c> because it makes the mapping matrix
/// rank-deficient and the array-basis inductance singular — so a schematic that could declare one
/// would place a component that cannot be simulated until someone visits another editor. One wire
/// means the component renders, wires up and solves the moment it is added, and the wire is a thing to
/// MOVE rather than a thing to create.</para>
/// </summary>
public partial class ParameterEditorViewModel
{
    /// <summary>True when the selected component is a wBond — gates the whole panel.</summary>
    public bool IsWBond => _target?.Symbol == SymbolKind.WBond;

    /// <summary>
    /// The parameters this panel owns, and which must therefore NOT also appear as generic text rows.
    ///
    /// <para><c>Design</c> and <c>Arrays</c> are hidden by design (§5.0); <c>SymbolPitch</c>/<c>RefPin</c>
    /// have real controls; <c>WarnUnsynced</c> is a checkbox and <c>File</c> is set by Update Layout from
    /// Schematic (<c>Source</c>, the retired Carried/Linked switch, stays hidden on older documents);
    /// <c>Material</c> is an ENUMERATION over the design's own metals, so it gets a dropdown rather than
    /// a text box a typo can reach.</para>
    ///
    /// <para><b><c>LoopHeight</c>, <c>Diameter</c> and <c>Temp</c> are deliberately NOT here.</b> They
    /// are expression fields like any other — which is the point: a generic row is what makes
    /// <c>LoopHeight = loopH</c> typable, and a <c>VAR</c> reference is what a sweep or an optimiser
    /// turns (WB44 property 4). The per-ARRAY spellings of all three do live here, because their names
    /// come from the instance's own array list.</para>
    ///
    /// <para><b><c>GroundPlane</c> joined this list on 2026-08-17</b> (owner: <i>"if GroundPlane is a
    /// bool … then it needs to be a checkbox or combobox entry"</i>). It is read as a BOOLEAN
    /// (<c>ComponentModelFactory.IsTrue</c>), so the text box offered three usable values and infinite
    /// unusable ones, with nothing on screen saying which three. The trade is stated where the control
    /// is built (<see cref="WBondGroundPlaneOptions"/>): a value that is neither blank nor a boolean —
    /// a <c>VAR</c> reference typed before this existed — is still shown and still committed, so the
    /// picker narrows what can be TYPED without invalidating anything already written.</para>
    /// </summary>
    internal static bool IsWBondPanelParameter(string name) =>
        name is WBondEmbedding.DesignParameter or WBondPlacement.ArraysParameter
             or "SymbolPitch" or "RefPin" or "IncludeCapacitance" or "er"
             or WBondPlacement.LegacySourceParameter or WBondPlacement.WarnUnsyncedParameter
             or "File" or "Material" or "GroundPlane"
             or WBondPlacement.MaterialLibraryParameter or FixedTempParameter
        || name.StartsWith("LoopHeight_", StringComparison.Ordinal)
        || name.StartsWith("Diameter_", StringComparison.Ordinal)
        || name.StartsWith("Material_", StringComparison.Ordinal);

    // ── The wire temperature: fixed or solved (brief-wbond-wire-temperature R-wbt-5) ────────────

    /// <summary>The Temp row's checkbox. Panel-owned because it has a real control; <c>Temp</c>,
    /// <c>TempStart</c> and <c>TempEnd</c> are NOT — they stay expression rows, so a <c>VAR</c> is typable
    /// in each (see the note on <see cref="IsWBondPanelParameter"/>).</summary>
    internal const string FixedTempParameter = "FixedTemp";

    /// <summary>Whether the instance's temperature is fixed. ABSENT is fixed: an instance saved before the
    /// solved mode existed keeps the answer it had (D4). A new placement writes "false". Every false spelling
    /// the factory reads ("off", "no", "0") unchecks the box, so the box never disagrees with the run.</summary>
    private bool WBondFixedTemp()
    {
        string v = WBondParameterValue(FixedTempParameter).Trim();
        return !CircuitRF.Core.Devices.BooleanParameter.FalseSpellings.Contains(v, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// R-wbt-5a/b — the Temp row leads with the FixedTemp checkbox; checked, Temp is live and the two boundary
    /// rows are greyed, unchecked the reverse. Greying never touches a value: a <c>VAR</c> in a greyed box is
    /// kept and committed unchanged.
    /// </summary>
    private void ConfigureWBondTemperatureRow(ParameterRowViewModel row)
    {
        bool fixedTemp = WBondFixedTemp();
        switch (row.Name)
        {
            case "Temp":
                row.ShowLeadingCheck(fixedTemp);
                row.LeadingCheckChanged = SetWBondFixedTemp;
                row.SetDisabledByMode(!fixedTemp);
                break;
            case "TempStart" or "TempEnd":
                row.SetDisabledByMode(fixedTemp);
                break;
        }
    }

    /// <summary>
    /// R-wbt-5e — one undoable <see cref="SetParametersCommand"/>, as <c>IncludeCapacitance</c> is. Never rewrites
    /// a value. An instance from before the solved mode states no boundary temperatures, so unchecking adds the
    /// two that are missing at their defaults — that is what the run would use — in canonical order, under Temp.
    /// </summary>
    private void SetWBondFixedTemp(bool fixedTemp)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        var updated = _target.Parameters.Select(p => p.Clone()).ToList();
        Set(FixedTempParameter, fixedTemp ? "true" : "false");
        if (!fixedTemp)
        {
            if (updated.All(p => p.Name != "TempStart")) Set("TempStart", ComponentTypeRegistry.WBondDefaultTempC);
            if (updated.All(p => p.Name != "TempEnd")) Set("TempEnd", ComponentTypeRegistry.WBondDefaultTempEndC);
        }
        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, WBondPlacement.InCanonicalOrder(updated)));

        void Set(string name, string value)
        {
            var param = updated.FirstOrDefault(p => p.Name == name);
            if (param is not null) param.Expression = value;
            else updated.Add(new EditableParameter { Name = name, Expression = value });
        }
    }

    // ── The three simple controls ─────────────────────────────────────────────

    /// <summary>
    /// Whether this panel offers its own <b>Update Layout</b> button (owner, 2026-08-17).
    ///
    /// <para>False when there is no workspace to update INTO — a button that can only report "no
    /// workspace is open" is worse than no button, and the schematic-wide Design-menu command is refused
    /// on exactly the same grounds.</para>
    /// </summary>
    public bool CanUpdateWBondLayout => IsWBond && _schematicVm?.UpdateWBondLayout is not null;

    /// <summary>
    /// Raised after <see cref="UpdateWBondLayoutCommand"/> has run, so a HOST dialog can close itself —
    /// the owner asked for the button to close the dialog and leave the layout focused, and only the view
    /// knows whether it is in a dialog at all (this panel is also the docked Properties inspector, which
    /// must not vanish).
    /// </summary>
    public event Action? WBondLayoutUpdated;

    /// <summary>
    /// <b>Update Layout</b> — writes this ONE wBond's wires into the cell's layout and focuses it, then
    /// closes the dialog.
    ///
    /// <para>Deliberately narrower than Design ▸ Update Layout from Schematic: only this component is
    /// written, so nothing else in the layout is re-resolved, re-placed or re-reported around the user
    /// while they are editing wires (owner, 2026-08-17).</para>
    /// </summary>
    [RelayCommand]
    private void UpdateWBondLayout()
    {
        if (_target is null || _schematicVm?.UpdateWBondLayout is not { } run) return;

        run(_schematicVm, _target);
        WBondLayoutUpdated?.Invoke();
    }

    /// <summary>Tight / Loose — SnP's own two values, meaning the same two things.</summary>
    public static string[] WBondSymbolPitchOptions { get; } =
        [nameof(WBondSymbolPitch.Tight), nameof(WBondSymbolPitch.Loose)];

    [ObservableProperty] private int _wBondSymbolPitchIndex = 1;   // Loose
    [ObservableProperty] private bool _wBondRefPin;

    /// <summary>
    /// Whether this component stamps capacitance to the reference plane (wbond.md §3.7).
    ///
    /// <para><b>A plain checkbox, not a three-value picker like <c>GroundPlane</c></b>, and that is a
    /// deliberate difference: capacitance is the one wBond parameter whose default CHANGES the answer
    /// for a design that already exists, so a reader has to be able to see its state without opening
    /// the payload. "As designed" would leave the most consequential setting on the panel showing
    /// nothing.</para>
    /// </summary>
    [ObservableProperty] private bool _wBondIncludeCapacitance = true;

    /// <summary>
    /// The overmold relative permittivity, <c>er</c> — the plastic an encapsulated package's wires
    /// are embedded in (wbond.md §3.7). 1 is air.
    ///
    /// <para><b>A text box and not a numeric stepper</b>, deliberately, and this is the difference
    /// between it and the checkbox directly above. <c>er</c> is an ordinary real expression, so
    /// <c>er = moldEr</c> is typable and a parametric sweep or an optimiser can turn it — which is
    /// exactly what someone characterising a package against measured data will want. A stepper
    /// would take that away for the sake of two arrows.</para>
    ///
    /// <para>Committed on every edit, like the other wBond controls; a value that is neither a number
    /// nor a resolvable variable is refused at Run by <c>WBondDesign.Validate</c>, where the message
    /// can say what the range means.</para>
    /// </summary>
    [ObservableProperty] private string _wBondEr = "1";

    [ObservableProperty] private string _wBondSummary = "";

    /// <summary>Why the design cannot be read, or empty when it can. Shown instead of the summary.</summary>
    [ObservableProperty] private string _wBondPayloadError = "";

    partial void OnWBondSymbolPitchIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;

        string value = (uint)newValue < (uint)WBondSymbolPitchOptions.Length
            ? WBondSymbolPitchOptions[newValue]
            : nameof(WBondSymbolPitch.Loose);

        ApplyWBondParam("SymbolPitch", value);
    }

    partial void OnWBondRefPinChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        ApplyWBondParam("RefPin", newValue ? "true" : "false");
    }

    partial void OnWBondIncludeCapacitanceChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        ApplyWBondParam("IncludeCapacitance", newValue ? "true" : "false");
    }

    partial void OnWBondErChanged(string? oldValue, string newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;

        // Blank is written as "1" rather than left empty. `er` is declared with a default of 1 and is
        // always emitted, so an empty expression would reach the evaluator as a parse error at Run
        // instead of meaning "air" — which is what someone who cleared the box meant.
        string text = string.IsNullOrWhiteSpace(newValue) ? "1" : newValue.Trim();
        ApplyWBondParam("er", text);
    }

    // ── GroundPlane: a boolean, so a picker rather than a text box ─────────────

    /// <summary>
    /// What the ground-plane override can be set to. <b>Three values, and the first is what UNSET
    /// means</b> — the design's own <c>GroundPlane.Enabled</c>, which is what a blank parameter has
    /// always meant and what <c>NetExtractor</c> drops rather than emitting.
    ///
    /// <para><b>Why a picker at all</b> (owner, 2026-08-17). The engine reads this through
    /// <c>ComponentModelFactory.IsTrue</c>, so a string is true only when it is literally "true" and a
    /// number only when it is non-zero — three usable values behind a box that accepted anything, with
    /// nothing saying so. Typing "yes" produced a silently DISABLED ground plane, which changes every
    /// inductance in the component.</para>
    ///
    /// <para><b>What the picker costs, stated rather than discovered:</b> a <c>VAR</c> reference is no
    /// longer typable here, unlike <c>Temp</c> and the loop heights which stay generic expression rows
    /// (WB44 property 4). Sweeping a ground plane is not a thing — it is present or it is not — and the
    /// value is not interpolable, so nothing an optimiser turns is lost. An expression already written
    /// into an older design is <b>kept</b>: <see cref="WBondGroundPlaneChoices"/> appends whatever is
    /// there when it is none of these three, so the panel never rewrites a value it did not understand.</para>
    /// </summary>
    public static string[] WBondGroundPlaneOptions { get; } = ["As designed", "Yes", "No"];

    /// <summary>The expression each option commits. Index-parallel to <see cref="WBondGroundPlaneOptions"/>.</summary>
    private static readonly string[] GroundPlaneValues = ["", "true", "false"];

    /// <summary>
    /// The picker's live item list: the three standing options, plus the component's own value when
    /// it is none of them. A ComboBox whose selection is absent from its items renders blank, which
    /// reads as the value having been lost — the same rule <c>ParameterRowViewModel.ChoiceOptions</c>
    /// already follows for a kit part's declared choices.
    /// </summary>
    public ObservableCollection<string> WBondGroundPlaneChoices { get; } = [.. WBondGroundPlaneOptions];

    [ObservableProperty] private int _wBondGroundPlaneIndex;

    partial void OnWBondGroundPlaneIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;

        // Beyond the three standing options is the carried-over value itself — selecting it is a
        // no-op, never a rewrite of an expression this panel does not understand.
        if ((uint)newValue >= (uint)GroundPlaneValues.Length) return;

        ApplyWBondParam("GroundPlane", GroundPlaneValues[newValue]);
    }

    /// <summary>
    /// Points the picker at the component's current value, extending the list when that value is
    /// neither blank nor a boolean.
    /// </summary>
    private void RefreshWBondGroundPlane()
    {
        string current = WBondParameterValue("GroundPlane").Trim();

        // Back to the three standing options each time, so a carried-over value from the previously
        // selected component cannot linger in the list of this one.
        while (WBondGroundPlaneChoices.Count > WBondGroundPlaneOptions.Length)
            WBondGroundPlaneChoices.RemoveAt(WBondGroundPlaneChoices.Count - 1);

        int index = current.Length == 0 ? 0
                  : current.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1
                  : current.Equals("false", StringComparison.OrdinalIgnoreCase) ? 2
                  : -1;

        if (index < 0)
        {
            WBondGroundPlaneChoices.Add(current);
            index = WBondGroundPlaneChoices.Count - 1;
        }

        WBondGroundPlaneIndex = index;
    }

    // ── Warn if Schematic Not Synced to Layout (wbond.md §9.7, revised 2026-10-05) ──

    /// <summary>
    /// Whether a Run warns when this component's wires no longer match the layout's <c>.wBond</c>. The
    /// schematic's own wires simulate either way — this replaced WB45's Carried/Linked choice, under
    /// which a Linked instance simulated the file and an array added in the layout refused the run.
    /// Checked unless the component says otherwise, so a schematic written before it existed warns.
    /// </summary>
    [ObservableProperty] private bool _wBondWarnUnsynced = true;

    /// <summary>The comparison's current answer, shown under the checkbox whether or not it warns.</summary>
    [ObservableProperty] private string _wBondSyncNote = "";

    partial void OnWBondWarnUnsyncedChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        ApplyWBondParam(WBondPlacement.WarnUnsyncedParameter, newValue ? "true" : "false");
    }

    // ── §5.5.1/WB44: the controlling parameters, per array ────────────────────

    /// <summary>
    /// One row per wire array, carrying that array's own <c>LoopHeight_&lt;array&gt;</c>,
    /// <c>Diameter_&lt;array&gt;</c> and <c>Material_&lt;array&gt;</c>.
    ///
    /// <para>Generated from the instance's OWN array list, so the rows name G1/G2 rather than asking
    /// the user to spell a suffix — which is also the only way this can be offered at all, since the
    /// names are not knowable until the payload is decoded.</para>
    /// </summary>
    public ObservableCollection<WBondControlRow> WBondControls { get; } = [];

    /// <summary>The metals this design declares — Au/Al/Cu/Ag plus anything the design added.</summary>
    public ObservableCollection<string> WBondMaterialOptions { get; } = [];

    /// <summary>
    /// The unsuffixed <c>Material</c> override — an enumeration, so a dropdown rather than a text box
    /// a typo can reach. The <b>first</b> entry is "As drawn", which is what unset means and is
    /// deliberately distinct from any metal's name.
    /// </summary>
    [ObservableProperty] private int _wBondMaterialIndex;

    partial void OnWBondMaterialIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        if (IsNewMaterialRow(newValue))
        {
            BeginNewWBondMaterial(() => WBondMaterialIndex = oldValue);
            return;
        }
        SetWBondMaterial("Material", MaterialValueAt(newValue));
    }

    /// <summary>Index 0 is "As drawn" — the empty expression that means the parameter is unset — and the
    /// last is <see cref="NewMaterialRow"/>, which is an action and never a value.</summary>
    private string MaterialValueAt(int index) =>
        index >= 1 && index < WBondMaterialOptions.Count && !IsNewMaterialRow(index)
            ? WBondMaterialOptions[index]
            : "";

    // ── Workspace materials and New Material… ─────────────────────────────────

    /// <summary>The material list's last row: opens the workspace's material library in the Materials editor
    /// (reusing one, or creating <c>tech/workspace-materials.cmat</c>) with a new material started.</summary>
    public const string NewMaterialRow = "New Material…";

    /// <summary>The workspace library the list was built from (absolute), or null — see
    /// <see cref="WBondMaterialLibrary.ForListing"/>.</summary>
    private string? _wBondLibraryPath;

    /// <summary>The metals in the list that come from <see cref="_wBondLibraryPath"/>.</summary>
    private HashSet<string> _wBondLibraryNames = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsNewMaterialRow(int index)
        => index >= 0 && index < WBondMaterialOptions.Count && WBondMaterialOptions[index] == NewMaterialRow;

    /// <summary>How the row's selection is put back after New Material…, deferred past the ComboBox's own
    /// selection commit (the layout inspector's footprint picker found that an edit made INSIDE it is
    /// swallowed). Tests run the work inline.</summary>
    internal Action<Action> PostToUi { get; set; } = work => Avalonia.Threading.Dispatcher.UIThread.Post(work);

    /// <summary>Puts the selection back (<paramref name="revert"/>) and asks the workspace to open the library.
    /// The new material is CHOSEN afterwards, from this list, once it has a name and a σ₂₀ and is saved.</summary>
    internal void BeginNewWBondMaterial(Action revert)
    {
        PostToUi(() =>
        {
            bool was = _isRefreshing;
            _isRefreshing = true;
            revert();
            _isRefreshing = was;
            if (_target is not null && _schematicVm?.NewWBondMaterial is { } open) open(_schematicVm, _target);
        });
    }

    /// <summary>
    /// Writes one <c>Material</c>/<c>Material_&lt;array&gt;</c> AND keeps <c>MaterialLibrary</c> true to it, in one
    /// undoable command: the library is named while any material parameter names one of its metals, and removed
    /// when none does — so choosing a shipped metal again is also how a missing library's reference is cleared.
    /// </summary>
    internal void SetWBondMaterial(string name, string value)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;

        var updated = _target.Parameters.Select(p => p.Clone()).ToList();
        var existing = updated.FirstOrDefault(p => p.Name == name);
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            // The unsuffixed one is declared, so it is blanked; a per-array one is removed (see
            // SetWBondControlParameter for why).
            if (name == "Material") { if (existing is not null) existing.Expression = ""; }
            else if (existing is not null) updated.Remove(existing);
        }
        else if (existing is not null) existing.Expression = trimmed;
        else updated.Add(new EditableParameter { Name = name, Expression = trimmed });

        bool usesLibrary = _wBondLibraryPath is not null && updated.Any(p =>
            WBondPlacement.IsMaterialParameter(p.Name) && _wBondLibraryNames.Contains(p.Expression.Trim()));
        updated.RemoveAll(p => p.Name == WBondPlacement.MaterialLibraryParameter);
        if (usesLibrary)
            updated.Add(new EditableParameter
            {
                Name = WBondPlacement.MaterialLibraryParameter,
                Expression = WBondPlacement.StoredMaterialLibrary(_wBondLibraryPath!, _schematicVm.EditModel.SchematicDirectory),
            });

        if (updated.Count == _target.Parameters.Count
            && updated.Zip(_target.Parameters).All(t => t.First.Name == t.Second.Name && t.First.Expression == t.Second.Expression))
            return;

        _schematicVm.Execute(new SetParametersCommand(
            _schematicVm.EditModel, _target, WBondPlacement.InCanonicalOrder(updated)));
    }

    /// <summary>Re-reads the workspace library's metals into the list — after the library is saved, so a metal
    /// just made in the Materials editor can be chosen without reselecting the component.</summary>
    public void RefreshWBondMaterials()
    {
        if (_target?.Symbol != SymbolKind.WBond) return;
        bool was = _isRefreshing;
        _isRefreshing = true;
        RebuildWBondControlRows(TryReadWBondDesign(out var design) ? design : null);
        _isRefreshing = was;
    }

    /// <summary>Writes one per-array controlling parameter, removing it entirely when it is blanked.</summary>
    internal void SetWBondControlParameter(string name, string value)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;

        var updated = _target.Parameters.Select(p => p.Clone()).ToList();
        var existing = updated.FirstOrDefault(p => p.Name == name);
        string trimmed = value.Trim();

        // An unset parameter is REMOVED, not left blank. Both mean "as drawn" to the extractor, which
        // drops a blank — but a row that is no longer there is what makes the panel honest about which
        // arrays actually carry an override.
        if (trimmed.Length == 0)
        {
            if (existing is null) return;
            updated.Remove(existing);
        }
        else if (existing is not null)
        {
            if (existing.Expression == trimmed) return;
            existing.Expression = trimmed;
        }
        else
        {
            updated.Add(new EditableParameter
            {
                Name = name,
                Expression = trimmed,
                Unit = name.StartsWith("Material_", StringComparison.Ordinal) ? "" : "mil",
                Dimension = name.StartsWith("Material_", StringComparison.Ordinal)
                    ? UnitDimension.None : UnitDimension.Length,
            });
        }

        // A per-array override is APPENDED when its box is first committed, so without this the
        // on-symbol label order is the order the user's focus happened to visit the boxes in — the
        // owner's "LoopHeight_G2, LoopHeight_G1, LoopHeight_G3" (2026-08-17). Sorting the list itself,
        // not at render time, is what makes the dialog and the symbol agree.
        _schematicVm.Execute(new SetParametersCommand(
            _schematicVm.EditModel, _target, WBondPlacement.InCanonicalOrder(updated)));
    }

    /// <summary>
    /// What a BLANK generic wBond row is actually using, shown greyed in its empty box: for <c>Temp</c>, the
    /// temperature stored with the wires (125 °C unless changed). Just the number — the row's unit column already
    /// says °C — because a box showing nothing gave no way to tell which temperature the run used.
    /// </summary>
    private string WBondPlaceholderFor(string name)
        => name == "Temp" && TryReadWBondDesign(out var design) && design is not null
            ? design.OperatingTempC.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : "";

    private string WBondParameterValue(string name) =>
        _target?.Parameters.FirstOrDefault(p => p.Name == name)?.Expression ?? "";

    /// <summary>A per-array controlling parameter's current expression, for a row to display.</summary>
    internal string ValueOfControl(string name) => WBondParameterValue(name);

    // ── The array editor ──────────────────────────────────────────────────────

    /// <summary>One row per wire array: its name — which IS a pin-pair name — and how many wires it holds.</summary>
    public ObservableCollection<WBondArrayEditRow> WBondArrays { get; } = [];

    /// <summary>True while there is more than one array, so the last one cannot be removed.</summary>
    public bool CanRemoveWBondArray => WBondArrays.Count > 1;

    /// <summary>
    /// Adds an array, named for the first free <c>G&lt;n&gt;</c>, carrying one default wire.
    ///
    /// <para>The new wire is offset from the ones already there by its own span, so a design built up
    /// here arrives as a row of separate wires rather than N copies stacked in one place — which
    /// would be singular the moment it was solved, two wires of zero separation having infinite
    /// mutual coupling.</para>
    /// </summary>
    [RelayCommand]
    private void AddWBondArray()
    {
        if (!TryReadWBondDesign(out var design) || design is null) return;

        long pitch = WBondUnits.ToNm(WBondEmbedding.DefaultWire.SpanMils, WBondUnit.Mil);
        long offset = pitch * design.Arrays.Count;

        long footZ = WBondDefaults.FootZNm;
        var start = WBondEmbedding.DefaultWire.StartAt(footZ);
        var end = WBondEmbedding.DefaultWire.EndAt(footZ);

        design.Arrays.Add(new WireArray
        {
            Name = NextFreeArrayName(design),
            Wires =
            {
                LoopShape.CreateSeedWire(
                    new Point3(start.X + offset, start.Y, start.Z),
                    new Point3(end.X + offset, end.Y, end.Z),
                    WBondUnits.ToNm(WBondEmbedding.DefaultWire.DiameterMils, WBondUnit.Mil),
                    WireMaterials.Default.Name,
                    WBondUnits.ToNm(WBondEmbedding.DefaultWire.LoopHeightMils, WBondUnit.Mil)),
            },
        });

        CommitWBondDesign(design);
    }

    /// <summary>
    /// Removes an array — <b>and every wire in it</b>, which is the part that needs saying.
    ///
    /// <para>The last one is never removable: a wBond with no arrays has no pins, and
    /// <c>WBondModel</c> refuses it by name rather than producing a zero-port component. Better to
    /// disable the button than to explain that afterwards.</para>
    /// </summary>
    [RelayCommand]
    private void RemoveWBondArray(WBondArrayEditRow? row)
    {
        if (row is null) return;
        if (!TryReadWBondDesign(out var design) || design is null) return;
        if (design.Arrays.Count <= 1) return;
        if ((uint)row.Index >= (uint)design.Arrays.Count) return;

        design.Arrays.RemoveAt(row.Index);
        CommitWBondDesign(design);
    }

    /// <summary>
    /// Renames an array, from its row. Refused — silently, by snapping the row back — when the name is
    /// blank or already taken: array names are the symbol's pin names and must be unique
    /// (<c>WBondDesign.Validate</c>), and a duplicate would make the payload unreadable by the
    /// component that carries it.
    /// </summary>
    internal void RenameWBondArray(WBondArrayEditRow row, string name)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        if (!TryReadWBondDesign(out var design) || design is null) return;
        if ((uint)row.Index >= (uint)design.Arrays.Count) return;

        string trimmed = name.Trim();
        bool taken = design.Arrays
            .Where((_, i) => i != row.Index)
            .Any(a => a.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

        if (trimmed.Length == 0 || taken)
        {
            RefreshWBondProperties();   // snap the box back to the name that is actually there
            return;
        }

        string previous = design.Arrays[row.Index].Name;
        if (previous.Equals(trimmed, StringComparison.Ordinal)) return;

        design.Arrays[row.Index].Name = trimmed;
        CommitWBondDesign(design, renamedFrom: previous, renamedTo: trimmed);
    }

    private static string NextFreeArrayName(WBondDesign design)
    {
        var used = new HashSet<string>(design.Arrays.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string candidate = "G" + n;
            if (used.Add(candidate)) return candidate;
        }
    }

    // ── Reading and writing the payload ───────────────────────────────────────

    private bool TryReadWBondDesign(out WBondDesign? design)
    {
        design = null;
        if (_target is null) return false;

        string? payload = _target.Parameters
            .FirstOrDefault(p => p.Name == WBondEmbedding.DesignParameter)?.Expression;

        return WBondEmbedding.TryDecode(payload, out design) && design is not null;
    }

    /// <summary>
    /// Writes the design back onto the component, <b>and the <c>Arrays</c> record with it</b>.
    ///
    /// <para>Both in ONE command, so undo takes them back together. They are two statements of the
    /// same fact — what this instance's wiring is drawn against — and a design edited without its
    /// record updated would report drift against itself on the next import.</para>
    /// </summary>
    /// <param name="renamedFrom">
    /// The array's previous name when this edit was a RENAME — so its controlling parameters travel
    /// with it. An override left behind under the old suffix silently stops applying, with its value
    /// still in the dialog and still drawn on the symbol.
    /// </param>
    /// <param name="renamedTo">Its new name.</param>
    private void CommitWBondDesign(WBondDesign design, string? renamedFrom = null, string? renamedTo = null)
    {
        if (_target is null || _schematicVm is null) return;

        var updated = _target.Parameters.Select(p => p.Clone()).ToList();
        Set(WBondEmbedding.DesignParameter, WBondEmbedding.Encode(design));
        Set(WBondPlacement.ArraysParameter, WBondSymbolProvider.ArraysKeyOf(design));

        // Follow a rename, drop an override whose array has just been deleted, then order the whole
        // list so the symbol's labels come out in array order. All three ride in the SAME command as
        // the design edit, so undo takes them back together — they are one statement about one change.
        var reconciled = WBondPlacement.ReconcilePerArrayParameters(
            updated, [.. design.Arrays.Select(a => a.Name)], renamedFrom, renamedTo);

        _schematicVm.Execute(new SetParametersCommand(
            _schematicVm.EditModel, _target, WBondPlacement.InCanonicalOrder(reconciled)));

        void Set(string name, string value)
        {
            var param = updated.FirstOrDefault(p => p.Name == name);
            if (param is not null) param.Expression = value;
            else updated.Add(new EditableParameter
                { Name = name, Expression = value, ShowOnSchematic = false });
        }
    }

    private void ApplyWBondParam(string name, string value)
    {
        var updated = _target!.Parameters.Select(p => p.Clone()).ToList();
        var param = updated.FirstOrDefault(p => p.Name == name);

        if (param is not null) param.Expression = value;
        else updated.Add(new EditableParameter { Name = name, Expression = value });

        _schematicVm!.Execute(new SetParametersCommand(_schematicVm.EditModel, _target!, updated));
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pulls the panel's state from the component. Mirrors <c>RefreshSnpProperties</c>, including its
    /// <c>_isRefreshing</c> guard so the property callbacks do not write back what they just read.
    /// </summary>
    private void RefreshWBondProperties()
    {
        if (_target?.Symbol != SymbolKind.WBond) return;

        string pitch = _target.Parameters.FirstOrDefault(p => p.Name == "SymbolPitch")?.Expression
                    ?? nameof(WBondSymbolPitch.Loose);
        int pitchIndex = Array.IndexOf(WBondSymbolPitchOptions, pitch);
        if (pitchIndex < 0) pitchIndex = Array.IndexOf(WBondSymbolPitchOptions, nameof(WBondSymbolPitch.Loose));

        bool refPin = (_target.Parameters.FirstOrDefault(p => p.Name == "RefPin")?.Expression ?? "false")
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        // Absent means the design's own flag decides, and that defaults to true — so an unset
        // parameter shows CHECKED, which is what the component will actually do.
        string capacitance = _target.Parameters
            .FirstOrDefault(p => p.Name == "IncludeCapacitance")?.Expression ?? "";
        bool includeCapacitance = string.IsNullOrWhiteSpace(capacitance)
            || capacitance.Equals("true", StringComparison.OrdinalIgnoreCase);

        // Absent means air, which is both the declared default and the value every design had before
        // overmold existed.
        string er = _target.Parameters.FirstOrDefault(p => p.Name == "er")?.Expression ?? "";
        if (string.IsNullOrWhiteSpace(er)) er = "1";

        bool readable = TryReadWBondDesign(out var design) && design is not null;

        OnPropertyChanged(nameof(CanUpdateWBondLayout));

        _isRefreshing = true;
        WBondSymbolPitchIndex = pitchIndex;
        WBondRefPin = refPin;
        WBondIncludeCapacitance = includeCapacitance;
        WBondEr = er;

        // An unreadable payload is a reported, repairable state — the same stance TryDecode itself
        // takes — not an empty panel that reads as "this component has nothing in it".
        WBondPayloadError = readable
            ? ""
            : "This wBond's embedded design could not be read. Use File ▸ Import ▸ Wirebond Wires… to replace it.";
        // The workspace technology's own unit, not a hard-coded one (owner, 2026-08-17).
        WBondSummary = readable ? SummaryOf(design!) : "";

        RebuildWBondArrayRows(design);
        RefreshWBondGroundPlane();
        RefreshWBondSync();
        RebuildWBondControlRows(design);
        _isRefreshing = false;
    }

    /// <summary>
    /// The one-line summary, describing what will actually SIMULATE rather than what was drawn.
    ///
    /// <para>Total wire length moves with loop height, so a component carrying <c>LoopHeight = 45 mil</c>
    /// over 20 mil wires would otherwise report a length no run ever uses — the same disagreement the
    /// owner hit at Update Layout on 2026-08-17, one surface over. When an override is in force the line
    /// says so, because a number that changes as you type in a box below it should explain itself.</para>
    ///
    /// <para><b>The design handed in is a fresh decode local to this refresh and is never committed</b>
    /// — the write-back paths (<see cref="AddWBondArray"/> and friends) each decode their own. Applying
    /// an override on a path that then writes back would bake it into the payload and break WB44
    /// property 1. A refused value (a zero, an unknown metal) falls back to the drawn summary rather
    /// than throwing inside a UI refresh; the run refuses it by name, which is where that belongs.</para>
    /// </summary>
    private string SummaryOf(WBondDesign design)
    {
        var unit = _schematicVm?.LengthDisplayUnit ?? LayoutUnit.Mil;
        string asDrawn = WBondSymbolGenerator.Describe(design, unit);

        if (_target is null) return asDrawn;

        var read = WBondPlacement.ReadControllingParameters(_target);
        if (read.Overrides.IsEmpty) return asDrawn;

        // A SECOND decode, and only on the uncommon path where an override is actually set: the
        // caller's design goes on to build the array rows, and reshaping the object it is holding
        // would leave this method's cost paid by whoever adds the next thing that reads geometry
        // from it. Decoding twice on every selection change would not be worth it; decoding twice
        // when the user has typed a loop height is.
        if (!TryReadWBondDesign(out var scratch) || scratch is null) return asDrawn;

        try { ControllingParameters.ApplyTo(scratch, read.Overrides); }
        catch (InvalidOperationException) { return asDrawn; }

        return WBondSymbolGenerator.Describe(scratch, unit) + " · with overrides";
    }

    /// <summary>
    /// Pulls the sync switch and states what the comparison finds right now — the same
    /// <see cref="WBondSync"/> answer the Run's warning comes from, so the two cannot disagree.
    /// </summary>
    private void RefreshWBondSync()
    {
        if (_target is null) return;

        WBondWarnUnsynced = WBondPlacement.WarnsIfUnsynced(_target);
        WBondSyncNote = WBondSync.Check(_target, _schematicVm?.EditModel.SchematicDirectory).Note;
    }

    /// <summary>
    /// Rebuilds the per-array controlling-parameter rows, and the material list they choose from.
    ///
    /// <para>The materials are the DESIGN's own (<c>WBondDesign.Materials</c>) followed by every other
    /// conductor circuitRF ships (<c>WireMaterials.Library</c>): the design's table is user-extensible,
    /// so restricting the dropdown to what shipped would make a design's own metal unnameable, and
    /// restricting it to the design would hide shipped metals a wire may be made of. An unknown name
    /// is refused BY NAME at elaboration, so a hand-authored <c>.cnl</c> is still checked.</para>
    /// </summary>
    private void RebuildWBondControlRows(WBondDesign? design)
    {
        var materials = new List<string> { AsDrawn };
        materials.AddRange(design?.MaterialChoices() ?? WireMaterials.Library.Select(m => m.Name));

        // The workspace library's metals, then any stored name the list cannot otherwise show (a metal of a
        // library that has since gone missing) — shown as itself rather than as "As drawn", which would be a
        // lie about what runs — then the action row.
        _wBondLibraryPath = _target is null ? null
            : WBondMaterialLibrary.ForListing(_target, _schematicVm?.EditModel.SchematicDirectory);
        _wBondLibraryNames = new HashSet<string>(
            WBondMaterialLibrary.ConductorsOf(_wBondLibraryPath).Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (string name in _wBondLibraryNames.Order(StringComparer.Ordinal))
            if (!materials.Contains(name, StringComparer.OrdinalIgnoreCase)) materials.Add(name);
        foreach (var p in _target?.Parameters ?? [])
            if (WBondPlacement.IsMaterialParameter(p.Name) && p.Expression.Trim() is { Length: > 0 } stored
                && !materials.Contains(stored, StringComparer.OrdinalIgnoreCase))
                materials.Add(stored);
        materials.Add(NewMaterialRow);

        if (!materials.SequenceEqual(WBondMaterialOptions))
        {
            WBondMaterialOptions.Clear();
            foreach (string m in materials) WBondMaterialOptions.Add(m);
        }

        string current = WBondParameterValue("Material");
        int index = current.Length == 0
            ? 0
            : materials.FindIndex(m => m.Equals(current, StringComparison.OrdinalIgnoreCase));
        WBondMaterialIndex = index < 0 ? 0 : index;

        var names = design?.Arrays.Select(a => a.Name).ToList() ?? [];

        if (names.Count != WBondControls.Count
            || names.Where((n, i) => WBondControls[i].ArrayName != n).Any())
        {
            WBondControls.Clear();
            foreach (string name in names) WBondControls.Add(new WBondControlRow(this, name));
        }

        foreach (var row in WBondControls) row.Pull();
    }

    /// <summary>What an UNSET controlling parameter reads as. Never a metal's name, and never "0".</summary>
    internal const string AsDrawn = "As drawn";

    /// <summary>
    /// Rebuilds the array rows — <b>only when the list has actually changed</b>.
    ///
    /// <para>Every commit re-enters here through the selection refresh, and rebuilding the rows
    /// unconditionally would replace the very text box the user is typing a rename into, mid-word.
    /// Comparing the name sequence first is what keeps a rename a rename.</para>
    /// </summary>
    private void RebuildWBondArrayRows(WBondDesign? design)
    {
        var names = design?.Arrays.Select(a => a.Name).ToList() ?? [];

        if (names.Count == WBondArrays.Count
            && names.Select((n, i) => WBondArrays[i].Name == n).All(same => same))
        {
            for (int i = 0; i < names.Count; i++)
                WBondArrays[i].WireCount = design!.Arrays[i].Wires.Count;

            OnPropertyChanged(nameof(CanRemoveWBondArray));
            return;
        }

        WBondArrays.Clear();
        for (int i = 0; i < names.Count; i++)
            WBondArrays.Add(new WBondArrayEditRow(this, i, names[i], design!.Arrays[i].Wires.Count));

        OnPropertyChanged(nameof(CanRemoveWBondArray));
    }
}

/// <summary>
/// One row of the wBond array editor: an array's name, and how many wires are in it.
///
/// <para>The name is committed on <see cref="Commit"/> rather than on every keystroke — a half-typed
/// name is routinely blank or a duplicate, and both are refused, so committing per character would
/// fight the user for the box.</para>
/// </summary>
public sealed partial class WBondArrayEditRow : ObservableObject
{
    private readonly ParameterEditorViewModel _owner;

    public WBondArrayEditRow(ParameterEditorViewModel owner, int index, string name, int wireCount)
    {
        _owner = owner;
        Index = index;
        _name = name;
        _wireCount = wireCount;
    }

    /// <summary>Position in the array list — which is also this pin pair's position on the symbol.</summary>
    public int Index { get; }

    [ObservableProperty] private string _name;

    [ObservableProperty] private int _wireCount;

    // The "G1.i / G1.o" column that used to sit here is GONE (owner, 2026-08-17: "the user does not
    // care or even needs to know what G1.i or G1.o is"). The pin NAMES are an internal spelling of
    // "this array's + and − terminals"; what the row is for is the array's own name and its wire count,
    // and the terminals are visible on the symbol itself where they are actually connected to.

    /// <summary>How many wires, phrased for a row that is mostly read at a glance.</summary>
    public string WiresText => WireCount == 1 ? "1 wire" : $"{WireCount} wires";

    partial void OnWireCountChanged(int value) => OnPropertyChanged(nameof(WiresText));

    /// <summary>Commits the typed name. Called from the view on Enter or lost focus.</summary>
    public void Commit() => _owner.RenameWBondArray(this, Name);
}

/// <summary>
/// One array's controlling parameters (<c>wbond.md</c> §5.5.1/WB44): <c>LoopHeight_&lt;array&gt;</c>,
/// <c>Diameter_&lt;array&gt;</c> and <c>Material_&lt;array&gt;</c>.
///
/// <h3>Unset must be visibly distinct from zero</h3>
/// <para>An empty box means "as drawn" — the parameter is not written at all — and <c>0</c> is an
/// error the elaboration refuses by name. They are not the same statement and cannot be allowed to
/// look the same: a wire flattened onto the ground plane and a wire left as the user drew it differ
/// by everything the component reports.</para>
///
/// <h3>Precedence against a layout edit, which the user cannot see from here</h3>
/// <para>A loop-height override regenerates a wire between its own existing FEET and skips a wire that
/// was individually dragged loose from its profile (WB2/WB24). So a dragged FOOT survives the
/// override, a dragged LOOP on a bound wire does not, and a dragged loop on a DETACHED wire does. Only
/// the last of those is silent, and it is reported at the run — see
/// <c>ComponentModelFactory.ReportDetached</c>, whose message names the count and the remedy.</para>
/// </summary>
public sealed partial class WBondControlRow : ObservableObject
{
    private readonly ParameterEditorViewModel _owner;
    private bool _pulling;

    public WBondControlRow(ParameterEditorViewModel owner, string arrayName)
    {
        _owner = owner;
        ArrayName = arrayName;
        _materials = owner.WBondMaterialOptions;
    }

    /// <summary>The array this row controls — which is also a pin-pair name on the symbol.</summary>
    public string ArrayName { get; }

    /// <summary>Shared with the panel: the design's own metals, "As drawn" first.</summary>
    [ObservableProperty] private ObservableCollection<string> _materials;

    /// <summary>The loop-height expression, in the row's own unit. Empty means "as drawn".</summary>
    [ObservableProperty] private string _loopHeight = "";

    /// <summary>The wire-diameter expression. Empty means "as drawn".</summary>
    [ObservableProperty] private string _diameter = "";

    [ObservableProperty] private int _materialIndex;

    /// <summary>Reads this row's three parameters back off the component.</summary>
    internal void Pull()
    {
        _pulling = true;

        LoopHeight = _owner.ValueOfControl("LoopHeight_" + ArrayName);
        Diameter   = _owner.ValueOfControl("Diameter_" + ArrayName);

        string material = _owner.ValueOfControl("Material_" + ArrayName);
        int index = material.Length == 0
            ? 0
            : IndexOfMaterial(material);
        MaterialIndex = index < 0 ? 0 : index;

        _pulling = false;
    }

    private int IndexOfMaterial(string name)
    {
        for (int i = 0; i < Materials.Count; i++)
            if (Materials[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    partial void OnMaterialIndexChanged(int oldValue, int newValue)
    {
        if (_pulling) return;
        if (_owner.IsNewMaterialRow(newValue))
        {
            _owner.BeginNewWBondMaterial(() => { _pulling = true; MaterialIndex = oldValue; _pulling = false; });
            return;
        }
        _owner.SetWBondMaterial(
            "Material_" + ArrayName,
            newValue >= 1 && newValue < Materials.Count ? Materials[newValue] : "");
    }

    /// <summary>
    /// Commits the two length boxes. Called from the view on Enter or lost focus rather than per
    /// keystroke — a half-typed expression is routinely unparseable, and re-elaborating on every
    /// character would fight the user for the box exactly as the array-name box would.
    /// </summary>
    public void CommitLoopHeight() => _owner.SetWBondControlParameter("LoopHeight_" + ArrayName, LoopHeight);

    /// <inheritdoc cref="CommitLoopHeight"/>
    public void CommitDiameter() => _owner.SetWBondControlParameter("Diameter_" + ArrayName, Diameter);
}
