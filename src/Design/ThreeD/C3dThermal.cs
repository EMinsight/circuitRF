// brief-em3d-73 R-em3d73-5 / -6 — a thermal setup and the document's thermal places: what is wrong with them.
//
// THE ONE VALIDATOR (CLAUDE.md: a rule living only in `check` is a rule the application does not enforce). `check` reports
// exactly these; brief 75's editor shows exactly these; brief 74's run refuses on exactly the errors among them. Three
// layers, each needing more than the one before:
//
//   Places(doc)                 — the file alone: shapes, names, one kind per probe, a positive resistance.
//   Places(doc, elaboration)    — against the solids: a sheet inside ONE solid, faces and wires that exist, contacts that
//                                 touch.
//   Setup(name, setup, doc, e)  — a thermal setup's section: a sink, faces, source overrides, materials' k, the sweep, the
//                                 measures.
//
// NOTHING HERE SOLVES OR MESHES. The containment test is exact for a box, a z-extruded polygon and a cylinder, and uses the
// solid's bounding box for everything else (a sweep, a polyhedron, a kernel solid) — which can only ACCEPT a sheet that is
// in fact outside a curved solid, never refuse one that is inside; brief 74's mesher is the exact check for those.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>What a thermal setup's value is, so its trailing unit is checked against it: a temperature (bare °C), a power, a
/// current, a frequency, or a plain number in the unit its key names (h in W/(m²·K), a duty, a resistance per area). <c>Any</c>
/// takes whatever unit the table knows — a sweep's Start and Stop, whose variable may be anything.</summary>
public enum ThermalQuantity { Any, Temperature, Power, Current, Frequency, Time, Plain }

/// <summary>The thermal setup's sentences and rules.</summary>
public static class C3dThermal
{
    /// <summary>D1 — a thermal setup lives in the <c>.c3d</c> it solves; a <c>.cem</c> naming one is refused.</summary>
    public const string CemRefusal =
        "A .cem cannot hold a thermal setup (Problem3D: Thermal): a thermal setup lives in the 3D view it solves, among its " +
        "embedded Setups. Move it there.";

    /// <summary>R-em3d73-5a — a thermal setup stating a 3D solver.</summary>
    public static string SolverStated(string name, Em3dSolver solver) =>
        $"Embedded setup '{name}' is a thermal setup and states Solver3D: {solver}. The thermal solver is circuitRF's own, so " +
        "a thermal setup names no EM solver: remove Solver3D.";

    /// <summary>What an EM path says to a thermal setup that reached it: brief 74's run service solves it, never an EM solver.</summary>
    public static string RunRefusal(string setupName)
        => $"Setup '{setupName}' is a thermal setup, which circuitRF's own thermal solver runs; no EM solver takes it.";

    /// <summary>The functions a measure may call over a probe.</summary>
    public static readonly string[] ProbeFunctions = ["Tmax", "Tmin", "Tavg", "T"];

    /// <summary>The face spelling that means every exposed face.</summary>
    public const string ExposedFaces = "*exposed*";

    /// <summary>How many sweep axes a thermal setup may state.</summary>
    public const int MaxSweepAxes = 2;

    /// <summary>brief-em3d-76 R-em3d76-4b — the variable a measure reads for 2ⁿ, n the document's symmetry planes, so a
    /// whole-device figure is explicit: <c>Rth_full = (Tmax(ch) - Ths) / (Pdiss * SymmetryFactor)</c>. Nothing is multiplied
    /// silently. A document variable of the same name wins over it.</summary>
    public const string SymmetryFactorName = "SymmetryFactor";

    // ── 1. The places, from the file alone ─────────────────────────────────────────────────────────────

    /// <summary>What is wrong with the document's thermal places that the file alone shows.</summary>
    public static IReadOnlyList<Diagnostic> Places(C3dDocument doc)
    {
        var found = new List<Diagnostic>();
        Names(doc, found);

        foreach (var h in doc.HeatSources)
        {
            if (h.Sheet is null == (h.Solid is null))
                found.Add(D.SourceShape(h.Name, h.Sheet is null ? "states neither a Sheet nor a Solid" : "states both a Sheet and a Solid; it is one or the other"));
            else if (h.Sheet is { } s)
            {
                if (s.Rect is null && s.Outline.Count == 0) found.Add(D.SourceShape(h.Name, "has a Sheet with neither a Rect nor an Outline"));
                else if (s.Rect is not null && s.Outline.Count > 0) found.Add(D.SourceShape(h.Name, "has a Sheet with both a Rect and an Outline; it must have one"));
                else if (s.Rect is { } r && (r.Size.U == 0 || r.Size.V == 0)) found.Add(D.SourceShape(h.Name, "has a Sheet rectangle with no area"));
                else if (s.Outline.Count > 0 && s.Outline.Distinct().Count() < 3) found.Add(D.SourceShape(h.Name, "has a Sheet outline of fewer than three distinct points"));
                if (h.Density == C3dHeatDensity.PerVolume) found.Add(D.SourceShape(h.Name, "is a sheet with a PerVolume density; a sheet's power is Total or PerArea"));
            }
            else if (string.IsNullOrWhiteSpace(h.Solid)) found.Add(D.SourceShape(h.Name, "names an empty Solid"));
            else if (h.Density == C3dHeatDensity.PerArea) found.Add(D.SourceShape(h.Name, "is a solid with a PerArea density; a solid's power is Total or PerVolume"));
            if (h.Power is { } p && Unparsable(p, ThermalQuantity.Power) is { } why) found.Add(D.SourceShape(h.Name, $"has a Power '{p}' that cannot be read: {why}"));
            Unread(h.Unread, $"Heat source '{h.Name}'", found);
        }

        foreach (var p in doc.Probes)
        {
            var kinds = p.Kinds();
            if (kinds.Count != 1)
                found.Add(D.ProbeShape(p.Name, kinds.Count == 0
                    ? "states no place; a probe is one of Point, Face, Solid, Spot, Line or Wire"
                    : $"states {string.Join(" and ", kinds)}; a probe is exactly one of them"));
            else if (p.Stat is not null && (p.Point is not null || p.Line is not null))
                found.Add(D.ProbeShape(p.Name, $"is a {kinds[0]} probe with a Stat; a point is one temperature and a line reports T along it"));
            if (p.Spot is { } spot && spot.Diameter <= 0 && C3dBindings.GetExpr(spot, nameof(C3dProbeSpot.Diameter), 0) is null)
                found.Add(D.ProbeShape(p.Name, "is a spot with a diameter that is not positive"));
            if (p.Line is { } line && line.From == line.To && line.Exprs is null)
                found.Add(D.ProbeShape(p.Name, "is a line whose two ends are the same point"));
            if (p.LimitC is { } lim && !double.IsFinite(lim))
                found.Add(D.ProbeShape(p.Name, "has a LimitC that is not a finite temperature"));
            Unread(p.Unread, $"Probe '{p.Name}'", found);
        }

        foreach (var m in doc.MeshRegions)
        {
            if ((m.Size.X <= 0 || m.Size.Y <= 0 || m.Size.Z <= 0) && m.Exprs?.ContainsKey(nameof(C3dMeshRegion.Size)) != true)
                found.Add(D.RegionShape(m.Name, "has a size component that is not positive"));
            if (!(m.SizeUm > 0) && C3dBindings.GetExpr(m, nameof(C3dMeshRegion.SizeUm), 0) is null)
                found.Add(D.RegionShape(m.Name, "has a SizeUm that is not positive"));
            if (m.Grading is { } g && !(g > 1))
                found.Add(D.RegionShape(m.Name, "has a Grading that is not above 1"));
            Unread(m.Unread, $"Mesh region '{m.Name}'", found);
        }

        // brief-em3d-76 — effective blocks and symmetry planes, from the file alone
        foreach (var b in doc.EffectiveBlocks)
        {
            if ((b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) && b.Exprs?.ContainsKey(nameof(C3dEffectiveBlock.Size)) != true)
                found.Add(D.BlockShape(b.Name, "has a size component that is not positive"));
            Unread(b.Unread, $"Effective block '{b.Name}'", found);
        }
        foreach (var g in doc.SymmetryPlanes.GroupBy(p => p.Axis).Where(g => g.Count() > 1))
            found.Add(D.Symmetry($"{g.Count()} symmetry planes are normal to {g.Key}; a model is cut at most once per axis"));
        foreach (var sp in doc.SymmetryPlanes) Unread(sp.Unread, $"The symmetry plane normal to {sp.Axis}", found);
        if (doc.WireGroundPlane is { } wg) Unread(wg.Unread, "The wire ground plane", found);

        var pairs = new HashSet<(string, string)>();
        for (int i = 0; i < doc.ContactResistances.Count; i++)
        {
            var c = doc.ContactResistances[i];
            string label = c.Between.Count == 2 ? $"between '{c.Between[0]}' and '{c.Between[1]}'" : $"#{i + 1}";
            if (c.Between.Count != 2 || c.Between.Any(string.IsNullOrWhiteSpace))
                found.Add(D.ContactShape(label, "must name exactly two objects in Between"));
            else if (string.Equals(c.Between[0], c.Between[1], StringComparison.Ordinal))
                found.Add(D.ContactShape(label, "names one object twice; a contact is between two"));
            else if (!pairs.Add(string.CompareOrdinal(c.Between[0], c.Between[1]) < 0 ? (c.Between[0], c.Between[1]) : (c.Between[1], c.Between[0])))
                found.Add(D.ContactShape(label, "names a pair another entry already names; one contact takes one resistance"));
            // brief-em3d-76 R-em3d76-1b — zero is perfect contact: an override may say so where the technology states a pair value
            if (!(c.ResistanceM2KW >= 0) || !double.IsFinite(c.ResistanceM2KW))
                found.Add(D.ContactShape(label, $"has a resistance of {Num(c.ResistanceM2KW)} m²·K/W; it must be zero (perfect contact) or positive"));
            Unread(c.Unread, $"The contact resistance {label}", found);
        }
        return found;
    }

    /// <summary>R-em3d73-4 — a thermal place's name is valid, and unique across the document's objects, instances, ports and
    /// the other thermal places. A clash between two EM items is <see cref="C3dValidation"/>'s to report, not this one's.</summary>
    private static void Names(C3dDocument doc, List<Diagnostic> found)
    {
        var thermal = doc.HeatSources.Select(h => (Kind: "heat source", h.Name))
            .Concat(doc.Probes.Select(p => (Kind: "probe", p.Name)))
            .Concat(doc.MeshRegions.Select(m => (Kind: "mesh region", m.Name)))
            .Concat(doc.EffectiveBlocks.Select(b => (Kind: "effective block", b.Name))).ToList();
        foreach (var (kind, name) in thermal)
            if (NameValidator.Validate(name) is { } why) found.Add(C3dDiagnostics.InvalidName(kind, name, why));
            // a probe is named in a measure (Tmax(die_top)): a name the expression engine cannot read is one no measure can use
            else if (kind == "probe" && C3dResolver.ValidateName(name) is { } asExpr) found.Add(D.ProbeName(name, asExpr));

        var others = doc.Objects.Select(o => o.Name).Concat(doc.Instances.Select(i => i.Name)).Concat(doc.Ports.Select(p => p.Name))
                        .Where(n => n.Length > 0).ToList();
        foreach (var g in thermal.Select(t => t.Name).Where(n => n.Length > 0).Concat(others)
                                 .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                                 .Where(g => g.Count() > 1 && thermal.Any(t => string.Equals(t.Name, g.Key, StringComparison.OrdinalIgnoreCase))))
            found.Add(C3dDiagnostics.DuplicateName(g.Key, g.Count()));
    }

    // ── 2. The places, against the solids ──────────────────────────────────────────────────────────────

    /// <summary>What is wrong with the thermal places against <paramref name="e"/>'s solids (R-em3d73-6a).</summary>
    public static IReadOnlyList<Diagnostic> Places(C3dDocument doc, C3dElaboration e)
    {
        var found = new List<Diagnostic>();
        if (!e.Ok) return found;
        double m = 1e-6 / doc.DbuPerMicron;
        double tol = Math.Max(m, 1e-12);
        var replaced = ReplacedBy(doc, e);

        foreach (var h in doc.HeatSources)
        {
            if (h.Solid is { Length: > 0 } sname)
            {
                if (replaced.TryGetValue(sname, out string? blk))
                    found.Add(D.SourceShape(h.Name, $"is spread through '{sname}', which effective block '{blk}' replaces; disable the block or move the source"));
                else if (SolidNamed(e, sname) is null) found.Add(D.SourceSolid(h.Name, sname, SomeSolids(e)));
                continue;
            }
            if (h.Sheet is not { } sheet || SheetSamples(sheet, m) is not { Count: > 0 } pts) continue;
            var meshed = Meshed(e).ToList();
            var holders = meshed.Where(s => pts.All(q => Inside(s.Primitive, q, tol))).ToList();
            if (holders.Count > 0) continue;
            var touched = meshed.Where(s => pts.Any(q => Inside(s.Primitive, q, tol))).Select(s => s.Name).Distinct().ToList();
            found.Add(touched.Count >= 1 && pts.All(q => meshed.Any(s => Inside(s.Primitive, q, tol)))
                ? D.SourceStraddles(h.Name, touched)
                : D.SourceOutside(h.Name, touched));
        }

        foreach (var p in doc.Probes)
        {
            foreach (string face in p.Face ?? [])
                if ((ReplacedFace(doc, face, replaced) ?? FaceProblem(doc, e, face)) is { } why) found.Add(D.ProbeFace(p.Name, face, why));
            if (p.Spot is { Face: var sf } && (ReplacedFace(doc, sf, replaced) ?? FaceProblem(doc, e, sf)) is { } why2) found.Add(D.ProbeFace(p.Name, sf, why2));
            // a Solid probe on a replaced solid is not a refusal: the run reads nothing there (a NaN), which is what an example
            // toggling its block on expects. A FACE on one is — the lowering refuses it, and so does the face check above.
            if (p.Solid is { } rs && replaced.TryGetValue(rs, out string? rblk))
                found.Add(D.ProbeReplaced(p.Name, rs, rblk));
            if (p.Solid is { } ps && SolidNamed(e, ps) is null) found.Add(D.ProbeSolid(p.Name, ps, SomeSolids(e)));
            if (p.Wire is { } w && !WireExists(e, w)) found.Add(D.ProbeWire(p.Name, w, e.Wires.Select(x => x.Name).Distinct().Take(8).ToList()));
        }

        // brief-em3d-76 — an enabled block cuts only what it may replace; a symmetry plane lies on the model's own extent
        foreach (var b in doc.EffectiveBlocks.Where(b => b.Enabled))
            if (Thermal.ThermalEffectiveBlocks.Intruder(doc, e, b) is { } other)
                found.Add(D.BlockShape(b.Name, $"cuts through '{other}', which is not board dielectric, a copper plane or a via barrel; a block " +
                                               "replaces only those — shrink it so it stops at the other solid's face"));
        if (doc.SymmetryPlanes.Count > 0 && MeshedExtent(e) is { } ext)
            foreach (var sp in doc.SymmetryPlanes)
            {
                double at = sp.At * m, lo = sp.Axis switch { C3dAxis.X => ext.X0, C3dAxis.Y => ext.Y0, _ => ext.Z0 },
                       hi = sp.Axis switch { C3dAxis.X => ext.X1, C3dAxis.Y => ext.Y1, _ => ext.Z1 };
                if (Math.Abs(at - lo) > tol && Math.Abs(at - hi) > tol)
                    found.Add(D.Symmetry($"The symmetry plane {sp.Axis} = {Num(at * 1e6)} µm does not lie on the model's extent ({Num(lo * 1e6)} to " +
                                         $"{Num(hi * 1e6)} µm along {sp.Axis}): the plane is the face the modelled half was cut on, so it is one end of the model"));
            }

        // brief-em3d-86 R-em3d86-2 — the drawn wires' ground plane resolves, and lies below every drawn wire (its image would
        // otherwise sit above the wire it mirrors)
        if (doc.WireGroundPlane is not null)
        {
            if (WireGroundPlaneZ(doc, e, out string? why) is not { } gz) found.Add(D.WireGround(why!));
            else
                foreach (var (name, w) in e.DrawnWires.OrderBy(x => x.Key, StringComparer.Ordinal))
                    if (w.Axis.Count > 0 && w.Axis.Min(p => p.Z) is var low && gz > low + tol)
                        found.Add(D.WireGround($"The wire ground plane at Z = {Num(gz * 1e6)} µm lies above drawn wire '{name}', whose lowest point " +
                                               $"is at {Num(low * 1e6)} µm: the image method mirrors each wire in a plane BELOW it"));
        }

        foreach (var c in doc.ContactResistances)
        {
            if (c.Between.Count != 2) continue;
            string label = $"between '{c.Between[0]}' and '{c.Between[1]}'";
            var a = replaced.ContainsKey(c.Between[0]) ? null : SolidNamed(e, c.Between[0]);
            var b = replaced.ContainsKey(c.Between[1]) ? null : SolidNamed(e, c.Between[1]);
            if (a is null || b is null)
            {
                found.Add(D.ContactApart(label, NotMeshedWhy(e, a is null ? c.Between[0] : c.Between[1], replaced)));
                continue;
            }
            if (!Touch(Em3dProblem.Bounds(a.Primitive), Em3dProblem.Bounds(b.Primitive), tol))
                found.Add(D.ContactApart(label, "the two do not touch, so there is no contact to override"));
        }
        return found;
    }

    /// <summary>The extent of every solid a thermal run meshes, metres; null with none.</summary>
    private static (double X0, double Y0, double Z0, double X1, double Y1, double Z1)? MeshedExtent(C3dElaboration e)
    {
        var bounds = e.Solids.Where(s => !CircuitRF.Design.Thermal.ThermalMaterials.NotMeshed(s.Role, s.Material)).Select(s => Em3dProblem.Bounds(s.Primitive)).ToList();
        if (bounds.Count == 0) return null;
        return (bounds.Min(b => b.X0), bounds.Min(b => b.Y0), bounds.Min(b => b.Z0), bounds.Max(b => b.X1), bounds.Max(b => b.Y1), bounds.Max(b => b.Z1));
    }

    /// <summary>
    /// brief-em3d-86 R-em3d86-2 — the height of the document's wire ground plane, metres in the view's frame: its Z, or the height
    /// of its face. Null with no plane; null with <paramref name="refusal"/> when the face is not a horizontal face of a conductor.
    /// </summary>
    public static double? WireGroundPlaneZ(C3dDocument doc, C3dElaboration e, out string? refusal)
    {
        refusal = null;
        if (doc.WireGroundPlane is not { } g) return null;
        double m = 1e-6 / doc.DbuPerMicron;
        if (g.Face is not { } face) return g.Z * m;
        var pieces = Thermal.ThermalLowerings.FacePieces(doc, e, e.Solids, face, out int si, out string? why);
        if (pieces is not { Count: > 0 })
        {
            refusal = $"The wire ground plane names the face '{face}': {why ?? "it has no area"}";
            return null;
        }
        if (e.Solids[si].Role != Em3dRole.Conductor)
        {
            refusal = $"The wire ground plane names the face '{face}', which is on '{e.Solids[si].Name}', not a conductor: an image plane is metal";
            return null;
        }
        var zs = pieces.SelectMany(pc => pc.Outer).Select(p => p.Z).ToList();
        if (zs.Max() - zs.Min() > m)
        {
            refusal = $"The wire ground plane names the face '{face}', which is not horizontal (it spans {Num(zs.Min() * 1e6)} to " +
                      $"{Num(zs.Max() * 1e6)} µm in Z): the image method reflects in a horizontal plane";
            return null;
        }
        return zs.Average();
    }

    /// <summary>brief-em3d-76 R-em3d76-4a — the symmetry plane face <paramref name="spelled"/> lies in wholly, or null.</summary>
    public static C3dSymmetryPlane? OnSymmetryPlane(C3dDocument doc, C3dElaboration e, string spelled)
    {
        if (doc.SymmetryPlanes.Count == 0) return null;
        var solids = e.Solids.Where(s => !CircuitRF.Design.Thermal.ThermalMaterials.NotMeshed(s.Role, s.Material)).ToList();
        if (Thermal.ThermalLowerings.FacePieces(doc, e, solids, spelled, out _, out _) is not { Count: > 0 } pieces) return null;
        double m = 1e-6 / doc.DbuPerMicron;
        foreach (var sp in doc.SymmetryPlanes)
        {
            double at = sp.At * m;
            bool all = pieces.All(pc =>
            {
                var b = pc.Bounds();
                var (lo, hi) = sp.Axis switch { C3dAxis.X => (b.X0, b.X1), C3dAxis.Y => (b.Y0, b.Y1), _ => (b.Z0, b.Z1) };
                return Math.Abs(lo - at) <= m && Math.Abs(hi - at) <= m;
            });
            if (all) return sp;
        }
        return null;
    }

    /// <summary>The elaborated solid of that name, or null.</summary>
    /// <summary>Only a solid a thermal run MESHES counts (not air, not vacuum: ThermalMaterials.NotMeshed, the lowering's own
    /// rule), so a place check passes is a place the run can put something on.</summary>
    private static Em3dSolid? SolidNamed(C3dElaboration e, string name) => Meshed(e).FirstOrDefault(s => s.Name == name);

    /// <summary>The solids a thermal run meshes: not air, and not a bond wire's (a wire is solved as a 1D chain — the lowering's
    /// own rule, so a place on a wire's solid is refused here as the run refuses it).</summary>
    private static IEnumerable<Em3dSolid> Meshed(C3dElaboration e)
    {
        var wires = new HashSet<string>(e.Wires.SelectMany(Thermal.ThermalWireLowering.SolidsOf).Concat(e.DrawnWires.Keys), StringComparer.Ordinal);
        return e.Solids.Where(s => !Thermal.ThermalMaterials.NotMeshed(s.Role, s.Material) && !wires.Contains(s.Name));
    }

    /// <summary>Each solid an ENABLED effective block replaces, and the block — the lowering's own replacement, so a place on
    /// one is refused here as the run refuses it. Empty with no technology (the run refuses that itself).</summary>
    private static Dictionary<string, string> ReplacedBy(C3dDocument doc, C3dElaboration e)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.Technology is null) return map;
        foreach (var b in doc.EffectiveBlocks.Where(b => b.Enabled))
            if (Thermal.ThermalEffectiveBlocks.Lower(doc, e, b, e.Technology, 0, out _) is { } low)
                foreach (string r in low.Removed) map.TryAdd(r, b.Name);
        return map;
    }

    /// <summary>Why face <paramref name="spelled"/> (<c>object/face</c>) is unusable because an enabled block replaces its solid, or
    /// null.</summary>
    private static string? ReplacedFace(C3dDocument doc, string spelled, IReadOnlyDictionary<string, string> replaced)
    {
        int slash = spelled.LastIndexOf('/');
        if (slash <= 0 || replaced.Count == 0) return null;
        string solid = C3dKernelUse.Resolve(doc, spelled[..slash], spelled[(slash + 1)..]).Item1;
        return replaced.TryGetValue(solid, out string? blk) ? $"it is on '{solid}', which effective block '{blk}' replaces" : null;
    }

    /// <summary>Why solid <paramref name="name"/> is not meshed although it exists — a wire, air, a replaced one — or the
    /// generic sentence.</summary>
    private static string NotMeshedWhy(C3dElaboration e, string name, IReadOnlyDictionary<string, string> replaced)
        => replaced.TryGetValue(name, out string? blk) ? $"'{name}' is replaced by effective block '{blk}'"
         : e.Solids.Any(s => s.Name == name) ? $"'{name}' is not meshed in a thermal run (a bond wire is solved as a 1D chain; air is not meshed)"
         : $"'{name}' is no solid of this 3D view";

    private static IReadOnlyList<string> SomeSolids(C3dElaboration e) => [.. Meshed(e).Select(s => s.Name).Take(8)];

    private static bool WireExists(C3dElaboration e, string name)
        => e.Wires.Any(w => w.Name == name || w.Array == name) || e.DrawnWires.ContainsKey(name) || e.Solids.Any(s => s.Name == name && s.Primitive is Em3dSweep);

    /// <summary>Why <c>object/face</c> names no face of <paramref name="e"/>, or null when it does. The object part may hold
    /// '/' (<c>U1/die/zmax</c>); the face is after the last one. The fold rule applies: <c>zmax</c> covers <c>zmax#1</c>.</summary>
    public static string? FaceProblem(C3dDocument doc, C3dElaboration e, string spelled)
    {
        int slash = spelled.LastIndexOf('/');
        if (slash <= 0 || slash == spelled.Length - 1) return "a face is spelled object/face";
        var (obj, face) = C3dKernelUse.Resolve(doc, spelled[..slash], spelled[(slash + 1)..]);
        if (string.Equals(obj, C3dValidation.ReservedName, StringComparison.OrdinalIgnoreCase))
            return "a thermal run has no air box: it meshes solids only";
        if (SolidNamed(e, obj) is null && e.Solids.Any(s => s.Name == obj))
            return $"'{obj}' is air or vacuum, which a thermal run does not mesh";
        if (SolidNamed(e, obj) is null || !e.Provenance.TryGetValue(obj, out var prov))
            return $"'{obj}' is no solid of this 3D view";
        if (prov.FaceNames.Count == 0) return null;     // a kernel solid whose faces only the kernel knows: brief 74's
        if (prov.FaceNames.Any(n => C3dKernelUse.Covers(face, n))) return null;
        return $"'{obj}' has no face '{face}' (it has {string.Join(", ", prov.FaceNames.Distinct().Take(12))})";
    }

    /// <summary>The points a sheet's containment is tested at, world metres: its corners (or outline vertices) and its
    /// centroid. Null for a sheet with no shape.</summary>
    private static List<Point3>? SheetSamples(C3dHeatSheet s, double m)
    {
        var uv = new List<(double U, double V)>();
        if (s.Rect is { } r)
        {
            double u0 = r.Min.U, v0 = r.Min.V, u1 = r.Min.U + r.Size.U, v1 = r.Min.V + r.Size.V;
            uv.AddRange([(u0, v0), (u1, v0), (u1, v1), (u0, v1)]);
        }
        else uv.AddRange(s.Outline.Select(p => ((double)p.U, (double)p.V)));
        if (uv.Count == 0) return null;
        uv.Add((uv.Average(p => p.U), uv.Average(p => p.V)));
        double w = s.Offset;
        return [.. uv.Select(p => s.Plane switch
        {
            C3dPlane.XY => new Point3(p.U * m, p.V * m, w * m),
            C3dPlane.YZ => new Point3(w * m, p.U * m, p.V * m),
            _           => new Point3(p.U * m, w * m, p.V * m),
        })];
    }

    /// <summary>Whether <paramref name="q"/> lies inside or on <paramref name="p"/> — exact for a box, a z-extruded polygon
    /// and a cylinder; the bounding box otherwise.</summary>
    public static bool Inside(Em3dPrimitive p, Point3 q, double tol)
    {
        switch (p)
        {
            case Em3dBox b:
                return q.X >= b.Min.X - tol && q.X <= b.Max.X + tol && q.Y >= b.Min.Y - tol && q.Y <= b.Max.Y + tol &&
                       q.Z >= b.Min.Z - tol && q.Z <= b.Max.Z + tol;
            case Em3dExtrudedPolygon x:
                if (q.Z < x.ZBottom - tol || q.Z > x.ZTop + tol) return false;
                if (!InPolygon(x.Outline, q.X, q.Y, tol)) return false;
                return !x.Holes.Any(h => InPolygon(h, q.X, q.Y, -tol));
            case Em3dCylinder c:
            {
                double ax = c.AxisEnd.X - c.AxisStart.X, ay = c.AxisEnd.Y - c.AxisStart.Y, az = c.AxisEnd.Z - c.AxisStart.Z;
                double len2 = ax * ax + ay * ay + az * az;
                if (len2 == 0) return false;
                double dx = q.X - c.AxisStart.X, dy = q.Y - c.AxisStart.Y, dz = q.Z - c.AxisStart.Z;
                double t = (dx * ax + dy * ay + dz * az) / len2;
                double len = Math.Sqrt(len2);
                if (t * len < -tol || t * len > len + tol) return false;
                double rx = dx - t * ax, ry = dy - t * ay, rz = dz - t * az;
                return Math.Sqrt(rx * rx + ry * ry + rz * rz) <= c.Radius + tol;
            }
            default:
                var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(p);
                return q.X >= x0 - tol && q.X <= x1 + tol && q.Y >= y0 - tol && q.Y <= y1 + tol && q.Z >= z0 - tol && q.Z <= z1 + tol;
        }
    }

    /// <summary>Point in polygon, with points within <paramref name="tol"/> of an edge counted inside (a negative tol counts
    /// them outside — a hole's own edge belongs to the solid).</summary>
    private static bool InPolygon(IReadOnlyList<Point2> ring, double x, double y, double tol)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (SegmentDistance(a, b, x, y) <= Math.Abs(tol)) return tol >= 0;
        }
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static double SegmentDistance(Point2 a, Point2 b, double x, double y)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / l2, 0, 1);
        double px = a.X + t * dx - x, py = a.Y + t * dy - y;
        return Math.Sqrt(px * px + py * py);
    }

    private static bool Touch((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a,
                              (double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, double tol)
        => a.X0 <= b.X1 + tol && b.X0 <= a.X1 + tol && a.Y0 <= b.Y1 + tol && b.Y0 <= a.Y1 + tol && a.Z0 <= b.Z1 + tol && b.Z0 <= a.Z1 + tol;

    // ── 3. A thermal setup ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What is wrong with thermal setup <paramref name="name"/> over <paramref name="doc"/> (R-em3d73-6a). <paramref name="e"/>
    /// may be null (the document did not elaborate): the checks that need solids are then skipped, since elaboration's own
    /// refusal is already the finding. <paramref name="resolution"/> is the document's resolved scope.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Setup(string name, EmSetup setup, C3dDocument doc, C3dElaboration? e,
                                                  C3dResolution resolution)
    {
        var found = new List<Diagnostic>();
        var t = setup.Thermal ?? new CemThermal();

        // a key no section reads — a typo ("PerDecde", "Ambient") — is named, at whatever depth it sits
        string at = $"Thermal setup '{name}'";
        Unread(t.Unread, at, found);
        foreach (var x in t.Sources ?? []) Unread(x.Unread, $"{at}'s source '{x.Name}'", found);
        foreach (var x in t.Boundaries ?? []) Unread(x.Unread, $"{at}'s boundary on '{x.Face}'", found);
        foreach (var x in t.Sweep ?? []) Unread(x.Unread, $"{at}'s sweep of '{x.Var}'", found);
        foreach (var x in t.Currents ?? [])
        {
            Unread(x.More, $"{at}'s current of {x.Subject}", found);
            Unread(x.FromCircuit?.Unread, $"{at}'s FromCircuit", found);
            foreach (var h in x.Harmonics ?? []) Unread(h.Unread, $"{at}'s harmonic {h.N} of {x.Subject}", found);
        }
        Unread(t.Mesh?.Unread, $"{at}'s Mesh", found);
        Unread(t.Balance?.Unread, $"{at}'s Balance", found);
        Unread(t.Submodel?.Unread, $"{at}'s Submodel", found);
        Unread(t.Rth?.Unread, $"{at}'s Rth", found);
        Unread(t.Zth?.Unread, $"{at}'s Zth", found);
        Unread(t.Pulse?.Unread, $"{at}'s Pulse", found);

        // A sink: a problem with no fixed-temperature or convection face has no steady state (overview §1b).
        // (a submodel's cut faces are its sink, so it may state none of its own)
        var boundaries = t.Boundaries ?? [];
        if (boundaries.Count == 0 && t.Submodel is null) found.Add(D.NoSink(name));
        var replaced = e is { Ok: true } && boundaries.Count > 0 ? ReplacedBy(doc, e) : [];
        foreach (var b in boundaries)
        {
            string where = $"The {b.Kind} boundary on '{b.Face}'";
            if (b.Face != ExposedFaces && e is { Ok: true } && (ReplacedFace(doc, b.Face, replaced) ?? FaceProblem(doc, e, b.Face)) is { } why)
                found.Add(D.BoundaryFace(name, b.Face, why));
            else if (b.Face != ExposedFaces && e is { Ok: true } && OnSymmetryPlane(doc, e, b.Face) is { } plane)
                found.Add(D.Symmetry($"Thermal setup '{name}' puts a {b.Kind} boundary on '{b.Face}', which lies on the symmetry plane " +
                                     $"{plane.Axis}: a mirror plane is insulated by definition. Remove the boundary, or the plane"));
            if (b.Kind == ThermalBoundaryKind.FixedT) Value(b.TempC, "TempC", ThermalQuantity.Temperature);
            else { Value(b.H, "H", ThermalQuantity.Plain); Value(b.AmbientC, "AmbientC", ThermalQuantity.Temperature); }

            void Value(string? text, string key, ThermalQuantity q)
            {
                if (string.IsNullOrWhiteSpace(text)) found.Add(D.BoundaryValue(name, $"{where} states no {key}"));
                else if (Unresolvable(text, q, resolution) is { } err) found.Add(D.BoundaryValue(name, $"{where} has {key} '{text}', which cannot be read: {err}"));
            }
        }

        // Source overrides name a source.
        foreach (var s in t.Sources ?? [])
        {
            if (!doc.HeatSources.Any(h => h.Name == s.Name))
                found.Add(D.SetupSource(name, $"overrides the power of '{s.Name}', which is no heat source of this 3D view" +
                                             (doc.HeatSources.Count == 0 ? " (it has none)" : $" (it has {string.Join(", ", doc.HeatSources.Select(h => $"'{h.Name}'"))})")));
            else if (Unresolvable(s.Power ?? "", ThermalQuantity.Power, resolution) is { } why)
                found.Add(D.SetupSource(name, $"gives '{s.Name}' the power '{s.Power}', which cannot be read: {why}"));
        }
        foreach (var h in doc.HeatSources)
            if (h.Power is null && !(t.Sources ?? []).Any(s => s.Name == h.Name))
                found.Add(D.SetupSource(name, $"gives heat source '{h.Name}' no power, and the source states no default Power"));
            else if (h.Power is { } own && !(t.Sources ?? []).Any(s => s.Name == h.Name) && Unparsable(own, ThermalQuantity.Power) is null &&
                     Unresolvable(own, ThermalQuantity.Power, resolution) is { } why)
                found.Add(D.SetupSource(name, $"uses heat source '{h.Name}''s own power '{own}', which cannot be read: {why}"));

        // brief-em3d-77 R-em3d77-1 — each current names a port of this view, a Dc that parses, and faces that exist; a wire's
        // convection names its ambient; a bond's resistances parse
        var currents = t.Currents ?? [];
        List<string>? arrayNames = null;
        foreach (var c in currents)
        {
            bool harmonics = c.Harmonics is { Count: > 0 };
            string who = c.Subject;
            // brief-em3d-79 R-em3d79-1 — a circuit link states the circuit and nothing else: the ports' currents are its pins'
            if (c.FromCircuit is { } fc)
            {
                if (string.IsNullOrWhiteSpace(fc.Schematic))
                    found.Add(D.Current(name, "has a FromCircuit current naming no Schematic: name the .csch or .cnl whose HB drives the ports"));
                if (c.Port is not null || c.Array is not null || c.Dc is not null || c.F0 is not null || harmonics || c.EnterFace is not null || c.LeaveFace is not null)
                    found.Add(D.Current(name, "has a FromCircuit current that also states a Port, Array, Dc, F0, Harmonics or contact face: the circuit " +
                                              "gives every port its currents — port p is pin p of the instance — so the entry states the circuit only"));
                continue;
            }
            if (c.Port is not null && c.Array is not null)
                found.Add(D.Current(name, $"has a current naming both port {c.Port} and array '{c.Array}'; an entry is one or the other"));
            else if (c.Port is null && c.Array is null)
                found.Add(D.Current(name, "has a current naming no Port and no Array"));
            if (c.Port is { } number && !doc.Ports.Any(p => p.Number == number))
                found.Add(D.Current(name, $"gives port {number} a current, and this 3D view has no port {number}" +
                                          (doc.Ports.Count == 0 ? " (it has none)" : $" (it has {string.Join(", ", doc.Ports.Select(p => p.Number))})")));
            // brief-em3d-78 R-em3d78-2 — an array entry states harmonics only, and names an array the model has
            if (c.Array is { } array && c.Port is null)
            {
                if (!harmonics) found.Add(D.Current(name, $"gives array '{array}' no Harmonics: an array entry states harmonic currents"));
                if (c.Dc is not null || c.EnterFace is not null || c.LeaveFace is not null)
                    found.Add(D.Current(name, $"gives array '{array}' a Dc or contact faces: a DC current is a port's, and the conduction solve shares it " +
                                              "among the wires — state it on the port"));
                if (e is { Ok: true })
                {
                    arrayNames ??= [.. Thermal.ThermalRfPlan.Group(e, [.. e.Wires.Select(w => w.Name)]).Select(a => a.Name)];
                    if (!arrayNames.Contains(array))
                        found.Add(D.Current(name, $"gives array '{array}' harmonic currents, and there is no such wire array " +
                                                  (arrayNames.Count == 0 ? "(this 3D view has none)" : $"(it has {string.Join(", ", arrayNames.Select(a => $"'{a}'"))})")));
                }
            }
            else if (string.IsNullOrWhiteSpace(c.Dc))
            {
                if (!harmonics) found.Add(D.Current(name, $"gives {who} no Dc current"));
            }
            else if (Unresolvable(c.Dc, ThermalQuantity.Current, resolution) is { } err) found.Add(D.Current(name, $"gives {who} the Dc '{c.Dc}', which cannot be read: {err}"));
            foreach (var (key, face) in new[] { ("EnterFace", c.EnterFace), ("LeaveFace", c.LeaveFace) })
                if (face is not null && c.Array is null && e is { Ok: true } && FaceProblem(doc, e, face) is { } why)
                    found.Add(D.Current(name, $"names '{face}' as {who}'s {key}, which does not exist: {why}"));

            // brief-em3d-78 R-em3d78-1 — the harmonics: an F0, and every one Peak or Rms, stated (D8: never inferred)
            if (!harmonics) continue;
            if (string.IsNullOrWhiteSpace(c.F0))
                found.Add(D.Current(name, $"gives {who} harmonic currents and no F0: state the fundamental frequency, Hz"));
            else if (Unresolvable(c.F0, ThermalQuantity.Frequency, resolution) is { } fe) found.Add(D.Current(name, $"gives {who} the F0 '{c.F0}', which cannot be read: {fe}"));
            for (int i = 0; i < c.Harmonics!.Count; i++)
            {
                var h = c.Harmonics[i];
                string entry = $"{who}'s harmonic entry {i + 1} (N = {h.N})";
                if (h.N < 1) found.Add(D.Current(name, $"gives {entry} a harmonic number below 1"));
                if (h.As is null)
                    found.Add(D.Current(name, $"gives {entry} no As: state whether its Amp is Peak or Rms — an amplitude is never assumed to be either"));
                if (string.IsNullOrWhiteSpace(h.Amp)) found.Add(D.Current(name, $"gives {entry} no Amp"));
                else if (Unresolvable(h.Amp, ThermalQuantity.Current, resolution) is { } ae) found.Add(D.Current(name, $"gives {entry} the Amp '{h.Amp}', which cannot be read: {ae}"));
            }
            foreach (var dupN in c.Harmonics.GroupBy(h => h.N).Where(g => g.Count() > 1))
                found.Add(D.Current(name, $"gives {who} harmonic {dupN.Key} {dupN.Count()} times; state each harmonic once"));
        }
        if (currents.Count(c => c.FromCircuit is not null) is var links and > 0)
        {
            if (links > 1) found.Add(D.Current(name, $"has {links} FromCircuit currents; one circuit drives the ports"));
            if (currents.Any(c => c.FromCircuit is null && (c.Dc is not null || c.Harmonics is { Count: > 0 })))
                found.Add(D.Current(name, "takes its currents from a circuit and also states a Dc or Harmonics per port: the circuit gives every " +
                                          "port its currents, so remove the per-port entries (or the FromCircuit one)"));
            if (t.Sweep is { Count: > 0 })
                found.Add(D.Current(name, "takes its currents from a circuit and states a Sweep of its own: the run is solved at every point of the " +
                                          "circuit's HB sweep, on its axes — sweep the circuit instead"));
        }
        foreach (var dup in currents.Where(c => c.Port is not null).GroupBy(c => c.Port).Where(g => g.Count() > 1))
            found.Add(D.Current(name, $"gives port {dup.Key} {dup.Count()} currents; a port carries one"));
        foreach (var dup in currents.Where(c => c.Array is not null && c.Port is null).GroupBy(c => c.Array).Where(g => g.Count() > 1))
            found.Add(D.Current(name, $"gives array '{dup.Key}' {dup.Count()} currents; state its harmonics in one entry"));
        if (t.Submodel is not null && currents.Count > 0)
            found.Add(D.Current(name, "is a submodel and gives ports currents; a submodel carries none in this version — run them in the whole-model setup"));
        if (t.WireConvectionH is { } wh && Unresolvable(wh, ThermalQuantity.Plain, resolution) is { } whe) found.Add(D.Current(name, $"has WireConvectionH '{wh}', which cannot be read: {whe}"));
        if (t.WireConvectionH is not null && string.IsNullOrWhiteSpace(t.WireAmbientC))
            found.Add(D.Current(name, "states WireConvectionH and no WireAmbientC: say what temperature a wire in air loses its heat to"));
        else if (t.WireAmbientC is { } wa && Unresolvable(wa, ThermalQuantity.Temperature, resolution) is { } wae) found.Add(D.Current(name, $"has WireAmbientC '{wa}', which cannot be read: {wae}"));
        foreach (var (key, text) in new[] { ("BondThermalResistance", t.BondThermalResistance), ("BondElectricalResistance", t.BondElectricalResistance) })
            if (text is not null && Unresolvable(text, ThermalQuantity.Plain, resolution) is { } be) found.Add(D.Current(name, $"has {key} '{text}', which cannot be read: {be}"));
        if (currents.Count > 0 && e is { Ok: true })
            foreach (var sol in e.Solids.Where(x => x.Role == Em3dRole.Conductor && !CircuitRF.Design.Thermal.ThermalMaterials.NotMeshed(x.Role, x.Material)))
                if (Thermal.ThermalMaterials.For(e, sol.Name, sol.Material) is { } rec && ThermalProperties.SigmaAt(rec.Material, 20) is null)
                    found.Add(D.Current(name, $"sends current through the conductors, and '{sol.Name}''s material '{rec.Material.Name}' states no electrical " +
                                              "conductivity (Sigma20 or SigmaVsTemp)"));

        // Every meshed solid's material states k (R-em3d73-1c): never a default. Air is not meshed. brief-em3d-74 — the run's
        // own lookup, which looks through a technology record stating no k to a same-name library record that does.
        // brief-em3d-76: each solid against its OWN technology — a placed layout's materials are its technology's
        if (e is { Ok: true, Technology: not null })
        {
            var missing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var read = new HashSet<TechMaterial>(ReferenceEqualityComparer.Instance);
            foreach (var s in e.Solids)
            {
                if (Thermal.ThermalMaterials.NotMeshed(s.Role, s.Material)) continue;
                var (tech, baseName) = Thermal.ThermalMaterials.Source(e, s.Name, s.Material);
                if (tech?.FindMaterial(baseName) is not { } tm) continue;      // undefined: elaboration's own refusal says
                if (Thermal.ThermalMaterials.Find(tech, baseName) is { } rec) read.Add(rec.Material);
                else (missing.TryGetValue(tm.Name, out var l) ? l : missing[tm.Name] = []).Add(s.Name);
            }
            foreach (var (material, objects) in missing)
                found.Add(D.MaterialK(name, material, objects));
            // the records the run will READ, held to the materials' own rules (a table running backwards, a k of zero): a
            // .ctech's check says so too, but a .c3d's check is what stands between the record and the solver
            // (one at a time: two technologies' same-name records are not a duplicate)
            foreach (var m in read)
                foreach (var problem in MaterialValidation.Validate([m]).Where(p => p.Severity == DiagnosticSeverity.Error))
                    found.Add(D.MaterialInvalid(name, problem.Message));
        }

        // The sweep: at most two axes, a variable of the document that no geometry reads.
        var sweep = t.Sweep ?? [];
        if (sweep.Count > MaxSweepAxes) found.Add(D.Sweep(name, $"states {sweep.Count} sweep axes; a thermal setup sweeps at most {MaxSweepAxes}"));
        foreach (var s in sweep)
        {
            if (string.IsNullOrWhiteSpace(s.Var) || !resolution.IsDefined(s.Var))
                found.Add(D.Sweep(name, $"sweeps '{s.Var}', which is no variable of this 3D view; a sweep variable is one of its VARs or its cell's parameters"));
            else if (GeometryReader(doc, resolution, s.Var) is { } reader)
                found.Add(D.SweepGeometry(name, s.Var, reader));
            if (s.Points < 1) found.Add(D.Sweep(name, $"sweeps '{s.Var}' over {s.Points} points; at least 1"));
            foreach (var (key, text) in new[] { ("Start", s.Start), ("Stop", s.Stop) })
                if (Unresolvable(text ?? "", ThermalQuantity.Any, resolution) is { } why)
                    found.Add(D.Sweep(name, $"sweeps '{s.Var}' from a {key} '{text}' that cannot be read: {why}"));
        }
        foreach (var dup in sweep.GroupBy(s => s.Var, StringComparer.Ordinal).Where(g => g.Count() > 1))
            found.Add(D.Sweep(name, $"sweeps '{dup.Key}' on {dup.Count()} axes; a variable is one axis"));

        // the Mesh and Balance numbers: refused here as the mesher and the solver would refuse them, not after meshing
        if (t.Mesh is { } tm0)
        {
            if (tm0.Order is { } order && order is not (1 or 2)) found.Add(D.Mesh(name, $"has Mesh.Order {order}; a thermal mesh is order 1 or 2"));
            if (tm0.Grading is { } g && !(g > 1 && double.IsFinite(g))) found.Add(D.Mesh(name, $"has Mesh.Grading {Num(g)}; it must be above 1"));
            if (tm0.SizeFromSources is { } sfs && !(sfs > 0 && double.IsFinite(sfs)))
                found.Add(D.Mesh(name, $"has Mesh.SizeFromSources {Num(sfs)}; it is a number of elements, above 0"));
            if (tm0.MinThroughThickness is { } mtt && mtt < 1)
                found.Add(D.Mesh(name, $"has Mesh.MinThroughThickness {mtt}; it is a number of elements, at least 1"));
        }
        if (t.Balance is { } bal)
        {
            if (bal.Tolerance is { } btol && !(btol > 0 && double.IsFinite(btol))) found.Add(D.Mesh(name, $"has Balance.Tolerance {Num(btol)}; it must be above 0"));
            if (bal.MaxIterations is { } its && its < 1) found.Add(D.Mesh(name, $"has Balance.MaxIterations {its}; at least 1"));
        }

        // brief-em3d-76 R-em3d76-3a — a submodel names a whole-model thermal setup and a mesh region, and states no sweep
        if (t.Submodel is { } sm)
        {
            var others = C3dSetups.Read(doc);
            var from = others.FirstOrDefault(o => o.Setup?.Name == sm.From)?.Setup;
            if (string.IsNullOrWhiteSpace(sm.From) || sm.From == name)
                found.Add(D.Submodel(name, "names no other setup as its From; a submodel is cut from a whole-model thermal setup"));
            else if (from is null)
                found.Add(D.Submodel(name, $"is cut from '{sm.From}', which is no embedded setup of this 3D view"));
            else if (from.Problem3D != Em3dProblemType.Thermal)
                found.Add(D.Submodel(name, $"is cut from '{sm.From}', which is not a thermal setup"));
            else if (from.Thermal?.Submodel is not null)
                found.Add(D.Submodel(name, $"is cut from '{sm.From}', which is itself a submodel; cut from the whole-model setup"));
            if (!doc.MeshRegions.Any(r => r.Name == sm.Region))
                found.Add(D.Submodel(name, $"names the Region '{sm.Region}', which is no mesh region of this 3D view" +
                                           (doc.MeshRegions.Count == 0 ? " (it has none)" : $" (it has {string.Join(", ", doc.MeshRegions.Select(r => $"'{r.Name}'"))})")));
            if (sweep.Count > 0)
                found.Add(D.Submodel(name, "states a Sweep; a submodel runs at the points its From setup's result carries"));
        }

        // Measures parse, and name probes that exist.
        foreach (string measure in t.Measures ?? [])
            if (MeasureProblem(measure, doc, resolution) is { } why) found.Add(D.Measure(name, measure, why));

        // brief-em3d-80 — the Rth matrix, Z_th and the pulse train name what exists, and Z_th's materials state a heat capacity
        SmallSignal(name, t, doc, e, resolution, found);

        return found;
    }

    /// <summary>brief-em3d-80 R-em3d80-1a/-2a/-2b/-4a — what <see cref="Setup"/> checks of a setup's Rth, Zth and Pulse.</summary>
    private static void SmallSignal(string name, CemThermal t, C3dDocument doc, C3dElaboration? e, C3dResolution resolution, List<Diagnostic> found)
    {
        var sources = doc.HeatSources.Select(h => h.Name).ToList();
        string Have() => sources.Count == 0 ? " (it has none)" : $" (it has {string.Join(", ", sources.Select(n => $"'{n}'"))})";
        void Names(List<string>? names, string key)
        {
            if (sources.Count == 0) { found.Add(D.SmallSignal(name, $"states {key}, and this 3D view has no heat source to drive")); return; }
            var list = names ?? [ThermalNamesConverter.All];
            if (list.Count == 0) found.Add(D.SmallSignal(name, $"lists no source in {key}.Sources: name them, or write \"*\" for every one"));
            if (list.Contains(ThermalNamesConverter.All) && list.Count > 1)
                found.Add(D.SmallSignal(name, $"lists \"*\" and names in {key}.Sources: \"*\" already means every heat source"));
            foreach (string n in list.Where(n => n != ThermalNamesConverter.All && !sources.Contains(n)))
                found.Add(D.SmallSignal(name, $"lists '{n}' in {key}.Sources, which is no heat source of this 3D view{Have()}"));
            foreach (var dup in list.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
                found.Add(D.SmallSignal(name, $"lists '{dup.Key}' {dup.Count()} times in {key}.Sources"));
        }
        if (t.Rth is { } rth) Names(rth.Sources, "Rth");
        if (t.Zth is { } z)
        {
            Names(z.Sources, "Zth");
            foreach (string probe in z.Probes ?? [])
            {
                var p = doc.Probes.FirstOrDefault(x => x.Name == probe);
                if (p is null)
                    found.Add(D.SmallSignal(name, $"reads the probe '{probe}' in Zth.Probes, which is no probe of this 3D view" +
                                                  (doc.Probes.Count == 0 ? " (it has none)" : $" (it has {string.Join(", ", doc.Probes.Select(x => $"'{x.Name}'"))})")));
                else if (p.Wire is not null)
                    found.Add(D.SmallSignal(name, $"reads the wire probe '{probe}' in Zth.Probes: a bond wire has no heat capacity in the frequency-domain " +
                                                  "solve, so Z_th reads only places in the meshed solids"));
            }
            double? lo = null, hi = null;
            foreach (var (key, text) in new[] { ("StartHz", z.StartHz), ("StopHz", z.StopHz) })
            {
                if (text is null) continue;
                double? v = Evaluate(resolution, text, out string? err, ThermalQuantity.Frequency);
                if (err is not null) { found.Add(D.SmallSignal(name, $"has Zth.{key} '{text}', which does not resolve: {err}")); continue; }
                if (!(v > 0)) found.Add(D.SmallSignal(name, $"has Zth.{key} '{text}' = {Num(v ?? double.NaN)}; a frequency here is a positive number of Hz (DC is always included)"));
                if (key == "StartHz") lo = v; else hi = v;
            }
            if ((lo ?? ZthDefaultStartHz) >= (hi ?? ZthDefaultStopHz))
                found.Add(D.SmallSignal(name, $"has a Zth band from {Num(lo ?? ZthDefaultStartHz)} Hz to {Num(hi ?? ZthDefaultStopHz)} Hz; StopHz must exceed StartHz"));
            if (z.PerDecade is < 1) found.Add(D.SmallSignal(name, $"has Zth.PerDecade {z.PerDecade}; at least 1"));

            // R-em3d80-2a — every meshed solid's material states ρ and c: never assumed
            if (e is { Ok: true, Technology: not null })
            {
                var missing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var sol in e.Solids)
                {
                    if (Thermal.ThermalMaterials.NotMeshed(sol.Role, sol.Material)) continue;
                    var (tech, baseName) = Thermal.ThermalMaterials.Source(e, sol.Name, sol.Material);
                    if (tech?.FindMaterial(baseName) is not { } tm) continue;
                    if (Thermal.ThermalMaterials.HeatCapacity(e, sol.Name, sol.Material) is null)
                        (missing.TryGetValue(tm.Name, out var l) ? l : missing[tm.Name] = []).Add(sol.Name);
                }
                foreach (var (material, objects) in missing) found.Add(D.MaterialHeat(name, material, objects));
            }
        }
        if (t.Pulse is { } pulse)
        {
            if (t.Zth is null)
                found.Add(D.SmallSignal(name, "states a Pulse and no Zth: a pulse train is answered from Z_th's Foster fit (there is no transient " +
                                              "solver) — add a Zth section"));
            foreach (var (key, text, required) in new[] { ("Period", pulse.Period, true), ("Duty", pulse.Duty, true), ("PeakPower", pulse.PeakPower, false) })
            {
                if (string.IsNullOrWhiteSpace(text)) { if (required) found.Add(D.SmallSignal(name, $"states a Pulse with no {key}")); continue; }
                var q = key == "Period" ? ThermalQuantity.Time : key == "Duty" ? ThermalQuantity.Plain : ThermalQuantity.Power;
                string? err = key == "Period" ? UnparsableTime(text) : Unresolvable(text, q, resolution);
                if (err is null && key == "Period" && Refs(SplitTime(text).Expr).Where(r => !resolution.IsDefined(r)).ToList() is { Count: > 0 } missing)
                    err = $"it reads {string.Join(", ", missing.Select(m => $"'{m}'"))}, which {(missing.Count == 1 ? "is no variable" : "are no variables")} of this 3D view";
                if (err is not null) { found.Add(D.SmallSignal(name, $"has Pulse.{key} '{text}', which cannot be read: {err}")); continue; }
                // the run's own range rules, where the value does not move with the sweep (a swept one is checked per point)
                if (key == "PeakPower" || Refs(key == "Period" ? SplitTime(text).Expr : SplitUnit(text).Expr).Overlaps((t.Sweep ?? []).Select(x => x.Var))) continue;
                double? v = key == "Period" ? EvaluateTime(resolution, text, out _) : Evaluate(resolution, text, out _, q);
                if (key == "Period" && v is { } per && !(per > 0))
                    found.Add(D.SmallSignal(name, $"has Pulse.Period '{text}' = {Num(per)} s; a period is positive"));
                if (key == "Duty" && v is { } duty && !(duty >= 0 && duty <= 1))
                    found.Add(D.SmallSignal(name, $"has Pulse.Duty '{text}' = {Num(duty)}; a duty lies between 0 and 1"));
            }
        }
    }

    /// <summary>
    /// Every expression a thermal setup states, with where it is and how to write it back: powers, boundary values, currents
    /// (Dc, F0, each harmonic's Amp), the wire and bond values, the sweep's ends, each measure's right-hand side, the Z_th band
    /// and the pulse. The ONE list the resolver's "used by", the Variables panel's uses and a VAR rename all walk, so a field
    /// added to the setup and missed here is missed by all three alike rather than by one silently. A sweep's Var is a bare
    /// name, not an expression, and is not here.
    /// </summary>
    public static IEnumerable<(string Path, string Text, Action<string> Set)> ExpressionFields(CemThermal t)
    {
        foreach (var x in t.Sources ?? [])
            if (x.Power is { Length: > 0 }) yield return ($"Sources[{x.Name}].Power", x.Power, v => x.Power = v);
        foreach (var b in t.Boundaries ?? [])
        {
            if (b.TempC is { Length: > 0 } tc) yield return ($"Boundaries[{b.Face}].TempC", tc, v => b.TempC = v);
            if (b.H is { Length: > 0 } h) yield return ($"Boundaries[{b.Face}].H", h, v => b.H = v);
            if (b.AmbientC is { Length: > 0 } a) yield return ($"Boundaries[{b.Face}].AmbientC", a, v => b.AmbientC = v);
        }
        foreach (var c in t.Currents ?? [])
        {
            string who = c.Port is { } port ? $"port {port}" : c.Array ?? "circuit";
            if (c.Dc is { Length: > 0 } dc) yield return ($"Currents[{who}].Dc", dc, v => c.Dc = v);
            if (c.F0 is { Length: > 0 } f0) yield return ($"Currents[{who}].F0", f0, v => c.F0 = v);
            foreach (var h in c.Harmonics ?? [])
                if (h.Amp is { Length: > 0 } amp) yield return ($"Currents[{who}].Harmonics[{h.N}].Amp", amp, v => h.Amp = v);
        }
        if (t.WireConvectionH is { Length: > 0 } wh) yield return ("WireConvectionH", wh, v => t.WireConvectionH = v);
        if (t.WireAmbientC is { Length: > 0 } wa) yield return ("WireAmbientC", wa, v => t.WireAmbientC = v);
        if (t.BondThermalResistance is { Length: > 0 } bt) yield return ("BondThermalResistance", bt, v => t.BondThermalResistance = v);
        if (t.BondElectricalResistance is { Length: > 0 } be) yield return ("BondElectricalResistance", be, v => t.BondElectricalResistance = v);
        foreach (var w in t.Sweep ?? [])
        {
            if (w.Start is { Length: > 0 }) yield return ($"Sweep[{w.Var}].Start", w.Start, v => w.Start = v);
            if (w.Stop is { Length: > 0 }) yield return ($"Sweep[{w.Var}].Stop", w.Stop, v => w.Stop = v);
        }
        if (t.Measures is { } measures)
            for (int i = 0; i < measures.Count; i++)
                if (measures[i].IndexOf('=') is > 0 and var eq)
                {
                    int k = i;
                    string lhs = measures[i][..eq];
                    yield return ($"Measures[{lhs.Trim()}]", measures[i][(eq + 1)..], v => measures[k] = lhs + "=" + v);
                }
        if (t.Zth is { } z)
        {
            if (z.StartHz is { Length: > 0 } zs) yield return ("Zth.StartHz", zs, v => z.StartHz = v);
            if (z.StopHz is { Length: > 0 } ze) yield return ("Zth.StopHz", ze, v => z.StopHz = v);
        }
        if (t.Pulse is { } pl)
        {
            if (pl.PeakPower is { Length: > 0 } pp) yield return ("Pulse.PeakPower", pp, v => pl.PeakPower = v);
            if (pl.Period is { Length: > 0 }) yield return ("Pulse.Period", pl.Period, v => pl.Period = v);
            if (pl.Duty is { Length: > 0 }) yield return ("Pulse.Duty", pl.Duty, v => pl.Duty = v);
        }
    }

    /// <summary>
    /// A setup renamed from <paramref name="from"/> to <paramref name="to"/>: every reference to it by name follows — each
    /// field plot pinned to it (brief-em3d-83) and each submodel cut From it (brief-em3d-76). The setup record itself is the
    /// caller's. Left alone, a pinned plot resolved to "setup missing" and a submodel refused, both after the rename looked done.
    /// </summary>
    public static void RenameSetupReferences(C3dDocument doc, string from, string to)
    {
        foreach (var plot in doc.FieldPlots)
            if (plot.Setup == from) plot.Setup = to;
        foreach (var (index, setup, t) in ThermalSetups(doc).ToList())
            if (t.Submodel is { } sm && sm.From == from)
            {
                sm.From = to;
                doc.Setups[index] = EmSetupPersistence.ToEmbedded(setup);
            }
    }

    /// <summary>The document's thermal setups, each with its index in <see cref="C3dDocument.Setups"/>: the embedded records whose
    /// Problem3D is Thermal, read (a record that does not read is passed over — check reports it).</summary>
    public static IEnumerable<(int Index, EmSetup Setup, CemThermal Thermal)> ThermalSetups(C3dDocument doc)
    {
        for (int i = 0; i < doc.Setups.Count; i++)
        {
            var element = doc.Setups[i];
            // told apart cheaply first: the resolver asks on every edit
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !element.EnumerateObject().Any(p => string.Equals(p.Name, "Problem3D", StringComparison.OrdinalIgnoreCase) &&
                                                     p.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                                                     string.Equals(p.Value.GetString(), "Thermal", StringComparison.OrdinalIgnoreCase)))
                continue;
            EmSetup setup;
            try { setup = EmSetupPersistence.FromEmbedded(element); }
            catch (Exception) { continue; }      // any reader refusal: C3dSetups.Read reports it
            if (setup.Thermal is { } t) yield return (i, setup, t);
        }
    }

    /// <summary>brief-em3d-80 — Z_th's band when the setup states none: a package's seconds to a channel's microseconds.</summary>
    public const double ZthDefaultStartHz = 0.01, ZthDefaultStopHz = 1e6;

    /// <summary>brief-em3d-80 — Z_th's frequencies per decade when the setup states none.</summary>
    public const int ZthDefaultPerDecade = 10;

    /// <summary>
    /// R-em3d73-5b — the first dimension that reads <paramref name="variable"/>, directly or through a VAR whose expression
    /// reads it, as <c>'item' path</c>; null when no geometry does. Instance overrides count: they size a child.
    /// </summary>
    public static string? GeometryReader(C3dDocument doc, C3dResolution resolution, string variable)
    {
        var reaching = new HashSet<string>(StringComparer.Ordinal) { variable };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var v in doc.Variables)
                if (!reaching.Contains(v.Name) && Refs(v.Expression).Overlaps(reaching)) { reaching.Add(v.Name); grew = true; }
        }
        foreach (var f in C3dBindings.Bound(doc))
            if (Refs(f.Expr.Expr).Overlaps(reaching)) return $"'{f.Item}' {f.Path} = {f.Expr.Expr}";
        foreach (var inst in doc.Instances)
            foreach (var (param, e) in inst.Params ?? [])
                if (Refs(e.Expr).Overlaps(reaching)) return $"'{inst.Name}' Params.{param} = {e.Expr}";
        _ = resolution;
        return null;
    }

    /// <summary>
    /// Why a measure cannot be evaluated, or null: <c>name = expression</c>, a name a VAR could have, an expression that
    /// parses, each <c>Tmax</c>/<c>Tmin</c>/<c>Tavg</c>/<c>T</c> called on exactly one probe of the document (<c>T</c> on a
    /// point or spot probe), and every other name a variable of the document.
    /// </summary>
    public static string? MeasureProblem(string measure, C3dDocument doc, C3dResolution resolution)
    {
        int eq = measure.IndexOf('=');
        if (eq <= 0 || eq + 1 < measure.Length && measure[eq + 1] == '=')
            return "a measure is written name = expression";
        string lhs = measure[..eq].Trim(), rhs = measure[(eq + 1)..].Trim();
        if (C3dResolver.ValidateName(lhs) is { } bad) return $"its name '{lhs}' cannot be used: {bad}";
        if (rhs.Length == 0) return "its expression is empty";
        Expr ast;
        try { ast = Parser.Parse(rhs); }
        catch (ExpressionException ex) { return $"its expression does not parse: {ex.Message}"; }

        var probes = doc.Probes.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var asProbe = new HashSet<string>(StringComparer.Ordinal);
        string? problem = null;
        Walk(ast);
        if (problem is not null) return problem;
        foreach (string r in AstWalker.CollectRefs(ast))
            if (!asProbe.Contains(r) && !resolution.IsDefined(r) && r != SymmetryFactorName)
                return probes.ContainsKey(r)
                    ? $"'{r}' is a probe; a probe is read through Tmax, Tmin, Tavg or T"
                    : $"'{r}' is neither a probe nor a variable of this 3D view";
        return null;

        void Walk(Expr e)
        {
            if (problem is not null) return;
            switch (e)
            {
                case CallExpr c when ProbeFunctions.Contains(c.Name, StringComparer.Ordinal):
                    if (c.Args.Length != 1 || c.Args[0] is not RefExpr { Name: var pn })
                    { problem = $"{c.Name} takes one probe, by name"; return; }
                    if (!probes.TryGetValue(pn, out var probe))
                    {
                        problem = $"{c.Name}({pn}) names no probe of this 3D view" +
                                  (probes.Count == 0 ? " (it has none)" : $" (it has {string.Join(", ", probes.Keys)})");
                        return;
                    }
                    if (c.Name == "T" && probe.Point is null && probe.Spot is null && probe.Stat is null)
                    { problem = $"T({pn}) reads one temperature, which only a point or spot probe, or a probe stating a Stat, has; use Tmax, Tmin or Tavg"; return; }
                    asProbe.Add(pn);
                    return;
                case CallExpr c: foreach (var a in c.Args) Walk(a); return;
                case UnaryExpr u: Walk(u.Operand); return;
                case BinaryExpr b: Walk(b.Left); Walk(b.Right); return;
                case CompareExpr cm: Walk(cm.Left); Walk(cm.Right); return;
                case LogicExpr l: Walk(l.Left); Walk(l.Right); return;
                case ConditionalExpr d: Walk(d.Condition); Walk(d.Then); Walk(d.Else); return;
            }
        }
    }

    private static HashSet<string> Refs(string expression)
    {
        try { return AstWalker.CollectRefs(Parser.Parse(expression)); }
        catch (ExpressionException) { return []; }
    }

    /// <summary>
    /// A power or temperature as a setup writes it — an expression, optionally followed by a spaced unit (<c>0.5 W</c>,
    /// <c>250 mW</c>) — evaluated in <paramref name="resolution"/>'s scope, in the base unit. Returns the value, or null with
    /// <paramref name="error"/> saying what does not resolve.
    /// </summary>
    public static double? Evaluate(C3dResolution resolution, string text, out string? error, ThermalQuantity quantity = ThermalQuantity.Any)
    {
        var (expr, unit) = SplitUnit(text);
        double scale = UnitScale(unit, quantity);
        if (double.IsNaN(scale)) { error = UnitRefusal(unit, quantity); return null; }
        Value v;
        try { v = resolution.Evaluate(expr, null); }
        catch (ExpressionException ex) { error = ex.Message; return null; }
        if (v.Kind != ValueKind.Real) { error = $"it is {v.Kind.ToString().ToLowerInvariant()}; it must be a real number"; return null; }
        error = null;
        return v.AsReal() * scale;
    }

    /// <summary>brief-em3d-80 — the time units a pulse's Period may end in (spaced), and their scale to seconds. The expression
    /// engine's unit table has no time units, so the pulse reads them itself.</summary>
    private static readonly Dictionary<string, double> TimeUnits = new(StringComparer.Ordinal)
    {
        ["s"] = 1, ["ms"] = 1e-3, ["us"] = 1e-6, ["µs"] = 1e-6, ["ns"] = 1e-9,
    };

    /// <summary>brief-em3d-80 — <paramref name="text"/> without a trailing spaced time unit, and that unit's scale to seconds (1
    /// when there is none).</summary>
    private static (string Expr, double Scale) SplitTime(string text)
    {
        string trimmed = text.Trim();
        int sp = trimmed.LastIndexOfAny([' ', '\t']);
        return sp > 0 && TimeUnits.TryGetValue(trimmed[(sp + 1)..], out double scale) ? (trimmed[..sp].TrimEnd(), scale) : (trimmed, 1);
    }

    /// <summary>brief-em3d-80 — a time in seconds: an expression, optionally followed by a spaced <c>s</c>, <c>ms</c>, <c>us</c>
    /// (<c>µs</c>) or <c>ns</c>.</summary>
    public static double? EvaluateTime(C3dResolution resolution, string text, out string? error)
    {
        var (expr, scale) = SplitTime(text);
        return Evaluate(resolution, expr, out error, ThermalQuantity.Time) * scale;
    }

    /// <summary>brief-em3d-80 — why <paramref name="text"/> cannot be a time here, or null.</summary>
    public static string? UnparsableTime(string text) => string.IsNullOrWhiteSpace(text) ? "it is empty" : Unparsable(SplitTime(text).Expr, ThermalQuantity.Time);

    private static (string Expr, string? Unit) SplitUnit(string text)
    {
        var (expr, unit) = Units.LiftInlineUnit(text.Trim());
        return (expr, unit);
    }

    /// <summary>Why <paramref name="text"/> cannot be an expression here (a trailing spaced unit allowed), or null.</summary>
    public static string? Unparsable(string text, ThermalQuantity quantity = ThermalQuantity.Any)
    {
        if (string.IsNullOrWhiteSpace(text)) return "it is empty";
        var (expr, unit) = SplitUnit(text);
        // the unit the run would refuse is refused here too, so check and the run agree ("30 dBm", "25 %", "85 m" as a temperature)
        if (double.IsNaN(UnitScale(unit, quantity))) return UnitRefusal(unit, quantity);
        try { Parser.Parse(expr); return null; }
        catch (ExpressionException ex) { return ex.Message; }
    }

    /// <summary>
    /// Why <paramref name="text"/> cannot be <paramref name="quantity"/> in <paramref name="resolution"/>'s scope, or null: it
    /// does not parse, its unit is not one of that quantity's, or a name it reads is no variable of the view — what the run
    /// would refuse at its first point, said before any meshing. A value is not EVALUATED here (a swept variable has no one
    /// value), only its names checked.
    /// </summary>
    public static string? Unresolvable(string text, ThermalQuantity quantity, C3dResolution resolution)
    {
        if (Unparsable(text, quantity) is { } why) return why;
        var missing = Refs(SplitUnit(text).Expr).Where(r => !resolution.IsDefined(r)).Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0 ? null
            : $"it reads {string.Join(", ", missing.Select(m => $"'{m}'"))}, which {(missing.Count == 1 ? "is no variable" : "are no variables")} of this 3D view";
    }

    /// <summary>
    /// A trailing unit's scale to the base unit (1 for none), or NaN for one that is not <paramref name="quantity"/>'s. The
    /// base symbols W and A carry no scale in the unit table (they are identity units there), so they are stated here. A bare
    /// SI prefix is a unit in that table ("m" is milli), so without the quantity "85 m" read as a temperature of 0.085 °C.
    /// </summary>
    private static double UnitScale(string? unit, ThermalQuantity quantity)
    {
        if (unit is null) return 1;
        double scale = unit is "W" or "A" ? 1 : Units.Scale(unit) ?? double.NaN;
        if (double.IsNaN(scale)) return double.NaN;
        string b = unit is "W" or "A" ? unit : Units.BaseUnit(unit);
        return quantity switch
        {
            ThermalQuantity.Any       => scale,
            ThermalQuantity.Power     => b == "W" ? scale : double.NaN,
            ThermalQuantity.Current   => b == "A" ? scale : double.NaN,
            ThermalQuantity.Frequency => b == "Hz" ? scale : double.NaN,
            _                         => double.NaN,           // a temperature, and every plain number, take no unit
        };
    }

    private static string UnitRefusal(string? unit, ThermalQuantity quantity) => quantity switch
    {
        ThermalQuantity.Temperature => $"it ends in '{unit}', and a temperature here takes no unit: it is a bare number of °C",
        ThermalQuantity.Power       => $"it ends in '{unit}', which is not a power: W, mW, uW or kW",
        ThermalQuantity.Current     => $"it ends in '{unit}', which is not a current: A, mA or uA",
        ThermalQuantity.Frequency   => $"it ends in '{unit}', which is not a frequency: Hz, kHz, MHz or GHz",
        ThermalQuantity.Plain       => $"it ends in '{unit}', and this value takes no unit: it is a bare number in the unit its key names",
        ThermalQuantity.Time        => $"it ends in '{unit}', which is not a time: s, ms, us or ns",
        _ => $"it ends in '{unit}', which is not a unit a thermal setup reads (temperatures are bare °C; powers W, mW, uW or kW; currents A, mA or uA)",
    };

    private static void Unread(Dictionary<string, System.Text.Json.JsonElement>? keys, string owner, List<Diagnostic> found)
    {
        if (keys is null) return;
        foreach (var k in keys.Keys) found.Add(C3dDiagnostics.UnreadKey(owner, k));
    }

    private static string Num(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    // ── The diagnostics ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The thermal findings' ids and sentences — one place, so a test and a reader spell them identically.</summary>
    public static class D
    {
        public const string SourceShapeId    = "c3d.thermal.source-shape";
        public const string SourceOutsideId  = "c3d.thermal.source-outside";
        public const string SourceSolidId    = "c3d.thermal.source-solid";
        public const string ProbeShapeId     = "c3d.thermal.probe-shape";
        public const string ProbeFaceId      = "c3d.thermal.probe-face";
        public const string ProbeSolidId     = "c3d.thermal.probe-solid";
        public const string ProbeWireId      = "c3d.thermal.probe-wire";
        public const string RegionShapeId    = "c3d.thermal.region-shape";
        public const string ContactShapeId   = "c3d.thermal.contact-shape";
        public const string ContactApartId   = "c3d.thermal.contact-apart";
        public const string NoSinkId         = "c3d.thermal.no-sink";
        public const string BoundaryFaceId   = "c3d.thermal.boundary-face";
        public const string BoundaryValueId  = "c3d.thermal.boundary-value";
        public const string SetupSourceId    = "c3d.thermal.setup-source";
        public const string MaterialKId      = "c3d.thermal.material-k";
        public const string SweepId          = "c3d.thermal.sweep";
        public const string SweepGeometryId  = "c3d.thermal.sweep-geometry";
        public const string MeasureId        = "c3d.thermal.measure";
        public const string BlockShapeId     = "c3d.thermal.effective-block";
        public const string SymmetryId       = "c3d.thermal.symmetry";
        public const string WireGroundId     = "c3d.thermal.wire-ground-plane";
        public const string SubmodelId       = "c3d.thermal.submodel";
        public const string CurrentId        = "c3d.thermal.current";
        public const string SmallSignalId    = "c3d.thermal.small-signal";
        public const string MaterialHeatId   = "c3d.thermal.material-heat";
        public const string MeshId           = "c3d.thermal.mesh";
        public const string ProbeNameId      = "c3d.thermal.probe-name";
        public const string MaterialInvalidId = "c3d.thermal.material-invalid";

        private static Diagnostic E(string id, string template, params (string, object?)[] args)
            => Diagnostic.Create(id, DiagnosticSeverity.Error, template, args);

        private static string List(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

        public static Diagnostic SourceShape(string name, string what)
            => E(SourceShapeId, "Heat source '{name}' {what}.", ("name", name), ("what", what));
        public static Diagnostic SourceOutside(string name, IReadOnlyList<string> touched)
            => E(SourceOutsideId, "Heat source '{name}' is not inside or on a solid{where}: a sheet source lies within one solid, which " +
                 "is what takes its heat.", ("name", name), ("where", touched.Count == 0 ? "" : $" (it only partly overlaps {List(touched)})"));
        public static Diagnostic SourceStraddles(string name, IReadOnlyList<string> solids)
            => E(SourceOutsideId, "Heat source '{name}' straddles {solids}: how its power would split between them is a guess, so it " +
                 "is refused. Split it into one source per solid.", ("name", name), ("solids", List(solids)));
        public static Diagnostic SourceSolid(string name, string solid, IReadOnlyList<string> some)
            => E(SourceSolidId, "Heat source '{name}' is spread through '{solid}', which is no solid of this 3D view (it has {some}).",
                 ("name", name), ("solid", solid), ("some", some.Count == 0 ? "none" : List(some)));
        public static Diagnostic ProbeShape(string name, string what)
            => E(ProbeShapeId, "Probe '{name}' {what}.", ("name", name), ("what", what));
        public static Diagnostic ProbeFace(string name, string face, string why)
            => E(ProbeFaceId, "Probe '{name}' reads the face '{face}', which does not exist: {why}. A face's name is kept through " +
                 "every edit; one that no longer exists is refused, never guessed.", ("name", name), ("face", face), ("why", why));
        public static Diagnostic ProbeSolid(string name, string solid, IReadOnlyList<string> some)
            => E(ProbeSolidId, "Probe '{name}' reads '{solid}', which is no solid of this 3D view (it has {some}).",
                 ("name", name), ("solid", solid), ("some", some.Count == 0 ? "none" : List(some)));
        public static Diagnostic ProbeWire(string name, string wire, IReadOnlyList<string> wires)
            => E(ProbeWireId, "Probe '{name}' reads the wire '{wire}', which is no bond wire of this 3D view ({wires}).",
                 ("name", name), ("wire", wire), ("wires", wires.Count == 0 ? "it has none" : "it has " + List(wires)));
        public static Diagnostic RegionShape(string name, string what)
            => E(RegionShapeId, "Mesh region '{name}' {what}.", ("name", name), ("what", what));
        public static Diagnostic ContactShape(string label, string what)
            => E(ContactShapeId, "The contact resistance {label} {what}.", ("label", label), ("what", what));
        public static Diagnostic ContactApart(string label, string why)
            => E(ContactApartId, "The contact resistance {label} overrides nothing: {why}.", ("label", label), ("why", why));
        public static Diagnostic NoSink(string setup)
            => E(NoSinkId, "Thermal setup '{setup}' has no FixedT or Convection boundary, so no heat can leave and there is no steady " +
                 "state. Fix a face's temperature (a heat sink's) or give faces a convection coefficient.", ("setup", setup));
        public static Diagnostic BoundaryFace(string setup, string face, string why)
            => E(BoundaryFaceId, "Thermal setup '{setup}' puts a boundary on '{face}', which does not exist: {why}.",
                 ("setup", setup), ("face", face), ("why", why));
        public static Diagnostic BoundaryValue(string setup, string what)
            => E(BoundaryValueId, "Thermal setup '{setup}': {what}.", ("setup", setup), ("what", what));
        public static Diagnostic SetupSource(string setup, string what)
            => E(SetupSourceId, "Thermal setup '{setup}' {what}.", ("setup", setup), ("what", what));
        public static Diagnostic MaterialK(string setup, string material, IReadOnlyList<string> objects)
            => E(MaterialKId, "Thermal setup '{setup}' meshes {objects}, made of '{material}', which states no thermal conductivity " +
                 "(ThermalK or ThermalKVsTemp). A thermal run never assumes one: add it to the material.",
                 ("setup", setup), ("material", material),
                 ("objects", List(objects.Take(4)) + (objects.Count > 4 ? $" and {objects.Count - 4} more" : "")));
        public static Diagnostic MaterialInvalid(string setup, string what)
            => E(MaterialInvalidId, "Thermal setup '{setup}' reads a material the solver cannot use: {what}", ("setup", setup), ("what", what));
        public static Diagnostic ProbeReplaced(string name, string solid, string block)
            => Diagnostic.Create(ProbeShapeId, DiagnosticSeverity.Warning,
                 "Probe '{name}' reads '{solid}', which effective block '{block}' replaces: it reads nothing while the block is enabled.",
                 ("name", name), ("solid", solid), ("block", block));
        public static Diagnostic ProbeName(string name, string why)
            => Diagnostic.Create(ProbeNameId, DiagnosticSeverity.Warning,
                 "Probe '{name}' can be read, but no measure can name it: {why}", ("name", name), ("why", why));
        public static Diagnostic Mesh(string setup, string what)
            => E(MeshId, "Thermal setup '{setup}' {what}.", ("setup", setup), ("what", what));
        public static Diagnostic Sweep(string setup, string what)
            => E(SweepId, "Thermal setup '{setup}' {what}.", ("setup", setup), ("what", what));
        public static Diagnostic SweepGeometry(string setup, string variable, string reader)
            => E(SweepGeometryId, "Thermal setup '{setup}' sweeps '{variable}', which geometry reads — first {reader}. A geometric sweep " +
                 "re-meshes at every point, which this version does not do: sweep a variable only powers, temperatures and " +
                 "coefficients read.", ("setup", setup), ("variable", variable), ("reader", reader));
        public static Diagnostic BlockShape(string name, string what)
            => E(BlockShapeId, "Effective block '{name}' {what}.", ("name", name), ("what", what));
        public static Diagnostic Symmetry(string what)
            => E(SymmetryId, "{what}.", ("what", what));
        public static Diagnostic WireGround(string what)
            => E(WireGroundId, "{what}.", ("what", what));
        public static Diagnostic Submodel(string setup, string what)
            => E(SubmodelId, "Thermal setup '{setup}' is a submodel that {what}.", ("setup", setup), ("what", what));
        public static Diagnostic Current(string setup, string what)
            => E(CurrentId, "Thermal setup '{setup}' {what}.", ("setup", setup), ("what", what));
        public static Diagnostic SmallSignal(string setup, string what)
            => E(SmallSignalId, "Thermal setup '{setup}' {what}.", ("setup", setup), ("what", what));
        public static Diagnostic MaterialHeat(string setup, string material, IReadOnlyList<string> objects)
            => E(MaterialHeatId, "Thermal setup '{setup}' computes Z_th, and meshes {objects}, made of '{material}', which states no heat " +
                 "capacity (DensityKgM3 and SpecificHeat). A thermal impedance never assumes one: add both to the material.",
                 ("setup", setup), ("material", material),
                 ("objects", List(objects.Take(4)) + (objects.Count > 4 ? $" and {objects.Count - 4} more" : "")));
        public static Diagnostic Measure(string setup, string measure, string why)
            => E(MeasureId, "Thermal setup '{setup}' has the measure '{measure}', which cannot be evaluated: {why}.",
                 ("setup", setup), ("measure", measure), ("why", why));
    }
}
