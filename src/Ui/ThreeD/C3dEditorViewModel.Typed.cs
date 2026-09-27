// brief-em3d-51 R-em3d51-3 — typing an expression as a dimension, and defining a name on the spot.
//
// THE FIELD PARSES IN ORDER (R-em3d51-3a): `number [unit]` is exact DBU, as before; otherwise `expression [unit]` goes to
// the expression engine — a trailing length unit is the SITE unit, as on a .cnl line, and with none the display unit is.
// The value is the document's own resolution (C3dResolution.Evaluate), so what the field shows, what the tool receives
// and what elaboration later computes are one computation.
//
// AN UNKNOWN NAME PAUSES THE GESTURE, it does not end it (R-em3d51-3c): Enter opens the Define strip beneath the field, a
// row per unknown name; its Enter defines them all and takes the step, and the definitions and the object the gesture
// then makes are ONE undo entry (C3dGroupEdit). Esc returns to the field with the text intact.

using System.Collections.ObjectModel;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One row of the Define strip: a name the typed expression uses and nothing defines.</summary>
public sealed partial class C3dDefineRow : ObservableObject
{
    public required string Name { get; init; }
    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private bool _asParameter;
    [ObservableProperty] private string? _error;
    public bool CanBeParameter { get; init; }
}

public sealed partial class C3dEditorViewModel
{
    /// <summary>The expressions typed in this gesture, waiting for the object they size: the step and dimension they were
    /// typed at, the expression, and its value in base SI.</summary>
    private readonly List<(int Step, int Dim, C3dExpr Expr, double Si)> _typedExpressions = [];

    /// <summary>R-em3d51-3b — the live preview beside the field: <c>= 0.254 mm</c>, <c>unknown: w</c>.</summary>
    [ObservableProperty] private string _fieldPreview = "";

    /// <summary>The values the field previews, for the rubber band (null where it says nothing).</summary>
    private long?[]? _fieldValues;

    public ObservableCollection<C3dDefineRow> DefineRows { get; } = [];
    [ObservableProperty] private bool _defineOpen;
    [ObservableProperty] private string? _defineError;

    /// <summary>What one typed dimension says.</summary>
    private readonly record struct TypedValue(long? Dbu, C3dExpr? Expr, double? Si, IReadOnlyList<string> Unknown, string? Error);

    /// <summary>The units a Define row offers.</summary>
    public static IReadOnlyList<string> DefineUnits { get; } = Enum.GetNames<LayoutUnit>();

    /// <summary>R-em3d51-3a — one dimension's text: a number (exact DBU), or an expression evaluated in the document's scope.</summary>
    private TypedValue ParseTyped(C3dDrawTool tool, int i, string text)
    {
        var d = tool.ParseField(i, text, Document.DisplayUnit, Document.DbuPerMicron);
        if (d.Kind == C3dDimensionKind.Value) return new TypedValue(d.Dbu, null, null, [], null);
        if (d.Kind == C3dDimensionKind.Invalid) return new TypedValue(null, null, null, [], d.Why);
        var (expr, unit) = SplitUnit(text.Trim(), Document.DisplayUnit);
        var res = Resolution;
        var unknown = C3dExpressionText.Names(expr).Where(n => !res.IsDefined(n)).ToList();
        if (unknown.Count > 0) return new TypedValue(null, null, null, unknown, null);
        try
        {
            string stored = C3dUnits.Stored(unit);
            var v = res.Evaluate(expr, stored);
            if (v.Kind != ValueKind.Real) return new TypedValue(null, null, null, [], $"'{expr}' is {v.Kind.ToString().ToLowerInvariant()}; a length is real.");
            double si = v.AsReal();
            double raw = si * 1e6 * Document.DbuPerMicron;
            if (!double.IsFinite(raw) || Math.Abs(raw) > long.MaxValue / 4.0) return new TypedValue(null, null, null, [], $"'{expr}' is too large to be a length.");
            return new TypedValue((long)Math.Round(raw, MidpointRounding.AwayFromZero), new C3dExpr(expr, stored), si, [], null);
        }
        catch (ExpressionException ex) { return new TypedValue(null, null, null, [], ex.Message); }
    }

    /// <summary><c>2*w mil</c> → (<c>2*w</c>, mil); with no trailing unit, the display unit.</summary>
    public static (string Expr, LayoutUnit Unit) SplitUnit(string text, LayoutUnit display)
    {
        int space = text.LastIndexOf(' ');
        if (space > 0)
        {
            string tail = text[(space + 1)..].Trim().ToLowerInvariant();
            LayoutUnit? unit = tail switch
            {
                "nm" => LayoutUnit.Nm, "u" or "um" or "µm" or "μm" => LayoutUnit.Um, "mm" => LayoutUnit.Mm,
                "mil" => LayoutUnit.Mil, "in" or "inch" => LayoutUnit.Inch, _ => null,
            };
            if (unit is { } u && text[..space].Trim().Length > 0) return (text[..space].Trim(), u);
        }
        return (text, display);
    }

    /// <summary>The preview for the field's current text (R-em3d51-3b): the value in the display unit with the honest
    /// <c>=</c>/<c>≈</c>, <c>unknown: name</c>, or the engine's message — never a blank for an expression — and, where the
    /// dimension lands in no named field, that it is evaluated once.</summary>
    private void UpdateFieldPreview()
    {
        _fieldValues = null;
        FieldPreview = "";
        if (!FieldOpen || _tool is not { } tool || _fieldIndex >= _fieldTexts.Length) return;
        var values = new long?[_fieldTexts.Length];
        for (int i = 0; i < _fieldTexts.Length; i++)
        {
            string t = i == _fieldIndex ? FieldText : _fieldTexts[i];
            if (t.Trim().Length == 0) continue;
            var p = ParseTyped(tool, i, t);
            values[i] = p.Dbu;
            if (i != _fieldIndex) continue;
            if (p.Unknown.Count > 0) FieldPreview = "unknown: " + string.Join(", ", p.Unknown);
            else if (p.Error is { } e) FieldPreview = p.Expr is null && tool.ParseField(i, t, Document.DisplayUnit, Document.DbuPerMicron).Kind == C3dDimensionKind.Invalid ? "" : e;
            else if (p.Expr is { } ex && p.Si is { } si)
            {
                string unit = LayoutUnits.AsciiSuffix(Document.DisplayUnit);
                double inDisplay = si / (Units.Scale(unit) ?? 1);
                FieldPreview = AnalysisPreviewHelper.FormatValueHonest(new Value(inDisplay)) + " " + LayoutUnits.Suffix(Document.DisplayUnit);
                if (Math.Abs(si) > C3dResolver.LargeMetres)
                    FieldPreview += $" — above 1 m: a literal beside a name with its own unit is METRES";
                if (tool.FieldFor(tool.Step, i) is null)
                    FieldPreview += " — " + (tool.Kind is C3dToolKind.Polygon or C3dToolKind.Polyline ? "outline points hold numbers — evaluated once" : "this holds a number — evaluated once");
                _ = ex;
            }
        }
        _fieldValues = values;
        Viewer.RequestFrame();
    }

    /// <summary>The field's Enter, through expressions (R-em3d51-3a/c). Returns false when it stopped (an error shown, or
    /// the Define strip opened).</summary>
    private bool TypedEnter(C3dDrawTool tool)
    {
        var values = new long?[_fieldTexts.Length];
        var exprs = new List<(int Dim, C3dExpr Expr, double Si)>();
        var unknown = new List<(string Name, int Dim, bool Bare)>();
        for (int i = 0; i < _fieldTexts.Length; i++)
        {
            string text = _fieldTexts[i].Trim();
            if (text.Length == 0) continue;                                  // left empty: the cursor's value
            var p = ParseTyped(tool, i, text);
            if (p.Unknown.Count > 0)
            {
                foreach (string n in p.Unknown)
                    if (!unknown.Any(u => u.Name == n)) unknown.Add((n, i, SplitUnit(text, Document.DisplayUnit).Expr == n));
                continue;
            }
            if (p.Error is { } why)
            {
                _fieldIndex = i;
                ShowFieldIndex();
                FieldError = why;
                return false;
            }
            values[i] = p.Dbu;
            if (p.Expr is { } e && p.Si is { } si) exprs.Add((i, e, si));
        }
        if (unknown.Count > 0) { OpenDefine(tool, unknown); return false; }

        int stepBefore = tool.Step;
        var step = tool.Typed(values, CursorInput());
        if (!step.Advanced && step.Refusal is { } refusal) { FieldError = refusal; return false; }
        foreach (var (dim, e, si) in exprs) _typedExpressions.Add((stepBefore, dim, e, si));
        CloseField();
        Apply(step);
        return true;
    }

    /// <summary>The finished object with every expression typed in this gesture bound to its field; the ones that land in no
    /// named field were evaluated once, and the status line says so.</summary>
    private C3dObject BindTyped(C3dDrawTool tool, C3dObject obj)
    {
        if (_typedExpressions.Count == 0) return obj;
        var once = new List<string>();
        foreach (var (step, dim, e, si) in _typedExpressions)
        {
            if (tool.FieldFor(step, dim) is not { } path || C3dBindings.Find(obj, path) is not { } f) { once.Add(e.Expr); continue; }
            var bound = si < 0 && tool.FieldIsMagnitude(step, dim) ? e with { Expr = Negated(e.Expr) } : e;
            C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, bound);
        }
        _typedExpressions.Clear();
        if (once.Count > 0) _evaluatedOnce = $" {string.Join(", ", once)} evaluated once: that dimension holds a number.";
        return obj;
    }

    private string _evaluatedOnce = "";

    /// <summary>The expression negated: <c>-w</c> becomes <c>w</c>, anything else <c>-(…)</c>.</summary>
    private static string Negated(string expr)
    {
        try { if (Parser.Parse(expr) is UnaryExpr { Op: "-", Operand: RefExpr r }) return r.Name; }
        catch (ExpressionException) { }
        return $"-({expr})";
    }

    private static bool Parses(string text)
    {
        try { Parser.Parse(text); return true; }
        catch (ExpressionException) { return false; }
    }

    // ── the Array panel (brief 46's; R-em3d51-3a: anywhere a dimension is typed) ──────────────

    /// <summary>The Array panel's counts and pitches: numbers, or expressions in the document's scope — a count must come
    /// out a whole number of at least 1. <paramref name="exprs"/> are the fields typed as expressions (bound when the
    /// panel acts on one instance's own array; copies take the numbers, evaluated once).</summary>
    private bool ArrayValues(out int[] n, out long[] pitch, out List<(string Field, int K, C3dExpr Expr)> exprs, out string? why)
    {
        n = new int[3];
        pitch = new long[3];
        exprs = [];
        why = null;
        var res = Resolution;
        string[] counts = [ArrayCountX, ArrayCountY, ArrayCountZ];
        for (int k = 0; k < 3; k++)
        {
            string t = counts[k].Trim();
            if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n[k]) && n[k] >= 1) continue;
            if (!Parses(t) || t.Length == 0) { why = $"A count is a whole number, at least 1 — '{counts[k]}' is not."; return false; }
            try
            {
                double v = res.Evaluate(t, null).AsReal();
                if (Math.Abs(v - Math.Round(v)) > 1e-9 || v < 1) { why = $"'{t}' is {v:G6}; a count is a whole number of at least 1, and is never rounded."; return false; }
                n[k] = (int)Math.Round(v);
                exprs.Add((nameof(C3dArray.Counts), k, new C3dExpr(t)));
            }
            catch (Exception ex) when (ex is ExpressionException or InvalidCastException) { why = $"'{t}': {ex.Message}"; return false; }
        }
        string[] pitches = [ArrayPitchX, ArrayPitchY, ArrayPitchZ];
        for (int k = 0; k < 3; k++)
        {
            string t = pitches[k].Trim();
            if (n[k] == 1 && t.Length == 0) continue;
            var d = C3dDimension.Parse(t, Document.DisplayUnit, Document.DbuPerMicron);
            if (d.Kind == C3dDimensionKind.Value) { pitch[k] = d.Dbu; continue; }
            if (d.Kind == C3dDimensionKind.Invalid) { why = d.Why; return false; }
            var (expr, unit) = SplitUnit(t, Document.DisplayUnit);
            try
            {
                double si = res.Evaluate(expr, C3dUnits.Stored(unit)).AsReal();
                pitch[k] = (long)Math.Round(si * 1e6 * Document.DbuPerMicron, MidpointRounding.AwayFromZero);
                exprs.Add((nameof(C3dArray.Pitch), k, new C3dExpr(expr, C3dUnits.Stored(unit))));
            }
            catch (UnresolvedNameException u) { why = $"unknown: {u.Name}"; return false; }
            catch (Exception ex) when (ex is ExpressionException or InvalidCastException) { why = $"'{t}': {ex.Message}"; return false; }
        }
        return true;
    }

    // ── the Define strip (R-em3d51-3c) ─────────────────────────────────────────────────────────

    private void OpenDefine(C3dDrawTool tool, List<(string Name, int Dim, bool Bare)> unknown)
    {
        DefineRows.Clear();
        var current = tool.Current(CursorInput());
        bool inCell = Cell.InCell;
        foreach (var (name, dim, bare) in unknown)
        {
            string value = bare && dim < current.Length && current[dim] is { } v
                ? C3dDimension.Spell(Math.Abs(v), Document.DisplayUnit, Document.DbuPerMicron) : "";
            DefineRows.Add(new C3dDefineRow { Name = name, ValueText = value, Unit = C3dUnits.Stored(Document.DisplayUnit), CanBeParameter = inCell });
        }
        DefineError = null;
        DefineOpen = true;
    }

    /// <summary>The strip's Enter: every row defined — a VAR in this 3D view, or a cell parameter — and the step taken, as
    /// one undo entry with the object the gesture makes.</summary>
    public void DefineEnter()
    {
        if (!DefineOpen || _tool is not { } tool) return;
        foreach (var r in DefineRows)
        {
            r.Error = C3dResolver.ValidateName(r.Name) is { } bad ? bad
                    : r.ValueText.Trim().Length == 0 ? "Type its value."
                    : !Parses(r.ValueText.Trim()) ? $"'{r.ValueText}' is not a value." : null;
        }
        if (DefineRows.Any(r => r.Error is not null)) return;
        BeginGroup($"Define {string.Join(", ", DefineRows.Select(r => r.Name))}");
        string? refusal = EditNames($"Define {string.Join(", ", DefineRows.Select(r => r.Name))}", (doc, ccell) =>
        {
            foreach (var r in DefineRows)
            {
                string value = r.ValueText.Trim();
                if (r.AsParameter)
                {
                    if (ccell is null) return $"'{r.Name}': this 3D view is in no cell, so it has no parameters.";
                    if (ccell.Parameters.Any(p => p.Name == r.Name)) return $"The cell already has a parameter '{r.Name}'.";
                    string? engine = C3dUnits.Engine(r.Unit, out _);
                    ccell.Parameters.Add(new CcellParameter
                    {
                        Name = r.Name, DefaultExpression = value, Unit = engine ?? "",
                        Dimension = engine is null ? UnitDimension.None : UnitDimension.Length, ShowOnSchematic = true,
                    });
                }
                else doc.Variables.Add(new C3dVariable { Name = r.Name, Expression = value, Unit = r.Unit.Length == 0 ? null : r.Unit });
            }
            return null;
        });
        if (refusal is not null) { DefineError = refusal; EndGroup(); return; }
        DefineOpen = false;
        DefineRows.Clear();
        TypedEnter(tool);          // the group ends when the gesture makes its object (Apply), or is cancelled
    }

    /// <summary>The strip's Esc: back to the field, its text intact.</summary>
    public void DefineEscape()
    {
        DefineOpen = false;
        DefineRows.Clear();
        FieldFocusRequested?.Invoke();
    }
}
