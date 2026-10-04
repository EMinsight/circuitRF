// brief-em3d-75 R-em3d75-4 / -5 — Plot Temperature: a thermal run's field over the model, on the same path as an EM field
// (brief 29's FieldRun → FieldStep → surfaces → FieldVertex), read from the run's thermal.pvd with no new reader.
//
// WHERE (4a) is three independent choices: the faces the user asked for one by one (right-click ▸ Plot Temperature, which
// toggles, and accumulates), every exposed face (View ▸ Temperature ▸ All Faces), and the clip plane's section. A painted face
// is drawn a hair in front of its own face, so the model around it is drawn as it was; All Faces stands in for the solids'
// own triangles, as an EM field on a solid does. Wires are coloured along their length whenever temperature is shown and the
// run tabulated them (brief 77 writes the tables; R-em3d75-5 draws them).
//
// THE RANGE (4b, D9) is the TRUE minimum and maximum of what is drawn — never a percentile — or, with Fix Range Across Sweep,
// the union of every sweep point's, so stepping the sweep never rescales the colours.
//
// A SWEEP STEP (4c, gate 7) re-reads ONE ARRAY: every step of a thermal run is written on the same mesh, so the step's pieces
// are read for T_C alone (FieldStep.WithArraysOf keeps the mesh already read) and every drawn surface is revalued through the
// recipe its cut or gather left (FieldSurface.Revalue). Nothing is sliced or gathered again: FieldGeometryBuilds does not move.
// The vertex buffer is still one interleaved FieldVertex array, so the re-upload carries the (unchanged) positions too — the
// GPU path has one field stream on all three backends, and splitting it was not this brief's to do.
//
// brief-em3d-96 — all of it per LAYER: the sweep step is one control, and every temperature layer on the focused layer's run
// follows it (OnSelectedFieldSolutionChanged).

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Thermal;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    private TemperatureTargets CurrentTemperatureTargets(FieldLayer layer)
    {
        var clip = PlaneOf(layer);
        return new(layer.TempAllFaces, layer.TempOnClip && clip.Enabled, clip.Equation, layer.FixRange,
                   MirrorSymmetry ? [.. SymmetryPlanes] : []);
    }

    /// <summary>brief-em3d-76 R-em3d76-4a — the document's symmetry planes (axis 0 x, 1 y, 2 z; world metres).</summary>
    public IReadOnlyList<(int Axis, double AtM)> SymmetryPlanes { get; private set; } = [];

    /// <summary>brief-em3d-76 R-em3d76-4c — draw the mirrored halves' temperature too. On by default.</summary>
    [ObservableProperty] private bool _mirrorSymmetry = true;

    partial void OnMirrorSymmetryChanged(bool value)
    {
        if (SymmetryPlanes.Count > 0) ScheduleFieldGeometry(l => l.Quantity is { IsTemperature: true });
    }

    /// <summary>The editor's symmetry planes; a change redraws a shown temperature.</summary>
    public void SetSymmetryPlanes(IReadOnlyList<(int Axis, double AtM)> planes)
    {
        if (planes.SequenceEqual(SymmetryPlanes)) return;
        SymmetryPlanes = planes;
        ScheduleFieldGeometry(l => l.Quantity is { IsTemperature: true });
    }

    /// <summary>Gate 7 — how many times drawn triangles were given another step's values and nothing else, over every layer.</summary>
    public long FieldRevalues { get; private set; }

    /// <summary>The run whose fields the focused layer reads is a thermal one.</summary>
    public bool IsThermalRun => Focused.Run?.Kind == FieldProblemKind.Thermal;

    /// <summary>The focused layer's thermal run's table (the .npy), or null.</summary>
    public ThermalResultTable? ThermalTable => Focused.Read?.Table;

    /// <summary>R-em3d75-4a — every exposed and conditioned face painted.</summary>
    [ObservableProperty] private bool _temperatureAllFaces;

    /// <summary>R-em3d75-4a — the clip plane's section painted.</summary>
    [ObservableProperty] private bool _temperatureOnClip;

    /// <summary>R-em3d75-4b — the range is the union of every sweep point's, so a step never rescales.</summary>
    [ObservableProperty] private bool _fixRangeAcrossSweep;

    /// <summary>R-em3d75-4c — the sweep slider: the step shown, 0-based.</summary>
    [ObservableProperty] private int _temperatureStep;

    /// <summary>The hottest point of what the focused layer draws, and what it is on — the hot-spot marker.</summary>
    public FieldHotSpot? HotSpot => Focused.HotSpot;

    /// <summary>The marker's label: <c>87.3 °C, die</c>.</summary>
    public string HotSpotLabel => Focused.HotSpotLabel;

    /// <summary>brief-em3d-96 — every drawn temperature layer's hot spot and label (the overlay rings each).</summary>
    public IReadOnlyList<(FieldHotSpot Spot, string Label)> HotSpots
        => [.. DrawnLayers.Where(l => l.Quantity is { IsTemperature: true } && l.HotSpot is not null).Select(l => (l.HotSpot!.Value, l.HotSpotLabel))];

    /// <summary>The focused layer's faces painted one by one, in the order they were asked for.</summary>
    public IReadOnlyList<TemperatureFace> TemperatureFaces => Focused.TempFaces;

    /// <summary>The slider's last position.</summary>
    public int TemperatureStepMax => Math.Max(0, FieldSolutions.Count - 1);

    /// <summary>The slider's label: the sweep values of the step shown.</summary>
    public string TemperatureStepLabel => ThermalTable is { } t && TemperatureStep < t.Points ? t.PointLabel(TemperatureStep)
                                        : FieldSolutions.Count > 1 ? $"point {TemperatureStep + 1} of {FieldSolutions.Count}" : "";

    /// <summary>A layer's step label: the sweep values of the step it shows.</summary>
    private static string StepLabelOf(FieldLayer layer)
    {
        var items = layer.Read?.Items ?? [];
        int step = layer.Item is { } i ? Math.Max(0, IndexOfItem(items, i)) : 0;
        return layer.Read?.Table is { } t && step < t.Points ? t.PointLabel(step)
             : items.Count > 1 ? $"point {step + 1} of {items.Count}" : "";
    }

    public bool HasTemperatureSweep => IsThermalRun && FieldSolutions.Count > 1;

    /// <summary>Temperature is being drawn: the focused layer shows a thermal run's field.</summary>
    public bool ShowsTemperature => ShowField && SelectedFieldQuantity is { IsTemperature: true };

    /// <summary>
    /// R-em3d75-4a — toggles temperature on face <paramref name="face"/> of scene object <paramref name="obj"/>: added (and the
    /// temperature shown) when it was not painted, taken away when it was. Several faces accumulate.
    /// </summary>
    public void ToggleTemperatureFace(string obj, int face)
    {
        var f = new TemperatureFace(obj, face);
        if (!Focused.TempFaces.Remove(f)) Focused.TempFaces.Add(f);
        ShowTemperature();
    }

    public bool IsTemperatureFace(string obj, int face) => Focused.TempFaces.Contains(new TemperatureFace(obj, face));

    /// <summary>Clears every painted face, All Faces and the clip plane: the temperature goes away.</summary>
    public void ClearTemperature()
    {
        Focused.TempFaces.Clear();
        TemperatureAllFaces = false;
        TemperatureOnClip = false;
        ShowField = false;
    }

    partial void OnTemperatureAllFacesChanged(bool value)
    {
        if (_applyingPlot) return;
        Focused.TempAllFaces = value;
        ShowTemperature();
    }

    partial void OnTemperatureOnClipChanged(bool value)
    {
        if (_applyingPlot) return;
        Focused.TempOnClip = value;
        ShowTemperature();
    }

    partial void OnFixRangeAcrossSweepChanged(bool value)
    {
        if (_applyingPlot) return;
        Focused.FixRange = value;
        if (!ShowsTemperature) return;
        ScheduleFieldGeometry(Focused);
    }

    partial void OnTemperatureStepChanged(int value)
    {
        OnPropertyChanged(nameof(TemperatureStepLabel));
        if (value >= 0 && value < FieldSolutions.Count && !ReferenceEquals(SelectedFieldSolution, FieldSolutions[value]))
            SelectedFieldSolution = FieldSolutions[value];
    }

    /// <summary>Shows the temperature when something is asked for, hides it when nothing is; rebuilds what is drawn.</summary>
    private void ShowTemperature()
    {
        if (_applyingPlot) return;                 // brief-em3d-83 — a plot sets the targets, then draws once
        var layer = Focused;
        bool any = layer.TempFaces.Count > 0 || layer.TempAllFaces || layer.TempOnClip;
        if (!any) { if (ShowsTemperature) ShowField = false; return; }
        if (!IsThermalRun) return;
        if (FieldQuantities.FirstOrDefault(q => q.IsTemperature) is { } t && !Equals(SelectedFieldQuantity, t)) SelectedFieldQuantity = t;
        if (!ShowField) ShowField = true;
        else ScheduleFieldGeometry(layer);
    }

    // ── building what is drawn ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Off the UI thread: the temperature's surfaces for the chosen targets on <paramref name="vol"/>, the wires coloured from
    /// the table at <paramref name="step"/>, the range, and the hot spot. Called by ScheduleFieldGeometry for a temperature.
    /// brief-em3d-89 — built by FieldSurfacePlot.Temperature, which `render --field` draws a Surfaces or Faces plot from too.
    /// </summary>
    private static (TemperatureParts Parts, FieldColorScale Scale, string? Note, HashSet<string> Covered) BuildTemperature(
        FieldQuantity q, FieldStep vol, FieldArray array, Scene3DModel scene, ClipPlane3D clip, IReadOnlyList<FieldGroup> groups,
        IReadOnlyList<TemperatureFace> faces, bool allFaces, bool onClip, ThermalResultTable? table, int step,
        IReadOnlyList<FieldSolution> steps, bool fixRange, long geometryVersion, CancellationToken ct,
        IReadOnlyList<(int Axis, double AtM)>? mirrors = null, TemperatureTargets? targets = null)
    {
        var b = FieldSurfacePlot.Temperature(q, vol, array, scene, clip, groups, faces, allFaces, onClip, table, step, steps, fixRange, ct, mirrors);
        return (new TemperatureParts(b.Surfaces, b.Nudges, b.Objects, b.Wires, faces, geometryVersion) { Reflections = b.Reflections, Targets = targets },
                b.Scale, b.Note, b.Covered);
    }

    private static FieldColorScale? Range(FieldQuantity q, IReadOnlyList<FieldSurface> surfaces, IReadOnlyList<FieldWireSurface> wires,
                                          ThermalResultTable? table, int step)
        => FieldSurfacePlot.Range(q, surfaces, wires, table, step);

    private static WireTemperature? Table(ThermalResultTable table, string wire, int step) => FieldSurfacePlot.Table(table, wire, step);

    /// <summary>Adopts a built temperature on the UI thread: the geometry, the range, the hot spot, the covered objects.</summary>
    private void AdoptTemperature(FieldLayer layer, TemperatureParts parts, FieldQuantity q, FieldColorScale scale, IReadOnlyCollection<string> covered,
                                  string? note)
    {
        layer.Temperature = parts;
        var all = parts.Surfaces.Concat(parts.Wires.Select(w => w.Surface)).ToList();
        var nudges = parts.Nudges.Concat(parts.Wires.Select(_ => Vector3.Zero)).ToList();
        layer.Surfaces = all;
        layer.OwnScale = scale;
        layer.Geometry = new Scene3DFieldGeometry(Scene3DFieldGeometry.Pack(q, all, nudges), ++_fieldVersion, parts.GeometryVersion);
        layer.Covered = new HashSet<string>(covered, StringComparer.Ordinal);
        layer.Covered.UnionWith(parts.Wires.Select(w => w.Wire));
        layer.HotSpot = FieldHotSpot.Of(q, all, nudges);
        layer.HotSpotLabel = layer.HotSpot is { } h
            ? $"{h.Value.ToString("0.0", CultureInfo.InvariantCulture)} °C, " +
              (h.Surface < parts.Objects.Count ? parts.Objects[h.Surface] : WireSpot(parts.Wires[h.Surface - parts.Objects.Count], h.At))
            : "";
        string step = StepLabelOf(layer);
        SetText(layer, note ?? $"Temperature: {all.Sum(x => x.TriangleCount):N0} triangles" + (step.Length > 0 ? $", {step}" : "") + ".");
        LayersChanged();
    }

    /// <summary>
    /// Gate 7 — a sweep step on the same run: the new step's temperature alone, read into the triangles already drawn through
    /// their recipes. False when there is nothing to revalue (the geometry is then built as usual): no parts, parts built for
    /// other targets (another plot's faces, All Faces, clip plane or fixed range), or a newer build scheduled and not yet
    /// adopted — revaluing the old parts then would cancel that build and draw what it was replacing.
    /// </summary>
    private bool TryRevalueTemperature(FieldLayer layer, FieldSolutionItem item)
    {
        if (layer.Temperature is not { } parts || layer.Volume is not { } vol || !ReferenceEquals(item.Run, layer.Run) ||
            item.Solution.VolumePvtu is not { } pvtu || layer.Quantity is not { IsTemperature: true } q ||
            parts.Surfaces.Any(s => s.Recipe is null))
            return false;
        if (parts.GeometryVersion != layer.GeometryVersion || parts.Targets is not { } built || !built.Same(CurrentTemperatureTargets(layer)) ||
            !parts.Faces.SequenceEqual(layer.TempFaces))
            return false;
        var table = layer.Read?.Table;
        int step = item.Solution.Index;
        var sol = item.Solution;
        bool fix = layer.FixRange;
        var fixedScale = layer.OwnScale;
        var scene = Scene;
        var cts = layer.NewBuild();
        Task.Run(() =>
        {
            try
            {
                var next = vol.WithArraysOf(pvtu);
                var array = next.Load(q.Array.Name) ?? throw new FieldReadException($"'{pvtu}' holds no {q.Array.Name}.");
                var surfaces = parts.Surfaces.Select(s => s.Revalue(array)!).ToList();
                var wires = table is null ? parts.Wires : [.. parts.Wires.Select(w => Table(table, w.Wire, step) is { } t ? FieldWires.Revalue(w, t) : w)];
                var scale = fix && fixedScale is not null ? fixedScale : Range(q, surfaces, wires, table, step) ?? FieldColorScale.MinMax(q, []);
                cts.Token.ThrowIfCancellationRequested();
                _post(() =>
                {
                    if (cts.IsCancellationRequested || _disposed || layer.Disposed || !ReferenceEquals(Scene.Geometry, scene.Geometry)) return;
                    layer.Volume = next;
                    layer.Loaded = sol;
                    FieldRevalues++;
                    layer.Revalues++;
                    AdoptTemperature(layer, parts with { Surfaces = surfaces, Wires = wires }, q, scale, layer.Covered.ToList(), null);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
            {
                _post(() => { if (!layer.Disposed) SetText(layer, "The temperature could not be read: " + e.Message); });
            }
        });
        return true;
    }

    // ── readouts ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d75-4c — T under the cursor: on a painted face or an All Faces solid, the volume's own shape functions at the
    /// point (FieldSampler — interpolated, never the nearest node); on a painted wire, T and s where the cursor's triangle is.
    /// "" off everything painted. With how far along the cursor's ray <paramref name="o"/> + t·<paramref name="d"/> it lies.
    /// </summary>
    private (string Text, float T) TemperatureUnderCursor(FieldLayer layer, uint id, Vector3 point, bool hit, FieldQuantity q, FieldRun run,
                                                          Vector3 o, Vector3 d, float hitT)
    {
        var ch = new double[2];
        double toUnits = 1 / run.ToMetres;
        bool Sample(Vector3 local)
        {
            if (layer.VolumeSampler is null || layer.Volume?.Load(q.Array.Name) is not { } a) return false;
            var (x, y, z) = Scene.ToWorld(local);
            return layer.VolumeSampler.Sample(a, x * toUnits, y * toUnits, z * toUnits, ch);
        }
        static string T(double v) => $"T = {v.ToString("0.00", CultureInfo.InvariantCulture)} °C";
        var plane = PlaneOf(layer);
        if (layer.TempOnClip && plane.Enabled)
        {
            var e = plane.Equation;
            var n = new Vector3(e.X, e.Y, e.Z);
            float den = Vector3.Dot(n, d);
            if (Math.Abs(den) > 1e-12f)
            {
                float t = -(Vector3.Dot(n, o) + e.W) / den;
                if (t > 0 && t <= hitT * 1.0001f && Sample(o + t * d)) return (T(ch[0]) + " (clip plane)", t);
            }
        }
        // brief-em3d-76 R-em3d76-4c — a mirrored half is not in the scene: the cursor's ray against its triangles, and the
        // modelled point's value read at the reflection of the hit
        if (layer.Temperature is { Reflections.Count: > 0 } parts && MirroredHit(parts) is { } mh &&
            (!hit || Vector3.Dot(point - mh.Origin, mh.Dir) > mh.T))
        {
            var p = mh.Origin + mh.T * mh.Dir;
            foreach (var (axis, at) in parts.Reflections[mh.Surface].Reverse())
                p = axis switch { 0 => p with { X = (float)(2 * at - p.X) }, 1 => p with { Y = (float)(2 * at - p.Y) }, _ => p with { Z = (float)(2 * at - p.Z) } };
            return Sample(p) ? (T(ch[0]) + " (the mirrored half: the modelled point's value)", mh.T) : ("", 0);
        }
        if (!hit || Scene.Object(id) is not { } obj) return ("", 0);
        if (layer.Temperature?.Wires.FirstOrDefault(w => w.Wire == obj.Name) is { } wire && WireAt(wire, point) is { } ws)
            return ($"{wire.Wire}, s = {FormatLength(ws.S)}: {T(ws.T)}", hitT);
        // The face the ID pass named, in any select mode (HoveredFace is Face mode's alone).
        bool painted = layer.Covered.Contains(obj.Name) || (LastPick.Object == id && layer.TempFaces.Contains(new TemperatureFace(obj.Name, LastPick.Face)));
        if (!painted) return ("", 0);
        if (Sample(point)) return (T(ch[0]), hitT);
        // an exterior face has nothing behind it: a hit rounded a hair outside lies in no tetrahedron, so read again a hair
        // along the ray — into the solid the eye is looking at (the EM face path reads a hair off its face the same way)
        float hair = (float)(1e-6 * (Scene.BoundsMax - Scene.BoundsMin).Length());
        return Sample(point + hair * d) ? (T(ch[0]), hitT) : ("", 0);
    }

    /// <summary>The nearest hit of the cursor's ray on a mirrored surface: the surface, the ray and its parameter; null when the
    /// ray meets none.</summary>
    private (int Surface, Vector3 Origin, Vector3 Dir, float T)? MirroredHit(TemperatureParts parts)
    {
        var (o, d) = View.Camera.Ray(View.CursorX, View.CursorY, _viewW, _viewH);
        (int, Vector3, Vector3, float)? best = null;
        float bt = float.MaxValue;
        for (int si = 0; si < parts.Surfaces.Count && si < parts.Reflections.Count; si++)
        {
            if (parts.Reflections[si].Length == 0) continue;
            var s = parts.Surfaces[si];
            for (int t = 0; t < s.TriangleCount; t++)
            {
                Vector3 V(int v) => new((float)s.Xyz[3 * v], (float)s.Xyz[3 * v + 1], (float)s.Xyz[3 * v + 2]);
                if (RayTriangle(o, d, V(3 * t), V(3 * t + 1), V(3 * t + 2)) is { } h && h < bt) { bt = h; best = (si, o, d, h); }
            }
        }
        return best;
    }

    /// <summary>Möller–Trumbore: the ray parameter of the hit, or null.</summary>
    private static float? RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (Math.Abs(det) < 1e-20f) return null;
        float inv = 1 / det;
        var tv = o - a;
        float u = Vector3.Dot(tv, p) * inv;
        if (u < 0 || u > 1) return null;
        var qv = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, qv) * inv;
        if (v < 0 || u + v > 1) return null;
        float t = Vector3.Dot(e2, qv) * inv;
        return t > 0 ? t : null;
    }

    /// <summary>R-em3d75-5 — a wire's hot spot names the wire AND where along it: <c>w3 at s = 412 µm</c>.</summary>
    private string WireSpot(FieldWireSurface w, Vector3 at) => WireAt(w, at) is { } ws ? $"{w.Wire} at s = {FormatLength(ws.S)}" : w.Wire;

    /// <summary>T and s at <paramref name="p"/> on a coloured wire: the triangle nearest the point, barycentric.</summary>
    private static (double T, double S)? WireAt(FieldWireSurface w, Vector3 p)
    {
        var s = w.Surface;
        double best = double.MaxValue;
        (double, double)? found = null;
        for (int t = 0; t < s.TriangleCount; t++)
        {
            var a = V(3 * t); var b = V(3 * t + 1); var c = V(3 * t + 2);
            var pd = new Vector3D(p.X, p.Y, p.Z);
            double d = FieldFaces.DistanceToTriangle(pd, a, b, c);
            if (d >= best) continue;
            best = d;
            // Barycentric of p's projection on the triangle's plane (clamped to it).
            double[] l = Bary(pd, a, b, c);
            int i = 3 * t;
            found = (l[0] * s.Values[i] + l[1] * s.Values[i + 1] + l[2] * s.Values[i + 2],
                     l[0] * w.S[i] + l[1] * w.S[i + 1] + l[2] * w.S[i + 2]);
        }
        return found;
        Vector3D V(int v) => new(s.Xyz[3 * v], s.Xyz[3 * v + 1], s.Xyz[3 * v + 2]);
    }

    private static double[] Bary(Vector3D p, Vector3D a, Vector3D b, Vector3D c)
    {
        double v0x = b.X - a.X, v0y = b.Y - a.Y, v0z = b.Z - a.Z, v1x = c.X - a.X, v1y = c.Y - a.Y, v1z = c.Z - a.Z;
        double v2x = p.X - a.X, v2y = p.Y - a.Y, v2z = p.Z - a.Z;
        double d00 = v0x * v0x + v0y * v0y + v0z * v0z, d01 = v0x * v1x + v0y * v1y + v0z * v1z, d11 = v1x * v1x + v1y * v1y + v1z * v1z;
        double d20 = v2x * v0x + v2y * v0y + v2z * v0z, d21 = v2x * v1x + v2y * v1y + v2z * v1z;
        double den = d00 * d11 - d01 * d01;
        if (!(Math.Abs(den) > 0)) return [1, 0, 0];
        double v = Math.Clamp((d11 * d20 - d01 * d21) / den, 0, 1), w = Math.Clamp((d00 * d21 - d01 * d20) / den, 0, 1);
        if (v + w > 1) { double k = v + w; v /= k; w /= k; }
        return [1 - v - w, v, w];
    }

    /// <summary>
    /// R-em3d75-4d — T along the segment from <paramref name="from"/> to <paramref name="to"/> (world metres) on the step shown,
    /// exactly at each sample (FieldSampler), or null with the reason when there is no thermal field to read.
    /// </summary>
    public FieldLine? TemperatureAlong((double X, double Y, double Z) from, (double X, double Y, double Z) to, int samples, out string? why)
    {
        why = null;
        var layer = Focused;
        if (!IsThermalRun || layer.Run is not { } run || layer.Volume is not { } vol)
        {
            why = "There is no thermal result to read: run the active thermal setup first.";
            return null;
        }
        if (vol.Load(FieldNames.TemperatureArray) is not { } a) { why = "The thermal result holds no temperature."; return null; }
        var sampler = layer.VolumeSampler ?? new FieldSampler(vol.Mesh);
        layer.VolumeSampler ??= sampler;
        var q = new FieldQuantity(a.Info, false, FieldMode.Value);
        return FieldLine.Along(sampler, a, q, run.ToMetres, from, to, samples);
    }

    /// <summary>Opens the thermal run's fields for reading without drawing them (Temperature Along, a line plot).</summary>
    public void EnsureTemperatureLoaded()
    {
        if (!IsThermalRun) return;
        if (SelectedFieldSolution is null && FieldSolutions.Count > 0) SelectedFieldSolution = FieldSolutions[0];
        if (Focused.Volume is null)
        {
            Focused.Item ??= SelectedFieldSolution;
            EnsureFieldLoaded(Focused);
        }
    }
}
