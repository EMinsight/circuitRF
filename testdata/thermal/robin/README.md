# S3 — slab with convection (Robin) faces

**Pins:** the Robin term, −k ∂T/∂n = h (T − T_amb) on a face, alone and on two faces with different h and
ambient. Both fields are polynomials of degree ≤ 2, so P2 reproduces them exactly. **Used by:** brief 74.

## Physics

A 2 mm slab, k = 20 W/(m·K), lateral faces insulated (1 mm × 1 mm), z up.

**S3a** — a flux q″ = 1e4 W/m² enters the top face; the bottom convects to T_amb = 25 °C with
h = 1000 W/(m²·K). Everything that enters leaves through the bottom, so

    T(0) = T_amb + q″/h = 35 °C exactly,     T(z) = T(0) + q″ z / k,     T(L) = 36 °C.

**S3b** — a uniform volumetric source q‴ = 5e7 W/m³, the bottom convecting to 25 °C with h = 2000, the
top to 40 °C with h = 200. With T = −q‴z²/(2k) + a z + b, the two Robin conditions are two linear
equations in a and b (solved in `make_robin.py`): T(0) = 71.4286 °C, T(L) = 75.7143 °C, maximum
75.7398 °C inside. The energy balance (what the two faces carry away equals q‴L) closes to 0 W/m².

## Files

`robin-S3a.csv`, `robin-S3b.csv` — `z [m]`, `T [degC]` at 101 points. `robin.json` — parameters, the face
and maximum temperatures, and the balance residual.

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_robin.py

Closed form. NumPy only.
