// ================================================================
//  EditorRound4PropertiesTests.cs — the 3D editor's fourth round of owner feedback, the Inspector half: a wire's diameter
//  is read and typed in the display unit; a vector's components share one line and nothing is listed twice; a wire's loop
//  height and span are edited as wBond's are; and Make Port offers only the kind the face can take.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EditorRound4PropertiesTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r4props-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public EditorRound4PropertiesTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void AWireDiameter_FollowsTheDisplayUnit_ShownAndTyped_AndIsStoredInMicrons()
    {
        var vm = Open([Die(), Lead(), Wire()], LayoutUnit.Mil);
        Select(vm, "w1");
        var p = vm.Properties;
        var d = p.Fields.Single(f => f.Label == "Diameter");
        Assert.Equal("1", d.Text);                                           // the 1 mil default, in mil
        Assert.Equal("mil", p.FieldRows.Single(r => r.Label == "Diameter").Unit);

        d.Text = "2";
        p.CommitField(d);
        Settle(vm);
        Assert.Equal("", p.Error);
        Assert.Equal(50.8, ((C3dWire)vm.Document.Objects[^1]).DiameterUm!.Value, 9);
    }

    [Fact]
    public void ABox_PutsEachVectorOnOneLine_AndListsNoDimensionTwice()
    {
        var vm = Open([Die(), Lead()], LayoutUnit.Um);
        Select(vm, "die");
        var p = vm.Properties;
        foreach (string group in new[] { "Placement origin", "Corner", "Size" })
            Assert.Equal(["x", "y", "z"], p.FieldRows.Single(r => r.Label == group).Fields.Select(f => f.Axis));
        Assert.DoesNotContain(p.Rows, r => r.Label is "Corner" or "Size");
    }

    [Fact]
    public void AWiresSpanAndLoopHeight_AreOneEntryEach_AndASpanOffEveryPadIsRefused()
    {
        var vm = Open([Die(), Lead(), Wire()], LayoutUnit.Um);
        Select(vm, "w1");
        var p = vm.Properties;
        Assert.Equal("600", p.WireSpan);
        int entries = vm.UndoEntries;

        p.WireSpan = "580";
        p.CommitWireSpan();
        Settle(vm);
        Assert.Equal("", p.Error);
        var w = (C3dWire)vm.Document.Objects[^1];
        Assert.Equal((50 * Um, 630 * Um), (w.Points[0].X, w.Points[^1].X));    // the start stays, the end moves
        Assert.Equal(entries + 1, vm.UndoEntries);

        p.WireSpan = "900";                                                     // x = 950: nothing to bond to
        p.CommitWireSpan();
        Assert.Contains("would be over no pad", p.Error, StringComparison.Ordinal);
        Assert.Equal(entries + 1, vm.UndoEntries);

        p.Reload();
        p.WireLoopHeight = "250";
        p.CommitWireLoopHeight();
        Settle(vm);
        Assert.Equal("", p.Error);
        w = (C3dWire)vm.Document.Objects[^1];
        Assert.InRange(vm.WireLoopHeightDbu(w)!.Value, 250 * Um - 1, 250 * Um + 1);
        Assert.Equal(630 * Um, w.Points[^1].X);                                 // x and y kept
        Assert.Equal(entries + 2, vm.UndoEntries);
    }

    [Fact]
    public void MakePort_OffersOnlyTheKindTheFaceCanTake_WithTheResolversReasonForTheOther()
    {
        // A gap block between a ground and a trace: its end face bridges the two (lumped), and is inside the air box.
        var vm = Open(
        [
            new C3dBox { Name = "gnd", Material = "Gold", Min = new(0, 0, 0), Size = new(200 * Um, 50 * Um, 5 * Um) },
            new C3dBox { Name = "gap", Material = "Fill", Min = new(0, 0, 5 * Um), Size = new(20 * Um, 50 * Um, 20 * Um) },
            new C3dBox { Name = "trace", Material = "Gold", Min = new(0, 0, 25 * Um), Size = new(200 * Um, 50 * Um, 5 * Um) },
        ], LayoutUnit.Um);
        var v = vm.Viewer;
        var gap = vm.SceneObject("gap")!;
        int xmax = Enumerable.Range(0, 6).Single(i => gap.FaceName(i) == "xmax");
        v.SelectMode = Scene3DSelectMode.Face;
        v.SetSelection([Scene3DItem.OfFace(gap.Id, xmax)]);
        var make = v.ContextMenuItems().Single(i => i.Header == "Make Port").Children!;
        var lumped = make.Single(i => i.Header == "Lumped");
        var wave = make.Single(i => i.Header == "Wave");
        Assert.True(lumped.Enabled);
        Assert.False(wave.Enabled);
        Assert.Contains("on neither of the air box's x faces", wave.Tip, StringComparison.Ordinal);
    }

    private static C3dBox Die() => new() { Name = "die", Material = "Gold", Min = new(0, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) };

    private static C3dBox Lead() => new() { Name = "lead", Material = "Gold", Min = new(600 * Um, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) };

    private static C3dWire Wire() => new()
    {
        Name = "w1", Material = "Gold",
        Points = [new(50 * Um, 20 * Um, 20 * Um), new(350 * Um, 20 * Um, 200 * Um), new(650 * Um, 20 * Um, 20 * Um)],
    };

    private static void Select(C3dEditorViewModel vm, string name)
    {
        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == name);
        vm.Properties.Reload();
        Assert.True(vm.Properties.IsEditable);
    }

    private C3dEditorViewModel Open(List<C3dObject> objects, LayoutUnit unit)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }, new TechMaterial { Name = "Fill", Epsr = 2 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument { DisplayUnit = unit, SnapDbu = 1 * Um, Objects = objects });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Settle(vm);
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");
}
