# Q4 — does Gmsh read our shape? (decides D12)

**Yes, every B-rep format version.** Gmsh 4.15.2 — the validated version — carries **OCCT 7.9.3**
(`gmsh-info.txt`), and it reads a B-rep text file written by OCCT 8.0.1 in all three format versions 8.0.1
offers. Each import fragments cleanly against an air box, both named faces come back by a tight box query
with **exactly one hit each**, and the second-order mesh completes.

| hand-off | file | volumes | surfaces | fillet hits | top hits | mesh (order 2) | nodes / elements |
|---|---|---|---|---|---|---|---|
| B-rep, `TopTools_FormatVersion_VERSION_1` | `part-v1.brep` (4,160 B) | 2 | 14 | **1** | **1** | exit 0 | 22,988 / 19,644 |
| B-rep, `VERSION_2` | `part-v2.brep` (4,711 B) | 2 | 14 | **1** | **1** | exit 0 | 22,988 / 19,644 |
| B-rep, `VERSION_3` (= `CURRENT`, the default) | `part-v3.brep` (4,156 B) | 2 | 14 | **1** | **1** | exit 0 | 22,988 / 19,644 |
| STEP (AP214, written in µm) + `Geometry.OCCTargetUnit = "UM"` | `part-um.step` (22,192 B) | 2 | 14 | **1** | **1** | exit 0 | 22,908 / 19,590 |
| the same STEP, **without** `OCCTargetUnit` | `part-um.step` | 2 | 14 | **0** | **0** | exit 0 | 23,727 / 19,439 |

The part is q4's `part.txt`: a 1000 × 800 × 500 µm lid, an r = 150 µm bore, its top rim filleted at 50 µm
(one torus face). Each `.geo` sets what `GmshGeoWriter` sets — `OCCBooleanPreserveNumbering = 1`,
`OCCBoundsUseStl = 1`, `e = 1e-3`, `Mesh.ElementOrder = 2`, `HighOrderOptimize = 2`,
`MeshSizeFromCurvature = 12` — and queries `Surface In BoundingBox` with the harness's **exact** face box
(`BRepBndLib::AddOptimal`, no tolerance) ± `e`. `run.sh` reproduces every row; the mesh is not kept. Wall
clock per mesh 3.9–4.8 s, mostly the order-2 optimisation.

**The format versions differ only where this part cannot see it.** v1 and v3 are the same bytes apart from
the header line; v2 adds the pcurves' UV end points. None of them carries triangles (`withTriangles =
false`), so v3's per-vertex normals for triangulation-only faces never arise. The three meshes are node for
node the same size.

**STEP works, and would lose three things B-rep keeps:**
1. **Its unit is a trap.** Gmsh converts a STEP file to OCCT's default unit (mm) unless `OCCTargetUnit`
   says otherwise. The last row read the µm file as a part 1 × 0.8 × 0.5 "units" across (`Q4 part box 0 0 0
   .. 1 0.8 0.5`), meshed it without complaint, and **both queries silently found nothing**. A B-rep has no
   unit: our numbers go in and come out as the same numbers.
2. **Its bytes are not reproducible.** Every STEP file carries a write timestamp in `FILE_NAME`
   (`'2026-09-27T02:02:28'`), so two exports of one shape differ. `GmshGeoWriter`'s rule — *an unchanged file
   reuses the mesh beside it* — needs byte-stable input; a B-rep gives it (Q7), a STEP does not unless the
   worker rewrites the header.
3. **It is not the same shape.** Going through the STEP translator and its healing gave a slightly different
   mesh (22,908 nodes against 22,988) of what should be identical geometry; the B-rep is the worker's shape
   exactly.

STEP is also 5× the bytes. Nothing was found that STEP carries and Gmsh needs: Gmsh ignores the names and
colours STEP adds, and the face table already travels in circuitRF's own records (overview §1i).

## Recommendation for D12

**The worker's B-rep, written as `TopTools_FormatVersion_VERSION_1`, without triangles.** Version 1 is read
by every OCCT (the v3 format appeared in OCCT 7.6), so pinning it costs nothing measurable here and keeps a
user's older Gmsh working if the validated list ever widens. Write it **explicitly**: `BRepTools::Write`'s
two-argument overloads write `CURRENT` (v3) despite the header comment calling v1 the default.

One caveat for brief 65, from selftest: B-rep text carries each shape's **flag bits**, and a validity check
sets "checked" while a STEP export clears it (`0111000` → `0101000`). Geometry is unaffected, but the bytes
change — so the worker should write the hand-off file from the operation's result, not after other work has
run on the same shape, or its bytes will depend on what else was asked of it.
