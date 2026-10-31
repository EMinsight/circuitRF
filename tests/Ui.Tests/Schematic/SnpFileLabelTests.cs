using System;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Schematic;

/// <summary>
/// GitHub issue #31 — a data file's <c>File</c> label shown with its stored path, with its file name
/// only, or not at all.
/// </summary>
public class SnpFileLabelTests
{
    [Fact]
    public void NameOnlyDrawsTheFileNameAndSurvivesASaveWhileThePathIsKept()
    {
        var model = new SchematicEditModel();
        var snp = new EditableComponent { InstanceName = "S1", Symbol = SymbolKind.Snp };
        snp.Parameters.Add(new EditableParameter { Name = "File", Expression = @"data\amp/spiral_5.5t.s2p" });
        model.Components.Add(snp);

        Assert.Contains(@"File = data\amp/spiral_5.5t.s2p", snp.ToRenderComponent().Labels);
        // An untouched file gains nothing.
        Assert.DoesNotContain("ShowFileNameOnly",
            SchematicPersistence.Serialize(model, "Cell", 0, 0, 1.0), StringComparison.Ordinal);

        snp.ShowFileNameOnly = true;
        var (restored, _, _) = SchematicPersistence.Deserialize(
            SchematicPersistence.Serialize(model, "Cell", 0, 0, 1.0));
        var back = restored.Components[0];

        Assert.True(back.ShowFileNameOnly);
        Assert.Contains("File = spiral_5.5t.s2p", back.ToRenderComponent().Labels);
        Assert.Equal(@"data\amp/spiral_5.5t.s2p", back.Parameters[0].Expression);
    }
}
