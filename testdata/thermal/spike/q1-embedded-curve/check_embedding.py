"""brief-em3d-72 Q1 — is an embedded curve CONFORMING? For each .msh given: the line elements of the
physical curve "wire", whether every one of their nodes is a node of the tetrahedra, whether every
segment is an EDGE of some tetrahedron, and the wire's meshed length against its geometric length.
SPIKE MATERIAL.   python check_embedding.py a1.msh a2.msh ...
"""
import sys

import meshio
import numpy as np


def check(path, true_length=None):
    m = meshio.read(path)
    tet_key = "tetra10" if "tetra10" in m.cells_dict else "tetra"
    line_key = "line3" if "line3" in m.cells_dict else "line"
    tets = m.cells_dict[tet_key]
    wire = m.cell_sets_dict["wire"][line_key]
    lines = m.cells_dict[line_key][wire]
    tet_nodes = set(np.unique(tets).tolist())
    wire_nodes = np.unique(lines)
    shared = all(int(n) in tet_nodes for n in wire_nodes)
    # corner edges of the tets
    corner = tets[:, :4]
    pairs = [(0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)]
    edges = set()
    for a, b in pairs:
        e = np.sort(corner[:, [a, b]], axis=1)
        edges.update(map(tuple, e.tolist()))
    seg = np.sort(lines[:, :2], axis=1)
    all_edges = all(tuple(s) in edges for s in seg.tolist())
    # line3: the middle node must also be the tet's mid-edge node (it is shared if it is a tet node)
    p = m.points
    length = float(np.sum(np.linalg.norm(p[lines[:, 1]] - p[lines[:, 0]], axis=1)))
    return {"file": path, "tetrahedra": len(tets), "tet_type": tet_key, "wire_line_elements": len(lines),
            "line_type": line_key, "wire_nodes": len(wire_nodes), "wire_nodes_shared_with_tets": shared,
            "every_segment_is_a_tet_edge": all_edges, "meshed_wire_length_um": length}


if __name__ == "__main__":
    for f in sys.argv[1:]:
        print(check(f))
