// brief-em3d-47 — the face and vertex gestures: Move Along Normal (N), Move (G) of a face, Move (G) of a vertex,
// Extrude to New Solid (Shift+E), and Align to Face.
//
// A FACE DRAG CANNOT BE PREVIEWED BY A RIGID TRANSFORM, because the neighbours change shape (R-em3d47-6). So each
// state of the gesture is an EDITED OBJECT — the kernel's answer for where the cursor is — and the editor draws it
// by re-elaborating and re-tessellating that one object from a snapshot in which it stands in for the document's
// own. The document itself is not written until the commit, which is one undo entry.
//
// THE CURSOR IS READ AS BRIEF 46 READS IT. Move reuses brief 46's MoveTool whole — its base point → target point,
// its X/Y/Z and Shift+X/Y/Z locks, its typed dx/dy/dz — and only takes its answer (a world translation) into the
// object's own frame by the inverse placement. Move Along Normal and Extrude read a distance along the face's
// world normal as brief 45's Extrude does: a feature's height near one, else the point of the line nearest the
// cursor's ray, or typed.

using Avalonia.Input;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Kernel;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Tools;

namespace CircuitRF.Ui.ThreeD.Operations;

/// <summary>What a face or vertex gesture needs of the editor beyond drawing.</summary>
public interface IC3dFaceHost : IC3dDrawHost
{
    /// <summary>A length in DBU as the status bar spells it, with its unit.</summary>
    string LengthText(double dbu);

    /// <summary>The (object, face) the pick pass last found under the cursor.</summary>
    (uint Object, int Face) Picked { get; }

    /// <summary>The world plane of a scene face (any object, instances' parts included), or null with the reason.</summary>
    C3dFaceCommands.WorldPlane? PlaneOfSceneFace(uint objectId, int face, out string? refusal);
}

/// <summary>Where a face sits in the world: a point on it (DBU) and its outward unit normal.</summary>
public readonly record struct C3dFaceFrame((double X, double Y, double Z) Through, (double X, double Y, double Z) Normal)
{
    /// <summary>The world axis the normal lies along and the sign, when it does.</summary>
    public (C3dAxis Axis, int Sign)? Axis
    {
        get
        {
            double[] n = [Normal.X, Normal.Y, Normal.Z];
            for (int k = 0; k < 3; k++)
                if (Math.Abs(Math.Abs(n[k]) - 1) < 1e-12 && Math.Abs(n[(k + 1) % 3]) < 1e-12 && Math.Abs(n[(k + 2) % 3]) < 1e-12)
                    return ((C3dAxis)k, Math.Sign(n[k]));
            return null;
        }
    }

    /// <summary>
    /// The frame of face <paramref name="face"/> of <paramref name="o"/> through its placement: a planar face's centroid and
    /// normal; a cylinder cap's centre and axis; a cylinder side's radial direction toward <paramref name="toward"/> (the
    /// cursor, world DBU); a sheet's centre and its plane's normal.
    /// </summary>
    public static C3dFaceFrame? Of(C3dObject o, string face, (double X, double Y, double Z)? toward)
    {
        var t = o.Placement.ToTransform();
        (double, double, double) Dir((double X, double Y, double Z) n)
            => (t.M00 * n.X + t.M01 * n.Y + t.M02 * n.Z, t.M10 * n.X + t.M11 * n.Y + t.M12 * n.Z, t.M20 * n.X + t.M21 * n.Y + t.M22 * n.Z);
        if (o is C3dCylinder c)
        {
            int a = (int)c.Axis;
            var caps = C3dFaceEditor.CapCentres(c);
            if (face is "top" or "bottom")
            {
                int s = Math.Sign(c.Length) * (face == "top" ? 1 : -1);
                var n = C3dBrepBuild.With(default, a, s);
                return new C3dFaceFrame(t.Apply(caps[face == "top" ? 1 : 0]), Dir((n.X, n.Y, n.Z)));
            }
            // The side: radially, toward the cursor (its local position less its component along the axis).
            var (bx, by, bz) = t.Apply(caps[0]);
            var ax = Dir((a == 0 ? 1 : 0, a == 1 ? 1 : 0, a == 2 ? 1 : 0));
            var w = toward ?? (bx + 1, by, bz);
            double dx = w.X - bx, dy = w.Y - by, dz = w.Z - bz;
            double along = dx * ax.Item1 + dy * ax.Item2 + dz * ax.Item3;
            dx -= along * ax.Item1; dy -= along * ax.Item2; dz -= along * ax.Item3;
            double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (l < 1e-9) return null;
            var n0 = (dx / l, dy / l, dz / l);
            return new C3dFaceFrame((bx + n0.Item1 * c.Radius + along * ax.Item1, by + n0.Item2 * c.Radius + along * ax.Item2,
                                     bz + n0.Item3 * c.Radius + along * ax.Item3), n0);
        }
        if (C3dFaceCommands.Polygon(o, face, out _) is not var (rings, _)) return null;
        var outer = rings[0];
        double cx = outer.Average(p => (double)p.X), cy = outer.Average(p => (double)p.Y), cz = outer.Average(p => (double)p.Z);
        var plane = C3dFaceCommands.PlaneOf(o, face, out _);
        if (plane is not { } pl) return null;
        var (px, py, pz) = t.Apply(new C3dPoint3((long)Math.Round(cx), (long)Math.Round(cy), (long)Math.Round(cz)));
        return new C3dFaceFrame((px, py, pz), (pl.Nx, pl.Ny, pl.Nz));
    }
}

/// <summary>A face or vertex gesture on ONE document object: it states, for the cursor, the edited object.</summary>
public abstract class C3dFaceEditTool(IC3dFaceHost host, int index, C3dObject source) : C3dDrawTool(host)
{
    protected IC3dFaceHost Face => host;

    /// <summary>The object's index in the document.</summary>
    public int Index { get; } = index;

    /// <summary>The object as it was when the gesture started, and its kernel (B-rep, adjacency, face bounds) — built once.</summary>
    public C3dFaceEditor Editor { get; } = new(source);

    protected C3dTransform Placement { get; } = source.Placement.ToTransform();

    /// <summary>What the edit is called in the undo list and the status bar.</summary>
    public abstract string Describe { get; }

    /// <summary>The edited object for the cursor where it is, or null when the cursor says nothing yet.</summary>
    public abstract C3dFaceEditResult? Evaluate(in C3dDrawInput input);

    /// <summary>The edit the commit writes, fixed by the finishing click or the typed field.</summary>
    public C3dFaceEditResult? Committed { get; protected set; }

    /// <summary>A key while it runs; true when it took it.</summary>
    public virtual bool Key(Key key, KeyModifiers modifiers) => false;

    /// <summary>A face or vertex gesture starts on the picked face or vertex — that pick is its only earlier stage — so
    /// Esc ends it.</summary>
    public override bool StepBack() => false;

    /// <summary>A world vector (DBU) in the object's own frame: exact when the placement is a composition of quarter
    /// turns and mirrors, else rounded once.</summary>
    protected (C3dPoint3 Local, bool Exact) ToLocal(C3dPoint3 world)
    {
        var inv = Placement.Inverse() with { Tx = 0, Ty = 0, Tz = 0 };
        var (x, y, z) = inv.Apply(world);
        var r = new C3dPoint3(R(x), R(y), R(z));
        return (r, Math.Abs(x - r.X) < 1e-6 && Math.Abs(y - r.Y) < 1e-6 && Math.Abs(z - r.Z) < 1e-6);
    }

    protected C3dPoint3 WorldOf(C3dPoint3 local)
    {
        var (x, y, z) = Placement.Apply(local);
        return new C3dPoint3(R(x), R(y), R(z));
    }

    protected static long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>
    /// How far along <paramref name="frame"/>'s normal the cursor says (DBU): a feature's height near one when the normal
    /// is a world axis (Host.Along — the height step every tool shares), a snapped point's projection, or the point of
    /// the normal's line nearest the cursor's ray.
    /// </summary>
    protected long? DistanceAlong(C3dFaceFrame frame, in C3dDrawInput input)
    {
        var through = new C3dPoint3(R(frame.Through.X), R(frame.Through.Y), R(frame.Through.Z));
        if (frame.Axis is var (axis, sign))
            return Host.Along(through, axis, input) is { } w ? (w - DrawingPlane.Get(through, axis)) * sign : null;
        var n = frame.Normal;
        if (input.Snap is { } s && input.SnapOnGeometry)
            return R(n.X * (s.X - frame.Through.X) + n.Y * (s.Y - frame.Through.Y) + n.Z * (s.Z - frame.Through.Z));
        if (!input.HasRay) return null;
        double per = C3dLowering.Metres(1, Host.DbuPerMicron);
        // The closest point of the line p + t·n to the ray o + u·d, in metres.
        var o = input.RayOrigin!.Value;
        var d = input.RayDirection!.Value;
        double px = frame.Through.X * per, py = frame.Through.Y * per, pz = frame.Through.Z * per;
        double b = n.X * d.X + n.Y * d.Y + n.Z * d.Z;
        double denom = 1 - b * b;
        if (denom < 1e-9) return null;
        double wx = px - o.X, wy = py - o.Y, wz = pz - o.Z;
        double dn = n.X * wx + n.Y * wy + n.Z * wz, dd = d.X * wx + d.Y * wy + d.Z * wz;
        double t = (b * dd - dn) / denom;
        return R(t / per);
    }
}

/// <summary>Move Along Normal (N) — push/pull: the face's plane moves, its neighbours keep theirs (R-em3d47-3a).</summary>
public sealed class PushPullTool : C3dFaceEditTool
{
    private static readonly string[] Distance = ["distance"];
    private readonly string _face;
    private readonly C3dFaceFrame _frame;

    public PushPullTool(IC3dFaceHost host, int index, C3dObject source, string face, C3dFaceFrame frame) : base(host, index, source)
    {
        _face = face;
        _frame = frame;
        Step = 1;
    }

    public string FaceName => _face;
    public override C3dToolKind Kind => C3dToolKind.PushPull;
    public override string Name => "Move Along Normal";
    public override string Describe => $"Move face {_face} of {Editor.Source.Name} along its normal";
    public override IReadOnlyList<string> Dimensions => Distance;

    public override string Prompt
        => $"Move Along Normal '{_face}': move to push or pull it and click, or type the distance. Its neighbours keep their planes. Esc cancels.";

    /// <summary>The cursor's distance, clamped where a neighbour would vanish (R-em3d47-3a).</summary>
    private long? CursorDistance(in C3dDrawInput input, out C3dNormalLimit? clampedBy)
    {
        clampedBy = null;
        if (DistanceAlong(_frame, input) is not { } d) return null;
        var limit = d == 0 ? null : Editor.Limit(_face, Math.Sign(d));
        long c = C3dFaceEditor.Clamp(d, limit);
        if (c != d) clampedBy = limit;
        return c;
    }

    /// <summary>What the status bar says of the clamp in force, or null.</summary>
    public string? ClampText { get; private set; }

    public override C3dFaceEditResult? Evaluate(in C3dDrawInput input)
    {
        if (CursorDistance(input, out var clamp) is not { } d) return null;
        ClampText = clamp is { } l ? $"'{l.Face}' would vanish at {Face.LengthText(l.Distance)}." : null;
        return Editor.MoveAlongNormal(_face, d, Face.LengthText);
    }

    public override long?[] Current(in C3dDrawInput input) => [CursorDistance(input, out _)];

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (Evaluate(input) is not { } r) return C3dToolStep.Refuse("Move the cursor along the face's normal, or type the distance.");
        if (!r.Ok) return C3dToolStep.Refuse(r.Refusal!);
        Committed = r;
        return C3dToolStep.Finish;
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        if ((values.Length > 0 ? values[0] : null) is not { } d) return Click(input);
        // A typed distance is taken as typed: past the clamp it is refused, naming the neighbour, never shortened.
        var r = Editor.MoveAlongNormal(_face, d, Face.LengthText);
        if (!r.Ok) return C3dToolStep.Refuse(r.Refusal!);
        Committed = r;
        return C3dToolStep.Finish;
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (CursorDistance(input, out _) is not { } d) return;
        double per = C3dLowering.Metres(1, Host.DbuPerMicron);
        var a = new Point3(_frame.Through.X * per, _frame.Through.Y * per, _frame.Through.Z * per);
        var b = new Point3(a.X + _frame.Normal.X * d * per, a.Y + _frame.Normal.Y * d * per, a.Z + _frame.Normal.Z * d * per);
        fixedPoints.Add(a);
        rubber.Add(new DrawSegment(a, b));
    }
}

/// <summary>
/// Move (G) of a face or a vertex: brief 46's MoveTool — base point, target point, axis and plane locks, typed dx/dy/dz —
/// whose translation, taken into the object's frame, moves the face's vertices (R-em3d47-3b) or the one vertex (-3e).
/// </summary>
public sealed class FaceMoveTool : C3dFaceEditTool
{
    private readonly MoveTool _mover;
    private readonly string? _face;
    private readonly int _vertex = -1;

    /// <summary>A face move. <paramref name="baseAt"/>, when given, is the base point at once (the cursor was on the face).</summary>
    public FaceMoveTool(IC3dFaceHost host, int index, C3dObject source, string face, C3dPoint3 pivot, (C3dPoint3 P, bool Exact)? baseAt)
        : base(host, index, source)
    {
        _face = face;
        _mover = new MoveTool(host, [], pivot);
        if (baseAt is var (p, exact)) _mover.SetBase(p, exact);
        Step = _mover.Step;
    }

    /// <summary>A vertex move: <paramref name="vertex"/> (an index into the editor's vertices) is the base point.</summary>
    public FaceMoveTool(IC3dFaceHost host, int index, C3dObject source, int vertex) : base(host, index, source)
    {
        _vertex = vertex;
        var at = WorldOf(Editor.Vertices[vertex]);
        _mover = new MoveTool(host, [], at);
        _mover.SetBase(at, Placement.IsIntegral);
        Step = _mover.Step;
    }

    public bool IsVertex => _vertex >= 0;
    public string? FaceName => _face;
    public int Vertex => _vertex;
    public override C3dToolKind Kind => IsVertex ? C3dToolKind.VertexMove : C3dToolKind.FaceMove;
    public override string Name => IsVertex ? "Move Vertex" : "Move Face";
    public override string Describe => IsVertex ? $"Move a vertex of {Editor.Source.Name}" : $"Move face {_face} of {Editor.Source.Name}";
    public override IReadOnlyList<string> Dimensions => _mover.Dimensions;
    public override string Prompt => _mover.Prompt.Replace("Move:", $"{Name}:", StringComparison.Ordinal);

    public override bool Key(Key key, KeyModifiers modifiers) => _mover.Key(key, modifiers);

    private C3dFaceEditResult Edit(C3dTransform t)
    {
        var (local, _) = ToLocal(new C3dPoint3(R(t.Tx), R(t.Ty), R(t.Tz)));
        return IsVertex ? Editor.MoveVertex(_vertex, Editor.Vertices[_vertex] + local) : Editor.MoveFace(_face!, local);
    }

    public override C3dFaceEditResult? Evaluate(in C3dDrawInput input)
        => _mover.Current(input, out _) is { } t ? Edit(t.Transform) : null;

    public override long?[] Current(in C3dDrawInput input) => _mover.Current(input);

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        var step = _mover.Click(input);
        Step = _mover.Step;
        if (!step.Finished) return step;
        return Finish(_mover.Committed!.Value.Transform);
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        var step = _mover.Typed(values, input);
        return step.Finished ? Finish(_mover.Committed!.Value.Transform) : step;
    }

    private C3dToolStep Finish(C3dTransform t)
    {
        var r = Edit(t);
        if (!r.Ok) return C3dToolStep.Refuse(r.Refusal!);
        Committed = r;
        return C3dToolStep.Finish;
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
        => _mover.Preview(input, rubber, fixedPoints);
}

/// <summary>Extrude to New Solid (Shift+E): a new object grows from the face along its normal; the source is unchanged
/// (R-em3d47-4). Its material is the current one; M takes the source's instead.</summary>
public sealed class ExtrudeFaceTool : C3dFaceEditTool
{
    private static readonly string[] Distance = ["distance"];
    private readonly string _face;
    private readonly C3dFaceFrame _frame;
    private readonly List<List<C3dPoint3>>? _rings;

    public ExtrudeFaceTool(IC3dFaceHost host, int index, C3dObject source, string face, C3dFaceFrame frame) : base(host, index, source)
    {
        _face = face;
        _frame = frame;
        _rings = C3dFaceCommands.Polygon(source, face, out _)?.Rings;
        Step = 1;
    }

    /// <summary>Take the source's material instead of the current one (M toggles).</summary>
    public bool SourceMaterial { get; private set; }

    public override C3dToolKind Kind => C3dToolKind.ExtrudeFace;
    public override string Name => "Extrude to New Solid";
    public override string Describe => $"Extrude face {_face} of {Editor.Source.Name} to a new solid";
    public override IReadOnlyList<string> Dimensions => Distance;

    public override string Prompt
        => $"Extrude '{_face}' to a new solid: move to set the distance and click, or type it. Material: " +
           (SourceMaterial ? $"the source's ({Editor.Source.Material ?? "none"})" : $"the current one ({Host.CurrentMaterial ?? "none"})") +
           " — M toggles. Esc cancels.";

    public override bool Key(Key key, KeyModifiers modifiers)
    {
        if (key != Avalonia.Input.Key.M || modifiers != KeyModifiers.None) return false;
        SourceMaterial = !SourceMaterial;
        return true;
    }

    public override C3dFaceEditResult? Evaluate(in C3dDrawInput input) => null;

    public override long?[] Current(in C3dDrawInput input) => [DistanceAlong(_frame, input)];

    public override C3dToolStep Click(in C3dDrawInput input)
        => DistanceAlong(_frame, input) is { } d ? Make(d) : C3dToolStep.Refuse("Extrude: type the distance.");

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
        => ((values.Length > 0 ? values[0] : null) ?? DistanceAlong(_frame, input)) is { } d ? Make(d) : C3dToolStep.Refuse("Extrude: type the distance.");

    private C3dToolStep Make(long d)
    {
        string prefix = Editor.Source is C3dCylinder ? "cylinder" : "prism";
        string? material = SourceMaterial ? Editor.Source.Material : Host.CurrentMaterial;
        var made = C3dFaceCommands.Extrude(Editor.Source, _face, d, Host.NextName(prefix), material, out var why);
        if (made is null) return C3dToolStep.Refuse(why!);
        if (made is C3dPolyhedron p) p.Name = Host.NextName("solid");
        Step = 0;
        return C3dToolStep.Done(made);
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (_rings is null || DistanceAlong(_frame, input) is not { } d) return;
        var n = _frame.Normal;
        var lift = new C3dPoint3(R(n.X * d), R(n.Y * d), R(n.Z * d));
        foreach (var ring in _rings)
        {
            var lo = ring.Select(WorldOf).ToList();
            var hi = lo.Select(p => p + lift).ToList();
            DrawGeometry.Chain(lo, true, Host.DbuPerMicron, rubber);
            DrawGeometry.Chain(hi, true, Host.DbuPerMicron, rubber);
            for (int k = 0; k < lo.Count; k++) rubber.Add(new DrawSegment(M(lo[k]), M(hi[k])));
        }
    }
}

/// <summary>
/// Align to Face: the selected face's object moves along a picked target face's normal until the two are coplanar —
/// touching (facing each other) or flush (the same way), T toggles, shown before the click (R-em3d47-4). A rigid
/// move of the whole object, so it previews as brief 46's operations do and commits as a translation.
/// </summary>
public sealed class AlignToFaceTool : C3dOperationTool
{
    private readonly C3dFaceCommands.WorldPlane _source;
    private readonly string _sourceFace;
    private readonly IC3dFaceHost _host;

    public AlignToFaceTool(IC3dFaceHost host, C3dTarget target, C3dPoint3 pivot, C3dFaceCommands.WorldPlane source, string sourceFace)
        : base(host, [target], pivot)
    {
        _host = host;
        _source = source;
        _sourceFace = sourceFace;
        Step = 1;
    }

    /// <summary>Touching (the faces face each other) or flush (they point the same way).</summary>
    public bool Touching { get; private set; } = true;

    public override C3dToolKind Kind => C3dToolKind.AlignFace;
    public override string Name => "Align to Face";
    public override bool TranslationOnly => true;
    public override IReadOnlyList<string> Dimensions => [];
    public override string ConstraintText => Touching ? "touching" : "flush";

    public override string Prompt
        => $"Align to Face: click the face to align '{_sourceFace}' with — {(Touching ? "Touching (facing it)" : "Flush (the same way)")}; " +
           "T toggles. Esc cancels.";

    public override bool Key(Key key, KeyModifiers modifiers)
    {
        if (key != Avalonia.Input.Key.T || modifiers != KeyModifiers.None) return false;
        Touching = !Touching;
        return true;
    }

    public override C3dOperationTransform? Current(in C3dDrawInput input, out string? refusal)
    {
        refusal = null;
        var (obj, face) = _host.Picked;
        if (obj == 0 || face < 0) { refusal = "Move over the face to align with."; return null; }
        if (_host.PlaneOfSceneFace(obj, face, out refusal) is not { } target) return null;
        if (C3dFaceCommands.Align(_source, target, Touching, out refusal) is not var (by, exact)) return null;
        return new C3dOperationTransform(C3dTransform.Translation(by), exact);
    }

    public override long?[] Current(in C3dDrawInput input) => [];

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        if (Current(input, out var why) is not { } t) return C3dToolStep.Refuse(why ?? "Click a face.");
        Committed = t;
        return C3dToolStep.Finish;
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input) => Click(input);

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints) { }
}
