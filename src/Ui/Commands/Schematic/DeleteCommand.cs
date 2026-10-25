using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

// ── Snapshot types for delete ─────────────────────────────────────────────────

internal readonly record struct DeletedComponent(EditableComponent Component, int Index);
internal readonly record struct DeletedWire(EditableWire Wire, int Index);
internal readonly record struct DeletedCanvasObject(EditableCanvasObject Object, int Index);
internal readonly record struct DeletedNetLabel(EditableNetLabel Label, int Index);
internal readonly record struct DeletedDot(EditableDot Dot, int Index);

/// <summary>
/// Deletes a selection of schematic objects.
/// Undo restores them at their original list positions and re-selects them.
/// </summary>
internal sealed class DeleteCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Action<IEnumerable<string>>? _reselect;

    private readonly List<DeletedComponent>     _comps  = [];
    private readonly List<DeletedWire>           _wires  = [];
    private readonly List<DeletedCanvasObject>   _cobjs  = [];
    private readonly List<DeletedNetLabel>       _labels = [];
    private readonly List<DeletedDot>            _dots   = [];

    public string Description => BuildDescription();

    public DeleteCommand(
        SchematicEditModel model,
        IReadOnlyList<string> selectedIds,
        Action<IEnumerable<string>>? reselect = null)
    {
        _model    = model;
        _reselect = reselect;

        // Snapshot positions before deletion
        foreach (var id in selectedIds)
        {
            for (int i = 0; i < model.Components.Count; i++)
                if (model.Components[i].Id == id) { _comps.Add(new(model.Components[i], i)); break; }

            for (int i = 0; i < model.Wires.Count; i++)
                if (model.Wires[i].Id == id) { _wires.Add(new(model.Wires[i], i)); break; }

            for (int i = 0; i < model.CanvasObjects.Count; i++)
                if (model.CanvasObjects[i].Id == id) { _cobjs.Add(new(model.CanvasObjects[i], i)); break; }

            for (int i = 0; i < model.NetLabels.Count; i++)
                if (model.NetLabels[i].Id == id) { _labels.Add(new(model.NetLabels[i], i)); break; }

            for (int i = 0; i < model.Dots.Count; i++)
                if (model.Dots[i].Id == id) { _dots.Add(new(model.Dots[i], i)); break; }
        }

        TakeLabelsOnDeletedPins();
    }

    /// <summary>
    /// A free-standing net label names the pin it sits on — an imported schematic puts one on every pin it joined by
    /// name rather than by wire. Deleting the part takes such a label with it, as deleting a wire takes the labels
    /// anchored to it (brief DFR26-5 R-dfr26-5-4): left behind it floats on the sheet naming nothing. A label that
    /// something staying still meets — another part's pin, a wire through the point — keeps naming that, and stays.
    /// </summary>
    private void TakeLabelsOnDeletedPins()
    {
        if (_comps.Count == 0) return;
        const double tol = SchematicEditModel.ConnectTolerance;
        var gone = _comps.Select(c => c.Component).ToHashSet();
        var goneWires = _wires.Select(w => w.Wire).ToHashSet();
        var taken = _labels.Select(l => l.Label).ToHashSet();
        var pins = gone.SelectMany(c => _model.PortDefsOf(c).Select(d => _model.PortWorldOf(c, d))).ToList();

        bool Touched(double x, double y)
            => _model.Components.Any(c => !gone.Contains(c)
                   && _model.PortDefsOf(c).Any(d => _model.PortWorldOf(c, d) is var p
                                                    && SchematicGeometry.CoincidentPoints(p.X, p.Y, x, y, tol)))
               || _model.Wires.Any(w => !goneWires.Contains(w) && OnWire(w, x, y, tol));

        for (int i = 0; i < _model.NetLabels.Count; i++)
        {
            var l = _model.NetLabels[i];
            if (l.IsAnchored || taken.Contains(l)) continue;
            if (!pins.Any(p => SchematicGeometry.CoincidentPoints(p.X, p.Y, l.X, l.Y, tol))) continue;
            if (Touched(l.X, l.Y)) continue;
            _labels.Add(new(l, i));
        }
    }

    private static bool OnWire(EditableWire w, double x, double y, double tol)
    {
        var pts = w.Points;
        if (pts.Count == 1) return SchematicGeometry.CoincidentPoints(pts[0].X, pts[0].Y, x, y, tol);
        for (int i = 0; i + 1 < pts.Count; i++)
            if (SchematicGeometry.PointOnSegment(x, y, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, tol)) return true;
        return false;
    }

    public void Execute()
    {
        foreach (var s in _comps)  _model.Components.Remove(s.Component);
        foreach (var s in _wires)  _model.Wires.Remove(s.Wire);
        foreach (var s in _cobjs)  _model.CanvasObjects.Remove(s.Object);
        foreach (var s in _labels) _model.NetLabels.Remove(s.Label);
        foreach (var s in _dots)   _model.Dots.Remove(s.Dot);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        // Re-insert at original indices (in reverse deletion order to keep indices valid)
        foreach (var s in _comps.OrderBy(x => x.Index))
            _model.Components.Insert(Math.Min(s.Index, _model.Components.Count), s.Component);

        foreach (var s in _wires.OrderBy(x => x.Index))
            _model.Wires.Insert(Math.Min(s.Index, _model.Wires.Count), s.Wire);

        foreach (var s in _cobjs.OrderBy(x => x.Index))
            _model.CanvasObjects.Insert(Math.Min(s.Index, _model.CanvasObjects.Count), s.Object);

        foreach (var s in _labels.OrderBy(x => x.Index))
            _model.NetLabels.Insert(Math.Min(s.Index, _model.NetLabels.Count), s.Label);

        foreach (var s in _dots.OrderBy(x => x.Index))
            _model.Dots.Insert(Math.Min(s.Index, _model.Dots.Count), s.Dot);

        _model.NotifyChanged();

        // Re-select all restored items
        var ids = _comps.Select(s => s.Component.Id)
            .Concat(_wires.Select(s => s.Wire.Id))
            .Concat(_cobjs.Select(s => s.Object.Id))
            .Concat(_labels.Select(s => s.Label.Id))
            .Concat(_dots.Select(s => s.Dot.Id));
        _reselect?.Invoke(ids);
    }

    private string BuildDescription()
    {
        int total = _comps.Count + _wires.Count + _cobjs.Count + _labels.Count + _dots.Count;
        return total == 1 ? "Delete object" : $"Delete {total} objects";
    }
}
