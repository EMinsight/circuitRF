// ================================================================
//  MenuCleanupTests.cs — the 3D menu cleanup (2026-09-27): the 3D menu offers no STEP items (File ▸ Import / Export has
//  them), has Select All Objects, spells no shortcut into a header (a gesture goes in the menu's shortcut column), and
//  enables each Modify item only when it can act on the selection — read off the predicates the canvas menu uses.
//  WorkspaceWindow cannot be constructed headlessly, so the menu's wiring is read from the .axaml itself.
//  Pixels were not seen.
// ================================================================

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class MenuCleanupTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-3dmenu-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public MenuCleanupTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── the menu's shape (both spellings) ────────────────────────────────────────────────────

    [Fact]
    public void BothThreeDMenus_HaveSelectAllObjects_NoStepImportOrExport_NoShortcutText_AndNoBareNativeKeyEquivalent()
    {
        var xml = XDocument.Parse(ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml"));
        var menus = xml.Descendants()
            .Where(e => (e.Name.LocalName, (string?)e.Attribute("Header")) is ("NativeMenuItem", "3D") or ("MenuItem", "_3D"))
            .ToList();
        Assert.Equal(2, menus.Count);
        foreach (var menu in menus)
        {
            var headers = menu.Descendants().Select(e => (string?)e.Attribute("Header")).OfType<string>().ToList();
            // Import STEP… and Export STEP… are File's only; 3D ▸ STEP holds what acts on an imported object (brief-em3d-129).
            Assert.DoesNotContain(headers, h => h.Contains("STEP…", StringComparison.Ordinal));
            Assert.Contains(headers, h => h.Replace("_", "") == "Split into Solids");
            Assert.Contains(headers, h => h.Replace("_", "") == "Select All Objects");
            Assert.All(headers, h => Assert.DoesNotMatch(@"\(\s*(Ctrl|Cmd|Shift|[A-Z])[^)]*\)$", h));
            Assert.All(headers, h => Assert.DoesNotContain("Ctrl/Cmd", h, StringComparison.Ordinal));
        }
        // A native key equivalent is taken by the menu before any control sees it: never a bare key, and never one
        // keystroke on two native items.
        var native = menus.Single(m => m.Name.LocalName == "NativeMenuItem");
        var gestures = native.Descendants().Select(e => (string?)e.Attribute("Gesture")).OfType<string>().ToList();
        Assert.Equal(["Meta+D", "Meta+G", "Meta+Shift+G"], gestures.Order(StringComparer.Ordinal));
        var allNative = xml.Descendants().Where(e => e.Name.LocalName == "NativeMenuItem")
                           .Select(e => (string?)e.Attribute("Gesture")).OfType<string>().ToList();
        Assert.All(gestures, g => Assert.Single(allNative, x => x == g));
    }

    [Fact]
    public void NoThreeDContextMenuHeader_SpellsItsShortcut()
    {
        foreach (string dir in new[] { "src/Ui/ThreeD", "src/Ui/Viewer3D" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRootDir(), dir), "*.cs", SearchOption.AllDirectories))
                foreach (System.Text.RegularExpressions.Match m in Regex.Matches(File.ReadAllText(file), @"new Viewer3DMenuItem\(\$?""([^""]*)"""))
                    Assert.DoesNotMatch(@"(Ctrl|Cmd)|\s\s\(.+\)$", m.Groups[1].Value);
    }

    // ── enablement ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModifyItems_AreEnabledOnlyWhenTheSelectionCanTakeThem()
    {
        var vm = Open(new C3dDocument { Objects = [Box("a", 0), Box("b", 1), Sheet("s")] });

        Assert.False(vm.HasModifySelection);                                   // nothing selected: Modify is disabled
        Assert.All(new[] { "Move", "Rotate", "Duplicate", "Group", "AlignXMin", "MirrorXY", "ConvertPoly" },
                   w => Assert.False(vm.CanRunModify(w), w));

        Click(vm, "a");                                                        // one box
        Assert.True(vm.HasModifySelection);
        Assert.True(vm.CanRunModify("Move"));
        Assert.True(vm.CanRunModify("Duplicate"));
        Assert.True(vm.CanRunModify("ConvertPoly"));
        Assert.False(vm.CanRunModify("Group"));                                // a group holds two things or more
        Assert.False(vm.CanRunModify("AlignXMin"));                            // align needs two
        Assert.False(vm.CanRunModify("Ungroup"));
        Assert.False(vm.CanExtrude);                                           // a box is not a sheet
        Assert.False(vm.CanRunModify("PushPull"));                             // no face selected

        ShiftClick(vm, "b");                                                   // two boxes
        Assert.True(vm.CanRunModify("Group"));
        Assert.True(vm.CanRunModify("AlignXMin"));
        Assert.False(vm.CanRunModify("ConvertPoly"));                          // one object only

        Click(vm, "s");
        Assert.True(vm.CanExtrude);

        // The menu is asked again when the selection or the mode changes.
        int raised = 0;
        vm.MenuStateChanged += () => raised++;
        vm.Viewer.SetSelection([]);
        Assert.True(raised > 0);
        raised = 0;
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        Assert.True(raised > 0);
    }

    [Fact]
    public void SelectAllObjects_TakesEveryShownObject_InObjectMode_AndIsCtrlOrCmdAOnThePane()
    {
        var vm = Open(new C3dDocument { Objects = [Box("a", 0), Box("b", 1), Box("c", 2)] });
        vm.Viewer.SetVisibleEverywhere(vm.SceneObject("c")!.Id, false);
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;

        Assert.True(vm.Viewer.HandleKey(Key.A, KeyModifiers.Meta, gestureInProgress: false));
        Assert.Equal(Scene3DSelectMode.Object, vm.Viewer.SelectMode);
        Assert.Equal(["a", "b"], vm.Viewer.SelectedObjects().Select(o => o.Name).Order());

        vm.Viewer.SetSelection([]);
        Assert.True(vm.Viewer.HandleKey(Key.A, KeyModifiers.Control, gestureInProgress: false));
        Assert.Equal(2, vm.Viewer.SelectedObjects().Count);
        Assert.False(vm.Viewer.HandleKey(Key.A, KeyModifiers.Meta, gestureInProgress: true));   // a gesture's own keys win
    }

    // ── fixtures (C3dGroupsTests' shape) ─────────────────────────────────────────────────────

    private static C3dBox Box(string name, long x)
        => new() { Name = name, Material = "Copper", Min = new C3dPoint3(x * 200 * Um, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, 100 * Um) };

    private static C3dSheet Sheet(string name) => new()
    {
        Name = name, Material = "Copper",
        Plane = C3dPlane.XY, Offset = 0,
        Rect = new C3dRect { Min = new C3dPoint2(0, 500 * Um), Size = new C3dPoint2(100 * Um, 100 * Um) },
    };

    private static void Click(C3dEditorViewModel vm, string name, bool shift = false)
    {
        vm.Viewer.OnPicked(vm.SceneObject(name)!.Id, 0, Vector3.Zero, true);
        vm.Viewer.Click(shift);
    }

    private static void ShiftClick(C3dEditorViewModel vm, string name) => Click(vm, name, shift: true);

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
        return vm;
    }

    private static string RepoRootDir([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(Path.Combine(RepoRootDir(), relativePath));
}
