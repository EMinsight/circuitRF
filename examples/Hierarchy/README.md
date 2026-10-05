# Hierarchy

One small design, built as cells inside cells, so you can follow hierarchy through all three
places it appears: the **schematic** a simulation runs, the **layout** that schematic produces,
and the **3D view** that places that layout.

The circuit is a 9 dB attenuator on a 50 Ω board: a 3 dB pi pad and a 6 dB pi pad, joined by three
microstrip lines. It is deliberately simple, so you can check every number below by hand.

## The cells

| Cell | Views | What it is |
|---|---|---|
| **Pad** | schematic, symbol, layout | A matched pi attenuator. Its attenuation is a **cell parameter**, `dB`, default 3 |
| **Board** | schematic, symbol, layout, 3D view | Two `Pad` instances, `X1` (dB = 3) and `X2` (dB = 6), joined by three 50 Ω microstrip lines. Its 3D view is the board as built: its own layout on an aluminium carrier |
| **Launch** | 3D view | A coaxial edge launch, copied from the **3D Connector** example: a housing whose bore is filled with PTFE, a flange imported from STEP, and a centre pin insulated from the housing by the PTFE |
| **Bench** | schematic | The test bench: `Board` between two 50 Ω terminations, swept 0.1–6 GHz |
| **Assembly** | 3D view | Board's 3D view placed as `B1`, and Launch placed twice, as `J1` and `J2` |

The hierarchy, top to bottom:

```
Bench (schematic)          Assembly (3D view)
  X1: Board                  B1: Board (3D view)
        X1: Pad dB=3               L1: Board (layout)
        X2: Pad dB=6                     X1: Pad (layout)    R1 R2 R3: 0402 land patterns
        TL1 TL2 TL3: MLIN                X2: Pad (layout)    R1 R2 R3: 0402 land patterns
                                         TL1 TL2 TL3: MLIN artwork
                             J1: Launch (3D view)
                             J2: Launch (3D view), turned 180°
```

## 1. Schematic hierarchy, and a simulation through it

Open `Bench` and press **Simulate**. The sweep runs through two levels of hierarchy, Bench into
Board into Pad, and the Data Display shows `S21_dB`:

| Frequency | S21 | S11 |
|---|---|---|
| 0.1 GHz | −9.01 dB | −60 dB |
| 1 GHz | −9.03 dB | −41 dB |
| 3 GHz | −9.06 dB | −33 dB |
| 6 GHz | −9.10 dB | −33 dB |

The 9 dB is the two pads in cascade. The loss in the lines adds the last tenth of a dB by
6 GHz.

**Go down a level** with **View ▸ Push Into Cell** (Ctrl+], ⌘] on macOS): select `X1` in Bench
and push in to reach Board, then push into either Pad. **Pop Out** (Ctrl+[) goes back up.

**Parameters pass down.** Pad has no resistor values typed into it. Its VAR block computes them
from `dB`:

```
K   = 10^(dB/20)
Rsh = Z0*(K+1)/(K-1)      shunt arms:  292.4 Ω at 3 dB,  150.5 Ω at 6 dB
Rse = Z0*(K^2-1)/(2*K)    series arm:   17.6 Ω at 3 dB,   37.4 Ω at 6 dB
```

A VAR block inside a cell belongs to that cell. Each instance evaluates it in its own scope, so
`X1` and `X2` are one cell drawn once but resolve to different resistors. Change `X2`'s `dB` to 10
and simulate again: S21 moves to about −13 dB, and nothing else needs editing.

Run the same thing headless with:

```
circuitrf netlist "Bench/schematic/Bench.csch"
circuitrf sparam  "Bench/schematic/Bench.csch"
```

The netlist shows the hierarchy as written: `define Pad`, then `define Board` placing it twice.

## 2. Layout hierarchy, linked to the schematic at both levels

Both layouts were made with **Design ▸ Update Layout from Schematic**, which is what makes them
*linked*. Each placed part remembers which schematic component it came from.

- **Pad's layout.** Update Layout from Schematic places R1, R2 and R3 as 0402 land patterns, read
  from each resistor's `Footprint` parameter. It also drops a ground via beside every pad whose
  schematic pin is on a ground symbol, and pours the ground plane under them. The parts were then
  arranged by hand along a 200 mil run, and the traces and the `IN`/`OUT` pins were drawn.
- **Board's layout.** The same command on Board's schematic places the three lines as MLIN
  artwork. It places **Pad's own layout** for `X1` and `X2`, because a schematic instance of a cell
  becomes a layout instance of that cell's layout. The parts were then lined up end to end, and a
  short launch was drawn at each board edge for the board's pins to sit on.

Because the layouts are linked, **run Update Layout from Schematic again** at either level and
nothing changes. Parts you moved stay where you put them, and ground vias follow their pads. Now
edit a schematic, for example add a fourth resistor to Pad, and run it again. Only the difference
is placed, and the Messages panel lists what was added.

Pad's layout is drawn **once** and appears twice in Board. Edit it, and both instances change.

**Silkscreen designators.** On Board, each resistor's designator is drawn on the silkscreen inside
its Pad, and the Gerber export carries the same text. It is Pad's own text, R1, R2 and R3, exactly
what you see when you push into Pad, so the board shows each name twice, once per Pad. `X1` and
`X2` themselves carry no designator: a module is not a part, so its designator is switched off on
each instance (the checkbox beside **Designator** in the Properties panel). The lines `TL1`–`TL3` keep theirs.

> **Not yet: unique board numbering.** A PCB annotator would number the six resistors R1–R6
> across the board. circuitRF does not do that yet; the designators come from the sub-cell, so a
> module placed twice repeats them. It is planned for a future release.

**Layout versus schematic** compares each level on its own:

```
circuitrf lvs .
```

- **Pad matches.** On its own, Pad is compared at its parameter's default, `dB = 3`, which is
  what an instance with no override would give it.
- **Board matches,** with one warning: the ground copper "carries no pin". That is accurate. Board's
  schematic states no ground of its own, because the lines' return and the pads' grounds are inside
  cells, so at Board's level the ground plane has no schematic net to belong to.
- **Pad's `.ccell` sets FlattenForLvs**, and that is required, not a preference. A pad's ground is
  its own vias and pour, which meet Board's ground plane through metal that no pin declares. A cell
  connected to its parent that way has no hierarchical reading, so LVS reads Pad flat inside Board.

## 3. 3D hierarchy

Open `Assembly`. It places two kinds of thing, and the difference matters when you go down a level.

- **`B1` and `J1`/`J2` are 3D views of other cells.** Select `B1` and press **Push Into Cell**,
  either the ↓ button in the 3D toolbar or Ctrl+] (⌘] on macOS). Board's 3D view opens in the
  same tab, and only Board is drawn, as in the layout editor. A bar above the view shows where you
  are (`Assembly › B1 · Board`); click a level in it to jump back to that level. Edits are made in
  Board's own coordinates and saved to Board's own file. **Pop Out** (the ↑ button, or Ctrl+[)
  returns to the assembly and asks whether to save first.
- **`L1`, inside Board's 3D view, is a layout.** Push into it and Board's layout opens in the
  layout editor, because a layout is edited in 2D. Push into `X1` there and you reach Pad's
  layout. That is four levels, Assembly ▸ Board (3D) ▸ Board (layout) ▸ Pad (layout), each one a
  file of its own.

**One cell, placed twice.** `J1` and `J2` are the same Launch cell. `J2` is turned 180° to face the
other edge. Push into either one, change the pin, and both launches change, as the two Pads do in
2D. Launch is drawn with booleans, a fillet and a STEP import, so it needs the geometry kernel
(OpenCASCADE) that ships inside circuitRF, as the **3D Connector** example does.

**A cell's 3D view can place its own layout.** That is how Board's 3D view gets its copper. The
stackup in the technology turns the 2D artwork into substrate, ground plane, traces and both pads'
vias, so editing Pad's layout changes the 3D assembly two levels up. What a 3D view cannot hold is
its own 3D view, because a cell can't contain itself.

The 3D views have **no EM setup**: a land pattern is copper only, with no resistor body, so a
full-wave solve of this board would model an open circuit where each resistor should be. They are
here to show the hierarchy. The **3D Package** and **3D Connector** examples cover solving one.

## Things to try

- Change `X1`'s `dB` on Board's schematic and simulate Bench. The layout does not need updating,
  because the 0402 land pattern is the same whatever resistor value is fitted.
- Change a line's `L` on Board's schematic, then **Update Layout from Schematic**. That line's
  artwork is regenerated in place, and the Messages panel reports it as updated.
- Delete `TL2` from Board's schematic and update the layout. The artwork it placed is **reported,
  not deleted** ("no longer in the schematic"), because removing board artwork is a decision for
  you to make, not the command.
