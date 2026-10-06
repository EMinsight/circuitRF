# Palace terminal ports by symmetry — brief-em3d-119

Runs behind `src/Design/RESOLVED.md` § "Palace terminal ports by symmetry — brief-em3d-119". Palace **v0.18.1**
(`0dc74cd`, 8 MPI ranks), Apple M4, 16 GB. No meshes are committed: each config names `mesh.msh`. Log launch lines are
shortened. Every run is at FE order 2, 5 GHz (geometry A) or 2 and 6 GHz (geometry B), and excites port 1 only unless
stated; the rest of each matrix follows from the structure's symmetry.

## The meshes

The harness that made them is `tools/palace-symmetry-spike/` (its README has the `run.sh` line for each directory).
113-a's harness was not kept, so its mesh generator was rebuilt from 113-a's README (a Gmsh-Python transcription of
`examples/cpw/mesh/mesh.jl`, `generate_cpw_wave_mesh`: OCC box, strip volumes removed with their end caps, Distance +
Threshold size field from the strip surfaces, `Mesh.Algorithm` 6 / `Algorithm3D` 10, first-order MSH 2.2). **Checked
against 113-a's own fixtures before use:** `a-touch-r2` reproduces `../palace/pair-a-per-line` (Z_PV 68.8686 Ω,
identical to every printed digit; S within 0.01 dB / 0.05°; ND 296,190 against 294,656), and the same construction on
geometry B reproduces `../palace/pair-b-per-line` (Z_PV 39.428 against 39.420 Ω, S within 0.05 dB / 0.5°; not
committed).

Attributes: 1 air, 2 substrate; ports 4 (x = 0, line 1), 5 (x = ℓ, line 1), 6 (x = 0, line 2), 7 (x = ℓ, line 2);
A: 10 box PEC, 11 strip surfaces; B: 8 ground, 9 end-face remainder (PEC), 10 box sides and lid, 11 strip surfaces;
12 the PMC strip between two ports (route A); 13 the symmetry cut plane y = 0 (route B).

Mesh names: `r2`/`r3` are 113-a's densities (A: near-strip 1.2·2⁻ʳ, far 2.0·2⁻ʳ mm, DistMin W, DistMax 2b; B: near
1.1·2⁻ʳ, far 2.0·2⁻ʳ mm, DistMin W, DistMax 3 mm). **`r3eX` adds a second Threshold field on the strips' long edges:
SizeMin X mm, DistMin 0.02, DistMax 0.3 mm, growing to the r3 near-strip size** (`r3e` alone means X = 0.02). This is
the one mesh departure from 113-a, and the reason is measured: at r3 the error halves from r2 and is the 3D line's own
impedance (the odd mode reads 69.4 Ω against 70.885 Ω), not the route.

## Geometry A (113-a's air stripline pair: b 2, W 1.2, S 0.4, t 0.02, ℓ 15 mm, PEC walls 6 mm beyond the strips)

| Directory | Departure from `../palace/pair-a-per-line` |
|---|---|
| `a-touch-r2`, `a-touch-r3` | Port 1 excited only. Otherwise 113-a's per-line run (ports meeting at the midline): the ODD-mode run of route A |
| `a-gap{0.3,0.15}-r2`, `a-gap{0.3,0.15,0.075}-r3` | **Route A.** The two port rectangles on each end face separated by a strip \|y\| < g/2 (g in mm), attribute 12, listed under `Boundaries.PMC`. Only that. The EVEN-mode run |
| `a-half{PMC,PEC}-r2`, `-r3`, `-r3e0.05`, `-r3e0.03`, `-r3e0.02` | **Route B.** The box cut at y = 0, line 1 only, attribute 13 under `PMC` (even) or `PEC` (odd); one wave port per end covering the half face |
| `asym-gap0.075-r3`, `asym-touch-r3` | Brief §4d: line 2 is 1.32 mm wide (10 % wider), the walls 6 mm beyond the wider strip (±7.52 mm), ports 1 **and** 3 excited. Route A's two runs on it |

## Geometry B (113-a's microstrip pair: εr 3.5, h 0.508, W 1.1, S 0.3, t 0.017, ℓ 15 mm)

| Directory | Departure |
|---|---|
| `b-half{PMC,PEC}-r2` | Route B on `../palace/pair-b-per-line`'s box (absorbing order 1 sides and lid 3 mm out; port windows 2.54 mm beyond the strip and above the substrate, rest of the end face PEC). Only the cut |
| `b-half{PMC,PEC}-r2-shield` | The above with attribute 10 (sides and lid) **PEC instead of absorbing**. Only that |
| `b-half{PMC,PEC}-r2-shield-full` | The above with the port covering the **whole** end face (no PEC remainder). Only that |
| `b-half{PMC,PEC}-r3e-shield-full` | The above at r3 with the 0.02 mm edge field. 333-338 s each, over the brief's five minutes |

## References

Not files: computed in the scratch harness and given in `RESOLVED.md`. Geometry A: a 2D finite-volume Laplace solve of the
cross-section on a graded grid (101.944 / 70.881 Ω at its finest, converging on 113-a's 101.95 / 70.885); the
asymmetric pair's exact 4-port is the homogeneous-air multiconductor line built from the same solver's 2 × 2
capacitance matrix (Zc = C⁻¹/c, β = k₀ for every mode). Geometry B: the same solver with a cell-wise permittivity
(quasi-static); with 113-a's walls 20 mm out it gives 58.611 Ω / 2.9101 and 40.704 Ω / 2.4283 (113-a: 58.62 / 2.910,
40.72 / 2.4285), and for the shielded box here (sides ±4.25 mm, PEC lid at 3.508 mm) 57.103 Ω / 2.8269 and
40.646 Ω / 2.4245.
