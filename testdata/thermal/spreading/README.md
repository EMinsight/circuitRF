# S5 — a rectangular heat source on a two-layer substrate (spreading resistance)

**Pins:** 3D assembly on a layered solid, a flux patch on a face, and the spreading resistance a 1D model
misses (here 96 % of the total). **Used by:** briefs 74 and 76, and the spike's Q4 and Q5.

## The problem

A 2 mm × 2 mm flux channel of two layers, every side face insulated, the bottom face held at 25 °C:

| Layer (top down) | Thickness | k |
|---|---|---|
| 1 (silicon-like) | 100 µm | 150 W/(m·K) |
| 2 (copper-like) | 1 mm | 390 W/(m·K) |

A uniform flux over a 200 µm × 100 µm rectangle centred on the top face carries **1 W**.

## Reference 1 — the Fourier series

Expanding in cos(mπx/a)·cos(nπy/b) (a = b = 2 mm), each mode obeys θ″ = λ²θ in each layer,
λ² = (mπ/a)² + (nπ/b)², with θ and kθ′ continuous at the interface and θ = 0 at the bottom. The mode's
top-face temperature is its flux coefficient times the stack's input impedance, built bottom up:

    Z₂ = tanh(λ t₂)/(k₂ λ),     Z = (Z₂ + tanh(λ t₁)/(k₁ λ)) / (1 + Z₂ k₁ λ tanh(λ t₁)),     Z(0) = t₁/k₁ + t₂/k₂.

This is the series solution for an eccentric rectangular source on a compound (two-layer) rectangular
flux channel published by **Y. S. Muzychka, J. R. Culham and M. M. Yovanovich**, *Thermal spreading
resistance of eccentric heat sources on rectangular flux channels*, J. Electronic Packaging 125 (2003),
and **Y. S. Muzychka, M. M. Yovanovich and J. R. Culham**, *Thermal spreading resistance in compound and
orthotropic systems*, J. Thermophysics and Heat Transfer 18 (2004) — there with a convective sink of
conductance h under the lower layer; here h → ∞ (a fixed bottom temperature). It was derived for this
case, not transcribed, and is implemented in [`../tools/spreading.py`](../tools/spreading.py).

Truncated at M = N = 2000, 4000, 8000 modes (`spreading.json`): the centre moves by 1.2e-4 K between the
last two, and the source mean, which converges like 1/M, is extrapolated by one Richardson step.

| Quantity | Value |
|---|---|
| Source-centre (peak) temperature | **46.7076 °C** (R = 21.7076 K/W) |
| Source-mean temperature | **43.0079 °C** (R = 18.0079 K/W) |
| 1D resistance t₁/(k₁A) + t₂/(k₂A) | 0.8077 K/W |
| Spreading resistance (mean − 1D) | 17.200 K/W |

## Reference 2 — an independent 3D FEM, refined below 0.1 %

scikit-fem P2 tetrahedra on Gmsh meshes of the quarter model (x ≥ a/2, y ≥ b/2; both mirror planes are
insulated, which is exact), CG + pyamg to 1e-12. The mesh is graded from `h_source` at the source to
`h_max` far away (Gmsh Distance/Threshold fields — the ones the circuitRF lowering already writes):

| h_source / h_max (µm) | P2 dofs | centre vs series | mean vs series |
|---|---|---|---|
| 40 / 250 | 1,904 | −0.070 % | −0.90 % |
| 20 / 200 | 3,264 | −0.026 % | −0.42 % |
| 10 / 160 | 6,120 | −0.034 % | −0.14 % |
| 5 / 130 | 11,935 | −0.024 % | −0.060 % |
| 2.5 / 110 | 28,650 | −0.024 % | −0.037 % |
| 1.25 / 80 | 90,602 | −0.016 % | −0.022 % |
| 0.8 / 60 | 205,944 | **−0.009 %** | **−0.013 %** |

Every rung from 2.5 µm down is within 0.1 % of the series and moves by less than 0.1 % from the one
before. `ladder.csv` has the numbers; the Gmsh meshes are regenerated into `mesh/` (git-ignored).

## Files

| File | What |
|---|---|
| `spreading.json` | the case, the reference temperatures, the series at each truncation, the FEM ladder |
| `surface-line.csv` | `x [m]`, `T [degC]` along the top face through the source centre (y = b/2), from the centre to the edge, 201 points (series) |
| `ladder.csv` | the FEM ladder above |

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_spreading.py     # ~15 s; needs gmsh on PATH

Gmsh (GPL) is called as a program on the `.geo` text the script writes; it is not imported and nothing of
it is committed. Meshing is single-threaded (`-nt 1`) so the ladder is reproducible.
