# Thermal: Die to Heatsink

Power in, temperature out. A GaN-on-SiC power transistor die is sintered to a copper–molybdenum flange, the flange is soldered to
a two-layer board with a field of plated vias, and the board sits on a thermal interface material on a heatsink held at
`Ths` = 85 °C. The setup sweeps the transistor's dissipated power, `Pdiss`, from 3 W to 11 W and reads the
junction-to-case and junction-to-heatsink thermal resistances.

The chapter that teaches this is **Thermal** in **Help ▸ circuitRF Documentation**. It quotes the same numbers as this file.

**What to expect when you press Run:** about **1 min 17 s** and **1.1 GB** for the three power points on the reference machine
(an Apple M4, the Debug build), on a mesh of 543,980 tetrahedra. Thermal needs **Gmsh**; the first run without it offers to install it.

## What is here

| Cell | View | What it is |
|---|---|---|
| **Board** | layout, µm | 10 × 8 mm of 508 µm FR-4 with 70 µm copper both sides: a pad under the flange on top, a plane underneath, and a 5 × 3 field of plated vias (0.3 mm drill, 25 µm wall) on a 1 mm pitch |
| **Die to Heatsink** | 3D view, µm | The board placed as `B1`; a 100 µm thermal interface material under it; the 6 × 4 × 1 mm flange; the 2.5 × 1 × 0.1 mm SiC die; the transistor as one heat-source sheet; five probes; the setup `Steady` |

Every material is one of circuitRF's generic materials, from `tech/generic-materials.cmat`.

## How it is modelled

- **The transistor** is the heat-source sheet `fet`, 0.8 × 0.25 mm on the die's top: its fingers' area as one sheet. At this scale
  the 2 µm GaN epitaxy is not drawn — drawn, it would force micrometre elements over the whole die. **Thermal Channel vs
  Surface** draws it, one finger at a time.
- **The bond lines** — 20 µm of sintered silver under the die, 50 µm of solder under the flange — are **contact resistances**,
  each its thickness over its conductivity (`ContactResistances` in the 3D view: 1E-07 and 8.62E-07 m²·K/W). A thin layer
  drawn as a solid meshes at its own thickness across its whole area, and these two alone would multiply the run time.
- **The heatsink** is the bottom face of the interface material, held at `Ths`. Every other face is insulated.

## What it reads

| Probe | Where | Statistic |
|---|---|---|
| `die_top` | the die's top face | Max — the junction |
| `flange_bot` | the flange's bottom face | Avg — **the case** |
| `flange_bot_hot` | the same face | Max |
| `via_centre` | the centre via's barrel | Max |
| `board_under` | the board's underside, on the interface material | Avg |

The measures: `Rth_jc = (Tmax(die_top) - Tavg(flange_bot)) / Pdiss`, `Rth_jc_hot` (the same with the flange's hottest point),
`Rth_ja = (Tmax(die_top) - Ths) / Pdiss`, and `T_interface = Tavg(board_under)`.

**"Case temperature" here is the flange bottom's AVERAGE.** A lab's definition may differ — a thermocouple under the centre of the
flange reads close to its hottest point — and the difference is not small: at 11 W, `Rth_jc` is **5.83 K/W** against the average
and `Rth_jc_hot` is **5.42 K/W** against the hottest point. Say which one a datasheet number means before comparing.

## Results

| Pdiss | Die top | Rth_jc | Rth_ja |
|---|---|---|---|
| 3 W | 116.2 °C | 5.08 K/W | 10.41 K/W |
| 7 W | 160.5 °C | 5.45 K/W | 10.79 K/W |
| 11 W | 207.9 °C | 5.83 K/W | 11.18 K/W |

At 11 W the flange bottom averages **143.8 °C** and the board's underside **89.58 °C**.

**Rth rises with power.** Silicon carbide's conductivity falls as it heats (374 W/(m·K) at 25 °C, its library table), and the
run solves that as a Newton loop over k(T) — 3 to 4 steps a point, reported in the run's notes. A constant-k model would give
one Rth for every power.

**A check that is not circuitRF's.** The heat crosses the interface material in one dimension, so its top averages
`85 + 11 × 100 µm / (3 W/(m·K) × 80 mm²) = 89.58 °C` at 11 W — exactly what `T_interface` reads.

**See it.** The view carries two field plots of the last point (11 W): **Surface**, the temperature on every exposed face,
drawn when you open the results; and **Section**, a clip plane through the middle of the stack (tick it in the tree to draw it
instead) — the heat spreading from the die through the flange and funnelling into the vias.

## The settings chosen for speed, and what they cost

**Element order 1** (the setup's *Mesh ▸ Order*). The default is order 2. On the same mesh:

| | Order 1 (shipped) | Order 2 |
|---|---|---|
| Die top, 11 W | 207.9 °C | 210.9 °C |
| Rth_jc, 11 W | 5.83 K/W | 5.99 K/W |
| Rth_ja, 11 W | 11.18 K/W | 11.45 K/W |
| Run | 1 min 17 s, 1.1 GB | 17 min 55 s, 8.3 GB |

Order 1 reads the junction about 2.5 % cool. The setup refines the mesh at the source (`SizeFromSources` 12) to recover most of
what order 1 loses there.

**The mesh check** (*Mesh ▸ Check*, off in the shipped setup) meshes again with every element size × 0.7 and re-solves the first
point: 1,153,126 tetrahedra, **2 min 57 s**, and the die top moves by **+0.13 °C (+0.41 % of its rise)**. That is small because the check
refines the SAME order-1 elements; it does not see the order-2 difference above, so do not read it as the error of order 1.

**One element through thin solids** (`MinThroughThickness` 1; the default is 2). Every solid is meshed at an element size from
its thinnest dimension, so the 70 µm copper and the 100 µm interface material set the element count; the setting halves it.

## The via field as an effective block

The view has an **effective block**, `via_field`, over the vias under the flange — disabled. Enable it (select it in the tree,
tick *Enabled*) and run again: the board's laminate, copper and via barrels under the flange become one block of
k_xy = 81.74 and k_z = 7.241 W/(m·K), and the vias are no longer meshed.

| | Vias as drawn (shipped) | Effective block |
|---|---|---|
| Die top, 11 W | 207.9 °C | 194.0 °C |
| Rth_ja, 11 W | 11.18 K/W | 9.91 K/W |
| Run | 1 min 17 s | 3 min 10 s |

The block reads the die **13.9 K** cooler: here it is an optimistic approximation, and it did not make this run faster. It pays
where there are hundreds of vias. While the block is on, the `via_centre` probe reads nothing (its via is replaced).

## Things to try

- Change `Ths` or `Pdiss` in the **Variables** panel, or the sweep on the setup's page.
- Right-click the die's top face ▸ **Plot Temperature**.
- Move the vias (edit **Board**) and watch `Rth_ja`.
