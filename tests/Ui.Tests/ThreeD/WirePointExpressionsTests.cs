// ================================================================
//  WirePointExpressionsTests.cs — brief-em3d-133, expressions in a wire's points in the editor: the Inspector's point rows
//  take an expression (R-em3d133-1); an end's z may be bound and stays on its pad (D1, R-em3d133-2); every edit that
//  rewrites a wire's points meets a bound component by its stated rule — the offset for a translation (D2), the exact
//  swap for a quarter turn or a mirror (D4), a refusal for Loop height (R-em3d133-4), the strict drag rule for a seat —
//  and an insert or a remove carries later points' expressions (R-em3d131-4).
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class WirePointExpressionsTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-wireexpr-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public WirePointExpressionsTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── gate 1: the apex follows h_loop, typed in the Inspector ──────────────────────────────

    [Fact]
    public void Gate1_AnApexZTypedAsHLoop_IsShownBound_AndTheLoopHeightFollowsHLoop()
    {
        var vm = Open(Wire());
        Select(vm, "w1");
        var apex = vm.Properties.WirePoints[1];
        apex.Z = "h_loop";
        vm.Properties.CommitWirePoint(apex);
        Settle(vm);
        Assert.Equal("", vm.Properties.Error);
        Select(vm, "w1");
        var z = vm.Properties.WirePoints[1].ZField;
        Assert.True(z.IsExpression);
        Assert.Equal("h_loop", z.Text);
        Assert.Equal("= 200 µm", z.ValueText);
        long low = vm.WireLoopHeightDbu(W(vm))!.Value;

        SetVar(vm, "h_loop", "300");
        long high = vm.WireLoopHeightDbu(W(vm))!.Value;
        Assert.InRange(high - low, 90 * Um, 110 * Um);
    }

    // ── gate 2: a foot follows its pad's variable ────────────────────────────────────────────

    [Fact]
    public void Gate2_AFootAtXPadPlus50um_FollowsTheLeadWhenXPadChanges_AndIsNotRefused()
    {
        var w = Wire();
        Bind(w, 2, 0, "x_pad + 50um");
        var vm = Open(w);
        SetVar(vm, "x_pad", "640");
        Assert.Equal(690 * Um, W(vm).Points[^1].X);
        Assert.Equal(640 * Um, ((C3dBox)vm.Document.Objects[1]).Min.X);
        Assert.False(vm.Elaboration!.WireRefusals.ContainsKey("w1"));
    }

    // ── gate 3: D2 through the editor, one test per translation ──────────────────────────────

    [Fact]
    public void Gate3_MovingAWire_WritesTheStepIntoEachExpression_AndTheInverseReadsBack()
    {
        var w = Wire();
        Bind(w, 1, 0, "x_mid");
        Bind(w, 1, 2, "h_loop");
        Bind(w, 2, 0, "x_pad + 50um");
        var vm = Open(w);
        string vars = Vars(vm);
        int i = vm.Document.Objects.Count - 1;
        void Move(long dx) => Assert.True(vm.ApplyTransform([new C3dTarget(false, i)], C3dTransform.Translation(new C3dPoint3(dx, 0, 0)), true, true, "Move"));

        Move(20 * Um);
        Settle(vm);
        Assert.Equal("x_mid + 20um", Expr(vm, 1, 0));
        Assert.Equal("x_pad + 70um", Expr(vm, 2, 0));
        Assert.Equal("h_loop", Expr(vm, 1, 2));                           // an unmoved axis: byte-identical
        Assert.Equal(vars, Vars(vm));
        Move(-40 * Um);
        Move(20 * Um);
        Settle(vm);
        Assert.Equal("x_mid", Expr(vm, 1, 0));
        Assert.Equal("x_pad + 50um", Expr(vm, 2, 0));
        Assert.Equal(vars, Vars(vm));                                     // no variable was rewritten
    }

    [Fact]
    public void Gate3_AVertexMoveOfABoundPoint_WritesTheStep_NotTheName()
    {
        var w = Wire();
        Bind(w, 1, 0, "x_mid");
        Bind(w, 1, 2, "h_loop");
        var vm = Open(w);
        string vars = Vars(vm);
        int entries = vm.UndoEntries;
        var v = vm.Viewer;
        v.SelectMode = Scene3DSelectMode.Vertex;
        v.SetSelection([Scene3DItem.OfVertex(vm.SceneObject("w1")!.Id, v.Scene.ToLocal(350e-6, 20e-6, 200e-6))]);
        vm.StartVertexMove();
        vm.OpenField("20");
        vm.FieldTab();
        vm.FieldText = "0";
        vm.FieldTab();
        vm.FieldText = "0";
        vm.FieldEnter();
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(370 * Um, W(vm).Points[1].X);
        Assert.Equal("x_mid + 20um", Expr(vm, 1, 0));
        Assert.Equal("h_loop", Expr(vm, 1, 2));
        Assert.Equal(vars, Vars(vm));
    }

    [Fact]
    public void Gate3_ASpan_WritesTheEndsStepIntoItsExpression()
    {
        var w = Wire();
        Bind(w, 2, 0, "x_pad + 50um");
        var vm = Open(w);
        string vars = Vars(vm);
        Assert.Null(vm.SetWireSpan(vm.Document.Objects.Count - 1, "620"));
        Settle(vm);
        Assert.Equal(670 * Um, W(vm).Points[^1].X);
        Assert.Equal("x_pad + 70um", Expr(vm, 2, 0));
        Assert.Equal(vars, Vars(vm));
    }

    /// <summary>Align and Align to Face both commit through ApplyTransform's translation, as Move does; Align is driven here.</summary>
    [Fact]
    public void Gate3_Align_WritesTheStepIntoEachExpression()
    {
        var w = Wire();
        Bind(w, 1, 0, "x_mid");
        Bind(w, 1, 2, "h_loop");
        var vm = Open(w);
        string vars = Vars(vm);
        long was = W(vm).Points[1].X;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("w1")!.Id), Scene3DItem.OfObject(vm.SceneObject("die")!.Id)]);
        vm.Align(C3dAxis.X, C3dEditorViewModel.AlignAt.Min);
        Settle(vm);
        long d = W(vm).Points[1].X - was;
        Assert.NotEqual(0, d);
        Assert.Equal(C3dPointExpressions.OffsetExpression(new C3dExpr("x_mid", "Um"), d, LayoutUnit.Um, 1000).Expr, Expr(vm, 1, 0));
        Assert.Equal("h_loop", Expr(vm, 1, 2));
        Assert.Equal(vars, Vars(vm));
    }

    // ── gate 4: D4 ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_AQuarterTurnAndAMirror_SwapExactly_AndTheirInversesReadBack_A30DegreeTurnIsRefused()
    {
        var w = Wire();
        Bind(w, 1, 0, "x_mid");
        var plain = Wire();
        plain.Name = "w2";
        var vm = Open(w, plain);
        int i = vm.Document.Objects.Count - 2;
        var before = C3dBindings.Copy(W(vm).Exprs)!;
        var pivot = new C3dPoint3(350 * Um, 20 * Um, 0);
        bool Turn(C3dTransform t, int at = -1) => vm.ApplyTransform([new C3dTarget(false, at < 0 ? i : at)], t, true, false, "Turn");

        Assert.True(Turn(C3dOperations.Rotation(C3dAxis.Z, 90, pivot)));
        Settle(vm);
        Assert.Equal("x_mid - 330um", Expr(vm, 1, 1));                    // y' = x − px + py
        Assert.Null(C3dBindings.GetExpr(W(vm), "Points[1]", 0));          // x' = −y + px + py, a number
        Assert.True(Turn(C3dOperations.Rotation(C3dAxis.Z, -90, pivot)));
        Settle(vm);
        AssertExprs(before, W(vm));

        Assert.True(Turn(C3dOperations.Mirror(C3dPlane.YZ, pivot)));
        Settle(vm);
        Assert.Equal("-(x_mid) + 700um", Expr(vm, 1, 0));
        Assert.True(Turn(C3dOperations.Mirror(C3dPlane.YZ, pivot)));
        Settle(vm);
        AssertExprs(before, W(vm));

        int entries = vm.UndoEntries;
        Assert.False(Turn(C3dOperations.Rotation(C3dAxis.Z, 30, pivot)));
        Assert.Contains("'w1' point 2 x holds x_mid", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(entries, vm.UndoEntries);
        Assert.True(Turn(C3dOperations.Rotation(C3dAxis.Z, 30, pivot), i + 1));   // the unbound wire turns
    }

    // ── gate 5: Loop height, and the strict drag rule for a seat ─────────────────────────────

    [Fact]
    public void Gate5_LoopHeight_IsRefusedOnABoundInterior_AndRunsOnAWireWhoseOnlyExpressionsAreItsEndsZ()
    {
        var bound = Wire();
        Bind(bound, 1, 2, "h_loop");
        var vm = Open(bound);
        int i = vm.Document.Objects.Count - 1;
        string doc = C3dPersistence.Serialize(vm.Document);
        int entries = vm.UndoEntries;
        string? why = vm.SetWireLoopHeight(i, "150");
        Assert.StartsWith("'w1' point 2 z holds h_loop", why, StringComparison.Ordinal);
        Assert.Contains("Replace it with a number, or edit the expression", why, StringComparison.Ordinal);
        Assert.Equal(doc, C3dPersistence.Serialize(vm.Document));
        Assert.Equal(entries, vm.UndoEntries);

        var ends = Wire();
        Bind(ends, 0, 2, "t_die");
        Bind(ends, 2, 2, "t_die");
        vm = Open(ends);
        Assert.Null(vm.SetWireLoopHeight(i, "150"));
        Settle(vm);
        Assert.InRange(vm.WireLoopHeightDbu(W(vm))!.Value, 150 * Um - 1, 150 * Um + 1);
        Assert.Equal("t_die", Expr(vm, 0, 2));
        Assert.Equal("t_die", Expr(vm, 2, 2));
    }

    [Fact]
    public void Gate5_ReseatOfABoundEndThatMisses_RewritesABareName_AndRefusesACompound()
    {
        var bare = Wire();
        Bind(bare, 0, 2, "z_end");                                        // 25 µm: 5 µm above the die
        var vm = Open(bare);
        Select(vm, "w1");
        int entries = vm.UndoEntries;
        vm.RunModify("ReseatWires");
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal("z_end", Expr(vm, 0, 2));
        Assert.Equal("20", vm.Document.Variables.Single(v => v.Name == "z_end").Expression);
        Assert.Equal(20 * Um, W(vm).Points[0].Z);

        var compound = Wire();
        Bind(compound, 0, 2, "t_die + 5um");
        vm = Open(compound);
        Select(vm, "w1");
        entries = vm.UndoEntries;
        vm.RunModify("ReseatWires");
        Assert.Contains("'w1' point 1 z holds t_die + 5um", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(entries, vm.UndoEntries);
        Assert.Equal("t_die + 5um", Expr(vm, 0, 2));
    }

    // ── gate 6: D1, an end on its pad's own expression ───────────────────────────────────────

    [Fact]
    public void Gate6_AnEndZOnThePadsOwnExpression_FollowsTDie_AndReseatWritesNothing_AMissIsRefusedWithItsText()
    {
        var w = Wire();
        Bind(w, 0, 2, "t_die");
        var vm = Open(w);
        SetVar(vm, "t_die", "30");
        Assert.Equal(30 * Um, W(vm).Points[0].Z);
        Assert.False(vm.Elaboration!.WireRefusals.ContainsKey("w1"));
        Select(vm, "w1");
        int entries = vm.UndoEntries;
        vm.RunModify("ReseatWires");
        Assert.Equal(entries, vm.UndoEntries);

        // Typed, a miss is refused and the document keeps what it had.
        var start = vm.Properties.WirePoints[0];
        start.Z = "t_die + 5um";
        vm.Properties.CommitWirePoint(start);
        Assert.Contains("w1's start is no longer on a pad", vm.Properties.Error, StringComparison.Ordinal);
        Assert.Contains("Its z is t_die + 5um.", vm.Properties.Error, StringComparison.Ordinal);
        Assert.Equal(entries, vm.UndoEntries);

        // Written another way, elaboration is the judge, with the same sentence.
        Assert.Null(vm.EditNames("bind", (doc, _) =>
        {
            Bind((C3dWire)doc.Objects[^1], 0, 2, "t_die + 5um");
            return null;
        }));
        Settle(vm);
        Assert.Contains("Its z is t_die + 5um.", vm.Elaboration!.WireRefusals["w1"], StringComparison.Ordinal);
    }

    // ── gate 7: insert and remove ────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_InsertAndRemoveCarryLaterPointsExpressions_ThroughUndoAndRedo()
    {
        var w = Wire();
        Bind(w, 1, 2, "h_loop");
        Bind(w, 2, 0, "x_pad + 50um");
        var vm = Open(w);
        int i = vm.Document.Objects.Count - 1;
        var original = C3dBindings.Copy(W(vm).Exprs)!;

        Assert.Null(vm.InsertWirePoint(i, 0));
        Settle(vm);
        Assert.Equal(["Points[2]", "Points[3]"], W(vm).Exprs!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("h_loop", Expr(vm, 2, 2));
        var inserted = C3dBindings.Copy(W(vm).Exprs)!;

        Assert.Null(vm.RemoveWirePoint(i, 1));
        Settle(vm);
        AssertExprs(original, W(vm));
        vm.UndoRedo.Undo();
        Settle(vm);
        AssertExprs(inserted, W(vm));
        vm.UndoRedo.Undo();
        Settle(vm);
        AssertExprs(original, W(vm));
        vm.UndoRedo.Redo();
        Settle(vm);
        AssertExprs(inserted, W(vm));
        Assert.Equal(650 * Um, W(vm).Points[3].X);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static C3dWire Wire() => new()
    {
        Name = "w1", Material = "Gold",
        Points = [new(50 * Um, 20 * Um, 20 * Um), new(350 * Um, 20 * Um, 200 * Um), new(650 * Um, 20 * Um, 20 * Um)],
    };

    private static void Bind(C3dWire w, int point, int k, string expr)
        => C3dBindings.SetExpr(w, C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Points))!.ElementAt(point), k, new C3dExpr(expr, "Um"));

    /// <summary>The first wire in the document (w1).</summary>
    private static C3dWire W(C3dEditorViewModel vm) => vm.Document.Objects.OfType<C3dWire>().First();

    private static string? Expr(C3dEditorViewModel vm, int point, int k) => C3dBindings.GetExpr(W(vm), $"Points[{point}]", k)?.Expr;

    private static string Vars(C3dEditorViewModel vm) => C3dPersistence.SerializeVariables(vm.Document.Variables);

    private static void AssertExprs(Dictionary<string, C3dExpr?[]> want, C3dWire w)
    {
        Assert.Equal(want.Keys.Order(StringComparer.Ordinal), w.Exprs!.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, slots) in want) Assert.Equal(slots, w.Exprs[key]);
    }

    private static void SetVar(C3dEditorViewModel vm, string name, string expression)
    {
        Assert.Null(vm.EditNames($"Set {name}", (doc, _) =>
        {
            doc.Variables.Single(v => v.Name == name).Expression = expression;
            return null;
        }));
        Settle(vm);
    }

    private static void Select(C3dEditorViewModel vm, string name)
    {
        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == name);
        vm.Properties.Reload();
        Assert.True(vm.Properties.IsEditable);
    }

    /// <summary>A die (its height t_die) and a lead (its x at x_pad), both gold, with the wires given.</summary>
    private C3dEditorViewModel Open(params C3dObject[] extra)
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
        var die = new C3dBox { Name = "die", Material = "Gold", Min = new(0, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) };
        C3dBindings.SetExpr(die, C3dBindings.SpecOf(typeof(C3dBox), nameof(C3dBox.Size))!, 2, new C3dExpr("t_die", "Um"));
        var lead = new C3dBox { Name = "lead", Material = "Gold", Min = new(600 * Um, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) };
        C3dBindings.SetExpr(lead, C3dBindings.SpecOf(typeof(C3dBox), nameof(C3dBox.Min))!, 0, new C3dExpr("x_pad", "Um"));
        C3dVariable Var(string n, string e) => new() { Name = n, Expression = e, Unit = "Um" };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            DisplayUnit = LayoutUnit.Um, SnapDbu = 1 * Um,
            Variables = [Var("t_die", "20"), Var("x_pad", "600"), Var("x_mid", "350"), Var("h_loop", "200"), Var("z_end", "25")],
            Objects = [die, lead, .. extra],
        });
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
