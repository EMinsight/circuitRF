// brief-em3d-46 R-em3d46-3 — Rotate: about X, Y or Z through a pivot, in 15° steps, freely with Shift held, or typed.
//
// The pivot is the selection's bounding-box centre until a Ctrl/Cmd-click sets it to a snapped point; it is drawn
// as a small cross while the gesture lasts. The angle is read on the plane through the pivot normal to the axis:
// the cursor's direction from the pivot there, against the direction it had when the rotation started (or when
// the axis changed) — so the selection turns with the cursor around the pivot. X, Y or Z changes the axis.
//
// What is committed is composed into each target's placement and CANONICALISED (C3dPlacement.Canonical), so a
// rotation never converts anything (overview §1g) and the rotation list never grows with editing.

using System.Globalization;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Tools;

namespace CircuitRF.Ui.ThreeD.Operations;

public sealed class RotateTool : C3dOperationTool
{
    /// <summary>The step a rotation snaps to unless Shift is held.</summary>
    public const double StepDeg = 15;

    /// <summary>A typed angle is held in the field's integer as this many units a degree (1e-9°, the canonical form's).</summary>
    public const double UnitsPerDegree = 1e9;

    private double? _reference;          // the cursor's angle on the rotation plane when the gesture (re)started, radians

    public RotateTool(IC3dDrawHost host, IReadOnlyList<C3dTarget> targets, C3dPoint3 pivot, C3dAxis axis) : base(host, targets, pivot)
    {
        Axis = axis;
        Step = 1;
    }

    public override C3dToolKind Kind => C3dToolKind.Rotate;
    public override bool ShowsPivot => true;

    public C3dAxis Axis { get; private set; }

    /// <summary>The angle shown and committed now, degrees.</summary>
    public double AngleDeg { get; private set; }

    private static readonly string[] AngleDim = ["angle"];
    public override IReadOnlyList<string> Dimensions => AngleDim;
    public override string? FieldSuffix(int index) => "°";

    public override string ConstraintText => $"about {Axis}";

    public override string Prompt
        => $"Rotate about {Axis} through the pivot: {Deg(AngleDeg)}° — click to place; Shift turns freely, X/Y/Z changes the axis, " +
           "Ctrl/Cmd-click sets the pivot, type an angle; Esc cancels.";

    private static string Deg(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    public override bool Key(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None) return false;
        C3dAxis? a = key switch { Avalonia.Input.Key.X => C3dAxis.X, Avalonia.Input.Key.Y => C3dAxis.Y, Avalonia.Input.Key.Z => C3dAxis.Z, _ => null };
        if (a is not { } axis) return false;
        Axis = axis;
        _reference = null;
        AngleDeg = 0;
        return true;
    }

    public override bool SetPivot(in C3dDrawInput input)
    {
        if (Host.FreePoint(input, out _) is not { } p) return false;
        Pivot = p;
        _reference = null;
        AngleDeg = 0;
        return true;
    }

    /// <summary>The cursor's angle about the axis on the plane through the pivot, radians; null when the plane is edge-on.</summary>
    private double? CursorAngle(in C3dDrawInput input)
    {
        var plane = new DrawingPlane(DrawingPlane.PlaneNormalTo(Axis), DrawingPlane.Get(Pivot, Axis));
        Point3 hit;
        if (input.Snap is { } s && input.SnapOnGeometry) hit = M(s);
        else if (input.HasRay && !plane.IsEdgeOn(input.RayDirection!.Value)
                 && plane.Hit(input.RayOrigin!.Value, input.RayDirection!.Value, Host.DbuPerMicron) is { } h) hit = h;
        else return null;
        var c = M(Pivot);
        var (u, v) = DrawingPlane.AxesOf(plane.Plane);
        double du = DrawingPlane.Get(hit, u) - DrawingPlane.Get(c, u), dv = DrawingPlane.Get(hit, v) - DrawingPlane.Get(c, v);
        if (du * du + dv * dv < 1e-30) return null;
        // (u, v) is right-handed about the normal for XY and YZ; XZ's (x, z) is left-handed about +y.
        double a = Math.Atan2(dv, du);
        return plane.Plane == C3dPlane.XZ ? -a : a;
    }

    public override C3dOperationTransform? Current(in C3dDrawInput input, out string? refusal)
    {
        refusal = null;
        if (CursorAngle(input) is { } now)
        {
            _reference ??= now;
            double deg = (now - _reference.Value) * 180 / Math.PI;
            deg = Math.IEEERemainder(deg, 360);
            if (!input.Free) deg = Math.Round(deg / StepDeg) * StepDeg;
            AngleDeg = deg == 0 ? 0 : deg;
        }
        return Of(AngleDeg);
    }

    private C3dOperationTransform Of(double deg)
    {
        var t = C3dOperations.Rotation(Axis, deg, Pivot);
        return new(t, t.IsIntegral);
    }

    public override long?[] Current(in C3dDrawInput input) => [(long)Math.Round(AngleDeg * UnitsPerDegree)];

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        Committed = Current(input, out _);
        return C3dToolStep.Finish;
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        AngleDeg = values[0] is { } v ? v / UnitsPerDegree : AngleDeg;
        Committed = Of(AngleDeg);
        return C3dToolStep.Finish;
    }

    /// <summary>An angle, degrees: <c>30</c>, <c>-45</c>, <c>22.5°</c>, <c>90deg</c>.</summary>
    public override C3dDimension ParseField(int index, string text, LayoutUnit unit, int dbuPerMicron)
    {
        string t = (text ?? "").Trim().TrimEnd('°').Trim();
        if (t.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) t = t[..^3].Trim();
        if (double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double deg) && double.IsFinite(deg))
            return new C3dDimension(C3dDimensionKind.Value, (long)Math.Round(deg * UnitsPerDegree), null);
        return new C3dDimension(C3dDimensionKind.Invalid, 0, $"'{text}' is not an angle in degrees.");
    }

    public override string SpellField(int index, long value, LayoutUnit unit, int dbuPerMicron)
        => (value / UnitsPerDegree).ToString("0.#########", CultureInfo.InvariantCulture);

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (CursorAngle(input) is null) return;
        var plane = new DrawingPlane(DrawingPlane.PlaneNormalTo(Axis), DrawingPlane.Get(Pivot, Axis));
        Point3 end;
        if (input.Snap is { } s && input.SnapOnGeometry) end = M(s);
        else if (input.HasRay && plane.Hit(input.RayOrigin!.Value, input.RayDirection!.Value, Host.DbuPerMicron) is { } h) end = h;
        else return;
        rubber.Add(new DrawSegment(M(Pivot), end));
    }
}
