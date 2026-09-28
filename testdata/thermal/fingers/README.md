# S6 — eight source fingers on a layered substrate: the Rth matrix

**Pins:** mutual heating between neighbouring sources — the thermal-resistance matrix
R_ij = (mean temperature rise over finger i) / (power in finger j), which brief 80's Rth matrix and Foster
network are built from. **Used by:** brief 80.

## The problem

A 1 mm × 1 mm substrate, every side face insulated, bottom held at 25 °C:

| Layer (top down) | Thickness | k |
|---|---|---|
| 1 (silicon-carbide-like) | 100 µm | 370 W/(m·K) |
| 2 (copper-tungsten-like) | 500 µm | 200 W/(m·K) |

Eight uniform-flux strips on the top face, each 10 µm × 200 µm, on a 40 µm pitch, the array centred.

## The reference — an independent 3D FEM, refined below 0.1 %

scikit-fem P2 tetrahedra on Gmsh meshes of the **half** model (the plane through the fingers'
mid-length is a mirror, and exciting any one finger keeps that symmetry), CG + pyamg to 1e-12, one AMG
hierarchy and eight right-hand sides. Graded from `h_source` at the fingers to `h_max`:

| h_source / h_max (µm) | P2 dofs | largest change in R from the rung before | largest difference from the series |
|---|---|---|---|
| 8 / 120 | 14,323 | — | 1.71 % |
| 4 / 100 | 34,329 | 1.25 % | 0.50 % |
| 2 / 80 | 91,918 | 0.34 % | 0.16 % |
| 1.2 / 60 | 209,759 | **0.088 %** | 0.069 % |
| 0.7 / 50 | 489,923 | **0.031 %** | **0.039 %** |

`rth-matrix.csv` is the last rung. In K/W (symmetric to 1e-4, as it must be):

```
19.828   8.128   5.774   4.577   3.817   3.282   2.884   2.578
 8.128  19.759   8.072   5.730   4.544   3.796   3.272   2.884
 5.774   8.072  19.715   8.039   5.708   4.534   3.796   3.282
 4.577   5.730   8.039  19.694   8.029   5.708   4.544   3.817
 …  (rows 5–8 mirror rows 4–1)
```

A finger's own resistance is ~19.8 K/W and its neighbour adds 8.1 K/W per watt — so with all eight on,
mutual heating triples the rise of an inner finger (`fingers.json`,
`all_on_mean_rise_K`: 15.0 K for an inner finger at 0.25 W each, against 4.9 K alone).

## The check — the layered-channel series

The same Fourier series as S5 ([`../tools/spreading.py`](../tools/spreading.py); derivation and citation in
[`../spreading/README.md`](../spreading/README.md)), each finger an eccentric source, at
(M, N) = (4000, 1000), (8000, 2000), (16000, 4000), with one Richardson step. It agrees with the finest FEM
to 0.039 % everywhere in the matrix, and it also gives the temperature across the whole array with every
finger on (`all-on-line.csv`), which the FEM does not.

## Files

| File | What |
|---|---|
| `rth-matrix.csv` | the 8 × 8 matrix (K/W), finest FEM rung |
| `all-on-line.csv` | `x [m]`, `T [degC]` along the fingers' mid-length line, all eight at 0.25 W (series), 1001 points over ±250 µm |
| `ladder.csv` | the FEM ladder |
| `fingers.json` | the case, the finger rectangles, the FEM matrix, the extrapolated series matrix, both ladders |

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_fingers.py     # ~75 s, ~2 GB for the series; needs gmsh on PATH
