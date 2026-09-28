using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// Gate tests for the var-unit-wins rule in Evaluator.Eval (brief-var-unit-wins-consistency Part B).
/// A site unit on a reference to a unit-bearing variable is skipped — the variable's own unit
/// was already applied in Resolve, so applying the site unit again would double-scale.
/// </summary>
public class EvaluatorVarUnitWinsTests
{
    private static (Evaluator ev, Scope scope) MakeScope(params (string name, string expr, string? unit)[] bindings)
    {
        var scope = new Scope("test");
        var ev    = new Evaluator();
        foreach (var (name, expr, unit) in bindings)
            scope.Bind(name, expr, unit);
        return (ev, scope);
    }

    // T1 — Eval_VarUnitWins_SkipsSiteUnit
    // X declared with GHz; Eval("X", scope, "GHz") must yield 2e9, NOT 2e18.
    // A unit-less Y still gets the site unit applied once.
    // A literal bypasses var-unit-wins and gets the site unit.
    [Fact]
    public void Eval_VarUnitWins_SkipsSiteUnit()
    {
        var (ev, scope) = MakeScope(
            ("X", "2", "GHz"),   // unit-bearing: Resolve("X") = 2e9
            ("Y", "2", null));   // unit-less

        // X has unit → site unit skipped → 2e9 (one application)
        Assert.Equal(2e9, ev.Eval("X", scope, "GHz").AsReal());

        // Y is unit-less → site unit applies → 2e9
        Assert.Equal(2e9, ev.Eval("Y", scope, "GHz").AsReal());

        // Literal has no variable → site unit applies → 2.4e9
        Assert.Equal(2.4e9, ev.Eval("2.4", scope, "GHz").AsReal());
    }

    // T2 — Eval_VarUnitWins_Compound
    // X*2 where X has unit "GHz": site unit is skipped (any ref has a unit).
    // Y*2 where Y is unit-less: site unit applies.
    [Fact]
    public void Eval_VarUnitWins_Compound()
    {
        var (ev, scope) = MakeScope(
            ("X", "2", "GHz"),   // unit-bearing: Resolve("X") = 2e9
            ("Y", "2", null));   // unit-less

        // X*2 → raw = 2e9 * 2 = 4e9; any ref has unit → skip site GHz → 4e9
        Assert.Equal(4e9, ev.Eval("X*2", scope, "GHz").AsReal());

        // Y*2 → raw = 4; no unit ref → apply site GHz → 4e9
        Assert.Equal(4e9, ev.Eval("Y*2", scope, "GHz").AsReal());
    }

    // T3 — Eval_PrefixedUnitDouble_Fixed
    // Cval declared with pF; Eval("Cval", scope, "pF") must yield 1e-12, NOT 1e-24.
    [Fact]
    public void Eval_PrefixedUnitDouble_Fixed()
    {
        var (ev, scope) = MakeScope(("Cval", "1", "pF"));   // Resolve("Cval") = 1e-12

        Assert.Equal(1e-12, ev.Eval("Cval", scope, "pF").AsReal(), precision: 5);
    }

    // T4 — no site unit → path untouched regardless of var unit
    [Fact]
    public void Eval_NoSiteUnit_NotAffected()
    {
        var (ev, scope) = MakeScope(("X", "2", "GHz"));

        // No site unit → ApplyUnit(raw, null) → raw (unit already in value)
        Assert.Equal(2e9, ev.Eval("X", scope, null).AsReal());
    }

    // T5 — unit-less var + site unit → site unit applies (unchanged baseline behavior)
    [Fact]
    public void Eval_UnitlessVar_SiteUnitApplies()
    {
        var (ev, scope) = MakeScope(("Rval", "50", null));

        // Rval has no unit → site Ohm (scale=1.0) applies → 50
        Assert.Equal(50.0, ev.Eval("Rval", scope, "Ohm").AsReal());
        // Site GHz applies → 50e9
        Assert.Equal(50e9, ev.Eval("Rval", scope, "GHz").AsReal());
    }

    // T6 — a VARIABLE's own unit follows the same rule as a site's. `b = 2*a` declared in mil, with `a`
    // in mil, is 20 mil: a was already scaled when it resolved, so b's unit must not scale it again.
    // Resolve applied a binding's unit unconditionally, so b came out 2.54e-5 times too small.
    [Fact]
    public void Resolve_VariableUnit_DoesNotRescaleAUnitBearingReference()
    {
        var (ev, scope) = MakeScope(("a", "10", "mil"), ("b", "2*a", "mil"), ("c", "a/4", null), ("d", "2", "mil"));

        Assert.Equal(20 * 2.54e-5, ev.Resolve("b", scope).AsReal(), 15);
        Assert.Equal(10 * 2.54e-5 / 4, ev.Resolve("c", scope).AsReal(), 15);  // unit-less: unchanged
        Assert.Equal(2 * 2.54e-5, ev.Resolve("d", scope).AsReal(), 15);       // a literal still takes its unit
        Assert.Equal(20 * 2.54e-5, ev.Eval("b", scope, "mil").AsReal(), 15);  // and a site in mil takes it once
    }

    // T7 — a frequency-dependent binding inlined across a cell boundary follows the same rule as Resolve.
    [Fact]
    public void FreqDeferral_VariableUnit_DoesNotRescaleAUnitBearingReference()
    {
        var scope = new Scope("cell");
        scope.Bind("L", "1", "nH");
        scope.Bind("x", "L * freq", "nH");   // x references L, which already carries nH

        string inlined = new FreqDeferral().InlineForCellBoundary("x", scope, new Evaluator());

        var at = new Scope("stamp");
        at.Bind("freq", "2");
        Assert.Equal(2e-9, new Evaluator().Eval(inlined, at).AsReal(), tolerance: 1e-21);
    }
}
