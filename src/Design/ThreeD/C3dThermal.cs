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
            if (h.Power is { } p && Unparsable(p) is { } why) found.Add(D.SourceShape(h.Name, $"has a Power '{p}' that does not parse: {why}"));
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

        for (int i = 0; i < doc.ContactResistances.Count; i++)
        {
            var c = doc.ContactResistances[i];
            string label = c.Between.Count == 2 ? $"between '{c.Between[0]}' and '{c.Between[1]}'" : $"#{i + 1}";
            if (c.Between.Count != 2 || c.Between.Any(string.IsNullOrWhiteSpace))
                found.Add(D.ContactShape(label, "must name exactly two objects in Between"));
            else if (string.Equals(c.Between[0], c.Between[1], StringComparison.Ordinal))
                found.Add(D.ContactShape(label, "names one object twice; a contact is between two"));
            if (!(c.ResistanceM2KW > 0) || !double.IsFinite(c.ResistanceM2KW))
                found.Add(D.ContactShape(label, $"has a resistance of {Num(c.ResistanceM2KW)} m²·K/W; it must be positive"));
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

        foreach (var h in doc.HeatSources)
        {
            if (h.Solid is { Length: > 0 } sname)
            {
                if (SolidNamed(e, sname) is null) found.Add(D.SourceSolid(h.Name, sname, SomeSolids(e)));
                continue;
            }
            if (h.Sheet is not { } sheet || SheetSamples(sheet, m) is not { Count: > 0 } pts) continue;
            var holders = e.Solids.Where(s => pts.All(q => Inside(s.Primitive, q, tol))).ToList();
            if (holders.Count > 0) continue;
            var touched = e.Solids.Where(s => pts.Any(q => Inside(s.Primitive, q, tol))).Select(s => s.Name).Distinct().ToList();
            found.Add(touched.Count >= 1 && pts.All(q => e.Solids.Any(s => Inside(s.Primitive, q, tol)))
                ? D.SourceStraddles(h.Name, touched)
                : D.SourceOutside(h.Name, touched));
        }

        foreach (var p in doc.Probes)
        {
            if (p.Face is { } face && FaceProblem(doc, e, face) is { } why) found.Add(D.ProbeFace(p.Name, face, why));
            if (p.Spot is { Face: var sf } && FaceProblem(doc, e, sf) is { } why2) found.Add(D.ProbeFace(p.Name, sf, why2));
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

        foreach (var c in doc.ContactResistances)
        {
            if (c.Between.Count != 2) continue;
            string label = $"between '{c.Between[0]}' and '{c.Between[1]}'";
            var a = SolidNamed(e, c.Between[0]);
            var b = SolidNamed(e, c.Between[1]);
            if (a is null || b is null)
            {
                found.Add(D.ContactApart(label, $"'{(a is null ? c.Between[0] : c.Between[1])}' is no solid of this 3D view"));
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
        var bounds = e.Solids.Where(s => !Thermal.ThermalMaterials.NotMeshed(s.Role, s.Material)).Select(s => Em3dProblem.Bounds(s.Primitive)).ToList();
        if (bounds.Count == 0) return null;
        return (bounds.Min(b => b.X0), bounds.Min(b => b.Y0), bounds.Min(b => b.Z0), bounds.Max(b => b.X1), bounds.Max(b => b.Y1), bounds.Max(b => b.Z1));
    }

    /// <summary>brief-em3d-76 R-em3d76-4a — the symmetry plane face <paramref name="spelled"/> lies in wholly, or null.</summary>
    public static C3dSymmetryPlane? OnSymmetryPlane(C3dDocument doc, C3dElaboration e, string spelled)
    {
        if (doc.SymmetryPlanes.Count == 0) return null;
        var solids = e.Solids.Where(s => !Thermal.ThermalMaterials.NotMeshed(s.Role, s.Material)).ToList();
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
    private static Em3dSolid? SolidNamed(C3dElaboration e, string name) => e.Solids.FirstOrDefault(s => s.Name == name);

    private static IReadOnlyList<string> SomeSolids(C3dElaboration e) => [.. e.Solids.Select(s => s.Name).Take(8)];

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

        // A sink: a problem with no fixed-temperature or convection face has no steady state (overview §1b).
        // (a submodel's cut faces are its sink, so it may state none of its own)
        var boundaries = t.Boundaries ?? [];
        if (boundaries.Count == 0 && t.Submodel is null) found.Add(D.NoSink(name));
        foreach (var b in boundaries)
        {
            string where = $"The {b.Kind} boundary on '{b.Face}'";
            if (b.Face != ExposedFaces && e is { Ok: true } && FaceProblem(doc, e, b.Face) is { } why)
                found.Add(D.BoundaryFace(name, b.Face, why));
            else if (b.Face != ExposedFaces && e is { Ok: true } && OnSymmetryPlane(doc, e, b.Face) is { } plane)
                found.Add(D.Symmetry($"Thermal setup '{name}' puts a {b.Kind} boundary on '{b.Face}', which lies on the symmetry plane " +
                                     $"{plane.Axis}: a mirror plane is insulated by definition. Remove the boundary, or the plane"));
            if (b.Kind == ThermalBoundaryKind.FixedT) Value(b.TempC, "TempC");
            else { Value(b.H, "H"); Value(b.AmbientC, "AmbientC"); }

            void Value(string? text, string key)
            {
                if (string.IsNullOrWhiteSpace(text)) found.Add(D.BoundaryValue(name, $"{where} states no {key}"));
                else if (Unparsable(text) is { } err) found.Add(D.BoundaryValue(name, $"{where} has {key} '{text}', which does not parse: {err}"));
            }
        }

        // Source overrides name a source.
        foreach (var s in t.Sources ?? [])
        {
            if (!doc.HeatSources.Any(h => h.Name == s.Name))
                found.Add(D.SetupSource(name, $"overrides the power of '{s.Name}', which is no heat source of this 3D view" +
                                             (doc.HeatSources.Count == 0 ? " (it has none)" : $" (it has {string.Join(", ", doc.HeatSources.Select(h => $"'{h.Name}'"))})")));
            else if (string.IsNullOrWhiteSpace(s.Power) || Unparsable(s.Power) is not null)
                found.Add(D.SetupSource(name, $"gives '{s.Name}' the power '{s.Power}', which does not parse: {Unparsable(s.Power ?? "") ?? "it is empty"}"));
        }
        foreach (var h in doc.HeatSources)
            if (h.Power is null && !(t.Sources ?? []).Any(s => s.Name == h.Name))
                found.Add(D.SetupSource(name, $"gives heat source '{h.Name}' no power, and the source states no default Power"));

        // brief-em3d-77 R-em3d77-1 — each current names a port of this view, a Dc that parses, and faces that exist; a wire's
        // convection names its ambient; a bond's resistances parse
        var currents = t.Currents ?? [];
        foreach (var c in currents)
        {
            if (!doc.Ports.Any(p => p.Number == c.Port))
                found.Add(D.Current(name, $"gives port {c.Port} a current, and this 3D view has no port {c.Port}" +
                                          (doc.Ports.Count == 0 ? " (it has none)" : $" (it has {string.Join(", ", doc.Ports.Select(p => p.Number))})")));
            if (string.IsNullOrWhiteSpace(c.Dc))
            {
                if (c.More is not { Count: > 0 }) found.Add(D.Current(name, $"gives port {c.Port} no Dc current"));
            }
            else if (Unparsable(c.Dc) is { } err) found.Add(D.Current(name, $"gives port {c.Port} the Dc '{c.Dc}', which does not parse: {err}"));
            foreach (var (key, face) in new[] { ("EnterFace", c.EnterFace), ("LeaveFace", c.LeaveFace) })
                if (face is not null && e is { Ok: true } && FaceProblem(doc, e, face) is { } why)
                    found.Add(D.Current(name, $"names '{face}' as port {c.Port}'s {key}, which does not exist: {why}"));
        }
        foreach (var dup in currents.GroupBy(c => c.Port).Where(g => g.Count() > 1))
            found.Add(D.Current(name, $"gives port {dup.Key} {dup.Count()} currents; a port carries one"));
        if (t.Submodel is not null && currents.Count > 0)
            found.Add(D.Current(name, "is a submodel and gives ports currents; a submodel carries none in this version — run them in the whole-model setup"));
        if (t.WireConvectionH is { } wh && Unparsable(wh) is { } whe) found.Add(D.Current(name, $"has WireConvectionH '{wh}', which does not parse: {whe}"));
        if (t.WireConvectionH is not null && string.IsNullOrWhiteSpace(t.WireAmbientC))
            found.Add(D.Current(name, "states WireConvectionH and no WireAmbientC: say what temperature a wire in air loses its heat to"));
        else if (t.WireAmbientC is { } wa && Unparsable(wa) is { } wae) found.Add(D.Current(name, $"has WireAmbientC '{wa}', which does not parse: {wae}"));
        foreach (var (key, text) in new[] { ("BondThermalResistance", t.BondThermalResistance), ("BondElectricalResistance", t.BondElectricalResistance) })
            if (text is not null && Unparsable(text) is { } be) found.Add(D.Current(name, $"has {key} '{text}', which does not parse: {be}"));
        if (currents.Count > 0 && e is { Ok: true })
            foreach (var sol in e.Solids.Where(x => x.Role == Em3dRole.Conductor && !Thermal.ThermalMaterials.NotMeshed(x.Role, x.Material)))
                if (Thermal.ThermalMaterials.For(e, sol.Name, sol.Material) is { } rec && ThermalProperties.SigmaAt(rec.Material, 20) is null)
                    found.Add(D.Current(name, $"sends current through the conductors, and '{sol.Name}''s material '{rec.Material.Name}' states no electrical " +
                                              "conductivity (Sigma20 or SigmaVsTemp)"));

        // Every meshed solid's material states k (R-em3d73-1c): never a default. Air is not meshed. brief-em3d-74 — the run's
        // own lookup, which looks through a technology record stating no k to a same-name library record that does.
        // brief-em3d-76: each solid against its OWN technology — a placed layout's materials are its technology's
        if (e is { Ok: true, Technology: not null })
        {
            var missing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var s in e.Solids)
            {
                if (Thermal.ThermalMaterials.NotMeshed(s.Role, s.Material)) continue;
                var (tech, baseName) = Thermal.ThermalMaterials.Source(e, s.Name, s.Material);
                if (tech?.FindMaterial(baseName) is not { } tm) continue;      // undefined: elaboration's own refusal says
                if (Thermal.ThermalMaterials.Find(tech, baseName) is null)
                    (missing.TryGetValue(tm.Name, out var l) ? l : missing[tm.Name] = []).Add(s.Name);
            }
            foreach (var (material, objects) in missing)
                found.Add(D.MaterialK(name, material, objects));
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
                if (string.IsNullOrWhiteSpace(text) || Unparsable(text) is not null)
                    found.Add(D.Sweep(name, $"sweeps '{s.Var}' from a {key} '{text}' that does not parse: {Unparsable(text ?? "") ?? "it is empty"}"));
        }
        foreach (var dup in sweep.GroupBy(s => s.Var, StringComparer.Ordinal).Where(g => g.Count() > 1))
            found.Add(D.Sweep(name, $"sweeps '{dup.Key}' on {dup.Count()} axes; a variable is one axis"));

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

        return found;
    }

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
                    if (c.Name == "T" && probe.Point is null && probe.Spot is null)
                    { problem = $"T({pn}) reads one temperature, which only a point or spot probe has; use Tmax, Tmin or Tavg"; return; }
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
    public static double? Evaluate(C3dResolution resolution, string text, out string? error)
    {
        var (expr, unit) = SplitUnit(text);
        double scale = unit switch
        {
            null or "W" or "K" or "degC" => 1,
            "mW" => 1e-3,
            "uW" => 1e-6,
            "kW" => 1e3,
            _ => Units.Scale(unit) ?? double.NaN,
        };
        if (double.IsNaN(scale)) { error = $"it ends in '{unit}', which is not a power or temperature unit a thermal setup reads"; return null; }
        Value v;
        try { v = resolution.Evaluate(expr, null); }
        catch (ExpressionException ex) { error = ex.Message; return null; }
        if (v.Kind != ValueKind.Real) { error = $"it is {v.Kind.ToString().ToLowerInvariant()}; it must be a real number"; return null; }
        error = null;
        return v.AsReal() * scale;
    }

    private static (string Expr, string? Unit) SplitUnit(string text)
    {
        var (expr, unit) = Units.LiftInlineUnit(text.Trim());
        return (expr, unit);
    }

    /// <summary>Why <paramref name="text"/> cannot be an expression here (a trailing spaced unit allowed), or null.</summary>
    public static string? Unparsable(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "it is empty";
        var (expr, _) = SplitUnit(text);
        try { Parser.Parse(expr); return null; }
        catch (ExpressionException ex) { return ex.Message; }
    }

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
        public const string SubmodelId       = "c3d.thermal.submodel";
        public const string CurrentId        = "c3d.thermal.current";

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
        public static Diagnostic Submodel(string setup, string what)
            => E(SubmodelId, "Thermal setup '{setup}' is a submodel that {what}.", ("setup", setup), ("what", what));
        public static Diagnostic Current(string setup, string what)
            => E(CurrentId, "Thermal setup '{setup}' {what}.", ("setup", setup), ("what", what));
        public static Diagnostic Measure(string setup, string measure, string why)
            => E(MeasureId, "Thermal setup '{setup}' has the measure '{measure}', which cannot be evaluated: {why}.",
                 ("setup", setup), ("measure", measure), ("why", why));
    }
}
