// brief-em3d-50 — bond wires in the 3D editor: the Wire tool's services, its toolbar values, Re-seat Wire Ends, the tree's
// flag on a wire that lands on no pad, and the axis drawn for Vertex mode.
//
// ONE LOOKUP. The tool asks "is this the top of a pad" through C3dWires.PadAt over the editor's current elaboration —
// the function elaboration itself answers with — so an end the tool accepts is an end the solver accepts.
//
// A WIRE IS NOT DRAGGED ALONG (R-em3d50-3c). When what a wire was bonded to moves, the wire keeps its points and its
// elaboration refuses it by name and end; the tree flags it and its axis is drawn in red. Re-Seat Wire Ends is the
// explicit fix for the vertical case: each end moved in z onto the top now under it, where there is one. Moving a
// wire's point in Vertex mode re-seats its feet on release, and an END moved to where there is no pad is refused.
//
// DEFAULTS (R-em3d50-3b): the last wire drawn, then the workspace's assembly rules (a .wasm's first allowed diameter),
// then built in (1 mil, Gold where the technology has it, a hexagonal section, wedge–wedge).

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.WBond;
using CommunityToolkit.Mvvm.ComponentModel;
using Point3 = CircuitRF.Engine.Em3d.Point3;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel : IC3dWireHost
{
    public static IReadOnlyList<BondStyle> BondStyles { get; } = [BondStyle.Wedge, BondStyle.Ball];
    public static IReadOnlyList<WireCrossSection> WireSections { get; } = [WireCrossSection.Hexagon, WireCrossSection.Round];

    public bool IsWireArmed { get => ArmedTool == C3dToolKind.Wire; set => ArmToggle(C3dToolKind.Wire, value); }

    // ── the toolbar (R-em3d50-3b) ────────────────────────────────────────────────────────────

    /// <summary>The next wire's diameter, in the display unit (a suffix is honoured).</summary>
    [ObservableProperty] private string _wireDiameterText = "";
    [ObservableProperty] private string? _wireDiameterError;
    [ObservableProperty] private string? _wireMaterial;
    [ObservableProperty] private BondStyle _wireStartStyle = BondStyle.Wedge;
    [ObservableProperty] private BondStyle _wireEndStyle = BondStyle.Wedge;
    [ObservableProperty] private WireCrossSection _wireSection = WireCrossSection.Hexagon;

    private double _wireDiameterUm = C3dWires.DefaultDiameterUm;
    private long? _lastLoopHeightDbu;
    private bool _wireDefaultsTaken;

    /// <summary>The technology's metals (a material with a conductivity) — what a wire may be made of.</summary>
    public IReadOnlyList<string> WireMetals
        => Elaboration?.Technology?.ResolvedMaterials.Where(m => m.Sigma20 is not null).Select(m => m.Name).ToList() ?? (IReadOnlyList<string>)[];

    partial void OnWireDiameterTextChanged(string value)
    {
        var d = C3dDimension.Parse(value, Document.DisplayUnit, Document.DbuPerMicron);
        if (d.Kind != C3dDimensionKind.Value || d.Dbu <= 0) { WireDiameterError = d.Why ?? "A diameter is a positive length."; return; }
        WireDiameterError = null;
        _wireDiameterUm = (double)LayoutUnits.FromDbu(d.Dbu, LayoutUnit.Um, Document.DbuPerMicron);
    }

    /// <summary>The first time the tool is armed: the assembly rules' first allowed diameter, the technology's gold (or its
    /// first metal). After a wire is drawn its values stay on the toolbar, which is "the last wire drawn".</summary>
    private void TakeWireDefaults()
    {
        OnPropertyChanged(nameof(WireMetals));
        if (!_wireDefaultsTaken)
        {
            _wireDefaultsTaken = true;
            var last = Document.Objects.OfType<C3dWire>().LastOrDefault();
            if (last is not null)
            {
                _wireDiameterUm = last.DiameterUm ?? C3dWires.DefaultDiameterUm;
                WireMaterial = C3dWires.MaterialOf(last);
                (WireStartStyle, WireEndStyle, WireSection) = (last.Start.Style, last.End.Style, C3dWires.SectionOf(last));
            }
            else if (WireWorkspace().WorkspaceRules().Rules?.AllowedDiametersNm is [var first, ..] && first > 0)
                _wireDiameterUm = first / 1000.0;
            WireDiameterText = Length((long)Math.Round(_wireDiameterUm * Document.DbuPerMicron, MidpointRounding.AwayFromZero));
        }
        var metals = WireMetals;
        if (WireMaterial is null || !metals.Contains(WireMaterial))
            WireMaterial = metals.FirstOrDefault(m => string.Equals(m, WireMaterials.Default.Name, StringComparison.OrdinalIgnoreCase))
                           ?? metals.FirstOrDefault() ?? WireMaterials.Default.Name;
    }

    public C3dWireTemplate WireTemplate
        => new(_wireDiameterUm, WireMaterial, WireSection, WireStartStyle, WireEndStyle, _lastLoopHeightDbu);

    private WireBondWorkspace WireWorkspace()
        => new(FilePath, WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(FilePath)) ?? _workspaceCws());

    private WireTool WireToolArmed()
    {
        TakeWireDefaults();
        return new WireTool(this, this);
    }

    /// <summary>After the tool drew a wire: its loop height is the next one's first guess.</summary>
    private void RememberWire(WireTool tool)
    {
        if (tool.LastAssembly is { } h) _lastLoopHeightDbu = h;
    }

    // ── IC3dWireHost ─────────────────────────────────────────────────────────────────────────

    private (C3dElaboration Of, List<C3dWirePad> Pads)? _pads;

    /// <summary>The current elaboration's conductor tops (world metres), kept until the next elaboration.</summary>
    private List<C3dWirePad> WirePads()
    {
        if (Elaboration is not { } e) return [];
        if (_pads is { } p && ReferenceEquals(p.Of, e)) return p.Pads;
        var pads = C3dWires.Pads(e.Solids, e.Sheets);
        _pads = (e, pads);
        return pads;
    }

    private double PerDbu => C3dLowering.Metres(1, Document.DbuPerMicron);

    public (string Pad, long TopDbu)? PadAt(C3dPoint3 p)
    {
        var at = new Point3(C3dLowering.Metres(p.X, Document.DbuPerMicron), C3dLowering.Metres(p.Y, Document.DbuPerMicron),
                            C3dLowering.Metres(p.Z, Document.DbuPerMicron));
        return C3dWires.PadAt(WirePads(), at, PerDbu) is { } pad
            ? (pad.Name, (long)Math.Round(pad.TopM / PerDbu, MidpointRounding.AwayFromZero))
            : null;
    }

    public (string Pad, C3dPoint3 At)? PadUnderRay(Point3 origin, Point3 direction)
    {
        if (C3dWires.PadHit(WirePads(), origin, direction) is not { } hit) return null;
        long R(double m) => (long)Math.Round(m / PerDbu, MidpointRounding.AwayFromZero);
        return (hit.Pad.Name, new C3dPoint3(R(hit.At.X), R(hit.At.Y), R(hit.Pad.TopM)));
    }

    /// <summary>
    /// 3D editor bugs round 2 — a wire starts and ends on the OBJECT under the cursor, any face of it: a metal solid or a
    /// sheet. The end lands on that object's top above the point (C3dWires.LandOn), since a bond sits on a top — the one
    /// place elaboration looks for it. <paramref name="precise"/> (a click) picks on the CPU from the scene itself;
    /// otherwise (the rubber band, every cursor move) the last ID pass's object and point are used. Null with the reason
    /// in plain words when there is no object, or it is not something a wire can bond to.
    /// </summary>
    public (string Pad, C3dPoint3 At)? PadOnObject(bool precise, out string? refusal)
    {
        refusal = null;
        var v = Viewer;
        var (id, world) = precise ? v.PickUnderCursor() : (v.LastPick.Object, v.CursorWorld);
        if (id == 0 && precise && v.LastPick.Object != 0) id = v.LastPick.Object;   // a wireframe has no triangles to hit
        if (v.Scene.Object(id) is not { } o)
        {
            refusal = "Click a metal object or a sheet — any face — to attach the wire.";
            return null;
        }
        var pads = WirePads().Where(q => q.Name == o.Name).ToList();
        if (pads.Count == 0)
        {
            refusal = WhyNoBond(o);
            return null;
        }
        if (world is not { } w) return null;
        double inset = (WireTemplate.DiameterUm > 0 ? WireTemplate.DiameterUm : C3dWires.DefaultDiameterUm) * 2e-6;
        if (C3dWires.LandOn(pads, w.X, w.Y, inset, PerDbu) is not { } land) return null;
        long R(double m) => (long)Math.Round(m / PerDbu, MidpointRounding.AwayFromZero);
        return (land.Pad.Name, new C3dPoint3(R(land.X), R(land.Y), R(land.Pad.TopM)));
    }

    /// <summary>Why a wire cannot attach to <paramref name="o"/>, in the words of the object and its material.</summary>
    private static string WhyNoBond(Scene3DObject o)
    {
        if (o.Wireframe || o.Material is not { Length: > 0 })
            return $"'{o.Name}' has no material. Give it a metal (Assign Material…) and a wire can attach to it.";
        return o.Kind switch
        {
            Scene3DKind.Dielectric or Scene3DKind.Air => $"'{o.Name}' is {o.Material}, an insulator: a wire attaches to metal — a conductor or a sheet.",
            Scene3DKind.Port => "A wire attaches to metal, not to a port.",
            Scene3DKind.Wire => $"'{o.Name}' is a wire: start the new one on the metal it lands on.",
            Scene3DKind.Sheet => $"'{o.Name}' is an upright sheet: a wire attaches to a level top.",
            _ => $"'{o.Name}' has no level top for a wire to attach to.",
        };
    }

    public double? MeasureAssembly(C3dWire candidate)
    {
        var r = C3dWires.Resolve(candidate, candidate.Name, C3dTransform.Identity, Document.DbuPerMicron, WirePads(), PerDbu,
                                 WireWorkspace(), LayoutUnits.AsciiSuffix(Document.DisplayUnit));
        return r.Resolution?.Report is { } report ? report.AssemblyLoopHeightM / PerDbu : null;
    }

    // ── Re-seat Wire Ends (R-em3d50-3c) ──────────────────────────────────────────────────────

    /// <summary>The selected objects that are wires, by document index — and the wire selected in the tree, because a
    /// wire that lands on no pad has no solid to pick in the view, and that is the wire Re-Seat is for.</summary>
    private List<int> SelectedWires()
    {
        var list = Targets().Where(t => !t.Instance && !IsOperandIndex(t.Index) && Document.Objects[t.Index] is C3dWire).Select(t => t.Index).ToList();
        if (SelectedTreeItem is { ObjectIndex: >= 0 and var i } && i < Document.Objects.Count && Document.Objects[i] is C3dWire && !list.Contains(i))
            list.Add(i);
        return list;
    }

    /// <summary>Each selected wire's ends moved in z onto the top now under them — one undo entry. An end with nothing under
    /// it is left where it is and named.</summary>
    public void ReseatWireEnds()
    {
        var wires = SelectedWires();
        if (wires.Count == 0) { StatusMessage = "Re-Seat Wire Ends acts on selected wires."; return; }
        var pads = WirePads();
        var slots = new List<C3dEditSlot>();
        var missing = new List<string>();
        foreach (int i in wires)
        {
            var w = (C3dWire)Document.Objects[i];
            var seated = C3dWires.Reseat(w, pads, Document.DbuPerMicron, out var unseated);
            missing.AddRange(unseated.Select(e => $"{w.Name}'s {e}"));
            string before = C3dPersistence.SerializeObject(w), after = C3dPersistence.SerializeObject(seated);
            if (after != before) slots.Add(new C3dEditSlot(false, i, before, after));
        }
        if (slots.Count > 0) Push(new C3dEdit($"Re-seat wire ends: {string.Join(", ", slots.Select(s => Document.Objects[s.Index].Name))}", slots, ApplySlots));
        string none = missing.Count == 0 ? "" : $" Nothing conductive is under {string.Join(", ", missing)}: move {(missing.Count == 1 ? "it" : "them")} onto a pad.";
        StatusMessage = (slots.Count > 0 ? $"Re-seated {slots.Count} wire(s)." : "Every end is already on the top under it.") + none;
    }

    /// <summary>
    /// A Vertex-mode edit of a wire, before it is committed: the feet re-seated on the tops under them. An END that moved
    /// to where nothing is under it is refused (the refusal's sentence), and so is one whose pad cannot be found at all.
    /// </summary>
    private string? SeatEditedWire(C3dWire before, ref C3dWire after)
    {
        var pads = WirePads();
        var seated = C3dWires.Reseat(after, pads, Document.DbuPerMicron, out var unseated);
        foreach (string end in unseated)
        {
            int i = end == "start" ? 0 : after.Points.Count - 1;
            int j = end == "start" ? 0 : before.Points.Count - 1;
            if (i < 0 || j < 0 || after.Points[i] == before.Points[j]) continue;
            var q = after.Points[i];
            return $"{after.Name}'s {end} would be over no pad at ({Length(q.X)}, {Length(q.Y)}): a wire's end is bonded to the top of a pad.";
        }
        after = seated;
        return null;
    }

    /// <summary>
    /// 3D editor round 3 — Properties' typed wire point: point <paramref name="k"/> of the wire at <paramref name="index"/>
    /// moved to <paramref name="world"/> (a wire's points ARE world points), the feet re-seated exactly as a Vertex-mode
    /// drag's are, one undo entry. An end moved off every pad is refused; an end's z is its pad's top, so a typed z on an
    /// end that the seat puts back is said rather than silently ignored. Null on success, else why not.
    /// </summary>
    public string? SetWirePoint(int index, int k, C3dPoint3 world)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dWire was) return "Select one wire.";
        if (k < 0 || k >= was.Points.Count) return $"{was.Name} has no point {k + 1}.";
        if (was.Points[k] == world) return null;
        string before = C3dPersistence.SerializeObject(was);
        var moved = (C3dWire)C3dPersistence.DeserializeObject(before);
        moved.Points[k] = world;
        if (SeatEditedWire(was, ref moved) is { } refusal) return refusal;
        bool end = k == 0 || k == was.Points.Count - 1;
        string? zNote = end && moved.Points[k].Z != world.Z
            ? $"{was.Name}'s {(k == 0 ? "start" : "end")} is bonded to the top of its pad, at z = {Length(moved.Points[k].Z)}: an end's z follows its pad."
            : null;
        string after = C3dPersistence.SerializeObject(moved);
        if (after == before) return zNote;
        if (!Push(new C3dEdit($"Set point {k + 1} of {was.Name}", [new C3dEditSlot(false, index, before, after)], ApplySlots)))
            return StatusMessage;
        StatusMessage = zNote ?? $"Moved point {k + 1} of '{was.Name}'.";
        return null;
    }

    /// <summary>
    /// Properties' "add a point" on a wire's row: a new point between point <paramref name="a"/> and the next, at the
    /// middle of the cubic (Catmull–Rom) through the points around them, so the wire stays smooth rather than gaining a
    /// corner. One undo entry; the ends do not move. Null on success, else why not.
    /// </summary>
    public string? InsertWirePoint(int index, int a)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dWire was) return "Select one wire.";
        if (a < 0 || a + 1 >= was.Points.Count) return $"{was.Name} has no segment after point {a + 1}.";
        string before = C3dPersistence.SerializeObject(was);
        var grown = (C3dWire)C3dPersistence.DeserializeObject(before);
        if (!C3dPointExpressions.RenumberPointExpressions(grown, nameof(C3dWire.Points), from: a + 1, removed: null)) return PointExpressionRefusal(was.Name);
        grown.Points.Insert(a + 1, CubicMidpoint(was.Points, a));
        if (!Push(new C3dEdit($"Add a point to {was.Name}", [new C3dEditSlot(false, index, before, C3dPersistence.SerializeObject(grown))], ApplySlots)))
            return StatusMessage;
        StatusMessage = $"Added point {a + 2} to '{was.Name}'.";
        return null;
    }

    /// <summary>Properties' "remove" on a wire's row: an interior point taken out, one undo entry. The two ends are bonded
    /// to their pads and are not removed. Null on success, else why not.</summary>
    public string? RemoveWirePoint(int index, int k)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dWire was) return "Select one wire.";
        if (k <= 0 || k >= was.Points.Count - 1) return $"{was.Name}'s ends are bonded to their pads: only a point between them is removed.";
        string before = C3dPersistence.SerializeObject(was);
        var shrunk = (C3dWire)C3dPersistence.DeserializeObject(before);
        if (!C3dPointExpressions.RenumberPointExpressions(shrunk, nameof(C3dWire.Points), from: k + 1, removed: k)) return PointExpressionRefusal(was.Name);
        shrunk.Points.RemoveAt(k);
        if (!Push(new C3dEdit($"Remove point {k + 1} of {was.Name}", [new C3dEditSlot(false, index, before, C3dPersistence.SerializeObject(shrunk))], ApplySlots)))
            return StatusMessage;
        StatusMessage = $"Removed point {k + 1} of '{was.Name}'.";
        return null;
    }

    private static string PointExpressionRefusal(string wire)
        => $"{wire}'s points carry expressions this version cannot renumber: edit the points in the file.";

    /// <summary>The middle of segment <paramref name="a"/>→<paramref name="a"/>+1 on the uniform Catmull–Rom cubic through
    /// the points either side: (−p₀ + 9p₁ + 9p₂ − p₃) / 16. A missing neighbour past an end is that end's segment carried
    /// straight on, so a two-point wire gains its plain midpoint.</summary>
    internal static C3dPoint3 CubicMidpoint(IReadOnlyList<C3dPoint3> p, int a)
    {
        var p1 = p[a];
        var p2 = p[a + 1];
        var p0 = a > 0 ? p[a - 1] : new C3dPoint3(2 * p1.X - p2.X, 2 * p1.Y - p2.Y, 2 * p1.Z - p2.Z);
        var p3 = a + 2 < p.Count ? p[a + 2] : new C3dPoint3(2 * p2.X - p1.X, 2 * p2.Y - p1.Y, 2 * p2.Z - p1.Z);
        static long M(long q0, long q1, long q2, long q3) => (long)Math.Round((-q0 + 9.0 * q1 + 9.0 * q2 - q3) / 16, MidpointRounding.AwayFromZero);
        return new C3dPoint3(M(p0.X, p1.X, p2.X, p3.X), M(p0.Y, p1.Y, p2.Y, p3.Y), M(p0.Z, p1.Z, p2.Z, p3.Z));
    }

    private IEnumerable<Viewer3DMenuItem> WireMenuItems()
    {
        if (SelectedWires().Count == 0) yield break;
        yield return new Viewer3DMenuItem("Re-Seat Wire Ends", ReseatWireEnds,
            Tip: "Move each end up or down onto the top of the pad now under it. A pad that moved sideways is not followed.");
    }

    // ── the tree and the overlay ─────────────────────────────────────────────────────────────

    private void RefreshWireFlags()
    {
        var refusals = Elaboration?.WireRefusals;
        foreach (var item in Tree.Where(g => g.Role is C3dTreeGroupRole.Objects or C3dTreeGroupRole.NotModelled).SelectMany(g => g.Items)
                                 .Where(i => i.Kind == C3dObject.KindOf(typeof(C3dWire))))
        {
            // 3D editor round 4 — a wire array's row carries the first refusal among its elements (w1[2]).
            var names = item.ObjectIndex >= 0 && item.ObjectIndex < Document.Objects.Count && Document.Objects[item.ObjectIndex] is C3dWire w
                ? C3dWires.ElementNames(w) : [item.Name];
            item.Refusal = refusals is null ? null : names.Select(n => refusals.TryGetValue(n, out var why) ? why : null).FirstOrDefault(x => x is not null);
        }
    }

    /// <summary>A refused wire's axis in red (it has no solid to draw), and in Vertex mode every wire's axis and points —
    /// what Vertex mode edits on a wire.</summary>
    private void FillWireOverlay(Viewer3DDrawOverlay overlay)
    {
        int dbu = Document.DbuPerMicron;
        bool vertex = Viewer.SelectMode == Scene3DSelectMode.Vertex;
        var refused = Elaboration?.WireRefusals;
        foreach (var w in Document.Objects.OfType<C3dWire>())
        {
            if (w.Hidden || w.Points.Count < 2) continue;
            // 3D editor round 4 — every element of a wire array; Vertex mode edits the drawn one (element 0), so only its
            // points are marked.
            foreach (var (name, axis) in C3dWires.ElementAxes(w))
            {
                bool bad = refused?.ContainsKey(name) == true;
                if (bad) DrawGeometry.Chain(axis, false, dbu, overlay.Crossing);
                else if (vertex) DrawGeometry.Chain(axis, false, dbu, overlay.Construction);
            }
            if (vertex) foreach (var p in w.Points) overlay.Fixed.Add(DrawGeometry.Metres(p, dbu));
        }
    }
}
