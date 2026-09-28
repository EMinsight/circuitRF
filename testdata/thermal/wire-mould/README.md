# W4 — the wire on the axis of a coaxial cylinder of mould compound

**Pins:** lateral heat loss from a wire into an overmould, and the runaway current that loss raises.
**Used by:** brief 77.

## Derivation

A wire (radius r_w = d/2) on the axis of a cylinder of mould compound (conductivity k_m, outer radius r_o)
whose outer surface is held at T_amb. Radial conduction through the annulus gives a lateral conductance
per unit length

    g′ = 2π k_m / ln(r_o / r_w),

and the wire's balance becomes k A T″ + I²ρ₀[1 + α(T − T₀)]/A − g′(T − T_amb) = 0. With

    m² = (g′ − I²ρ₀α/A) / (k A),     T_p = (g′ T_amb + I²ρ₀(1 − αT₀)/A) / (g′ − I²ρ₀α/A)

(T_p the particular constant), and ends at T_a (s = 0) and T_b (s = L):

    T(s) = T_p + [(T_a − T_p) S(m(L − s)) + (T_b − T_p) S(m s)] / S(m L),

S = sinh when m² > 0 and S = sin (with |m|) when m² < 0. Equal ends reduce to the brief's
T = T_p + (T_end − T_p) cosh(m x)/cosh(mL/2), x from the centre. The runaway is where the sin form's
denominator vanishes, |m| L = π:

    I*² = (A / (ρ₀ α)) · (g′ + k A π² / L²).

**What this model is.** The mould's own AXIAL conduction is neglected and the annulus is treated as
radial only — the model the brief states. A 3D mesh of the mould with the wire embedded as a line will
not reproduce this exactly, and not only for that reason: see the findings note (Q1) on the line-source
singularity, which is the harder of the two.

## The cases

k_m = 0.8 W/(m·K) (a representative epoxy mould compound; typical values 0.6–1), r_o = 100 µm and
500 µm, the four metals with W1's diameters and lengths, ends and ambient
(T_a, T_b, T_amb) = (25, 25, 25), (100, 100, 100) and (100, 25, 25) °C, and currents 0.25, 0.5, 0.9, 0.99
of **this** model's I*. Both branches are exercised (cosh/sinh at the lower currents, cos/sin near I*).

The mould raises the runaway current substantially — for 1 mil, 1 mm wires by ×1.36 (gold, r_o = 500 µm)
to ×1.75 (aluminium, r_o = 100 µm); `wire-mould.json` has every case beside the no-mould I*.

## Files

| File | What |
|---|---|
| `<metal>.csv` | `d [m]`, `r_outer [m]`, `L [m]`, `T_a`, `T_b`, `T_amb [degC]`, `I/I*_mould [1]`, `I [A]`, `s [m]`, `T [degC]` |
| `wire-mould.json` | per case: g′, I* with and without the mould, m², the branch, T_p, T_max, the BVP check |

## Check

Every profile is also solved by `solve_bvp`; largest difference **8.9e-6 K**.

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_wire_mould.py     # ~20 s
