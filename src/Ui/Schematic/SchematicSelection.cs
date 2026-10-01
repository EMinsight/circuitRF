namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Tracks which schematic objects are currently selected.
/// Selection is view state — not persisted to .csch and not part of SchematicEditModel.
/// Fires Changed so SchematicViewModel can invalidate the canvas.
///
/// Two orthogonal selection types are maintained:
///   • Object IDs (components, whole wires, canvas objects) — in _ids
///   • Wire segment selections as (wireId, segmentIndex) pairs — in _segments
/// They coexist; clearing one does not clear the other unless Clear() is called.
/// </summary>
public sealed class SchematicSelection
{
    private readonly HashSet<string> _ids = new();
    private readonly HashSet<(string WireId, int SegmentIndex)> _segments = new();

    // The stretch of a selected segment between the junctions either side of the click, for a segment
    // something joins between its vertices (WireGeometry.SpanAround). Absent: the whole segment.
    private readonly Dictionary<(string WireId, int SegmentIndex), ((double X, double Y) A, (double X, double Y) B)> _spans = new();

    public event EventHandler? Changed;

    /// <summary>Current selected object IDs (components, wires, canvas objects).</summary>
    public IReadOnlySet<string> Ids => _ids;

    public bool IsEmpty => _ids.Count == 0 && _segments.Count == 0;
    public int  Count   => _ids.Count;

    /// <summary>True when at least one wire segment is specifically selected.</summary>
    public bool HasSelectedSegments => _segments.Count > 0;

    public bool IsSelected(string id) => _ids.Contains(id);

    /// <summary>Returns true when the specified wire segment is selected.</summary>
    public bool IsSegmentSelected(string wireId, int segmentIndex)
        => _segments.Contains((wireId, segmentIndex));

    // ── Object ID selection ───────────────────────────────────────────────────

    public void SelectOne(string id)
    {
        _ids.Clear();
        _segments.Clear();
        _spans.Clear();
        _ids.Add(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(string id)
    {
        _ids.Add(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Toggle(string id)
    {
        if (!_ids.Remove(id)) _ids.Add(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetAll(IEnumerable<string> ids)
    {
        _ids.Clear();
        _segments.Clear();
        _spans.Clear();
        foreach (var id in ids) _ids.Add(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets object selection without firing Changed (caller handles overlay update).</summary>
    public void SetAllSilent(IEnumerable<string> ids)
    {
        _ids.Clear();
        _segments.Clear();
        _spans.Clear();
        foreach (var id in ids) _ids.Add(id);
    }

    // ── Segment selection ─────────────────────────────────────────────────────

    /// <summary>Clears all selection and selects exactly one segment.</summary>
    public void SelectOneSegment(string wireId, int segmentIndex,
                                 ((double X, double Y) A, (double X, double Y) B)? span = null)
    {
        _ids.Clear();
        _segments.Clear();
        _spans.Clear();
        _segments.Add((wireId, segmentIndex));
        if (span is { } sp) _spans[(wireId, segmentIndex)] = sp;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears all selection and selects exactly these segments, each with its stretch (null:
    /// the whole segment) — Alt + double-click's wire run (<see cref="WireRunSelector"/>).</summary>
    public void SelectSegments(IEnumerable<(string WireId, int SegmentIndex, ((double X, double Y) A, (double X, double Y) B)? Span)> segments)
    {
        _ids.Clear();
        _segments.Clear();
        _spans.Clear();
        foreach (var (wireId, index, span) in segments)
        {
            _segments.Add((wireId, index));
            if (span is { } sp) _spans[(wireId, index)] = sp;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a segment to the selection (without clearing other selections).</summary>
    public void AddSegment(string wireId, int segmentIndex)
    {
        _segments.Add((wireId, segmentIndex));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Toggles a segment in/out of the selection.</summary>
    public void ToggleSegment(string wireId, int segmentIndex,
                              ((double X, double Y) A, (double X, double Y) B)? span = null)
    {
        var key = (wireId, segmentIndex);
        _spans.Remove(key);
        if (!_segments.Remove(key))
        {
            _segments.Add(key);
            if (span is { } sp) _spans[key] = sp;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears only segment selections, fires Changed if any were present.</summary>
    public void ClearSegments()
    {
        if (_segments.Count == 0) return;
        _segments.Clear();
        _spans.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears segment selections without firing Changed (caller handles overlay update).</summary>
    public void ClearSegmentsSilent()
    {
        _segments.Clear();
        _spans.Clear();
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    /// <summary>Clears both object-ID and segment selections. Fires Changed if either was non-empty.</summary>
    public void Clear()
    {
        bool any = _ids.Count > 0 || _segments.Count > 0;
        _ids.Clear();
        _segments.Clear();
        _spans.Clear();
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── Query helpers ─────────────────────────────────────────────────────────

    /// <summary>Returns selected component IDs that exist in the edit model.</summary>
    public IReadOnlyList<string> GetSelectedComponentIds(SchematicEditModel model)
        => _ids.Where(id => model.FindComponent(id) is not null).ToList();

    /// <summary>Returns selected wire IDs that exist in the edit model.</summary>
    public IReadOnlyList<string> GetSelectedWireIds(SchematicEditModel model)
        => _ids.Where(id => model.FindWire(id) is not null).ToList();

    /// <summary>Returns selected canvas-object IDs that exist in the edit model.</summary>
    public IReadOnlyList<string> GetSelectedCanvasObjectIds(SchematicEditModel model)
        => _ids.Where(id => model.FindCanvasObject(id) is not null).ToList();

    /// <summary>
    /// Returns valid selected segments: wire exists and segment index is in range.
    /// </summary>
    public IReadOnlyList<(string WireId, int SegmentIndex)> GetSelectedSegments(SchematicEditModel model)
        => _segments
            .Where(s => model.FindWire(s.WireId) is { } w && s.SegmentIndex < w.Points.Count - 1)
            .ToList();

    /// <summary>
    /// The selected stretch of each selected segment that has one and whose stretch still lies on it
    /// (<see cref="WireGeometry.ValidSpan"/>). A selected segment absent here is selected whole — what is
    /// highlighted and what Delete removes are read from this one place, so they cannot differ.
    /// </summary>
    public IReadOnlyDictionary<(string WireId, int SegmentIndex), ((double X, double Y) A, (double X, double Y) B)>
        GetSelectedSpans(SchematicEditModel model)
    {
        var r = new Dictionary<(string WireId, int SegmentIndex), ((double X, double Y) A, (double X, double Y) B)>();
        foreach (var (key, span) in _spans)
            if (_segments.Contains(key) && model.FindWire(key.WireId) is { } w &&
                WireGeometry.ValidSpan(w, key.SegmentIndex, span) is { } ok)
                r[key] = ok;
        return r;
    }

    /// <summary>Returns all segment selections (no model validation).</summary>
    public IReadOnlyList<(string WireId, int SegmentIndex)> GetSelectedSegments()
        => _segments.ToList();

    public HashSet<string> SnapshotSet() => new(_ids);
}
