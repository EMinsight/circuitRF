using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>
/// One small hierarchical design the tuning tests share: a top-level resistor, a VAR row, and two
/// instances (X1, X2) of a two-port cell DUT whose R3 is a literal and whose R4 reads the cell
/// parameter Rbias. X1 overrides Rbias; X2 has no Rbias of its own and inherits the default.
/// Components sit 1000 units apart so nothing connects by accident.
/// </summary>
internal static class TuningFixture
{
    public const string DutFolder = "/elsewhere/lib/DUT";

    public static EditableComponent Part(string name, SymbolKind kind, double x, params (string Name, string Expr, string Unit)[] ps)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = 200 };
        foreach (var (n, e, u) in ps) c.Parameters.Add(new EditableParameter { Name = n, Expression = e, Unit = u });
        return c;
    }

    /// <summary>The cell DUT: Pin1 — R3 — Pin2, plus R4 = Rbias on its own.</summary>
    public static SchematicEditModel Dut(string r3 = "10")
    {
        var sub = new SchematicEditModel();
        sub.Components.Add(Pin(1, -100, 0));
        sub.Components.Add(new EditableComponent
        {
            InstanceName = "R3", Symbol = SymbolKind.Resistor, X = 0, Y = 400,
            Parameters = { new EditableParameter { Name = "R", Expression = r3, Unit = "Ohm" } },
        });
        sub.Components.Add(Pin(2, -100, 400));
        var r4 = Part("R4", SymbolKind.Resistor, 1000, ("R", "Rbias", ""));
        sub.Components.Add(r4);
        sub.Wires.Add(Wire((0, 0), (0, 200)));
        sub.Wires.Add(Wire((0, 400), (0, 600)));
        return sub;
    }

    /// <summary>The top schematic. <paramref name="x2Rbias"/> null leaves X2 inheriting the default.</summary>
    public static SchematicEditModel Top(string r1 = "50", string wline = "300", string? x2Rbias = null)
    {
        var top = new SchematicEditModel();
        top.Components.Add(Part("R1", SymbolKind.Resistor, 0, ("R", r1, "Ohm")));
        top.Components.Add(Part("VAR1", SymbolKind.Var, 1000, ("Wline", wline, "um")));
        top.Components.Add(Cell("X1", 2000, ("Rbias", "2", "kOhm")));
        top.Components.Add(x2Rbias is null ? Cell("X2", 3000) : Cell("X2", 3000, ("Rbias", x2Rbias, "kOhm")));
        return top;
    }

    public static EditableComponent Cell(string name, double x, params (string Name, string Expr, string Unit)[] ps)
    {
        var c = Part(name, SymbolKind.Resistor, x, ps);
        c.CellRef = "DUT";
        return c;
    }

    public static ICellResolver Resolver(string r3 = "10") => new StubResolver(
        new CellResolution("DUT", Dut(r3), [new ParameterDeclaration("Rbias", "1", "kOhm")], DutFolder));

    private sealed class StubResolver(CellResolution dut) : ICellResolver
    {
        public CellResolution? Resolve(EditableComponent comp, SchematicEditModel _) => comp.CellRef == "DUT" ? dut : null;
    }

    private static EditableComponent Pin(int num, double x, double y)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Pin, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return c;
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }
}
