# W1 — a wire between fixed, equal end temperatures, DC current, ρ(T) linear, k constant

**Pins:** the 1D wire element, Joule heat with ρ(T), the runaway current and the fusing current.
**Used by:** brief 77. Every W gate compares **T along the whole wire** (101 points here), not only its
maximum, because the owner's display is the temperature along the wire.

## Derivation (standard physics — derived here, not attributed to any paper)

A round wire of diameter d, area A = πd²/4, length L, thermal conductivity k, resistivity
ρ(T) = ρ₀[1 + α(T − T₀)] with T₀ = 20 °C, carrying a DC current I, both ends held at T_end, no lateral loss.
Per unit length the heat balance is

    k A T″ + I² ρ₀ [1 + α (T − T₀)] / A = 0.

Put θ = T − T₀ + 1/α. Then θ″ + β² θ = 0 with **β² = I² ρ₀ α / (k A²)**, and with x measured from the
centre (x = s − L/2) and θ(±L/2) = θ_end = T_end − T₀ + 1/α:

    T(x) = T₀ − 1/α + (T_end − T₀ + 1/α) · cos(β x) / cos(β L / 2).

**Runaway.** The solution exists only while βL/2 < π/2; at βL/2 = π/2 the centre temperature diverges.
That limit is the runaway current

    I* = (π / L) · A · √(k / (ρ₀ α)).

**Fusing (centre melt).** The centre reaches the melting point T_m when cos(βL/2) = r with
r = (T_end − T₀ + 1/α)/(T_m − T₀ + 1/α), so

    I_fuse = (2A / L) · √(k / (ρ₀ α)) · arccos(r)   <  I*.

## The brief's worked check (reproduced)

1 mil gold (d = 25.4 µm), L = 1 mm, ρ₀ = 2.44e-8 Ω·m and α = 0.0034 /K at 20 °C, k = 312 W/(m·K), ends
at 25 °C, T_m = 1064.18 °C: **I\* = 3.0871 A, I_fuse = 2.6441 A** (the brief: ≈ 3.09 A, ≈ 2.64 A).
Its profiles are in `worked-check.csv`.

## The cases (R-em3d72-2c)

For each of **gold, copper, aluminium, silver**, with ρ₀ = ρ₂₀, α = α₂₀ and k = k₂₀ from
[`../metals/constants.json`](../metals/) (the pure-metal tables' 20 °C values and tangent), and T_m the
ITS-90 melting point:

- diameters 25.4 µm and 50.8 µm, plus 38.1 µm for copper and aluminium;
- lengths 1 mm and 3 mm; ends at 25 °C and at 100 °C;
- currents 0.25, 0.5, 0.9 and 0.99 of that wire's own I*.

At a fixed fraction of I*, T(s/L) is the same for every d and L (βL is fixed), so those rows differ only
in scale — they test that the solver handles the geometry, not new physics. At 0.9 and 0.99 I* the centre
is far above the melting point (e.g. ~19,700 °C for 0.99): the closed form knows nothing of melting, and
the rows are kept as a test of the solver's approach to runaway, flagged `centre_above_melting` in the JSON.

## Files

| File | What |
|---|---|
| `gold.csv`, `copper.csv`, `aluminium.csv`, `silver.csv` | `d [m]`, `L [m]`, `T_end [degC]`, `I/I* [1]`, `I [A]`, `s [m]` (arc length from one end), `T [degC]` |
| `worked-check.csv` | the same for the worked check's parameters |
| `wire-rho.json` | per metal: the parameters, I* and I_fuse for every d/L/T_end, and per profile the centre temperature and the check below |

## Check

Every profile is also solved by SciPy's collocation BVP solver (`solve_bvp`, in the scaled variable s/L,
relative tolerance 1e-8, loosened to at most 1e-6 where a 0.99 I* profile exhausts the node budget); the
largest difference from the closed form anywhere is **9.5e-6 K** (on a ~19,700 °C centre).

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_wire_rho.py      # ~4 s; needs ../metals first
