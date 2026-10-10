// brief-em3d-132 R-em3d132-4 — the pure functions a wire's and a polyline's bound points share: renumbering on insert and
// remove, the offset writer (overview D2, R-em3d131-2) and the swap writer (D4, R-em3d131-5).
//
// DECIDED ON THE AST, SPLICED AT TOKEN POSITIONS. The engine has no printer that gives text back as it was typed —
// FreqDeferral.Render parenthesises every node, so `x_pad + 50um` would come back `(x_pad+50um)`, and a move followed by
// its inverse could never read back as the original text. So each writer asks the PARSED expression what it is (an
// addition whose right operand is a literal, a negation, an atom), and then rewrites only the tokens that decision names,
// leaving every other character as the user typed it. The result is re-parsed before it is returned: text that does not
// parse is never written.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;

namespace CircuitRF.Design.ThreeD;

public static class C3dPointExpressions
{
    /// <summary>
    /// An inserted or removed point carries every later point's expressions with it (overview R-em3d131-4): the entries
    /// of <paramref name="property"/> (<c>Points</c>, <c>Points3</c>) keyed <c>P[k]</c> at index <paramref name="from"/> and
    /// above move up one (an insert) or down one (a remove, whose own point <paramref name="removed"/> loses its entries).
    /// False — nothing changed — for a key of that property in any other shape, which this cannot renumber and so must not
    /// guess at. Another property's keys (<c>Points3[…]</c> when renumbering <c>Points</c>) are left alone.
    /// </summary>
    public static bool RenumberPointExpressions(IC3dBindable owner, string property, int from, int? removed)
    {
        if (owner.Exprs is not { } map) return true;
        var moved = new Dictionary<string, C3dExpr?[]>(StringComparer.Ordinal);
        foreach (var (key, slots) in map)
        {
            bool ours = key.StartsWith(property, StringComparison.Ordinal)
                        && (key.Length == property.Length || key[property.Length] is '[' or '.');
            if (!ours) { moved[key] = slots; continue; }
            int close = key.IndexOf(']', StringComparison.Ordinal);
            if (key.Length <= property.Length || key[property.Length] != '[' || close < 0 ||
                !int.TryParse(key.AsSpan(property.Length + 1, close - property.Length - 1), NumberStyles.None,
                              CultureInfo.InvariantCulture, out int k))
                return false;
            if (k == removed) continue;
            int to = k < from ? k : removed is null ? k + 1 : k - 1;
            moved[$"{property}[{to}]{key[(close + 1)..]}"] = slots;
        }
        owner.Exprs = moved.Count == 0 ? null : moved;
        return true;
    }

    // ── the offset writer (D2) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="expr"/> moved by <paramref name="offsetDbu"/> (overview R-em3d131-2): the offset is added at the top
    /// level as a <c>+</c> or <c>-</c> term in <paramref name="unit"/> with its suffix, or folded into a trailing
    /// <c>+</c>/<c>-</c> literal, which a fold to zero drops. A zero offset returns <paramref name="expr"/> itself.
    /// </summary>
    public static C3dExpr OffsetExpression(C3dExpr expr, long offsetDbu, LayoutUnit unit, int dbuPerMicron)
        => offsetDbu == 0 ? expr : expr with { Expr = Offset(expr.Expr, SiteUnit(expr.Unit), offsetDbu, unit, dbuPerMicron) };

    /// <summary>
    /// The swap writer (overview R-em3d131-5): <c>s·(expr) + c</c> for a quarter-turn or a mirror. <paramref name="sign"/>
    /// +1 is the offset writer alone; -1 negates first — a trailing literal stays outside with its sign flipped
    /// (<c>t + 5um</c> → <c>-(t) - 5um</c>), a negation is cancelled (<c>-(e)</c> → <c>e</c>), a literal takes the sign
    /// (<c>5um</c> → <c>-5um</c>), and anything else is wrapped (<c>-(ey)</c>).
    /// </summary>
    public static C3dExpr SwapExpression(C3dExpr expr, int sign, long offsetDbu, LayoutUnit unit, int dbuPerMicron)
    {
        if (sign is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(sign), sign, "A swap's sign is +1 or -1.");
        var turned = sign == 1 ? expr : expr with { Expr = Proved(Negate(expr.Expr)) };
        return OffsetExpression(turned, offsetDbu, unit, dbuPerMicron);
    }

    private static string Offset(string text, LayoutUnit? site, long offsetDbu, LayoutUnit unit, int dbuPerMicron)
    {
        var (ast, tokens) = Read(text);
        if (TrailingLiteral(ast, tokens) is var (op, lit))
        {
            var litUnit = lit.Kind == TokenKind.Quantity ? LengthUnit(lit.Unit) : site;
            string number = lit.Kind == TokenKind.Quantity ? lit.Text[..lit.UnitStart] : lit.Text;
            if (litUnit is { } lu && decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
            {
                decimal perUnit = LayoutUnits.ToDbu(1, lu, 1000) * (decimal)dbuPerMicron / 1000m;   // nm per unit, exact
                decimal total = (op.Kind == TokenKind.Minus ? -value : value) * perUnit + offsetDbu;
                decimal folded = Math.Round(total / perUnit, 10);
                if (folded * perUnit == total)
                {
                    string head = text[..op.Position].TrimEnd();
                    if (folded == 0) return Proved(head);
                    string sign = folded < 0 ? "-" : "+";
                    string spelled = Math.Abs(folded).ToString("0.##########", CultureInfo.InvariantCulture)
                                     + (lit.Kind == TokenKind.Quantity ? lit.Text[lit.UnitStart..] : "");
                    return Proved(text[..op.Position] + sign + text[(op.Position + 1)..lit.Position] + spelled + text[(lit.Position + lit.Text.Length)..]);
                }
            }
        }
        string term = LayoutUnits.Format(Math.Abs(offsetDbu), unit, dbuPerMicron, LayoutUnits.SpellDecimals(unit, dbuPerMicron))
                      + LayoutUnits.AsciiSuffix(unit);
        string body = text.Trim();
        if (LowerThanAddition(tokens)) body = "(" + body + ")";
        return Proved($"{body} {(offsetDbu < 0 ? "-" : "+")} {term}");
    }

    /// <summary><paramref name="text"/> negated, by the rules <see cref="SwapExpression"/> states.</summary>
    private static string Negate(string text)
    {
        var (ast, tokens) = Read(text);
        if (TrailingLiteral(ast, tokens) is var (op, _))
        {
            string head = text[..op.Position].TrimEnd();
            return Negate(head) + " " + (op.Kind == TokenKind.Minus ? "+" : "-") + text[(op.Position + 1)..].TrimEnd();
        }
        if (ast is UnaryExpr { Op: "-" } && tokens[0].Kind == TokenKind.Minus)
            return Unwrap(text[tokens[1].Position..].Trim());
        if (ast is NumberExpr && tokens[0].Kind is TokenKind.Number or TokenKind.Quantity) return "-" + text.Trim();
        return "-(" + Unwrap(text.Trim()) + ")";
    }

    /// <summary>The text inside one pair of parentheses that encloses all of it; the text itself otherwise.</summary>
    private static string Unwrap(string text)
    {
        var tokens = new Tokenizer(text).Tokenize();
        if (tokens.Length < 3 || tokens[0].Kind != TokenKind.LParen || tokens[^2].Kind != TokenKind.RParen) return text;
        int depth = 0;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            depth += tokens[i].Kind switch { TokenKind.LParen => 1, TokenKind.RParen => -1, _ => 0 };
            if (depth == 0 && i < tokens.Length - 2) return text;      // the first parenthesis closes before the end
        }
        return text[(tokens[0].Position + 1)..tokens[^2].Position].Trim();
    }

    /// <summary>The operator and the literal when the expression is an addition or a subtraction whose right operand is a
    /// plain literal written last (<c>x + 50um</c>, <c>x - 5</c>); null otherwise.</summary>
    private static (Token Op, Token Literal)? TrailingLiteral(Expr ast, Token[] tokens)
    {
        if (ast is not BinaryExpr { Op: "+" or "-", Right: NumberExpr } || tokens.Length < 4) return null;
        var (op, lit) = (tokens[^3], tokens[^2]);
        if (op.Kind is not (TokenKind.Plus or TokenKind.Minus) || lit.Kind is not (TokenKind.Number or TokenKind.Quantity)) return null;
        return (op, lit);
    }

    /// <summary>True when an operator that binds more loosely than <c>+</c> stands at the top level: the offset term must
    /// not be pulled inside a comparison, a logical operator or a conditional.</summary>
    private static bool LowerThanAddition(Token[] tokens)
    {
        int depth = 0;
        foreach (var t in tokens)
        {
            if (t.Kind is TokenKind.LParen or TokenKind.LBracket) depth++;
            else if (t.Kind is TokenKind.RParen or TokenKind.RBracket) depth--;
            else if (depth == 0 && t.Kind is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual
                     or TokenKind.EqualEqual or TokenKind.BangEqual or TokenKind.AmpAmp or TokenKind.PipePipe
                     or TokenKind.Question or TokenKind.Colon)
                return true;
        }
        return false;
    }

    private static (Expr Ast, Token[] Tokens) Read(string text) => (Parser.Parse(text), new Tokenizer(text).Tokenize());

    /// <summary>The text, once it is shown to parse.</summary>
    private static string Proved(string text)
    {
        Parser.Parse(text);
        return text;
    }

    private static LayoutUnit? SiteUnit(string? stored)
        => stored is { Length: > 0 } && Enum.TryParse<LayoutUnit>(stored, ignoreCase: true, out var u) && !int.TryParse(stored, out _) ? u : null;

    /// <summary>A unit literal's engine spelling as a layout unit; null for any other unit (a fold then appends instead).</summary>
    private static LayoutUnit? LengthUnit(string? engine)
        => engine is null ? null : Enum.GetValues<LayoutUnit>().Cast<LayoutUnit?>().FirstOrDefault(u => LayoutUnits.AsciiSuffix(u!.Value) == engine);
}
