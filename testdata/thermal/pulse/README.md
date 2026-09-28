# Z2 — a pulse train through a thermal network: the closed-form peak against harmonic sums

**Pins:** pulsed operation via Z_th(jω) (brief 80 has no transient solver): the periodic-steady-state
temperature of a network driven by a rectangular pulse train, and **how many harmonics** a Fourier-series
evaluation of it needs. **Used by:** brief 80.

## Physics

A Foster network Z(jω) = Σᵢ Rᵢ/(1 + jωτᵢ) driven by power P for t_on in every period T. In periodic
steady state each term rises during the pulse and decays after it; the peak is at the end of the pulse:

    ΔT_peak = P Σᵢ Rᵢ (1 − e^{−t_on/τᵢ}) / (1 − e^{−T/τᵢ})

and the whole waveform is the same per-term exponentials (`make_pulse.py`, `closed_form`). The Fourier
route is ΔT(t) = P̄ Z(0) + 2 Re Σ_{n≥1} Pₙ Z(jnω₀) e^{jnω₀t}, Pₙ = P(1 − e^{−jnω₀t_on})/(jnω₀T).

| Case | Network | P | T | t_on |
|---|---|---|---|---|
| Z2a | 0.5 K/W, 1 µs · 1.0 K/W, 100 µs · 2.0 K/W, 10 ms | 10 W | 1 ms | 100 µs (10 %) |
| Z2b | 0.2, 0.3 µs · 0.4, 5 µs · 0.8, 200 µs · 1.5, 50 ms | 100 W | 1 ms | 10 µs (1 %, radar-like) |
| Z2c | the **silicon slab of Z1** over 1 mm² — an infinite Foster network | 1 W | 1 ms | 100 µs |

Peaks: Z2a **13.412686 K**, Z2b **60.029601 K**, Z2c **0.616432 K**. (A slab with a fixed back face is
exactly Σ_m R_m/(1 + jωτ_m) with λ_m = (m + ½)π/L, R_m = 2/(k L λ_m²), τ_m = ρc/(kλ_m²); Z2c sums 200,000
terms and adds the tail, Σ_{m≥M} R_m = (2L/(kπ²)) ψ′(M + ½), 7e-7 K.)

## How many harmonics — a finding for brief 80 (`truncation-*.csv`)

The plain sum converges **slowly**, because a rectangular pulse's harmonics fall like 1/n and a
Foster network's impedance too: the error at the peak falls like **1/N**.

| N | Z2a plain | Z2a asymptote removed | Z2b plain | Z2b asymptote removed | Z2c (slab) plain |
|---|---|---|---|---|---|
| 100 | −12.5 % | 6.8e-2 | −23 % | 3.8e-1 | −5.9 % |
| 1,000 | −1.9 % | 1.5e-4 | −5.9 % | 4.2e-3 | −1.9 % |
| 10,000 | −0.19 % | 1.6e-7 | −0.63 % | 4.8e-6 | −0.59 % |
| 100,000 | −0.019 % | 1.6e-10 | −0.063 % | 4.8e-9 | −0.19 % |

**Removing the high-frequency asymptote fixes it for a lumped network.** Z(jω) → Σ Rᵢ/(jωτᵢ) = 1/(jωC);
subtracting that from every harmonic and adding its exact time-domain response back (the running integral
of the zero-mean pulse train over C — a sawtooth, `fourier_tail_corrected`) leaves coefficients falling
like 1/n³: 1e-5 at a few thousand harmonics instead of a million.

**A distributed network (Z2c) converges like N^(−1/2)** — its impedance falls like ω^(−1/2), not ω⁻¹ —
and its asymptote is the semi-infinite 1/(kγ), which a 1/(jωC) subtraction does not remove. Brief 80's
FEM Z_th is distributed. The plain sum is not usable for a peak there (0.06 % needs ~10⁶ harmonics), and
the fix has to take out the √ω asymptote — whose response to a pulse train, 2q√(t/(πρck)) per edge,
summed over past pulses, is closed-form — or fit a Foster network to the FEM Z_th first and use the
closed form above. The findings note carries this forward.

## Files

| File | What |
|---|---|
| `pulse-Z2a.csv`, `pulse-Z2b.csv` | `t [s]`, `dT [K]` over one period (closed form), 2000 points |
| `truncation-Z2a.csv`, `truncation-Z2b.csv` | N; the plain Fourier value at t_on and its error; the asymptote-removed value and its relative error |
| `truncation-Z2c.csv` | N; the plain Fourier value and error for the slab |
| `pulse.json` | the cases, peaks, mean and minimum rises, the truncation tables |

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_pulse.py     # ~1 s
