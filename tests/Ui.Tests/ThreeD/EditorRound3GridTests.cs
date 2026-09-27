// ================================================================
//  EditorRound3GridTests.cs — 3D editor bugs round 3, the grid and the view: a perspective zoom in stops at the drawing
//  plane instead of carrying the eye through it (the grid vanished below a ~10 µm scale), close in the minor spacing is
//  the snap step and then a major-every-th of it, a double-click on the Z letter from the Right view turns to Top, and
//  the Snap combobox offers round numbers of mil on a micron process. Headless; no pixels.
// ================================================================

using System.Numerics;
using Avalonia;
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
public sealed class EditorRound3GridTests : IDisposable
{
    private const float W = 800, H = 600;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-c3dr3-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>The reported case: the plane on a substrate's top face, the orbit centre below it. Zooming in at the
    /// middle of a perspective top view used to put the eye under the plane at a ~50 µm distance, where the grid is
    /// behind it; now the eye stays above it however far the zoom goes, and the grid is drawn every frame.</summary>
    [Fact]
    public void PerspectiveZoom_NeverCarriesTheEyeThroughTheDrawingPlane()
    {
        var vm = Open(Write([Box("sub", "Gold", 0, 0, 0, 2000, 2000, 100)]));
        var v = vm.Viewer;
        vm.PlaneOffsetText = "100";
        vm.CommitPlaneOffset();
        v.View.Camera.SetStandardView(StandardView3D.Top);
        v.View.Camera.Projection = Projection3D.Perspective;
        v.View.Camera.Target = v.Scene.ToLocal(1e-3, 1e-3, 20e-6);
        double planeZ = v.Scene.ToLocal(0, 0, 100e-6).Z;
        var plan = new Scene3DFramePlan();
        for (int i = 0; i < 80; i++)
        {
            v.Zoom(1, W / 2, H / 2, W, H);
            Assert.True(v.View.Camera.Eye.Z > planeZ, $"notch {i}: the eye is at {v.View.Camera.Eye.Z}, the plane at {planeZ}");
        }
        plan.Plan(v.Scene, v.View, (int)W, (int)H, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        var g = plan.Uniforms.AsSpan(Scene3DFramePlan.GridAt, PlaneGrid.Floats).ToArray();
        Assert.NotNull(PlaneGrid.RayHit(g, 0, 0));                    // the centre's ray meets the plane in front of the eye
        Assert.InRange(plan.GridSpacing.MinorPixels, PlaneGrid.MinPixels, PlaneGrid.MaxPixels);
    }

    /// <summary>Zoomed out the spacing is the display unit's own 1-2-5 step, at least 14 px; once the snap step spans
    /// 14 px it IS the minor spacing, and once it spans ten times that the snap step becomes the major lines.</summary>
    [Fact]
    public void Spacing_CloseIn_IsTheSnapStep_ThenATenthOfIt()
    {
        const int dbu = 1000;
        long snap = 10 * Um;                                           // 10 µm
        double M(double px) => 10e-6 / px;                             // metres a pixel when the snap step spans px

        var far = PlaneGrid.Spacing(M(2), LayoutUnit.Um, dbu, snap);    // snap 2 px: the unit's steps
        Assert.Equal(100 * Um, far.MinorDbu);                          // 100 µm = 20 px
        Assert.Equal(snap, PlaneGrid.Spacing(M(14), LayoutUnit.Um, dbu, snap).MinorDbu);
        Assert.Equal(snap, PlaneGrid.Spacing(M(139), LayoutUnit.Um, dbu, snap).MinorDbu);
        var close = PlaneGrid.Spacing(M(140), LayoutUnit.Um, dbu, snap);
        Assert.Equal(snap / 10, close.MinorDbu);                       // the snap step is now every major line
        Assert.Equal(10, close.MajorEvery);
        // A step that does not divide by ten (25 DBU) goes back to the unit's steps below it: 0.002 µm = 16 px.
        Assert.Equal(2, PlaneGrid.Spacing(25e-9 / 200, LayoutUnit.Um, dbu, 25).MinorDbu);
    }

    /// <summary>From the Right view the Z arm is drawn full length and its letter sits on the ring; a double-click on the
    /// far half of that letter found nothing and the view never turned to Top.</summary>
    [Fact]
    public void AxisIndicator_FromTheRightView_TheZLetterTurnsToTop()
    {
        var o = Viewer3DOverlay.AxisIndicatorCentre(H);
        var cam = new Camera3D { FovY = Camera3D.DefaultFovY, Distance = 1 };
        cam.SetStandardView(StandardView3D.Right);
        var letterTop = new Point(o.X, o.Y - (Viewer3DOverlay.AxisArm + 8 + 5));
        Assert.Equal(StandardView3D.Top, Viewer3DOverlay.AxisIndicatorHit(cam, o, letterTop));
    }

    /// <summary>A 1 µm process shown in mil: the rungs are round numbers of mil, not 1 µm's multiples converted.</summary>
    [Fact]
    public void SnapLadder_InMil_OnAMicronProcess_IsRoundNumbers()
    {
        var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 50, 50, 20)], defaultSnapDbu: Um));
        vm.DisplayUnit = LayoutUnit.Mil;
        Assert.Equal(["0.01 mil", "0.05 mil", "0.1 mil", "0.5 mil", "1 mil", "2.5 mil", "5 mil"], vm.SnapLadderOptions);
    }

    private C3dEditorViewModel Open(string c3d)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(30)), "the scene never settled");
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        return vm;
    }

    private static C3dBox Box(string name, string? material, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private string Write(List<C3dObject> objects, long defaultSnapDbu = 0)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            DefaultSnapDbu = defaultSnapDbu,
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument { Objects = objects });
        return path;
    }
}
