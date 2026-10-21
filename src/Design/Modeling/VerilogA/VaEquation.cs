using System.Text;
using CircuitRF.Core.Expressions;
using static CircuitRF.Design.Modeling.VerilogA.VaText;

namespace CircuitRF.Design.Modeling.VerilogA;

/// <summary>A construct an equation holds that has no Verilog-A translation: the function by name, or the node.</summary>
internal sealed class VaUntranslatableException(string what) : Exception(what);

/// <summary>
/// An equation constituent as Verilog-A (brief-modelcomposer-26 R-mc26-5, R-mc26-6), translated from the expression engine's
/// AST and never from its text.
///
/// <para><b>Value and gradient, by source-to-source forward mode.</b> Every node that is not a leaf writes its value into a
/// temporary and its derivative by each input into one more, exactly as the engine's dual numbers form them
/// (<c>CompiledSddExpr.EvalDualInto</c>) — the same arithmetic in the same order, the engine's domain guards included
/// (<c>exp</c> capped at 700, <c>ln</c> of a non-positive number taken at 1e-300, <c>sqrt</c> of a negative one at 0), so
/// the emitted gradient is the engine's to rounding. Verilog-A's own derivatives cannot stand in for it: they differentiate
/// at the circuit's operating point only, and guided extrapolation and the bias-point linearisation need the gradient at a
/// point the circuit is not at.</para>
///
/// <para><b>Three mappings are traps.</b> circuitRF's <c>log</c> and <c>ln</c> are both natural, and Verilog-A's <c>log</c>
/// is base 10, so both are written <c>ln</c> and circuitRF's <c>log10</c> is written <c>log</c>. <c>if(c, a, b)</c> becomes
/// <c>c ? a : b</c>, <c>^</c> becomes <c>pow</c>, and <c>sign</c> is spelled out. A literal carrying a unit is already base
/// SI in the AST, so no unit token reaches the file.</para>
/// </summary>
internal sealed class VaEquation
{
    /// <summary>
    /// Every function the engine's SDD compiler accepts, with its arity: the set this translator covers. A function the
    /// engine gains without a row here is refused by name, and a test that reads the engine's own list fails until it has one.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Functions = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["exp"] = 1, ["log"] = 1, ["ln"] = 1, ["sqrt"] = 1, ["tanh"] = 1, ["sin"] = 1, ["cos"] = 1, ["abs"] = 1,
        ["pow"] = 2, ["sinh"] = 1, ["cosh"] = 1, ["tan"] = 1, ["log10"] = 1, ["min"] = 2, ["max"] = 2, ["sign"] = 1,
        ["if"] = 3, ["atan"] = 1, ["atan2"] = 2, ["asin"] = 1, ["acos"] = 1,
    };

    private readonly IReadOnlyList<string> _inputs;
    private readonly Func<string, string> _name;
    private readonly List<string> _code = [];
    private readonly List<string> _locals = [];
    private int _next;

    /// <param name="inputs">The inputs the gradient is taken by, as the function names its arguments.</param>
    /// <param name="name">What a referenced name is written as: an input as itself, a parameter as its module
    /// parameter.</param>
    public VaEquation(IReadOnlyList<string> inputs, Func<string, string> name)
    {
        _inputs = inputs;
        _name = name;
    }

    /// <summary>The statements, in order.</summary>
    public IReadOnlyList<string> Code => _code;

    /// <summary>Every temporary the statements assign, to declare.</summary>
    public IReadOnlyList<string> Locals => _locals;

    /// <summary>
    /// Emits <paramref name="e"/>; returns the value's atom and each input's derivative atom (null: identically zero).
    /// An atom is a temporary, a name or a literal.
    /// </summary>
    public (string Value, string?[] Grad) Emit(Expr e)
    {
        var (v, g) = Node(e);
        return (v.Text, [.. g.Select(x => x?.Text)]);
    }

    // ── atoms ──────────────────────────────────────────────────────────────────

    /// <summary>A value or derivative: its text, and whether it is the literal one (so a product by it can be dropped
    /// without changing a bit).</summary>
    private sealed record Atom(string Text, bool IsOne = false);

    private static readonly Atom One = new("1.0", IsOne: true);

    private int Width => _inputs.Count;

    private Atom Temp(string rhs, string? suffix = null)
    {
        string name = suffix is null ? $"t{++_next}" : $"t{_next}_{suffix}";
        _locals.Add(name);
        _code.Add($"{name} = {rhs};");
        return new Atom(name);
    }

    /// <summary>Starts a node: its value temporary.</summary>
    private Atom Value(string rhs) => Temp(rhs);

    /// <summary>Its derivatives, each written into <c>t&lt;k&gt;_&lt;input&gt;</c>; a null rhs is zero and writes nothing.</summary>
    private Atom?[] Grad(Func<int, string?> rhs)
    {
        var g = new Atom?[Width];
        for (int i = 0; i < Width; i++)
            if (rhs(i) is { } r) g[i] = Temp(r, _inputs[i]);
        return g;
    }

    private static Atom?[] Zero(int width) => new Atom?[width];

    private static string P(Atom a) => a.Text.StartsWith('-') ? $"({a.Text})" : a.Text;

    /// <summary><c>a*b</c> with a literal one dropped — exact, since <c>x·1 = x</c>.</summary>
    private static string Mul(Atom a, Atom b) => a.IsOne ? P(b) : b.IsOne ? P(a) : $"{P(a)}*{P(b)}";

    // ── nodes ──────────────────────────────────────────────────────────────────

    private (Atom V, Atom?[] G) Node(Expr e)
    {
        switch (e)
        {
            case NumberExpr n:
                return (new Atom(Operand(n.Value)), Zero(Width));
            case ConstExpr c:
                return c.Name switch
                {
                    "pi" => (new Atom("`M_PI"), Zero(Width)),
                    "e"  => (new Atom("`M_E"), Zero(Width)),
                    _    => throw new VaUntranslatableException($"the constant '{c.Name}'"),
                };
            case RefExpr r:
            {
                int k = IndexOf(r.Name);
                var g = Zero(Width);
                if (k >= 0) g[k] = One;
                return (new Atom(_name(r.Name)), g);
            }
            case UnaryExpr { Op: "+" } u:
                return Node(u.Operand);
            case UnaryExpr { Op: "-" } u:
            {
                var (a, ga) = Node(u.Operand);
                var v = Value($"-{P(a)}");
                return (v, Grad(i => ga[i] is { } d ? $"-{P(d)}" : null));
            }
            case BinaryExpr b:
                return Binary(b.Op, Node(b.Left), Node(b.Right));
            case ConditionalExpr cd:
                return Select(Condition(cd.Condition), Node(cd.Then), Node(cd.Else));
            case CallExpr cl:
                return Call(cl);
            default:
                throw new VaUntranslatableException(e switch
                {
                    CompareExpr or LogicExpr => "a comparison used as a number",
                    StringLiteralExpr => "a string",
                    _ => $"'{e.GetType().Name}'",
                });
        }
    }

    private int IndexOf(string name)
    {
        for (int i = 0; i < Width; i++) if (_inputs[i] == name) return i;
        return -1;
    }

    private (Atom, Atom?[]) Binary(string op, (Atom V, Atom?[] G) l, (Atom V, Atom?[] G) r)
    {
        var (a, ga) = l;
        var (b, gb) = r;
        switch (op)
        {
            case "+":
            {
                var v = Value($"{P(a)} + {P(b)}");
                return (v, Grad(i => (ga[i], gb[i]) switch
                {
                    (null, null) => null,
                    ({ } x, null) => x.Text,
                    (null, { } y) => y.Text,
                    ({ } x, { } y) => $"{P(x)} + {P(y)}",
                }));
            }
            case "-":
            {
                var v = Value($"{P(a)} - {P(b)}");
                return (v, Grad(i => (ga[i], gb[i]) switch
                {
                    (null, null) => null,
                    ({ } x, null) => x.Text,
                    (null, { } y) => $"-{P(y)}",
                    ({ } x, { } y) => $"{P(x)} - {P(y)}",
                }));
            }
            case "*":
            {
                // (ab)' = a'b + ab'
                var v = Value($"{P(a)}*{P(b)}");
                return (v, Grad(i => (ga[i], gb[i]) switch
                {
                    (null, null) => null,
                    ({ } x, null) => Mul(x, b),
                    (null, { } y) => Mul(a, y),
                    ({ } x, { } y) => $"{Mul(x, b)} + {Mul(a, y)}",
                }));
            }
            case "/":
            {
                // (a/b)' = (a'b − ab')/b², as the engine forms it.
                var v = Value($"{P(a)}/{P(b)}");
                if (ga.All(x => x is null) && gb.All(x => x is null)) return (v, Zero(Width));
                var bb = Temp($"{P(b)}*{P(b)}", "bb");
                return (v, Grad(i => (ga[i], gb[i]) switch
                {
                    (null, null) => null,
                    ({ } x, null) => $"({Mul(x, b)})/{bb.Text}",
                    (null, { } y) => $"(-{Mul(a, y)})/{bb.Text}",
                    ({ } x, { } y) => $"({Mul(x, b)} - {Mul(a, y)})/{bb.Text}",
                }));
            }
            case "^":
                return Pow(l, r);
            default:
                throw new VaUntranslatableException($"the operator '{op}'");
        }
    }

    /// <summary><c>a^b</c>: <c>p·(b/a·a′ + ln(a)·b′)</c>, each factor guarded as the engine guards it.</summary>
    private (Atom, Atom?[]) Pow((Atom V, Atom?[] G) l, (Atom V, Atom?[] G) r)
    {
        var (a, ga) = l;
        var (b, gb) = r;
        var v = Value($"pow({a.Text}, {b.Text})");
        bool needA = ga.Any(x => x is not null), needB = gb.Any(x => x is not null);
        if (!needA && !needB) return (v, Zero(Width));
        var bOverA = needA ? Temp($"({P(a)} != 0.0) ? {P(b)}/{P(a)} : 0.0", "ba") : null;
        var lnA = needB ? Temp($"({P(a)} > 0.0) ? ln({a.Text}) : 0.0", "la") : null;
        return (v, Grad(i => (ga[i], gb[i]) switch
        {
            (null, null) => null,
            ({ } x, null) => $"{v.Text}*({Mul(bOverA!, x)})",
            (null, { } y) => $"{v.Text}*({Mul(lnA!, y)})",
            ({ } x, { } y) => $"{v.Text}*({Mul(bOverA!, x)} + {Mul(lnA!, y)})",
        }));
    }

    private (Atom, Atom?[]) Select(string condition, (Atom V, Atom?[] G) t, (Atom V, Atom?[] G) f)
    {
        var v = Value($"({condition}) ? {P(t.V)} : {P(f.V)}");
        return (v, Grad(i => (t.G[i], f.G[i]) switch
        {
            (null, null) => null,
            var (x, y) => $"({condition}) ? {(x is null ? "0.0" : P(x))} : {(y is null ? "0.0" : P(y))}",
        }));
    }

    /// <summary>A condition, as Verilog-A's comparison and logical operators spell it — the same as circuitRF's.</summary>
    private string Condition(Expr c) => c switch
    {
        CompareExpr cmp => $"{P(Node(cmp.Left).V)} {cmp.Op} {P(Node(cmp.Right).V)}",
        LogicExpr lg    => $"({Condition(lg.Left)}) {lg.Op} ({Condition(lg.Right)})",
        UnaryExpr { Op: "!" } u => $"!({Condition(u.Operand)})",
        ConditionalExpr cd => $"({Condition(cd.Condition)}) ? ({Condition(cd.Then)}) : ({Condition(cd.Else)})",
        _ => throw new VaUntranslatableException("a condition that is not a comparison"),
    };

    private (Atom, Atom?[]) Call(CallExpr cl)
    {
        if (!Functions.TryGetValue(cl.Name, out int arity))
            throw new VaUntranslatableException($"the function '{cl.Name}'");
        if (cl.Args.Length != arity)
            throw new VaUntranslatableException($"'{cl.Name}' with {cl.Args.Length} arguments");

        if (cl.Name == "if") return Select(Condition(cl.Args[0]), Node(cl.Args[1]), Node(cl.Args[2]));
        if (cl.Name == "pow") return Pow(Node(cl.Args[0]), Node(cl.Args[1]));

        var (a, ga) = Node(cl.Args[0]);
        string A = a.Text;
        Atom?[] Chain(string factor) => Grad(i => ga[i] is { } d ? $"{factor}*{P(d)}" : null);

        switch (cl.Name)
        {
            case "exp":
            {
                var ev = Value($"exp(({P(a)} > 700.0) ? 700.0 : {P(a)})");
                return (ev, Chain(ev.Text));
            }
            case "log":
            case "ln":
            {
                var av = Value($"({P(a)} <= 0.0) ? 1e-300 : {P(a)}");
                var v = Value($"ln({av.Text})");
                return (v, Grad(i => ga[i] is { } d ? $"{P(d)}/{av.Text}" : null));
            }
            case "log10":
            {
                var av = Value($"({P(a)} <= 0.0) ? 1e-300 : {P(a)}");
                var v = Value($"log({av.Text})");
                return (v, Grad(i => ga[i] is { } d ? $"{P(d)}/({av.Text}*{Num(Math.Log(10.0))})" : null));
            }
            case "sqrt":
            {
                var av = Value($"({P(a)} < 0.0) ? 0.0 : {P(a)}");
                var v = Value($"sqrt({av.Text})");
                return (v, Grad(i => ga[i] is { } d ? $"{P(d)}/(({v.Text} > 0.0) ? 2.0*{v.Text} : 1e-150)" : null));
            }
            case "tanh":
            {
                var v = Value($"tanh({A})");
                return (v, Chain($"(1.0 - {v.Text}*{v.Text})"));
            }
            case "sin":  { var v = Value($"sin({A})");  return (v, Chain($"cos({A})")); }
            case "cos":  { var v = Value($"cos({A})");  return (v, Chain($"(-sin({A}))")); }
            case "sinh": { var v = Value($"sinh({A})"); return (v, Chain($"cosh({A})")); }
            case "cosh": { var v = Value($"cosh({A})"); return (v, Chain($"sinh({A})")); }
            case "tan":
            {
                var v = Value($"tan({A})");
                return (v, Grad(i => ga[i] is { } d ? $"{P(d)}/(cos({A})*cos({A}))" : null));
            }
            case "abs":
            {
                var v = Value($"abs({A})");
                return (v, Chain($"(({P(a)} >= 0.0) ? 1.0 : -1.0)"));
            }
            case "sign":
                return (Value($"({P(a)} > 0.0) ? 1.0 : (({P(a)} < 0.0) ? -1.0 : 0.0)"), Zero(Width));
            case "min":
            case "max":
            {
                var (b, gb) = Node(cl.Args[1]);
                string op = cl.Name == "max" ? ">=" : "<=";
                var v = Value($"{cl.Name}({A}, {b.Text})");
                return (v, Grad(i => (ga[i], gb[i]) switch
                {
                    (null, null) => null,
                    var (x, y) => $"({P(a)} {op} {P(b)}) ? {(x is null ? "0.0" : P(x))} : {(y is null ? "0.0" : P(y))}",
                }));
            }
            // The engine carries these four as constants under differentiation (their derivative is zero there), and the
            // emitted gradient is the engine's.
            case "atan":
            case "asin":
            case "acos":
                return (Value($"{cl.Name}({A})"), Zero(Width));
            case "atan2":
            {
                var (b, _) = Node(cl.Args[1]);
                return (Value($"atan2({A}, {b.Text})"), Zero(Width));
            }
            default:
                throw new VaUntranslatableException($"the function '{cl.Name}'");
        }
    }

    // ── plain text ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="e"/> as one Verilog-A expression with no gradient — what a network element's value folds to. The
    /// function names map as for an equation; the engine's domain guards are not written, because a network value is a
    /// constant of the device, evaluated once.
    /// </summary>
    public static string Text(Expr e, Func<string, string> name)
    {
        var sb = new StringBuilder();
        Write(e, name, sb);
        return sb.ToString();
    }

    private static void Write(Expr e, Func<string, string> name, StringBuilder sb)
    {
        switch (e)
        {
            case NumberExpr n: sb.Append(Operand(n.Value)); break;
            case ConstExpr { Name: "pi" }: sb.Append("`M_PI"); break;
            case ConstExpr { Name: "e" }: sb.Append("`M_E"); break;
            case RefExpr r: sb.Append(name(r.Name)); break;
            case UnaryExpr { Op: "+" } u: Write(u.Operand, name, sb); break;
            case UnaryExpr { Op: "-" } u: sb.Append("(-"); Write(u.Operand, name, sb); sb.Append(')'); break;
            case BinaryExpr { Op: "^" } p:
                sb.Append("pow("); Write(p.Left, name, sb); sb.Append(", "); Write(p.Right, name, sb); sb.Append(')');
                break;
            case BinaryExpr b when b.Op is "+" or "-" or "*" or "/":
                sb.Append('('); Write(b.Left, name, sb); sb.Append($" {b.Op} "); Write(b.Right, name, sb); sb.Append(')');
                break;
            case CallExpr cl when Functions.TryGetValue(cl.Name, out int arity) && cl.Args.Length == arity
                                  && cl.Name is not ("if" or "sign"):
                sb.Append(cl.Name switch { "log" => "ln", "log10" => "log", _ => cl.Name }).Append('(');
                for (int k = 0; k < cl.Args.Length; k++)
                {
                    if (k > 0) sb.Append(", ");
                    Write(cl.Args[k], name, sb);
                }
                sb.Append(')');
                break;
            default:
                throw new VaUntranslatableException(e is CallExpr c ? $"the function '{c.Name}'" : $"'{e.GetType().Name}'");
        }
    }
}
