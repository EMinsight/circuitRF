// ================================================================
//  EditorRound3PropertiesTests.cs — the 3D editor's third round of owner feedback, the Properties half: a box's size is
//  offered as X, Y and Z size and resizes it; a bond wire's diameter, bonds and every point are editable, each typed
//  point one undo entry, bad text refused, and an end moved off every pad refused exactly as a drag's is.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EditorRound3PropertiesTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r3props-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public EditorRound3PropertiesTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ABox_OffersXYZSize_AndTypingOneResizesIt_InOneEntry_WhileZeroIsRefused()
    {
        var vm = Open();
        Select(vm, "die");
        var p = vm.Properties;
        Assert.Equal(["Corner x", "Corner y", "Corner z", "X size", "Y size", "Z size"],
                     p.Fields.Where(f => !f.Path.StartsWith("Placement.", StringComparison.Ordinal)).Select(f => f.Label));

        int entries = vm.UndoEntries;
        var x = p.Fields.Single(f => f.Label == "X size");
        x.Text = "250";
        p.CommitField(x);
        Settle(vm);
        Assert.Equal("", p.Error);
        Assert.Equal(250 * Um, ((C3dBox)vm.Document.Objects[0]).Size.X);
        Assert.Equal(entries + 1, vm.UndoEntries);

        var z = p.Fields.Single(f => f.Label == "Z size");
        z.Text = "0";
        p.CommitField(z);
        Assert.Equal("Z size is a positive length.", p.Error);
        Assert.Equal(20 * Um, ((C3dBox)vm.Document.Objects[0]).Size.Z);
        Assert.Equal(entries + 1, vm.UndoEntries);
    }

    [Fact]
    public void AWire_ShowsItsDiameterPointsAndBonds_NotAPlacement_AndADiameterWithAUnitIsMicrons()
    {
        var vm = Open(Wire());
        Select(vm, "w1");
        var p = vm.Properties;
        Assert.True(p.IsWire);
        Assert.False(p.IsPlaced);
        Assert.Equal(["Start", "1", "End"], p.WirePoints.Select(r => r.Label));
        Assert.DoesNotContain(p.Fields, f => f.Path.StartsWith("Placement.", StringComparison.Ordinal));
        var d = p.Fields.Single(f => f.Label == "Diameter (µm)");       // unstated: offered at the default it is built with
        Assert.Equal(25.4, double.Parse(d.Text, System.Globalization.CultureInfo.InvariantCulture), 6);

        d.Text = "1.5mil";
        p.CommitField(d);
        Settle(vm);
        Assert.Equal(38.1, ((C3dWire)vm.Document.Objects[^1]).DiameterUm!.Value, 6);

        p.WireStartStyle = BondStyle.Ball;
        Settle(vm);
        Assert.Equal(BondStyle.Ball, ((C3dWire)vm.Document.Objects[^1]).Start.Style);
    }

    [Fact]
    public void AWirePoint_TypedInProperties_MovesInOneEntry_BadTextAndAnEndOffEveryPadAreRefused()
    {
        var vm = Open(Wire());
        Select(vm, "w1");
        var p = vm.Properties;
        int entries = vm.UndoEntries;

        var mid = p.WirePoints[1];
        mid.Z = "abc";
        p.CommitWirePoint(mid);
        Assert.StartsWith("A point is three lengths", p.Error, StringComparison.Ordinal);
        Assert.Equal(entries, vm.UndoEntries);

        mid.Z = "150";
        p.CommitWirePoint(mid);
        Settle(vm);
        Assert.Equal("", p.Error);
        Assert.Equal(150 * Um, ((C3dWire)vm.Document.Objects[^1]).Points[1].Z);
        Assert.Equal(entries + 1, vm.UndoEntries);

        var end = p.WirePoints[^1];
        end.X = "2000";                                                   // nothing is there to bond to
        p.CommitWirePoint(end);
        Assert.Contains("would be over no pad", p.Error, StringComparison.Ordinal);
        Assert.Equal(650 * Um, ((C3dWire)vm.Document.Objects[^1]).Points[^1].X);
        Assert.Equal(entries + 1, vm.UndoEntries);
    }

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

    private C3dEditorViewModel Open(C3dObject? extra = null)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        var objects = new List<C3dObject>
        {
            new C3dBox { Name = "die", Material = "Gold", Min = new(0, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) },
            new C3dBox { Name = "lead", Material = "Gold", Min = new(600 * Um, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) },
        };
        if (extra is not null) objects.Add(extra);
        C3dPersistence.SaveToFile(path, new C3dDocument { DisplayUnit = LayoutUnit.Um, SnapDbu = 1 * Um, Objects = objects });
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
