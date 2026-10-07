using CircuitRF.Core.Design;

namespace CircuitRF.Design.Optimization;

/// <param name="TestBench">The testbench to elaborate; null when <paramref name="Refusal"/> is set.</param>
/// <param name="Library">The library to elaborate with; null when refused.</param>
/// <param name="Notes">One line per key that resolved to nothing and was skipped (overview D3).</param>
/// <param name="Refusal">Why nothing was applied, or null.</param>
public sealed record TunedDesign(
    TestBench?            TestBench,
    Library?              Library,
    IReadOnlyList<string> Notes,
    string?               Refusal);

/// <summary>
/// Puts tuned values into a design in memory — the netlist a run elaborates, never a file.
///
/// <para><b>The gate is equality with typing.</b> Elaborating the result equals elaborating a design
/// in which the same text had been typed into the same rows: a top-level value replaces the
/// instance's assignment, a VAR value replaces the variable, a value inside a cell replaces it in the
/// cell DEFINITION (so every instance moves, overview D2), and an inherited cell parameter gains the
/// override Push would add.</para>
///
/// <para>The inputs are not modified. The copies share every object nothing changed — an
/// <see cref="Instance"/>, a <see cref="Variable"/> and a <see cref="Cell"/> are replaced, never
/// mutated, so the original keeps its own.</para>
/// </summary>
public static class TunableOverrides
{
    /// <param name="values">Key → value text (<c>R1.R</c> → <c>75 Ohm</c>).</param>
    /// <param name="setVariables">Variables a <c>--set</c> already replaced. <c>--set</c> applies
    /// first; a tuned value naming one of them is a refusal naming both, since one of the two would
    /// silently lose.</param>
    public static TunedDesign Apply(
        TestBench tb, Library lib, IReadOnlyDictionary<string, string> values,
        IReadOnlyCollection<string>? setVariables = null)
    {
        var notes   = new List<string>();
        var bench   = CopyOf(tb);
        var library = new Library(lib.Name);
        library.Cells.AddRange(lib.Cells);
        var copied  = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (keyText, value) in values)
        {
            if (!TunableKey.TryParse(keyText, out var key))
            {
                notes.Add($"'{keyText}' is not a tunable key; skipped.");
                continue;
            }

            if (key.Cell is null && key.IsVariable && setVariables?.Contains(key.Name) == true)
                return new TunedDesign(null, null, notes,
                    $"'{key.Name}' is set both by --set and by a tuned value; remove one of the two.");

            List<Instance> instances;
            List<Variable> variables;
            if (key.Cell is null)
            {
                instances = bench.Instances;
                variables = bench.GlobalVariables;
            }
            else
            {
                int ci = library.Cells.FindIndex(c => c.Name == key.Cell);
                if (ci < 0) { notes.Add($"'{keyText}' names no cell '{key.Cell}'; skipped."); continue; }
                if (copied.Add(key.Cell)) library.Cells[ci] = CopyOf(library.Cells[ci]);
                instances = library.Cells[ci].Instances;
                variables = library.Cells[ci].Variables;
            }

            if (key.IsVariable)
            {
                int vi = variables.FindIndex(v => v.Name == key.Name);
                if (vi < 0) { notes.Add($"'{keyText}' names no variable; skipped."); continue; }
                var (expr, unit) = TunableValue.Split(value, variables[vi].Unit);
                variables[vi] = new Variable(key.Name, expr, unit);
                continue;
            }

            int ii = instances.FindIndex(i => i.InstanceName == key.Instance);
            if (ii < 0) { notes.Add($"'{keyText}' names no instance '{key.Instance}'; skipped."); continue; }
            var inst      = instances[ii];
            var overrides = inst.Overrides.ToList();
            int oi        = overrides.FindIndex(o => o.Name == key.Name);

            if (oi >= 0)
            {
                var (expr, unit) = TunableValue.Split(value, overrides[oi].Unit);
                overrides[oi] = new ParameterAssignment(key.Name, expr, unit);
            }
            else if (lib.Find(inst.Reference)?.Parameters.FirstOrDefault(p => p.Name == key.Name) is { } decl)
            {
                // An inherited cell parameter: the override Push would add.
                var (expr, unit) = TunableValue.Split(value, decl.Unit);
                overrides.Add(new ParameterAssignment(key.Name, expr, unit));
            }
            else
            {
                notes.Add($"'{keyText}': {key.Instance} has no parameter {key.Name}; skipped.");
                continue;
            }

            instances[ii] = new Instance(inst.InstanceName, inst.Reference, inst.NetBindings, overrides)
            {
                RefNetBinding = inst.RefNetBinding,
            };
        }

        return new TunedDesign(bench, library, notes, null);
    }

    private static TestBench CopyOf(TestBench tb)
    {
        var c = new TestBench(tb.Name) { Tuning = tb.Tuning };
        c.Instances.AddRange(tb.Instances);
        c.GlobalVariables.AddRange(tb.GlobalVariables);
        c.Functions.AddRange(tb.Functions);
        c.Analyses.AddRange(tb.Analyses);
        c.Measurements.AddRange(tb.Measurements);
        c.RawDirectives.AddRange(tb.RawDirectives);
        c.ReadWarnings.AddRange(tb.ReadWarnings);
        c.ReadNotes.AddRange(tb.ReadNotes);
        c.LabeledNets.UnionWith(tb.LabeledNets);
        return c;
    }

    private static Cell CopyOf(Cell cell)
    {
        var c = new Cell(cell.Name);
        c.Ports.AddRange(cell.Ports);
        c.Parameters.AddRange(cell.Parameters);
        c.Variables.AddRange(cell.Variables);
        c.Instances.AddRange(cell.Instances);
        return c;
    }
}
