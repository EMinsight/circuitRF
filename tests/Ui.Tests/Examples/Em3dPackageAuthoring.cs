// ================================================================
//  Em3dPackageAuthoring.cs — brief-em3d-52 R-em3d52-1b: the 3D Package example's package cell, DRAWN with the 3D
//  editor's own operations rather than written as JSON. Skipped unless CRF_AUTHOR_3D_PACKAGE=1, because it WRITES
//  examples/3D Package/Package/ (the cell folder is replaced). It is kept, not deleted, so the file can be re-drawn from
//  its gestures after a format change, and so the gestures the README and the user page describe are the ones used.
//
//  Every step is one the editor offers a user: the Variables panel (Add, Promote to Cell Parameter), the toolbar's
//  material and drawing plane, the Box and Cylinder tools with their dimensions TYPED (expressions included), the
//  Properties Inspector's name and dimension fields, Array…, Place Cell, the Wire tool (two clicks and a typed loop
//  height), the Port tool on a drawing plane, Setup Analyses' Add and its panel, and the air box's face menu. The GPU is
//  the recording fake the editor gates use; the picks are the CPU patch those gates use.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class Em3dPackageAuthoring
{
    private const float W = 1600, H = 1200;
    private const double UmM = 1e-6;
    private const long Mil = 25_400;                        // DBU per mil at 1000 DBU/µm
    private readonly Queue<Action> _posted = new();

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_3D_PACKAGE") != "1")
                Skip = "re-draws examples/3D Package/Package only with CRF_AUTHOR_3D_PACKAGE=1";
        }
    }

    [AuthorFact]
    public void DrawThePackage()
    {
        Snap3DPreference.TestOverrideActive = true;
        string ws = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Package");
        string cellDir = Path.Combine(ws, "Package");
        if (Directory.Exists(cellDir)) Directory.Delete(cellDir, true);

        // New Cell ▸ 3D view — the function the GUI's New Cell calls; the units come from the workspace's technology.
        var tech = TechPersistence.LoadFromFile(Path.Combine(ws, "tech", "ceramic-package.ctech"));
        var made = CellCreate.Create(ws, "Package", CellViews.ThreeD, tech: tech);
        string c3d = made.ThreeDPath!;

        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        try
        {
            vm.RestoreActiveSetup(null);
            vm.Viewer.Resized(W, H);
            vm.Viewer.Session.EnsureBackend();
            vm.Start();
            Settle(vm);
            Assert.Equal(LayoutUnit.Mil, vm.Document.DisplayUnit);
            Draw(vm, ws);
            Assert.Null(vm.Save());
        }
        finally { vm.Dispose(); }
    }

    private void Draw(C3dEditorViewModel vm, string ws)
    {
        // ── the names: the cavity's three sizes are cell parameters, the rest VARs of this 3D view ──────────────
        vm.ShowVariables = true;
        var v = vm.Variables!;
        foreach (var (name, value) in new[] { ("cav_w", "400"), ("cav_l", "320"), ("cav_h", "40"), ("wall", "20"), ("floor_t", "2"), ("lid_t", "5") })
        {
            v.NewName = name; v.NewExpression = value; v.NewUnit = "Mil";
            v.Add();
            Assert.Equal("", v.Error);
        }
        foreach (string p in new[] { "cav_w", "cav_l", "cav_h" })
        {
            v.Promote(v.Rows.Single(r => r.Name == p));
            Assert.Equal("", v.Error);
        }
        vm.ShowVariables = false;
        Settle(vm);

        Frame(vm, new Vector3(-260, -220, -10), new Vector3(260, 220, 60));

        // ── base: the floor, the alumina, the die-attach pad and its vias ─────────────────────────────────────
        Box(vm, "floor", "Gold", ["-cav_w/2 - wall", "-cav_l/2 - wall", "-floor_t"], ["cav_w + 2*wall", "cav_l + 2*wall", "floor_t"]);
        Box(vm, "base", "Alumina", ["-cav_w/2", "-cav_l/2", "0"], ["cav_w", "cav_l", "5"]);
        Box(vm, "attach", "Gold", ["-23", "-15", "5"], ["46", "30", "0.5"]);
        Cylinder(vm, "via1", "Gold", -18 * Mil, -10 * Mil, 0, "2", "5");
        Select(vm, "via1");
        vm.OpenArray();
        vm.ArrayCountX = "2"; vm.ArrayCountY = "2"; vm.ArrayCountZ = "1";
        vm.ArrayPitchX = "36"; vm.ArrayPitchY = "20"; vm.ArrayPitchZ = "0";
        Assert.Null(vm.ArrayError);
        vm.AcceptArray();
        Settle(vm);

        // ── the leads: 5 mil wide on 5 mil of alumina (50 Ω), 170 mil long, stopping 4 mil short of the walls ────
        Box(vm, "lead1", "Gold", ["-196", "-2.5", "5"], ["170", "5", "0.2"]);
        Box(vm, "lead2", "Gold", ["26", "-2.5", "5"], ["170", "5", "0.2"]);

        // ── the die: the Thru die cell's LAYOUT, bottom-centre on the attach pad ─────────────────────────────
        Assert.Null(vm.BeginInstancePlacement("../../Thru die", Path.Combine(ws, "Thru die"), C3dInstanceView.Layout));
        vm.PlaceArmedAt(new C3dPoint3(0, 0, (long)(5.5 * Mil)), bottomCentre: true);
        Settle(vm);
        Assert.Null(vm.RenameInstance(0, "U1"));
        Settle(vm);

        // ── the wires: ball on the die pad, wedge on the lead, 8 mil loop ─────────────────────────────────────
        vm.WireStartStyle = BondStyle.Ball;
        vm.WireEndStyle = BondStyle.Wedge;
        Wire(vm, "w1", dieX: -400, leadXMil: -27);
        Wire(vm, "w2", dieX: 400, leadXMil: 27);

        // ── the ports: at each lead's outer end, a sheet from the floor up to the lead ────────────────────────
        Port(vm, -196 * Mil);
        Port(vm, 196 * Mil);

        // ── the walls and the lid, last: drawn first they would hide what goes inside ──────────────────────────
        Frame(vm, new Vector3(-260, -220, -10), new Vector3(260, 220, 60));
        Box(vm, "wall_w", "Gold", ["-cav_w/2 - wall", "-cav_l/2 - wall", "0"], ["wall", "cav_l + 2*wall", "cav_h"]);
        Box(vm, "wall_e", "Gold", ["cav_w/2", "-cav_l/2 - wall", "0"], ["wall", "cav_l + 2*wall", "cav_h"]);
        Box(vm, "wall_s", "Gold", ["-cav_w/2", "-cav_l/2 - wall", "0"], ["cav_w", "wall", "cav_h"]);
        Box(vm, "wall_n", "Gold", ["-cav_w/2", "cav_l/2", "0"], ["cav_w", "wall", "cav_h"]);
        Box(vm, "lid", "Lid alloy", ["-cav_w/2 - wall", "-cav_l/2 - wall", "cav_h"], ["cav_w + 2*wall", "cav_l + 2*wall", "lid_t"]);

        // ── the setups: a driven sweep (active) and the lid's eigenmodes; each a closed metal box, no padding ──
        vm.AddSetup();
        Settle(vm);
        Assert.Null(vm.RenameSetup("Driven"));
        var panel = vm.SetupEditor!.ViewModel;
        panel.Frequency.StartCoeff = "2";
        panel.Frequency.StopCoeff = "30";
        panel.Frequency.NumPointsExpr = DrivenPoints;
        Settle(vm);
        PalaceFields(vm, DrivenPalace);
        ClosedBox(vm);

        vm.AddSetup();
        Settle(vm);
        vm.SelectedSetupItem = vm.SetupItems.Single(i => i.Name != "Driven");
        Assert.Null(vm.RenameSetup("Lid modes"));
        vm.SetActiveSetup("Lid modes");
        Settle(vm);
        panel = vm.SetupEditor!.ViewModel;
        panel.Problem3DChoice = EmSetupEditorViewModel.Problem3DChoices.Single(c => c.Value == Em3dProblemType.Eigenmode);
        panel.EigenCountText = EigenCount;
        panel.EigenTargetText = EigenTargetGHz;
        panel.CommitEigenmode();
        Settle(vm);
        PalaceFields(vm, LidPalace);
        ClosedBox(vm);
        vm.SetActiveSetup("Driven");
        Settle(vm);
    }

    // ── gestures ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Box: the toolbar's material, a click on the XY plane, the width and depth typed (Tab between), the height
    /// typed; then the name and the corner typed in Properties.</summary>
    private void Box(C3dEditorViewModel vm, string name, string material, string[] min, string[] size)
    {
        vm.CurrentMaterial = material;
        vm.SetPlane(new DrawingPlane(C3dPlane.XY, 0));
        vm.Arm(C3dToolKind.Box);
        ClickPlane(vm, 0, 0);
        Assert.Equal(1, vm.Tool!.Step);
        vm.OpenField(null);
        vm.FieldText = size[0];
        vm.FieldTab();
        vm.FieldText = size[1];
        vm.FieldEnter();
        Assert.False(vm.DefineOpen);
        Assert.Equal(2, vm.Tool!.Step);
        vm.OpenField(null);
        vm.FieldText = size[2];
        vm.FieldEnter();
        Settle(vm);
        vm.Disarm();
        int index = vm.Document.Objects.Count - 1;
        Assert.IsType<C3dBox>(vm.Document.Objects[index]);
        Name(vm, index, name);
        for (int k = 0; k < 3; k++) Field(vm, name, $"Min[{k}]", min[k]);
    }

    /// <summary>Cylinder: a click for the centre, the radius and the height typed; the centre typed in Properties.</summary>
    private void Cylinder(C3dEditorViewModel vm, string name, string material, long x, long y, long z, string radius, string height)
    {
        vm.CurrentMaterial = material;
        vm.SetPlane(new DrawingPlane(C3dPlane.XY, 0));
        vm.Arm(C3dToolKind.Cylinder);
        ClickPlane(vm, 0, 0);
        vm.OpenField(null);
        vm.FieldText = radius;
        vm.FieldEnter();
        Assert.Equal(2, vm.Tool!.Step);
        vm.OpenField(null);
        vm.FieldText = height;
        vm.FieldEnter();
        Settle(vm);
        vm.Disarm();
        int index = vm.Document.Objects.Count - 1;
        Assert.IsType<C3dCylinder>(vm.Document.Objects[index]);
        Name(vm, index, name);
        string L(long dbu) => C3dDimension.Spell(dbu, vm.Document.DisplayUnit, vm.Document.DbuPerMicron);
        Field(vm, name, "Base[0]", L(x));
        Field(vm, name, "Base[1]", L(y));
        Field(vm, name, "Base[2]", L(z));
    }

    /// <summary>Wire: zoomed onto the pair, a click on the die pad's top, a click on the lead's top, the loop height typed.</summary>
    private void Wire(C3dEditorViewModel vm, string name, double dieX, double leadXMil)
    {
        double leadX = leadXMil * 25.4;
        Frame(vm, new Vector3((float)Math.Min(dieX, leadX) - 150, -150, 0), new Vector3((float)Math.Max(dieX, leadX) + 150, 150, 300), top: true);
        vm.Arm(C3dToolKind.Wire);
        double padTop = Top(vm, dieX, 0);
        ClickAt(vm, dieX, 0, padTop);
        Assert.True(vm.Tool!.Step == 1, vm.ViewportLine);
        ClickAt(vm, leadX, 0, Top(vm, leadX, 0));
        Assert.True(vm.Tool!.Step == 2, vm.ViewportLine);
        vm.OpenField(null);
        vm.FieldText = "8";
        vm.FieldEnter();
        Settle(vm);
        vm.Disarm();
        int index = vm.Document.Objects.Count - 1;
        Assert.IsType<C3dWire>(vm.Document.Objects[index]);
        Name(vm, index, name);
        // The ball's click landed where the ray met the pad, a fraction of a micron off its centre line: Properties'
        // Start row puts it on the pad's centre (the foot is re-seated as a Vertex-mode drag's is).
        Select(vm, name);
        var start = vm.Properties.WirePoints[0];
        start.X = $"{dieX}um";
        start.Y = "0";
        vm.Properties.CommitWirePoint(start);
        Assert.Equal("", vm.Properties.Error);
        Settle(vm);
    }

    /// <summary>Port: the YZ drawing plane at the lead's end, and the rectangle's two corners, snapped (the Port tool's
    /// clicks, with the editor's own AddPort commit).</summary>
    private void Port(C3dEditorViewModel vm, long x)
    {
        vm.SetPlane(new DrawingPlane(C3dPlane.YZ, x));
        var tool = new PortTool(vm, () => vm.NewPortTemplate());
        Assert.True(tool.Click(Snap(x, (long)(-2.5 * Mil), 0)).Advanced);
        var step = tool.Click(Snap(x, (long)(2.5 * Mil), 5 * Mil));
        Assert.True(step.Finished, step.Refusal);
        vm.AddPort(tool.Made!);
        Settle(vm);
        var r = vm.PortResults.Single(p => p.Port.Number == tool.Made!.Number);
        Assert.True(r.Resolved is not null, r.Refusal);
    }

    // ── the setups' Palace settings, chosen by measurement (README ▸ the settings traded away) ──────────────

    private const string DrivenPoints = "1121";
    private static readonly (PalaceQuality Quality, string? Order, string? Edge, PalaceLinearSolver Solver) DrivenPalace =
        (PalaceQuality.Draft, "2", "1", PalaceLinearSolver.Direct);
    private const string EigenCount = "1", EigenTargetGHz = "20";
    private static readonly (PalaceQuality Quality, string? Order, string? Edge, PalaceLinearSolver Solver) LidPalace =
        (PalaceQuality.Draft, "2", "1", PalaceLinearSolver.Direct);

    /// <summary>The panel's Palace row: the Quality combo, a box typed and committed per overridden field, the Linear
    /// solver combo.</summary>
    private void PalaceFields(C3dEditorViewModel vm, (PalaceQuality Quality, string? Order, string? Edge, PalaceLinearSolver Solver) p)
    {
        var panel = vm.SetupEditor!.ViewModel;
        panel.PalaceQualityChoice = EmSetupEditorViewModel.PalaceQualityChoices.Single(c => c.Value == p.Quality);
        Settle(vm);
        panel = vm.SetupEditor!.ViewModel;
        if (p.Order is { } order) { panel.PalaceElementOrderText = order; panel.CommitPalaceField("Palace.ElementOrder"); Settle(vm); }
        panel = vm.SetupEditor!.ViewModel;
        if (p.Edge is { } edge) { panel.PalaceEdgeRefinementText = edge; panel.CommitPalaceField("Palace.EdgeRefinement"); Settle(vm); }
        panel = vm.SetupEditor!.ViewModel;
        panel.PalaceLinearSolverChoice = EmSetupEditorViewModel.PalaceLinearSolverChoices.Single(c => c.Value == p.Solver);
        Settle(vm);
        Assert.Null(vm.SetupEditor!.ViewModel.PalaceFieldError);
    }

    /// <summary>The air box's face menu, face by face: PEC, no padding — the package is its own shield.</summary>
    private void ClosedBox(C3dEditorViewModel vm)
    {
        foreach (string face in new[] { "xmin", "xmax", "ymin", "ymax", "zmin", "zmax" })
        {
            Assert.Null(vm.SetAirBoxBoundary(face, Em3dBoundaryKind.Pec));
            Assert.Null(vm.SetAirBoxPadding(face, "0"));
        }
        Settle(vm);
    }

    private void Name(C3dEditorViewModel vm, int index, string name)
    {
        Select(vm, vm.Document.Objects[index].Name);
        vm.Properties.NameText = name;
        vm.Properties.CommitName();
        Assert.Equal("", vm.Properties.Error);
        Settle(vm);
    }

    private void Field(C3dEditorViewModel vm, string obj, string path, string text)
    {
        Select(vm, obj);
        var f = vm.Properties.Fields.Single(x => x.Path == path);
        f.Text = text;
        vm.Properties.CommitField(f);
        Assert.Equal("", vm.Properties.Error);
        Settle(vm);
    }

    private void Select(C3dEditorViewModel vm, string name)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject(name)!.Id)]);
        vm.Properties.Reload();
    }

    // ── the view ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The camera on a region: from the iso view (corners in mil), or nearly straight down (corners in µm).</summary>
    private static void Frame(C3dEditorViewModel vm, Vector3 min, Vector3 max, bool top = false)
    {
        var s = vm.Viewer.Scene;
        double k = top ? 1 : 25.4;
        Vector3 L(Vector3 p) => s.ToLocal(p.X * k * UmM, p.Y * k * UmM, p.Z * k * UmM);
        var cam = Camera3D.Fit(L(min), L(max), W / H);
        if (top) { cam.Yaw = -MathF.PI / 2; cam.Pitch = 1.35f; }
        vm.Viewer.View.Camera = cam;
    }

    private double Top(C3dEditorViewModel vm, double xUm, double yUm)
    {
        // The highest top of a conductor under (x, y): the pad the wire tool will find there.
        double best = double.NegativeInfinity;
        foreach (var solid in vm.Elaboration!.Solids)
        {
            if (solid.Primitive is Em3dBox b && b.Min.X <= xUm * UmM && xUm * UmM <= b.Max.X && b.Min.Y <= yUm * UmM && yUm * UmM <= b.Max.Y)
                best = Math.Max(best, b.Max.Z);
            if (solid.Primitive is Em3dExtrudedPolygon p && solid.Name.StartsWith("U1/", StringComparison.Ordinal)
                && Math.Abs(xUm) >= 350 && Math.Abs(xUm) <= 450)
                best = Math.Max(best, p.ZTop);
        }
        return best / UmM;
    }

    private void ClickPlane(C3dEditorViewModel vm, double xMil, double yMil)
        => ClickAt(vm, xMil * 25.4, yMil * 25.4, vm.Plane.OffsetDbu / 1000.0);

    private void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        v.Hover(sx, sy);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, sx, sy, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
        v.Click(false);
        Settle(vm);
    }

    private static C3dDrawInput Snap(long x, long y, long z) => new(new C3dPoint3(x, y, z), true, true, null, null);

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
}
