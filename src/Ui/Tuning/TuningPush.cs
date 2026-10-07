// ================================================================
//  TuningPush.cs  —  tuned values into the documents that own them
//  (brief-tuneopt-4 R-to4-8, overview D2)
//
//  One undo step PER DOCUMENT, through each document's own session:
//  top-level, VAR and instance values into the tuned schematic; a
//  sub-cell's into that cell's schematic. A read-only owner is skipped
//  and counted. Nothing is written for a value that already equals
//  the schematic's, so a Push after a Push is a no-op.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tuning;

/// <summary>What a Push wrote and skipped — the panel's status line.</summary>
public sealed record TuningPushReport(
    int Written,
    IReadOnlyList<(string Cell, int Count)> PerCell,
    int SkippedReadOnly,
    int SkippedOther)
{
    /// <summary><c>Pushed 5 values · DUT: 2 · skipped 1 read-only</c>.</summary>
    public string StatusLine
    {
        get
        {
            var parts = new List<string>
            {
                Written == 0 ? "Nothing to push" : $"Pushed {Written} value{(Written == 1 ? "" : "s")}",
            };
            parts.AddRange(PerCell.Select(c => $"{c.Cell}: {c.Count}"));
            if (SkippedReadOnly > 0) parts.Add($"skipped {SkippedReadOnly} read-only");
            if (SkippedOther > 0)    parts.Add($"skipped {SkippedOther} not found");
            return string.Join(" · ", parts);
        }
    }
}

public static class TuningPush
{
    /// <summary>
    /// Writes <paramref name="values"/> (key → value text) into the drawings
    /// <paramref name="catalog"/> found them in. <paramref name="sessionFor"/> answers the session that
    /// edits a sub-cell's drawing — opening it as a tab without focus when it has none, so its new
    /// unsaved state is visible — and is not asked about the tuned schematic itself.
    /// </summary>
    public static TuningPushReport Push(
        TunableCatalog catalog, IReadOnlyDictionary<string, string> values,
        SchematicViewModel tuned, Func<SchematicEditModel, SchematicViewModel?> sessionFor)
    {
        int readOnly = 0, other = 0;
        var byDrawing = new Dictionary<SchematicEditModel, (string Cell, List<IUiCommand> Commands)>(ReferenceEqualityComparer.Instance);
        var order     = new List<SchematicEditModel>();

        foreach (var (key, text) in values)
        {
            if (catalog.Find(key) is not { } t) { other++; continue; }
            if (t.DisabledReason is not null) continue;            // a swept value is the sweep's, not ours
            if (!TunableValue.TryParse(text, out double number, out string unit, out _)) { other++; continue; }
            if (number == t.Value && unit == t.Unit) continue;     // already what the schematic says
            if (t.ReadOnlyReason is not null) { readOnly++; continue; }

            var drawing = t.Cell is null ? tuned.EditModel : catalog.Drawings.GetValueOrDefault(t.Cell);
            if (drawing is null || CommandFor(drawing, t, number, unit) is not { } command) { other++; continue; }

            if (!byDrawing.TryGetValue(drawing, out var group))
            {
                group = (t.Cell ?? "", []);
                byDrawing[drawing] = group;
                order.Add(drawing);
            }
            group.Commands.Add(command);
        }

        int written = 0;
        var perCell = new List<(string, int)>();
        foreach (var drawing in order)
        {
            var (cell, commands) = byDrawing[drawing];
            var session = ReferenceEquals(drawing, tuned.EditModel) ? tuned : sessionFor(drawing);
            if (session is null) { other += commands.Count; continue; }

            session.Execute(new CommandBatch(
                $"Push {commands.Count} tuned value{(commands.Count == 1 ? "" : "s")}", commands));
            written += commands.Count;
            if (cell.Length > 0) perCell.Add((cell, commands.Count));
        }
        return new TuningPushReport(written, perCell, readOnly, other);
    }

    /// <summary>The edit that makes <paramref name="drawing"/> hold <paramref name="number"/>
    /// <paramref name="unit"/> for <paramref name="t"/>, or null when the row is no longer there.</summary>
    private static IUiCommand? CommandFor(SchematicEditModel drawing, Tunable t, double number, string unit)
    {
        string expr = number.ToString("G15", CultureInfo.InvariantCulture);

        if (t.Kind == TunableKind.Variable)
        {
            var row = drawing.Components
                .Where(c => c.Symbol == SymbolKind.Var)
                .SelectMany(c => c.Parameters)
                .FirstOrDefault(p => p.Name.Trim() == t.Owner);
            return row is null ? null : new EditParameterCommand(drawing, row, expr, UnitFor(row, unit));
        }

        var component = drawing.Components.FirstOrDefault(c => c.InstanceName == t.Owner);
        if (component is null || t.Parameter is null) return null;

        var param = component.Parameters.FirstOrDefault(p => p.Name == t.Parameter);
        if (param is not null) return new EditParameterCommand(drawing, param, expr, UnitFor(param, unit));

        // An instance inheriting its cell's default gains the override (overview D2).
        return t.Kind == TunableKind.CellParameter
            ? new AddParameterCommand(drawing, component,
                new EditableParameter { Name = t.Parameter, Expression = expr, Unit = unit })
            : null;
    }

    /// <summary>The row's own unit spelling when it means the same unit (Ω stays Ω), else the tuned one.
    /// A unit written inside the expression (<c>47pF</c>) moves to the unit column.</summary>
    private static string UnitFor(EditableParameter row, string unit)
        => row.Unit.Length > 0 && UnitNormalizer.ToEngineUnit(row.Unit) == unit ? row.Unit : unit;
}
