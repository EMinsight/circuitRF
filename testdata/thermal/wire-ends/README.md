# W1b — unequal end temperatures, and the constant-ρ limit

**Pins:** T(s) along a wire whose die-pad end is hotter than its lead end; the hot spot's POSITION,
shifted toward the hotter end; and the α → 0 limit, where the trigonometric form must give way to a
parabola without the solver noticing. **Used by:** brief 77.

## Derivation

As W1 (`../wire-rho/README.md`), with s the arc length from end a (s = 0, T_a) to end b (s = L, T_b).
With θ = T − T₀ + 1/α and β² = I²ρ₀α/(kA²), the general solution of θ″ + β²θ = 0 through both ends is

    θ(s) = [θ_a sin(β (L − s)) + θ_b sin(β s)] / sin(β L).

The runaway limit is the same, βL = π (the same I*). **The hot spot** is where dθ/ds = 0,

    θ_b cos(β s) = θ_a cos(β (L − s)),

solved by `brentq`; if dθ/ds does not change sign inside the wire, the hottest point is the hotter end.

**The constant-ρ limit** (α → 0; ρ = ρ₀ everywhere) is the parabola

    T(s) = T_a + (T_b − T_a) s / L + (I² ρ₀ / (2 k A²)) s (L − s),

with its maximum at s = L/2 + (T_b − T_a) k A / (q′ L), q′ = I²ρ₀/A, clamped to the wire. The sin form
evaluated at α = 1e-7 /K differs from it by at most **0.010 K** (the O(α) term, not round-off).

## The cases

Die pad T_a = 100 °C at s = 0, lead T_b = 25 °C at s = L. For each of the four metals (parameters from
[`../metals/constants.json`](../metals/)), the diameters and lengths of W1, currents 0.25, 0.5, 0.9, 0.99 of
I*. The constant-ρ files use the **same currents in amperes**.

As the current rises the hot spot walks from the hot end toward the centre (gold, any d, L):

| I/I* | hot spot s/L | T_max |
|---|---|---|
| 0.25 | 0.1414 | 102.17 °C |
| 0.50 | 0.4241 | 195.26 °C |
| 0.90 | 0.4933 | 1749.96 °C |
| 0.99 | 0.4994 | 19667.95 °C |

## Files

| File | What |
|---|---|
| `<metal>.csv` | `d [m]`, `L [m]`, `T_a [degC]`, `T_b [degC]`, `I/I* [1]`, `I [A]`, `s [m]`, `T [degC]` |
| `<metal>-constant-rho.csv` | the parabola at the same currents; the `I/I*` column names the linear-ρ row the current came from |
| `wire-ends.json` | parameters, and per profile the hot spot's s and T_max, and the checks |

## Check

Every linear-ρ profile is also solved by `solve_bvp`; the largest difference from the closed form is
**9.8e-6 K**.

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_wire_ends.py     # ~5 s
