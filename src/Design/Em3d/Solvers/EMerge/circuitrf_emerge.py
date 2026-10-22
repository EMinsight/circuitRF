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

(brief-emi-7) When problem.json has a "fields" block, the solve also writes FIELDS, in the .vtu layout circuitRF reads for
Palace (an UnstructuredGrid of Lagrange tetrahedra, every cell with its own nodes, inline-base64 geometry, appended-raw
arrays, no compression, complex quantities as <name>_real/<name>_imag):
  driven     postpro/paraview/driven/excitation_<port>/Cycle<k>/data.pvtu + proc000000.vtu, one per (port, frequency) asked
             for, each from a direct solve at that frequency (the adaptive sweep's fields are at its own samples). E and B
             at a PEAK incident power of fields.incidentPowerW, circuitRF's reading of Palace's convention; ff_*.npy, the
             far field on the air box's absorbing faces, when fields.farField asks.
  eigenmode  postpro/paraview/eigenmode/Cycle<k>/..., mode k-1 of modes.npy, normalised so the integral of eps|E|^2 over
             the mesh is fields.modeEnergyJ (Palace's mode normalisation), its largest field component real.
run.json's "fieldSteps" lists them with what circuitRF needs beside them: each driven step's port voltage and incident
voltage, each mode's integral of eps|E|^2 per material and each lumped port's voltage. circuitRF writes the .pvd collections.
"""

import json
import os
import sys

DRIVER_VERSION = 2
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
        sim, counts, model = build(problem, os.path.dirname(path), em, gmsh, GeoVolume, GeoSurface)
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
        outcome = solve(problem, os.path.dirname(path), sim, model, heard, warnings)
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
    names = ["background"]
    for s in problem["solids"]:
        v = GeoVolume(shape(gmsh, s["shape"], folder), name=s["name"])
        v.set_material(mats[s["material"]]).prio_set(s["priority"])
        owners.append((v, s["priority"], s["role"], s["material"]))
        names.append(s["name"])

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
    for k, (v, priority, role, mat) in enumerate(owners):
        for t in v.tags:
            if t not in owner or priority >= owner[t][0]:
                owner[t] = (priority, role, mat, k)
    metal = {t for t, (_, role, _, _) in owner.items() if role == "conductor"}
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
    lumped = []
    for p, plate in ports:
        lumped.append((p, sim.mw.bc.LumpedPort(plate, p["number"], width=p["widthM"], height=p["heightM"],
                                               direction=tuple(p["direction"]), Z0=p["z0"])))
    for e, plate in elements:
        sim.mw.bc.LumpedElement(plate, impedance(e), width=e["widthM"], height=e["heightM"])

    mesh = sim.mesh
    # brief-emi-7 — what the fields need of the model: each meshed volume's owner (circuitRF's object, by index into
    # names, and its material), the lumped ports' EMerge objects, and the absorbing faces a far field is integrated over.
    volumes = {t: (k, mat) for t, (_, _, mat, k) in owner.items() if t not in metal}
    absorbing = [tag for key, kind in box["faces"].items() if kind == "absorbing" for tag in faces[key]]
    model = {"names": names, "volumes": volumes, "ports": lumped, "absorbing": absorbing}
    return sim, {"tets": int(mesh.n_tets), "edges": int(mesh.n_edges), "triangles": int(mesh.n_tris),
                 "faces": {key: len(tags) for key, tags in faces.items()}, "conductorFaces": sum(len(t) for t in walls.values())}, model


# -- the solve (brief-emi-5) ----------------------------------------------------------------------------------------------

def solve(problem, folder, sim, model, heard, warnings):
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
        out = {"modes": len(entries)}
        if problem.get("fields") is not None:
            out.update(mode_fields(problem, folder, sim, model, list(data.field.iter())))
        return out

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
    if problem.get("fields") is not None and problem["fields"]["frequenciesHz"]:
        out.update(driven_fields(problem, folder, sim, model, [] if adaptive else list(data.field.iter())))
    return out


# -- fields (brief-emi-7) -------------------------------------------------------------------------------------------------

MU0 = 4e-7 * 3.141592653589793
EPS0 = 8.8541878128e-12
PARAVIEW = os.path.join("postpro", "paraview")
# VTK's Lagrange tetrahedron and its 10-node edge order: the four vertices, then the midpoints of (0,1) (1,2) (0,2) (0,3)
# (1,3) (2,3).
LAGRANGE_TETRAHEDRON = 71
TET_EDGES = ((0, 1), (1, 2), (0, 2), (0, 3), (1, 3), (2, 3))


def cell_nodes(mesh, order):
    """Every cell's own nodes, cell by cell (a Nedelec field is not continuous across a face, so no node is shared): the
    points (3, cells * per), the cell each belongs to, and the nodes per cell."""
    import numpy as np
    corners = mesh.nodes[:, mesh.tets]                          # (3, 4, cells)
    if order == 2:
        mids = [(corners[:, a, :] + corners[:, b, :]) / 2 for a, b in TET_EDGES]
        corners = np.concatenate([corners, np.stack(mids, axis=1)], axis=1)
    per = corners.shape[1]
    points = np.ascontiguousarray(np.transpose(corners, (0, 2, 1)).reshape(3, -1))
    return points, np.repeat(np.arange(mesh.n_tets), per), per


def evaluate(field, points, cells):
    """E and H at points, each evaluated INSIDE the cell named for it (the point-to-tetrahedron mapping supplied), never by
    a search that would pick either side of a face."""
    import numpy as np
    field._silent = True
    field.interpolate(points[0], points[1], points[2], usenan=False, mapping=cells)
    E = np.array([field.Ex, field.Ey, field.Ez]).reshape(3, -1)
    H = np.array([field.Hx, field.Hy, field.Hz]).reshape(3, -1)
    return E, H


def cell_owners(mesh, model):
    """Each tetrahedron's object (by index into the model's names) and material index."""
    import numpy as np
    obj = np.zeros(mesh.n_tets, dtype=np.int32)
    mat = np.zeros(mesh.n_tets, dtype=np.int32)
    for tag, tets in mesh.vtag_to_tet.items():
        if tag in model["volumes"]:
            k, m = model["volumes"][tag]
            obj[tets] = k
            mat[tets] = m
    return obj, mat


def write_step(folder, rel, points, per, attribute, arrays):
    """One step in Palace's layout: <rel>/data.pvtu naming one piece, <rel>/proc000000.vtu. arrays: name -> (3, N) float
    (point data, Float32). Returns the bytes written."""
    import base64
    import numpy as np
    d = os.path.join(folder, rel)
    os.makedirs(d, exist_ok=True)
    cells = attribute.shape[0]

    def inline(a):
        b = np.ascontiguousarray(a).tobytes()
        return (base64.b64encode(np.uint32(len(b)).tobytes()) + base64.b64encode(b)).decode("ascii")

    def head(kind, name, comps, tail):
        n = ' Name="%s"' % name if name else ""
        return '<DataArray type="%s"%s NumberOfComponents="%d" %s' % (kind, n, comps, tail)

    blocks, offset, entries = [], 0, []
    for name, a in arrays:
        b = np.ascontiguousarray(a.T.astype(np.float32)).tobytes()
        entries.append(head("Float32", name, 3, 'format="appended" offset="%d"/>' % offset))
        blocks.append(np.uint32(len(b)).tobytes() + b)
        offset += 4 + len(b)
    xml = ['<?xml version="1.0"?>',
           '<VTKFile type="UnstructuredGrid" version="2.2" byte_order="LittleEndian" header_type="UInt32">',
           '<UnstructuredGrid>', '<Piece NumberOfPoints="%d" NumberOfCells="%d">' % (points.shape[1], cells),
           '<Points>', head("Float32", None, 3, 'format="binary">') + inline(points.T.astype(np.float32)) + '</DataArray>',
           '</Points>', '<Cells>',
           head("Int32", "connectivity", 1, 'format="binary">') + inline(np.arange(cells * per, dtype=np.int32)) + '</DataArray>',
           head("Int32", "offsets", 1, 'format="binary">') + inline(np.arange(1, cells + 1, dtype=np.int32) * per) + '</DataArray>',
           head("UInt8", "types", 1, 'format="binary">') + inline(np.full(cells, LAGRANGE_TETRAHEDRON, dtype=np.uint8)) + '</DataArray>',
           '</Cells>', '<PointData>'] + entries + ['</PointData>', '<CellData>',
           head("Int32", "attribute", 1, 'format="binary">') + inline(attribute.astype(np.int32)) + '</DataArray>',
           '</CellData>', '</Piece>', '</UnstructuredGrid>', '<AppendedData encoding="raw">']
    piece = os.path.join(d, "proc000000.vtu")
    with open(piece, "wb") as f:
        f.write(("\n".join(xml) + "\n_").encode("ascii"))
        for b in blocks:
            f.write(b)
        f.write(b"\n</AppendedData>\n</VTKFile>\n")
    pdata = "\n".join('<PDataArray type="Float32" Name="%s" NumberOfComponents="3" format="appended"/>' % n for n, _ in arrays)
    pvtu = "\n".join([
        '<?xml version="1.0"?>', '<VTKFile type="PUnstructuredGrid" version="2.2" byte_order="LittleEndian">',
        '<PUnstructuredGrid GhostLevel="0">',
        '<PPoints>', '<PDataArray type="Float32" Name="Points" NumberOfComponents="3" format="binary"/>', '</PPoints>',
        '<PCells>', '<PDataArray type="Int32" Name="connectivity" NumberOfComponents="1" format="binary"/>',
        '<PDataArray type="Int32" Name="offsets" NumberOfComponents="1" format="binary"/>',
        '<PDataArray type="UInt8" Name="types" NumberOfComponents="1" format="binary"/>', '</PCells>',
        '<PPointData>', pdata, '</PPointData>',
        '<PCellData>', '<PDataArray type="Int32" Name="attribute" NumberOfComponents="1" format="binary"/>', '</PCellData>',
        '<Piece Source="proc000000.vtu"/>', '</PUnstructuredGrid>', '</VTKFile>', ''])
    with open(os.path.join(d, "data.pvtu"), "w", encoding="ascii") as f:
        f.write(pvtu)
    return os.path.getsize(piece) + len(pvtu)


def write_field(folder, rel, mesh, field, points, cells, per, attribute, scale):
    """E and B of field (times scale) at every cell's own nodes, as one step. Returns the bytes written."""
    import numpy as np
    E, H = evaluate(field, points, cells)
    ur = np.real(np.asarray(field._dur)).reshape(-1)[cells]
    E = E * scale
    B = H * (scale * MU0) * ur
    arrays = [("B_imag", B.imag), ("B_real", B.real), ("E_imag", E.imag), ("E_real", E.real)]
    return write_step(folder, rel, points, per, attribute, arrays)


def write_groups(folder, model, mats):
    """groups.json: each attribute's object, as circuitRF's Palace mesh records it (FieldGroups reads Name, Attribute,
    Dimension, Kind)."""
    groups = [{"Attribute": k + 1, "Name": name, "Dimension": 3, "Kind": "Background" if k == 0 else "Volume",
               "Material": mats[m]["name"]}
              for k, name in enumerate(model["names"])
              for m in sorted({mm for kk, mm in model["volumes"].values() if kk == k})[:1]]
    with open(os.path.join(folder, "groups.json"), "w", encoding="utf-8") as f:
        json.dump({"Generator": "circuitRF brief-emi-7: the mapping from named objects to EMerge field attributes",
                   "LengthUnitM": 1, "Groups": groups}, f, indent=2)


def port_voltage(field, bc):
    """A lumped port's voltage in field: EMerge's own integration lines, averaged, as its S-parameters are computed."""
    import numpy as np
    lines = list(getattr(bc, "voltage_integration_line", None) or [])
    if not lines:
        return None
    total = 0
    for line in lines:
        field._silent = True
        field.interpolate(np.asarray(line.xs), np.asarray(line.ys), np.asarray(line.zs), usenan=False)
        total += line.line_integral_precalc(np.ravel(field.Ex), np.ravel(field.Ey), np.ravel(field.Ez)) / len(lines)
    return complex(total)


def energy_by_material(field, mesh, mat, materials):
    """The integral of Re(eps)|E|^2 over each material's cells, J (peak phasors), by EMerge's own tetrahedral quadrature."""
    import numpy as np
    from emerge._emerge.mth.optimized import gaus_quad_tet
    from emr.mth import generate_int_data_tet
    rule = gaus_quad_tet(4)
    X, Y, Z, W, A, _ = generate_int_data_tet(mesh.nodes, mesh.tets, rule)
    cells = np.tile(np.arange(mesh.n_tets), rule.shape[1])     # the points come quadrature point by quadrature point
    E, _ = evaluate(field, np.array([X, Y, Z]), cells)
    er = np.real(np.asarray(field._der)).reshape(-1)[cells]
    density = EPS0 * er * np.sum(np.abs(E) ** 2, axis=0) * A * W
    return [float(np.sum(density[mat[cells] == m])) for m in range(len(materials))]


def driven_fields(problem, folder, sim, model, solved):
    """The fields asked for, one step per (port, frequency), and the far field when asked; returns run.json's fields."""
    import numpy as np
    req = problem["fields"]
    freqs = req["frequenciesHz"]
    near = lambda a, b: abs(a - b) <= 1e-9 * max(abs(a), abs(b))
    have = {}
    for e in solved:
        for f in freqs:
            if near(float(np.real(e.freq)), f):
                have[f] = e
    missing = [f for f in freqs if f not in have]
    if missing:
        # A direct solve at each: the adaptive sweep fitted S between its own samples, and has no field anywhere else.
        sim.mw.set_frequency(list(missing))
        for e in sim.mw.run_sweep().field.iter():
            for f in missing:
                if near(float(np.real(e.freq)), f):
                    have[f] = e
    mesh = sim.mesh
    order = problem["settings"]["elementOrder"]
    points, cells, per = cell_nodes(mesh, order) if req["write"] else (None, None, 0)
    obj, _ = cell_owners(mesh, model)
    ports = sorted(model["ports"], key=lambda pb: pb[0]["number"])
    steps, written = [], 0
    farfield = req.get("farField")
    if farfield:
        theta = np.arange(0, 180 + 1e-9, farfield["thetaStepDeg"])
        phi = np.arange(0, 360 - 1e-9, farfield["phiStepDeg"])
        T, P = np.meshgrid(np.radians(theta), np.radians(phi), indexing="ij")
        far = np.zeros((len(freqs), len(ports), 2, len(theta), len(phi)), dtype=np.complex128)
        faces = em_faces(model["absorbing"])
    for i, f in enumerate(freqs):
        field = have[f]
        for j, (p, bc) in enumerate(ports):
            field.excite_port(p["number"])
            v, vinc = port_voltage(field, bc), getattr(bc, "voltage", None)
            # EMerge drives a lumped port with an incident wave of sqrt(2 P Z0) peak, P its power (1 W): P time-averaged.
            scale = (req["incidentPowerW"] / bc.power) ** 0.5
            step = {"port": p["number"], "frequencyHz": f, "scale": scale,
                    "v": None if v is None else [v.real * scale, v.imag * scale],
                    "vinc": None if vinc is None else [float(vinc) * scale, 0.0]}
            if req["write"]:
                rel = os.path.join(PARAVIEW, "driven", "excitation_%d" % p["number"], "Cycle%06d" % (i + 1))
                written += write_field(folder, rel, mesh, field, points, cells, per, obj + 1, scale)
                step["pvtu"] = os.path.join(rel, "data.pvtu").replace(os.sep, "/")
            if farfield:
                ff = field.farfield(T, P, faces)
                far[i, j, 0] = np.asarray(ff.Etheta).reshape(T.shape) * scale
                far[i, j, 1] = np.asarray(ff.Ephi).reshape(T.shape) * scale
            steps.append(step)
    if req["write"]:
        write_groups(folder, model, problem["materials"])
    if farfield:
        np.save(os.path.join(folder, "ff_E.npy"), np.ascontiguousarray(far))
        np.save(os.path.join(folder, "ff_theta.npy"), np.ascontiguousarray(theta.astype(np.float64)))
        np.save(os.path.join(folder, "ff_phi.npy"), np.ascontiguousarray(phi.astype(np.float64)))
        np.save(os.path.join(folder, "ff_freq.npy"), np.ascontiguousarray(np.array(freqs, dtype=np.float64)))
    return {"fieldSteps": steps, "fieldBytes": written, "fieldOrder": order}


def em_faces(tags):
    import emerge as em
    return em.FaceSelection(list(tags))


def mode_fields(problem, folder, sim, model, entries):
    """Each mode's energy by material, its lumped ports' voltages and (asked) its field, normalised as Palace normalises a
    mode; returns run.json's fields."""
    import numpy as np
    req = problem["fields"]
    mesh = sim.mesh
    order = problem["settings"]["elementOrder"]
    points, cells, per = cell_nodes(mesh, order) if req["write"] else (None, None, 0)
    obj, mat = cell_owners(mesh, model)
    ports = sorted(model["ports"], key=lambda pb: pb[0]["number"])
    steps, written = [], 0
    for k, field in enumerate(entries):
        energy = energy_by_material(field, mesh, mat, problem["materials"])
        total = sum(energy)
        scale = (req["modeEnergyJ"] / total) ** 0.5 if total > 0 else 1.0
        if req["write"]:
            # The phase: the largest field component at any node real and positive, so Re{E} is the mode at its peak.
            E, _ = evaluate(field, points, cells)
            at = np.unravel_index(np.argmax(np.abs(E)), E.shape)
            scale = scale * np.conj(E[at]) / abs(E[at]) if abs(E[at]) > 0 else scale
        volts = [port_voltage(field, bc) for _, bc in ports]
        step = {"mode": k, "energy": [e * abs(scale) ** 2 for e in energy],
                "portV": [None if v is None else [(v * scale).real, (v * scale).imag] for v in volts]}
        if req["write"]:
            rel = os.path.join(PARAVIEW, "eigenmode", "Cycle%06d" % (k + 1))
            written += write_field(folder, rel, mesh, field, points, cells, per, obj + 1, scale)
            step["pvtu"] = os.path.join(rel, "data.pvtu").replace(os.sep, "/")
        steps.append(step)
    if req["write"]:
        write_groups(folder, model, problem["materials"])
    return {"fieldSteps": steps, "fieldBytes": written, "fieldOrder": order,
            "portNumbers": [p["number"] for p, _ in ports], "portR": [p["z0"] for p, _ in ports],
            "materialNames": [m["name"] for m in problem["materials"]]}


if __name__ == "__main__":
    main(sys.argv)
