// brief-em3d-50 R-em3d50-3 — Wire: a bond wire from one pad to another, across hierarchy, then its loop height.
//
// THREE CLICKS. The start, on the top of a pad — a snapped point (face-centre snap is the natural target on a pad) or
// any point that lies on an upward-facing conductor face; anything else is refused with "a wire starts on the top of
// a pad". The end, by the same rule, with a rubber-band arch (LoopShape.Seed) following the cursor. Then the LOOP
// HEIGHT, set with the mouse as a box's height is set — or typed — and measured the ASSEMBLY way (em-3d.md §6.6):
// from the bottom of the lower foot (the lower pad's top) to the top of the wire at its apex. The status bar shows
// that number and the axis's own loop height side by side, as `explain` does, because users hold one or the other.
//
// THE NUMBER TYPED IS THE NUMBER MEASURED AFTERWARDS (gate 2): the arch is solved against the resolved solid
// (C3dWires.ForAssemblyHeight), not against a formula for it, so the mitre at the apex and a ball's neck cannot make
// the two disagree.
//
// A pad is looked up where elaboration will look it up (C3dWires.PadAt), in the editor's current elaboration, so an
// end the tool accepts is an end elaboration accepts. The ends are SEATED on the pad's top: a snap that is a hair off
// in z (a float in an instance) is written at the top's own DBU.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.WBond;
using Point3 = CircuitRF.Engine.Em3d.Point3;

namespace CircuitRF.Ui.ThreeD.Tools;

/// <summary>What a new wire is made with — the toolbar's values (R-em3d50-3b).</summary>
public sealed record C3dWireTemplate(double DiameterUm, string? Material, WireCrossSection Section, BondStyle StartStyle,
                                     BondStyle EndStyle, long? LoopHeightDbu);

/// <summary>The editor's side of the Wire tool.</summary>
public interface IC3dWireHost
{
    /// <summary>The pad whose top surface holds <paramref name="p"/> (document DBU), with its top in DBU; null for none.</summary>
    (string Pad, long TopDbu)? PadAt(C3dPoint3 p);

    /// <summary>The pad top the cursor's ray meets first, as a document DBU point on it; null for none.</summary>
    (string Pad, C3dPoint3 At)? PadUnderRay(Point3 origin, Point3 direction);

    /// <summary>The resolved assembly loop height of <paramref name="candidate"/>, DBU; null when it does not resolve.</summary>
    double? MeasureAssembly(C3dWire candidate);

    C3dWireTemplate WireTemplate { get; }

    string Length(long dbu);
}

public sealed class WireTool(IC3dDrawHost host, IC3dWireHost wires) : C3dDrawTool(host)
{
    private static readonly string[] None = [];
    private static readonly string[] LoopHeight = ["loop height"];

    /// <summary>What the first sketch of an arch rises to before a loop height is set: 150 µm, a common first guess.</summary>
    public const long DefaultAssemblyUm = 150;

    private C3dPoint3 _a, _b;
    private string _padA = "", _padB = "";
    private C3dWireTemplate _template = wires.WireTemplate;
    private (long Assembly, double? Measured, long AxisLoop, List<C3dPoint3> Points)? _shown;

    public override C3dToolKind Kind => C3dToolKind.Wire;

    /// <summary>The assembly loop height of the last wire this tool finished, DBU.</summary>
    public long? LastAssembly { get; private set; }

    public override string Prompt => Step switch
    {
        0 => "Wire: click the top of the first pad (a face-centre snap lands on a pad's middle).",
        1 => $"Wire: click the top of the second pad (from '{_padA}').",
        _ => _shown is { } s
            ? $"Wire: loop height {wires.Length(s.Assembly)} (assembly: lower foot to the top of the apex) · axis loop " +
              $"{wires.Length(s.AxisLoop)} — move and click, or type it. Esc cancels."
            : "Wire: move to set the loop height and click (or type it). Esc cancels.",
    };

    public override IReadOnlyList<string> Dimensions => Step == 2 ? LoopHeight : None;

    private long DefaultAssembly => _template.LoopHeightDbu ?? DefaultAssemblyUm * Host.DbuPerMicron;

    /// <summary>The cursor's assembly loop height: its z along the vertical through the arch's crest, above the lower pad.</summary>
    private long? Assembly(in C3dDrawInput input)
    {
        double s = LoopShape.SeedPeakSpan;
        var crest = new C3dPoint3(_a.X + (long)Math.Round((_b.X - _a.X) * s), _a.Y + (long)Math.Round((_b.Y - _a.Y) * s), Math.Max(_a.Z, _b.Z));
        return Host.Along(crest, C3dAxis.Z, input) is { } z ? Math.Max(0, z - Math.Min(_a.Z, _b.Z)) : null;
    }

    public override long?[] Current(in C3dDrawInput input) => Step == 2 ? [Assembly(input)] : [];

    private C3dPoint3? OnPad(in C3dDrawInput input, out string pad, out string? refusal)
    {
        pad = "";
        refusal = Step == 0 ? "A wire starts on the top of a pad." : "A wire ends on the top of a pad.";
        // A snapped point on a pad's top (a face centre, a corner, an edge) — or else the pad top under the cursor.
        if (input.Snap is { } snap && input.SnapOnGeometry && wires.PadAt(snap) is { } hit)
        {
            pad = hit.Pad;
            refusal = null;
            return snap with { Z = hit.TopDbu };
        }
        if (input.HasRay && wires.PadUnderRay(input.RayOrigin!.Value, input.RayDirection!.Value) is { } under)
        {
            pad = under.Pad;
            refusal = null;
            return under.At;
        }
        return null;
    }

    public override C3dToolStep Click(in C3dDrawInput input)
    {
        switch (Step)
        {
            case 0:
                if (OnPad(input, out _padA, out var why0) is not { } a) return C3dToolStep.Refuse(why0!);
                _template = wires.WireTemplate;
                _a = a;
                Step = 1;
                return C3dToolStep.Next;
            case 1:
                if (OnPad(input, out _padB, out var why1) is not { } b) return C3dToolStep.Refuse(why1!);
                if (b.X == _a.X && b.Y == _a.Y) return C3dToolStep.Refuse("A wire's two ends are at one plan position: pick the other pad.");
                _b = b;
                _shown = null;
                Step = 2;
                return C3dToolStep.Next;
            default:
                return Assembly(input) is { } h ? Finish(h) : C3dToolStep.Refuse("Wire: the cursor is looking straight down; type the loop height instead.");
        }
    }

    public override C3dToolStep Typed(long?[] values, in C3dDrawInput input)
    {
        if (Step != 2) return new(false);
        long? h = values.Length > 0 ? values[0] : null;
        h ??= Assembly(input);
        if (h is not { } hh) return C3dToolStep.Refuse("Wire: type the loop height.");
        if (hh <= 0) return C3dToolStep.Refuse("A loop height is above the lower foot: type a positive length.");
        return Finish(hh);
    }

    /// <summary>The wire between the two ends with an assembly loop height of <paramref name="assembly"/>, solved against
    /// its resolved solid; null points when it does not resolve at all.</summary>
    public (C3dWire Wire, double? Measured) Shape(C3dPoint3 a, C3dPoint3 b, long assembly, string name)
    {
        var w = New(name, []);
        double half = C3dWires.HalfHeightDbu(_template.Section, C3dWires.DiameterNm(w), Host.DbuPerMicron);
        var (points, measured) = C3dWires.ForAssemblyHeight(a, b, assembly, half, pts => wires.MeasureAssembly(New(name, pts)));
        w.Points = points;
        return (w, measured);
    }

    private C3dWire New(string name, List<C3dPoint3> points) => new()
    {
        Name = name,
        Material = _template.Material,
        Points = points,
        DiameterUm = _template.DiameterUm,
        Section = _template.Section == WireCrossSection.Hexagon ? null : _template.Section,
        Start = new C3dWireEnd { Style = _template.StartStyle },
        End = new C3dWireEnd { Style = _template.EndStyle },
    };

    /// <summary>The wire's axis loop height (max − min z of its points), DBU.</summary>
    private static long AxisLoop(C3dWire w) => w.Points.Count == 0 ? 0 : w.Points.Max(p => p.Z) - w.Points.Min(p => p.Z);

    private C3dToolStep Finish(long assembly)
    {
        var (wire, measured) = Shape(_a, _b, assembly, Host.NextName("w"));
        if (measured is null) return C3dToolStep.Refuse("Wire: this wire does not build between those pads (see 3D ▸ Check).");
        Step = 0;
        _shown = null;
        LastAssembly = assembly;
        return C3dToolStep.Done(wire);
    }

    public override void Preview(in C3dDrawInput input, List<DrawSegment> rubber, List<Point3> fixedPoints)
    {
        if (Step == 0) return;
        fixedPoints.Add(M(_a));
        List<C3dPoint3> arch;
        if (Step == 1)
        {
            if ((OnPad(input, out _, out _) ?? Host.FreePoint(input, out _)) is not { } c || (c.X == _a.X && c.Y == _a.Y)) return;
            arch = C3dWires.Arch(_a, c, DefaultAssembly);
        }
        else
        {
            fixedPoints.Add(M(_b));
            arch = Track(input).Points;
        }
        DrawGeometry.Chain(arch, false, Host.DbuPerMicron, rubber);
    }

    /// <summary>Step 3's arch for the cursor where it is — solved, measured, and kept for the prompt. The editor calls it
    /// on every cursor move, so the status bar's two numbers follow the mouse.</summary>
    public (long Assembly, double? Measured, long AxisLoop, List<C3dPoint3> Points) Track(in C3dDrawInput input)
    {
        long assembly = Assembly(input) ?? DefaultAssembly;
        if (_shown is { } s && s.Assembly == assembly) return s;
        var (w, measured) = Shape(_a, _b, assembly, "w");
        _shown = (assembly, measured, AxisLoop(w), w.Points);
        return _shown.Value;
    }

    public override void Reset()
    {
        base.Reset();
        _shown = null;
    }

    /// <summary>Back from the loop height to the second pad drops the arch the height was tracking.</summary>
    public override bool StepBack()
    {
        _shown = null;
        return base.StepBack();
    }
}
