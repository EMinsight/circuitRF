using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine.Optimization;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>
/// brief-tuneopt-7 R-to7-7: one algorithm list. The ids a file may name, what this build can run, the
/// <c>optimize</c> line's <c>algorithm=</c> summary and the <c>reference optimizers</c> page — which the
/// MCP reference serves byte for byte, since it runs the same verb — all come from
/// <see cref="OptimizerAlgorithms"/> and must agree with it. (The <c>opt</c> verb's <c>--algorithm</c>
/// help arrives with TO-11 and reads the same registry.)
/// </summary>
public sealed class AlgorithmRegistryTests
{
    [Fact]
    public void TheRegistry_TheBuild_TheDirectiveAndTheReference_Agree()
    {
        var all = OptimizerAlgorithms.All;
        Assert.Equal(all.Select(a => a.Id), OptimizerAlgorithms.Ids);
        Assert.Equal(all.Count, all.Select(a => a.Id).Distinct().Count());

        // Everything the factory builds is on the menu, and the run offers exactly that.
        Assert.Equal(all.Select(a => a.Id).Where(OptimizerFactory.IsBuilt), OptimizationRun.Available);
        foreach (var id in new[] { "lm", "bfgsb", "minimax", "simplex", "trust_region", "pattern", "random", "de", "pso", "cmaes" })
            Assert.Contains(id, OptimizationRun.Available);

        var key = AnalysisDirectiveSchema.TuningDirectives.Single(d => d.Keyword == "optimize").Keys.Single(k => k.Name == "algorithm");
        Assert.Contains("One of: " + string.Join(", ", OptimizerAlgorithms.Ids) + ".", key.Summary);

        string page = CircuitRF.Cli.Reference.RenderOptimizers();
        string flat = string.Join(' ', page.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        var lines = page.Split('\n');
        foreach (var a in all)
        {
            var head = Assert.Single(lines, l => l.StartsWith(a.Id + " ", StringComparison.Ordinal));
            Assert.Contains(a.Label, head);
            Assert.Equal(!OptimizerFactory.IsBuilt(a.Id), head.Contains("(not in this build)"));
            Assert.Contains(a.UseWhen, flat);
            foreach (var o in a.Options)
                Assert.Contains($"{o.Name} {o.Default} {o.Summary}", flat);
        }
        foreach (var o in OptimizerAlgorithms.CommonOptions)
            Assert.Contains($"{o.Name} {o.Default} {o.Summary}", flat);
    }

    /// <summary>Every option an algorithm reads is one the registry documents: reading an unlisted one
    /// throws, and every constructor reads all of its options.</summary>
    [Fact]
    public void EveryBuiltAlgorithm_ReadsOnlyDocumentedOptions()
    {
        foreach (var id in OptimizationRun.Available.Where(id => id != OptimizerAlgorithms.Auto))
            Assert.NotEmpty(OptimizerFactory.Create(id, [0.3, 0.6, 0.5, 0.2], 1)!.Ask());
    }
}
