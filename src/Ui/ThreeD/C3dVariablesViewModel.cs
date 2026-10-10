// brief-em3d-51 R-em3d51-4a — the Variables panel: the 3D view's VARs, and its cell's parameters below them, read-only.
//
// Every action is ONE undo entry (C3dEditorViewModel.EditNames), and each is the headless function the CLI's reader
// would agree with (C3dVariableEdits): a rename rewrites every reference through the tokenizer, never by substring; a
// delete of a VAR in use is refused, listing what uses it, with Inline as the alternative; a LINKED VAR's value is the
// cell parameter's, so editing it writes the .ccell default — one number, nothing to keep in step.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One row of the panel: a VAR, or (read-only) a cell parameter.</summary>
public sealed partial class C3dVariableRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Expression { get; init; }
    public string? Unit { get; init; }
    public required string ValueText { get; init; }
    public required string LinkText { get; init; }
    public int Uses { get; init; }
    public bool IsParameter { get; init; }
    public bool IsLinked { get; init; }
    public bool CanLink { get; init; }
    public bool CanUnlink { get; init; }
    public bool CanPromote { get; init; }
    public string? Error { get; init; }

    /// <summary>3D editor bugs round 6 — why Delete would be refused (what uses the VAR), or null. The button is disabled
    /// and says this as its tooltip: a refusal written below the panel's rows went unseen when they scrolled.</summary>
    public string? DeleteRefusal { get; init; }
    public bool CanDelete => DeleteRefusal is null;
    public string DeleteTip => DeleteRefusal ?? "Delete";

    /// <summary>The edit fields, prefilled with what the row holds.</summary>
    [ObservableProperty] private string _editExpression = "";
    [ObservableProperty] private string _editUnit = "";
    [ObservableProperty] private string _editName = "";

    public string UsesText => Uses == 1 ? "1 field" : $"{Uses} fields";
}

public sealed partial class C3dVariablesViewModel(C3dEditorViewModel editor) : ObservableObject
{
    /// <summary>The units a VAR may carry: none (it takes the units of what it references), a length, or degrees.</summary>
    public static IReadOnlyList<string> UnitChoices { get; } = ["", .. Enum.GetNames<LayoutUnit>(), C3dUnits.Degrees];

    public ObservableCollection<C3dVariableRow> Rows { get; } = [];
    public ObservableCollection<C3dVariableRow> Parameters { get; } = [];

    [ObservableProperty] private C3dVariableRow? _selected;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newExpression = "";
    [ObservableProperty] private string _newUnit = "";

    /// <summary>Names a dimension uses that nothing defines (<c>Used but not defined: mySpan</c>), or empty. The first of them
    /// is offered in the Add row's name while that is empty, so defining it is a value and Enter.</summary>
    [ObservableProperty] private string _unknownText = "";

    /// <summary>Whether the view is in a cell: Promote and Link need one.</summary>
    public bool InCell => editor.Cell.InCell;

    public string Heading => InCell
        ? $"VARs of this 3D view, and the cell's parameters ({editor.Cell.Parameters.Count})"
        : "VARs of this 3D view (a loose 3D view has no cell parameters)";

    /// <summary>Re-reads the document's names.</summary>
    public void Reload()
    {
        string? keep = Selected?.Name;
        var res = editor.Resolution;
        var cell = editor.Cell;
        Rows.Clear();
        foreach (var v in editor.Document.Variables)
        {
            var param = cell.Parameter(v.Name);
            bool linked = param is not null && v.Linked != false;
            res.Names.TryGetValue(v.Name, out var n);
            string value = n is null ? "" : ValueOf(n);
            Rows.Add(new C3dVariableRow
            {
                Name = v.Name,
                Expression = v.Expression,
                Unit = v.Unit,
                ValueText = value,
                LinkText = linked ? $"{v.Name} ← cell parameter {value}"
                         : param is not null ? $"unlinked: hides cell parameter '{v.Name}'" : "",
                Uses = res.Uses.TryGetValue(v.Name, out var u) ? u.Count : 0,
                IsLinked = linked,
                CanLink = param is not null && !linked,
                CanUnlink = linked,
                CanPromote = cell.InCell && param is null,
                Error = n?.Error,
                DeleteRefusal = C3dVariableEdits.DeleteRefusal(editor.Document, v.Name),
                EditExpression = linked ? param!.DefaultExpression : v.Expression,
                EditUnit = linked ? param!.Unit : v.Unit ?? "",
                EditName = v.Name,
            });
        }
        Parameters.Clear();
        foreach (var p in cell.Parameters)
        {
            res.Names.TryGetValue(p.Name, out var n);
            Parameters.Add(new C3dVariableRow
            {
                Name = p.Name,
                Expression = p.DefaultExpression,
                Unit = p.Unit,
                ValueText = n is null ? "" : ValueOf(n),
                LinkText = editor.Document.Variables.Any(v => v.Name == p.Name) ? "has a VAR of its name" : "",
                Uses = res.Uses.TryGetValue(p.Name, out var u) ? u.Count : 0,
                IsParameter = true,
                Error = n?.Error,
            });
        }
        Selected = Rows.FirstOrDefault(r => r.Name == keep);
        UnknownText = res.Unknown.Count == 0 ? "" : $"Used but not defined: {string.Join(", ", res.Unknown)}";
        if (NewName.Length == 0 && res.Unknown.Count > 0) NewName = res.Unknown.First();
        OnPropertyChanged(nameof(InCell));
        OnPropertyChanged(nameof(Heading));
    }

    /// <summary>A resolved name in the display unit: <c>= 0.254 mm</c>, <c>= 45°</c>, or a plain number.</summary>
    private string ValueOf(C3dName n)
    {
        if (n.Error is { } e) return "unresolved: " + e;
        if (n.Value is not { } v) return "";
        if (n.Unit is "deg" or "rad") return "= " + (v * 180 / Math.PI).ToString("0.######", CultureInfo.InvariantCulture) + "°";
        if (n.Unit is null) return "= " + v.ToString("G6", CultureInfo.InvariantCulture);
        return "= " + C3dUnits.Spell(v, editor.Document.DisplayUnit);
    }

    private void Done(string? refusal)
    {
        Error = refusal ?? "";
        if (refusal is null) Reload();
    }

    // ── actions ───────────────────────────────────────────────────────────────────────────────

    public void Add()
    {
        string name = NewName.Trim();
        string expression = NewExpression.Trim();
        string? unit = NewUnit.Length == 0 ? null : NewUnit;
        Done(editor.EditNames($"Add VAR {name}", (doc, ccell) =>
        {
            if (C3dResolver.ValidateName(name) is { } bad) return $"'{name}': {bad}";
            if (doc.Variables.Any(v => v.Name == name)) return $"A VAR named '{name}' already exists.";
            if (expression.Length == 0) return "Type the VAR's value or expression.";
            doc.Variables.Add(new C3dVariable
            {
                Name = name, Expression = expression, Unit = unit,
                Linked = ccell?.Parameters.Any(p => p.Name == name) == true ? true : null,
            });
            return null;
        }));
        if (Error.Length == 0) { NewName = ""; NewExpression = ""; }
    }

    /// <summary>The row's value edited. A linked VAR writes the cell parameter's default (R-em3d51-2c).</summary>
    public void Edit(C3dVariableRow row)
    {
        string expression = row.EditExpression.Trim();
        string? unit = row.EditUnit.Length == 0 ? null : row.EditUnit;
        if (expression.Length == 0) { Error = "A VAR needs a value or an expression."; return; }
        Done(editor.EditNames($"Set {row.Name} = {expression}", (doc, ccell) =>
        {
            if (row.IsLinked)
                return ccell is null ? "This 3D view is in no cell." : C3dVariableEdits.SetParameterDefault(ccell, row.Name, expression, unit);
            if (doc.Variables.FirstOrDefault(v => v.Name == row.Name) is not { } v) return $"There is no VAR '{row.Name}'.";
            v.Expression = expression;
            v.Unit = unit;
            return null;
        }));
    }

    public void Rename(C3dVariableRow row)
    {
        string to = row.EditName.Trim();
        var cell = editor.Cell;
        Done(editor.EditNames($"Rename VAR {row.Name} to {to}", (doc, _) => C3dVariableEdits.Rename(doc, cell, row.Name, to)));
    }

    public void Delete(C3dVariableRow row)
        => Done(editor.EditNames($"Delete VAR {row.Name}", (doc, _) => C3dVariableEdits.Delete(doc, row.Name)));

    /// <summary>Delete's alternative for a VAR in use: its fields hold the numbers they resolve to now.</summary>
    public void InlineAndDelete(C3dVariableRow row)
    {
        var cell = editor.Cell;
        Done(editor.EditNames($"Inline and delete VAR {row.Name}", (doc, _) =>
        {
            C3dResolver.Resolve(doc, cell);
            return C3dVariableEdits.InlineAndDelete(doc, row.Name);
        }));
    }

    public void Link(C3dVariableRow row, bool linked)
    {
        var cell = editor.Cell;
        Done(editor.EditNames($"{(linked ? "Link" : "Unlink")} VAR {row.Name}", (doc, _) => C3dVariableEdits.SetLinked(doc, cell, row.Name, linked)));
    }

    /// <summary>R-em3d51-2d — Promote to Cell Parameter: the <c>.ccell</c> parameter written and the VAR linked, one entry.</summary>
    public void Promote(C3dVariableRow row)
        => Done(editor.EditNames($"Promote {row.Name} to a cell parameter", (doc, ccell) =>
               ccell is null ? "This 3D view is in no cell, so it has no parameters to promote to." : C3dVariableEdits.Promote(doc, ccell, row.Name)));
}
