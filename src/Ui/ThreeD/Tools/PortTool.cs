// brief-em3d-49 R-em3d49-2d — Port: drawn like a sheet (two snapped clicks on the drawing plane), made a PORT record, not
// an object. The next free number is the editor's and Z0 comes from the last port; the polarity is not the tool's to
// decide — the editor resolves the rubber band's rectangle by contact on every move (C3dPorts) and draws the arrow, or
// the refusal's words, beside the cursor.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD.Tools;

public sealed class PortTool(IC3dDrawHost host, Func<C3dPort> template) : C3dRectangleTool(host)
{
    public override C3dToolKind Kind => C3dToolKind.Port;

    public override string Prompt => Step == 0
        ? "Port: click the first corner on the drawing plane (between the two conductors)."
        : "Port: click the opposite corner (or type the width, Tab, the depth). The arrow runs from − to +. Esc cancels.";

    /// <summary>The port the last click made — the editor inserts it as one undo entry.</summary>
    public C3dPort? Made { get; private set; }

    public override C3dToolStep Click(in C3dDrawInput input) => Finish(RectangleClick(input)!.Value);

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
        => Step == 1 ? Finish(TypedRectangle(values, input)) : new(false);

    private C3dToolStep Finish(C3dToolStep step)
    {
        if (!step.Advanced || Step != 2) return step;
        Step = 0;
        Made = Port(A, B);
        return C3dToolStep.Finish;
    }

    /// <summary>The port the rubber band would make with its second corner at the cursor, or null before the first click.</summary>
    public C3dPort? Preview(in C3dDrawInput input)
        => Step == 1 && Cursor(input, out _) is { } c && c.U != A.U && c.V != A.V ? Port(A, c) : null;

    private C3dPort Port(C3dPoint2 a, C3dPoint2 b)
    {
        var p = template();
        p.Plane = Plane.Plane;
        p.Offset = Plane.OffsetDbu;
        p.Rect = new C3dRect
        {
            Min = new C3dPoint2(Math.Min(a.U, b.U), Math.Min(a.V, b.V)),
            Size = new C3dPoint2(Math.Abs(b.U - a.U), Math.Abs(b.V - a.V)),
        };
        return p;
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
        => PreviewRectangle(input, rubber, fixedPoints);
}
