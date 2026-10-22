"""circuitRF's EMerge driver (brief-emi-4).

A CONSTANT program: circuitRF writes it unchanged into every EMerge run directory beside that run's problem.json, which is
pure data -- every object by index, every length in metres, every decision made by circuitRF before this starts. Nothing
read from problem.json is ever executed, evaluated or imported: names are only labels handed to EMerge, numbers are
numbers, and a word of the format is only ever compared against a fixed set.

Each solid is built with the same OpenCASCADE calls circuitRF's Gmsh script for Palace writes, then handed to EMerge as
one of its own volumes, so the two FEM solvers mesh one geometry. Overlaps are EMerge's priorities (circuitRF's precedence,
already ranked). Kernel solids are their B-rep files, imported as they are and scaled from micrometres.

    python -I circuitrf_emerge.py <problem.json> mesh

builds the model and meshes it, and prints one line of JSON: the counts, and any warning. Exit 0 on success; 2 with an
"error" line when the input or EMerge refuses it.

    python -I circuitrf_emerge.py <problem.json> solve

(brief-emi-5) builds, meshes and solves it, and writes PLAIN ARRAYS beside problem.json -- circuitRF builds the DataSet,
the Touchstone and their provenance from them, so EMerge's own Touchstone writer is never used:
  driven     freq.npy (the requested grid, Hz), S.npy (complex, [f, i, j], ports in ascending number), Z0.npy (each port's
             reference, ohms, complex)
  eigenmode  modes.npy (complex frequencies, Hz, as EMerge returns them -- null-space solutions included; circuitRF
             filters), Q.npy (each one's Q)
and run.json (versions, the sparse solver EMerge called, tets, samples, the sweep's error, timings). The JSON line it
prints last is run.json's content.
"""

import json
import os
import sys

DRIVER_VERSION = 1
FORMAT = "circuitRF EMerge problem"
FORMAT_VERSION = 1
MODES = ("mesh", "solve")
RUN_FILE = "run.json"


def report(**fields):
    fields["driver"] = DRIVER_VERSION
    print(json.dumps(fields, sort_keys=True), flush=True)


def fail(message):
    report(error=message)
    sys.exit(2)


def main(argv):
    if len(argv) != 3 or argv[2] not in MODES:
        fail("usage: circuitrf_emerge.py <problem.json> " + "|".join(MODES))
    path = os.path.abspath(argv[1])
    with open(path, encoding="utf-8") as f:
        problem = json.load(f)
    if problem.get("format") != FORMAT or problem.get("version") != FORMAT_VERSION:
        fail("this driver reads %s version %d, and %s is not one" % (FORMAT, FORMAT_VERSION, os.path.basename(path)))

    import emerge as em
    import gmsh
    try:
        from emerge._emerge.geometry import GeoVolume, GeoSurface
    except ImportError as e:
        fail("this EMerge has no geometry classes where circuitRF's driver looks for them (%s); tested with EMerge %s"
             % (e, ", ".join(problem["testedEmerge"])))

    warnings = []
    version = getattr(em, "__version__", None)
    if version not in problem["testedEmerge"]:
        warnings.append("EMerge %s is not a version circuitRF has tested (%s); it is run as it is."
                        % (version, ", ".join(problem["testedEmerge"])))

    import time
    started = time.time()
    try:
        sim, counts = build(problem, os.path.dirname(path), em, gmsh, GeoVolume, GeoSurface)
    except Exception as e:  # EMerge's or Gmsh's own refusal, reported as the run's
        fail("%s: %s" % (type(e).__name__, e))
    if argv[2] == "mesh":
        report(emerge=version, warnings=warnings, **counts)
        return
    meshed = time.time()
    # The mesh's size, before any solve: circuitRF checks the run's memory on it and stops the run when it is refused.
    report(stage="meshed", tets=counts["tets"], edges=counts["edges"], triangles=counts["triangles"])
    # After the build: creating the Simulation reconfigures EMerge's logger, which would drop a handler added before it.
    heard = Heard()
    from loguru import logger
    logger.add(heard.sink, level="INFO", format="{message}")
    try:
        outcome = solve(problem, os.path.dirname(path), sim, heard, warnings)
    except Exception as e:
        fail("%s: %s" % (type(e).__name__, e))
    run = dict(emerge=version, warnings=warnings, sparseSolver=heard.solver, meshSeconds=round(meshed - started, 3),
               solveSeconds=round(time.time() - meshed, 3), **counts)
    run.update(outcome)
    run["tets"] = int(sim.mesh.n_tets)          # after any adaptive refinement
    mw = sim.mw
    basis = getattr(mw, "basis", None)
    run["unknowns"] = int(basis.n_field) if basis is not None and hasattr(basis, "n_field") else None
    run["driver"] = DRIVER_VERSION
    with open(os.path.join(os.path.dirname(path), RUN_FILE), "w", encoding="utf-8") as f:
        json.dump(run, f, sort_keys=True)
    report(**run)


class Heard:
    """What EMerge's own log said that the run reports: the direct solver it called and the adaptive sweep's last
    estimate. Read from the message text EMerge 3.0.0b2 logs; a line it no longer logs leaves the field unreported."""

    def __init__(self):
        self.solver = None
        self.error = None
        self.converged = None

    def sink(self, message):
        import re
        text = message.record["message"]
        m = re.search(r"Calling (\S+(?: \S+)?) Solver", text)
        if m:
            self.solver = m.group(1)
        m = re.search(r"estimated error ([0-9.eE+-]+)", text)
        if m:
            self.error = float(m.group(1))
        if text.startswith("Adaptive sweep converged"):
            self.converged = True
        elif text.startswith("Adaptive sweep stopped at max_samples"):
            self.converged = False


# -- geometry ------------------------------------------------------------------------------------------------------------

def points(gmsh, ring):
    return [gmsh.model.occ.addPoint(p[0], p[1], p[2]) for p in ring]


def polygon_loop(gmsh, ring):
    tags = points(gmsh, ring)
    lines = [gmsh.model.occ.addLine(tags[k], tags[(k + 1) % len(tags)]) for k in range(len(tags))]
    return gmsh.model.occ.addCurveLoop(lines)


def planar(gmsh, rings):
    """A plane region: the first ring the outline, the rest holes."""
    return gmsh.model.occ.addPlaneSurface([polygon_loop(gmsh, r) for r in rings])


def rectangle(gmsh, origin, u, v):
    o = origin
    ring = [o, [o[0] + u[0], o[1] + u[1], o[2] + u[2]],
            [o[0] + u[0] + v[0], o[1] + u[1] + v[1], o[2] + u[2] + v[2]], [o[0] + v[0], o[1] + v[1], o[2] + v[2]]]
    return planar(gmsh, [ring])


def volumes_of(dimtags):
    return [t for d, t in dimtags if d == 3]


def shape(gmsh, s, folder):
    """The OCC volume tags of one solid's shape."""
    occ = gmsh.model.occ
    kind = s["kind"]
    if kind == "box":
        lo, hi = s["min"], s["max"]
        return [occ.addBox(lo[0], lo[1], lo[2], hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2])]
    if kind == "cylinder":
        p, a = s["start"], s["axis"]
        return [occ.addCylinder(p[0], p[1], p[2], a[0], a[1], a[2], s["radius"])]
    if kind == "sphere":
        c = s["center"]
        return [occ.addSphere(c[0], c[1], c[2], s["radius"])]
    if kind == "truncatedSphere":
        c, lo, hi = s["center"], s["clipMin"], s["clipMax"]
        ball = occ.addSphere(c[0], c[1], c[2], s["radius"])
        clip = occ.addBox(lo[0], lo[1], lo[2], hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2])
        kept, _ = occ.intersect([(3, ball)], [(3, clip)])
        return volumes_of(kept)
    if kind == "ellipsoid":
        c, (a, b, cc) = s["center"], s["semiAxes"]
        v = occ.addSphere(0, 0, 0, 1)
        occ.dilate([(3, v)], 0, 0, 0, a, b, cc)
        if "rotationAxis" in s:
            ax = s["rotationAxis"]
            occ.rotate([(3, v)], 0, 0, 0, ax[0], ax[1], ax[2], s["rotationAngle"])
        occ.translate([(3, v)], c[0], c[1], c[2])
        return [v]
    if kind == "extrusion":
        base = planar(gmsh, s["rings"])
        return volumes_of(occ.extrude([(2, base)], 0, 0, s["height"]))
    if kind == "sweep":
        wires = []
        for ring in s["rings"]:
            tags = points(gmsh, ring)
            if s["round"]:
                curves = [occ.addSpline(tags + [tags[0]])]
            else:
                curves = [occ.addLine(tags[k], tags[(k + 1) % len(tags)]) for k in range(len(tags))]
            wires.append(occ.addWire(curves))
        return volumes_of(occ.addThruSections(wires, makeSolid=True, makeRuled=True))
    if kind == "polyhedron":
        tags = points(gmsh, s["vertices"])
        edges = {}

        def edge(a, b):
            key = (min(a, b), max(a, b))
            if key not in edges:
                edges[key] = occ.addLine(tags[key[0]], tags[key[1]])
            return edges[key] if a < b else -edges[key]

        faces = []
        for rings in s["faces"]:
            loops = [occ.addCurveLoop([edge(r[k], r[(k + 1) % len(r)]) for k in range(len(r))]) for r in rings]
            faces.append(occ.addPlaneSurface(loops))
        return [occ.addVolume([occ.addSurfaceLoop(faces)])]
    if kind == "brep":
        imported = occ.importShapes(os.path.join(folder, s["file"]), highestDimOnly=True)
        occ.dilate(imported, 0, 0, 0, s["scale"], s["scale"], s["scale"])
        return volumes_of(imported)
    raise ValueError("unknown shape kind")


def material(em, m):
    if m["pec"]:
        return em.lib.PEC
    er = m["erTensor"] if "erTensor" in m else m["er"]
    if isinstance(er, list):
        import numpy as np
        er = np.diag(er)
    return em.Material(er=er, ur=m["ur"], tand=m["tand"], cond=m["cond"], name=m["name"])


def impedance(element):
    """A lumped element's Z(f), from its R, L and C."""
    import math
    r, l, c, series = element["r"], element["l"], element["c"], element["topology"] == "series"

    def z(f):
        w = 2 * math.pi * f
        if series:
            return (r or 0) + (1j * w * l if l is not None else 0) + (1 / (1j * w * c) if c is not None else 0)
        y = (1 / r if r is not None else 0) + (1 / (1j * w * l) if l is not None else 0) + (1j * w * c if c is not None else 0)
        return 1 / y

    return z


# -- the model -----------------------------------------------------------------------------------------------------------

def build(problem, folder, em, gmsh, GeoVolume, GeoSurface):
    sim = em.Simulation("circuitrf")
    occ = gmsh.model.occ
    settings = problem["settings"]
    mats = [material(em, m) for m in problem["materials"]]

    box = problem["box"]
    lo, hi = box["min"], box["max"]
    air = GeoVolume(occ.addBox(lo[0], lo[1], lo[2], hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]), name="air box")
    air.set_material(mats[box["material"]]).prio_set(0)
    owners = [(air, 0, "air", box["material"])]
    for s in problem["solids"]:
        v = GeoVolume(shape(gmsh, s["shape"], folder), name=s["name"])
        v.set_material(mats[s["material"]]).prio_set(s["priority"])
        owners.append((v, s["priority"], s["role"], s["material"]))

    sheets = []
    for sh in problem["sheets"]:
        g = GeoSurface(planar(gmsh, sh["rings"]), name=sh["name"])
        g.set_material(mats[sh["material"]])
        if not problem["materials"][sh["material"]]["pec"]:
            g.assign_thickness(sh["thicknessM"])
        sheets.append(g)
    ports = [(p, GeoSurface(rectangle(gmsh, p["origin"], p["u"], p["v"]), name=p["name"])) for p in problem["ports"]]
    elements = [(e, GeoSurface(rectangle(gmsh, e["origin"], e["u"], e["v"]), name=e["name"]))
                for e in problem["lumpedElements"]]

    sim.commit_geometry()

    # Conductors are voids, as in Palace's mesh: EMerge has already fragmented the model and resolved every overlap by
    # priority, so each fragment has one owner. Every fragment a conductor owns is removed before meshing, and the faces
    # it shares with meshed space carry its metal (a perfect conductor, or its surface impedance). Without this, Gmsh fills
    # every conductor with tetrahedra EMerge then discards, and a conductor overlapping another leaves the overlap's
    # slivers, curves and points to mesh (the 3D Connector's pin, half sunk into its 35 um trace, never finished).
    gmsh.model.occ.synchronize()
    owner = {}
    for v, priority, role, mat in owners:
        for t in v.tags:
            if t not in owner or priority >= owner[t][0]:
                owner[t] = (priority, role, mat)
    metal = {t for t, (_, role, _) in owner.items() if role == "conductor"}
    walls = {}
    for _, tag in gmsh.model.getEntities(2):
        up, _ = gmsh.model.getAdjacencies(2, tag)
        in_metal = [v for v in up if v in metal]
        if in_metal and any(v not in metal for v in up):
            walls.setdefault(owner[in_metal[0]][2], []).append(tag)
    # Recursive, as Palace's script deletes its conductors: whatever bounded only metal goes too, down to its curves and
    # points, and whatever still bounds meshed space stays.
    gmsh.model.occ.remove([(3, t) for t in sorted(metal)], recursive=True)
    gmsh.model.occ.synchronize()
    for v, _, _, _ in owners:
        v.tags = [t for t in v.tags if t not in metal]
    # commit_geometry has already cached every object's boundary faces and generated EMerge's default boundary conditions
    # from them: a removed conductor would keep a surface impedance on faces that no longer exist, and the solve stops on
    # the first. So the cache is taken again, as commit_geometry takes it (a removed conductor has no boundary left), and
    # the defaults are cleared; EMerge generates them afresh when it solves, under the conditions set below.
    for g in sim.all_geos():
        g._cache_gmsh()
    sim.mw.reset()

    sim.mw.set_basis_space(element_order=settings["elementOrder"])
    sim.mw.set_resolution(settings["resolution"])
    sim.mw.set_frequency(problem["frequenciesHz"] if problem["problem"] == "driven" else [problem["sizingHz"]])

    # Refinement along the edges of every sheet and every conductor's faces.
    edge = settings["edgeSizeM"]
    for g in sheets:
        sim.mesher.set_boundary_size(g, edge)
    for tags in walls.values():
        sim.mesher.set_boundary_size(em.FaceSelection(tags), edge)
    for region in problem["meshRegions"]:
        rlo, rhi = region["min"], region["max"]
        field = gmsh.model.mesh.field.add("Box")
        for key, value in (("VIn", region["sizeM"]), ("VOut", settings["largestSizeM"]), ("Thickness", region["thicknessM"]),
                           ("XMin", rlo[0]), ("XMax", rhi[0]), ("YMin", rlo[1]), ("YMax", rhi[1]), ("ZMin", rlo[2]), ("ZMax", rhi[2])):
            gmsh.model.mesh.field.setNumber(field, key, value)
        sim.mesher.mesh_fields.append(field)

    # The air box's faces: every outer face of meshed space on a box plane. PEC is EMerge's own default for an outer face,
    # so only PMC and absorbing faces are stated.
    size = max(hi[k] - lo[k] for k in range(3))
    tol = 1e-9 * size
    planes = {"xmin": (0, lo[0]), "xmax": (0, hi[0]), "ymin": (1, lo[1]), "ymax": (1, hi[1]), "zmin": (2, lo[2]), "zmax": (2, hi[2])}
    faces = {key: [] for key in planes}
    for _, tag in gmsh.model.getEntities(2):
        up, _ = gmsh.model.getAdjacencies(2, tag)
        if len(up) != 1:
            continue
        c = gmsh.model.occ.getCenterOfMass(2, tag)
        for key, (axis, at) in planes.items():
            if abs(c[axis] - at) <= tol:
                faces[key].append(tag)

    # A floor under every element size, a thousandth of the conductor-edge size. EMerge sizes curved edges by their
    # curvature, and on an imported B-rep's trimmed curve that estimate can be degenerate: the 3D Connector's pin, cut
    # by its trace, asked Gmsh for elements of essentially zero length and took gigabytes in the 1D mesh before being
    # stopped. The model here is in metres where Palace's is in micrometres, so Gmsh's absolute tolerances no longer
    # catch it. No real feature is near the floor, and the launch's mesh is the same from edge/10 000 to edge/10.
    gmsh.option.setNumber("Mesh.MeshSizeMin", edge / 1000)
    sim.generate_mesh()

    for mat, tags in walls.items():
        if problem["materials"][mat]["pec"]:
            sim.mw.bc.PEC(em.FaceSelection(tags))
        else:
            sim.mw.bc.SurfaceImpedance(em.FaceSelection(tags), mats[mat])
    for key, kind in box["faces"].items():
        if not faces[key] or kind == "pec":
            continue
        selection = em.FaceSelection(faces[key])
        if kind == "pmc":
            sim.mw.bc.PMC(selection)
        elif kind == "absorbing":
            sim.mw.bc.AbsorbingBoundary(selection)
    for p, plate in ports:
        sim.mw.bc.LumpedPort(plate, p["number"], width=p["widthM"], height=p["heightM"], direction=tuple(p["direction"]),
                             Z0=p["z0"])
    for e, plate in elements:
        sim.mw.bc.LumpedElement(plate, impedance(e), width=e["widthM"], height=e["heightM"])

    mesh = sim.mesh
    return sim, {"tets": int(mesh.n_tets), "edges": int(mesh.n_edges), "triangles": int(mesh.n_tris),
                 "faces": {key: len(tags) for key, tags in faces.items()}, "conductorFaces": sum(len(t) for t in walls.values())}


# -- the solve (brief-emi-5) ----------------------------------------------------------------------------------------------

def solve(problem, folder, sim, heard, warnings):
    """Solves the built model and writes its arrays into folder; returns run.json's solve fields."""
    import numpy as np
    settings = problem["settings"]
    if problem["problem"] == "eigenmode":
        if settings["adaptiveMesh"] > 0:
            warnings.append("EMerge refines a mesh on a driven solve, so this eigenmode run used the initial mesh.")
        eig = problem["eigenmode"]
        data = sim.mw.eigenmode(search_frequency=eig["targetHz"], nmodes=eig["count"])
        entries = list(data.scalar.iter())
        np.save(os.path.join(folder, "modes.npy"), np.array([complex(e.freq) for e in entries], dtype=np.complex128))
        np.save(os.path.join(folder, "Q.npy"), np.array([float(np.real(e.Q)) for e in entries], dtype=np.float64))
        return {"modes": len(entries)}

    if settings["adaptiveMesh"] > 0:
        # Up to adaptiveMesh refinements: EMerge counts solve passes, the first on the initial mesh.
        sim.adaptive_mesh_refinement(max_steps=settings["adaptiveMesh"] + 1)
    freqs = np.array(problem["frequenciesHz"], dtype=np.float64)
    sim.mw.set_frequency(list(freqs))
    adaptive = settings["adaptiveSweep"] and len(freqs) > settings["initialSamples"]
    if adaptive:
        data = sim.mw.run_adaptive_sweep(n_initial=settings["initialSamples"], tol=settings["tolerance"],
                                         max_samples=settings["maxSamples"])
    else:
        data = sim.mw.run_sweep()
    grid = data.scalar.grid
    numbers = [p["number"] for p in sorted(problem["ports"], key=lambda p: p["number"])]
    index = [grid._portmap[n] for n in numbers]
    full = grid.model_Smat(freqs) if adaptive else grid.Smat
    S = np.asarray(full)[:, index, :][:, :, index].astype(np.complex128)
    if not adaptive and not np.allclose(np.ravel(grid.freq), freqs, rtol=1e-12, atol=0):
        raise ValueError("EMerge solved at frequencies other than the requested ones")
    z0 = np.asarray(grid.Z0).reshape(-1, len(grid._portmap))[0][index].astype(np.complex128)
    # C order, which circuitRF reads: S is indexed out of EMerge's matrix, and numpy keeps such a view in Fortran order.
    np.save(os.path.join(folder, "freq.npy"), np.ascontiguousarray(freqs))
    np.save(os.path.join(folder, "S.npy"), np.ascontiguousarray(S))
    np.save(os.path.join(folder, "Z0.npy"), np.ascontiguousarray(z0))
    out = {"samples": int(np.size(grid.freq)), "adaptive": bool(adaptive)}
    if adaptive:
        out["error"] = heard.error
        out["converged"] = heard.converged
    return out


if __name__ == "__main__":
    main(sys.argv)
