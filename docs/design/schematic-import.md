# Import Schematic

**Status:** SI-1 implemented (the neutral model, the map, the build, the check, the two write paths); SI-2 implemented
(the `.kicad_sch` reader, §8); SI-3 implemented (the EDIF 2.0.0 reader, §9); SI-4 implemented (the binary `.DSN`
design, versions 2.0 and 3.2, §10); SI-5 implemented (the entry points, §11); SI-6 done (the acceptance, §12, and the
user page `docs/user/src/reference/schematic-import.md`). Source of decisions:
`docs/sonnet-briefs/brief-schimp-0-overview.md` (D1–D11).

## 1. The pipeline

```
file ──SchematicFileClassifier (content, never extension)──▶ reader ──▶ ForeignSchematic   (bytes → neutral model)
                                                                          │
                                       SchematicImportMap: each part → native component, ground, or symbol cell
                                                                          │
                                       SchematicImportBuild: nets from geometry → TestBench + ExactPlacement
                                                                          │  NetlistSchematic.Build
                         ┌────────────────────────────────────────────────┴───────────────────────┐
              SchematicImport.Import (new cell)                          SchematicImport.BuildInto (open schematic)
                         └──────────────── ImportConnectivity.Compare, every import ──────────────┘
```

Everything lives in `src/Design/Schematic/Import/` (namespace `CircuitRF.Design.Schematic.Import`). A reader touches
nothing but bytes; only `SchematicImport` writes. The GUI command, the drop, the CLI noun and the MCP tool call one of
its two functions and hold no import logic (D1).

## 2. The neutral model — `ForeignSchematic.cs`

| Record | Holds |
|---|---|
| `ForeignSymbol` | key (unique in the source), display and library names, units; per unit pins and graphics |
| `ForeignPin` | `Number` (the identity), `Name`, electrical type, connection point, body side, length, `Hidden`, `ImplicitNet` |
| `ForeignSheet` | name, page size, `Instances`, `Wires`, `Junctions`, `Labels`, `NoConnects`, `SheetInstances`, `Texts`, `Graphics` |
| `ForeignInstance` | reference, unit (from 1), symbol key, origin, rotation 0/90/180/270, mirror, value, properties, `Fields` |
| `ForeignField` | `Reference` or `Value` (or another shown property), its text, where it is shown, justification, `Vertical`, `Hidden` |
| `ForeignLabel` | kind `Local / Global / Hierarchical / OffPage / Power`, text, anchor |
| `ForeignSchematic` | format (by extension), source path, sheets, symbols, root sheet, `Hierarchical`, `DeclaredNets`, `Notes` |

**Two coordinate conventions.** Symbol geometry is mils, **Y up** (the symbol formats' own handedness, and
`ComponentPart`'s), so the one flip in `ComponentImport.BuildSymbol` applies once. Sheet contents are mils, **Y down** —
a sheet point is a schematic point (one schematic unit is one mil, D6). A pin lands at
`SchematicGeometry.LocalToWorld((x, −y), origin, rotation, mirror)`: mirror across the symbol's vertical axis first, then
quarter turns clockwise on screen. A reader whose format composes differently converts to this.

**Pin numbers are the identity** (`ForeignPinRef(reference, pinNumber)`), never names (`GND` repeats) or a format's port
ids. A multi-unit part is one reference; unit *n* is placed as instance `<ref><letter>` (`U1A`, `U1B`) and its pins map
back to the reference's numbers. A pin with no number is known by its name, then its 1-based position (reported).

Readers must give every (reference, unit) once; two placements claiming the same pair are renamed `<ref>_2` with a note.

## 3. The map — `SchematicImportMap.cs`

Rules are data, tried in order; every condition a rule states must hold (symbol name pattern, whole name,
case-insensitive; reference prefix letters; pin count; optional value pattern). A multi-unit symbol never matches.

| Rule | Symbol name | Prefix | Pins | Becomes |
|---|---|---|---|---|
| resistor | `R`, `RES`, `RESISTOR`, or one of those then `_`/space and more | `R` | 2 | `Resistor`, `R` |
| capacitor | `C`, `CAP`, `CAPACITOR`, `CP`, likewise | `C` | 2 | `Capacitor`, `C` |
| inductor | `L`, `IND`, `INDUCTOR`, likewise | `L` | 2 | `Inductor`, `L` |
| ground | `GND`, `GROUND`, `EARTH`, `0`, likewise | — | 1 | ground (its net is node 0) |

Anything unmatched becomes a **symbol cell**. The value is read by the bill-of-materials reader (`10k`, `4k7`, `100nF`,
`4u7`, `2R2`, `1.5 nH`) and written as a number and unit (`10 kOhm`). A value that does not read as the part's dimension
makes the native part's value a global variable `<Ref>_<Param>`, with a default (a not-fitted marker defaults to an open:
1 GOhm, 0.01 pF, 1 H; anything else to 1 kOhm, 1 nF, 1 nH) and the source text in a note. A power or global label named
`GND`, `GROUND`, `EARTH` or `0` is ground; `AGND`, `DGND` and the like stay named nets. A user may push a native row to a
symbol cell (`SchematicImportOptions.SymbolCells`), never the reverse.

## 4. The build — `SchematicImportBuild.cs`

**Nets, in two layers.** *Physical*: a wire's vertices; anything (wire end, junction, pin, label, sheet pin) lying on a
wire, interior included; anything at the same point. Two wires merely crossing are not joined. *Logical*: physical groups
joined by name — local and hierarchical labels per sheet, global/power/off-page labels across the scope, a hidden power
pin's `ImplicitNet`, and ground. A net is named by its best label (power/global, then hierarchical, off-page, local),
made unique; an unnamed net is a quiet `N`k, drawn without a label when one group of its wires reaches every pin.

**Placement.** A symbol cell's symbol is translated so its first pin is the local origin, and the instance is placed with
that pin on the source's point. Its pins are NOT snapped to the 100-mil pin grid (`BuildSymbol(…, snapPins: false)`), so
a part pitching pins at 50 mils keeps them apart and every pin lands exactly. A native part
takes the rotation and on-grid origin nearest its source pins; a pin it misses is re-joined by one dog-leg, the source
wire never moved — except where a native symbol longer than the source's lies over a wire: the stretch along its axis
between its two pins is cut away, so the wire ends on the pin instead of inside the body (nothing is cut where anything
else connects inside that stretch). A pin the source joins by touching a label or pin (no wire), and a hidden power pin, get a net label on
the pin. Each wired physical ground group keeps its wires and gets a ground symbol where the source grounded it; a
ground pin with no wire gets one on the pin.

**The grid.** The drawing is never snapped (`ExactPlacement.SnapToGrid = false`); its `GridSize` is the largest divisor of
100 that every source point sits on, because extraction quantizes connection points to it. Measured in
`src/Design/RESOLVED.md` (R-si1-6).

**Pages and hierarchy (D5).** A flat design's pages are tiled left to right with a 20-square gap; off-page labels join by
name. A hierarchical design gets one cell per distinct child sheet in the parts folder: a rectangle symbol (the first
placement's rectangle, ports in its pin order) and a schematic whose hierarchical labels are `Pin`s. A global net used on
several sheets is not joined across cells (circuitRF keeps a cell's nets local) and is reported; ground is global.

**Ports (D9).** `SchematicImportOptions.Ports` names references (a connector drawn as a Port where it was: first pin off
ground the signal, the next its reference) or nets (a Port to ground beside the drawing). With any, the default
S-parameter analysis `SP1`. A connector made a Port gets no symbol cell, and a wired ground group that only its pins
reached (its shield's wire and ground symbol) is not drawn, since nothing would be left on it;
`ImportBuildResult.PortParts` names those references, so the report counts them as Ports, not as symbol cells.

Free text and graphic lines become schematic text and lines; junctions become dots. Title blocks and borders are not
imported.

**Labels where the source shows them.** A `ForeignField`'s point is, along the line of text, the end its `Justify`
names (or the middle) and, across it, the line's middle, in sheet coordinates — each reader converts its format's own
anchor to that. After `NetlistSchematic.Build`, each part from the source gets per-row `LabelOffsets` (offsets from the
renderer's default row anchor, which is measured from the part's real glyph — a symbol cell's own drawing): the name
row on the `Reference`, a native part's value row — or a symbol cell's type row, its cell name being the nearest thing
it has — on the `Value`. A native part's type row is hidden (the source shows none); a field the source hides, or a
format lists among its shown fields and this one is not, hides its row. Each row keeps circuitRF's text and is aligned
by the justified end, so it grows away from the part as the source's does; when that makes a part's own rows collide
(circuitRF's "R = 15 kOhm" for the source's "15k" side by side with the reference) they are stacked as one block at the
source's place, growing away from the part. **A part whose shown fields include vertical text keeps circuitRF's own
layout**: a label here is always level, and a level row centred where a vertical one stood — a resistor's value written
inside its body, which is the `.kicad_sch` default for an upright resistor — covers the part. Parts placed beside the
drawing, sheet instances and Ports keep the default too.

## 5. The check — `ImportConnectivity.cs`

Each produced schematic is extracted by `NetExtractor` (a black-box resolver answers every cell with one `Pin` per symbol
pin, so a model-less symbol cell still binds its nets), instance pins are translated back to source pins through the
build's own table, and the result is compared as **sets of pin sets** with the source's geometric nets — and, when the
format declares nets, with those. A hierarchy's declared nets span its sheets while its cells are built one at a time, so
both sides are first FLATTENED (`ImportConnectivity.Flatten`): each sheet instance's pin is joined to its cell's port,
every cell's ground is one net, and the synthetic pins are dropped. That is possible only when every sheet cell is placed
once — a schematic placed twice holds one set of references for two places — and otherwise each cell is still checked
against its own drawing. Single-pin nets, no-connect pins and ground-symbol pins are left out of
both sides. Each difference names its pins: "joined in the source, split in the import" or the converse. The import still
completes; the report carries the verdict.

When a source declares nets, its declared nets are also compared with its OWN drawing (`SourceAgreement`, R-si3-3):
a difference there is the file's record disagreeing with its picture, which no import can reconcile, and the report says
so in those words rather than leaving the user to infer it from two failed comparisons.

## 6. The writes — `SchematicImport.cs`

`Import(source, parentDir, cellName, options)` refuses a name `NameValidator` rejects or one already there; decides every
cell (D4: `<source-stem>_parts/` beside the target, one cell per distinct symbol unit, reused when its `.csym` is
byte-identical, refused naming both when a same-named cell differs) before writing anything; writes the symbol and sheet
cells, then the target cell through `CellCreate` with a `SchematicImportSource` block (format, file NAME, UTC date). Then
the check, then the report.

`BuildInto(source, into, topLeft, options)` builds the same, writes its parts beside `into`'s cell (so an unsaved
schematic is refused), moves the drawing so its top-left lands on `topLeft` by whole grid squares, drops analyses, and
renames every instance, net label and variable `into` already has (`SchematicMerge`, shared with the image paste). The
edit carries the `GridSize` the drawing needs; the editor adds it as one undo step (SI-5).

## 7. The classifier — `SchematicFileClassifier.cs`

CFBF signature → `BinaryDsn` (SI-4 confirms by its root streams); text starting `(kicad_sch` → `KicadSch`; text starting
`(edif` → `Edif200` when its `edifVersion` is `2 0 0`, else refused naming the version. Anything else, an ASCII `.dsn` kit
symbol and a routing `.dsn` included, is `Unknown` with "not a schematic design".

## 8. The `.kicad_sch` reader — `SchematicSexprReader.cs` (SI-2)

`SchematicSexprReader.Read(path) → ForeignSchematicRead` (a `ForeignSchematic`, or a refusal). Parsed with `PcbSexpr`;
each `lib_symbols` entry goes through `ComponentSymbolSexprReader.ReadSymbol`/`ReadUnit` (the symbol library's own
reader, now taking a parsed node) for its drawing, while the reader takes the pins itself because the neutral model needs
their type, `hide`, length and side. Nothing outside the file is looked up. `DeclaredNets` is null: the format declares
no nets, so the geometric check is the check.

- **Units and axes.** Millimetres → mils by `mm / 0.0254` (to 1e-4 mil). The sheet is Y down and is not flipped; the
  embedded symbols are Y up and stay so.
- **A placed symbol's transform.** The format turns counter-clockwise on screen by its angle, THEN mirrors on screen
  (`mirror x` top-to-bottom, `mirror y` left-right). In `ForeignInstance`'s terms: no mirror → rotation `−angle`;
  `mirror y` → mirrored, rotation `angle`; `mirror x` → mirrored, rotation `angle + 180`. Held by
  `opamp_units.kicad_sch`.
- **References.** The symbol's own `instances` block on the sheet path that reached it, else the root file's
  `symbol_instances` keyed by path (the older epoch), else its Reference property. A child file placed by several sheets
  is read ONCE (D5), by the first path; it is one `ForeignSheet` named after its file stem.
- **Power symbols** (`(power)` in `lib_symbols`) become `ForeignLabel(Power)` named by their Value (a local power symbol a
  local label) — ground-named ones are ground through the map. One whose pin is not a power input (a rule-check flag)
  is left out and counted. A hidden power-input pin on any other part carries `ImplicitNet` = its pin name, in every
  version of the format to date; the report says how many and under which version.
- **Buses.** Bus segments, bus entries and labels on a bus are drawing: each member connects by its own label on its own
  wire, exactly as the source's editor requires. A bus-named sheet pin or hierarchical label (`D[0..7]`, `{A B}`) is not
  expanded; the report lists those buses, because their members are not joined across the sheet boundary (D11).
  `~{X}`, `_{x}`, `^{x}` are text markup, not bus groups.
- **Fields.** A `Reference`/`Value` property's `at` is its anchor on the sheet, already turned with the symbol; its
  justification and angle are in the symbol's own frame, so the anchor-to-centre offset is turned by the text angle and
  then by the symbol's transform (a mirrored part's left-justified field reads right-justified), and a quarter-turned
  symbol swaps the field between level and vertical. `hide` hides it.
- **Not carried, one counted sentence per kind (R-si2-3):** images, tables, rule areas, net-class directives, circles,
  arcs and curves drawn on the sheet, pinless parts (mounting holes, logos), alternate body styles (imported with the
  standard body). The file's version is reported and branched on nowhere.

Fixtures: `testdata/schematic-import/sexpr/` (authored, tiny). The gate is
`tests/Ui.Tests/SchematicImport/SchematicSexprReaderTests.cs`.

## 9. The EDIF 2.0.0 reader — `EdifReader.cs`, `EdifSexpr.cs`, `EdifText.cs` (SI-3)

`EdifReader.Read(path, EdifReadOptions?) → ForeignSchematicRead`. Written from the standard.

- **Tokenizer.** `EdifSexpr` gives `PcbNode`s with tags lowercased (EDIF keywords are case-insensitive) and strings raw:
  a backslash is a character in EDIF, which `PcbSexpr` would eat (R-si3-1, `src/Design/RESOLVED.md`). Identifiers compare
  case-insensitively. `rename` is honoured everywhere: the display name is the renamed text, references use the id.
- **Text.** `%d%` escapes and `%d d%` groups; adjacent escapes are ONE run, decoded as one byte string — UTF-8, else the
  code page (`EdifReadOptions.CodePage`, default 1252), else Latin-1 — and the notes count each. In a run CR LF / CR / LF
  are one newline and other C0 controls are dropped; a trailing newline is dropped. A malformed `%` stays as written.
- **Units and axes.** Each library's `numberDefinition` distance scale (`(scale 1 (e 254 -6) (unit DISTANCE))` = 10 mil),
  a library without one taking the file's first; a file with none is refused. Sheet points are negated (Y up, content at
  y ≤ 0); symbol points stay Y up.
- **Orientation.** The standard mirrors first (MX: y → −y, MY: x → −x), then turns counter-clockwise. In `ForeignInstance`
  terms: `Rn` → rotation 360−n; MY → mirrored, 0; MX → mirrored, 180; MXR90 → mirrored, 90; MYR90 → mirrored, 270. All
  eight are tested against the standard's matrices. An unknown word is read as R0 and counted.
- **Symbols.** A cell whose view has no `page`. Ports from `interface`; figures and `portImplementation`s from `contents`
  or `interface/symbol`, whichever is used. The lead (the implementation's own figure) gives side and length; no lead, or
  `INVISIBLEPIN`, is hidden. A hidden pin with `IMPLICITPORTCLASS`, or a hidden `POWER` pin, carries `ImplicitNet` = its
  name. Units = the longest `PackagePortNumbers` list. Pin types: `PAS IO IN OUT POWER HIZ OC OE 3ST DBO_IN DBO_OUT` and
  the standard's `direction` words; unknown → passive. Symbol text is not drawn (counted).
- **Pin numbers**, in order: the placed part's `portInstance` designator → the port's designator → `PackagePortNumbers`
  [unit−1] → the pin name → the port id. Every source past the first is counted in the notes. A placed part that renumbers
  a pin gets a variant symbol (`key#n`).
- **Fields.** A `designator` or a `Value` property written as a `stringDisplay` is read (a part whose designator is one
  is no longer skipped as a title block), and its `display`'s `origin` is taken as a page point with `justify` LOWERLEFT
  unless stated and an R90/R270 `orientation` as vertical text.
- **Placed parts.** `designator` is the reference; a `Designator` property is the unit suffix (stripped from it) and
  `PositionInPackage` the 0-based unit (else the suffix letter). No designator → a title block or border, skipped and
  counted. A cell whose view has pages is a hierarchical sheet instead.
- **Connectors.** A page's `portImplementation` is a label at its connection dot named by its display name: off-page
  when its connector cell's name ends `_OFF_PAGE_CONNECTOR`, hierarchical when a sheet cell's interface has a port of
  that name, otherwise power.
- **Nets.** Each `net`'s `joined` `portRef`s are the declared connectivity; its `figure`s' paths are wires and its dots
  junctions. Nets are joined across pages through connectors of the same name. A non-generated net name (not `N<digits>`)
  becomes one local label per touching group of its wires, which is how one alias joins separate stretches on a page.
  A hidden power pin no net lists is joined to the net its name gives (counted). Buses (`netBundle`, `portBundle`,
  `member` refs) are not imported (counted).
- **Hierarchy.** A sheet instance's pins are its cell's own block symbol's connection dots, placed like a part. A
  hierarchical cell of more than one page is refused by name. Declared nets are populated for flat designs only — the
  check compares a hierarchical design per sheet against its drawing.

Fixtures: `testdata/schematic-import/edif/` (written by hand). Gate:
`tests/Ui.Tests/SchematicImport/EdifReaderTests.cs`.

## 10. The binary `.DSN` reader — `Cfb/CompoundFile.cs`, `BinaryDsn/` (SI-4)

The container follows the MS-CFB specification; no other implementation was read or ported (overview D7). Every
layout fact is in `src/Design/RESOLVED.md`.

- **`CompoundFile`** — read-only CFBF, versions 3 and 4; every chain bounded, loops and truncation refusals.
- **`DsnLibrary`** — the `Library` stream: the version (u16 major, u16 minor after a 32-byte title) and the STRING
  TABLE every record indexes (found by its count, not walked to). Version 2 indexes it 1-based; version 3 opens it with
  an empty string that index 0 names.
- **`DsnCache`** — each symbol view's graphics (lines, arcs, ellipses, polylines; in version 3 also rectangles and filled
  polygons, and text, which is counted and not drawn), body size and pins (name, start, hotspot), found by name. A
  polyline's and a polygon's points are stored **Y first**. Each pin is drawn as the line from its start to its hotspot,
  which the symbol's graphics do not include. Version 3 skips a graphic kind it does not draw by its size and counts it.
- **`DsnPageV2` / `DsnPageV3`** — one page each, read in order: title blocks, nets, net names, wires (with their net
  id, and in version 3 their aliases), parts (reference, symbol view, value, properties, position, rotation code, unit,
  and every pin's page position and wire), ground/power symbols and off-page connectors (the symbol's name, the NET name
  by string index, the wire); in version 3 also hierarchical BLOCKS (in the parts list: their own rectangle symbol inline,
  their name, the schematic they place, their pins) and hierarchical PORTS (the list after the parts). Version 3 is version 2's sections with every record size-prefixed, so a section not read
  is skipped by size and reported; in version 2 it is a refusal.
- **Displayed properties** — each part lists the properties it shows (`Part Reference`/`Reference`, `Value`, any other):
  a name index and an offset, page units from the part's FILE position (not the re-anchored origin) and not turned with
  the part, to the text's TOP-LEFT; then a byte read as rotation (odd = vertical). A property not listed is not shown.
- **`BinaryDsnReader`** — version dispatch (2.0, 3.2; anything else refused with the EDIF route), the transform
  (rotation code r turns a quarter counter-clockwise on screen, re-anchored at the turned body's top-left; bit 2
  mirrors first), the labels, and the DECLARED nets: pins joined through their wire's net id, pins that touch, and the
  names ground, power and off-page symbols carry — by name across pages, since net ids are per page. A multi-unit
  part's pins are numbered per unit (`A1`, `B1`). A net the file names as one but draws in pieces (an alias spelling
  another piece's automatic name) gets a label on each piece; an alias or such a label spelling an off-page or power
  name is that design-wide net, a global label.
- **Hierarchy (version 3.2).** Each `Views/` storage is a schematic. The root is the one no block places; each schematic
  becomes one sheet with its pages tiled left to right in it (`SchematicImportBuild.NextPageX`, the build's own page
  rule), so a multi-page schematic stays one cell and its off-page connectors join within it. Blocks become sheet
  instances, ports hierarchical labels; ground and power names stay design-wide, off-page names their schematic's. A
  design of one schematic that has ports keeps them as hierarchical labels — the import's port candidates. A version-2
  design with several schematics is refused.

Gate: `tests/Ui.Tests/SchematicImport/CompoundFileTests.cs` (built containers) and `BinaryDsnReaderTests.cs` (refusals
and symbol graphics on built containers).
`SchematicImportBuildTests.AHierarchicalDesign_…` holds the flattened check on a committed source, ground across cells
included, and that a wrong declaration is caught.

## 11. The entry points (SI-5)

File ▸ Import ▸ Schematic… (the main window's File menu and its torn-off copy), the drop onto an open, SAVED schematic,
the drop onto a project-tree folder, `circuitrf import schematic` and the MCP `import what=schematic` all call
`SchematicImport.Read` and then `Import`, `Preview` or `BuildInto` (`SchematicImport.Entry.cs`), and nothing past them —
a comment-stripped source scan holds it. The dialog's preview is the very build `Import` writes, into a scratch folder
laid out like the target. A drop builds with no dialog (every part per the map, no Port) as one undo step that includes
any lowered connection grid; an unsaved schematic is refused because its parts have no "beside". A dropped picture
stays a picture. The CLI exits 1 when the check is not matched, with the cell written. Detail: `src/Ui/RESOLVED.md`,
`docs/design/cli.md` §30; gate `tests/Ui.Tests/SchematicImport/SchematicImportEntryPointTests.cs`.

## 12. Acceptance (SI-6)

`tests/Ui.Tests/SchematicImport/SchematicImportAcceptanceTests.cs`:

- **The round trip**: the authored `testdata/schematic-import/sexpr/matching.kicad_sch` (series L, shunt C, series R,
  two connectors), imported by the CLI with both connectors as Ports, `check`s clean and simulates to the S-parameters
  of the same circuit written as a `.cnl`, to 1e-9. An imported schematic of native parts is a circuit.

Open for the owner: a value written as a bare number (`10` on a resistor) is not read as base units, so it becomes a
default-valued variable.
