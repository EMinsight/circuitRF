// brief-em3d-42 R-em3d42-3/-4 — a .c3d elaborated: its objects and every instance below it, as the neutral
// problem's solids, sheets and materials in metres, with a provenance map back to the document.
//
// THE ONE RULE OF THE SERIES (overview §0): what the editor shows is what the solver gets. The editor will
// draw THIS, and the problem assembly (C3dProblemAssembly) solves THIS plus a setup. Elaboration is
// independent of any setup — the physics it needs (the sheet rule's top frequency, σ(T)'s temperature)
// comes in through C3dElaborationOptions, and with none a conductor with thickness is a solid.
//
// INSTANCES (R-em3d42-3c). A 3D view elaborates recursively; a layout view goes through Em3dLayoutSolids
// with the INSTANCE options and the layout's OWN technology (resolved from the layout's own path, never
// the parent's). ExternalWorkspaceGate is not consulted: that gate exists because a layout hierarchy
// matches layers by number, and nothing here matches by layer — every instance becomes metres and named
// materials before its parent sees it (overview §1i). The bottom of a layout's stackup sits at the
// instance's z.
//
// NAMES. An object inside an instance is `<instance>/<object>`, nested for depth; an array element is
// `<instance>[i,j,k]/<object>` (brief 49's ports name sub-cell conductors this way). Objects come first in
// construction order, then instances in theirs, each instance's content keeping its own relative order.
//
// MATERIALS (R-em3d42-3e). Each object's material resolves in its own document's technology. Equal values
// under one name merge; different values are kept as `<name>@<technology>` with a note listing both —
// a silent merge would give one of them the wrong conductivity, and nothing would look wrong. The
// technology is named by its .ctech file's stem (its display name may hold spaces and commas).
//
// BOND WIRES (brief-em3d-50). A drawn Wire resolves AFTER its document's objects and instances, because its ends land
// on conductors anywhere below it — a die pad in `U1`, a lead in the parent (C3dWires: the pad lookup, then the same
// Em3dWires.Resolve a .wBond's wires use). A wire that lands on no pad is refused by name and end, and the rest of the
// document still elaborates, so the editor can draw it and flag it (WireRefusals).
//
// CACHING (R-em3d42-4), because the editor calls this on every edit. An elaborator instance keeps a
// per-OBJECT cache keyed by the object's serialized form, its world transform and its document's scale,
// and a per-CHILD cache keyed by (file, file stamp, view, technology stamp) — brief 28's rule: a file
// re-written at the same path is not the same file. ObjectsElaborated and ChildrenElaborated count
// misses, which is what gate 7 reads.

using System.Globalization;
using System.Text;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using WireMaterial = CircuitRF.WBond.WireMaterial;

namespace CircuitRF.Design.ThreeD;

/// <summary>The physics elaboration needs from a setup, or its defaults with none.</summary>
/// <param name="FMaxHz">The top frequency, for the sheet rule inside a layout instance; null makes every
/// conductor with thickness a solid.</param>
/// <param name="TempC">The temperature σ(T) is evaluated at.</param>
public sealed record C3dElaborationOptions(double? FMaxHz = null, double TempC = EmSetup.DefaultOperatingTempC);

/// <summary>Where one object of the elaborated problem came from (R-em3d42-3a).</summary>
/// <param name="InstancePath">The instance path, <c>U1/U3</c> or <c>U1[0,1,0]</c>; empty for the document's own objects.</param>
/// <param name="DocumentPath">The file the object is in — the <c>.c3d</c>, or the placed <c>.clay</c>.</param>
/// <param name="ObjectName">Its name in that file (a layout's solid name for a layout instance).</param>
/// <param name="FaceNames">Its faces' names, indexed by the neutral primitive's face numbering.</param>
public sealed record C3dProvenance(string InstancePath, string DocumentPath, string ObjectName, IReadOnlyList<string> FaceNames)
{
    /// <summary>
    /// brief-em3d-44 R-em3d44-4 — whether an integer point of the object's own document lands on an integer DBU
    /// point of the TOP document: every placement from it to the top is integral (translations and quarter
    /// turns) and every document on the way has the top's DBU per µm. A snap to such an object's corner is an
    /// exact DBU point; any other is metres, rounded only when used.
    /// </summary>
    public bool Exact { get; init; } = true;

    /// <summary>brief-em3d-44 R-em3d44-3b — for an object inside an instance, the element's transform (its
    /// document's metres to world metres); null for the document's own objects. Two objects of one child
    /// under transforms with the same rotation are the same mesh moved by the difference of translations.</summary>
    public C3dTransform? Element { get; init; }
}

/// <summary>One step of the walk <c>explain</c> reports (R-em3d42-6): what, and how it was decided.</summary>
public sealed record C3dWalkStep(string Subject, string Detail);

/// <summary>A <c>.c3d</c>, elaborated. The geometry is in construction order, metres.</summary>
public sealed record C3dElaboration(
    IReadOnlyList<Em3dSolid>                      Solids,
    IReadOnlyList<Em3dSheet>                      Sheets,
    IReadOnlyList<Em3dMaterial>                   Materials,
    IReadOnlyDictionary<string, C3dProvenance>    Provenance,
    IReadOnlyList<string>                         Notes,
    IReadOnlyList<string>                         Refusals)
{
    public bool Ok => Refusals.Count == 0;

    /// <summary>Warnings a layout instance's own build raised (a foot overhanging its pad…).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Each conductor's nets, by object name: a layout instance's nets, and a drawn conductor's own
    /// name (a terminal names a drawn conductor by its name).</summary>
    public IReadOnlyDictionary<string, HashSet<string>> ObjectNets { get; init; } = new Dictionary<string, HashSet<string>>();

    /// <summary>The conductors on a layout instance's ground-reference stackup entries.</summary>
    public IReadOnlyList<string> GroundBandObjects { get; init; } = [];

    /// <summary>Where each material's values resolved from, by final name.</summary>
    public IReadOnlyDictionary<string, string> MaterialSources { get; init; } = new Dictionary<string, string>();

    /// <summary>What each object is, by name — a layout instance's origins, prefixed; a drawn object's by its role.</summary>
    public IReadOnlyDictionary<string, Em3dObjectOrigin> Origins { get; init; } = new Dictionary<string, Em3dObjectOrigin>();

    /// <summary>A layout instance's bond-wire reports, renamed into the parent, and every drawn wire's (brief-em3d-50).</summary>
    public IReadOnlyList<Em3dWireReport> Wires { get; init; } = [];

    /// <summary>brief-em3d-50 — each drawn wire that did not resolve, by its elaborated name: the refusal, which is also in
    /// <see cref="Refusals"/>. What the editor flags in its tree and draws in red.</summary>
    public IReadOnlyDictionary<string, string> WireRefusals { get; init; } = new Dictionary<string, string>();

    /// <summary>brief-em3d-50 — each drawn wire's result, by elaborated name: its pads and process values, for
    /// <c>explain</c> and the Wire tool's readout.</summary>
    public IReadOnlyDictionary<string, C3dWireResult> DrawnWires { get; init; } = new Dictionary<string, C3dWireResult>();

    /// <summary>brief-em3d-48 R-em3d48-6b — the instances whose cell or view resolved to nothing or could not be read, by
    /// instance path: what the editor draws as a dashed box with the cell's name.</summary>
    public IReadOnlyList<(string InstancePath, string CellRef)> Unresolved { get; init; } = [];

    /// <summary>The walk: instances resolved, units converted, materials merged, objects lowered.</summary>
    public IReadOnlyList<C3dWalkStep> WalkInstances { get; init; } = [];
    public IReadOnlyList<C3dWalkStep> WalkUnits { get; init; } = [];
    public IReadOnlyList<C3dWalkStep> WalkMaterials { get; init; } = [];
    public IReadOnlyList<C3dWalkStep> WalkLowering { get; init; } = [];

    /// <summary>The document's own technology, and where it resolved.</summary>
    public Technology? Technology { get; init; }
    public string? TechnologyPath { get; init; }

    /// <summary>The elaborated content's bound, metres; null when there is nothing in it.</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1)? Extent()
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        void Grow((double, double, double, double, double, double) b)
        {
            x0 = Math.Min(x0, b.Item1); y0 = Math.Min(y0, b.Item2); z0 = Math.Min(z0, b.Item3);
            x1 = Math.Max(x1, b.Item4); y1 = Math.Max(y1, b.Item5); z1 = Math.Max(z1, b.Item6);
        }
        foreach (var s in Solids) Grow(Em3dProblem.Bounds(s.Primitive));
        foreach (var s in Sheets) Grow(s.WorldBounds());
        return double.IsInfinity(x0) ? null : (x0, y0, z0, x1, y1, z1);
    }
}

/// <summary>
/// Elaborates <c>.c3d</c> documents. Keep one per open document: its caches are what make re-elaboration
/// after one edit cost one object (R-em3d42-4).
/// </summary>
public sealed class C3dElaborator(TechnologyCache? technologies = null)
{
    private readonly TechnologyCache _tech = technologies ?? new TechnologyCache();
    private readonly Dictionary<string, C3dLowered?> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _children = new(StringComparer.Ordinal);

    // brief-em3d-48 R-em3d48-3a — a layout instance's solids LOWERED under each element's transform, kept from one
    // elaboration to the next: an unchanged element hands back the very primitives it did last time, so the scene's
    // tessellation cache (keyed by primitive) hits and an array of a layout child is tessellated once, even when a column
    // is added. What one elaboration did not use is dropped at its end, as the tessellation cache drops its own.
    private Dictionary<(object Child, string Transform), (C3dLowered[] Solids, C3dSheetGeometry[] Sheets)> _layoutKept = [];
    private Dictionary<(object Child, string Transform), (C3dLowered[] Solids, C3dSheetGeometry[] Sheets)> _layoutUsed = [];

    /// <summary>Objects lowered because the per-object cache missed.</summary>
    public long ObjectsElaborated { get; private set; }

    /// <summary>Child views read and built because the per-child cache missed.</summary>
    public long ChildrenElaborated { get; private set; }

    /// <summary>One elaboration with no cache kept.</summary>
    public static C3dElaboration ElaborateOnce(C3dDocument document, string path, string? workspaceCws,
                                               C3dElaborationOptions? options = null)
        => new C3dElaborator().Elaborate(document, path, workspaceCws, options);

    /// <summary>
    /// <paramref name="document"/>, at <paramref name="path"/>, elaborated. <paramref name="workspaceCws"/> is
    /// the technology walk's fallback when the path has no ancestor workspace.
    /// </summary>
    public C3dElaboration Elaborate(C3dDocument document, string path, string? workspaceCws, C3dElaborationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        var run = new Run(this, options ?? new C3dElaborationOptions(), workspaceCws);
        var result = run.Go(document, Path.GetFullPath(path));
        (_layoutKept, _layoutUsed) = (_layoutUsed, _layoutKept);
        _layoutUsed.Clear();
        return result;
    }

    // ── caches ────────────────────────────────────────────────────────────────────────────────────

    private C3dLowered? LowerCached(C3dObject obj, C3dTransform world, int dbuPerMicron)
    {
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{dbuPerMicron}|{world.M00:R},{world.M01:R},{world.M02:R},{world.M10:R},{world.M11:R},{world.M12:R},{world.M20:R},{world.M21:R},{world.M22:R},{world.Tx:R},{world.Ty:R},{world.Tz:R}|") +
            C3dPersistence.SerializeObject(obj);
        if (_objects.TryGetValue(key, out var hit)) return hit;
        ObjectsElaborated++;
        return _objects[key] = C3dLowering.Lower(obj, world, dbuPerMicron);
    }

    private static string Stamp(string? path)
    {
        if (path is null || !File.Exists(path)) return "-";
        var info = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{info.LastWriteTimeUtc.Ticks}:{info.Length}");
    }

    /// <summary>A child 3D view: its document and technology.</summary>
    private sealed record Child3D(C3dDocument Document, TechResolution Tech, string? Refusal);

    /// <summary>A child layout view: its solids through its own technology.</summary>
    private sealed record ChildLayout(Em3dLayoutSolidsResult? Solids, TechResolution Tech, string TechName, string? Refusal,
                                      int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron);

    private Child3D Child3DCached(string path, string? fallbackCws)
    {
        // The technology stamp needs the document read first; a document read is what the file stamp guards.
        string docKey = "3d|" + path + "|" + Stamp(path);
        if (_children.TryGetValue(docKey, out var hit) && hit is Child3D c)
        {
            if (Stamp(c.Tech.ResolvedPath) == TechStampOf(docKey)) return c;
        }
        ChildrenElaborated++;
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(path); }
        catch (Exception e)
        {
            var bad = new Child3D(new C3dDocument(), new TechResolution(null, null, TechResolutionSource.None, []), e.Message);
            _children[docKey] = bad;
            return bad;
        }
        var (tech, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, path, fallbackCws, _tech);
        var made = new Child3D(doc, tech, null);
        _children[docKey] = made;
        _children["techstamp|" + docKey] = Stamp(tech.ResolvedPath);
        return made;
    }

    private string TechStampOf(string docKey) => _children.TryGetValue("techstamp|" + docKey, out var s) ? (string)s : "-";

    private ChildLayout ChildLayoutCached(string path, string? fallbackCws, C3dElaborationOptions options)
    {
        string key = string.Create(CultureInfo.InvariantCulture, $"layout|{path}|{Stamp(path)}|{options.FMaxHz:R}|{options.TempC:R}");
        if (_children.TryGetValue(key, out var hit) && hit is ChildLayout c && Stamp(c.Tech.ResolvedPath) == TechStampOf(key))
            return c;
        ChildrenElaborated++;
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(path); }
        catch (Exception e)
        {
            var bad = new ChildLayout(null, new TechResolution(null, null, TechResolutionSource.None, []), "", e.Message);
            _children[key] = bad;
            return bad;
        }
        var (tech, _) = TechnologyResolver.ResolveForDocument(view.TechRef, path, fallbackCws, _tech);
        ChildLayout made;
        if (tech.Tech is not { } t)
            made = new ChildLayout(null, tech, "", "its technology does not resolve" +
                                   (tech.Diagnostics.Count > 0 ? ": " + string.Join(" ", tech.Diagnostics) : "."));
        else
        {
            var solids = Em3dLayoutSolids.From(new EmLayoutSource(path, view, t, view.DbuPerMicron), t, null,
                                               new Em3dLayoutSolidsOptions(options.FMaxHz, options.TempC, Instance: true));
            made = new ChildLayout(solids, tech, TechName(tech), solids.Refusal, view.DbuPerMicron);
        }
        _children[key] = made;
        _children["techstamp|" + key] = Stamp(tech.ResolvedPath);
        return made;
    }

    /// <summary>A layout child's solids and sheets under <paramref name="t"/>: kept from the last elaboration when the same
    /// child was placed under the same transform.</summary>
    private (C3dLowered[] Solids, C3dSheetGeometry[] Sheets) LayoutLowered(object child, Em3dLayoutSolidsResult solids, C3dTransform t,
                                                                         Func<Em3dSolid, C3dLowered> lower)
    {
        var key = (child, string.Create(CultureInfo.InvariantCulture,
            $"{t.M00:R},{t.M01:R},{t.M02:R},{t.M10:R},{t.M11:R},{t.M12:R},{t.M20:R},{t.M21:R},{t.M22:R},{t.Tx:R},{t.Ty:R},{t.Tz:R}"));
        if (_layoutUsed.TryGetValue(key, out var hit) || _layoutKept.TryGetValue(key, out hit)) return _layoutUsed[key] = hit;
        (C3dLowered[] Solids, C3dSheetGeometry[] Sheets) made = (solids.Solids.Select(lower).ToArray(), solids.Sheets.Select(sh => C3dLowering.TransformSheet(C3dLowering.Geometry(sh), t)).ToArray());
        return _layoutUsed[key] = made;
    }

    private static string TechName(TechResolution t)
        => t.ResolvedPath is { } p ? Path.GetFileNameWithoutExtension(p) : t.Tech?.Name ?? "(no technology)";

    /// <summary>A technology material's values at <paramref name="tempC"/> — the one rule elaboration resolves a drawn
    /// object's material by (brief-em3d-48 compares a flattened object's against it).</summary>
    public static Em3dMaterial MaterialValues(TechMaterial m, double tempC)
    {
        double sigma = 0;
        if (m.Sigma20 is { } s20)
            sigma = m.Alpha20 is { } a20 ? new WireMaterial(m.Name, s20, a20, 0).SigmaAt(tempC) : s20;
        return new Em3dMaterial(m.Name, m.Epsr ?? 1, m.EpsrTensor is { Length: 3 } t ? [.. t] : null, m.TanD ?? 0, m.Mur ?? 1, sigma);
    }

    // ── one elaboration ─────────────────────────────────────────────────────────────────────────────

    private sealed class Run(C3dElaborator owner, C3dElaborationOptions options, string? workspaceCws)
    {
        private readonly List<Em3dSolid> _solids = [];
        private readonly List<Em3dSheet> _sheets = [];
        private readonly Dictionary<string, C3dProvenance> _provenance = new(StringComparer.Ordinal);
        private readonly List<string> _notes = [];
        private readonly List<string> _warnings = [];
        private readonly List<string> _refusals = [];
        private readonly Dictionary<string, HashSet<string>> _nets = new(StringComparer.Ordinal);
        private readonly List<string> _groundBand = [];
        private readonly Dictionary<string, Em3dObjectOrigin> _origins = new(StringComparer.Ordinal);
        private readonly List<Em3dWireReport> _wires = [];
        private readonly Dictionary<string, string> _wireRefusals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, C3dWireResult> _drawnWires = new(StringComparer.Ordinal);
        private readonly List<(string Name, string Source)> _builtInWireValues = [];
        private readonly List<C3dWalkStep> _walkInstances = [], _walkUnits = [], _walkLowering = [];
        private readonly List<(string, string)> _unresolved = [];
        private int _order;
        private int _polylines;
        private int _topDbu;
        private readonly SortedSet<string> _ignored = new(StringComparer.Ordinal);
        private bool _anyLayoutInstance;

        // Materials: every (technology, name) with its values, and each object's key, renamed at the end.
        private readonly List<(string Tech, string Name, Em3dMaterial Values, string Source)> _materials = [];
        private readonly List<(int Index, bool Sheet, string Tech, string Name)> _uses = [];

        public C3dElaboration Go(C3dDocument doc, string path)
        {
            var (tech, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, path, workspaceCws, owner._tech);
            foreach (string d in tech.Diagnostics) _notes.Add(d);
            _topDbu = doc.DbuPerMicron;
            Document(doc, path, tech, C3dTransform.Identity, "", [(CellOf(path), "", path)], exact: true);

            if (_polylines > 0)
                _notes.Add($"{_polylines} polyline(s) are construction geometry and are not in the 3D problem.");
            if (_ignored.Count > 0 || _anyLayoutInstance)
            {
                var parts = _ignored.Append(_anyLayoutInstance ? "a layout's .cem solve region" : null).OfType<string>().ToList();
                string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
                _notes.Add($"A placed cell contributes geometry only (overview §1k): {list} " +
                           $"{(parts.Count == 1 ? "is" : "are")} not part of its instance. Only the parent says where a signal enters.");
            }

            if (_builtInWireValues.Count > 0)
                _notes.Add($"No {string.Join(", ", _builtInWireValues.Select(v => v.Source).Distinct())} is stated on " +
                           $"{string.Join(", ", _builtInWireValues.Select(v => v.Name).Distinct().Select(n => $"'{n}'"))} or in the " +
                           "workspace's assembly rules, so the built-in starting value was used. It is a first guess, not assembly " +
                           "data: set it on the wire's end, or in the .wasm the workspace's DefaultAssemblyRef names.");

            var (materials, sources, walkMaterials) = Materials();
            var solids = new List<Em3dSolid>(_solids);
            var sheets = new List<Em3dSheet>(_sheets);
            foreach (var (index, isSheet, t, n) in _uses)
            {
                string final = Final(t, n);
                if (isSheet) sheets[index] = sheets[index] with { Material = final };
                else solids[index] = solids[index] with { Material = final };
            }

            return new C3dElaboration(solids, sheets, materials, _provenance, _notes, _refusals)
            {
                Warnings = _warnings,
                Unresolved = _unresolved,
                ObjectNets = _nets,
                GroundBandObjects = _groundBand,
                MaterialSources = sources,
                Origins = _origins,
                Wires = _wires,
                WireRefusals = _wireRefusals,
                DrawnWires = _drawnWires,
                WalkInstances = _walkInstances,
                WalkUnits = _walkUnits,
                WalkMaterials = walkMaterials,
                WalkLowering = _walkLowering,
                Technology = tech.Tech,
                TechnologyPath = tech.ResolvedPath,
            };
        }

        /// <summary>A document's objects and instances under <paramref name="world"/> (metres).</summary>
        private void Document(C3dDocument doc, string path, TechResolution tech, C3dTransform world, string prefix,
                              List<(string Cell, string Instance, string Path)> stack, bool exact)
        {
            string techName = TechName(tech);
            string unit = LayoutUnits.AsciiSuffix(doc.DisplayUnit);
            _walkUnits.Add(new C3dWalkStep(prefix.Length == 0 ? Path.GetFileName(path) : prefix.TrimEnd('/'),
                $"{doc.DbuPerMicron} DBU per µm (1 DBU = {C3dLowering.Metres(1, doc.DbuPerMicron).ToString("R", CultureInfo.InvariantCulture)} m), " +
                $"displayed in {unit}; converted to metres through the exact decimal path, then placed"));
            if (prefix.Length > 0)
            {
                if (doc.Ports.Count > 0) _ignored.Add("its ports");
                if (doc.Setups.Count > 0) _ignored.Add("its setups");
                if (doc.FaceBoundaries.Count > 0) _ignored.Add("its face boundaries");
            }

            foreach (var obj in doc.Objects)
            {
                if (obj is C3dPolyline) { _polylines++; continue; }
                if (obj is C3dWire) continue;                       // after the instances: see Wires
                string name = prefix + obj.Name;
                if (obj.Material is not { Length: > 0 } matName)
                {
                    _refusals.Add($"'{name}' has no material, so nothing says what it is to a solver.");
                    continue;
                }
                if (tech.Tech?.FindMaterial(matName) is not { } material)
                {
                    _refusals.Add($"'{name}' is made of '{matName}', which its technology ({techName}) does not define.");
                    continue;
                }
                if (Role(obj, material, name) is not { } role) continue;

                var lowered = owner.LowerCached(obj, world, doc.DbuPerMicron);
                if (lowered is null) continue;
                var values = Resolve(material, out string source);
                string key = Register(techName, material.Name, values, $"technology '{techName}' Materials" + source);

                if (lowered.Sheet is { } g)
                {
                    if (role != Em3dRole.Conductor)
                    {
                        _refusals.Add($"Sheet '{name}' is made of '{matName}', a {role.ToString().ToLowerInvariant()}: a sheet is a " +
                                      "conductor thin enough to be a surface, so a sheet of anything else has no meaning. Make it a " +
                                      "solid, or give it a conducting material.");
                        continue;
                    }
                    double t = (obj as C3dSheet)?.ThicknessUm is { } um ? um * 1e-6 : 0;
                    _uses.Add((_sheets.Count, true, techName, material.Name));
                    _sheets.Add(new Em3dSheet(name, key, g.Outline, g.Holes, g.Z, t, ++_order) { Frame = g.Frame });
                }
                else
                {
                    _uses.Add((_solids.Count, false, techName, material.Name));
                    _solids.Add(new Em3dSolid(name, key, role, lowered.Solid!, ++_order));
                }
                _provenance[name] = new C3dProvenance(prefix.TrimEnd('/'), path, obj.Name, lowered.FaceNames)
                {
                    Exact = exact && obj.Placement.ToTransform().IsIntegral,
                    Element = prefix.Length > 0 ? world : null,
                };
                _walkLowering.Add(new C3dWalkStep(name, lowered.Kind));
                _origins[name] = new Em3dObjectOrigin(role switch
                {
                    Em3dRole.Conductor => Em3dObjectKind.Conductor, Em3dRole.Air => Em3dObjectKind.Air, _ => Em3dObjectKind.Body,
                }, null, null, null);
                if (role == Em3dRole.Conductor) Net(name, name);
            }

            string baseDir = Path.GetDirectoryName(path)!;
            foreach (var inst in doc.Instances) Instance(doc, inst, baseDir, world, prefix, stack, exact);
            if (doc.Objects.Any(o => o is C3dWire)) Wires(doc, path, tech, world, prefix);
        }

        /// <summary>brief-em3d-50 — the document's drawn wires, landed on the conductors of this document and everything
        /// below it (never a sibling's: a wire in a child lands inside that child).</summary>
        private void Wires(C3dDocument doc, string path, TechResolution tech, C3dTransform world, string prefix)
        {
            string techName = TechName(tech);
            var wireNames = new HashSet<string>(doc.Objects.OfType<C3dWire>().Select(w => prefix + w.Name), StringComparer.Ordinal);
            var pads = C3dWires.Pads(_solids, _sheets, prefix, wireNames);
            double tol = C3dLowering.Metres(1, _topDbu);
            var workspace = new WireBondWorkspace(path, WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(path)) ?? workspaceCws);
            foreach (var w in doc.Objects.OfType<C3dWire>())
            {
                string name = prefix + w.Name;
                string matName = C3dWires.MaterialOf(w);
                string? refusal = null;
                TechMaterial? material = tech.Tech?.FindMaterial(matName);
                if (material is null)
                {
                    var metals = tech.Tech?.Materials.Where(m => m.Sigma20 is not null).Select(m => m.Name).ToList() ?? [];
                    refusal = $"Wire '{name}' is made of '{matName}', which its technology ({techName}) does not define. Known metals: " +
                              $"{(metals.Count == 0 ? "none" : string.Join(", ", metals))}. A 3D model does not substitute a metal.";
                }
                else if (material.Sigma20 is null)
                    refusal = $"Wire '{name}' is made of '{matName}', which technology '{techName}' defines with no conductivity " +
                              "(Sigma20), so it is not a metal.";
                else if (w.Role is { } role && role != Em3dRole.Conductor)
                    refusal = $"Wire '{name}' has the role {role}; a wire is a conductor.";
                C3dWireResult? result = null;
                if (refusal is null)
                {
                    result = C3dWires.Resolve(w, name, world, doc.DbuPerMicron, pads, tol, workspace, LayoutUnits.AsciiSuffix(doc.DisplayUnit));
                    _drawnWires[name] = result;
                    refusal = result.Refusal;
                }
                if (refusal is not null || result?.Resolution is not { Sweep: { } sweep } r)
                {
                    refusal ??= $"Wire '{name}' could not be built.";
                    _wireRefusals[name] = refusal;
                    _refusals.Add(refusal);
                    continue;
                }
                _warnings.AddRange(r.Warnings);
                var values = Resolve(material!, out string source);
                string key = Register(techName, material!.Name, values, $"technology '{techName}' Materials" + source);
                void Solid(string solidName, Em3dPrimitive primitive)
                {
                    _uses.Add((_solids.Count, false, techName, material.Name));
                    _solids.Add(new Em3dSolid(solidName, key, Em3dRole.Conductor, primitive, ++_order));
                    _provenance[solidName] = new C3dProvenance(prefix.TrimEnd('/'), path, w.Name, []) { Exact = false, Element = prefix.Length > 0 ? world : null };
                    _origins[solidName] = new Em3dObjectOrigin(Em3dObjectKind.Wire, null, null, null);
                    Net(solidName, name);
                }
                Solid(name, sweep);
                foreach (var (ballName, ball) in r.Balls) Solid(ballName, ball);
                _walkLowering.Add(new C3dWalkStep(name, $"wire: {r.Report!.Section} sweep of {sweep.Rings.Count} sections from " +
                                                        $"'{result.StartPad!.Name}' to '{result.EndPad!.Name}'"));
                _wires.Add(r.Report with { Material = material.Name });
                foreach (var (v, what) in new[] { (result.StartProcess!, w.Start), (result.EndProcess!, w.End) })
                {
                    if (what.Style == WBond.BondStyle.Wedge && v.FootLength.Source == WireBondValueSource.BuiltIn)
                        _builtInWireValues.Add((name, "wedge-foot length"));
                    if (what.Style == WBond.BondStyle.Ball && v.BallDiameter.Source == WireBondValueSource.BuiltIn)
                        _builtInWireValues.Add((name, "ball diameter and height"));
                }
            }
        }

        /// <summary>One instance: resolved, then elaborated once per array element.</summary>
        private void Instance(C3dDocument doc, C3dInstance inst, string baseDir, C3dTransform world, string prefix,
                              List<(string Cell, string Instance, string Path)> stack, bool exact)
        {
            string instPath = prefix + inst.Name;
            var viewType = inst.View == C3dInstanceView.Layout ? ViewType.Layout : ViewType.ThreeD;
            string noun = CellFolder.ViewNoun(viewType);
            string? cellDir = ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir, out _, out bool exists);
            if (cellDir is null || !exists)
            {
                _unresolved.Add((instPath, inst.CellRef));
                _refusals.Add($"Instance '{instPath}' places cell '{inst.CellRef}', which resolves to nothing" +
                              (cellDir is null ? " (the reference cannot be worked out from here)." : $" (tried {cellDir})."));
                return;
            }
            var primary = CellFolder.ResolvePrimary(cellDir, viewType);
            if (primary.ResolvedName is not { } file)
            {
                _unresolved.Add((instPath, inst.CellRef));
                _refusals.Add($"Instance '{instPath}' places the {noun} of cell '{Path.GetFileName(cellDir)}', which has " +
                              $"{(primary.State == PrimaryState.NoView ? $"no {noun}" : $"no primary {noun}")} " +
                              $"(tried {CellFolder.SubFolderPath(cellDir, viewType)}).");
                return;
            }
            string viewPath = Path.Combine(CellFolder.SubFolderPath(cellDir, viewType), file);

            var counts = inst.Array?.Counts ?? [1, 1, 1];
            if (counts.Count != 3 || counts.Any(n => n < 1))
            {
                _refusals.Add($"Instance '{instPath}' is an array of [{string.Join(", ", counts)}]; an array states three counts, each at least 1.");
                return;
            }
            var pitch = inst.Array?.Pitch ?? default;
            bool isArray = inst.Array is not null && counts.Any(n => n > 1);

            if (viewType == ViewType.ThreeD)
            {
                if (stack.FindIndex(s => string.Equals(s.Path, viewPath, StringComparison.OrdinalIgnoreCase)) is int at && at >= 0)
                {
                    var cycle = new StringBuilder();
                    for (int k = at; k < stack.Count; k++)
                        cycle.Append(k == at ? "" : " → ").Append(stack[k].Cell).Append(k + 1 < stack.Count ? "/" + stack[k + 1].Instance : "/" + inst.Name);
                    cycle.Append(" → ").Append(stack[at].Cell);
                    _refusals.Add($"The 3D view reaches itself through its instances: {cycle}. A cell cannot contain itself.");
                    return;
                }
                var child = owner.Child3DCached(viewPath, workspaceCws);
                _walkInstances.Add(new C3dWalkStep(instPath,
                    $"cell folder {cellDir} (from '{inst.CellRef}'); {noun} {viewPath} ({primary.State}); technology " +
                    $"{child.Tech.ResolvedPath ?? "(none)"} ({child.Tech.Source}, resolved from the 3D view's own path)" +
                    (isArray ? $"; array {counts[0]}×{counts[1]}×{counts[2]}" : "")));
                if (child.Refusal is { } why)
                {
                    _unresolved.Add((instPath, inst.CellRef));
                    _refusals.Add($"Instance '{instPath}' places '{viewPath}', which cannot be read: {why}");
                    return;
                }
                var next = new List<(string, string, string)>(stack) { (CellOf(viewPath), inst.Name, viewPath) };
                foreach (var (ijk, w, integral) in Elements(doc, inst, counts, pitch, world))
                    Document(child.Document, viewPath, child.Tech, w, prefix + inst.Name + (isArray ? ijk : "") + "/", next,
                             exact && integral && child.Document.DbuPerMicron == _topDbu);
                return;
            }

            _anyLayoutInstance = true;
            var layout = owner.ChildLayoutCached(viewPath, workspaceCws, options);
            _walkInstances.Add(new C3dWalkStep(instPath,
                $"cell folder {cellDir} (from '{inst.CellRef}'); {noun} {viewPath} ({primary.State}); technology " +
                $"{layout.Tech.ResolvedPath ?? "(none)"} ({layout.Tech.Source}, resolved from the layout's own path — the " +
                $"layout's technology, never the parent's)" + (isArray ? $"; array {counts[0]}×{counts[1]}×{counts[2]}" : "")));
            if (layout.Refusal is { } refusal || layout.Solids is not { } solids)
            {
                _unresolved.Add((instPath, inst.CellRef));
                _refusals.Add($"Instance '{instPath}' places layout '{viewPath}', which cannot be put in 3D: {layout.Refusal}");
                return;
            }
            if (solids.Notes.Count > 0 || solids.Warnings.Count > 0)
            {
                foreach (string n in solids.Notes) _notes.Add($"{instPath}: {n}");
                foreach (string w in solids.Warnings) _warnings.Add($"{instPath}: {w}");
            }
            if (LayoutHasPortShapes(viewPath)) _ignored.Add("a layout's port shapes");
            foreach (var (ijk, w, integral) in Elements(doc, inst, counts, pitch, world))
                Layout(layout, solids, layout.TechName, viewPath, prefix + inst.Name + (isArray ? ijk : ""), w,
                       exact && integral && layout.DbuPerMicron == _topDbu);
        }

        /// <summary>Each array element's transform: the placement's rotation and mirror, and its origin moved
        /// by the pitch in the PARENT's frame (LayoutInstanceTransform.ArrayCellOrigin's rule), then the world.</summary>
        private static IEnumerable<(string Ijk, C3dTransform World, bool Integral)> Elements(C3dDocument parent, C3dInstance inst,
                                                                                IReadOnlyList<int> counts, C3dPoint3 pitch,
                                                                                C3dTransform world)
        {
            var placement = inst.Placement.ToTransform();
            for (int k = 0; k < counts[2]; k++)
                for (int j = 0; j < counts[1]; j++)
                    for (int i = 0; i < counts[0]; i++)
                    {
                        var t = placement with
                        {
                            Tx = placement.Tx + (double)i * pitch.X,
                            Ty = placement.Ty + (double)j * pitch.Y,
                            Tz = placement.Tz + (double)k * pitch.Z,
                        };
                        yield return ($"[{i},{j},{k}]", C3dLowering.InMetres(t, parent.DbuPerMicron).Then(world), t.IsIntegral);
                    }
        }

        /// <summary>A layout instance's solids under <paramref name="world"/>, its stack's bottom at the instance's z.</summary>
        private void Layout(object child, Em3dLayoutSolidsResult solids, string techName, string viewPath, string instPath, C3dTransform world,
                            bool exact)
        {
            var t = C3dTransform.Identity with { Tz = -solids.StackBottomM };
            t = t.Then(world);
            var (loweredSolids, loweredSheets) = owner.LayoutLowered(child, solids, t,
                s => C3dLowering.Transform(s.Primitive, FaceNamesOf(s.Primitive), t, KindOf(s.Primitive)));
            string prefix = instPath + "/";
            int baseOrder = _order;
            int maxOrder = 0;
            var materialKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in solids.Materials)
                materialKey[m.Name] = Register(techName, m.Name, m, solids.MaterialSources.GetValueOrDefault(m.Name) ?? $"technology '{techName}'");

            for (int si = 0; si < solids.Solids.Count; si++)
            {
                var s = solids.Solids[si];
                var lowered = loweredSolids[si];
                string name = prefix + s.Name;
                _uses.Add((_solids.Count, false, techName, s.Material));
                _solids.Add(new Em3dSolid(name, materialKey[s.Material], s.Role, lowered.Solid!, baseOrder + s.Order));
                maxOrder = Math.Max(maxOrder, s.Order);
                _provenance[name] = new C3dProvenance(instPath, viewPath, s.Name, lowered.FaceNames) { Exact = exact, Element = t };
                _walkLowering.Add(new C3dWalkStep(name, $"{lowered.Kind} (from the layout)"));
            }
            for (int hi = 0; hi < solids.Sheets.Count; hi++)
            {
                var sh = solids.Sheets[hi];
                var g = loweredSheets[hi];
                string name = prefix + sh.Name;
                _uses.Add((_sheets.Count, true, techName, sh.Material));
                _sheets.Add(new Em3dSheet(name, materialKey[sh.Material], g.Outline, g.Holes, g.Z, sh.ThicknessM, baseOrder + sh.Order)
                            { Frame = g.Frame });
                maxOrder = Math.Max(maxOrder, sh.Order);
                _provenance[name] = new C3dProvenance(instPath, viewPath, sh.Name, []) { Exact = exact, Element = t };
                _walkLowering.Add(new C3dWalkStep(name, $"{(g.Frame is null ? C3dLowering.KindSheet : C3dLowering.KindFramedSheet)} (from the layout)"));
            }
            _order = baseOrder + maxOrder;

            foreach (var (obj, nets) in solids.ObjectNets)
                foreach (string n in nets) Net(prefix + obj, n);
            _groundBand.AddRange(solids.GroundBandObjects.Select(o => prefix + o));
            foreach (var (obj, origin) in solids.Origins) _origins[prefix + obj] = origin;
            foreach (var w in solids.Wires)
                _wires.Add(w with
                {
                    Name = prefix + w.Name,
                    Start = w.Start with { Pad = prefix + w.Start.Pad },
                    End = w.End with { Pad = prefix + w.End.Pad },
                });
        }

        private static IReadOnlyList<string> FaceNamesOf(Em3dPrimitive p) => p switch
        {
            Em3dBox => Em3dTessellation.BoxFaces,
            Em3dExtrudedPolygon e => ["bottom", "top", .. Enumerable.Range(0, e.Outline.Count).Select(k => $"side{k}"),
                                      .. e.Holes.SelectMany((h, i) => Enumerable.Range(0, h.Count).Select(k => $"hole{i}.side{k}"))],
            Em3dCylinder => ["bottom", "top", "side"],
            _ => [],
        };

        private static string KindOf(Em3dPrimitive p) => p switch
        {
            Em3dBox => C3dLowering.KindBox, Em3dExtrudedPolygon => C3dLowering.KindExtrusion,
            Em3dCylinder => C3dLowering.KindCylinder, _ => C3dLowering.KindPolyhedron,
        };

        private static bool LayoutHasPortShapes(string clayPath)
        {
            try { return LayoutPersistence.LoadFromFile(clayPath).Shapes.Any(s => s is LabelShape { IsPort: true }); }
            catch { return false; }
        }

        /// <summary>
        /// R-em3d42-3e — the object's role: an explicit Role wins; otherwise a material with σ and no εr is a
        /// conductor, one with εr a dielectric, and <c>Air</c> is air. σ AND εr with no Role is refused: whether a
        /// lossy dielectric is a conductor is exactly the call the user must make.
        /// </summary>
        private Em3dRole? Role(C3dObject obj, TechMaterial m, string name)
        {
            if (obj.Role is { } stated) return stated;
            if (string.Equals(m.Name, Em3dGenerator.AirMaterial, StringComparison.OrdinalIgnoreCase)) return Em3dRole.Air;
            bool hasSigma = m.Sigma20 is > 0;
            bool hasEpsr = m.Epsr is not null || m.EpsrTensor is { Length: 3 };
            if (hasSigma && hasEpsr)
            {
                _refusals.Add($"'{name}' is made of '{m.Name}', which states both a conductivity and a permittivity, so it could be " +
                              "a conductor or a lossy dielectric. Say which with the object's Role.");
                return null;
            }
            if (hasSigma) return Em3dRole.Conductor;
            if (hasEpsr) return Em3dRole.Dielectric;
            _refusals.Add($"'{name}' is made of '{m.Name}', which states neither a conductivity nor a permittivity, so nothing says " +
                          "what it is. Give the material σ₂₀ or εr, or give the object a Role.");
            return null;
        }

        /// <summary>A technology material's values at the operating temperature — the generator's body rule.</summary>
        private Em3dMaterial Resolve(TechMaterial m, out string note)
        {
            note = m.Sigma20 is not null && m.Alpha20 is null ? " (σ₂₀, no α₂₀)" : "";
            return MaterialValues(m, options.TempC);
        }

        private string Register(string tech, string name, Em3dMaterial values, string source)
        {
            if (!_materials.Any(x => x.Tech == tech && x.Name == name))
                _materials.Add((tech, name, values with { Name = name }, source));
            return name;
        }

        private readonly Dictionary<(string Tech, string Name), string> _final = [];
        private string Final(string tech, string name) => _final.GetValueOrDefault((tech, name), name);

        /// <summary>R-em3d42-3e — merge equal values under one name, qualify different ones with their technology.</summary>
        private (List<Em3dMaterial>, Dictionary<string, string>, List<C3dWalkStep>) Materials()
        {
            var list = new List<Em3dMaterial>();
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            var walk = new List<C3dWalkStep>();
            foreach (var group in _materials.GroupBy(m => m.Name, StringComparer.Ordinal))
            {
                var sets = new List<(Em3dMaterial Values, List<string> Techs, string Source)>();
                foreach (var m in group)
                {
                    int i = sets.FindIndex(s => Same(s.Values, m.Values));
                    if (i >= 0) sets[i].Techs.Add(m.Tech);
                    else sets.Add((m.Values, [m.Tech], m.Source));
                }
                if (sets.Count == 1)
                {
                    list.Add(sets[0].Values);
                    sources[group.Key] = sets[0].Source;
                    walk.Add(new C3dWalkStep(group.Key, sets[0].Techs.Count > 1
                        ? $"equal in {string.Join(" and ", sets[0].Techs.Select(t => $"'{t}'"))}, merged"
                        : $"from '{sets[0].Techs[0]}'"));
                    continue;
                }
                var described = new List<string>();
                foreach (var (values, techs, source) in sets)
                {
                    string q = $"{group.Key}@{techs[0]}";
                    foreach (string t in techs) _final[(t, group.Key)] = q;
                    list.Add(values with { Name = q });
                    sources[q] = source;
                    described.Add($"'{q}' ({Describe(values)})");
                    walk.Add(new C3dWalkStep(q, $"'{group.Key}' in {string.Join(" and ", techs.Select(t => $"'{t}'"))}, kept apart"));
                }
                _notes.Add($"Material '{group.Key}' has different values in {sets.Count} technologies, so each is kept under its own " +
                           $"name: {string.Join(", ", described)}.");
            }
            return (list, sources, walk);

            static bool Same(Em3dMaterial a, Em3dMaterial b) =>
                a.Epsr == b.Epsr && a.TanD == b.TanD && a.Mur == b.Mur && a.SigmaSm == b.SigmaSm &&
                (a.EpsrTensor ?? []).SequenceEqual(b.EpsrTensor ?? []);
            static string Describe(Em3dMaterial m) => string.Create(CultureInfo.InvariantCulture,
                $"εr {m.Epsr:G6}, tanδ {m.TanD:G6}, μr {m.Mur:G6}, σ {m.SigmaSm:G6} S/m");
        }

        private void Net(string obj, string net)
            => (_nets.TryGetValue(obj, out var set) ? set : _nets[obj] = new(StringComparer.Ordinal)).Add(net);

        /// <summary>The cell a view belongs to: its folder's name, above the view's own sub-folder.</summary>
        private static string CellOf(string viewPath)
        {
            string? dir = Path.GetDirectoryName(viewPath);
            return dir is not null && Path.GetFileName(dir) is var sub &&
                   string.Equals(sub, CellFolder.SubFolderName(ViewType.ThreeD), StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(Path.GetDirectoryName(dir)) ?? Path.GetFileNameWithoutExtension(viewPath)
                : Path.GetFileNameWithoutExtension(viewPath);
        }
    }
}
