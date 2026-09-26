// brief-em3d-46 R-em3d46-1 — what an object operation is while it runs: a gesture on the SELECTION, fed the same
// cursor input a drawing tool is (C3dDrawInput: the snap, exact or not, and the cursor's ray), which states at
// every moment ONE rigid transform of the world in DBU. The editor draws that transform as the preview — per-draw
// transforms on what is already drawn — and composes it into the targets' placements once, on the commit.
//
// AN OPERATION NEVER TOUCHES THE DOCUMENT, like a drawing tool (brief 45): a cancelled one leaves the document
// byte for byte as it was, and a committed one is ONE undo entry.

using Avalonia.Input;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Tools;

namespace CircuitRF.Ui.ThreeD.Operations;

/// <summary>One thing an operation acts on: a document object (<see cref="Instance"/> false) or an instance, by index.</summary>
public readonly record struct C3dTarget(bool Instance, int Index);

/// <summary>The rigid transform an operation states now: world DBU, and whether it is exact (both of its points
/// were exact DBU points, and it is an integer transform).</summary>
public readonly record struct C3dOperationTransform(C3dTransform Transform, bool Exact);

public abstract class C3dOperationTool(IC3dDrawHost host, IReadOnlyList<C3dTarget> targets, C3dPoint3 pivot) : C3dDrawTool(host)
{
    /// <summary>What it acts on, fixed when it started.</summary>
    public IReadOnlyList<C3dTarget> Targets { get; } = targets;

    /// <summary>The pivot: the targets' bounding-box centre, unless the user set another (a rotation's Ctrl/Cmd-click).</summary>
    public C3dPoint3 Pivot { get; protected set; } = pivot;

    /// <summary>Whether the overlay draws <see cref="Pivot"/> as a cross.</summary>
    public virtual bool ShowsPivot => false;

    /// <summary>The copies stay where they are and the moved ones are NEW (Duplicate).</summary>
    public virtual bool KeepsOriginal => false;

    /// <summary>The transform the cursor states now, or null when it states none (no base point yet, the ray misses).</summary>
    public abstract C3dOperationTransform? Current(in C3dDrawInput input, out string? refusal);

    /// <summary>The transform the commit writes: the one fixed by the finishing click or the typed field.</summary>
    public C3dOperationTransform? Committed { get; protected set; }

    /// <summary>The transform is a translation, committed to the origin only, so a hand-written rotation list survives
    /// verbatim (Move; brief-em3d-47's Align to Face).</summary>
    public virtual bool TranslationOnly => false;

    /// <summary>A key while it runs (an axis lock); true when it took it.</summary>
    public virtual bool Key(Key key, KeyModifiers modifiers) => false;

    /// <summary>A Ctrl/Cmd-click: the pivot, where the operation has one.</summary>
    public virtual bool SetPivot(in C3dDrawInput input) => false;

    /// <summary>What the status line says of the constraint in force, or empty.</summary>
    public virtual string ConstraintText => "";

    protected static C3dPoint3 Delta(C3dPoint3 a, C3dPoint3 b) => b - a;
}
