// Designer report, round 16 — Create Schematic from Artwork and the way back. One test per claim:
//   * a net the layout names (a shape's Net) names the recognised circuit's net;
//   * a new stackup conductor or dielectric is like the last of its kind, or the generic board when it is the first.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Ui.Layout;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class DesignerRound16Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r16-" + Guid.NewGuid().ToString("N")[..12]);

    public DesignerRound16Tests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>The trace from port 1 to C1 labelled "RF in": the port's net takes the name, and every other node on
    /// that copper takes it numbered, so each section is findable by the name it was given.</summary>
    [Fact]
    public void ANetTheLayoutNames_NamesTheCircuitsNet()
    {
        var input = EmitBoards.Saved(_root);
        var feed = input.View.Shapes.OfType<RectShape>().Single(r => r.Layer == Top && r.X1 == 0 && r.Y2 < Um(12_000));
        feed.Net = "RF in";

        var circuit = RecognitionEmit.Build(ArtworkRecognition.Recognize(input), input);
        var bindings = circuit.TestBench.Instances.SelectMany(i => i.NetBindings).Distinct().ToList();

        Assert.Equal(["RF_in", "0"], circuit.TestBench.Instances.Single(i => i.InstanceName == "P1").NetBindings);
        Assert.Contains("RF_in_2", bindings);
        Assert.DoesNotContain("p1", bindings);
    }

    [Fact]
    public void ANewStackupLayer_IsLikeTheLastOfItsKind_OrTheGenericBoard()
    {
        var vm = new TechEditorViewModel(Path.Combine(_root, "t.ctech"), new Technology { Name = "T" });
        vm.AddConductorLayerCommand.Execute(null);
        vm.AddDielectricLayerCommand.Execute(null);
        var (metal, first) = (vm.Working.Stackup.Layers[0], vm.Working.Stackup.Layers[1]);
        Assert.Equal((35_000L, ConductorMaterials.Copper.SigmaSm), (metal.ThicknessDbu, metal.SigmaSm));
        Assert.Equal((1_778_000L, 4.4, 0.02), (first.ThicknessDbu, first.Epsr, first.TanD));

        var core = vm.Working.Stackup.Layers[1];
        core.ThicknessDbu = 508_000;
        (core.Epsr, core.TanD) = (3.66, 0.0037);
        vm.AddDielectricLayerCommand.Execute(null);
        var second = vm.Working.Stackup.Layers[^1];
        Assert.Equal((508_000L, 3.66, 0.0037), (second.ThicknessDbu, second.Epsr, second.TanD));
    }
}
