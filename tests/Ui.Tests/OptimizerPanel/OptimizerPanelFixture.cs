using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Optimization;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests.OptimizerPanel;

/// <summary>
/// TO-10's panel over a bench whose VAR block holds the variables and whose netlist is
/// <paramref name="cnl"/> — the "fake fast evaluator": goals that read only variables cost no
/// simulation (overview D10), so a run is a few milliseconds of arithmetic. The panel's thread hops are
/// played inline by the test.
/// </summary>
internal sealed class OptimizerPanelFixture
{
    public readonly SchematicViewModel      Top;
    public readonly OptimizerPanelViewModel Panel = new();
    public readonly string                  Cnl;

    public OptimizerPanelFixture(string cnl, TuningSetup setup, params (string Name, string Expr, string Unit)[] vars)
        : this(cnl, setup, [TuningFixture.Part("VAR1", SymbolKind.Var, 0, vars)]) { }

    public OptimizerPanelFixture(string cnl, TuningSetup setup, IEnumerable<EditableComponent> components)
    {
        Cnl = cnl;
        var model = new SchematicEditModel();
        model.Components.AddRange(components);
        model.Tuning = setup;
        Top = new SchematicViewModel(model);
        Panel.Discover       = vm => TunableCatalog.Discover(vm.EditModel, TuningFixture.Resolver());
        Panel.PrepareCircuit = _ => PreparedCircuit.FromText(Cnl, null, null);
        Panel.SetActiveSchematic(Top, "tb.csch");
    }

    public OptimizerVariableRowViewModel Row(string key) => Panel.Variables.Single(r => r.Key == key);

    public static TunableEntry Var(string key, string min, string max, bool opt = true, bool tune = false)
        => new() { Key = key, Min = min, Max = max, Opt = opt, Tune = tune };

    public static OptimizationGoal Goal(string name, string expr, GoalType type, string limit)
        => new() { Name = name, Expression = expr, Type = type, Limit = limit };

    /// <summary>A bench with one real variable <c>A</c> (start 10) and the netlist that declares it.</summary>
    public static OptimizerPanelFixture OneVariable(string min, string max, string algorithm, int maxIter,
                                                    params OptimizationGoal[] goals)
    {
        const string cnl = """
            A = 10
            Port:P1 in 0 Num=1 Z=50 Ohm
            R:R1 in 0 R=50 Ohm
            """;
        var setup = new TuningSetup
        {
            Variables = [Var("A", min, max)],
            Goals     = [.. goals],
            Optimizer = new OptimizerSettings { Algorithm = algorithm, MaxIterations = maxIter },
        };
        return new OptimizerPanelFixture(cnl, setup, ("A", "10", ""));
    }
}
