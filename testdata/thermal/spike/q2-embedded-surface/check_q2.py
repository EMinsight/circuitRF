"""brief-em3d-72 Q2/Q3 — for a physical surface group in a .msh: how many triangles, whether every
triangle is a face of the tetrahedra, and WHICH VOLUMES lie on each side (from the tetrahedra that share
the face and their elementary/physical tags). SPIKE MATERIAL.   python check_q2.py q2.msh strip foot
"""
import sys
from collections import Counter, defaultdict

import meshio
import numpy as np


def sides(path, groups):
    m = meshio.read(path)
    tk = "tetra10" if "tetra10" in m.cells_dict else "tetra"
    fk = "triangle6" if "triangle6" in m.cells_dict else "triangle"
    tets = m.cells_dict[tk][:, :4]
    phys = m.cell_data_dict["gmsh:physical"][tk]
    geom = m.cell_data_dict["gmsh:geometrical"][tk]
    faces = defaultdict(list)
    for t, tet in enumerate(tets):
        for drop in range(4):
            faces[tuple(sorted(np.delete(tet, drop).tolist()))].append(t)
    tri = m.cells_dict[fk][:, :3]
    tri_phys = m.cell_data_dict["gmsh:physical"][fk]
    names = {v[0]: k for k, v in m.field_data.items()}
    out = {}
    for g in groups:
        tag = m.field_data[g][0]
        sel = tri[tri_phys == tag]
        conform = 0
        side_count = Counter()
        for t in sel:
            owners = faces.get(tuple(sorted(t.tolist())), [])
            if owners:
                conform += 1
            side_count[tuple(sorted((names.get(int(phys[o]), "?"), int(geom[o])) for o in owners))] += 1
        out[g] = {"triangles": len(sel), "triangles_that_are_tet_faces": conform,
                  "sides (physical, elementary) -> triangles": {str(k): v for k, v in side_count.items()}}
    return out


if __name__ == "__main__":
    import json
    print(json.dumps(sides(sys.argv[1], sys.argv[2:]), indent=1))
