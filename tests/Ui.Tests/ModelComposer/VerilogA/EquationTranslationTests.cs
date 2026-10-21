using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Modeling.VerilogA;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// brief-modelcomposer-26 R-mc26-5, R-mc26-6: an equation's translation computes the engine's value, and its emitted
/// forward-mode gradient is the engine's dual-number gradient (<c>EvalDualInto</c>) — both checked by running the emitted
/// text with Verilog-A's meanings (<see cref="VaInterpreter"/>), never by reading its spelling.
/// </summary>
public sealed class EquationTranslationTests
{
    private static readonly string[] Inputs = ["vgs", "vds"];

    /// <summary>Every function in the engine's set, used as an equation over (vgs, vds) with one parameter, <c>p</c>.
    /// Arguments stay away from zero (the engine's <c>sinh</c> is <c>(eˣ − e⁻ˣ)/2</c>, which loses digits there) and inside
    /// each function's domain.</summary>
    public static readonly Dictionary<string, string> Cases = new()
    {
        ["exp"] = "exp(p*vgs - vds)",
        ["log"] = "log(vgs*vds + p)",
        ["ln"] = "ln(vgs + vds*vds)",
        ["sqrt"] = "sqrt(vgs*p + vds)",
        ["tanh"] = "tanh(vgs*vds - p)",
        ["sin"] = "sin(vgs + p*vds)",
        ["cos"] = "cos(vgs*vds)",
        ["abs"] = "abs(vgs - 2*vds)*vds",
        ["pow"] = "pow(vgs + 1, vds)",
        ["sinh"] = "sinh(vgs + vds)",
        ["cosh"] = "cosh(vgs - p*vds)",
        ["tan"] = "tan(vgs*0.5 + vds*0.2)",
        ["log10"] = "log10(vgs*p + vds)",
        ["min"] = "min(vgs*vgs, vds)",
        ["max"] = "max(vgs*vgs, vds)*p",
        ["sign"] = "sign(vgs - vds)*vds*vgs",
        ["if"] = "if(vgs > vds && vds < 1.5, vgs*vds, p/vgs)",
        ["atan"] = "atan(vgs) + vgs*vds",
        ["atan2"] = "atan2(vgs, vds) + vds",
        ["asin"] = "asin(vgs/2) + vds",
        ["acos"] = "acos(vds/3) + vgs*p",
        // The operators and literal forms the brief names besides the functions.
        ["^"] = "(vgs + 1)^vds - vds^2",
        ["unit literal"] = "vgs*2pF + vds*3mA",
        ["conditional"] = "vgs > 1 ? vds : -vds",
        ["e and pi"] = "e*vgs + pi*vds",
        ["division"] = "(vgs*p - 1)/(vds + vgs)",
    };

    [Fact]
    public void EveryFunctionTheEngineAccepts_HasATranslation()
    {
        // The engine's function set is the cases of its two SDD compilers. A function either gains without a row in
        // VaEquation.Functions fails here, rather than reaching a model and being refused at export.
        var engine = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in (string[])["src/Core/Expressions/SddCompiled.cs", "src/Core/Expressions/SddRegisterProgram.cs"])
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(File.ReadAllText(FixturePaths.Require(file)), "case \"([a-z0-9_]+)\":"))
                engine.Add(m.Groups[1].Value);

        Assert.Equal(engine, new SortedSet<string>(VaEquation.Functions.Keys, StringComparer.Ordinal));
        Assert.All(engine, f => Assert.True(Cases.ContainsKey(f), $"'{f}' has no translation case here"));
    }

    [Fact]
    public void EachTranslation_ComputesTheEngineValueAndGradient_At50SeededPoints()
    {
        var rng = new Random(26);
        var failures = new List<string>();
        foreach (var (name, expression) in Cases)
        {
            var ast = Parser.Parse(expression);
            var parameters = new Dictionary<string, double> { ["p"] = 0.7 };
            var engine = CompiledSddExpr.CompileNamed(ast, Inputs, parameters, "test");
            var eq = new VaEquation(Inputs, n => n);
            var (value, grad) = eq.Emit(ast);

            for (int k = 0; k < 50; k++)
            {
                double vgs = 0.3 + 1.2 * rng.NextDouble(), vds = 0.3 + 1.7 * rng.NextDouble();
                var g = new double[2];
                double want = engine.EvalDualInto([vgs, vds], g, "test");

                var va = new VaInterpreter(new Dictionary<string, double> { ["vgs"] = vgs, ["vds"] = vds, ["p"] = 0.7 });
                va.Run(eq.Code);
                double got = va.Eval(value);
                Check(name, "value", want, got, vgs, vds, failures);
                for (int i = 0; i < 2; i++)
                    Check(name, $"d/d{Inputs[i]}", g[i], grad[i] is null ? 0.0 : va.Eval(grad[i]!), vgs, vds, failures);
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    private static void Check(string name, string what, double want, double got, double vgs, double vds, List<string> failures)
    {
        if (Math.Abs(got - want) <= 1e-14 * Math.Abs(want) || got == want) return;
        failures.Add($"{name} {what} at vgs={vgs:R}, vds={vds:R}: engine {want:R}, Verilog-A {got:R}");
    }

    [Fact]
    public void TheThreeTraps_AreSpelledAsVerilogAMeansThem()
    {
        // log and ln are both natural in circuitRF; Verilog-A's log is base 10, so circuitRF's log10 is written log.
        Assert.Contains("ln(", Body("log(vgs)"));
        Assert.DoesNotContain("log(", Body("ln(vgs)"));
        Assert.Contains("log(", Body("log10(vgs)"));
        Assert.Contains("pow(", Body("vgs^vds"));
        Assert.Contains("?", Body("if(vgs > 0, vgs, vds)"));
        Assert.DoesNotContain("sign", Body("sign(vgs)"));
        // A unit literal is base SI in the AST: no unit token reaches the file.
        string unit = Body("vgs*2pF");
        Assert.Contains("2e-12", unit);
        Assert.DoesNotContain("pF", unit);
    }

    [Fact]
    public void AFunctionOutsideTheSet_IsRefusedByName()
    {
        var eq = new VaEquation(Inputs, n => n);
        var ex = Assert.Throws<VaUntranslatableException>(() => eq.Emit(Parser.Parse("vgs + limexp(vds)")));
        Assert.Equal("the function 'limexp'", ex.Message);
    }

    private static string Body(string expression)
    {
        var eq = new VaEquation(Inputs, n => n);
        eq.Emit(Parser.Parse(expression));
        return string.Join("\n", eq.Code);
    }
}
