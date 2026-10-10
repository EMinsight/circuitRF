// ================================================================
//  WireHeldShapeTests.cs — brief-em3d-135, a wire's Loop height and Span held by an expression: typed in the Inspector,
//  each is bound to the wire's own LoopHeight / Span field and the arch is shaped to it whenever the document resolves,
//  so changing the variable changes the wire. An empty field lets go and keeps the wire as it is. A held field refuses an
//  expression in a point component it overwrites. A file-held wire elaborates to its variable headlessly and re-saves
//  byte for byte.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class WireHeldShapeTests : IDisposable
{
    private const long Um = 1000;
    private const double PerDbu = 1e-9;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-wireheld-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private string _path = "", _cws = "";

    public WireHeldShapeTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ASpanTypedAsAVariable_IsHeld_AndTheEndFollowsTheVariable()
    {
        var vm = Open(Wire());
        Select(vm, "w1");
        vm.Properties.WireSpan = "s_w";
        vm.Properties.CommitWireSpan();
        Settle(vm);
        Assert.Equal("", vm.Properties.Error);
        Select(vm, "w1");
        Assert.Equal("s_w", vm.Properties.WireSpan);
        Assert.Equal("span = 600 µm", vm.Properties.WireShapeNotes);

        SetVar(vm, "s_w", "620");
        Assert.Equal(670 * Um, W(vm).Points[^1].X);
        Assert.Equal(50 * Um, W(vm).Points[0].X);
        Assert.False(vm.Elaboration!.WireRefusals.ContainsKey("w1"));
    }

    /// <summary>The owner's rule for the Inspector: a mistyped name is refused, and the field keeps the value it had — the
    /// span, the loop height, a wire point's x, y and z, and an ordinary dimension (a box's size) alike. Nothing is written and no undo entry is made.</summary>
    [Fact]
    public void AMistypedNameCommittedInTheInspector_IsRefused_AndTheFieldKeepsItsValue()
    {
        var vm = Open(Wire());
        string before = C3dPersistence.Serialize(vm.Document);
        int entries = vm.UndoEntries;
        Select(vm, "w1");
        string loop = vm.Properties.WireLoopHeight;
        vm.Properties.WireSpan = "mySpann";
        vm.Properties.CommitWireSpan();
        Assert.Contains("'mySpann' is not defined", vm.Properties.Error);
        Assert.Equal("600", vm.Properties.WireSpan);
        vm.Properties.WireLoopHeight = "h_lop";
        vm.Properties.CommitWireLoopHeight();
        Assert.Contains("'h_lop' is not defined", vm.Properties.Error);
        Assert.Equal(loop, vm.Properties.WireLoopHeight);
        foreach (int c in (int[])[0, 1, 2])
        {
            Select(vm, "w1");
            var row = vm.Properties.WirePoints[1];
            var field = c switch { 0 => row.XField, 1 => row.YField, _ => row.ZField };
            string was = field.Text;
            field.Text = "x_mdi + 10um";
            vm.Properties.CommitWirePoint(row);
            Assert.Contains("'x_mdi' is not defined", vm.Properties.Error);
            Assert.Equal(was, field.Text);
        }

        Select(vm, "die");
        var size = vm.Properties.Fields.First(f => f.Path == "Size[0]");
        size.Text = "2*x_pd";
        vm.Properties.CommitField(size);
        Assert.Contains("'x_pd' is not defined", vm.Properties.Error);
        Assert.Equal(size.Loaded, size.Text);

        Assert.Equal(entries, vm.UndoEntries);
        Assert.Equal(before, C3dPersistence.Serialize(vm.Document));
    }

    /// <summary>A file whose held span names a VAR it does not have (written by hand, or the VAR deleted since): nothing runs,
    /// but the drawing is everything else plus the row as it stands. The Variables panel offers the name in its Add row, the
    /// Add makes the document dirty, the row follows, and Save writes the VAR beside the held span.</summary>
    [Fact]
    public void AnUndefinedHeldSpanInTheFile_DrawsTheRest_AndTheVariablesPanelDefinesIt()
    {
        var w = Wire();
        w.Array = new C3dWireArray { Count = 5, Pitch = new(0, 8 * Um, 0) };
        C3dBindings.SetExpr(w, C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Span))!, 0, new C3dExpr("mySpan", "Um"));
        var vm = Open(w);
        Assert.False(vm.Elaboration!.Ok);
        Assert.Contains(vm.Elaboration.Solids, s => s.Name == "die");
        Assert.Contains(vm.Elaboration.Solids, s => s.Name == "w1[4]");
        Assert.False(vm.IsDirty);

        var panel = vm.Variables!;
        panel.Reload();
        Assert.Equal("mySpan", panel.NewName);
        Assert.Contains("mySpan", panel.UnknownText);
        panel.NewExpression = "620";
        panel.NewUnit = "Um";
        panel.Add();
        Settle(vm);
        Assert.Equal("", panel.Error);
        Assert.True(vm.IsDirty);
        Assert.Equal(670 * Um, W(vm).Points[^1].X);
        Assert.True(vm.Elaboration!.Ok, string.Join(" | ", vm.Elaboration.Refusals));

        Assert.Null(vm.Save());
        var saved = C3dPersistence.LoadFromFile(_path);
        Assert.Contains(saved.Variables, v => v.Name == "mySpan" && v.Expression == "620");
        Assert.Contains("\"Span\": { \"Expr\": \"mySpan\"", File.ReadAllText(_path));
    }

    [Fact]
    public void ALoopHeightTypedAsAVariable_IsHeld_AndTheElaboratedLoopHeightFollowsIt()
    {
        var vm = Open(Wire());
        Select(vm, "w1");
        vm.Properties.WireLoopHeight = "h_loop";
        vm.Properties.CommitWireLoopHeight();
        Settle(vm);
        Assert.Equal("", vm.Properties.Error);
        Assert.InRange(Report(vm).AssemblyLoopHeightM / PerDbu - 200 * Um, -1.0, 1.0);

        SetVar(vm, "h_loop", "300");
        Assert.InRange(Report(vm).AssemblyLoopHeightM / PerDbu - 300 * Um, -1.0, 1.0);
        Assert.InRange(vm.WireLoopHeightDbu(W(vm))!.Value, 300 * Um - 1, 300 * Um + 1);
    }

    [Fact]
    public void EmptyingAHeldLoopHeightOrSpan_LetsGo_AndKeepsTheWireAsItIs()
    {
        var vm = Open(Wire());
        Select(vm, "w1");
        vm.Properties.WireSpan = "s_w";
        vm.Properties.CommitWireSpan();
        vm.Properties.WireLoopHeight = "h_loop";
        vm.Properties.CommitWireLoopHeight();
        SetVar(vm, "s_w", "620");
        SetVar(vm, "h_loop", "300");
        var shape = W(vm).Points.ToList();

        Select(vm, "w1");
        vm.Properties.WireSpan = "";
        vm.Properties.CommitWireSpan();
        vm.Properties.WireLoopHeight = "";
        vm.Properties.CommitWireLoopHeight();
        Settle(vm);
        Assert.Equal("", vm.Properties.Error);
        Assert.Null(W(vm).Span);
        Assert.Null(W(vm).LoopHeight);
        Assert.Null(W(vm).Exprs);
        Assert.Equal(shape, W(vm).Points);
        Select(vm, "w1");
        Assert.Equal("620", vm.Properties.WireSpan);
        Assert.Equal("300", vm.Properties.WireLoopHeight);

        SetVar(vm, "s_w", "580");
        SetVar(vm, "h_loop", "250");
        Assert.Equal(shape, W(vm).Points);
    }

    [Fact]
    public void AHeldSpan_RefusesAnExpressionInAPointItMoves_AndIsRefusedOnAWireThatHasOne()
    {
        var bound = Wire();
        C3dBindings.SetExpr(bound, C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Points))!.ElementAt(2), 0, new C3dExpr("x_pad + 50um", "Um"));
        var vm = Open(bound);
        int i = vm.Document.Objects.Count - 1, entries = vm.UndoEntries;
        Assert.Contains("point 3 x holds x_pad + 50um", vm.SetWireSpan(i, "s_w"));
        Assert.Equal(entries, vm.UndoEntries);

        Assert.Null(vm.SetWirePoint(i, 2, ["650", null, null]));
        Assert.Null(vm.SetWireSpan(i, "s_w"));
        Assert.Contains("holds its span at s_w", vm.SetWirePoint(i, 1, ["x_mid", null, null]));
    }

    [Fact]
    public void AFileHeldLoopHeight_ElaboratesToItsVariableHeadlessly_AndReSavesByteForByte()
    {
        var w = Wire();
        var spec = C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.LoopHeight))!;
        C3dBindings.SetExpr(w, spec, 0, new C3dExpr("h_loop", "Um"));
        Write(w);
        string text = File.ReadAllText(_path);
        Assert.Contains("\"LoopHeight\": { \"Expr\": \"h_loop\", \"Unit\": \"Um\" }", text);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.LoadFromFile(_path)));

        var e = new C3dElaborator().Elaborate(C3dPersistence.LoadFromFile(_path), _path, _cws);
        Assert.Empty(e.WireRefusals);
        Assert.InRange(e.Wires.Single().AssemblyLoopHeightM / PerDbu - 200 * Um, -1.0, 1.0);

        // Opened, held and saved, the file then reads back unchanged: holding the height it already has writes nothing.
        var vm = Edit();
        string saved = C3dPersistence.Serialize(vm.Document);
        Assert.Equal(saved, C3dPersistence.Serialize(C3dPersistence.Deserialize(saved)));
        vm.Dispose();
        _open.Remove(vm);
        File.WriteAllText(_path, saved);
        Assert.Equal(saved, C3dPersistence.Serialize(Edit().Document));
    }

    /// <summary>The owner's rule for every picture, headless too: <c>render</c> of a view with one mistyped name draws the rest
    /// and says what it left out (exit 0); the elaboration a run takes is still refused, because nothing runs from it.</summary>
    [Fact]
    public void Render_OfAViewWithAMistypedName_DrawsWhatResolved_AndARunStillRefuses()
    {
        var w = Wire();
        C3dBindings.SetExpr(w, C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Span))!, 0, new C3dExpr("mySpann", "Um"));
        Write(w);
        string png = Path.Combine(_root, "partial.png");
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitrf.slnx"))) repo = Path.GetDirectoryName(repo)!;

        var (exit, stdout, stderr) = CircuitRF.Ui.Tests.Em3d.CliProcess.Run(repo, [], ["render", _path, "-o", png, "--iso", "--size", "200x150"]);
        Assert.True(exit == 0, stderr + stdout);
        Assert.True(File.Exists(png));
        Assert.Contains("the picture is what resolved; left out: 'w1' Span = mySpann", stderr);
        // Nothing runs from it: the elaboration a run would take is refused (every run gates on Ok).
        Assert.False(new C3dElaborator().Elaborate(C3dPersistence.LoadFromFile(_path), _path, _cws).Ok);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Die to lead, 600 µm, the apex 180 µm above the feet.</summary>
    private static C3dWire Wire() => new()
    {
        Name = "w1", Material = "Gold",
        Points = [new(50 * Um, 20 * Um, 20 * Um), new(350 * Um, 20 * Um, 200 * Um), new(650 * Um, 20 * Um, 20 * Um)],
    };

    private static C3dWire W(C3dEditorViewModel vm) => vm.Document.Objects.OfType<C3dWire>().First();

    private static CircuitRF.Design.Layout.Em3d.Em3dWireReport Report(C3dEditorViewModel vm) => vm.Elaboration!.Wires.Single(r => r.Name == "w1");

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

    /// <summary>A die and a lead, both gold, 20 µm thick, with the wires given, written to a fresh workspace.</summary>
    private void Write(params C3dObject[] extra)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        _cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(_cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "cell.c3d");
        var die = new C3dBox { Name = "die", Material = "Gold", Min = new(0, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) };
        var lead = new C3dBox { Name = "lead", Material = "Gold", Min = new(600 * Um, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) };
        C3dVariable Var(string n, string e) => new() { Name = n, Expression = e, Unit = "Um" };
        C3dPersistence.SaveToFile(_path, new C3dDocument
        {
            DisplayUnit = LayoutUnit.Um, SnapDbu = 1 * Um,
            Variables = [Var("x_pad", "600"), Var("x_mid", "350"), Var("h_loop", "200"), Var("s_w", "600")],
            Objects = [die, lead, .. extra],
        });
    }

    private C3dEditorViewModel Open(params C3dObject[] extra)
    {
        Write(extra);
        return Edit();
    }

    private C3dEditorViewModel Edit()
    {
        var fake = new PatchRecordingBackend();
        string cws = _cws;
        var vm = new C3dEditorViewModel(_path, C3dPersistence.LoadFromFile(_path), () => fake, () => cws, a => a());
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
