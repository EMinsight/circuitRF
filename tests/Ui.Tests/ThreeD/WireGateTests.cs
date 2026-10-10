// ================================================================
//  WireGateTests.cs — the gate for brief-em3d-50: a bond wire drawn in a .c3d, across hierarchy. Gate 1 (the split,
//  byte for byte) is WireSplitDumpTests. Here: a typed loop height is the measured one (the tool driven through the
//  editor, and once through the viewer's clicks and typed field); a wire from a die pad inside a layout instance to a
//  lead in the parent; no pad, no wire, and Re-seat fixing the vertical case only; the same rings as the .cem route
//  on the Bond wire example; and an array element whose end misses is made and flagged. No solver, no pixel.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.WBond;
using Xunit;
using Point3 = CircuitRF.Engine.Em3d.Point3;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class WireGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;                       // DBU per µm at the default 1000 DBU/µm
    private const double PerDbu = 1e-9;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-wire50-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public WireGateTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 2. the typed loop height is the measured one ─────────────────────────────────────────

    [Theory]
    [InlineData(BondStyle.Ball, BondStyle.Wedge, 0, 8.0)]
    [InlineData(BondStyle.Wedge, BondStyle.Wedge, 0, 8.0)]
    [InlineData(BondStyle.Ball, BondStyle.Wedge, 254, 15.0)]          // the lead's top 10 mil above the die pad's
    public void Gate2_ATypedAssemblyLoopHeight_IsTheLoopHeightMeasuredOnTheElaboratedWire(BondStyle start, BondStyle end, int riseUm, double loopMil)
    {
        var vm = OpenPads(riseUm);
        vm.Arm(C3dToolKind.Wire);
        (vm.WireStartStyle, vm.WireEndStyle) = (start, end);
        var tool = Assert.IsType<WireTool>(vm.Tool);
        Assert.True(tool.Click(Snap(50 * Um, 50 * Um, 20 * Um)).Advanced);
        Assert.True(tool.Click(Snap(650 * Um, 50 * Um, (20 + riseUm) * Um)).Advanced);
        long typed = (long)Math.Round(loopMil * 25.4 * Um);
        var wire = Assert.IsType<C3dWire>(tool.Typed([typed], default).Result);
        Assert.Equal((start, end), (wire.Start.Style, wire.End.Style));

        vm.Document.Objects.Add(wire);
        var e = C3dElaborator.ElaborateOnce(vm.Document, vm.FilePath, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var report = e.Wires.Single(w => w.Name == wire.Name);
        Assert.InRange(report.AssemblyLoopHeightM / PerDbu - typed, -1.0, 1.0);
        Assert.Equal(("die", "lead"), (report.Start.Pad, report.End.Pad));
    }

    /// <summary>The same, through the editor's own input: two clicks on the pads' tops (a snap or the ray's hit), the
    /// typed field's "8mil", Enter — one undo entry, and the elaboration the editor adopts measures 8 mil.</summary>
    [Fact]
    public void Gate2_ThroughTheViewer_TwoClicksAndATypedLoopHeight_DrawOneWireThatMeasuresIt()
    {
        var vm = OpenPads(0);
        vm.Arm(C3dToolKind.Wire);
        vm.WireStartStyle = BondStyle.Ball;
        ClickAt(vm, 50, 50, 20);
        Assert.Equal(1, vm.Tool!.Step);
        ClickAt(vm, 650, 50, 20);
        Assert.Equal(2, vm.Tool!.Step);
        vm.OpenField(null);
        vm.FieldText = "8mil";
        vm.FieldEnter();
        Settle(vm);

        var wire = Assert.IsType<C3dWire>(vm.Document.Objects[^1]);
        Assert.Equal(1, vm.ToolCommits);
        var report = vm.Elaboration!.Wires.Single(w => w.Name == wire.Name);
        Assert.InRange(report.AssemblyLoopHeightM / PerDbu - 203_200, -1.0, 1.0);
    }

    /// <summary>3D editor bugs round 2 — a wire attaches to ANY face of a metal object: in the Front view (no top in
    /// sight) the two side faces are clicked, and each end lands on its object's top, inside its outline. An object with
    /// no material is refused by name, saying why.</summary>
    [Fact]
    public void AnyFaceOfAMetalObject_StartsAndEndsAWire_OnItsTop_AndAMaterialLessOneIsRefusedByName()
    {
        var vm = OpenPads(0, extra: new C3dBox { Name = "bare", Min = new(300 * Um, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) });
        vm.Viewer.StandardViewCommand.Execute(StandardView3D.Front);
        // Snapping is a per-USER preference, shared by every test in the process: put back, whatever happens.
        bool snapWas = vm.Viewer.SnapEnabled;
        vm.Viewer.SnapEnabled = false;                                // a snapped point on a top is taken exactly: not this path
        try
        {
            vm.Arm(C3dToolKind.Wire);
            Assert.Contains("any face", vm.ViewportLine);                  // the prompt, on the viewport's line

            ClickAt(vm, 350, 0, 10);
            Assert.Equal(0, vm.Tool!.Step);
            Assert.Contains("'bare' has no material", vm.ViewportLine);    // the refusal replaces it there

            ClickAt(vm, 50, 0, 7);
            Assert.Equal(1, vm.Tool!.Step);
            ClickAt(vm, 640, 0, 7);
            Assert.Equal(2, vm.Tool!.Step);
            vm.OpenField(null);
            vm.FieldText = "8mil";
            vm.FieldEnter();
            Settle(vm);

            var wire = Assert.IsType<C3dWire>(vm.Document.Objects[^1]);
            var (a, b) = (wire.Points[0], wire.Points[^1]);
            Assert.Equal((20 * Um, 20 * Um), (a.Z, b.Z));
            Assert.InRange(a.X, 0, 100 * Um);
            Assert.InRange(a.Y, 1, 60 * Um);                              // inside the edge, not on it
            Assert.InRange(b.X, 600 * Um, 700 * Um);
            var report = vm.Elaboration!.Wires.Single(w => w.Name == wire.Name);
            Assert.Equal(("die", "lead"), (report.Start.Pad, report.End.Pad));
        }
        finally { vm.Viewer.SnapEnabled = snapWas; }
    }

    /// <summary>Picking an end lights the TOP FACE the end will be seated on, not the whole object under the cursor: over the die's
    /// front face the target is the die's top (its outline at z = 20 µm), and the object's own hover tint is off while the tool is
    /// armed.</summary>
    [Fact]
    public void PickingAnEnd_HighlightsOnlyTheTopFaceItLandsOn()
    {
        var vm = OpenPads(0);
        bool snapWas = vm.Viewer.SnapEnabled;
        vm.Viewer.SnapEnabled = false;
        try
        {
            vm.Arm(C3dToolKind.Wire);
            Assert.True(vm.Viewer.View.HoverHidden);
            HoverAt(vm, 50, 0, 7, withPoint: true);                       // the die's front (y = 0) face
            var overlay = new Viewer3DDrawOverlay();
            vm.FillDrawOverlay(overlay);
            var (picked, pickedFace) = vm.Viewer.LastPick;
            Assert.Equal("ymin", vm.Viewer.Scene.Object(picked)!.FaceName(pickedFace));   // the cursor is on a SIDE face
            var rings = Assert.Single(overlay.TargetFaces);
            var outline = Assert.Single(rings);
            Assert.All(outline, p => Assert.Equal(20 * UmM, p.Z, 12));    // its top, not the face under the cursor
            Assert.Equal((0, 100 * UmM), (outline.Min(p => p.X), outline.Max(p => p.X)), new TupleTolerance(1e-12));
            vm.Disarm();
            Assert.False(vm.Viewer.View.HoverHidden);
        }
        finally { vm.Viewer.SnapEnabled = snapWas; }
    }

    /// <summary>From Top view the loop height can be neither seen nor set with the mouse: once the second end is placed the
    /// height field opens on its own, says what Enter takes, and an empty Enter places the wire at that height.</summary>
    [Fact]
    public void FromTopView_TheLoopHeightFieldOpensAfterTheSecondEnd_AndEnterTakesTheDefault()
    {
        var vm = OpenPads(0);
        vm.Viewer.StandardViewCommand.Execute(StandardView3D.Top);
        bool snapWas = vm.Viewer.SnapEnabled;
        vm.Viewer.SnapEnabled = false;
        try
        {
            vm.Arm(C3dToolKind.Wire);
            ClickAt(vm, 50, 30, 20);
            ClickAt(vm, 650, 30, 20);
            Assert.Equal(2, vm.Tool!.Step);
            Assert.True(vm.FieldOpen);
            Assert.Contains("Enter: ", vm.FieldLabel);
            Assert.Contains("LOOP HEIGHT", vm.ToolPrompt);
            Assert.DoesNotContain("passes through", vm.ToolPrompt);       // nothing between the pads: no false alarm
            vm.FieldEnter();                                              // empty: the default
            Settle(vm);
            Assert.DoesNotContain("passes through", vm.StatusMessage);
            var wire = Assert.IsType<C3dWire>(vm.Document.Objects[^1]);
            Assert.Equal(0, vm.Tool!.Step);                               // still armed, for the next wire
            Assert.InRange(wire.Points.Max(p => p.Z), 20 * Um + 100 * Um, 20 * Um + 200 * Um);   // ~150 µm above the pads
        }
        finally { vm.Viewer.SnapEnabled = snapWas; }
    }

    /// <summary>A part between the pads taller than the default arch: from above the offered height is raised until the arch
    /// clears it, and Enter places a clear wire; a height typed lower than the part is placed, named and drawn red.</summary>
    [Fact]
    public void FromTopView_TheOfferedHeightClearsAPartBetweenThePads_AndATypedLowerOneIsNamed()
    {
        var cap = new C3dBox { Name = "cap", Material = "Gold", Min = new(300 * Um, 0, 0), Size = new(100 * Um, 60 * Um, 400 * Um) };
        var vm = OpenPads(0, extra: cap);
        vm.Viewer.StandardViewCommand.Execute(StandardView3D.Top);
        bool snapWas = vm.Viewer.SnapEnabled;
        vm.Viewer.SnapEnabled = false;
        try
        {
            vm.Arm(C3dToolKind.Wire);
            ClickAt(vm, 50, 8, 20);
            ClickAt(vm, 650, 8, 20);
            Assert.Contains("raised to clear", vm.ToolPrompt);
            Assert.DoesNotContain("passes through", vm.ToolPrompt);
            Assert.Equal(0, ((WireTool)vm.Tool!).SuggestedLoopHeight!.Value % Um);   // rounded up to a whole µm
            vm.FieldEnter();
            Settle(vm);
            var clear = Assert.IsType<C3dWire>(vm.Document.Objects[^1]);
            Assert.True(clear.Points.Max(p => p.Z) > 400 * Um);
            Assert.DoesNotContain("passes through", vm.StatusMessage);

            ClickAt(vm, 50, 52, 20);                                      // far enough from the first for any Settings diameter
            ClickAt(vm, 650, 52, 20);
            vm.FieldText = "100um";                                       // under the cap's 400 µm
            vm.FieldEnter();
            Settle(vm);
            Assert.Contains("passes through", vm.StatusMessage);
            Assert.Contains("'cap'", vm.StatusMessage);
        }
        finally { vm.Viewer.SnapEnabled = snapWas; }
    }

    /// <summary>Pads 2 mm apart in height (the owner's case): the loop height is measured from the LOWER foot, so 150 µm sat below
    /// the higher one and the arch ran straight downhill through its own start pad. The offered height rises above the higher foot.</summary>
    [Fact]
    public void FromTopView_PadsAtDifferentHeights_TheOfferedArchRisesAboveTheHigherFoot()
    {
        var vm = OpenPads(2000);                                          // the lead's top at 2.02 mm, the die's at 20 µm
        vm.Viewer.StandardViewCommand.Execute(StandardView3D.Top);
        bool snapWas = vm.Viewer.SnapEnabled;
        vm.Viewer.SnapEnabled = false;
        try
        {
            vm.DisplayUnit = LayoutUnit.Mil;
            vm.Arm(C3dToolKind.Wire);
            ClickAt(vm, 650, 30, 2020);                                   // start on the HIGH pad
            ClickAt(vm, 50, 30, 20);
            Assert.Equal(0, ((WireTool)vm.Tool!).SuggestedLoopHeight!.Value % 2540);   // rounded up to 0.1 mil
            vm.FieldEnter();
            Settle(vm);
            var wire = Assert.IsType<C3dWire>(vm.Document.Objects[^1]);
            Assert.True(wire.Points.Max(p => p.Z) > 2020 * Um, $"apex {wire.Points.Max(p => p.Z)}");
            Assert.DoesNotContain("passes through", vm.StatusMessage);
            Assert.False(vm.Elaboration!.WireRefusals.ContainsKey(wire.Name));
        }
        finally { vm.Viewer.SnapEnabled = snapWas; }
    }

    /// <summary>W arms the Wire tool (as its toolbar button does) and W again puts it away; mid-wire it is not a tool key.</summary>
    [Fact]
    public void W_ArmsTheWireTool_AndAgainPutsItAway()
    {
        var vm = OpenPads(0);
        var v = vm.Viewer;
        Assert.True(v.HandleKey(Avalonia.Input.Key.W, Avalonia.Input.KeyModifiers.None, false));
        Assert.True(vm.IsWireArmed);
        Assert.True(v.HandleKey(Avalonia.Input.Key.W, Avalonia.Input.KeyModifiers.None, false));
        Assert.False(vm.IsWireArmed);

        bool snapWas = v.SnapEnabled;
        v.SnapEnabled = false;
        try
        {
            v.HandleKey(Avalonia.Input.Key.W, Avalonia.Input.KeyModifiers.None, false);
            ClickAt(vm, 50, 0, 7);                                         // the start placed: a gesture under way
            v.HandleKey(Avalonia.Input.Key.W, Avalonia.Input.KeyModifiers.None, false);
            Assert.True(vm.IsWireArmed);
            Assert.Equal(1, vm.Tool!.Step);
        }
        finally { v.SnapEnabled = snapWas; }
    }

    /// <summary>A new wire is made with Settings ▸ Wirebonds' diameter and points per wire, as the layout view's and the profile's
    /// are — not the diameter of the last wire already in the document (here 3.37 mil, which no Settings value is).</summary>
    [Fact]
    public void ANewWire_TakesItsDiameterAndPointsFromSettings_NotFromTheDocumentsLastWire()
    {
        var old = new C3dWire
        {
            Name = "old", Material = "Gold", DiameterUm = 3.37 * 25.4,
            Points = [new(50 * Um, 50 * Um, 20 * Um), new(350 * Um, 50 * Um, 200 * Um), new(650 * Um, 50 * Um, 20 * Um)],
        };
        var vm = OpenPads(0, extra: old);
        vm.Viewer.StandardViewCommand.Execute(StandardView3D.Top);
        bool snapWas = vm.Viewer.SnapEnabled;
        vm.Viewer.SnapEnabled = false;
        try
        {
            vm.Arm(C3dToolKind.Wire);
            Assert.Equal(CircuitRF.Ui.WBond.WBondDefaults.DiameterNm / 1000.0, vm.WireTemplate.DiameterUm, 6);
            ClickAt(vm, 50, 20, 20);
            ClickAt(vm, 650, 20, 20);
            vm.FieldEnter();
            Settle(vm);
            var drawn = Assert.IsType<C3dWire>(vm.Document.Objects[^1]);
            Assert.NotEqual("old", drawn.Name);
            Assert.Equal(CircuitRF.Ui.WBond.WBondDefaults.Points, drawn.Points.Count);
            Assert.Equal(CircuitRF.Ui.WBond.WBondDefaults.DiameterNm / 1000.0, drawn.DiameterUm ?? C3dWires.DefaultDiameterUm, 3);
        }
        finally { vm.Viewer.SnapEnabled = snapWas; }
    }

    private sealed class TupleTolerance(double tol) : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) <= tol && Math.Abs(a.Item2 - b.Item2) <= tol;
        public int GetHashCode((double, double) v) => 0;
    }

    // ── 3. across hierarchy ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AWireFromADiePadInsideALayoutInstanceToAParentLead_SitsItsFeetOnBothTops()
    {
        string ws = CopyExample(withWBond: false);
        string c3d = WriteC3d(ws, "Pkg", Package(dieZ: 300 * Um));
        var e = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(c3d), c3d, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));

        double dieTop = Top(e, "U1/Pads/2"), leadTop = Top(e, "lead");
        Assert.Equal(300 * UmM + 106 * UmM, dieTop, 12);                  // the die's stack sits at the instance's z
        var report = e.Wires.Single(w => w.Name == "w1");
        Assert.Equal(("U1/Pads/2", dieTop, "lead", leadTop), (report.Start.Pad, report.Start.PadTopM, report.End.Pad, report.End.PadTopM));

        // The start is a ball ON the die pad; the end's foot lies on the lead, its lowest corners at the lead's top exactly.
        var ball = Assert.IsType<Em3dTruncatedSphere>(e.Solids.Single(s => s.Name == "w1/ball/start").Primitive);
        Assert.Equal(dieTop, ball.ZMin);
        var sweep = Assert.IsType<Em3dSweep>(e.Solids.Single(s => s.Name == "w1").Primitive);
        foreach (var ring in sweep.Rings.TakeLast(2)) Assert.Equal(2, ring.Count(q => q.Z == leadTop));
    }

    // ── 4. no pad, no wire; Re-seat is the vertical fix only ─────────────────────────────────

    [Fact]
    public void Gate4_MovingTheDie_RefusesTheWireByNameAndEnd_AndReseatFixesOnlyTheVerticalMove()
    {
        string ws = CopyExample(withWBond: false);
        foreach (var (move, fixable) in new[] { (new C3dPoint3(2000 * Um, 0, 300 * Um), false), (new C3dPoint3(0, 0, 350 * Um), true) })
        {
            var doc = Package(dieZ: 300 * Um);
            doc.Instances[0].Placement.Origin = move;                  // the die moves; the wire keeps its points
            string c3d = WriteC3d(ws, "Pkg", doc);
            var e = C3dElaborator.ElaborateOnce(doc, c3d, null);
            Assert.False(e.Ok);
            Assert.Contains("w1's start is no longer on a pad", e.WireRefusals["w1"], StringComparison.Ordinal);
            Assert.DoesNotContain(e.Solids, s => s.Name == "w1");       // never a foot in mid-air

            var wire = (C3dWire)doc.Objects.Single(o => o.Name == "w1");
            var seated = C3dWires.Reseat(wire, C3dWires.Pads(e.Solids, e.Sheets), doc.DbuPerMicron, out var unseated);
            doc.Objects[doc.Objects.IndexOf(wire)] = seated;
            var again = C3dElaborator.ElaborateOnce(doc, c3d, null);
            Assert.Equal(fixable, again.Ok);
            Assert.Equal(fixable ? new List<string>() : new List<string> { "start" }, unseated);
            if (fixable) Assert.Equal(Top(again, "U1/Pads/2"), again.Wires.Single(w => w.Name == "w1").Start.PadTopM);
        }
    }

    // ── 5. equivalence with the .cem route ───────────────────────────────────────────────────

    [Fact]
    public void Gate5_TheBondWireExampleRedrawnAsAC3dWire_GivesTheCemRoutesRings()
    {
        string ws = CopyExample(withWBond: true);
        string cem = Path.Combine(ws, "Bond wire", "em", "Bond wire 3D.cem");
        var setup = EmSetupPersistence.LoadFromFile(cem);
        var resolved = EmSetupResolver.Resolve(cem, setup.LayoutRef, Path.Combine(ws, ".cws"), new TechnologyCache());
        var a = Em3dGenerator.Generate(setup, resolved.Source!, resolved.Source!.Technology!);
        Assert.True(a.Ok, a.Refusal);
        var expected = Assert.IsType<Em3dSweep>(a.Problem!.Solids.Single(s => s.Name == "wire/G1/1").Primitive);

        // The same wire drawn: the .wBond's points in the world (its z = 0 is the ground's top), ends seated on the pads'
        // tops, the layout placed WITHOUT its .wBond.
        string wbondPath = Path.Combine(ws, "Bond wire", "layout", "Bond wire.wBond");
        var src = WBondIo.ReadFile(wbondPath).AllWires().Single();
        File.Delete(wbondPath);
        double zOrigin = expected.Path[2].Z - src.Points[1].Z * 1e-9;    // an interior point is not moved by the build
        double padTop = a.Problem!.Solids.Single(s => s.Name == "Pads/1").Primitive is Em3dExtrudedPolygon p ? p.ZTop : double.NaN;
        long Dbu(double m) => (long)Math.Round(m / PerDbu, MidpointRounding.AwayFromZero);
        var points = src.Points.Select((q, i) => new C3dPoint3(q.X, q.Y,
            i == 0 || i == src.Points.Count - 1 ? Dbu(padTop) : Dbu(zOrigin + q.Z * 1e-9))).ToList();
        string c3d = WriteC3d(ws, "Redrawn", new C3dDocument
        {
            TechRef = "../../tech/bond-wire-on-alumina.ctech",
            Objects = [new C3dWire
            {
                Name = "w1", Material = src.Material, Points = points, DiameterUm = src.DiameterNm / 1000.0,
                Start = new C3dWireEnd { FootLengthUm = src.FootLengthNm / 1000.0 }, End = new C3dWireEnd { FootLengthUm = src.FootLengthNm / 1000.0 },
            }],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Bond wire", View = C3dInstanceView.Layout }],
        });
        var b = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(c3d), c3d, null);
        Assert.True(b.Ok, string.Join(" ", b.Refusals));
        Assert.DoesNotContain(b.Solids, s => s.Name.StartsWith("U1/wire/", StringComparison.Ordinal));   // no .wBond came in
        var drawn = Assert.IsType<Em3dSweep>(b.Solids.Single(s => s.Name == "w1").Primitive);

        Assert.Equal(expected.Rings.Count, drawn.Rings.Count);
        for (int i = 0; i < expected.Rings.Count; i++)
            for (int k = 0; k < expected.Rings[i].Count; k++)
                foreach (var (x, y) in new[] { (expected.Rings[i][k].X, drawn.Rings[i][k].X), (expected.Rings[i][k].Y, drawn.Rings[i][k].Y),
                                               (expected.Rings[i][k].Z, drawn.Rings[i][k].Z) })
                    Assert.True(Math.Abs(x - y) <= 1e-12 * Math.Max(Math.Abs(x), Math.Abs(y)) + 1e-18, $"ring {i} corner {k}: {x} vs {y}");
    }

    /// <summary>A refused wire has no solid to pick, so Re-Seat takes the wire selected in the tree: one undo entry, and
    /// the flag goes.</summary>
    [Fact]
    public void Gate4_AFlaggedWireSelectedInTheTree_IsReseatedInOneUndoEntry()
    {
        var vm = OpenPads(0, extra: new C3dWire
        {
            Name = "w1", Material = "Gold",                                  // its start 5 µm above the die's top
            Points = [new(50 * Um, 20 * Um, 25 * Um), new(350 * Um, 20 * Um, 200 * Um), new(650 * Um, 20 * Um, 20 * Um)],
        });
        var item = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "w1");   // by material the wire is under Gold
        Assert.Contains("w1's start is no longer on a pad", item.Refusal, StringComparison.Ordinal);

        vm.SelectedTreeItem = item;
        int before = vm.UndoEntries;
        vm.RunModify("ReseatWires");
        Settle(vm);
        Assert.Equal(before + 1, vm.UndoEntries);
        Assert.Equal(20 * Um, ((C3dWire)vm.Document.Objects[^1]).Points[0].Z);
        Assert.Null(vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "w1").Refusal);
    }

    // ── R-em3d50-4. an array of wires ────────────────────────────────────────────────────────

    [Fact]
    public void Array_AnElementWhoseEndMissesItsPad_IsMadeAnyway_AndFlaggedInTheTree()
    {
        var vm = OpenPads(0, leads: 2);                                  // three die pads, two leads
        var wire = new C3dWire
        {
            Name = "w1", Material = "Gold",
            Points = [new(50 * Um, 20 * Um, 20 * Um), new(350 * Um, 20 * Um, 200 * Um), new(650 * Um, 20 * Um, 20 * Um)],
        };
        vm.Document.Objects.Add(wire);
        Assert.True(vm.InsertCopies([new C3dTarget(false, vm.Document.Objects.Count - 1)],
                                    [C3dTransform.Translation(new C3dPoint3(0, 100 * Um, 0)), C3dTransform.Translation(new C3dPoint3(0, 200 * Um, 0))],
                                    "Array w1"));
        Settle(vm);
        var copies = vm.Document.Objects.OfType<C3dWire>().ToList();
        Assert.Equal(3, copies.Count);
        Assert.All(copies, c => Assert.True(c.Placement.IsDefault));    // the pitch is in the points
        Assert.Equal(200 * Um + 20 * Um, copies[^1].Points[0].Y);
        var flagged = vm.Tree.SelectMany(g => g.Items).Where(i => i.Refusal is not null).Select(i => i.Name).ToList();
        Assert.Equal([copies[^1].Name], flagged);
        Assert.Contains("end is no longer on a pad", vm.Elaboration!.WireRefusals[copies[^1].Name], StringComparison.Ordinal);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    private static C3dDrawInput Snap(long x, long y, long z) => new(new C3dPoint3(x, y, z), true, true, null, null);

    /// <summary>A die pad row at x 0–100 µm (tops at 20 µm, 100 µm pitch in y) and a lead row at x 600–700 µm, whose
    /// tops are <paramref name="riseUm"/> higher.</summary>
    private C3dEditorViewModel OpenPads(int riseUm, int leads = 1, C3dObject? extra = null)
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
        var objects = new List<C3dObject>();
        for (int k = 0; k < (leads > 1 ? 3 : 1); k++)
            objects.Add(new C3dBox { Name = k == 0 ? "die" : $"die{k}", Material = "Gold", Min = new(0, k * 100 * Um, 0), Size = new(100 * Um, 60 * Um, 20 * Um) });
        for (int k = 0; k < leads; k++)
            objects.Add(new C3dBox { Name = k == 0 ? "lead" : $"lead{k}", Material = "Gold", Min = new(600 * Um, k * 100 * Um, 0), Size = new(100 * Um, 60 * Um, (20 + riseUm) * Um) });
        if (extra is not null) objects.Add(extra);
        C3dPersistence.SaveToFile(path, new C3dDocument { SnapDbu = 1 * Um, Objects = objects });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-50 * UmM, -50 * UmM, 0), s.ToLocal(750 * UmM, 300 * UmM, 300 * UmM), W / H);
        cam.Yaw = -0.4f; cam.Pitch = 0.9f;
        vm.Viewer.View.Camera = cam;
        return vm;
    }

    /// <summary>A package: a lead in the parent, the Bond wire example's layout placed at <paramref name="dieZ"/>, and a
    /// ball–wedge wire from its right pad to the lead.</summary>
    private static C3dDocument Package(long dieZ) => new()
    {
        TechRef = "../../tech/bond-wire-on-alumina.ctech",
        Objects =
        [
            new C3dBox { Name = "lead", Material = "Gold", Min = new(1000 * Um, -50 * Um, 0), Size = new(200 * Um, 100 * Um, 150 * Um) },
            new C3dWire
            {
                Name = "w1", Material = "Gold",
                Points = [new(500 * Um, 0, dieZ + 106 * Um), new(700 * Um, 0, dieZ + 300 * Um), new(1100 * Um, 0, 150 * Um)],
                Start = new C3dWireEnd { Style = BondStyle.Ball },
            },
        ],
        Instances = [new C3dInstance { Name = "U1", CellRef = "../../Bond wire", View = C3dInstanceView.Layout,
                                       Placement = new C3dPlacement { Origin = new(0, 0, dieZ) } }],
    };

    private static double Top(C3dElaboration e, string name) => e.Solids.Single(s => s.Name == name).Primitive switch
    {
        Em3dExtrudedPolygon p => p.ZTop,
        Em3dBox b => b.Max.Z,
        var other => throw new InvalidOperationException(other.GetType().Name),
    };

    private string CopyExample(bool withWBond)
    {
        string src = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D EM");
        string dst = Path.Combine(_root, "ex" + Guid.NewGuid().ToString("N")[..6]);
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (!withWBond && f.EndsWith(".wBond", StringComparison.OrdinalIgnoreCase)) continue;
            string to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dst;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");

    /// <summary>A click exactly where world point (µm) lands: the cursor's ray passes through it.</summary>
    private static void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        HoverAt(vm, x, y, z);
        vm.Viewer.Click(false);
    }

    /// <summary>The cursor exactly where world point (µm) lands, and the ID pass's answer for it — with that point as the hit
    /// when <paramref name="withPoint"/> (a hover reads it; a click picks again on its own).</summary>
    private static void HoverAt(C3dEditorViewModel vm, double x, double y, double z, bool withPoint = false)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        v.Hover(sx, sy);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, sx, sy, W, H, v.View.Visible);
        v.OnPicked(id, face, withPoint ? v.Scene.ToLocal(x * UmM, y * UmM, z * UmM) : Vector3.Zero, id != 0);
    }
}
