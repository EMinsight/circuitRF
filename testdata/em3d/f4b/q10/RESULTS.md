# Q10 — openEMS reads our tessellation

**Yes, both formats, and the mesh is watertight.**

`occt_probe q10` tessellates q4's part (the bored, filleted lid) with `BRepMesh_IncrementalMesh` at a **5 µm
absolute linear deflection and 0.5 rad angular deflection** (a tenth of a 50 µm FDTD cell) and writes
`part.stl` (binary, float32, µm) and `part.ply` (ASCII, merged vertices, µm). `mesh.txt`:

| | |
|---|---|
| per-face nodes / triangles | 346 / 484 |
| merged through the **edge polygons** (`Poly_PolygonOnTriangulation`, per face occurrence — so a seam's two sides meet) | 242 vertices; **0** edges used once, **0** used more than twice, **0** degenerate triangles → **watertight** |
| merged by **exact coordinate** | 242 vertices, also watertight — OCCT writes a shared edge's nodes to bit-identical coordinates on both faces |

`case-stl.xml` and `case-ply.xml` are hand-written CSXCAD files: one `Metal` whose only primitive is a
`PolyhedronReader` of the file, an excitation, a 50 µm grid, PEC walls, **10 timesteps**. openEMS
0.37.0-rc3:

| run | exit | "No primitives found" | PEC edges in `--debug-PEC`'s `PEC_dump.vtp` |
|---|---|---|---|
| `case-stl.xml` (binary STL) | 0 | **absent** | **8,838** (3,683 points) |
| `case-ply.xml` (ASCII PLY) | 0 | **absent** | **8,838** — the dump is byte-identical to the STL run's |
| `case-missing.xml` (control: a file that does not exist) | **0** | `Warning: Invalid primitive found in property: part!` / `Warning: No primitives found in property: part!` | **0** |

Logs: `openems-stl.log`, `openems-ply.log`, `openems-missing.log`. The control repeats F0 Q8's warning: a
missing file still exits 0, so brief 65's existence check before the run stays necessary. A binary STL reads
as well as F0's ASCII one; the two formats give the same staircase to the byte.
