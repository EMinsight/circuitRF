// ================================================================
//  ExpressionsEditorGateTests.cs — brief-em3d-51 §8, the editor's gates: Promote (4), Define on the spot (5, with the
//  typed field's preview of the unit trap, 6), and the drag rule on the view model (11). Counters only (overview §1n);
//  the GPU is a recording fake. The headless gates are ExpressionsGateTests.cs.
// ================================================================

using System.Numerics;
using Avalonia.Input;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class ExpressionsEditorGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-expr51e-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public ExpressionsEditorGateTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── gate 4 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_PromoteWritesTheCcellParameterAndLinks_AndOneUndoRemovesBoth()
    {
        var doc = new C3dDocument { Variables = [new C3dVariable { Name = "w", Expression = "10", Unit = "Mil" }], Objects = [Box("pad", 0, 0, 0, 40, 30, 5)] };
        Bind(doc.Objects[0], "Size", 0, "w", "Mil");
        var (vm, ccell) = Open(doc);
        string before = File.ReadAllText(ccell);
        int entries = vm.UndoEntries;

        vm.ShowVariables = true;
        vm.Variables!.Promote(vm.Variables.Rows.Single(r => r.Name == "w"));
        Assert.Equal("", vm.Variables.Error);
        var p = Assert.Single(CellPersistence.LoadFromFile(ccell).Parameters);
        Assert.Equal(("w", "10", "mil"), (p.Name, p.DefaultExpression, p.Unit));
        Assert.True(vm.Document.Variables.Single().Linked);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(C3dNameSource.LinkedVar, vm.Resolution.Names["w"].Source);
        Assert.Equal(10 * 25.4, vm.Resolution.FieldValues[("pad", "Size[0]")] * 1e6, 1e-9);   // values agree by construction

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(before, File.ReadAllText(ccell));
        Assert.Null(vm.Document.Variables.Single().Linked);
    }

    // ── gates 5 and 6 ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_AnUnknownNameIsDefinedOnTheSpot_AsTheRubberBandsWidth_AndOneUndoRemovesBoth()
    {
        var (vm, _) = Open(new C3dDocument { SnapDbu = Um, Objects = [Box("pad", 60, 60, 0, 10, 10, 5)] });
        int objects = vm.Document.Objects.Count, entries = vm.UndoEntries;
        vm.Arm(C3dToolKind.Box);
        ClickAt(vm, 0, 0, 0);
        HoverPlane(vm, 40, 30);
        long band = vm.Tool!.Current(vm.CursorInput())[0]!.Value;
        Assert.Equal(40 * Um, band);

        vm.OpenField(null);
        vm.FieldText = "w";
        Assert.Equal("unknown: w", vm.FieldPreview);
        vm.FieldTab();
        vm.FieldText = "30";
        vm.FieldEnter();
        Assert.True(vm.DefineOpen);
        var row = Assert.Single(vm.DefineRows);
        Assert.Equal("w", row.Name);
        vm.DefineEnter();
        Assert.False(vm.DefineOpen);
        Assert.Equal(2, vm.Tool!.Step);                                                 // the gesture went on

        // Gate 6's preview: a literal beside a unit-bearing name is metres, and the field says so as it is typed.
        vm.OpenField(null);
        vm.FieldText = "2*w + 5";
        Assert.StartsWith("= 5.00008E+06 µm", vm.FieldPreview);                        // 5.00008 m, in µm
        Assert.Contains("above 1 m", vm.FieldPreview);
        vm.FieldText = "20";
        vm.FieldEnter();
        Settle(vm);

        var box = Assert.IsType<C3dBox>(vm.Document.Objects[^1]);
        Assert.Equal(new C3dExpr("w", "Um"), C3dBindings.GetExpr(box, "Size", 0));
        Assert.Equal(band, box.Size.X);
        Assert.Equal(40e-6, vm.Resolution.Names["w"].Value!.Value, 1e-15);
        Assert.Equal(entries + 1, vm.UndoEntries);                                      // the definition and the box: one entry

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(objects, vm.Document.Objects.Count);
        Assert.Empty(vm.Document.Variables);
    }

    // ── gate 11 ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("gap", "gap", false, "25")]       // a bare unlinked VAR: the VAR's expression
    [InlineData("w", "w", true, "25")]            // a bare cell parameter: its .ccell default
    [InlineData("2*l + gap", "l", false, "7.5")]  // linear in one name: its first name, solved; gap is held
    public void Gate11_ADragWritesTheNameItsFieldHolds_AndEveryObjectUsingItFollows(string expr, string writes, bool parameter, string value)
    {
        var (vm, ccell) = Open(DragDocument(expr));
        SelectFace(vm, "a", "zmax");
        Assert.True(vm.DrawKey(Key.N, KeyModifiers.None));
        vm.OpenField(null);
        vm.FieldText = "5";
        vm.FieldEnter();
        Settle(vm);

        Assert.Equal([writes], vm.NamesWritten);
        string written = parameter ? CellPersistence.LoadFromFile(ccell).Parameters.Single(p => p.Name == writes).DefaultExpression
                                   : vm.Document.Variables.Single(v => v.Name == writes).Expression;
        Assert.Equal(value, written);
        var a = (C3dBox)vm.Document.Objects[0];
        Assert.Equal(new C3dExpr(expr, "Um"), C3dBindings.GetExpr(a, "Size", 2));      // the expression is kept
        Assert.Equal(25 * Um, a.Size.Z);                                                // … and now says 20 + 5 µm
        if (writes == "gap") Assert.Equal(25 * Um, ((C3dBox)vm.Document.Objects[1]).Size.X);   // the other user of gap followed

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(20 * Um, ((C3dBox)vm.Document.Objects[0]).Size.Z);
    }

    [Fact]
    public void Gate11_AnExpressionNotLinearInOneName_IsRefused_AndTheDocumentIsByteIdentical()
    {
        var (vm, _) = Open(DragDocument("2*gap*l"));
        string document = C3dPersistence.Serialize(vm.Document);
        SelectFace(vm, "a", "zmax");
        vm.DrawKey(Key.N, KeyModifiers.None);
        vm.OpenField(null);
        vm.FieldText = "5";
        vm.FieldEnter();
        Settle(vm);
        Assert.Equal(1, vm.ExpressionRefusals);
        Assert.Contains("not linear in one name", vm.StatusMessage);
        Assert.True(vm.CanReplaceWithNumber);
        Assert.Equal(document, C3dPersistence.Serialize(vm.Document));

        vm.ReplaceWithNumber();                                                          // the one-click alternative
        Settle(vm);
        Assert.Null(C3dBindings.GetExpr((C3dBox)vm.Document.Objects[0], "Size", 2));
        Assert.Equal(20 * Um, ((C3dBox)vm.Document.Objects[0]).Size.Z);                   // the number it held
    }

    [Fact]
    public void Gate11_ThePreviewMovesEveryObjectTheCommitMoves()
    {
        var (vm, _) = Open(DragDocument("gap"));
        var v = vm.Viewer;
        var resolved = vm.Document.Objects.Select(C3dPersistence.SerializeResolved).ToList();
        SelectFace(vm, "a", "zmax");
        vm.DrawKey(Key.N, KeyModifiers.None);
        long objects = vm.ObjectsElaborated;
        int previews = vm.FacePreviews;
        for (int i = 0; i < 12 && vm.FacePreviews == previews; i++)
        {
            HoverVm(v, 200 + i * 5, 40 + i * 9);
            Settle(vm);
        }
        int shown = vm.FacePreviews - previews;
        Assert.True(shown >= 1, "the preview never moved");
        long previewed = vm.ObjectsElaborated - objects;
        v.Click(false);
        Settle(vm);
        var changed = vm.Document.Objects.Where((o, i) => C3dPersistence.SerializeResolved(o) != resolved[i]).Select(o => o.Name).ToList();
        Assert.Equal(["a", "e"], changed);                                              // the dragged box and the other gap user
        Assert.Equal(changed.Count * shown, previewed);                                 // … and each preview drew both
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Box 'a' 20 µm tall through <paramref name="expr"/>; box 'e' as wide as gap; a cell parameter w (20 µm) when
    /// the expression names it; VARs gap and l chosen so that 'a' is 20 µm. For the product, the VARs are unit-less, so
    /// 2*gap*l is a plain number of µm rather than metres squared.</summary>
    private static C3dDocument DragDocument(string expr)
    {
        var a = Box("a", 0, 0, 0, 40, 30, 20);
        var e = Box("e", 60, 0, 0, 20, 30, 10);
        Bind(a, "Size", 2, expr, "Um");
        Bind(e, "Size", 0, "gap", "Um");
        bool product = expr == "2*gap*l";
        return new C3dDocument
        {
            SnapDbu = Um,
            Variables =
            [
                new C3dVariable { Name = "gap", Expression = product || expr == "2*l + gap" ? "10" : "20", Unit = product ? null : "Um" },
                new C3dVariable { Name = "l", Expression = product ? "1" : "5", Unit = product ? null : "Um" },
            ],
            Objects = [a, e],
        };
    }

    private static C3dBox Box(string name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = "Gold", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private static void Bind(IC3dBindable owner, string property, int k, string expr, string? unit)
        => C3dBindings.SetExpr(owner, C3dBindings.SpecOf(owner.GetType(), property)!, k, new C3dExpr(expr, unit));

    private (C3dEditorViewModel Vm, string Ccell) Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology { Name = "tech", Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }] });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string cell = CellFolder.CreateCellFolder(ws, "Cell");
        string ccell = Path.Combine(cell, CellFolder.CcellFileName);
        if (doc.Objects.Any(o => C3dBindings.BoundOf(o.Name, o).Any(f => C3dExpressionText.References(f.Expr.Expr, "w"))) && !doc.Variables.Any(v => v.Name == "w"))
        {
            var file = CellPersistence.LoadFromFile(ccell);
            file.Parameters.Add(new CcellParameter { Name = "w", DefaultExpression = "20", Unit = "um", Dimension = UnitDimension.Length });
            CellPersistence.SaveToFile(ccell, file);
        }
        string dir = Path.Combine(cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-20 * UmM, -20 * UmM, -20 * UmM), s.ToLocal(120 * UmM, 90 * UmM, 60 * UmM), W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return (vm, ccell);
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)), "the scene never settled");

    private static void SelectFace(C3dEditorViewModel vm, string obj, string face)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        var o = vm.SceneObject(obj)!;
        int f = Enumerable.Range(0, o.FaceNames.Count).Single(i => o.FaceNames[i] == face);
        vm.Viewer.SetSelection([Scene3DItem.OfFace(o.Id, f)]);
    }

    private static void HoverVm(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }

    /// <summary>The cursor over world point (x, y, 0) µm, on the XY drawing plane.</summary>
    private static void HoverPlane(C3dEditorViewModel vm, double x, double y)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, 0), W, H);
        Assert.True(front);
        HoverVm(v, sx, sy);
    }

    private static void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        HoverVm(v, sx, sy);
        v.Click(false);
    }
}
