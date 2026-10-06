"""Gmsh meshes for brief-em3d-119, transcribed from Palace's examples/cpw/mesh/mesh.jl
(generate_cpw_wave_mesh): OCC box, metal volume removed with its x-normal end caps, Distance +
Threshold size field from the metal, Algorithm 6 / 3D 10, MSH 2.2 binary. mm units (L0 = 1e-3).

Geometry A (air stripline pair) and B (microstrip pair). Port faces:
  shared: one rectangle per end covering both strips, attribute 4 (x0) / 5 (xL) (brief 124)
  touch : one rectangle per strip, the end face split at the midline (113-a's per-line run)
  gap   : the two rectangles separated by a strip |y| < g/2, its own attribute (12), given PMC
  half  : y <= 0 only; the cut plane y = 0 is attribute 13 (PMC or PEC in the config)
Attributes: volume 1 (air), 2 (substrate, B); ports 4 (x0, y<0), 5 (xL, y<0), 6 (x0, y>0),
7 (xL, y>0); 10 outer PEC (A) / absorbing (B); 11 strip surfaces; 8 ground (B); 9 end-face
remainder PEC (B); 12 gap strip; 13 cut plane.
"""
import sys, json, gmsh

def build(path, geom="A", mode="touch", g=0.0, r=2, wa=None, wb=None, verbose=0, edge=None, fullport=False, Y=None):
    occ = gmsh.model.occ
    gmsh.initialize(); gmsh.option.setNumber("General.Verbosity", verbose)
    gmsh.model.add("m")
    L = 15.0
    if geom == "A":
        b, t, S = 2.0, 0.02, 0.4
        W = 1.2; wa = wa or W; wb = wb or W
        Y = Y or 0.2 + max(wa, wb) + 6.0     # PEC walls 6 mm beyond the strips (symmetric box)
        z0, z1 = -b / 2, b / 2
        zs0, zs1 = -t / 2, t / 2
        lnear, lfar, dmin, dmax = 1.2 * 2.0 ** -r, 2.0 * 2.0 ** -r, W, 2 * b
        pz0, pz1, py = z0, z1, Y             # port rectangle = whole end face
    else:
        h, t, S = 0.508, 0.017, 0.3
        W = 1.1; wa = wa or W; wb = wb or W
        Y = Y or S / 2 + max(wa, wb) + 3.0
        z0, z1 = 0.0, h + 3.0
        zs0, zs1 = h, h + t
        lnear, lfar, dmin, dmax = 1.1 * 2.0 ** -r, 2.0 * 2.0 ** -r, W, 3.0
        pz0, pz1, py = 0.0, h + 2.54, S / 2 + max(wa, wb) + 2.54
        if fullport: pz1, py = z1, Y
    ylo = -Y; yhi = 0.0 if mode == "half" else Y
    dom = occ.addBox(0, ylo, z0, L, yhi - ylo, z1 - z0)
    strips = [occ.addBox(0, -S / 2 - wa, zs0, L, wa, zs1 - zs0)]
    if mode != "half":
        strips.append(occ.addBox(0, S / 2, zs0, L, wb, zs1 - zs0))
    vols = [(3, dom)]
    if geom == "B":
        sub = occ.addBox(0, ylo, 0, L, yhi - ylo, h)
        vols.append((3, sub))
    # imprint tools on both end faces
    tools = []
    def rect(x, ya, yb, za, zb):
        p = [occ.addPoint(x, ya, za), occ.addPoint(x, yb, za), occ.addPoint(x, yb, zb), occ.addPoint(x, ya, zb)]
        l = [occ.addLine(p[i], p[(i + 1) % 4]) for i in range(4)]
        return occ.addPlaneSurface([occ.addCurveLoop(l)])
    for x in (0.0, L):
        if mode == "gap":
            tools.append((2, rect(x, -g / 2, g / 2, z0, z1 if geom == "A" else pz1)))
        if geom == "B" or mode == "touch":
            # B: port rectangles inside a PEC end face; A touch: split at the midline
            if geom == "B":
                if mode == "gap":
                    tools.append((2, rect(x, -py, -g / 2, pz0, pz1))); tools.append((2, rect(x, g / 2, py, pz0, pz1)))
                elif mode == "half":
                    tools.append((2, rect(x, -py, 0.0, pz0, pz1)))
                elif mode == "shared":
                    tools.append((2, rect(x, -py, py, pz0, pz1)))
                else:
                    tools.append((2, rect(x, -py, 0.0, pz0, pz1))); tools.append((2, rect(x, 0.0, py, pz0, pz1)))
            else:
                a = occ.addPoint(x, 0, z0); c = occ.addPoint(x, 0, z1)
                tools.append((1, occ.addLine(a, c)))
    occ.synchronize()
    cut, _ = occ.cut(vols, [(3, s) for s in strips], removeObject=True, removeTool=True)
    occ.synchronize()
    out, _ = occ.fragment(cut, tools)
    occ.synchronize()
    v3 = [d for d in gmsh.model.getEntities(3)]
    bnd = set(abs(t) for _, t in gmsh.model.getBoundary(v3, combined=False, oriented=False))
    # drop dangling surfaces (imprint pieces over the strip holes)
    for d, tg in gmsh.model.getEntities(2):
        if tg not in bnd:
            up, _ = gmsh.model.getAdjacencies(2, tg)
            if len(up) == 0:
                occ.remove([(2, tg)], recursive=True)
    occ.synchronize()
    eps = 1e-6
    groups = {}
    def add(k, tg): groups.setdefault(k, []).append(tg)
    air, subv = [], []
    for _, tg in gmsh.model.getEntities(3):
        x0_, y0_, zz0, x1_, y1_, zz1 = gmsh.model.getBoundingBox(3, tg)
        (subv if geom == "B" and zz1 < z0 + 0.6 else air).append(tg)
    for _, tg in gmsh.model.getEntities(2):
        up, _ = gmsh.model.getAdjacencies(2, tg)
        bx = gmsh.model.getBoundingBox(2, tg)
        xa, ya, za, xb, yb, zb = bx
        yc, zc = (ya + yb) / 2, (za + zb) / 2
        if len(up) == 2:
            continue  # interior (substrate/air interface)
        if xb - xa < eps and (abs(xa) < eps or abs(xa - L) < eps):
            near = abs(xa) < eps
            if mode == "gap" and abs(ya + g / 2) < eps and abs(yb - g / 2) < eps:
                add(12, tg); continue
            inport = (ya > -py - eps and yb < py + eps and za > pz0 - eps and zb < pz1 + eps)
            if geom == "B" and not inport:
                add(9, tg); continue
            if yc < 0 or mode == "shared":   # shared: ONE port face per end covering both strips
                add(4 if near else 5, tg)
            else:
                add(6 if near else 7, tg)
            continue
        if mode == "half" and yb - ya < eps and abs(ya) < eps:
            add(13, tg); continue
        on_box = (abs(ya - ylo) < eps and yb - ya < eps) or (abs(yb - yhi) < eps and yb - ya < eps) \
            or (zb - za < eps and (abs(za - z0) < eps or abs(za - z1) < eps))
        if on_box:
            if geom == "B" and zb - za < eps and abs(za) < eps:
                add(8, tg)
            else:
                add(10, tg)
        else:
            add(11, tg)
    gmsh.model.addPhysicalGroup(3, air, 1)
    if subv: gmsh.model.addPhysicalGroup(3, subv, 2)
    for k, v in sorted(groups.items()):
        gmsh.model.addPhysicalGroup(2, v, k)
    metal = groups[11]
    gmsh.option.setNumber("Mesh.MeshSizeMin", lnear)
    gmsh.option.setNumber("Mesh.MeshSizeMax", lfar)
    gmsh.option.setNumber("Mesh.MeshSizeFromPoints", 0)
    gmsh.option.setNumber("Mesh.MeshSizeFromCurvature", 0)
    gmsh.option.setNumber("Mesh.MeshSizeExtendFromBoundary", 0)
    pts = [t for d, t in gmsh.model.getBoundary([(2, s) for s in metal], False, True, True) if d == 0]
    crv = [t for d, t in gmsh.model.getBoundary([(2, s) for s in metal], False, False, False) if d == 1]
    f = gmsh.model.mesh.field
    f.add("Distance", 1); f.setNumbers(1, "PointsList", pts); f.setNumbers(1, "CurvesList", crv)
    f.setNumbers(1, "SurfacesList", metal); f.setNumber(1, "Sampling", int(L / lnear) + 1)
    f.add("Threshold", 2); f.setNumber(2, "InField", 1); f.setNumber(2, "SizeMin", lnear)
    f.setNumber(2, "SizeMax", lfar); f.setNumber(2, "DistMin", dmin); f.setNumber(2, "DistMax", dmax)
    fl = [2]
    if edge:  # local refinement at the strips' long edges: [size, distmin, distmax]
        ecrv = [c for c in crv if (lambda bb: bb[3] - bb[0] > L - 1e-6)(gmsh.model.getBoundingBox(1, c))]
        f.add("Distance", 3); f.setNumbers(3, "CurvesList", ecrv); f.setNumber(3, "Sampling", int(L / edge[0]) + 1)
        f.add("Threshold", 4); f.setNumber(4, "InField", 3); f.setNumber(4, "SizeMin", edge[0])
        f.setNumber(4, "SizeMax", lnear); f.setNumber(4, "DistMin", edge[1]); f.setNumber(4, "DistMax", edge[2])
        fl.append(4); gmsh.option.setNumber("Mesh.MeshSizeMin", edge[0])
    f.add("Min", 101); f.setNumbers(101, "FieldsList", fl); f.setAsBackgroundMesh(101)
    gmsh.option.setNumber("Mesh.Algorithm", 6); gmsh.option.setNumber("Mesh.Algorithm3D", 10)
    for s in metal: gmsh.model.mesh.setAlgorithm(2, s, 8)
    gmsh.model.mesh.generate(3)
    gmsh.option.setNumber("Mesh.MshFileVersion", 2.2); gmsh.option.setNumber("Mesh.Binary", 1)
    gmsh.write(path)
    summary = {k: len(v) for k, v in sorted(groups.items())}
    ntet = len(gmsh.model.mesh.getElementsByType(4)[0])
    gmsh.finalize()
    return summary, ntet

if __name__ == "__main__":
    a = json.loads(sys.argv[2])
    print(build(sys.argv[1], **a))
