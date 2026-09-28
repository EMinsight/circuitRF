# S2 — three-layer composite slab with one interface resistance

**Pins:** interface elements — the temperature JUMP ΔT = q″R″ across a contact resistance R″ (m²·K/W),
and that the rest of the stack is unaffected by it. **Used by:** brief 76.

## Physics

Lateral faces insulated (1 mm × 1 mm), so the field is 1D. Layers bottom to top, z up:

| Layer | Thickness | k |
|---|---|---|
| base (copper-like) | 1 mm | 390 W/(m·K) |
| attach (solder-like) | 50 µm | 57 W/(m·K) |
| die (silicon-like) | 100 µm | 150 W/(m·K) |

The base's bottom face is held at 25 °C and a uniform flux q″ = 1e6 W/m² enters the die's top face. With
no source inside, the flux is q″ through every layer, T is linear in each with slope q″/k, and across the
contact between *attach* and *die* it jumps by exactly q″R″.

| Case | R″ (attach/die) | T below | T above | jump | T top |
|---|---|---|---|---|---|
| S2a | 1e-5 m²·K/W | 28.4413 °C | 38.4413 °C | **10.000 K** | 39.1080 °C |
| S2b | 0 | 28.4413 °C | 28.4413 °C | 0 | 29.1080 °C |

S2b is S2a with the resistance removed: every value below the interface is identical, and every value
above it is 10 K lower. A solver that duplicates the interface nodes (brief 76) must reproduce S2b with a
zero resistance, which is the degenerate case of the same elements.

## Files

`composite-S2a.csv`, `composite-S2b.csv` — `z [m]`, `T [degC]`, `layer`. Each layer has 41 rows; its end
rows repeat the interface z, so across the resistance two rows share one z and differ by the jump.
`composite.json` — the stack, the interface and the temperatures above.

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_composite.py

Closed form (series thermal resistances). NumPy only.
