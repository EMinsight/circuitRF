// brief-em3d-51 R-em3d51-4 / -6 — what the editor does to a 3D view's names, headless: rename a VAR through the AST, delete
// (or inline) it, link and unlink it, promote it to a cell parameter, and the drag rule — which name a dragged dimension
// writes, and what value.
//
// NEVER STRING SUBSTITUTION (CLAUDE.md). A rename rewrites the IDENTIFIER TOKENS that the parser reads as a reference to
// the name — `ww` is a different token from `w`, and `w(…)` is a function call — and the result is parsed again to
// prove it references the new name and not the old one.
//
// THE DRAG RULE is measured, not derived (PCellHandleSolver's approach): the field's expression is evaluated with one
// name nudged, which gives its slope; a second nudge and a cross-nudge prove the expression is affine. A bare reference
// writes that name; an affine expression writes its first name (in reading order) and holds the others; anything else —
// `2*w*l`, where the slope in w depends on l — is refused at the grab.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.ThreeD;

/// <summary>Expressions as text, read through the tokenizer and the parser.</summary>
public static class C3dExpressionText
{
    /// <summary>The names <paramref name="expression"/> references, in reading order, each once. Empty when it does not parse.</summary>
    public static IReadOnlyList<string> Names(string expression)
    {
        HashSet<string> refs;
        try { refs = AstWalker.CollectRefs(Parser.Parse(expression)); }
        catch (ExpressionException) { return []; }
        var order = new List<string>();
        foreach (var t in RefTokens(expression))
            if (refs.Contains(t.Text) && !order.Contains(t.Text)) order.Add(t.Text);
        return order;
    }

    /// <summary>True when <paramref name="expression"/> references <paramref name="name"/>.</summary>
    public static bool References(string expression, string name) => Names(expression).Contains(name);

    /// <summary>
    /// <paramref name="expression"/> with every reference to <paramref name="from"/> renamed <paramref name="to"/> — by
    /// token, never by substring, and re-parsed to prove it. Unchanged when it does not reference the name.
    /// </summary>
    public static string Rename(string expression, string from, string to)
    {
        if (!References(expression, from)) return expression;
        var tokens = RefTokens(expression).Where(t => t.Text == from).OrderByDescending(t => t.Position).ToList();
        string result = expression;
        foreach (var t in tokens) result = result[..t.Position] + to + result[(t.Position + from.Length)..];
        var after = AstWalker.CollectRefs(Parser.Parse(result));
        if (after.Contains(from) || !after.Contains(to))
            throw new InvalidOperationException($"Renaming '{from}' to '{to}' in '{expression}' did not rename the reference.");
        return result;
    }

    /// <summary>
    /// <paramref name="expression"/> with probe <paramref name="from"/> renamed <paramref name="to"/> where a probe function reads
    /// it — <c>Tmax(from)</c>, <c>T(from)</c> — and nowhere else: a VAR or a measure that happens to share the name is not a
    /// probe. By token, never by pattern.
    /// </summary>
    public static string RenameProbe(string expression, string from, string to)
    {
        Token[] tokens;
        try { tokens = new Tokenizer(expression).Tokenize(); }
        catch (ExpressionException) { return expression; }
        string result = expression;
        for (int i = tokens.Length - 2; i >= 2; i--)
            if (tokens[i].Kind == TokenKind.Identifier && tokens[i].Text == from && tokens[i - 1].Kind == TokenKind.LParen &&
                tokens[i + 1].Kind == TokenKind.RParen && tokens[i - 2].Kind == TokenKind.Identifier &&
                C3dThermal.ProbeFunctions.Contains(tokens[i - 2].Text, StringComparer.Ordinal))
                result = result[..tokens[i].Position] + to + result[(tokens[i].Position + from.Length)..];
        return result;
    }

    /// <summary>Identifier tokens that are references — not a function's name (followed by '('), not a qualified part.</summary>
    private static IEnumerable<Token> RefTokens(string expression)
    {
        Token[] tokens;
        try { tokens = new Tokenizer(expression).Tokenize(); }
        catch (ExpressionException) { yield break; }
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Kind != TokenKind.Identifier) continue;
            if (i + 1 < tokens.Length && tokens[i + 1].Kind is TokenKind.LParen or TokenKind.Dot) continue;
            if (i > 0 && tokens[i - 1].Kind == TokenKind.Dot) continue;
            yield return tokens[i];
        }
    }

    /// <summary>A value as an expression's literal: the shortest spelling that reads back the same to 12 digits.</summary>
    public static string Number(double value)
    {
        string s = value.ToString("G12", CultureInfo.InvariantCulture);
        return s.Contains('E') ? value.ToString("R", CultureInfo.InvariantCulture) : s;
    }
}

/// <summary>One write a drag makes: a VAR's expression, or a cell parameter's default (in the <c>.ccell</c>).</summary>
/// <param name="Unit">The unit the value is written in: the name's own, or — for a name that had none but takes a unit
/// from what it referenced — the field's site unit, which is then written with it.</param>
public sealed record C3dWrite(string Name, bool Parameter, string Expression, string? Unit);

/// <summary>The drag rule's answer for one field: which name it writes, or why it is refused.</summary>
public sealed record C3dDragBinding(string? Name, bool Parameter, string? Refusal);

/// <summary>R-em3d51-6 — the drag rule, and the edits the Variables panel makes.</summary>
public static class C3dVariableEdits
{
    // ── the drag rule ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Which name a drag of this field would write — or the refusal, naming the field. The field is
    /// <paramref name="item"/>'s <paramref name="path"/>, holding <paramref name="e"/>.
    /// </summary>
    public static C3dDragBinding Classify(C3dResolution res, C3dCell cell, string item, string path, C3dExpr e, C3dFieldKind kind)
    {
        string where = $"'{item}' {path} holds {e.Expr}";
        Expr ast;
        try { ast = Parser.Parse(e.Expr); }
        catch (ExpressionException ex) { return new(null, false, $"{where}, which does not parse ({ex.Message}). Replace it with a number to drag it."); }
        var names = C3dExpressionText.Names(e.Expr);
        var writable = names.Where(n => Writable(res, n)).ToList();
        if (writable.Count == 0)
            return new(null, false, $"{where}, which names nothing a drag can write. Replace it with a number to drag it.");
        if (ast is RefExpr bare) return new(bare.Name, IsParameter(res, cell, bare.Name), null);

        double f0 = Measure(res, e, kind, null, 0);
        double Slope(string n) => Measure(res, e, kind, n, Step(res, n)) - f0;
        var slopes = writable.ToDictionary(n => n, Slope, StringComparer.Ordinal);
        var moving = writable.Where(n => Math.Abs(slopes[n]) > 1e-12 * Math.Max(1, Math.Abs(f0))).ToList();
        if (moving.Count == 0)
            return new(null, false, $"{where}, which does not change when any of its names does. Replace it with a number to drag it.");
        string solve = moving[0];
        bool affine = true;
        foreach (string n in moving)
        {
            double h = Step(res, n);
            double twice = Measure(res, e, kind, n, 2 * h) - f0;
            if (!Near(twice, 2 * slopes[n], f0)) { affine = false; break; }
            foreach (string m in moving.Where(m => m != n))
            {
                double both = MeasureTwo(res, e, kind, n, h, m, Step(res, m)) - f0;
                if (!Near(both, slopes[n] + slopes[m], f0)) { affine = false; break; }
            }
        }
        if (!affine)
            return new(null, false, $"{where}, which is not linear in one name, so no single value of {string.Join(" or ", moving)} " +
                                    "follows the drag. Replace it with a number to drag it, or change the names in the Variables panel.");
        return new(solve, IsParameter(res, cell, solve), null);
    }

    /// <summary>
    /// The write that puts field <paramref name="e"/> at <paramref name="targetSi"/> (base SI: metres, radians for an angle,
    /// a count): the name <see cref="Classify"/> chose, solved by its slope. Null with a refusal when it cannot.
    /// </summary>
    public static C3dWrite? Solve(C3dResolution res, C3dCell cell, C3dExpr e, C3dFieldKind kind, double targetSi, string siteUnit,
                                  out string? refusal, string item = "", string path = "")
    {
        var b = Classify(res, cell, item, path, e, kind);
        refusal = b.Refusal;
        if (b.Name is not { } name) return null;
        double f0 = Measure(res, e, kind, null, 0);
        double h = Step(res, name);
        double slope = (Measure(res, e, kind, name, h) - f0) / h;
        if (Math.Abs(slope) < 1e-300) { refusal = $"'{item}' {path} does not move when {name} does."; return null; }
        var n = res.Names[name];
        double x = (n.Value ?? 0) + (targetSi - f0) / slope;
        // The value in the name's own unit; a name that had none but TOOK one (the base-unit mark) is written in the site unit.
        string? unit = n.Unit is null ? null : n.Unit == C3dResolver.BaseUnit ? siteUnit : n.Unit;
        string? stored = b.Parameter ? cell.Parameter(name)?.Unit : null;
        if (b.Parameter && !string.IsNullOrEmpty(stored)) unit = C3dUnits.Engine(stored, out _) ?? unit;
        double scale = unit is null ? 1 : Units.Scale(C3dUnits.Engine(unit, out _) ?? unit) ?? 1;
        return new C3dWrite(name, b.Parameter, C3dExpressionText.Number(x / scale), unit);
    }

    private static bool Writable(C3dResolution res, string name)
        => res.Names.TryGetValue(name, out var n) && n.Value is not null && n.Source != C3dNameSource.Set;

    /// <summary>A bare parameter, or a VAR linked to one, writes the parameter.</summary>
    private static bool IsParameter(C3dResolution res, C3dCell cell, string name)
        => cell.Parameter(name) is not null && res.Names.TryGetValue(name, out var n) && n.Source is C3dNameSource.CellDefault or C3dNameSource.LinkedVar or C3dNameSource.Override;

    private static double Step(C3dResolution res, string name)
    {
        double v = res.Names[name].Value ?? 0;
        return Math.Abs(v) > 1e-15 ? Math.Abs(v) * 1e-3 : 1e-6;
    }

    private static bool Near(double a, double b, double scale) => Math.Abs(a - b) <= 1e-7 * Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1e-9 * Math.Max(1, Math.Abs(scale)));

    /// <summary>The field's value (base SI) with <paramref name="name"/> nudged by <paramref name="by"/>, every other name
    /// held at its resolved value — a flat mirror of the scope, with each name's own unit so var-unit-wins still applies.</summary>
    private static double Measure(C3dResolution res, C3dExpr e, C3dFieldKind kind, string? name, double by)
        => MeasureTwo(res, e, kind, name, by, null, 0);

    private static double MeasureTwo(C3dResolution res, C3dExpr e, C3dFieldKind kind, string? a, double da, string? b, double db)
    {
        var ev = new Evaluator();
        var scope = new Scope("flat");
        foreach (var n in res.Names.Values)
        {
            if (n.Value is not { } v) continue;
            if (n.Name == a) v += da;
            if (n.Name == b) v += db;
            scope.Bind(n.Name, "__resolved__", n.Unit);
            ev.InjectResolved("flat", n.Name, new Value(v));
        }
        string? unit = kind switch
        {
            C3dFieldKind.Count => null,
            C3dFieldKind.Angle => C3dUnits.Engine(e.Unit ?? C3dUnits.Degrees, out _),
            _ => C3dUnits.Engine(e.Unit, out _),
        };
        return ev.Eval(e.Expr, scope, unit).AsReal();
    }

    /// <summary>Writes <paramref name="w"/>: a VAR's expression (taking the write's unit when it had none), or the cell
    /// parameter's default in <paramref name="ccell"/>.</summary>
    public static string? Apply(C3dDocument doc, CcellFile? ccell, C3dWrite w)
    {
        if (w.Parameter)
        {
            if (ccell is null) return $"'{w.Name}' is a cell parameter, and this 3D view is in no cell.";
            return SetParameterDefault(ccell, w.Name, w.Expression, w.Unit);
        }
        if (doc.Variables.FirstOrDefault(v => v.Name == w.Name) is not { } var) return $"There is no VAR '{w.Name}'.";
        var.Expression = w.Expression;
        if (w.Unit is { } u && C3dUnits.Engine(var.Unit, out _) != u)
            var.Unit = Enum.GetValues<Layout.LayoutUnit>().FirstOrDefault(x => Layout.LayoutUnits.AsciiSuffix(x) == u) is var lu
                       && Layout.LayoutUnits.AsciiSuffix(lu) == u ? C3dUnits.Stored(lu) : u;
        return null;
    }

    // ── the Variables panel ────────────────────────────────────────────────────────────────────────

    /// <summary>What references <paramref name="name"/> in the document: fields (item, path), VARs and instance overrides.</summary>
    public static List<(string Item, string Path)> UsesOf(C3dDocument doc, string name)
    {
        var uses = new List<(string, string)>();
        foreach (var f in C3dBindings.Bound(doc))
            if (C3dExpressionText.References(f.Expr.Expr, name)) uses.Add((f.Item, f.Path));
        foreach (var v in doc.Variables)
            if (v.Name != name && C3dExpressionText.References(v.Expression, name)) uses.Add(($"VAR {v.Name}", "Expression"));
        foreach (var i in doc.Instances)
            foreach (var (p, e) in i.Params ?? [])
                if (C3dExpressionText.References(e.Expr, name)) uses.Add((i.Name, $"Params.{p}"));
        // brief-em3d-73 — the thermal content: a heat source's default power, and every expression of every thermal setup
        foreach (var h in doc.HeatSources)
            if (h.Power is { Length: > 0 } hp && C3dExpressionText.References(hp, name)) uses.Add((h.Name, "Power"));
        foreach (var (_, setup, t) in C3dThermal.ThermalSetups(doc))
        {
            foreach (var f in C3dThermal.ExpressionFields(t))
                if (C3dExpressionText.References(f.Text, name)) uses.Add(($"setup {setup.Name}", f.Path));
            foreach (var w in t.Sweep ?? [])
                if (w.Var == name) uses.Add(($"setup {setup.Name}", $"Sweep[{w.Var}].Var"));
        }
        return uses;
    }

    /// <summary>Renames VAR <paramref name="from"/> to <paramref name="to"/> and every reference to it in the document —
    /// fields, other VARs, instance overrides — through the tokenizer. The refusal, or null.</summary>
    public static string? Rename(C3dDocument doc, C3dCell cell, string from, string to)
    {
        if (from == to) return null;
        if (doc.Variables.FirstOrDefault(v => v.Name == from) is not { } var) return $"There is no VAR '{from}'.";
        if (C3dResolver.ValidateName(to) is { } bad) return $"'{to}': {bad}";
        if (doc.Variables.Any(v => v.Name == to)) return $"A VAR named '{to}' already exists.";
        if (cell.Parameter(to) is not null && var.Linked == false)
            return $"'{to}' is a parameter of this cell; a VAR of that name would be linked to it. Rename to another name, or promote.";
        foreach (var f in C3dBindings.Bound(doc).ToList())
        {
            string renamed = C3dExpressionText.Rename(f.Expr.Expr, from, to);
            if (renamed != f.Expr.Expr) C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, f.Expr with { Expr = renamed });
        }
        foreach (var v in doc.Variables) v.Expression = C3dExpressionText.Rename(v.Expression, from, to);
        foreach (var i in doc.Instances)
            if (i.Params is { } ps)
                foreach (string k in ps.Keys.ToList()) ps[k] = ps[k] with { Expr = C3dExpressionText.Rename(ps[k].Expr, from, to) };
        // the thermal content, through the same tokenizer: a heat source's power, and each thermal setup rewritten in place
        foreach (var h in doc.HeatSources)
            if (h.Power is { Length: > 0 } hp) h.Power = C3dExpressionText.Rename(hp, from, to);
        foreach (var (index, setup, t) in C3dThermal.ThermalSetups(doc).ToList())
        {
            bool changed = false;
            foreach (var f in C3dThermal.ExpressionFields(t).ToList())
                if (C3dExpressionText.Rename(f.Text, from, to) is var renamed && renamed != f.Text) { f.Set(renamed); changed = true; }
            foreach (var w in t.Sweep ?? [])
                if (w.Var == from) { w.Var = to; changed = true; }
            if (changed) doc.Setups[index] = Layout.Em.EmSetupPersistence.ToEmbedded(setup);
        }
        var.Name = to;
        if (cell.Parameter(to) is not null) var.Linked = true;
        return null;
    }

    /// <summary>Deletes VAR <paramref name="name"/> — refused while anything uses it, listing what does.</summary>
    public static string? Delete(C3dDocument doc, string name)
    {
        if (DeleteRefusal(doc, name) is { } refusal) return refusal;
        doc.Variables.RemoveAll(v => v.Name == name);
        return null;
    }

    /// <summary>Why <see cref="Delete"/> would refuse VAR <paramref name="name"/> — what uses it — or null when it would not.
    /// Asked before the gesture, so a Delete that cannot act says why before it is pressed.</summary>
    public static string? DeleteRefusal(C3dDocument doc, string name)
    {
        var uses = UsesOf(doc, name);
        return uses.Count > 0
            ? $"VAR '{name}' is used by {Describe(uses)}. Inline its value to write numbers into those fields, or change them first."
            : null;
    }

    /// <summary>
    /// Delete's alternative: every FIELD that references <paramref name="name"/> becomes the number it resolves to now,
    /// then the VAR is deleted. Refused while a VAR or an override references it (a number there would drop its unit).
    /// The document must be resolved.
    /// </summary>
    public static string? InlineAndDelete(C3dDocument doc, string name)
    {
        // only a dimension FIELD can take the number in place; anything else that reads the VAR (another VAR, an override, a
        // heat source's power or a thermal setup's expression) keeps it
        var fields = C3dBindings.Bound(doc).Where(f => C3dExpressionText.References(f.Expr.Expr, name)).Select(f => (f.Item, f.Path)).ToHashSet();
        var other = UsesOf(doc, name).Where(u => !fields.Contains(u)).ToList();
        if (other.Count > 0) return $"VAR '{name}' is also used by {Describe(other)}, which cannot hold a number in its place.";
        foreach (var f in C3dBindings.Bound(doc).ToList())
            if (C3dExpressionText.References(f.Expr.Expr, name)) C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, null);
        doc.Variables.RemoveAll(v => v.Name == name);
        return null;
    }

    /// <summary>Links (or unlinks) a VAR to the cell parameter of its name.</summary>
    public static string? SetLinked(C3dDocument doc, C3dCell cell, string name, bool linked)
    {
        if (doc.Variables.FirstOrDefault(v => v.Name == name) is not { } v) return $"There is no VAR '{name}'.";
        if (cell.Parameter(name) is null) return $"The cell has no parameter '{name}' to link to.";
        v.Linked = linked ? true : false;
        return null;
    }

    /// <summary>
    /// R-em3d51-2d — Promote to Cell Parameter: a <c>.ccell</c> parameter with the VAR's name, its expression as the default
    /// and its unit, and the VAR linked. Values agree by construction.
    /// </summary>
    public static string? Promote(C3dDocument doc, CcellFile ccell, string name)
    {
        if (doc.Variables.FirstOrDefault(v => v.Name == name) is not { } v) return $"There is no VAR '{name}'.";
        if (ccell.Parameters.Any(p => p.Name == name)) return $"The cell already has a parameter '{name}'; link the VAR to it instead.";
        string? unit = C3dUnits.Engine(v.Unit, out _);
        ccell.Parameters.Add(new CcellParameter
        {
            Name = name,
            DefaultExpression = v.Expression,
            Unit = unit ?? "",
            Dimension = unit is null ? UnitDimension.None : unit is "deg" or "rad" ? UnitDimension.Angle : UnitDimension.Length,
            ShowOnSchematic = true,
        });
        v.Linked = true;
        return null;
    }

    /// <summary>R-em3d51-2c — a linked VAR's value edited: the PARAMETER's <c>.ccell</c> default is written.</summary>
    public static string? SetParameterDefault(CcellFile ccell, string name, string expression, string? unit)
    {
        if (ccell.Parameters.FirstOrDefault(p => p.Name == name) is not { } p) return $"The cell has no parameter '{name}'.";
        p.DefaultExpression = expression;
        if (unit is not null) p.Unit = C3dUnits.Engine(unit, out _) ?? unit;
        return null;
    }

    private static string Describe(List<(string Item, string Path)> uses)
        => string.Join(", ", uses.Take(6).Select(u => u.Item.StartsWith("VAR ", StringComparison.Ordinal) ? u.Item : $"'{u.Item}' {u.Path}"))
           + (uses.Count > 6 ? $" and {uses.Count - 6} more" : "");
}
