---
title: Create Schematic from Artwork
slug: reference/artwork-to-schematic.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Create Schematic from Artwork
lede: Turn a board's copper into a schematic of native components — lines, bends, tees, vias and parts — that simulates in seconds, reviewed first in a parts table you can correct.
keywords: recognise, recognize, artwork, Gerber, board, parts table, BOM, placement, MLIN, CPWG, SLIN, TLIN, VIAGND, FromArtwork, Show in Artwork, schematic from layout, ground, stitching vias
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it does</a></li>
<li><a href="#open">Opening it</a></li>
<li><a href="#target">Target</a></li>
<li><a href="#scope">Scope</a></li>
<li><a href="#options">Options</a></li>
<li><a href="#companions">BOM and placement files</a></li>
<li><a href="#parts">The parts table</a></li>
<li><a href="#recognised">What is recognised, and what is not</a></li>
<li><a href="#ic">A board with an IC on it</a></li>
<li><a href="#report">The report</a></li>
<li><a href="#probe">Show in Artwork</a></li>
<li><a href="#fromartwork">Models existing artwork, and layout sync</a></li>
<li><a href="#headless">From the command line</a></li>
</ol>
</nav>

## What it does {#what}

A board of ordinary SMT parts joined by controlled-impedance lines does not need an EM run: its copper is
already a circuit. **Design ▸ Create Schematic from Artwork…** reads that circuit out of the artwork &mdash; an
imported Gerber set, a flattened GDSII, a board read back as copper &mdash; and writes it as a schematic of
native circuitRF components:

- every two-terminal part as an **R**, **L**, **C**, or an S-parameter file where the workspace has one for its
  part number;
- every trace as **MLIN** (with **MBEND**, **MTEE**, **MCROSS** and **MTAPER** where the copper has them),
  **CPWG**, **SLIN**, or **TLIN** where no circuit model fits &mdash; each bound to the layout's own technology;
- the vias that matter as **VIA** and **VIAGND**, and ground stitching dropped;
- a port wherever a line leaves the circuit, and an S-parameter analysis ready to run.

It is a best attempt that you then check and clean up. Everything it guessed and everything it left out is
said, with a count, in the [report](#report).

## Opening it {#open}

Focus a **saved** layout and choose **Design ▸ Create Schematic from Artwork…**. A layout that has never been
saved has no cell to write beside, and is refused with a message, as **Update Schematic from Layout** refuses
it. The dialog reads the layout as it is saved on disk; save first to include recent edits.

The dialog stays open beside the layout: selecting a row of its parts table marks that part on the artwork.
Recognition runs when the dialog opens, and again whenever the scope, an option or a companion file changes;
the strip at the bottom shows its progress.

## Target {#target}

| Choice | Writes |
|---|---|
| **New cell** | A new cell beside the artwork's, holding a schematic only. The name defaults to `<cell>_model` and is checked as you type. |
| **This cell's schematic** | The artwork's own cell. Offered **only when that cell has no schematic view**, so you can start a schematic there and clean it up in place. |

A name that is a cell this command created earlier turns the button into **Replace**; its confirmation says
that a history checkpoint is taken first, so the replaced schematic can always be restored. A cell whose
schematic you drew yourself is never replaced &mdash; choose another name.

## Scope {#scope}

**Whole layout**, or **Selection** &mdash; the copper inside whatever is selected in the layout when the dialog
opens. Copper outside the scope is not read. A line the selection's outline cuts becomes a **port** there, which
is how you model one section of a board without any EM. Ground is always read from the whole board.

## Options {#options}

| Option | Choices |
|---|---|
| **Ground** | **Auto** reads the largest copper on the stackup's reference conductor, and everything joined to it, as ground. **Pick on layout** asks you to click the copper that is ground. |
| **Ground vias** | **Model as VIAGND** keeps a via that grounds a part's own pad or a line end as a stackup-bound `VIAGND` (at most four per pad). **Plain GND** writes a plain ground instead. Stitching vias are dropped either way. |
| **Coplanar lines** | How a line with ground close beside it is read: **Auto** (grounded coplanar when both side gaps are within the factor's substrate heights, 3 by default), **Microstrip**, or **GCPW**. |
| **Frequency** | The S-parameter analysis written with the schematic. Default: the layout's EM setup's sweep, else 100 MHz – 6 GHz in 201 points. The stop frequency is also the frequency coupled lines and each `TLIN` are judged at. |

Every MLIN and CPWG records the gaps measured beside it, so a line read the wrong way is one swap away.

## BOM and placement files {#companions}

**BOM…** names a bill of materials: values, part numbers and kinds by designator. **Placement…** names a
placement file: where each designator sits. A placement file that does not say which point of each part its
coordinates are shows a three-way **Origin** choice &mdash; the footprint's symbol origin, the part body's
centre, or pin 1 &mdash; with **nothing chosen**, and recognition waits until you choose. On an 0402 the
difference is the width of a pad, so it is never guessed.

## The parts table {#parts}

One row per part found on the board. **Kind**, **Value** and **Model** are yours to correct, and so is
**Refdes** where it was read off the silkscreen or made up (`C_A1`); every other column is measured on the board.

| Column | |
|---|---|
| ● | Confidence: green for a placed part or a BOM that agrees with the pads, amber for one source, red for a land pattern alone. Its tooltip lists the evidence. |
| **Kind** | R, L, C, Short, Open, MultiPin, Connector, Ignore, or `?` &mdash; an unknown kind is generated as a capacitor. |
| **Connection** | Series, shunt, or why the part is left out (shorted, bridged, multi-pin). |
| **Case** | The case size the land pattern matches. |
| **Value** | With its unit (`10 pF`, `4.7 nH`, `49.9 Ohm`). Empty when unknown, showing the variable that stands for it; a value of the wrong dimension for the kind is outlined in red. |
| **Model** | Ideal, a two-port Touchstone file found in the workspace, or **Browse…**. |
| **Part number**, **X**, **Y** | From the BOM and the board. |

Click a column header to sort; type in the filter box to narrow the list. Selecting a row marks the part's
pads on the layout and brings it into view.

**A value that is not known becomes a global variable** named `<Refdes>_<Param>` &mdash; `C6_C`, `L2_L` &mdash;
with a tuning entry, so it is a slider in the **Tuning** panel and a variable in the **Optimizer** from the first
simulation. Its starting value is chosen to be transparent (a series capacitor 100 pF, a shunt one 0.01 pF), so
the first run shows the lines alone.

Your edits are kept across every re-run the options cause. **Export Parts…** writes the table as CSV and
**Import Parts…** reads an edited one back &mdash; the same file `circuitrf recognize --parts` reads, so a table
can be corrected in a spreadsheet or by an agent and brought back.

### Designators on the silkscreen {#silkscreen}

When nothing else names a part &mdash; no placed footprint, no placement file &mdash; the designator printed
beside it on the silkscreen does. Stroked silkscreen text is read at any rotation, and mirrored on the bottom
side; each designator goes to the part it stands beside, even where it is printed a little nearer a
neighbour. Its prefix gives the part's kind (`C`, `L`, `R`, `FB`, &hellip;). Values are never read from the
silkscreen. Text drawn as filled outlines is not read.

A board's font may draw a character unlike the built-in one, and then a designator is misread or not read at
all &mdash; the report lists lines that look like designators but have a glyph that fits two characters. Correct
the **Refdes** of a part named from the silkscreen, or of a made-up one beside such a line, and click **Learn
These Glyphs**: the glyphs you corrected are remembered as those characters for every later recognition, in
the dialog and from the command line alike. They are kept with your own settings, never in the workspace.

## What is recognised, and what is not {#recognised}

| On the board | In the schematic |
|---|---|
| A trace on microstrip | `MLIN`, with `MBEND`, `MTEE`, `MCROSS` and `MTAPER` at its bends, junctions and linear width ramps |
| A trace with ground close on both sides | `CPWG` |
| A trace between two planes | `SLIN` |
| Any other cross-section | `TLIN` in its physical form, with Z0 and εeff from the cross-section solve |
| A two-pad part | `R`, `L`, `C` or a two-port `SnP` |
| A via joining two signal layers | `VIA` |
| A via grounding a pad or a line end | `VIAGND` (or `GND`) |
| Stitching and plane-tie vias | Nothing &mdash; counted |
| A part with more than two pads, a connector | Cut out; its pads on RF lines become ports |

Not modelled: coupled lines (a close parallel pair is reported as modelled uncoupled), step, gap and open-end
discontinuities, case-size parasitics, curved tapers (they become `MTAPER` or stepped lines), and the pads
themselves &mdash; a line ends at the pad's edge. Nothing is ever written to the layout.

A line read as the wrong type &mdash; a top-side pour close to a microstrip can make it read as `CPWG` &mdash; is
one [**Swap Line Type**](components.html#swap-line-type) away: the width and length are kept, and a swap to
`CPWG` takes the gap measured off the board.

## A board with an IC on it {#ic}

An IC, or any part with more than two pads, is not modelled. Each of its pads that meets an RF line becomes a
**port**, so the schematic is the networks around the device as the device sees them &mdash; what matching work
needs. Pads on ground join ground; bias and control pads are left open and listed.

To bring the device in afterwards, import its symbol and footprint with
[**File ▸ Import ▸ Component…**](footprints.html#imported), place it, and wire it to those ports &mdash; or put an
S-parameter file of the device in its place.

## The report {#report}

After **Create**, the schematic opens focused and the report goes to **Messages**, one line per kind of finding
with its count: the ground chosen, vias dropped and kept, ports and where they came from, parts by source,
silkscreen text read and designators that named no part, unknown kinds and values, `TLIN` fallbacks by reason, coupled pairs, copper read as nothing. A line about
places on the board has a toggle that expands it to them; **double-click** one to select and zoom the artwork
there. The same report is in the dialog's bottom strip while you work.

A recognition is never refused for being imperfect. It is refused only when the layout has no technology,
there is no copper in the scope, or no port could be found &mdash; and then the reason is in the dialog's status
line and the dialog stays open.

## Show in Artwork {#probe}

Right-click a component the command created and choose **Show in Artwork**: the layout it came from opens and
the component's copper is marked &mdash; a line's centre line, a part's pads. The schematic records which layout
it was created from, the scope and the options, so this works in later sessions too.

## Models existing artwork, and layout sync {#fromartwork}

Every component the command creates carries a **Models existing artwork** flag, shown as a check box in its
parameter editor. It means the copper already exists:

- **Update Layout from Schematic** leaves its artwork alone and counts it, so a schematic written into the
  artwork's own cell never has lines generated over the imported board;
- **Update Schematic from Layout** does not place it a second time.

Clear the box to hand that component back to ordinary layout sync; it is one undo step and changes nothing else.

## From the command line {#headless}

[`circuitrf recognize`](cli.html#recognize) runs the same recognition with the same options, and an agent can
call it through the MCP `recognize` tool. With no output named it writes nothing and prints the report and the
parts table &mdash; review, edit the table, then write:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf recognize Board --parts-out parts.csv
<span class="prompt">$ </span>circuitrf recognize Board --parts parts.csv --into new:Board_model</code></pre>

When an EM run of a whole board is refused for its size, the refusal names this command.
