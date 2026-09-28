# W3 — the wire at a harmonic current: RF heat from the exact internal impedance

**Pins:** RF heat per unit length at f ≫ the skin corner, per harmonic, entered as PEAK phasors, and
evaluated at the local temperature. **Used by:** brief 78.

## Physics

As W1 (equal ends at 25 °C, L = 1 mm, ρ(T) linear, k constant — parameters from
[`../metals/constants.json`](../metals/)), with the heat per unit length

    q′(T) = Σₙ ½ |Iₙ|² R′_ac(fₙ, T)       (Iₙ the PEAK phasor of harmonic n; DC contributes I₀² R′_dc)

— harmonics are orthogonal, so their powers add and phase does not matter. **R′_ac** is the real part of
the exact internal impedance per unit length of a round wire of radius a (Schelkunoff; Ramo, Whinnery &
Van Duzer, *Fields and Waves in Communication Electronics*, the round-wire internal impedance):

    Z′ = (γ ρ / (2π a)) · J₀(γa) / J₁(γa),     γ = (1 − j)/δ,   δ = √(ρ / (π f μ₀)),

with ρ = ρ(T) at the local temperature. It tends to ρ/A at DC and to a/(2δ) + ¼ times that at high
frequency (checked: 8.762 vs 8.756 at 10 GHz for 25.4 µm, ρ = 2.2e-8). The Bessel functions are SciPy's
(`jve`, exponentially scaled so the ratio stays finite at a/δ ≈ 30) — **independent of circuitRF's own
implementation** in `src/WBond/InternalImpedance.cs`, which is the point.

The skin corner (δ = a) is ~34 MHz for 1 mil gold and a quarter of that for 2 mil, so 1 GHz and above is
f ≫ corner.

## The cases (each metal, d = 25.4 and 50.8 µm)

| Name | Harmonics (peak, as a fraction of the wire's W1 I*) | total RMS |
|---|---|---|
| `dc-0.25` | DC 0.25 | 0.25 I* — **must equal W1 exactly** (it does, to 8e-12 K) |
| `1GHz-rms0.10`, `1GHz-rms0.25` | 1 GHz, √2 × 0.10 or 0.25 | 0.10 / 0.25 I* |
| `10GHz-rms0.10`, `10GHz-rms0.25` | 10 GHz, likewise | |
| `2GHz-h123` | 2, 4, 6 GHz at 0.30, 0.15, 0.1118 | 0.25 I* |

What "treating RF as DC" gets wrong, in heat at 25 °C (1 mil gold, 0.25 I* RMS): 27.4 W/m as DC,
80.1 W/m at 1 GHz, 237 W/m at 10 GHz — **8.7× more at 10 GHz**, and the centre at 269 °C rather than
48 °C. The unsafe direction, as the overview says.

## Files

| File | What |
|---|---|
| `rac-<metal>.csv` | `d [m]`, `T [degC]` (20, 85, 125), `f [Hz]` (1 MHz – 31.6 GHz, 46 points), `Rac' [Ohm/m]`, `Rac'/Rdc' [1]` |
| `<metal>.csv` | `d [m]`, `excitation`, `s [m]`, `T [degC]` |
| `wire-rf.json` | per profile: the harmonics (f, peak current), total RMS, q′ at T_end and as-if-DC, the centre temperature, the checks; the skin corner per diameter |

## Method and check

Shooting from the centre (as W2), checked by `solve_bvp`: largest difference **9.2e-9 K**. During the
shooting a trial trajectory can dive below T₀ − 1/α where the linear ρ turns negative; ρ is floored at
1e-3 ρ₀ there, which no converged profile comes near.

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_wire_rf.py     # ~3 s
