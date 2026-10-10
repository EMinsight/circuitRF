using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.ThreeD.Tools;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

/// <summary>
/// A gizmo drag that snaps to geometry lands the selection's nearer bounding-box face (the pivot, with no extent) on the
/// snap, wherever along the arrow (or across the plane square) the handle was grabbed. Measured from the grab point, a
/// sheet dragged up its Z arrow to a box's corner stopped short of the corner's height by the grab's distance from it.
/// </summary>
public sealed class GizmoSnapTests
{
    private static readonly C3dPoint3 Pivot = new(100, 200, 300);
    private static readonly C3dPoint3 Corner = new(7_000, 8_000, 9_000);

    [Theory]
    [InlineData(C3dMoveLock.AxisX, 5_000, 0, 0, 6_900, 0, 0)]
    [InlineData(C3dMoveLock.AxisY, 0, -4_000, 0, 0, 7_800, 0)]
    [InlineData(C3dMoveLock.AxisZ, 0, 0, 5_000, 0, 0, 8_700)]
    [InlineData(C3dMoveLock.PlaneZ, 3_000, -2_000, 0, 6_900, 7_800, 0)]
    public void ASnappedGizmoDrag_MovesThePivotOntoTheSnap(C3dMoveLock lockTo, long gx, long gy, long gz, long dx, long dy, long dz)
    {
        var tool = new MoveTool(new Host(), [new C3dTarget(false, 0)], Pivot, lockTo: lockTo) { FromGizmo = true };
        tool.SetBase(Pivot + new C3dPoint3(gx, gy, gz), exact: true);
        var now = tool.Current(new C3dDrawInput(Corner, true, true, null, null), out _);
        Assert.Equal(C3dTransform.Translation(new C3dPoint3(dx, dy, dz)), now!.Value.Transform);
    }

    /// <summary>A solid's bounding-box face nearer the snap goes onto it, along any axis: its top dragged up to a corner above,
    /// its bottom dragged down onto one.</summary>
    [Theory]
    [InlineData(C3dMoveLock.AxisZ, 9_000, 8_000)]
    [InlineData(C3dMoveLock.AxisZ, -5_000, -5_000)]
    [InlineData(C3dMoveLock.AxisX, 9_000, 8_000)]
    [InlineData(C3dMoveLock.AxisY, -5_000, -5_000)]
    public void ASnappedGizmoDragOfASolid_LandsItsNearerFaceOnTheSnap(C3dMoveLock lockTo, long snap, long moved)
    {
        var box = (new C3dPoint3(0, 0, 0), new C3dPoint3(1_000, 1_000, 1_000), true);
        var tool = new MoveTool(new Host(), [new C3dTarget(false, 0)], new C3dPoint3(500, 500, 500), lockTo: lockTo)
            { FromGizmo = true, Extent = box };
        var axis = lockTo switch { C3dMoveLock.AxisX => C3dAxis.X, C3dMoveLock.AxisY => C3dAxis.Y, _ => C3dAxis.Z };
        tool.SetBase(DrawingPlane.With(new C3dPoint3(500, 500, 500), axis, 3_000), exact: true);
        var now = tool.Current(new C3dDrawInput(new C3dPoint3(snap, snap, snap), true, true, null, null), out _);
        Assert.Equal(C3dTransform.Translation(DrawingPlane.With(default, axis, moved)), now!.Value.Transform);
    }

    /// <summary>A Move's base is a point the user picked on the geometry: that point, not the pivot, goes to the snap.</summary>
    [Fact]
    public void ASnappedMove_MovesThePickedBaseOntoTheSnap()
    {
        var tool = new MoveTool(new Host(), [new C3dTarget(false, 0)], Pivot, lockTo: C3dMoveLock.AxisZ);
        tool.SetBase(new C3dPoint3(100, 200, 1_000), exact: true);
        var now = tool.Current(new C3dDrawInput(Corner, true, true, null, null), out _);
        Assert.Equal(C3dTransform.Translation(new C3dPoint3(0, 0, 8_000)), now!.Value.Transform);
    }

    private sealed class Host : IC3dDrawHost
    {
        public DrawingPlane Plane => new(C3dPlane.XY, 0);
        public int DbuPerMicron => 1000;
        public C3dPoint3? PlanePoint(in C3dDrawInput input, out string? refusal) { refusal = null; return input.Snap; }
        public C3dPoint3? FreePoint(in C3dDrawInput input, out string? refusal) { refusal = null; return input.Snap; }
        public long? Along(C3dPoint3 through, C3dAxis axis, in C3dDrawInput input) => input.Snap is { } s ? DrawingPlane.Get(s, axis) : null;
        public string NextName(string prefix) => prefix + "1";
        public string? CurrentMaterial => null;
        public double? ThicknessUmFor(string? material) => null;
        public C3dPoint3? SurfacePoint(in C3dDrawInput input, out string? face, out string? refusal) { face = null; refusal = null; return null; }
    }
}
