# Brief 4 — symbols from a kit's own glyphs (OPTIONAL)

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf4-n` · **Phase:** schematic side
**Area:** `src/Design/Schematic/PCellKitSchematicParts.cs`, new `src/Design/Schematic/KitGlyphSymbol.cs`,
`tools/pcell-python/gdsfactory_bridge/bridge.py` (glyph discovery), `src/Ui/Schematic/KitPaletteMerge.cs`
**Depends on:** 2 · **Blocks:** —
**Status:** **Not to be built until the owner says so** (overview D9).
**Rule:** overview §0.

---

## 0. The question this answers

`PCellKitSchematicParts` says circuitRF **draws nothing on a kit's behalf**, because a generated box
with the right pin count reads as a part nobody recognises. That rule stands.

Some gdsfactory kits ship **their own drawing** of each cell. Kit B ships an SVG glyph for 38 of its
cells: 100 × 100 viewBox, lead lines running to the box edge, no pin markup. Building a `.csym` from
the kit's drawing is not circuitRF drawing on the kit's behalf, so it is within the rule. What
circuitRF adds is only **where the pins go**, and that comes from the cell's ports.

What such a symbol buys: the cell can be placed on a schematic, and Update Layout from Schematic and
LVS can see it. What it does **not** buy is a simulation model. A kit B resonator placed on a
schematic still has no circuit behaviour until an EM run produces an SnP for it (the existing
EM-to-SnP flow). The completion note must say this to the owner plainly.

## 1. `R-gdf4-1` — find the glyph

- `register_pdk` reports, per generator, a glyph path when the kit's package holds an SVG whose stem
  equals the cell name. Look in a `symbols/` package directory or beside the cell module.
- The match is by **file stem against the cell name**, which is the declaration-by-name rule
  `PCellKitSchematicParts` already uses for `.csym`. There is no fuzzy matching.
- A kit-supplied `.csym` with the same stem wins over a glyph.

## 2. `R-gdf4-2` — build the symbol

- **`R-gdf4-2a`** Convert the SVG's paths, rects, circles and text into `.csym` graphics. Use the
  symbol editor's own primitives. Unsupported SVG constructs are dropped **and counted** in the import
  report.
  - Colours are not carried. The symbol takes the schematic theme's symbol colour, as every built-in
    symbol does.
  - The kit's dark-mode CSS is ignored.
- **`R-gdf4-2b`** **Pins.** For each port (brief 2 §4c: `placement` excluded):
  - Choose the box edge from the port's outward orientation: 180° → left, 0° → right, 90° → top,
    270° → bottom.
  - Order pins along that edge by the port's coordinate across it.
  - Spread them evenly on the schematic grid.
  - Where a glyph lead line ends on that edge, snap the pin to the nearest lead end.
  - **If a cell has more ports on one edge than the glyph has lead ends there, place the pins on the
    grid anyway and say so in the report.** Never drop a pin.
- **`R-gdf4-2c`** The symbol carries **no parameters** (the existing rule: parameters come from the
  generator).

## 3. Tests (synthetic glyphs, tier A)

**UI is rendered headlessly and inspected** (overview §6): each symbol built from a kit glyph, placed on a schematic canvas and as its palette tile, with its pins where the cell's ports are. Render each with `tools/UiSnapshot`
(add a mode where none hosts it), look at the PNG, fix what it shows, and list the pictures in the completion
note.


1. A two-lead horizontal glyph with ports at 180°/0° gives pins on the lead ends.
2. Four ports on one edge with two lead ends gives four pins, and the report says two are not on a
   lead.
3. An SVG `<filter>` is dropped and counted, and the rest of the glyph converts.
4. A kit `.csym` with the same stem wins over the glyph.

## 4. Scope

- No symbol for a cell with no glyph.
- No generated box, ever.
- No circuit model.
