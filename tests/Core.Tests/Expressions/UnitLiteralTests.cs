using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// brief-units-in-expressions — a number with a unit glued to it (<c>10um</c>, <c>2.4GHz</c>) is a unit LITERAL: one
/// token, scaled to base SI at parse time, and unit-bearing, so a site unit is never applied on top of it. A bare
/// operand beside a unit-bearing one takes the site unit (Q1 (b)), to the power of the dimension it sits beside.
/// </summary>
public class UnitLiteralTests
{
    private const double Mil = 2.54e-5;

    private static double Eval(string expression, Scope? scope = null, string? unit = null)
        => new Evaluator().Eval(expression, scope ?? new Scope("t"), unit).AsReal();

    private static void Close(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) <= 1e-12 * Math.Abs(expected), $"expected {expected:R}, got {actual:R}");

    private static Scope ScopeOf(params (string Name, string Expr, string? Unit)[] bindings)
    {
        var scope = new Scope("t");
        foreach (var (n, e, u) in bindings) scope.Bind(n, e, u);
        return scope;
    }

    // ── M1 — the literal ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASumOfLiterals_IsTheSumInBaseSI()
        => Assert.Equal(10 * 1e-6 + 1 * Mil, Eval("10um + 1mil"));

    [Theory]
    [InlineData("2.4GHz", 2.4 * 1e9)]
    [InlineData("50Ω", 50.0)]
    [InlineData("10µm", 10 * 1e-6)]
    [InlineData("1e-3mm", 1e-3 * 1e-3)]
    [InlineData("1.5nH", 1.5 * 1e-9)]
    [InlineData("2u", 2 * 1e-6)]          // Q2: a bare SI prefix is a suffix…
    [InlineData("2m", 2 * 1e-3)]          // …and m is MILLI, never the metre
    [InlineData("2metre", 2.0)]
    [InlineData("10V + 100mV", 10.1)]     // Q3: V, A, W are suffixes
    [InlineData("2W", 2.0)]
    public void ALiteral_IsScaledAtParseTime(string text, double expected)
        => Close(expected, Eval(text));

    [Fact]
    public void TheImplicitImaginary_KeepsItsMeaning()
        => Assert.Equal(new System.Numerics.Complex(0, 10), new Evaluator().Eval("10j", new Scope("t")).AsComplex());

    [Theory]
    [InlineData("10mils", "'mils' in '10mils' is not a unit")]
    [InlineData("2meter", "'meter' in '2meter' is not a unit")]
    [InlineData("0dBm", "is not a multiplier")]
    public void AnUnknownOrLogarithmicSuffix_IsRefusedByName(string text, string message)
    {
        var ex = Assert.Throws<ParseException>(() => Parser.Parse(text));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void WhitespaceSeparates_SoASpacedUnitIsStillNotALiteral()
        => Assert.Throws<ParseException>(() => Parser.Parse("2 GHz"));

    [Fact]
    public void AnSddEquation_WithALiteral_EvaluatesAndDifferentiates()
    {
        var ast = Parser.Parse("1pF * _v1^2");
        var (value, grad) = SddEvaluator.EvalDual(ast, new Dictionary<string, double>(), [3.0]);
        Close(9e-12, value);
        Close(6e-12, grad[0]);
    }

    // ── M2 — unit-bearing everywhere ─────────────────────────────────────────────────────────────

    [Fact]
    public void ASiteUnit_IsNotAppliedToALiteral_InEvalOrResolve()
    {
        var scope = ScopeOf(("x", "10um + 1mil", "mil"));
        Close(10e-6 + Mil, Eval("10um + 1mil", scope, "mil"));
        Close(10e-6 + Mil, new Evaluator().Resolve("x", scope).AsReal());
    }

    [Fact]
    public void AUnitlessVar_HoldingALiteral_IsUnitBearing()
    {
        var scope = ScopeOf(("w", "10mil", null), ("w2", "w", null));
        Close(10 * Mil, Eval("w2", scope, "mil"));
    }

    [Fact]
    public void FreqDeferral_WritesALiteralBack_AsALiteral()
    {
        var scope = new Scope("cell");
        scope.Bind("x", "10um * freq", "mil");
        string inlined = new FreqDeferral().InlineForCellBoundary("x", scope, new Evaluator());
        Assert.Contains("10um", inlined);

        var at = new Scope("stamp");
        at.Bind("freq", "2");
        Close(20e-6, new Evaluator().Eval(inlined, at, "mil").AsReal());
    }

    // ── M3 — a bare operand beside a unit-bearing one (Q1 (b)) ──────────────────────────────────

    [Fact]
    public void ACellBoundaryInline_GivesTheSiteUnitToItsBareOperands_BeforeFolding()
    {
        var scope = ScopeOf(("w", "10", "mil"));
        string inlined = new FreqDeferral().InlineForCellBoundary("w*freq + 5", scope, new Evaluator(), bareOperandUnit: "mil");
        var at = new Scope("stamp");
        at.Bind("freq", "2");
        Close(25 * Mil, new Evaluator().Eval(inlined, at).AsReal());
    }

    [Fact]
    public void AFrequencyField_ReadsLiteralsAndGivesItsUnitToBareOperands()
    {
        var globals = new Dictionary<string, Value> { ["RFfreq"] = new Value(2e9) };
        Close(2.4e9, FreqUnit.ResolveHz("2.4GHz", "MHz", globals, ["RFfreq"]));
        Close(2.1e9, FreqUnit.ResolveHz("RFfreq + 100", "MHz", globals, ["RFfreq"]));
    }

    [Theory]
    [InlineData("w + 40", 50 * Mil)]                 // additive: the 40 takes the site unit
    [InlineData("2*w", 20 * Mil)]                    // multiplicative: the 2 stays a number
    [InlineData("w + 40mil", 50 * Mil)]
    [InlineData("if(w > 5, w, 3)", 10 * Mil)]        // the comparison's 5 is 5 mil
    [InlineData("if(w > 15, w, 3)", 3 * Mil)]        // …and the other branch is 3 mil
    [InlineData("max(w, 12)", 12 * Mil)]
    public void ABareOperand_BesideAUnitBearingOne_TakesTheSiteUnit(string text, double expected)
        => Close(expected, Eval(text, ScopeOf(("w", "10", "mil")), "mil"));

    [Fact]
    public void ThePowerIsTracked()
    {
        var scope = ScopeOf(("w", "3", "mil"), ("h", "2", "mil"));
        Close(5 * Mil, Eval("sqrt(w*w + 16)", scope, "mil"));   // 16 square mil
        Close(2.5, Eval("w/h + 1", scope, "mil"));              // a ratio plus exactly 1
    }

    [Fact]
    public void AVarsOwnUnit_ReachesItsBareOperands()
    {
        var scope = ScopeOf(("a", "10", "mil"), ("b", "a + 5", "mil"));
        Close(15 * Mil, new Evaluator().Resolve("b", scope).AsReal());
    }
}
