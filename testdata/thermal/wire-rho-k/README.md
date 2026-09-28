# W2 — the wire with the metal's ρ(T) AND k(T) tables (conductive balance)

**Pins:** conductive balance with both dependences, and the current at which a steady state stops
existing. **Used by:** brief 77.

## Method

As W1 (equal ends, DC, no lateral loss), with ρ(T) and k(T) taken from
[`../metals/<metal>.csv`](../metals/), both interpolated **piecewise-linearly in T** (clamped). No closed
form exists, so two independent numerical solutions:

- **the reference — shooting from the centre**: by symmetry T′(L/2) = 0, so for a trial centre
  temperature T_c the IVP (d/ds)(kA T′) = −I²ρ(T)/A is integrated from the centre to an end with SciPy's
  `DOP853` (rtol = atol = 1e-13), and T_c is found by `brentq` so the end is at T_end. The LOWEST such T_c
  is taken — the stable branch (below);
- **the check — collocation**: `solve_bvp` on the whole wire. The largest difference between the two
  anywhere is **6.1e-8 K**.

## The steady-state limit — a finding

For each wire, the script maps **I(T_c)**: the current whose steady state has centre temperature T_c.
With W1's linear ρ and constant k it rises monotonically to I* as T_c → ∞. **With the tables it can peak
below the melting point** — a fold. Above the peak there is no steady state at all; below it there are two,
and the one with the hotter centre is unstable. So the physical limit is

    I_limit = max over T_c ≤ T_m of I(T_c)        — "fold" if the maximum is inside, "melt" if it is at T_m.

| Metal | ends 25 °C | ends 100 °C |
|---|---|---|
| gold | **fold** at T_c ≈ 876 °C | **fold** at T_c ≈ 1023 °C |
| copper | **fold** at T_c ≈ 1007 °C | melt |
| aluminium | melt | melt |
| silver | melt | melt |

The fold current is only 0.03–0.6 % above the centre-melt current, but it is a different kind of answer:
**a Newton solver bracketing the runaway (overview §1g) finds the fold, and a gold wire at 25 °C ends
never has a steady state with its centre between ~876 °C and melting.** The currents of the profiles are
0.25, 0.5, 0.9, 0.99 × **I_limit**.

## How much the tables matter (1 mil, 1 mm, ends 25 °C)

| Metal | I_limit (tables) | ρ table, k constant | W1 linear model's I_fuse | W1 / tables |
|---|---|---|---|---|
| gold | 2.3832 A | 2.4939 A | 2.7156 A | **1.146** |
| copper | 3.0987 A | 3.2300 A | 3.3991 A | 1.097 |
| aluminium | 1.8317 A | 1.8584 A | 1.8699 A | 1.021 |
| silver | 3.3287 A | 3.4382 A | 3.6404 A | 1.094 |

**The linear-ρ model overstates gold's fusing current by 15 %**, the unsafe direction: most
(of the logarithm, about 60 %) is ρ(T)'s upward curvature (the table ends 19.5 % above the 20 °C tangent,
`../metals/README.md`: 2.7156 → 2.4939 A) and about 40 % is k(T) falling with temperature (2.4939 →
2.3701 A at centre melt) — which is why k(T) belongs in the loop.
Aluminium is nearly linear to its (low) melting point and barely moves.

## Files

| File | What |
|---|---|
| `<metal>.csv` | `d [m]`, `L [m]`, `T_end [degC]`, `I/I_limit [1]`, `I [A]`, `s [m]`, `T [degC]`; the d/L/end grid of W1 |
| `wire-rho-k.json` | per case: I_limit, its kind and centre temperature, the centre-melt current, the ρ-table/k-constant current, the W1 linear-model currents, the I(T_c) curve (25 points), and per profile T_c and the BVP check |

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_wire_rho_k.py     # ~5 min; needs ../metals first
