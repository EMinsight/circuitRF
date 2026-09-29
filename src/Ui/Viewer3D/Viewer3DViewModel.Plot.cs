// brief-em3d-83 — the viewer draws ONE document field plot. The editor hands it the visible plot, resolved against the scene
// (FieldPlotRequest), and the viewer sets its own field state from it: which run it reads, which saved solution — picked BY
// VALUE, never by the combobox's index — which quantity, where, and the colour scale. The phase, the loop and play/pause stay
// the viewer's: an animation is not a design decision.
//
// A plot whose data is missing draws NOTHING and says why (FieldPlotProblem): no run of its setup, a run that no longer holds
// its solution, a step that no longer offers its quantity. It is never re-pointed to the nearest frequency (R-em3d83-5).
//
// With no plot (null) the viewer is what brief 29 made it: the ACTIVE setup's run, read and offered, nothing drawn.
//
// brief-em3d-84 — the RESOLUTION (run directories, discovery, the solution by value, the R-em3d83-5 sentences, the quantity pick,
// the legend's lines) is FieldPlotResolver's, below the firewall, so `circuitrf render --field` resolves a plot as this does.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    private FieldPlotRequest? _plot;
    private bool _applyingPlot;

    /// <summary>The plot drawn, or null.</summary>
    public FieldPlotRequest? Plot => _plot;

    /// <summary>R-em3d83-5 — why the plot draws nothing (no run, a solution or quantity the run no longer holds), or null.</summary>
    [ObservableProperty] private string? _fieldPlotProblem;

    /// <summary>The run's fields were read again (a new run, another setup): the editor re-checks its plots.</summary>
    public event Action? FieldsRead;

    /// <summary>
    /// Draws <paramref name="plot"/> (null draws nothing). A plot on another setup's run reads that run first; the same run
    /// re-picks its solution and quantity by value and rebuilds what is drawn.
    /// </summary>
    public void SetPlot(FieldPlotRequest? plot)
    {
        _plot = plot;
        if (plot is null)
        {
            FieldPlotProblem = null;
            _applyingPlot = true;
            try
            {
                _fieldFaces.Clear();
                _temperatureFaces.Clear();
                TemperatureAllFaces = false;
                TemperatureOnClip = false;
                ShowField = false;
            }
            finally { _applyingPlot = false; }
            ClearFieldGeometry();
            FieldText = "";
        }
        if (FieldRunDirectories() != _fieldDirs) { RefreshFields(); return; }   // ApplyPlot runs when the run has been read
        if (plot is not null) ApplyPlot();
    }

    /// <summary>The plot's settings into the field state, its solution picked by value, then one rebuild.</summary>
    private void ApplyPlot()
    {
        if (_plot is not { } p) return;
        FieldSolutionItem? item;
        bool moved;
        _applyingPlot = true;
        try
        {
            FieldOnClipPlane = p.On == C3dFieldPlotOn.ClipPlane;
            FieldOnSurfaces = p.On == C3dFieldPlotOn.Surfaces;
            FieldDb = p.Db;
            FieldPercentile = p.Percentile;
            FixRangeAcrossSweep = p.FixRange;
            _fieldFaces.Clear();
            _temperatureFaces.Clear();
            if (p.On == C3dFieldPlotOn.Faces)
            {
                if (p.IsTemperature) _temperatureFaces.AddRange(p.Faces.Select(f => new TemperatureFace(f.Object, f.Face)));
                else _fieldFaces.AddRange(p.Faces);
            }
            TemperatureAllFaces = p.IsTemperature && p.On == C3dFieldPlotOn.Surfaces;
            TemperatureOnClip = p.IsTemperature && p.On == C3dFieldPlotOn.ClipPlane;
            item = PlotSolution(p);
            moved = !ReferenceEquals(SelectedFieldSolution, item);
            if (moved) SelectedFieldSolution = item;
            ShowField = item is not null;
        }
        finally { _applyingPlot = false; }
        FieldPlotProblem = item is null ? FieldPlotResolver.PlotProblem(p, [.. FieldSolutions], FieldsRan, Scene.Problem) : null;
        if (item is null)
        {
            ClearFieldGeometry();
            FieldText = FieldPlotProblem ?? "";
            return;
        }
        // A sweep step on the same thermal run re-reads its temperature alone (brief 75 gate 7).
        if (moved && p.IsTemperature && TryRevalueTemperature(item)) return;
        EnsureFieldLoaded();
    }

    /// <summary>The plot's quantity among what the loaded step offers; false (and the reason) when it offers none.</summary>
    private bool PickPlotQuantity(FieldPlotRequest p)
    {
        var q = FieldPlotResolver.PickQuantity([.. FieldQuantities], p, out string? problem);
        _applyingPlot = true;
        try { SelectedFieldQuantity = q; }
        finally { _applyingPlot = false; }
        if (q is not null) return true;
        FieldPlotProblem = problem;
        ClearFieldGeometry();
        FieldText = FieldPlotProblem!;
        return false;
    }

    /// <summary>The saved solution a plot shows: its value among the run's (of its solver), or the first when it names none.</summary>
    private FieldSolutionItem? PlotSolution(FieldPlotRequest p) => FieldPlotResolver.PickSolution(p.Solution, p.Solver, FieldSolutions, Scene.Problem);

    /// <summary>The plane a ClipPlane plot cuts on, or the view's own clip plane with no plot.</summary>
    private ClipPlane3D FieldPlane => _plot is { On: C3dFieldPlotOn.ClipPlane } p ? p.Plane : View.Clip;

    /// <summary>A run directory of the setup read exists: it ran, whether or not it saved fields.</summary>
    private bool FieldsRan { get; set; }
}
