// 3D editor bugs round 6 — the setup panel and the setup cards of a .c3d (owner's list, 2026-09-28).
//
// The Mesh button meshed for circuitRF's own kernels, which no 3D solver reads, and the Stackup group could only be empty
// in a .c3d, whose geometry is the 3D model. A double-click on a setup card could land on a selectable fidelity line and
// open nothing.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.ThreeD;

public class EditorRound6SetupTests
{
    [Fact]
    public void ThreeDSetup_HidesTheMeshButton_AndA_C3dSetupHidesTheStackup()
    {
        var embedded = new EmSetupEditorViewModel("model.c3d", new EmSetup { Name = "S1", Solver3D = Em3dSolver.Palace }, embedded: true);
        Assert.False(embedded.ShowMeshButton);
        Assert.False(embedded.ShowStackup);

        // A .cem keeps its stackup (the 3D generator builds from it) and shows Mesh only while its own kernel runs.
        var cem = new EmSetupEditorViewModel("panel.cem", new EmSetup { Name = "panel", AnalysisKind = EmAnalysisKind.Planar });
        Assert.True(cem.ShowMeshButton);
        Assert.True(cem.ShowStackup);
        var raised = new List<string?>();
        cem.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        cem.Solver3DChoice = EmSetupEditorViewModel.Solver3DChoices.First(c => c.Value == Em3dSolver.Palace);
        Assert.False(cem.ShowMeshButton);
        Assert.Contains(nameof(EmSetupEditorViewModel.ShowMeshButton), raised);
        Assert.True(cem.ShowStackup);

        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/Layout/EmSetupEditorView.axaml"));
        Assert.Contains("Name=\"MeshButton\" Margin=\"4,0,0,0\"\n                        IsVisible=\"{Binding ViewModel.ShowMeshButton}\"",
                        xaml.Replace("\r\n", "\n"), StringComparison.Ordinal);
        int stackup = xaml.IndexOf("Stackup: SHOWN", StringComparison.Ordinal);
        Assert.Contains("ViewModel.ShowStackup", xaml[stackup..(stackup + 400)], StringComparison.Ordinal);
    }

    [Fact]
    public void SetupCard_DoubleClickIsReadOffThePress_AndNoCardTextIsSelectable()
    {
        string root = RepoRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src/Ui/Views/ThreeD/C3dSetupAnalysesView.axaml"));
        string code = File.ReadAllText(Path.Combine(root, "src/Ui/Views/ThreeD/C3dSetupAnalysesView.axaml.cs"));
        Assert.DoesNotContain("<SelectableTextBlock", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleTapped", xaml, StringComparison.Ordinal);
        Assert.Contains("AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel, handledEventsToo: true)", code, StringComparison.Ordinal);
        Assert.Contains("ClickCount != 2", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
