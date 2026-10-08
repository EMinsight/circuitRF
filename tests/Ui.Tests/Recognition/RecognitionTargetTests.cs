// brief-artsch-6-emit-and-target-cell.md §4 — the target (R-as6-6) through the one entry point (R-as6-8): a new
// cell is created; the artwork cell is offered only while it has no schematic view; a replace of a schematic this
// command wrote takes a checkpoint first; a hand-drawn one is refused; and the .clay is never touched.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class RecognitionTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as6-target-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly RecognitionInput _input;
    private readonly byte[] _clay;

    public RecognitionTargetTests()
    {
        _input = EmitBoards.Saved(_root);
        _clay = File.ReadAllBytes(_input.ClayPath!);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private void ClayUntouched() => Assert.Equal(_clay, File.ReadAllBytes(_input.ClayPath!));

    [Fact]
    public void ANewCellIsCreatedWithTheArtworksTechnologyAndProvenance_AndAnExistingNameIsRefused()
    {
        Assert.Equal("Board_model", RecognitionTarget.DefaultCellName(_input.ClayPath!));
        var run = ArtworkRecognition.Run(_input, RecognitionTarget.NewCell("Board_model"));

        Assert.True(run.Ok, run.Refusal);
        Assert.Equal(Path.Combine(_root, "Board_model", "schematic", "Board_model.csch"), run.SchematicPath);
        var (model, _, _) = SchematicPersistence.LoadFromFile(run.SchematicPath!);
        Assert.Equal("../../tech/board.ctech", model.TechRef);
        Assert.Equal("../../Board/layout/Board.clay", model.ArtworkSource!.Layout);
        Assert.Equal("whole", model.ArtworkSource.Scope);
        var c1 = model.Components.Single(c => c.InstanceName == "C1");
        Assert.True(c1.FromArtwork);
        Assert.Single(c1.ArtworkAnchor);
        Assert.True(model.Components.Single(c => c.InstanceName == "TL1").ArtworkMeasured.ContainsKey("Z0"));
        Assert.All(model.Components.Where(c => c.Symbol == SymbolKind.Ground), g => Assert.False(g.FromArtwork));
        Assert.False(run.CheckpointTaken);

        var again = ArtworkRecognition.Run(_input, RecognitionTarget.NewCell("Board_model"));
        Assert.Contains("A cell named 'Board_model' already exists", again.Refusal);
        ClayUntouched();
    }

    [Fact]
    public void TheArtworkCellIsOfferedOnlyWhileItHasNoSchematicView()
    {
        Assert.True(RecognitionTarget.ArtworkCellOffered(_input.ClayPath!));
        var run = ArtworkRecognition.Run(_input, RecognitionTarget.ArtworkCell);
        Assert.True(run.Ok, run.Refusal);
        Assert.Equal(Path.Combine(_root, "Board", "schematic", "Board.csch"), run.SchematicPath);

        Assert.False(RecognitionTarget.ArtworkCellOffered(_input.ClayPath!));
        Assert.Contains("already has a schematic view", ArtworkRecognition.Run(_input, RecognitionTarget.ArtworkCell).Refusal);
        ClayUntouched();
    }

    [Fact]
    public void AReplaceTakesACheckpointFirst_AndAHandDrawnSchematicIsRefused()
    {
        var first = ArtworkRecognition.Run(_input, RecognitionTarget.NewCell("Board_model"));
        string path = first.SchematicPath!;
        string before = File.ReadAllText(path);

        int checkpoints = 0;
        bool takenBeforeTheWrite = false;
        var options = new RecognitionRunOptions
        {
            Now = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),   // so the replace is visibly a new write
            Checkpoint = (p, intent) =>
            {
                checkpoints++;
                takenBeforeTheWrite = p == path && File.ReadAllText(p) == before && intent == RecognitionTarget.ReplaceIntent;
                return null;
            },
        };

        string cell = Path.Combine(_root, "Board_model");
        Assert.True(RecognitionTarget.IsReplaceable(cell));
        var replaced = ArtworkRecognition.Run(_input, RecognitionTarget.Replace(cell), options);
        Assert.True(replaced.Ok, replaced.Refusal);
        Assert.Equal((1, true, true), (checkpoints, takenBeforeTheWrite, replaced.CheckpointTaken));
        Assert.NotEqual(before, File.ReadAllText(path));

        var hand = CellCreate.Create(_root, "Hand");
        var refused = ArtworkRecognition.Run(_input, RecognitionTarget.Replace(hand.CellDir), options);
        Assert.Equal("Hand's schematic was not created from artwork; choose a new cell", refused.Refusal);
        Assert.Equal(1, checkpoints);
        ClayUntouched();
    }
}
