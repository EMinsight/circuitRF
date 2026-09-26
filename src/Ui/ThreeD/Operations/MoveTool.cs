// brief-em3d-46 R-em3d46-2 — Move: a BASE point, then a TARGET point, both snapped; the displacement is target − base.
// That is the CAD way, and it is what makes "put this pad's corner exactly on that trace's corner" two clicks.
//
//   * the base is the first click — or, when the move started with the cursor over the selection (the key, the
//     gizmo), the snapped point under the cursor at that moment;
//   * X, Y or Z locks the move to that world axis (again unlocks); the displacement is then how far along the axis
//     the cursor says — a geometry snap projected onto the axis, so a snap still decides HOW FAR, or else the point
//     of the axis nearest the cursor's ray, on the grid. Shift+X/Y/Z locks it to the plane NORMAL to that axis;
//   * with nothing locked the target is the snapped point, or the drawing-plane point when nothing is snapped;
//   * digits open the typed field: dx, dy, dz, or one distance along a locked axis — exact in DBU.
//
// A displacement between two exact points is an integer DBU vector (R-em3d46-2d). One between inexact points is
// rounded once — the points are DBU already, rounded when they were resolved — and the commit says "≈".
//
// DUPLICATE (R-em3d46-4a) is this tool with KeepsOriginal: its base is the pivot and it starts at once. Esc cancels
// the duplicate as well as the move — nothing was written — and the copies go in with their move as ONE entry.

using Avalonia.Input;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Tools;

namespace CircuitRF.Ui.ThreeD.Operations;

/// <summary>What a move is held to: nothing, a world axis, or the plane normal to an axis.</summary>
public enum C3dMoveLock { None, AxisX, AxisY, AxisZ, PlaneX, PlaneY, PlaneZ }

public sealed class MoveTool : C3dOperationTool
{
    private C3dPoint3 _base;
    private bool _baseExact;
    private readonly bool _duplicate;

    public MoveTool(IC3dDrawHost host, IReadOnlyList<C3dTarget> targets, C3dPoint3 pivot, bool duplicate = false,
                    C3dMoveLock lockTo = C3dMoveLock.None)
        : base(host, targets, pivot)
    {
        _duplicate = duplicate;
        Lock = lockTo;
        if (duplicate) SetBase(pivot, exact: true);
    }

    public override C3dToolKind Kind => C3dToolKind.Move;
    public override string Name => _duplicate ? "Duplicate" : "Move";
    public override bool KeepsOriginal => _duplicate;
    public override bool TranslationOnly => true;

    public C3dMoveLock Lock { get; private set; }

    /// <summary>Started by a press on a gizmo handle: the release commits it (R-em3d46-5).</summary>
    public bool FromGizmo { get; init; }

    /// <summary>The base point, once there is one.</summary>
    public C3dPoint3? Base => Step > 0 ? _base : null;

    /// <summary>The base point is fixed and the selection follows the cursor.</summary>
    public void SetBase(C3dPoint3 p, bool exact)
    {
        _base = p;
        _baseExact = exact;
        Step = 1;
    }

    private static readonly string[] Xyz = ["dx", "dy", "dz"];
    private static readonly string[] Distance = ["distance"];
    private static readonly string[] None = [];

    public override IReadOnlyList<string> Dimensions => Step == 0 ? None : AxisLocked is not null ? Distance : Xyz;

    private C3dAxis? AxisLocked => Lock switch
    {
        C3dMoveLock.AxisX => C3dAxis.X, C3dMoveLock.AxisY => C3dAxis.Y, C3dMoveLock.AxisZ => C3dAxis.Z, _ => null,
    };

    private C3dAxis? PlaneNormal => Lock switch
    {
        C3dMoveLock.PlaneX => C3dAxis.X, C3dMoveLock.PlaneY => C3dAxis.Y, C3dMoveLock.PlaneZ => C3dAxis.Z, _ => null,
    };

    public override string ConstraintText => Lock switch
    {
        C3dMoveLock.None => "",
        C3dMoveLock.AxisX or C3dMoveLock.AxisY or C3dMoveLock.AxisZ => $"along {AxisLocked} only",
        _ => $"in the {DrawingPlane.PlaneNormalTo(PlaneNormal!.Value)} plane only",
    };

    public override string Prompt => Step == 0
        ? $"{Name}: click the base point."
        : $"{Name}: click the target point" + (ConstraintText.Length > 0 ? $" ({ConstraintText})" : "")
          + " — X, Y or Z locks an axis, Shift+X/Y/Z a plane; type a distance; Esc cancels.";

    public override bool Key(Key key, KeyModifiers modifiers)
    {
        if (modifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return false;
        bool shift = modifiers == KeyModifiers.Shift;
        C3dMoveLock? want = key switch
        {
            Avalonia.Input.Key.X => shift ? C3dMoveLock.PlaneX : C3dMoveLock.AxisX,
            Avalonia.Input.Key.Y => shift ? C3dMoveLock.PlaneY : C3dMoveLock.AxisY,
            Avalonia.Input.Key.Z => shift ? C3dMoveLock.PlaneZ : C3dMoveLock.AxisZ,
            _ => null,
        };
        if (want is not { } w) return false;
        Lock = Lock == w ? C3dMoveLock.None : w;      // the same key again unlocks
        return true;
    }

    /// <summary>The target the cursor states, with the lock applied, and whether it is exact.</summary>
    private (C3dPoint3 Target, bool Exact)? Target(in C3dDrawInput input, out string? refusal)
    {
        refusal = null;
        if (AxisLocked is { } axis)
        {
            if (Host.Along(_base, axis, input) is not { } w) { refusal = $"The cursor's line of sight runs along {axis}: orbit, or type the distance."; return null; }
            bool exact = input.Snap is null || !input.SnapOnGeometry || input.SnapExact;
            return (DrawingPlane.With(_base, axis, w), exact);
        }
        if (PlaneNormal is { } n)
        {
            if (input.Snap is { } s && input.SnapOnGeometry) return (DrawingPlane.With(s, n, DrawingPlane.Get(_base, n)), input.SnapExact);
            var plane = new DrawingPlane(DrawingPlane.PlaneNormalTo(n), DrawingPlane.Get(_base, n));
            if (!input.HasRay || plane.IsEdgeOn(input.RayDirection!.Value)
                || plane.Hit(input.RayOrigin!.Value, input.RayDirection!.Value, Host.DbuPerMicron) is not { } hit)
            {
                refusal = $"The {plane.Plane} plane through the base is edge-on: orbit, or type the distance.";
                return null;
            }
            double per = 1e-6 / Host.DbuPerMicron;
            long R(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
            return (DrawingPlane.With(new C3dPoint3(R(hit.X), R(hit.Y), R(hit.Z)), n, DrawingPlane.Get(_base, n)), true);
        }
        if (Host.FreePoint(input, out refusal) is not { } p) return null;
        return (p, input.Snap is null || !input.SnapOnGeometry || input.SnapExact);
    }

    public override C3dOperationTransform? Current(in C3dDrawInput input, out string? refusal)
    {
        refusal = null;
        if (Step == 0) return null;
        if (Target(input, out refusal) is not { } t) return null;
        return new(C3dTransform.Translation(t.Target - _base), _baseExact && t.Exact);
    }

    public override long?[] Current(in C3dDrawInput input)
    {
        if (Step == 0 || Target(input, out _) is not { } t) return AxisLocked is not null ? [null] : [null, null, null];
        var d = t.Target - _base;
        return AxisLocked is { } a ? [DrawingPlane.Get(d, a)] : [d.X, d.Y, d.Z];
    }

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (Step == 0)
        {
            if (Host.FreePoint(input, out var why) is not { } p) return C3dToolStep.Refuse(why!);
            SetBase(p, input.Snap is null || !input.SnapOnGeometry || input.SnapExact);
            return C3dToolStep.Next;
        }
        if (Current(input, out var refusal) is not { } now) return C3dToolStep.Refuse(refusal ?? "Move the cursor over the view.");
        Committed = now;
        return C3dToolStep.Finish;
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        if (Step == 0) return C3dToolStep.Refuse("Click the base point first.");
        var cur = Current(input);
        C3dPoint3 d;
        if (AxisLocked is { } a) d = DrawingPlane.With(default, a, values[0] ?? cur[0] ?? 0);
        else d = new C3dPoint3(values[0] ?? cur[0] ?? 0, values.Length > 1 ? values[1] ?? cur[1] ?? 0 : 0, values.Length > 2 ? values[2] ?? cur[2] ?? 0 : 0);
        // Typed values are exact DBU; a component left to the cursor is as exact as the cursor.
        Committed = new(C3dTransform.Translation(d), true);
        return C3dToolStep.Finish;
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        fixedPoints.Add(M(_base));
        if (Target(input, out _) is { } t) rubber.Add(new DrawSegment(M(_base), M(t.Target)));
    }
}
