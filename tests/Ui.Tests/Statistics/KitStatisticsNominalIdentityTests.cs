using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Optimization;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>
/// R-ya3-6 — keeping distributions live changes no nominal result. "Before" is the same text with every
/// distribution reduced to its nominal (<see cref="SpiceExpression.ReduceDistributions"/>), which is exactly
/// what the importer used to write; "after" is the live text this phase writes.
/// </summary>
public sealed class KitStatisticsNominalIdentityTests : IDisposable
{
    private readonly string _kit = Path.Combine(Path.GetTempPath(), "crf-ya3-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_kit, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Every example extracts a netlist with no distribution in it, so it is the netlist it always was.</summary>
    [Fact]
    public void Examples_HoldNoDistribution()
    {
        string examples = FixturePaths.Require("examples");
        int extracted = 0;
        foreach (var csch in Directory.EnumerateFiles(examples, "*.csch", SearchOption.AllDirectories))
        {
            string text;
            try { text = SchematicCircuit.CnlTextOf(csch); }
            catch (Exception) { continue; }   // one that needs a display or a kit is not this gate's question
            extracted++;
            Assert.True(SpiceExpression.ReduceDistributions(text) == text, $"{csch} holds a distribution");
        }
        Assert.True(extracted > 20, $"only {extracted} examples extracted");
    }

    /// <summary>
    /// A kit-shaped design — process draws in a corner section's globals, a mismatch draw in a variant
    /// subcircuit — runs bit-identically live and reduced; a section that includes the part's own library leaves
    /// the netlist byte-identical, while a mismatch section brings its variant subcircuit.
    /// </summary>
    [Fact]
    public void AKitDesign_RunsBitIdentically_AndOnlyAVariantSectionChangesTheNetlist()
    {
        string corner = WriteKit();
        string part   = Path.Combine(_kit, "r_mod.lib");

        string Netlist(string section, bool variants = true)
        {
            var read = PdkCorners.SectionFor(corner, section, out _)!;
            var lib  = SpiceNetlistReader.ReadFile(part);
            if (variants) KitCornerVariants.Apply(part, lib.Library, lib.ModelCards, [read]);
            var tb = new TestBench("tb");
            tb.GlobalVariables.AddRange(read.Variables);
            return CnlWriter.Write(tb, lib.Library, "kit") + """

                Port:P1 in 0 Num=1 Z=50 Ohm
                rpart:X1 in out
                Port:P2 out 0 Num=2 Z=50 Ohm
                analysis SP1 type=sparam start=1 stop=3 npts=5 Unit=GHz
                """;
        }

        // The nominal section pairs with the part's own library: its definitions move not one byte.
        Assert.Equal(Netlist("r_typ", variants: false), Netlist("r_typ"));
        Assert.DoesNotContain("agauss", Netlist("r_typ"));

        // The mismatch section's variant subcircuit replaces the library's own.
        Assert.Contains("agauss(1,0.01,1)", Netlist("r_mm"));

        // Live and reduced run alike.
        foreach (var live in new[] { Netlist("r_stat"), Netlist("r_mm") })
        {
            string reduced = SpiceExpression.ReduceDistributions(live);
            Assert.NotEqual(live, reduced);
            AssertSameResults(live, reduced);
        }
    }

    /// <summary>
    /// Over a real kit's corner files: each section read live and read nominal holds the same netlist modulo the
    /// distributions' spelling, and every global and every subcircuit expression in it evaluates to the same bits.
    /// </summary>
    [FixtureFact(KitFixture, "link a kit's SPICE-dialect model folder (the one holding its corner files) there")]
    public void AKitFixture_EvaluatesIdentically_LiveAndNominal()
    {
        string dir = FixturePaths.Require(KitFixture);
        var axes = PdkCorners.Discover(Directory.EnumerateFiles(dir).Select(f => (f, Path.GetFileName(f))));
        Assert.NotEmpty(axes);

        int sections = 0, distributions = 0;
        foreach (var axis in axes)
            foreach (var section in axis.Options)
            {
                string request = $".lib \"{axis.AxisId}\" \"{section}\"";
                var live    = SpiceNetlistReader.Read(request, dir);
                var nominal = SpiceNetlistReader.Read(request, dir, distributions: SpiceDistributions.Nominal);
                sections++;
                distributions += live.Statistics.Count;

                Assert.Equal(Text(nominal), SpiceExpression.ReduceDistributions(Text(live)));
                Assert.Equal(Outcomes(nominal), Outcomes(live));
            }
        Assert.True(distributions > 0, $"{sections} sections read and none carries a distribution");

        static string Text(SpiceNetlistResult r)
        {
            var tb = new TestBench("kit");
            tb.GlobalVariables.AddRange(r.Variables);
            return CnlWriter.Write(tb, r.Library, "kit");
        }
    }

    private const string KitFixture = "testdata/kit-statistics";

    // ── what every expression of a read evaluates to ─────────────────────────

    private static List<string> Outcomes(SpiceNetlistResult r)
    {
        var outcomes = new List<string>();
        var global = new Scope("global");
        foreach (var v in r.Variables) global.Bind(v.Name, v.Expression, v.Unit);
        var ev = new Evaluator();
        foreach (var fn in r.Functions) ev.RegisterFunction(fn);

        foreach (var v in r.Variables) outcomes.Add(v.Name + " " + Outcome(() => ev.Resolve(v.Name, global)));
        foreach (var cell in r.Library.Cells)
        {
            var scope = new Scope(cell.Name, global) { InstancePath = cell.Name };
            foreach (var p in cell.Parameters) scope.Bind(p.Name, p.DefaultExpression, p.Unit);
            foreach (var v in cell.Variables)  scope.Bind(v.Name, v.Expression, v.Unit);
            foreach (var p in cell.Parameters) outcomes.Add($"{cell.Name}.{p.Name} " + Outcome(() => ev.Resolve(p.Name, scope)));
            foreach (var v in cell.Variables)  outcomes.Add($"{cell.Name}.{v.Name} " + Outcome(() => ev.Resolve(v.Name, scope)));
            foreach (var inst in cell.Instances)
                foreach (var o in inst.Overrides)
                    outcomes.Add($"{cell.Name}.{inst.InstanceName}.{o.Name} " + Outcome(() => ev.Eval(o.Expression, scope, o.Unit)));
        }
        return outcomes;
    }

    private static string Outcome(Func<Value> f)
    {
        try
        {
            var v = f();
            return v.Kind switch
            {
                ValueKind.Real    => BitConverter.DoubleToInt64Bits(v.AsReal()).ToString(),
                ValueKind.Complex => $"{BitConverter.DoubleToInt64Bits(v.AsComplex().Real)},{BitConverter.DoubleToInt64Bits(v.AsComplex().Imaginary)}",
                _                 => v.Kind + ":" + v,
            };
        }
        catch (Exception ex) { return "throws " + ex.GetType().Name; }
    }

    // ── a kit-shaped fixture, and a design placing its part ─────────────────

    private string WriteKit()
    {
        Directory.CreateDirectory(_kit);
        File.WriteAllText(Path.Combine(_kit, "rCorners.lib"), """
            .LIB r_typ
            .param rsh = 50
            .include r_mod.lib
            .ENDL r_typ

            .LIB r_stat
            .param rsh_norm = 50
            .include r_stat.lib
            .include r_mod.lib
            .ENDL r_stat

            .LIB r_mm
            .param rsh = 50
            .include r_mod_mm.lib
            .ENDL r_mm
            """);
        File.WriteAllText(Path.Combine(_kit, "r_stat.lib"), """
            .param mc_rsh = 'agauss(rsh_norm, 2.5, 1)'
            .param rsh = mc_rsh
            """);
        File.WriteAllText(Path.Combine(_kit, "r_mod.lib"), """
            .subckt rpart a b w=1u l=10u
            R1 a b 'rsh*l/w'
            C1 b 0 1p
            .ends rpart
            """);
        File.WriteAllText(Path.Combine(_kit, "r_mod_mm.lib"), """
            .subckt rpart a b w=1u l=10u
            .param mm = 'agauss(1, 0.01, 1)'
            R1 a b 'rsh*l/w*mm'
            C1 b 0 'aunif(1p, 0.05p)'
            .ends rpart
            """);
        return Path.Combine(_kit, "rCorners.lib");
    }

    private static void AssertSameResults(string a, string b)
    {
        var ra = CircuitEvaluation.Evaluate(OptCircuits.Prepare(a), new CircuitEvaluationRequest { Analyses = ["SP1"] });
        var rb = CircuitEvaluation.Evaluate(OptCircuits.Prepare(b), new CircuitEvaluationRequest { Analyses = ["SP1"] });
        Assert.Equal(RunStatus.Success, ra.Status);
        Assert.Equal(RunStatus.Success, rb.Status);
        var sa = ra.GroupedResults!.CubesIn("SP1")["S"];
        var sb = rb.GroupedResults!.CubesIn("SP1")["S"];
        Assert.Equal(sb.ComplexValues, sa.ComplexValues);
    }
}
