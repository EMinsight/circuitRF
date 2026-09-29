# Thermal: Channel vs Surface

How hot is the transistor's channel, and what does an infrared camera looking at the chip actually see? A GaN transistor's
hottest point is in the channel, a fraction of a micrometre under the surface, at the drain-side edge of the gate — and a
**field plate** sits right over it. The camera sees the passivation above the field plate, averaged over its spot. This example
measures the difference, and shows why it is not one number.

The chapter that teaches this is **Thermal** in **Help ▸ circuitRF Documentation**. It quotes the same numbers as this file.

**What to expect when you press Run:** *One Finger* takes about **3 min 28 s** and **2.0 GB** on the reference machine (an Apple
M4, the Debug build, on 216,837 tetrahedra — measured while another run shared the machine, so yours should be no slower); *Eight Fingers* takes less.
Thermal needs **Gmsh**; the first run without it offers to install it.

## What is here

| Cell | View | What it is |
|---|---|---|
| **One Finger** | 3D view, µm | One gate finger's unit cell: from the middle of its source to the middle of its drain (20 µm, half the 40 µm pitch), 100 µm of SiC, 1.5 µm of GaN, the passivation, the gate, the field plate, source and drain metal. The setup `Steady` |
| **Eight Fingers** | 3D view, µm | Eight fingers at the 40 µm pitch on 100 µm of SiC over a 500 µm copper–tungsten carrier, 1 × 1 mm. The setup `Array`: the Rth matrix, Z_th, and a radar pulse train |

Every material is one of circuitRF's generic materials, from `tech/generic-materials.cmat` — including the **GaN/SiC interface
resistance**, 3.3E-08 m²·K/W, which the library states as a pair and every run applies where the epitaxy meets the substrate.
It is a property of a growth process, not of the two materials: put your own process's value there.

## One Finger

- **The channel** is a GaN box 0.5 µm wide and 0.25 µm deep at the drain-side edge of the gate, carrying the finger's heat as a
  volume source (`hot_spot`). `P_finger` = 0.25 W is the whole finger's power — 5 W/mm over its 50 µm width.
- **A symmetry plane** at the finger's mid-width (y = 0) halves the model, so the source carries `P_finger / 2`, and `Rth_ch`
  multiplies back by `SymmetryFactor` — nothing is multiplied silently. The two side walls are insulated too: they are the mirror
  planes between neighbouring fingers, so this is an **inner finger of a long array**. *Eight Fingers* shows what the array's
  ends do.
- **The IR spot**, `ir`, is a disk of diameter `ir_spot` (a VAR, 3 µm) on the passivation's top, straight over the channel: its
  average is what the camera reports.
- **A line probe**, `up`, runs from the channel straight up through the field plate to the surface.
- The substrate's bottom is held at `Tbase` = 85 °C.

| Measure | Value |
|---|---|
| Channel, `Tmax(channel_T)` | 202.7 °C |
| IR spot, `Tavg(ir)`, 3 µm | 182.5 °C |
| `dT_ch_ir`, the offset | 20.2 K |
| `Rth_ch`, the whole finger | 470.9 K/W |

**Why there is an offset at all.** The heat is generated in a sliver under the gate's edge and spreads down into the SiC; the
surface above it is reached only through the passivation, and the gold field plate over the channel spreads what does reach it
sideways. The camera never sees the peak. Plot the line probe (`up`) to see where the 20 K is lost.

**The setting that moves the answer most is the spot.** The same run with `ir_spot` changed in the **Variables** panel:

| `ir_spot` | Offset |
|---|---|
| 1 µm | 18.1 K |
| 3 µm (shipped) | 20.2 K |
| 10 µm | 30.1 K |

A larger spot averages cooler surface in, so the offset grows. Quote an IR measurement with its spot size, and correct it with the
offset for THAT spot.

**See it.** The view carries the field plot **Section**, a clip plane through the finger 10 µm from its mid-width: the hot sliver
under the gate's edge, the field plate above it, and the heat fanning out into the SiC. **Surface** (untick *Section*, tick
*Surface*) paints every exposed face instead.

**Element order 2 is kept here, on purpose.** At order 1 the same mesh runs in **12 s** but reads the channel at **199.5 °C**,
the offset at **18.4 K** and `Rth_ch` at **458.2 K/W**: 9 % low on exactly the number this example exists for. The heat sits in a
sub-micrometre region and order 1 cannot follow its curvature.

## Eight Fingers

Eight 10 × 200 µm heat-source strips on a 40 µm pitch — the plan of the eight-finger reference case in
`testdata/thermal/fingers` — each at `P_finger` = 0.25 W, the substrate's bottom at 85 °C, modelled as the half on one side of the
fingers' mid-length: 58,032 tetrahedra.

| Measure | Value |
|---|---|
| Fourth finger (the middle of the array), `T_centre` | 105.1 °C |
| Edge finger, `T_edge` | 101.6 °C |
| `dT_centre_edge` | 3.5 K |

**The Rth matrix** (the run prints it, and plots it from the tree): each finger's own resistance is **52.91 K/W** at the edge and
**53.17 K/W** for the fourth finger, and a neighbour adds **20.52 K/W** per watt. These are per watt in the MODELLED half of each
finger (the run's notes say so). With all eight on, the edge finger rises **15.84 K** and the fourth **19.13 K**: the middle
of the array runs hotter because it has neighbours on both sides.

**A radar pulse**, 1 ms period at 10 % duty: the fourth finger peaks at **90.71 °C**, a single pulse reaches **90.62 °C**, and
the average is **85.75 °C**. The pulse comes from Z_th solved at the harmonics of 1 kHz (49 of them here), so the heat that crosses
the 120 µm from the edge finger is counted exactly. It arrives late, and a Foster fit of it could not show that: the peak was
0.3 K lower when it was built from that fit. Each finger's own Z_th is still fitted as a Foster network, written as a `.cnl`
you can simulate. Change the pulse on the setup's page; `PeakPower`, `Period` and `Duty` are expressions.

The fingers here run at 1.25 W/mm (0.25 W over 200 µm), a quarter of *One Finger*'s density, which is why the array is cooler.
Set `P_finger` to 1 for the same 5 W/mm.

**See it.** The view opens on the field plot **Surface**, every exposed face. It also carries two clip planes, hidden: **Top — 1 µm
under the surface** and **Across the fingers — y = 1 µm**. Tick either in the tree, or draw one with no window:
`circuitrf render "Eight Fingers/3d/Eight Fingers.c3d" -o top.png --field "Top — 1 µm under the surface"`.
