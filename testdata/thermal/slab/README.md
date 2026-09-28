# S1 — slab: fixed temperature on one face, uniform flux into the other

**Pins:** assembly, a Dirichlet face, a Neumann (flux) source, a volumetric source, and the P2 element's
exactness — both fields below are polynomials of degree ≤ 2, which second-order elements reproduce
**exactly on any mesh**, so a gate on this case can be tight (1e-9 K), not a convergence study.
**Used by:** brief 74.

## Physics

A box 1 mm × 1 mm × L, z up. Every lateral face insulated, so the 3D field is exactly the 1D one. The
bottom face (z = 0) is held at T_b; a uniform flux q″ enters the top face (z = L); a uniform volumetric
source q‴ may act throughout. With −k T″ = q‴, T(0) = T_b, k T′(L) = q″:

    T(z) = T_b + (q″ + q‴ L) z / k − q‴ z² / (2k)

| Case | L | k | T_b | q″ | q‴ | T(L) | power |
|---|---|---|---|---|---|---|---|
| S1a | 1 mm | 150 W/(m·K) | 25 °C | 1e6 W/m² | 0 | 31.6667 °C | 1 W |
| S1b | 1 mm | 150 W/(m·K) | 25 °C | 1e6 W/m² | 1e9 W/m³ | 35.0 °C | 2 W |

## Files

`slab-S1a.csv`, `slab-S1b.csv` — `z [m]`, `T [degC]` at 101 points. `slab.json` — the parameters and T(L).

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_slab.py

Closed form, derived above (textbook: Carslaw & Jaeger, *Conduction of Heat in Solids*, §2.3). NumPy only.
