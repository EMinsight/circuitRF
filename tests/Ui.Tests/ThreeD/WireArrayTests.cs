// ================================================================
//  WireArrayTests.cs — 3D editor round 4: a bond wire's own array, a row of identical wires. A wire with no array is
//  written as it always was; a row elaborates into one wire per element, each named w1[k] and refused by that name;
//  Array… on one wire sets the wire's own row (one object, one undo entry) and refuses a second axis; the Inspector
//  offers the number of wires on a plain wire and the pitch once it is a row; a rotation turns the pitch with it.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class WireArrayTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-wirearr-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public WireArrayTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void AWireWithNoArray_IsWrittenAsBefore_AndARowWithAnExpressionRoundTrips()
    {
        Assert.DoesNotContain("\"Array\"", C3dPersistence.SerializeObject(Wire()), StringComparison.Ordinal);

        var w = Wire();
        w.Array = new C3dWireArray { Count = 3, Pitch = new(0, 15 * Um, 0) };
        C3dBindings.SetExpr(w.Array, C3dBindings.SpecOf(typeof(C3dWireArray), nameof(C3dWireArray.Count))!, 0, new C3dExpr("n"));
        var doc = new C3dDocument { Objects = [w] };
        var back = (C3dWire)C3dPersistence.Deserialize(C3dPersistence.Serialize(doc)).Objects[0];
        Assert.Equal(new C3dPoint3(0, 15 * Um, 0), back.Array!.Pitch);
        Assert.Equal("n", C3dBindings.GetExpr(back.Array, nameof(C3dWireArray.Count), 0)!.Expr);
    }

    [Fact]
    public void ARow_ElaboratesToOneWirePerElement_EachRefusedByItsOwnName()
    {
        var w = Wire();
        w.Array = new C3dWireArray { Count = 4, Pitch = new(0, 15 * Um, 0) };     // y = 20, 35, 50 on the pads; 65 off them
        var vm = Open(w);
        var e = vm.Elaboration!;
        Assert.Equal(["w1[0]", "w1[1]", "w1[2]"], e.Solids.Where(s => s.Name.StartsWith("w1[", StringComparison.Ordinal)).Select(s => s.Name).Order());
        Assert.All(e.Solids.Where(s => s.Name.StartsWith("w1[", StringComparison.Ordinal)), s => Assert.Equal("w1", e.Provenance[s.Name].ObjectName));
        Assert.Contains("w1[3]'s start is no longer on a pad", e.WireRefusals["w1[3]"], StringComparison.Ordinal);
        Assert.Equal(3, vm.SceneObjectsFor(vm.Document.Objects[^1]).Count());
        Assert.NotNull(vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "w1").Refusal);   // the row is flagged
    }

    [Fact]
    public void ArrayOnOneWire_SetsItsOwnRow_InOneEntry_AndRefusesASecondAxis()
    {
        var vm = Open(Wire());
        Select(vm);
        int entries = vm.UndoEntries;
        vm.OpenArray();
        Assert.Equal(("1", "2"), (vm.ArrayCountX, vm.ArrayCountY));          // across the wire's run, which is along x
        vm.ArrayCountX = "2";
        vm.AcceptArray();
        Assert.Contains("one row", vm.ArrayError, StringComparison.Ordinal);
        Assert.Equal(entries, vm.UndoEntries);

        vm.ArrayCountX = "1";
        vm.ArrayCountY = "3";
        vm.ArrayPitchY = "15";
        vm.AcceptArray();
        Settle(vm);
        Assert.Equal(3, vm.Document.Objects.Count);                          // one object: no copies were written
        var row = ((C3dWire)vm.Document.Objects[^1]).Array!;
        Assert.Equal((3L, new C3dPoint3(0, 15 * Um, 0)), (row.Count, row.Pitch));
        Assert.Equal(entries + 1, vm.UndoEntries);
    }

    [Fact]
    public void TheInspector_OffersTheNumberOfWires_TypingOneMakesTheRow_ThenThePitchIsEditable()
    {
        var vm = Open(Wire());
        Select(vm);
        var p = vm.Properties;
        var count = p.Fields.Single(f => f.Label == "Number of wires");
        count.Text = "3";
        p.CommitField(count);
        Settle(vm);
        Assert.Equal("", p.Error);
        Assert.Equal(3L, ((C3dWire)vm.Document.Objects[^1]).Array!.Count);

        // The default pitch is four diameters across the run; the pads here are narrower, so it is typed down.
        var pitchY = p.Fields.Single(f => f.Label == "Pitch y");
        Assert.Equal(101.6, double.Parse(pitchY.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        pitchY.Text = "12";
        p.CommitField(pitchY);
        Settle(vm);
        Assert.Equal(12 * Um, ((C3dWire)vm.Document.Objects[^1]).Array!.Pitch.Y);

        // The tree selects every element of the row; the Inspector still edits the one wire.
        vm.SelectedTreeItem = null;
        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "w1");
        Assert.Equal(3, vm.Viewer.Selection.Count);
        Assert.True(p.IsEditable);
    }

    [Fact]
    public void ARotationBakedIntoAWire_TurnsItsPitchWithIt()
    {
        var w = Wire();
        w.Array = new C3dWireArray { Count = 2, Pitch = new(0, 15 * Um, 0) };
        w.Placement.Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 90 }];
        Assert.True(C3dWires.BakePlacement(w));
        Assert.Equal(new C3dPoint3(-15 * Um, 0, 0), w.Array.Pitch);
    }

    private static C3dWire Wire() => new()
    {
        Name = "w1", Material = "Gold",
        Points = [new(50 * Um, 20 * Um, 20 * Um), new(350 * Um, 20 * Um, 200 * Um), new(650 * Um, 20 * Um, 20 * Um)],
    };

    private static void Select(C3dEditorViewModel vm)
    {
        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "w1");
        vm.Properties.Reload();
        Assert.True(vm.Properties.IsEditable);
    }

    private C3dEditorViewModel Open(C3dObject extra)
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
            extra,
        };
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
