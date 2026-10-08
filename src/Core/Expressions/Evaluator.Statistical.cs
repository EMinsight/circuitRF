namespace CircuitRF.Core.Expressions;

/// <summary>
/// Which kind of statistical draw a distribution call is (yield overview D6). Decided by the SCOPE the call is
/// evaluated in, never by the function: the testbench's global scope gives a <see cref="Process"/> draw, shared by
/// every instance in a trial; a cell instance's scope gives a <see cref="Mismatch"/> draw, one per instance.
/// </summary>
public enum StatisticalKind { Process, Mismatch }

/// <summary>
/// One stream's draw in one trial: its standard normal <paramref name="Z"/>, the uniform
/// <paramref name="U"/> = Φ(z) in (0, 1), and the run's <c>sigmascale</c>, which multiplies every spread.
/// </summary>
public readonly record struct StatisticalDraw(double Z, double U, double SigmaScale);

/// <summary>
/// What a Monte Carlo trial hands the evaluator so a distribution call draws instead of returning its nominal.
/// The evaluator owns the dialect's arithmetic (<c>agauss</c> is an absolute deviation at k σ, …); the
/// implementation owns the numbers — the stream hash, Φ — which live below this layer in <c>src/Engine</c>.
/// </summary>
public interface IStatisticalDraws
{
    /// <summary>The draw for <paramref name="stream"/>, or null when this kind is off for the run
    /// (<c>statistics process=0</c> / <c>mismatch=0</c>) and the call evaluates to its nominal.</summary>
    StatisticalDraw? Draw(StatisticalKind kind, string stream);
}

/// <summary>One distribution call an evaluation reached: the function, process or mismatch, and its stream.</summary>
public sealed record StatisticalCall(string Function, StatisticalKind Kind, string Stream);

/// <summary>
/// The dialect's distribution functions (yield overview D6, docs/design/yield.md §7): <c>agauss</c>, <c>gauss</c>,
/// <c>aunif</c>, <c>unif</c> and the two-argument <c>limit</c>. Outside a trial each is its FIRST argument —
/// evaluated alone, so its kind and unit are that argument's and its spread is never even read, which is what
/// keeps every nominal result byte-identical to the days when the importer reduced the call to that argument.
/// Inside a trial (<see cref="Statistics"/> set) it is a draw on its stream.
/// </summary>
public sealed partial class Evaluator
{
    /// <summary>The distribution functions, by the name circuitRF spells them with.</summary>
    public static readonly IReadOnlySet<string> StatisticalFunctions =
        new HashSet<string>(StringComparer.Ordinal) { "agauss", "gauss", "aunif", "unif", "limit" };

    /// <summary>
    /// Whether <paramref name="call"/> is a distribution. <c>limit</c> with three arguments is a clamp, not a
    /// distribution — the argument count is the only thing that separates the two readings, and it settles it.
    /// </summary>
    public static bool IsStatisticalCall(CallExpr call)
        => StatisticalFunctions.Contains(call.Name) && !(call.Name == "limit" && call.Args.Length == 3);

    /// <summary>
    /// The trial's draws, or null for an ordinary (nominal) evaluation. A property of THIS evaluator — the
    /// elaborator passes it down for one trial — never a global, so two runs in one process cannot see each
    /// other's draws.
    /// </summary>
    public IStatisticalDraws? Statistics { get; set; }

    private readonly List<StatisticalCall> _statCalls   = [];
    private readonly HashSet<string>       _statStreams = new(StringComparer.Ordinal);
    private readonly List<string>          _statProblems = [];

    /// <summary>Every distribution call this evaluator reached, once per stream, nominal or not.</summary>
    public IReadOnlyList<StatisticalCall> StatisticalCalls => _statCalls;

    /// <summary>
    /// Every draw that could not be made, by stream. A trial with any is a trial that did not evaluate (D7) — kept
    /// here as well as thrown, because some parameter paths fall back to the verbatim text when evaluation throws.
    /// </summary>
    public IReadOnlyList<string> StatisticalProblems => _statProblems;

    /// <summary>
    /// Where a distribution call sits, which is what names its stream: the binding being resolved, or the
    /// instance parameter being evaluated, with the instance path of its scope. The count gives the second and
    /// later calls in one expression an ordinal of their own.
    /// </summary>
    private sealed class StatFrame(string? path, string name)
    {
        public string? Path  { get; } = path;
        public string  Name  { get; } = name;
        public int     Count { get; set; }
    }

    private readonly List<StatFrame> _statFrames = [];

    /// <summary>
    /// Evaluates an instance parameter's expression, naming the stream of any distribution call in it after the
    /// instance's local name and the parameter (<paramref name="site"/>, e.g. <c>R1.R</c>) under the scope's
    /// instance path — <c>X1.R1.R</c> inside <c>X1</c>. Otherwise exactly <see cref="Eval"/>.
    /// </summary>
    public Value EvalParameter(string expression, Scope scope, string? unit, string site)
    {
        _statFrames.Add(new StatFrame(scope.InstancePath, site));
        try { return Eval(expression, scope, unit); }
        finally { _statFrames.RemoveAt(_statFrames.Count - 1); }
    }

    private void PushStatFrame(Scope owner, string name) => _statFrames.Add(new StatFrame(owner.InstancePath, name));
    private void PopStatFrame() => _statFrames.RemoveAt(_statFrames.Count - 1);

    private Value EvalStatistical(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length == 0) throw new ArityException(cl.Name, 2, 0);

        // The kind is the scope's; the stream is the innermost site's (a binding being resolved, or a parameter).
        var    frame = _statFrames.Count > 0 ? _statFrames[^1] : null;
        string? path = frame is not null ? frame.Path : scope.InstancePath;
        var    kind  = path is null ? StatisticalKind.Process : StatisticalKind.Mismatch;
        string name  = frame?.Name ?? "expr";
        int    n     = frame is null ? 1 : ++frame.Count;
        string stream = (path is null ? name : path + "." + name) + (n > 1 ? "#" + n : "");

        if (_statStreams.Add(stream)) _statCalls.Add(new StatisticalCall(cl.Name, kind, stream));

        // Nominal: the first argument ALONE. The spread is not evaluated, so a spread naming something that does
        // not resolve outside a trial (a kit's mismatch coefficient) costs a nominal run nothing — as before.
        if (Statistics?.Draw(kind, stream) is not { } draw)
            return EvalExpr(cl.Args[0], scope);

        try
        {
            return new Value(Drawn(cl, scope, draw));
        }
        catch (Exception ex)
        {
            _statProblems.Add($"{stream}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The dialect's arithmetic. <c>agauss(nom, dev, k)</c>: dev is an absolute deviation at k σ.
    /// <c>gauss(nom, rel, k)</c>: rel is relative, at k σ. <c>aunif(nom, dev)</c>: uniform on nom ± dev.
    /// <c>unif(nom, rel)</c>: uniform on nom·(1 ± rel). <c>limit(nom, dev)</c>: nom − dev or nom + dev, equally
    /// likely. k defaults to 1; k = 0 is NO spread (a kit writes <c>(mm_ok != 1 ? 0 : 1)</c> as k to switch its
    /// mismatch off, and that is the only reading that does not divide by zero). <c>sigmascale</c> multiplies every
    /// spread, the two-point <c>limit</c>'s included.
    /// </summary>
    private double Drawn(CallExpr cl, Scope scope, StatisticalDraw d)
    {
        double Arg(int i)
        {
            var v = EvalExpr(cl.Args[i], scope);
            if (v.Kind != ValueKind.Real)
                throw new TypeErrorException($"'{cl.Name}' draws a real value only; argument {i + 1} is {v.Kind}");
            return v.AsReal();
        }

        bool sigmaForm = cl.Name is "agauss" or "gauss";
        if (sigmaForm ? cl.Args.Length is < 2 or > 3 : cl.Args.Length != 2)
            throw new ExpressionException(
                $"Function '{cl.Name}' expects {(sigmaForm ? "2 or 3" : "2")} argument(s), got {cl.Args.Length}");

        double nom    = Arg(0);
        double spread = Arg(1) * d.SigmaScale;

        switch (cl.Name)
        {
            case "agauss":
            case "gauss":
            {
                double k = cl.Args.Length == 3 ? Arg(2) : 1.0;
                if (k < 0) throw new DomainException(cl.Name, $"the sigma count k is negative ({k})");
                if (k == 0) return nom;
                double sigma = spread / k;
                return cl.Name == "agauss" ? nom + sigma * d.Z : nom * (1 + sigma * d.Z);
            }
            case "aunif": return nom + spread * (2 * d.U - 1);
            case "unif":  return nom * (1 + spread * (2 * d.U - 1));
            default:      return d.U < 0.5 ? nom - spread : nom + spread;   // limit
        }
    }

    // `limit(x, lo, hi)` — the clamp. The SPICE reader rewrites it to min(max(…)) by arity; a design may write it.
    private Value EvalClamp(CallExpr cl, Scope scope)
    {
        var x  = EvalExpr(cl.Args[0], scope);
        var lo = EvalExpr(cl.Args[1], scope);
        var hi = EvalExpr(cl.Args[2], scope);
        if (x.Kind != ValueKind.Real || lo.Kind != ValueKind.Real || hi.Kind != ValueKind.Real)
            throw new TypeErrorException("'limit' requires real arguments");
        return new Value(Math.Min(Math.Max(x.AsReal(), lo.AsReal()), hi.AsReal()));
    }

    /// <summary>Whether <paramref name="expression"/> calls a distribution anywhere; false when it does not parse.</summary>
    public static bool ContainsStatisticalCall(string expression)
    {
        try { return ContainsStatisticalCall(Parser.Parse(expression)); }
        catch { return false; }
    }

    private static bool ContainsStatisticalCall(Expr ast) => ast switch
    {
        CallExpr cl        => IsStatisticalCall(cl) || cl.Args.Any(ContainsStatisticalCall),
        UnaryExpr u        => ContainsStatisticalCall(u.Operand),
        BinaryExpr b       => ContainsStatisticalCall(b.Left) || ContainsStatisticalCall(b.Right),
        CompareExpr c      => ContainsStatisticalCall(c.Left) || ContainsStatisticalCall(c.Right),
        LogicExpr l        => ContainsStatisticalCall(l.Left) || ContainsStatisticalCall(l.Right),
        ConditionalExpr cd => ContainsStatisticalCall(cd.Condition) || ContainsStatisticalCall(cd.Then) || ContainsStatisticalCall(cd.Else),
        IndexExpr ix       => ContainsStatisticalCall(ix.Target),
        _                  => false,
    };

    /// <summary>
    /// <paramref name="ast"/> with every distribution call replaced by its nominal (first) argument — the
    /// expression a nominal evaluation actually computes. The unit rules read this view, so a spread that names a
    /// unit-bearing variable cannot change how a site unit applies to the value.
    /// </summary>
    internal static Expr NominalView(Expr ast) => ast switch
    {
        CallExpr cl when IsStatisticalCall(cl) && cl.Args.Length > 0 => NominalView(cl.Args[0]),
        CallExpr cl          => cl with { Args = [.. cl.Args.Select(NominalView)] },
        UnaryExpr u          => u with { Operand = NominalView(u.Operand) },
        BinaryExpr b         => b with { Left = NominalView(b.Left), Right = NominalView(b.Right) },
        CompareExpr c        => c with { Left = NominalView(c.Left), Right = NominalView(c.Right) },
        LogicExpr l          => l with { Left = NominalView(l.Left), Right = NominalView(l.Right) },
        ConditionalExpr cd   => new ConditionalExpr(NominalView(cd.Condition), NominalView(cd.Then), NominalView(cd.Else)),
        IndexExpr ix         => ix with { Target = NominalView(ix.Target) },
        _                    => ast,
    };
}
