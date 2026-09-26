// brief-em3d-46 R-em3d46-6 — Measure, in every 3D pane: the editor AND the read-only viewer (brief 43 §2c: the
// viewer is for measuring and reading). Two snapped clicks; after the first a rubber band follows the cursor and
// the card updates live; after the second the measurement STAYS until Esc, another tool, or a third click — which
// starts a new one from that point. It works in any selection mode and changes no selection.
//
// NOTHING IS WRITTEN (R-em3d46-6f): a measurement is not document state, adds no undo entry and does not dirty the
// document. It is two points held here and a line the 2D overlay projects each frame from the camera alone, so
// orbiting keeps the line on its points and uploads nothing.

using CircuitRF.Design.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    /// <summary>Measure is armed: clicks place its points.</summary>
    [ObservableProperty] private bool _measureActive;

    /// <summary>The card, or null before the first point.</summary>
    [ObservableProperty] private Viewer3DMeasureReadout? _measureReadout;

    /// <summary>The first point, and the second — live while it follows the cursor, fixed once clicked.</summary>
    public Viewer3DMeasurePoint? MeasureP1 { get; private set; }
    public Viewer3DMeasurePoint? MeasureP2 { get; private set; }
    public bool MeasureP2Fixed { get; private set; }

    /// <summary>The unit and DBU the card spells in: the document's display unit, or the viewer's layout's.</summary>
    public LayoutUnit MeasureUnit { get; private set; } = LayoutUnit.Um;
    public int MeasureDbuPerMicron { get; private set; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>The display unit changed: the card is spelled again. The points do not move.</summary>
    public void SetMeasureUnits(LayoutUnit unit, int dbuPerMicron)
    {
        MeasureUnit = unit;
        MeasureDbuPerMicron = dbuPerMicron;
        RefreshMeasureReadout();
    }

    public bool IsMeasureArmed { get => MeasureActive; set { if (value) StartMeasure(); else EndMeasure(); } }

    partial void OnMeasureActiveChanged(bool value) => OnPropertyChanged(nameof(IsMeasureArmed));

    /// <summary><c>M</c>, 3D ▸ Measure, the toolbar: arms Measure, or ends it when it is armed.</summary>
    public void ToggleMeasure()
    {
        if (MeasureActive) EndMeasure();
        else StartMeasure();
    }

    public void StartMeasure()
    {
        if (MeasureActive) return;
        EditHost?.MeasureStarted();
        MeasureP1 = MeasureP2 = null;
        MeasureP2Fixed = false;
        MeasureActive = true;
        MeasureReadout = null;
        FrameRequested?.Invoke();
    }

    /// <summary>Esc, another tool: the measurement goes away.</summary>
    public void EndMeasure()
    {
        if (!MeasureActive && MeasureP1 is null) return;
        MeasureActive = false;
        MeasureP1 = MeasureP2 = null;
        MeasureP2Fixed = false;
        MeasureReadout = null;
        FrameRequested?.Invoke();
    }

    /// <summary>A click: the first point, the second, or — with both fixed — a new first point.</summary>
    public void MeasureClick()
    {
        if (MeasureCursorPoint() is not { } p) return;
        if (MeasureP1 is null || MeasureP2Fixed)
        {
            MeasureP1 = p;
            MeasureP2 = null;
            MeasureP2Fixed = false;
        }
        else
        {
            MeasureP2 = p;
            MeasureP2Fixed = true;
        }
        RefreshMeasureReadout();
        FrameRequested?.Invoke();
    }

    /// <summary>The cursor moved: a live second point follows it.</summary>
    private void MeasureFollow()
    {
        if (!MeasureActive || MeasureP1 is null || MeasureP2Fixed) return;
        var p = MeasureCursorPoint();
        if (p == MeasureP2) return;
        MeasureP2 = p;
        RefreshMeasureReadout();
        FrameRequested?.Invoke();
    }

    private void RefreshMeasureReadout()
        => MeasureReadout = MeasureP1 is { } a ? Viewer3DMeasureReadout.Build(a, MeasureP2, MeasureUnit, MeasureDbuPerMicron) : null;

    /// <summary>
    /// The point a click takes: the editor's (the snap as the document's exact point, else the drawing plane);
    /// in the viewer the snap when it is on, else the surface under the cursor. A surface point is not a feature,
    /// so it is marked ≈.
    /// </summary>
    private Viewer3DMeasurePoint? MeasureCursorPoint()
    {
        if (View.CursorX < 0) return null;
        if (EditHost?.MeasurePoint() is { } e) return e;
        double per = 1e-6 / Math.Max(1, MeasureDbuPerMicron);
        if (Snap.IsSnap)
        {
            var w = Snap.World;
            return new Viewer3DMeasurePoint(w.X, w.Y, w.Z, Whole(w.X) && Whole(w.Y) && Whole(w.Z));
        }
        if (_lastCursorWorld is { } c) return new Viewer3DMeasurePoint(c.X, c.Y, c.Z, false);
        return null;

        bool Whole(double m) { double d = m / per; return Math.Abs(d - Math.Round(d)) <= 1e-6; }
    }
}
