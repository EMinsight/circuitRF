// ================================================================
//  C3dGroupsTests.cs — .c3d groups: objects and instances gathered under a name, groups inside groups. A group is the
//  members' own `Group` path (C3dGroups), so it is saved in the file, and every group edit is one undo entry. A click in
//  the view selects the whole top-most group; the Inspector edits it as one thing; Ungroup takes apart only the outermost.
//  Pixels were not seen: these read the model and the view model.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class C3dGroupsTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-groups-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public C3dGroupsTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── the model ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AGroupSitsWhereItsUnitsMeet_UngroupDropsOnlyItsOwnLevel_AndARenameKeepsTheRest()
    {
        var doc = new C3dDocument { Objects = [Box("a", 0, "pa"), Box("b", 1, "pa/match"), Box("c", 2), Box("d", 3, "pa")] };
        C3dMemberRef M(int i) => new(false, i);

        Assert.Equal([new C3dGroupUnit(M(0), "pa")], C3dGroups.Units(doc, [M(0), M(1), M(3)]));      // all of pa: one unit
        var units = C3dGroups.Units(doc, [M(1), M(2)]);
        Assert.Equal([new C3dGroupUnit(M(1), "pa/match"), new C3dGroupUnit(M(2), null)], units);    // match whole; c alone

        // b's group and c meet at the top: the new group is there, and match goes inside it whole.
        Assert.Equal([(M(1), "G/match"), (M(2), "G")], C3dGroups.Group(doc, units, "G").Select(x => (x.Member, x.Path)));
        // Two of pa's members meet inside pa.
        Assert.Equal([(M(0), "pa/G"), (M(3), "pa/G")],
                     C3dGroups.Group(doc, C3dGroups.Units(doc, [M(0), M(3)]), "G").Select(x => (x.Member, x.Path)));

        Assert.Equal([(M(0), null), (M(1), "match"), (M(3), null)], C3dGroups.Ungroup(doc, "pa").Select(x => (x.Member, x.Path)));
        Assert.Equal([(M(1), "pa/m2")], C3dGroups.Rename(doc, "pa/match", "m2").Select(x => (x.Member, x.Path)));
        Assert.Equal(["pa", "pa/match"], C3dGroups.All(doc).Select(g => g.Path));
    }

    [Fact]
    public void ACopyOfAWholeGroupIsANewGroupAtEveryLevel_AMemberCopiedAloneStaysInItsGroup()
    {
        var doc = new C3dDocument { Objects = [Box("a", 0, "pa"), Box("b", 1, "pa/match"), Box("c", 2, "other"), Box("d", 3, "other")] };
        var used = C3dGroups.Names(doc);
        var copy = C3dGroups.CopyPaths(doc, [new C3dGroupUnit(new(false, 0), "pa")], used);
        Assert.Equal("pa2", copy("pa"));
        Assert.Equal("pa2/match2", copy("pa/match"));
        Assert.Equal("other", copy("other"));                                      // c copied alone keeps its group
        Assert.Equal("pa3", C3dGroups.CopyPaths(doc, [new C3dGroupUnit(new(false, 0), "pa")], used)("pa"));   // the next copy
    }

    [Fact]
    public void TheFileSaysEachMembersGroup_AndCheckRefusesAGroupNamedInTwoPlaces()
    {
        var doc = new C3dDocument
        {
            Objects = [Box("a", 0, "pa/match"), Box("b", 1)],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../die", Group = "pa" }],
        };
        string text = C3dPersistence.Serialize(doc);
        Assert.Contains("\"Group\": \"pa/match\"", text);
        Assert.Equal(2, text.Split("\"Group\"").Length - 1);                           // b says nothing: it is in none
        var back = C3dPersistence.Deserialize(text);
        Assert.Equal(["pa/match", null], back.Objects.Select(o => o.Group));
        Assert.Equal("pa", back.Instances[0].Group);
        Assert.DoesNotContain(C3dValidation.Validate(back), d => d.Id.StartsWith("c3d.group", StringComparison.Ordinal));

        back.Objects[1].Group = "other/match";
        Assert.Contains(C3dValidation.Validate(back), d => d.Id == "c3d.group.duplicate");
    }

    [Fact]
    public void GroupIntoCell_GivesTheInstanceTheGroupItsContentsShared_AndTheCellKeepsOnlyWhatIsBelowIt()
    {
        string parent = Path.Combine(_root, "cells");
        Directory.CreateDirectory(parent);
        var doc = new C3dDocument { Objects = [Box("a", 0, "pa/match"), Box("b", 1, "pa"), Box("c", 2)] };
        var (placed, _, c3d, why) = C3dHierarchy.GroupIntoCell(doc, Path.Combine(parent, "Top", "3d", "Top.c3d"), [0, 1], [], "Stage", parent, default);
        Assert.Null(why);
        Assert.Equal("pa", placed!.Group);
        Assert.Equal(["match", null], C3dPersistence.LoadFromFile(c3d!).Objects.Select(o => o.Group));
    }

    // ── the editor ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GroupObjects_IsOneUndoEntry_ListsTheGroupOnceInTheTree_WithAnInstance_AndIsSaved()
    {
        var vm = Open(Doc(withInstance: true), out string path);
        vm.Viewer.SetSelection([Item(vm, "a"), Item(vm, "d"), .. vm.Viewer.Scene.Objects.Where(o => vm.InstanceOf(o) == "U1").Select(o => Scene3DItem.OfObject(o.Id))]);
        int entries = vm.UndoEntries;
        vm.GroupSelection();
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(["Group1", null, null, "Group1"], vm.Document.Objects.Select(o => o.Group));
        Assert.Equal("Group1", vm.Document.Instances[0].Group);

        var groups = vm.Tree.Single(g => g.Role == C3dTreeGroupRole.Groups);
        var row = Assert.Single(groups.Items);
        Assert.Equal(["a", "d", "U1"], row.Children.Select(c => c.Name));
        Assert.Equal(["b", "c"], vm.Tree.Where(g => g.Role != C3dTreeGroupRole.Groups).SelectMany(g => g.Items)
                                        .Where(i => i.ObjectIndex >= 0 || i.InstanceIndex >= 0).Select(i => i.Name).OrderBy(n => n));
        Assert.Same(row, vm.SelectedTreeItem);                                       // the new group is the selection
        Assert.True(vm.Properties.IsGroup);

        Assert.Null(vm.Save());
        var reopened = Reopen(path);
        Assert.Equal("Group1", reopened.Document.Objects[3].Group);
        Assert.Equal("Group1", reopened.Document.Instances[0].Group);

        vm.UndoRedo.Undo();
        Assert.All(vm.Document.Objects, o => Assert.Null(o.Group));
        Assert.DoesNotContain(vm.Tree, g => g.Role == C3dTreeGroupRole.Groups);
        vm.UndoRedo.Redo();
        Assert.Equal("Group1", vm.Document.Instances[0].Group);
    }

    [Fact]
    public void AClickOnAMember_SelectsTheWholeGroup_AndTheInspectorEditsItAsOne()
    {
        var doc = Doc();
        doc.Objects[0].Group = doc.Objects[3].Group = "PA";            // a Copper at x 0, d Alumina at x 600 µm
        var vm = Open(doc, out _);

        Click(vm, "a");
        Assert.Equal(["a", "d"], vm.Viewer.SelectedObjects().Select(o => o.Name));
        Assert.True(vm.SelectedTreeItem is { IsGroup: true, Name: "PA" });
        var p = vm.Properties;
        Assert.True(p.IsGroup);
        Assert.Null(p.Material);
        Assert.Equal(C3dPropertiesViewModel.Various, p.MaterialPlaceholder);

        int entries = vm.UndoEntries;
        p.Material = "Copper";                                           // every member, one entry
        Assert.Equal(["Copper", "Copper"], new[] { vm.Document.Objects[0].Material, vm.Document.Objects[3].Material });
        Assert.Equal(entries + 1, vm.UndoEntries);
        Settle(vm);

        var cornerX = vm.Properties.Fields.Single(f => f.Label == "Corner x");
        cornerX.Text = "500";                                            // the box's corner: every member moves by 500 µm
        vm.Properties.CommitField(cornerX);
        Assert.Equal([500 * Um, 500 * Um], new[] { vm.Document.Objects[0].Placement.Origin.X, vm.Document.Objects[3].Placement.Origin.X });
        Assert.Equal(0, vm.Document.Objects[1].Placement.Origin.X);      // b is not in it

        vm.Properties.NameText = "Stage1";
        vm.Properties.CommitName();
        Assert.Equal(["Stage1", "Stage1"], new[] { vm.Document.Objects[0].Group, vm.Document.Objects[3].Group });
    }

    [Fact]
    public void GroupsNest_AndUngroupTakesApartOnlyTheOutermost()
    {
        var vm = Open(Doc(), out _);
        vm.Viewer.SetSelection([Item(vm, "a"), Item(vm, "b")]);
        vm.GroupSelection();                                              // Group1: a, b
        vm.Viewer.SetSelection([Item(vm, "c"), Item(vm, "d")]);
        vm.GroupSelection();                                              // Group2: c, d
        Click(vm, "a");
        ShiftClick(vm, "c");                                              // both groups, whole
        vm.GroupSelection();
        Assert.Equal(["Group3/Group1", "Group3/Group1", "Group3/Group2", "Group3/Group2"], vm.Document.Objects.Select(o => o.Group));

        Click(vm, "d");                                                   // any of it: the top-most group
        Assert.Equal(4, vm.Viewer.SelectedObjects().Count);
        Assert.Contains(vm.Viewer.ContextMenuItems(), m => m.Header == "Ungroup Group3  (Ctrl/Cmd+Shift+G)");
        vm.UngroupSelection();
        Assert.Equal(["Group1", "Group1", "Group2", "Group2"], vm.Document.Objects.Select(o => o.Group));

        vm.UndoRedo.Undo();
        Assert.Equal("Group3/Group2", vm.Document.Objects[3].Group);
    }

    [Fact]
    public void GroupObjects_IsDisabledForOneGroupOrOneObjectAlone_InBothMenus_AndTheKeyAddsNoUndoStep()
    {
        var doc = Doc();
        doc.Objects[0].Group = doc.Objects[1].Group = "PA";
        var vm = Open(doc, out _);
        C3dTreeItem GroupRow() => vm.Tree.Single(g => g.Role == C3dTreeGroupRole.Groups).Items.Single();
        static Viewer3DMenuItem GroupItem(IEnumerable<Viewer3DMenuItem> items) => items.Single(m => m.Header.StartsWith("Group Objects", StringComparison.Ordinal));

        Click(vm, "a");                                                   // the group PA, alone
        Assert.False(GroupItem(vm.Viewer.ContextMenuItems()).Enabled);
        vm.SelectedTreeItem = GroupRow();                                 // its row, right-clicked
        Assert.False(GroupItem(vm.TreeMenuItems(GroupRow())).Enabled);
        int entries = vm.UndoEntries;
        vm.GroupSelection();                                              // Ctrl/Cmd+G
        Assert.Equal(entries, vm.UndoEntries);
        Assert.Contains("already a group", vm.StatusMessage);

        Click(vm, "c");                                                   // one object alone
        Assert.False(GroupItem(vm.Viewer.ContextMenuItems()).Enabled);
        ShiftClick(vm, "a");                                              // c and the group: two things
        Assert.True(GroupItem(vm.Viewer.ContextMenuItems()).Enabled);
    }

    [Fact]
    public void DuplicatingAGroup_MakesANewGroup_AndDeletingAGroupTakesItsInstanceToo()
    {
        var doc = Doc(withInstance: true);
        doc.Objects[0].Group = doc.Objects[1].Group = doc.Instances[0].Group = "PA";
        var vm = Open(doc, out _);

        Click(vm, "a");
        var targets = vm.Targets();
        Assert.True(vm.InsertCopies(targets, [C3dTransform.Translation(new C3dPoint3(0, 2000 * Um, 0))], "Duplicate PA"));
        Assert.Equal(["PA", "PA", null, null, "PA2", "PA2"], vm.Document.Objects.Select(o => o.Group));
        Assert.Equal(["PA", "PA2"], vm.Document.Instances.Select(i => i.Group));
        Settle(vm);

        Click(vm, "b");
        Assert.True(vm.DeleteSelection());
        Assert.Equal(["c", "d", "a2", "b2"], vm.Document.Objects.Select(o => o.Name));
        Assert.Equal("PA2", Assert.Single(vm.Document.Instances).Group);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(string name, long x, string? group = null, string material = "Copper")
        => new() { Name = name, Material = material, Group = group, Min = new C3dPoint3(x * 200 * Um, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, 100 * Um) };

    private static C3dDocument Doc(bool withInstance = false) => new()
    {
        Objects = [Box("a", 0), Box("b", 1), Box("c", 2), Box("d", 3, material: "Alumina")],
        Instances = withInstance ? [new C3dInstance { Name = "U1", CellRef = "../../die", Placement = new C3dPlacement { Origin = new C3dPoint3(0, 500 * Um, 0) } }] : [],
    };

    private static Scene3DItem Item(C3dEditorViewModel vm, string name) => Scene3DItem.OfObject(vm.SceneObject(name)!.Id);

    /// <summary>A click in the view on <paramref name="name"/>: the pick under the cursor, then the click.</summary>
    private static void Click(C3dEditorViewModel vm, string name, bool shift = false)
    {
        vm.Viewer.OnPicked(vm.SceneObject(name)!.Id, 0, Vector3.Zero, true);
        vm.Viewer.Click(shift);
    }

    private static void ShiftClick(C3dEditorViewModel vm, string name) => Click(vm, name, shift: true);

    private C3dEditorViewModel Open(C3dDocument doc, out string path)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Alumina", Epsr = 9.8 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string die = Path.Combine(ws, "die", "3d");
        Directory.CreateDirectory(die);
        C3dPersistence.SaveToFile(Path.Combine(die, "die.c3d"), new C3dDocument { Objects = [Box("chip", 0)] });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return Reopen(path);
    }

    private C3dEditorViewModel Reopen(string path)
    {
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!, ".cws");
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => cws, _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
}
