# Z1 — the thermal impedance of a slab, Z_th(jω)

**Pins:** the frequency-domain solve (K + jωC) T = q. **Used by:** brief 80.

## Physics

A slab of thickness L, conductivity k, density ρ and specific heat c; the back face (x = L) held at a
fixed temperature, a periodic flux q̃″e^{jωt} into the front face. The temperature phasor at the front,
per unit flux, is (Carslaw & Jaeger, *Conduction of Heat in Solids*, periodic heating of a slab)

    Z″_th(jω) = T̃(0) / q̃″ = tanh(γ L) / (k γ),       γ = √(jω ρ c / k),

in K·m²/W (divide by the heated area for K/W). It tends to L/k at DC and to 1/(kγ) — the semi-infinite
medium, ∝ ω^(−1/2) — once the thermal penetration depth is short of L.

| Material | L | k | ρ | c | L/k (K·m²/W) | ρcL²/k |
|---|---|---|---|---|---|---|
| silicon | 100 µm | 148 | 2329 | 705 | 6.757e-7 | 111 µs |
| copper | 1 mm | 401 | 8960 | 385 | 2.494e-6 | 8.60 ms |

k at 300 K from Ho, Powell & Liley (1974); ρ and c at 25 °C from the CRC Handbook of Chemistry and Physics.

## Check

An independent numerical solution: second-order finite differences of −kT″ + jωρcT = 0 on 4000 cells,
graded (cubically) toward the heated face so the penetration depth is resolved at 10 MHz, a half-cell flux
balance at x = 0, solved with SciPy's sparse LU. It agrees with the closed form to **7.1e-6** (silicon) and
**3.9e-6** (copper) relative, at every frequency.

## Files

`zth-silicon.csv`, `zth-copper.csv` — `f [Hz]` (0.1 Hz – 10 MHz, 81 points, logarithmic), `Re`, `Im`,
`|Z''th|` in K·m²/W, `arg` in degrees. `zth-slab.json` — the materials, L/k, the diffusion time, and the
finite-difference agreement.

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_zth_slab.py     # ~1 s
