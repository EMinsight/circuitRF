# Planar line references — CPWG and SLIN

The externally generated references for the grounded coplanar waveguide and stripline models of
`docs/sonnet-briefs/brief-artsch-1-cpwg-and-stripline-models.md` (R-as1-6). The models and every number
quoted about them are in `docs/design/planar-line-models.md`. Produced on 2026-10-08.

**Nothing in this directory reads, imports or calls circuitRF**, and nothing in `src/` or `tests/` may
import these scripts; the tests read only the two text files.

| File | What it is | Proves |
|---|---|---|
| `formulas.py` → `implementation-references.txt` | The closed forms written a second time: complete elliptic integrals from `scipy.special` (not an AGM), the hyperbolic moduli evaluated directly, every constant restated. Z0, εeff and the two attenuations at 0, 1, 10 and 40 GHz. | that the C# **is** the formulas — `CpwgModelTests` / `SlinModelTests` hold it to 1e-6 relative |
| `fieldsolve.py` → `physics-references.txt` | A quasi-static 2-D finite-element solve of each cross-section with and without the dielectric: Z0 = 1/(c·√(C·C_air)), εeff = C/C_air. P2 triangles on a tensor grid graded toward every conductor edge, refined twice and Richardson-extrapolated. | that the formulas **are** the physics, to each row's stated tolerance |

## Versions and machine

| | |
|---|---|
| Machine | Apple M4, macOS 27.0 |
| Python | 3.13.1 |
| numpy / scipy | 2.5.3 / 1.18.1 |
| scikit-fem | **12.0.2** (BSD-3-Clause) — the field solver |

Both scripts run in a throwaway virtual environment (`python3 -m venv v && v/bin/pip install scikit-fem`):

```
v/bin/python -I formulas.py   > implementation-references.txt
v/bin/python -I fieldsolve.py > physics-references.txt      # about a minute
```

## How far the field solve can be trusted

Before use it was run on the two cross-sections with exact answers: the zero-thickness centred
stripline (Cohn, K(k)/K(k′) with k = sech(πW/2b)) and the air-filled coplanar line (K(k′)/K(k)). Both
converge at first order — the edge singularity sets it — with the error halving per level (1.8e-4,
8.9e-5, 4.6e-5), so the two-level Richardson extrapolation lands within 1e-6 of the exact value. Each
row's own `dZ`/`dE` is the size of its extrapolation; all are ≤ 1.2e-6 except the zero-thickness SLIN
(4.3e-5). That first check is also what showed the textbooks' "30π" and "60π" are η₀/4 and η₀/2 with
η₀ rounded to 120π — a 0.07 % offset the models therefore do not carry.

## What the comparison found (Z0 / εeff, model against field)

| Line | Geometry (W, G or H1, H or H2, T, εr) | Z0 | εeff | Branch |
|---|---|---|---|---|
| CPWG | 1.0 mm, 0.2 mm, 0.508 mm, 35 µm, 3.66 | −0.61 % | −0.84 % | coplanar |
| CPWG | 0.5 mm, 0.15 mm, 0.254 mm, 18 µm, 3.0 | +0.06 % | −0.35 % | coplanar |
| CPWG | 0.3 mm, 0.5 mm, 1.6 mm, 35 µm, 4.4 | +0.59 % | +0.07 % | coplanar |
| CPWG | 2.0 mm, 0.25 mm, 0.8 mm, 35 µm, 4.4 | −0.35 % | −0.57 % | coplanar |
| CPWG | 70 µm, 50 µm, 100 µm, 3 µm, 12.9 | +0.28 % | +0.21 % | coplanar |
| CPWG | 0.4 mm, 0.4 mm, 0.2 mm, 18 µm, 10.2 | −1.99 % | +3.27 % | microstrip |
| CPWG | 1.0 mm, 1.5 mm, 1.0 mm, 35 µm, 4.4 | +0.32 % | +3.62 % | microstrip |
| CPWG | 1.0 mm, 6.0 mm, 1.0 mm, 35 µm, 4.4 | −1.12 % | +1.53 % | microstrip |
| SLIN | 0.3 mm, 0.3 mm, 0.3 mm, 17 µm, 3.5 | −0.01 % | exact | centred |
| SLIN | 1.2 mm, 0.5 mm, 0.5 mm, 35 µm, 2.2 | −0.03 % | exact | centred |
| SLIN | 0.1 mm, 0.2 mm, 0.2 mm, 17 µm, 4.2 | −0.03 % | exact | centred |
| SLIN | 0.2 mm, 0.15 mm, 0.3 mm, 17 µm, 3.5 | +1.24 % | exact | offset 2:1 |
| SLIN | 0.25 mm, 0.1 mm, 0.4 mm, 35 µm, 3.5 | +3.38 % | exact | offset 4:1 — outside the stated range, and warned |
| SLIN | 0.5 mm, 0.4 mm, 0.4 mm, 0, 10.2 | 0.00 % | exact | centred, zero thickness (Cohn exact) |

The microstrip-branch εeff error is MLIN's own: Hammerstad–Jensen's thickness correction on 35 µm copper
is as far off at G/H = 6, where the coplanar ground no longer matters. Dispersion and loss have no
physics reference here — a quasi-static solve has neither — and are covered by the implementation table
and the published forms they come from.
