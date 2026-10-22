"""brief-emw-1: the four spike cases in EMerge, and everything the terminal transform needs from each run.

    <emerge python> cases.py <case> <run name> ['{"json": "options"}']

Cases (geometry in mm, built here directly in Gmsh through EMerge's own geometry classes, as circuitRF's driver does):
  A  one 1.575 mm microstrip, 15 mm, on the 3D Wave Ports laminate (0.508 mm, er 2.2) in the Coupled Microstrip's
     housing (sides +-4.5 mm, lid 3 mm above the laminate, all PEC), a one-mode wave port at each end
  B  the 3D Wave Ports Coupled Microstrip cell: strips 1.2 and 2 mm, 0.3 mm apart, same laminate (tand 0.0009) and
     housing; a two-mode wave port at each end
  C  the 3D Wave Ports Pair cell: strips 1.65 mm, 0.4 mm apart, between ground planes 2 mm apart in PTFE (er 2.1),
     15 mm; sides 11 strip widths out, PEC by default (`sides`), a two-mode wave port at each end
  D  the Launch's coax alone: pin 0.4 mm, bore 1.34 mm, PTFE (er 2.1), 10 mm, a wave port at each end, `port` modal
     (EMerge's 2D mode solve) or coax (its analytic coax port)

Options: res (EMerge resolution, fraction of a wavelength, default 0.15), edge (mm, size along sheet edges, default
0.1), order (element order, default 2), freqs (GHz), nmodes (per face; default 1 for A and D, 2 for B and C), port
(modal | ih | coax), mixed (recompute modes per frequency, default true where the face is inhomogeneous), align
(align_modes to each terminal's path), sides (C: pec | pmc), tand (override the dielectric's loss tangent), threads,
hmax (mm, the largest element), curv (elements per 2 pi of a curved edge), presolve (solve this many port modes before the sweep), neff (its shift, as beta/k0), direct (dense eigen solve),
tem (keep TEM modes only), dedupe (guard against EMerge storing a re-solved frequency's modes twice), npath (Gauss points per terminal path), sweep (point | adaptive), nsamp (adaptive initial samples).

Writes runs/<run name>/data.npz and meta.json: per frequency the raw modal S (EMerge's own S corrections switched
off), the column power constants EMerge scaled S by, V (each terminal path's line integral of the TOTAL E for each
excitation), and per face from the mode fields themselves: beta_n, EMerge's Z0, T_V[k, n] (each path's integral of
mode n), the E-overlap matrix W[i, n] = <e_n, e_i> / <e_i, e_i> that EMerge's S projection uses, and the power Gram
Gp[n, m] = 1/2 \\int e_n x h_m* . n dS.
"""
import json, os, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
MM = 1e-3


def options(case, given):
    o = dict(res=0.15, edge=0.1, order=2, freqs=None, nmodes=None, port="modal", mixed=None, align=False,
             sides="pec", tand=None, hmax=None, curv=None, presolve=None, neff=None, direct=False, tem=False, dedupe=False, threads=8, npath=21, sweep="point", nsamp=5)
    o.update(given)
    o["case"] = case
    if o["freqs"] is None:
        o["freqs"] = {"A": [2, 6, 10], "B": [2, 5, 7.5, 11], "C": [2, 3.5, 10], "D": [2, 6, 10]}[case]
    if o["nmodes"] is None:
        o["nmodes"] = 2 if case in "BC" else 1
    if o["mixed"] is None:
        o["mixed"] = case in "AB"
    return o


def main(argv):
    case, name = argv[1], argv[2]
    o = options(case, json.loads(argv[3]) if len(argv) > 3 else {})
    out = os.path.join(HERE, "runs", name)
    os.makedirs(out, exist_ok=True)
    for v in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "VECLIB_MAXIMUM_THREADS", "RSLAB_NUM_THREADS", "RAYON_NUM_THREADS"):
        os.environ[v] = str(o["threads"])
    import numpy as np
    import emerge as em
    import gmsh
    from emerge._emerge.geometry import GeoVolume, GeoSurface

    if o["dedupe"]:
        # EMerge 3.0.0b2 appends a re-solved frequency's modes to the ones already stored for it (the sweep's mean
        # frequency, and every adaptive sample, are solved twice), so a two-mode face ends up with mode 2 a copy of mode 1.
        # The guard: forget a frequency's modes before they are solved again.
        from emerge._emerge.physics.microwave.microwave_3d import Microwave3D
        solve_modes = Microwave3D.modal_analysis

        def modal_analysis(self, port, *a, freq=None, **k):
            f = self.frequencies[0] if freq is None else freq
            port.available_modes.pop(2 * np.pi * f / 299792458, None)
            return solve_modes(self, port, *a, freq=freq, **k)
        Microwave3D.modal_analysis = modal_analysis

    t0 = time.time()
    sim = em.Simulation("emw1_" + name)
    sim.settings.mw_cap_sp_single = False
    sim.settings.mw_cap_sp_col = False
    sim.settings.mw_recip_sp = False
    sim.settings.size_check = False
    occ = gmsh.model.occ
    faces, sheets, length = build(case, o, em, occ, GeoVolume, GeoSurface)
    sim.commit_geometry()
    sim.mw.set_basis_space(element_order=o["order"])
    sim.mw.set_resolution(o["res"])
    sim.mw.set_frequency([f * 1e9 for f in o["freqs"]])
    for g in sheets:
        sim.mesher.set_boundary_size(g, o["edge"] * MM)
    gmsh.option.setNumber("Mesh.MeshSizeMin", o["edge"] * MM / 1000)
    if o["curv"]:
        sim.mesher.set_curved_boundary_meshing(o["curv"])
    if o["hmax"]:
        sim.mesher.set_global_max_size(o["hmax"] * MM)
    sim.generate_mesh()

    # Outer faces by plane: x = 0 and x = length are the ports; for C the y faces are the sides.
    gmsh.model.occ.synchronize()
    plane = {}
    for _, tag in gmsh.model.getEntities(2):
        up, _ = gmsh.model.getAdjacencies(2, tag)
        if len(up) != 1:
            continue
        c = gmsh.model.occ.getCenterOfMass(2, tag)
        for key, axis, at in (("x0", 0, 0.0), ("x1", 0, length), ("ylo", 1, None), ("yhi", 1, None)):
            if at is not None and abs(c[axis] - at) < 1e-9:
                plane.setdefault(key, []).append(tag)
        if case == "C":
            ys = o["_Y"]
            if abs(c[1] + ys) < 1e-9: plane.setdefault("ylo", []).append(tag)
            if abs(c[1] - ys) < 1e-9: plane.setdefault("yhi", []).append(tag)
    if case == "C" and o["sides"] == "pmc":
        sim.mw.bc.PMC(em.FaceSelection(plane["ylo"] + plane["yhi"]))

    ports = []
    for k, f in enumerate(faces):
        sel = em.FaceSelection(plane[f["plane"]])
        number = k + 1
        if o["port"] == "coax":
            cs = em.CoordinateSystem(em.Axis((0, 1, 0)), em.Axis((0, 0, 1)), em.Axis((1, 0, 0)), (f["x"], 0, 0)) \
                if hasattr(em, "CoordinateSystem") else None
            bc = sim.mw.bc.CoaxPort(sel, number, (o["_rin"], o["_rout"]), cs, er=o["_er"])
        elif o["port"] == "ih":
            bc = sim.mw.bc.WavePortIH(sel, number, number_of_modes=o["nmodes"])
        else:
            bc = sim.mw.bc.ModalPort(sel, number, number_of_modes=o["nmodes"], mixed_materials=o["mixed"],
                                     modetype="TEM" if o["tem"] else None)
        if o["port"] != "coax":
            a, b = f["paths"][0]
            bc.set_integration_line(a, b, 101)       # Z0 and each mode's sign from terminal 1's path, as Palace's entry 1
            if o["align"]:
                bc.align_modes(*[tuple(np.subtract(q, p) / np.linalg.norm(np.subtract(q, p))) for p, q in f["paths"]])
        ports.append((f, bc))
    if o["presolve"]:
        # The port modes solved here rather than by the sweep: `presolve` eigenpairs around neff * k0 (EMerge's own default
        # shift is sqrt(er_max er_min ...) k0, exactly the TEM eigenvalue of a homogeneous face), at the sweep's mean frequency.
        fmean = sum(o["freqs"]) / len(o["freqs"]) * 1e9
        for f, bc in ports:
            sim.mw.modal_analysis(bc, nmodes=o["presolve"], direct=o["direct"], TEM=o["tem"], target_neff=o["neff"], freq=fmean)
            print(json.dumps({"presolved": f["plane"], "betas": [round(complex(m.beta).real, 4) for m in bc.get_modes(2 * 3.141592653589793 * fmean / 299792458)],
                              "types": [m.modetype for m in bc.get_modes(2 * 3.141592653589793 * fmean / 299792458)]}))
    t_mesh = time.time()

    if o["sweep"] == "adaptive":
        data = sim.mw.run_adaptive_sweep(n_initial=o["nsamp"], tol=1e-4, max_samples=40)
    else:
        data = sim.mw.run_sweep()
    t_solve = time.time()
    powers = sim.mw._check_port_powers()

    gl_x, gl_w = np.polynomial.legendre.leggauss(o["npath"])

    def path_points(p, q):
        p, q = np.asarray(p, float), np.asarray(q, float)
        s = (gl_x + 1) / 2
        pts = p[:, None] + (q - p)[:, None] * s[None, :]
        return pts, (q - p), gl_w / 2

    def integrate(func, p, q):
        pts, d, w = path_points(p, q)
        E = func(pts[0], pts[1], pts[2])
        return complex(np.sum(w * (E[0] * d[0] + E[1] * d[1] + E[2] * d[2])))

    mesh = sim.mesh
    rows = []
    fields = list(data.field.iter()) if o["sweep"] == "point" else []
    for fd in fields:
        fd._silent = True
        k0 = fd.k0
        order = [pm.smat_index for pm in fd.port_modes]
        S = np.array(fd._Sp, dtype=complex)
        terms = [(f, bc, n, path) for f, bc in ports for n, path in enumerate(f["paths"])]
        V = np.zeros((len(terms), len(order)), complex)
        for j, idx in enumerate(order):
            fd.excite_port(idx)
            for i, (f, bc, n, (p, q)) in enumerate(terms):
                V[i, j] = integrate(lambda x, y, z: fd.interpolate(x, y, z, usenan=False).E, p, q)
        per_face = []
        for f, bc in ports:
            nm = len(bc.port_modes) if hasattr(bc, "port_modes") else 1
            tris = mesh.get_triangles(bc.tags)
            tv = mesh.tris[:, tris]
            normal = np.asarray(f["normal"], float)
            xq, wq = tri_quadrature(mesh.nodes, tv)
            e = [bc.port_mode_3d_global(xq[0], xq[1], xq[2], k0, which="E", mode_nr=m + 1) for m in range(nm)]
            et = [ei - np.outer(normal, normal @ ei) for ei in e]
            h = [bc.port_mode_3d_global(xq[0], xq[1], xq[2], k0, which="H", mode_nr=m + 1) for m in range(nm)] \
                if o["port"] != "coax" else None
            W = np.array([[np.sum(wq * np.sum(et[n] * np.conj(et[i]), 0)) / np.sum(wq * np.sum(et[i] * np.conj(et[i]), 0))
                           for n in range(nm)] for i in range(nm)])
            Gp = None
            if h is not None:
                Gp = np.array([[0.5 * np.sum(wq * (normal @ np.cross(e[n].T, np.conj(h[m]).T).T)) for m in range(nm)]
                               for n in range(nm)])
            TV = np.array([[integrate(lambda x, y, z, m=m: bc.port_mode_3d_global(x, y, z, k0, which="E", mode_nr=m + 1), p, q)
                            for m in range(nm)] for p, q in f["paths"]])
            if o["port"] == "coax":
                betas, z0s = [bc.get_beta(k0)], [abs(bc.portZ0(k0))]
            else:
                # [:nm]: a mixed-materials port re-solved at the sweep's mean frequency holds that frequency's modes twice
                modes = bc.get_modes(k0)[:nm]
                betas = [complex(m.beta) * k0 / m.k0 if m.modetype == "TEM" and not o["mixed"] else complex(m.beta) for m in modes]
                z0s = [float(abs(m.Z0)) if m.Z0 is not None else float("nan") for m in modes]
            per_face.append(dict(beta=np.array(betas, complex), z0=np.array(z0s), W=W, Gp=Gp, TV=TV,
                                 beta_robin=complex(bc.get_beta(k0)), types=[getattr(m, "modetype", "TEM") for m in (bc.get_modes(k0)[:nm] if o["port"] != "coax" else [None])] if o["port"] != "coax" else ["TEM"]))
        rows.append(dict(f=fd.freq, S=S, V=V, order=[float(x) for x in order],
                         p=np.array([powers[x] for x in order], float), faces=per_face))

    grid = data.scalar.grid
    fgrid = np.ravel(grid.freq)
    smat = np.asarray(grid.Smat)
    meta = dict(options=o, emerge=em.__version__, tets=int(mesh.n_tets), unknowns=int(sim.mw.basis.n_field),
                meshSeconds=round(t_mesh - t0, 2), solveSeconds=round(t_solve - t_mesh, 2),
                faces=[{k: v for k, v in f.items()} for f in faces])
    if o["sweep"] == "adaptive":
        fq = np.union1d(np.linspace(min(o["freqs"]), max(o["freqs"]), 41), o["freqs"]) * 1e9
        meta["adaptiveSamples"] = int(fgrid.size)
        np.savez(os.path.join(out, "adaptive.npz"), f=fq, S=np.asarray(grid.model_Smat(fq)), fs=fgrid, Ss=smat)
    np.save(os.path.join(out, "data.npy"), np.array(rows, dtype=object), allow_pickle=True)
    with open(os.path.join(out, "meta.json"), "w") as fh:
        json.dump(meta, fh, indent=1, default=str)
    print(json.dumps({k: meta[k] for k in ("tets", "unknowns", "meshSeconds", "solveSeconds")}))


def tri_quadrature(nodes, tv):
    """A degree-4, 6-point rule on every triangle: points (3, T*6) and weights (T*6,) carrying each triangle's area."""
    import numpy as np
    a, b = 0.445948490915965, 0.091576213509771
    wa, wb = 0.223381589678011, 0.109951743655322
    bary = np.array([[a, a, 1 - 2 * a], [a, 1 - 2 * a, a], [1 - 2 * a, a, a],
                     [b, b, 1 - 2 * b], [b, 1 - 2 * b, b], [1 - 2 * b, b, b]])
    w = np.array([wa] * 3 + [wb] * 3)
    P = [nodes[:, tv[k]] for k in range(3)]
    area = 0.5 * np.linalg.norm(np.cross((P[1] - P[0]).T, (P[2] - P[0]).T), axis=1)
    pts = sum(bary[:, k][None, :, None] * P[k].T[:, None, :] for k in range(3))   # (T, 6, 3)
    return pts.reshape(-1, 3).T, (area[:, None] * w[None, :]).ravel()


def build(case, o, em, occ, GeoVolume, GeoSurface):
    """The geometry. Returns the port faces (plane key, x, inward normal, terminal paths ground -> strip in metres), the
    sheets (for edge refinement) and the line's length (m)."""
    def mat(er, tand):
        return em.Material(er=er, tand=tand if o["tand"] is None else o["tand"])

    if case in "AB":
        h, ell, Y, lid = 0.508, 15.0, 4.5, 3.0
        strips = [(-0.7875, 0.7875)] if case == "A" else [(-(0.15 + 1.2), -0.15), (0.15, 0.15 + 2.0)]
        air = GeoVolume(occ.addBox(0, -Y * MM, 0, ell * MM, 2 * Y * MM, (h + lid) * MM), name="air")
        air.set_material(em.lib.AIR).prio_set(0)
        lam = GeoVolume(occ.addBox(0, -Y * MM, 0, ell * MM, 2 * Y * MM, h * MM), name="laminate")
        lam.set_material(mat(2.2, 0.0 if case == "A" else 0.0009)).prio_set(1)
        sheets = []
        for k, (ya, yb) in enumerate(strips):
            g = GeoSurface(occ.addRectangle(0, ya * MM, h * MM, ell * MM, (yb - ya) * MM), name=f"strip{k}")
            g.set_material(em.lib.PEC)
            sheets.append(g)
        paths = lambda x: [((x, (ya + yb) / 2 * MM, 0.0), (x, (ya + yb) / 2 * MM, h * MM)) for ya, yb in strips]
        return ([dict(plane="x0", x=0.0, normal=(1, 0, 0), paths=paths(0.0)),
                 dict(plane="x1", x=ell * MM, normal=(-1, 0, 0), paths=paths(ell * MM))], sheets, ell * MM)
    if case == "C":
        w, s, b, ell = 1.65, 0.4, 2.0, 15.0
        Y = s / 2 + 11 * w
        o["_Y"] = Y * MM
        fill = GeoVolume(occ.addBox(0, -Y * MM, -b / 2 * MM, ell * MM, 2 * Y * MM, b * MM), name="fill")
        fill.set_material(mat(2.1, 0.0)).prio_set(0)
        strips = [(-(s / 2 + w), -s / 2), (s / 2, s / 2 + w)]
        sheets = []
        for k, (ya, yb) in enumerate(strips):
            g = GeoSurface(occ.addRectangle(0, ya * MM, 0, ell * MM, (yb - ya) * MM), name=f"strip{k}")
            g.set_material(em.lib.PEC)
            sheets.append(g)
        paths = lambda x: [((x, (ya + yb) / 2 * MM, -b / 2 * MM), (x, (ya + yb) / 2 * MM, 0.0)) for ya, yb in strips]
        return ([dict(plane="x0", x=0.0, normal=(1, 0, 0), paths=paths(0.0)),
                 dict(plane="x1", x=ell * MM, normal=(-1, 0, 0), paths=paths(ell * MM))], sheets, ell * MM)
    if case == "D":
        rin, rout, ell = 0.2, 0.67, 10.0
        o["_rin"], o["_rout"], o["_er"] = rin * MM, rout * MM, 2.1
        bore = occ.addCylinder(0, 0, 0, ell * MM, 0, 0, rout * MM)
        pin = occ.addCylinder(0, 0, 0, ell * MM, 0, 0, rin * MM)
        cut, _ = occ.cut([(3, bore)], [(3, pin)])
        occ.synchronize()
        die = GeoVolume([t for d, t in cut if d == 3], name="ptfe")
        die.set_material(mat(2.1, 0.0)).prio_set(0)
        paths = lambda x: [((x, rout * MM, 0.0), (x, rin * MM, 0.0))]   # housing -> pin, as the Launch's arrow
        return ([dict(plane="x0", x=0.0, normal=(1, 0, 0), paths=paths(0.0)),
                 dict(plane="x1", x=ell * MM, normal=(-1, 0, 0), paths=paths(ell * MM))], [], ell * MM)
    raise SystemExit("case is A, B, C or D")


if __name__ == "__main__":
    main(sys.argv)
