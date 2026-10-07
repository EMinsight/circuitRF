using CircuitRF.Design.Schematic;
using CircuitRF.Render;
using Xunit;

namespace CircuitRF.Ui.Tests.Render;

/// <summary>
/// <c>DocumentExtents.SchematicBox</c> — the box `render --fit` frames a schematic on — must hold every
/// component's labels. It aggregated only the model's fixed ±200 square per component, so a VAR block
/// at the bottom of a sheet ran off the picture (found while generating schematics from netlists).
/// </summary>
public sealed class SchematicFitTests
{
    [Fact]
    public void AFit_HoldsEveryRowOfAVariableBlock()
    {
        var model = new SchematicEditModel();
        var block = new EditableComponent { InstanceName = "V1", Symbol = SymbolKind.Var, X = 0, Y = 0 };
        for (int i = 0; i < 12; i++)
            block.Parameters.Add(new EditableParameter { Name = $"Var{i}", Expression = $"{i}" });
        model.Components.Add(block);

        var (rm, _) = model.BuildRenderModel();
        var c = rm.Components.Single();
        Assert.True(c.FullBbMaxY > rm.BbMaxY, "the fixture must have labels past the model box to test anything");

        var box = DocumentExtents.SchematicBox(model, rm)!.Value;
        Assert.True(box.X0 <= c.FullBbMinX && box.Y0 <= c.FullBbMinY
                    && box.X1 >= c.FullBbMaxX && box.Y1 >= c.FullBbMaxY);
    }
}
