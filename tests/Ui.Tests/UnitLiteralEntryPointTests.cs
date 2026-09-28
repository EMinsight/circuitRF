// ================================================================
//  UnitLiteralEntryPointTests.cs — brief-units-in-expressions M4: `10um + 1mil` typed in the schematic's parameter
//  editor, the 3D view's typed field and a .cnl line means one value, and no edge splitter tears a compound expression
//  holding literals into an expression and a unit.
// ================================================================

using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class UnitLiteralEntryPointTests
{
    private const string Text = "10um + 1mil";
    private const double Expected = 10e-6 + 25.4e-6;

    [Fact]
    public void TheSameText_IsTheSameValue_InAllThreeEntryPoints()
    {
        // The schematic's parameter editor: the text is kept whole, and the engine reads each literal.
        var p = new EditableParameter { Name = "W", Expression = "1", Unit = "mil" };
        var (schExpr, schUnit) = SchematicViewModel.ParseExpressionUnit(Text, p);
        Assert.Equal((Text, ""), (schExpr, schUnit));
        double schematic = new Evaluator().Eval(schExpr, new Scope("s"), p.Unit).AsReal();

        // The 3D view's typed field: not an exact plain value, so it is an expression, evaluated at the display unit.
        Assert.Equal(C3dDimensionKind.Expression, C3dDimension.Parse(Text, LayoutUnit.Mil, 1000).Kind);
        var (typedExpr, typedUnit) = C3dEditorViewModel.SplitUnit(Text, LayoutUnit.Mil);
        Assert.Equal(Text, typedExpr);
        double threeD = C3dResolver.Resolve(new C3dDocument(), C3dCell.None).Evaluate(typedExpr, C3dUnits.Stored(typedUnit)).AsReal();

        // A .cnl VAR line (Units.LiftInlineUnit) and a component parameter (the reader's glued-unit split).
        var (_, tb) = new CnlReader().Read($"x = {Text}\nR:R1 a 0 R=10um+1mil\n");
        var x = Assert.Single(tb.GlobalVariables);
        Assert.Equal((Text, null), (x.Expression, x.Unit));
        var r = Assert.Single(Assert.Single(tb.Instances).Overrides);
        Assert.Equal(("10um+1mil", null), (r.Expression, r.Unit));
        var scope = new Scope("g");
        scope.Bind(x.Name, x.Expression, x.Unit);
        double cnl = new Evaluator().Resolve("x", scope).AsReal();

        Assert.Equal(Expected, schematic, 1e-18);
        Assert.Equal(Expected, threeD, 1e-18);
        Assert.Equal(Expected, cnl, 1e-18);
    }

    [Fact]
    public void AWholeGluedValue_IsStillLiftedIntoTheUnit()
    {
        var p = new EditableParameter { Name = "L", Expression = "1", Unit = "nH" };
        Assert.Equal(("2.5", "nH"), SchematicViewModel.ParseExpressionUnit("2.5nH", p));
        Assert.Equal(("2", "GHz"), Units.LiftInlineUnit("2 GHz"));
    }
}
