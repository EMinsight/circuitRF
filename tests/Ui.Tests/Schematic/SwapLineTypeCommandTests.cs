// brief-artsch-11 R-as11-5: Swap Line Type on a multi-selection is ONE undo step — the lines that can swap do, a
// line that refuses is listed and left as it was, and one undo restores every one of them.

using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests.Schematic;

public sealed class SwapLineTypeCommandTests
{
    private sealed class CapturingSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static EditableComponent Mlin(string name, double x, double? gap)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Mlin, X = x };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 2))
            c.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        if (gap is { } g) { c.ArtworkMeasured["GapLeft"] = g; c.ArtworkMeasured["GapRight"] = g; }
        return c;
    }

    [Fact]
    public void ThreeLines_OneRefusing_SwapsTwoInOneUndoStep_ListsTheThird_AndUndoRestoresAll()
    {
        var model = new SchematicEditModel();
        var lines = new[] { Mlin("TL1", 0, 0.3e-3), Mlin("TL2", 1000, 0.25e-3), Mlin("TL3", 2000, null) };
        model.Components.AddRange(lines);
        var before = lines.Select(c => c.Parameters.Select(p => (p.Name, p.Expression, p.Unit)).ToList()).ToList();
        var sink = new CapturingSink();
        var vm = new SchematicViewModel(model, sink);

        var outcome = vm.SwapLineType(lines, SymbolKind.Cpwg);   // TL3 has no gap, and nobody to ask

        Assert.Equal(["TL1", "TL2"], outcome.Swapped);
        Assert.Contains("state G", Assert.Single(outcome.Refused));
        Assert.Equal([SymbolKind.Cpwg, SymbolKind.Cpwg, SymbolKind.Mlin], lines.Select(c => c.Symbol));
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Warning && m.Text.Contains("TL3"));

        vm.UndoRedo.Undo();

        Assert.False(vm.UndoRedo.CanUndo);
        Assert.All(lines, c => Assert.Equal(SymbolKind.Mlin, c.Symbol));
        Assert.Equal(before, lines.Select(c => c.Parameters.Select(p => (p.Name, p.Expression, p.Unit)).ToList()));
    }
}
