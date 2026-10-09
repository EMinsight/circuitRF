# Create Schematic from Artwork

**Series:** `docs/sonnet-briefs/brief-artsch-0-overview.md` (decisions D1–D20) · **Code:**
`src/Design/Layout/Recognition/` · **Builds on:** the trace impedance review (`src/Design/Layout/Em/TraceImpedanceAnalysis*.cs`),
the shared copper partition (`src/Design/Layout/Extraction/CopperPieces.cs`, `src/Design/Layout/Drc/DrcConnectivity.cs`),
the board interchange readers (`src/Design/Layout/Interchange/`), railRF's part discovery, the footprint work, the drawn
netlist (`src/Design/Schematic/NetlistSchematic.cs`) and the microstrip family's stackup binding.

This note records the decisions and the pipeline, then one section per phase as it lands. AS-3 wrote it.

---

## 1. What it is for

A user imported a production board's Gerbers, put a port at every part, ran the planar MoM engine and was refused at
about two million unknowns. The board was simple: about ten SMT parts joined by 50 Ω lines and a top pour stitched to
the bottom plane by hundreds of vias. Nothing on it needed an EM run.

The command turns flattened artwork — an imported Gerber, a flattened GDSII, a `.kicad_pcb` read back as copper — into a
**schematic of native circuitRF components** that simulates in seconds: parts as R, L, C (or an SnP), traces as MLIN and
its discontinuities, grounded coplanar waveguide and stripline as CPWG and SLIN, anything else as TLIN in its physical
form, the stitching vias dropped and the vias that matter kept as VIA / VIAGND. It is a best attempt that the user then
checks and cleans up — a light model between "one impedance number" and brute-force EM, good to about 2 GHz on an
ordinary board, enough to predict a matching network and to compare a datasheet's target against a measurement.

## 2. The pipeline (D3)

```
artwork (.clay + .ctech [+ .cem] [+ placement/BOM])
   │
   ├─ AS-3  board graph   — ground net, via classes, signal islands, ports, scope
   ├─ AS-4  parts         — evidence → parts table (editable; CSV)
   ├─ AS-5  lines         — TraceImpedanceAnalysis chains → line elements
   │
   └─ AS-6  emit          — TestBench (+ globals, tune entries, sparam analysis)
                             └─ NetlistSchematic.Build → .csch in the target cell
```

`ArtworkRecognition.Recognize(RecognitionInput, RunControl?)` is the one entry point. It is pure: it reads what it is
handed, posts nothing, writes nothing, and returns a `RecognitionResult` — the board graph, later stages' outputs, and a
`RecognitionReport`.

## 3. The locked decisions, and why

| | Decision | Why |
|---|---|---|
| **D1** | Recognition produces a netlist (a `TestBench`); the drawing already exists (`NetlistSchematic.Build`). The `.cnl` text is a first-class output. | No second schematic writer. The `.cnl` is what a test compares and an agent reads. |
| **D2** | Everything that decides anything lives in `src/Design/Layout/Recognition/`, below the firewall; `src/Ui` holds the dialog, the parts-table view model and the cross-probe only. | A rule that lives only in the dialog is a rule the CLI does not apply. AS-7's gate is byte identity between the two. |
| **D3** | The pipeline above; the trace review is reused, not re-derived. Where it lacks something, the missing record is added to its output. | Its pieces, chains, junctions, end kinds, per-station Z0/εeff and configuration strings are exactly what line recognition needs; walking the copper twice is two answers. |
| **D4** | Target: a new cell (default `<artwork cell>_model`, made with `CellCreate`), or the artwork cell only when it has no schematic view. A re-run replaces only a schematic this command wrote, after a confirmation and a history checkpoint. | A hand-made schematic is never overwritten; the checkpoint is the revision-control floor. |
| **D5** | Every created component carries `FromArtwork=true`; Update Layout from Schematic skips it, Update Schematic from Layout does not duplicate it. The command never writes the layout. | Without it, a schematic written into the artwork cell would have its MLINs generated as copper on top of the imported Gerber. |
| **D6** | Ground: a named ground net (`GND`, `AGND`, `0`); else the piece with the largest copper on the stackup's reference conductor, and everything joined to it. A separate pour is a signal island and is reported. The user can name another. | The evidence order a designer would use; an override because every heuristic has a board it misreads. |
| **D7** | Vias: ground–ground dropped; signal–signal across layers a VIA; a ground pad that is its own island a VIAGND per via, nearest four; signal–ground a VIAGND. `vias=ground` makes every VIAGND a GND. | Hundreds of stitching vias are the thing that refused the MoM run; the few that carry current to ground are real inductance. |
| **D8** | Ports, in priority: the `.cem`'s, layout pins and port labels, a multi-pin part's pads on a line, an edge launch or connector, a scope crossing. All 50 Ω. | The user's own ports first; the rest are the places a matching network is measured from. |
| **D9** | A part with more than two pads is cut out; its pads on lines become ports, on ground join ground, the rest are left open and listed. | The schematic is the networks around the device as the device sees them — what matching work needs. The device comes in afterwards as an SnP or through Import Component. |
| **D10** | Parts evidence: placed footprints, placement + BOM, land-pattern match, silkscreen (AS-10). Unknown values become global variables `<Refdes>_<Param>` with tune entries and transparent initial values. The parts table is a CSV. | Every unknown part is a knob; the first simulation shows the lines alone. The format is the contract for a user and an agent alike. |
| **D11** | Scope: the whole layout, the selection, or `--region` with SI units. A line crossing the boundary becomes a port. | The designer's "clip a section" case, without any EM. |
| **D12** | The schematic carries a provenance block; each component its artwork anchor. | The anchor is the cross-probe; the provenance marks a schematic as this command's to replace. |
| **D13** | Line element by the majority configuration of its stations: MLIN, CPWG, SLIN, else TLIN in its physical form. The coplanar reading is the user's to choose; measured gaps are always recorded. | A trace is easily misread as GCPW when a pour comes close; recording the gaps makes it one swap away. |
| **D14** | MLIN regions get MBEND / MTEE / MCROSS / MTAPER; CPWG / SLIN / TLIN regions do not. Short pieces merge into neighbours; no line is dropped for being short. Coupling is reported, not modelled. | A short thin line is an inductor; coupled-line models are not in this series. |
| **D15** | The testbench carries an S-parameter analysis: the `.cem`'s range, else 100 MHz – 6 GHz, 201 points. | Enough to see a match; editable. |
| **D16** | Every guess and omission is a finding with a count. Refused only for no technology, no copper in scope, or no port. | A best attempt is the product; hiding what it guessed would make it untrustworthy. |
| **D17** | Committed fixtures are synthetic; field boards are git-ignored `FixtureFact` data. | Third-party artwork is never committed. |
| **D18** | Non-goals: coupled lines, step / open / gap models, case parasitics, multi-pin device models, MKLOPF, writing the layout. | Scope. |
| **D19** | Swap Line Type: MLIN ↔ CPWG ↔ SLIN ↔ TLIN in place, W and L held, Z0 free. | The misread-line remedy, useful on any schematic. |
| **D20** | A `.csch` may name its own technology (`TechRef`; `.cnl` `technology "<path>"`), resolved before the workspace default. | An imported board's cell carries its own `.ctech`; without this an MLIN would be computed on the wrong substrate, silently. |

## 4. AS-3 — the board graph

`src/Design/Layout/Recognition/`: `ArtworkRecognition.cs` (entry point, `RecognitionInput`, `RecognitionResult`),
`BoardGraph.cs`, `GroundReading.cs`, `ViaClassification.cs`, `PortDiscovery.cs`, `RecognitionScope.cs`,
`RecognitionReport.cs`, `RecognitionOptions.cs`.

### 4.1 Inputs and the partition (R-as3-1, R-as3-2)

`RecognitionInput.FromFile(clay)` reads through `TraceImpedanceAnalysis.LoadLayout` — the walk the trace review and
`circuitrf impedance` already use: the view, its technology resolved as the editor resolves it, the placed cells
flattened as a DRC run flattens them. Its EM setup is the `.cem` given, else the first in its workspace that analyses
it (`EmSetupResolver.FindSetupsForLayout`).

Islands are built over `CopperPieces.Build` — **the one partition** DRC, railRF and LVS share — and a via is classified
by the pieces its barrel touched, read off the partition's own record (`ConnectivityPartition.Barrels`, now carried on
`CopperPieces`). No barrel is tested a second time and there is no second connectivity model. The whole board is
partitioned **once**; a scope adds exactly one more partition, of the clipped copper.

The graph reads that partition at the **piece** level, not the net level. A shunt part's ground pad tied to the plane
by six vias is on the ground *net*, and so is a shorted stub; neither is ground *copper*, and the vias between them and
the plane are the elements the circuit needs.

### 4.2 Ground (R-as3-3, D6)

The ground choice, in order: `RecognitionOptions.GroundAt` (a point — on no copper it is a refusal naming the point);
`GroundNet` (a name the artwork does not state is a refusal listing those it does); a net the artwork names `GND`,
`AGND` or `0`; the largest piece on the stackup's ground-reference conductor; the reference itself where the stackup
declares it and the artwork draws nothing (every net a barrel carried down to it is ground); and, where no reference
is flagged, the largest copper on the board. The report states which.

Every conductor piece on the ground net is then either **ground copper** or not:

- **ground copper** (a pour or a plane) — the chosen piece; or a piece **wider than any trace** somewhere (it survives
  erosion by half the widest trace the review reads on its layer, `TraceImpedanceAnalysis.WidestTraceDbu` — its own
  `WidthRange`); or a piece carrying `PourViaCount` (4) vias or more that is **not pad-sized**;
- **a candidate ground pad** — pad-sized by the review's own pad rule: shorter than `MinAspect` (4) of its widths and
  no larger than the widest trace either way;
- otherwise copper on the ground net through a via: a shorted line.

A pour **not** on the ground net is a **separate pour**: a signal island, reported by name and location.

### 4.3 Vias (R-as3-4, D7)

An **island** is a set of non-ground conductor pieces joined through the barrels between them. A via's ends are the
pieces its barrel met, plus the undrawn reference when its span passes one:

| ends | class | element |
|---|---|---|
| ground copper only | `Stitching` | none — dropped and counted |
| islands only | `SignalTransition` | `VIA` |
| ground copper and a ground-pad island | `PadGround` | `VIAGND`, the nearest `MaxGroundViasPerPad` (4) to the pad's centre; the rest none, counted |
| ground copper and any other island | `SignalShort` | `VIAGND` |
| one conductor or none | `JoinsNothing` | none — counted |

A **ground-pad island** is an island whose every piece is a candidate ground pad and which has at least one via to
ground copper. A part's ground pad that sits **on** a pour is part of the pour's piece, so its vias are stitching and
the part is a plain GND; nothing needs a rule for it. Under `vias=ground` every `VIAGND` is `GND`. Several VIAGNDs on
one pad are reported once: their mutual inductance is not modelled. Each via keeps its drill, pad, span and position for
AS-6's `ViaSubstrateInjection`.

### 4.4 Signal islands (R-as3-5)

An island is a **signal island** — one node of the circuit before AS-5 subdivides it — when it is not a ground pad and
it carries a port, a pad of a two-pad placed part, a kept via, a line (any piece that is not pad-sized), or a separate
pour. Anything else is **copper read as nothing** (a test point, a logo, a fiducial): listed and left out, but kept in
the graph as `IslandKind.Nothing` so AS-4, finding a part's land on it, can promote it. A multi-pin part's pad does not
make its island a node by itself (D9): a bias pad on a pad of its own is left open.

### 4.5 Ports (R-as3-6, D8, D9)

`PortDiscovery`, in priority order:

1. the `.cem`'s ports — a layout's port labels, numbered by `EmPortExtraction.NumberPorts`, with the setup's Z0;
2. without a `.cem`, the same labels as port labels; then layout pins;
3. pads of a placed part with more than two pads that land on a signal island (`PlacedPins.Of`, artwork only). Placement
   rows and land-pattern clusters are AS-4's reading;
4. a signal island reaching the board outline within `EdgeReachMicrons` (500 µm) — the importer's `Outline` layer where
   it drew one, else the copper's extent — one port per stretch of copper in the edge strip, at the copper's own edge; a
   separate pour is not an edge launch. And the pads of a connector footprint — a placed part or a placement row whose
   designator is J, P or X **followed by a digit** (so `PS1`, a power supply, is not one) — on a signal island;
5. a crossing of the scope's boundary, centred on the cut, named `X1`, `X2`, ….

A port within `PortMergeMicrons` (1 mm) of a higher-priority port on the same island is that port. Ports from labels
keep the number they state; every other port takes the lowest free number, pins first, then left to right and top to
bottom. Names are `P<n>`, a label's own text where it is not a bare number, a pin's name, `<Refdes>.<pin>` for a part's
pad, or `X<k>`. A port label, pin or connector that lands on no signal copper is reported, not a port. No port in scope
is the refusal *"Nothing in scope reaches a port — add port labels, a .cem, or widen the selection."*

### 4.6 Scope (R-as3-7, D11)

`RecognitionScope` is `Whole`, `Rectangle(...)` or `Polygons(...)` (the selection's outline), DBU. Copper is clipped to
it with `LayoutClipper`; a via or a label is kept where its centre is inside. **Ground is read from the whole board**:
each clipped piece takes the class of the whole-board piece under it, so a pour the selection cuts in two is still one
ground. A crossing is where the original copper continues just outside the boundary; when the scope cuts a piece of
ground copper the report says so.

### 4.7 The report (R-as3-8, D16)

`RecognitionReport` is a list of `RecognitionFinding(Class, Count, Sentence, Anchors)` — the GUI expands a class and
cross-probes its anchors, the CLI prints one line per class. AS-3's classes: ground chosen, stitching vias dropped,
ground vias kept (and capped, and their coupling not modelled), signal vias kept, vias joining nothing, separate pours,
copper read as nothing, conflicting net names, ports by source, ports on no signal copper, and the scope cutting ground.

### 4.8 Gates

`tests/Ui.Tests/Recognition/`: `BoardGraphGroundTests`, `ViaClassificationTests`, `PortDiscoveryTests`,
`RecognitionCountersTests` (the 200-via board reads its partition once — a counter on the `ConnectivityCache`), and
`ArtworkRecognitionFieldTests` (`FixtureFact` over the git-ignored `testdata/artwork-boards/<board>/`, each with a
hand-written `expected.json`: `{ "clay": "<path to the .clay>", "ports": <count> }`).

---

## 5. AS-4 — parts and the parts table

`PartReading.Read`, called by `ArtworkRecognition.Recognize` after the board graph is built; the result carries a
`PartsTable` (`RecognitionResult.Parts`). Files: `PartReading.cs`, `LandPatternMatch.cs`, `PartEvidence.cs`,
`PartsTable.cs`, `PartsTableCsv.cs`, `PartModelResolution.cs` (class `PartModelResolver` — railRF already has a record
named `PartModelResolution`, the one-library resolution this calls).

### 5.1 Evidence (R-as4-1, D10)

Strongest first, and every field of a row records which source gave it (`Evidence`: `refdes=placement;kind=refdes;
case=land;value=bom;pn=bom;model=file`):

1. **Placed instances** — `PlacedPins` (railRF's and LVS's walk) gives each designated part's pads on their own land
   layer; `LayoutPartKind.Of` the declared kind; the land-pattern cell's name the case (`smt-<case>@<density>_<hash>`
   parsed exactly, else any unambiguous token through `FootprintTokens`).
2. **Placement + bill of materials** — passed in already read, refusals intact: a refused placement or BOM refuses the
   recognition with the reader's own sentence, so an unstated origin is never guessed. A placement row lands where
   `RailPartMarks.For` puts the part's body (the row's point ± `PadReachDbu`), on the nearest unnamed land pattern whose
   centre or a pad is inside it. A row naming a placed instance only adds its case. BOM rows are taken by designator;
   several rows with different part numbers or values give neither, with a note.
3. **Land patterns** (`LandPatternMatch`) on pads no instance claims. Pad outlines come from the side's **paste**
   openings, else its **solder-mask** openings (by board-format alias, purpose, then a name carrying the side), each a
   union of the layer's shapes kept where it is at least 82 % of its bounding box and has copper of that side under its
   centre. A side with neither reads **copper**: pad-shaped pieces by the trace review's own pad rule, and the copper
   ahead of every trace end the review names `"pad"` (an axis-aligned window as long and wide as the largest land). The
   reference lands and mask openings are **generated** — `ChipLandPatternGenerator` draws every case at every density
   once on a minimal board technology — never tabulated. A pair matches a case when both pads' along/across extents and
   the gap are each within 20 %, at 0° or 90°; smallest RMS error wins; a different case within 5 points is the
   runner-up, named in the row's notes. Candidates resolve best fit first; a pad is in one part at most. A third pad of
   the same size continuing the pair's line with the pair's own gap (±10 %) makes the pair two pins of a package row.
4. **Further sources** through `IPartEvidenceSource` (AS-10's silkscreen): a `PartClaim` names the nearest unnamed part
   within its reach.
5. **Nothing**: generated designators `C_A1`, `C_A2`, … top to bottom, left to right.

### 5.2 Kind, value, connection, model (R-as4-2 … R-as4-6)

- **Kind**: designator prefix (`R`; `L`/`FB`/`FL`; `C`; `J`/`P`/`X` → Connector; a two-pad `D`/`Q`/`U`/`IC`/`Y`/`SW`/`TP`
  is `Ignore` with a note, more pads `MultiPin`), then the placed `PartKind`, then the BOM description's type word
  (`BomTablePaste.ReadTypeWord`, shared). More than two pads is `MultiPin` unless a connector. A BOM not-fitted marker
  (`BomTablePaste.IsNotFittedMarker`, plus a capital `NF`) is `Open`; a jumper case or an R whose value is 0 Ω is `Short`.
- **Value**: the BOM value column, else its description, through `BomTablePaste.TryReadValue` (SI prefixes, the
  letter-as-decimal `4R7` / `10n0`), in base SI. A value of another dimension than the kind is a note and a
  `PartValueWrongDimension` finding, and is not used.
- **Unknown value** → `Variable = <Refdes>_<Param>` on every modelled R/L/C/unknown row without a value or a measured
  model; `PartRow.TransparentValue` is its starting value (series C 100 pF, L 0.1 nH, R 0 Ω; shunt C 0.01 pF, L 1 µH,
  R 1 MΩ).
- **Connection**: the two terminals are paired by `RailPartDiscovery.SeriesTerminals`, and each is looked up in the
  board graph — an island, ground copper (a body piece, or a `PadGround` island) or no copper. One end on ground is
  `Shunt` (terminals ordered signal end first); both on ground `Shorted`; both on one island `Bridged`; otherwise
  `Series`; an end on no copper `Unplaced`. Shorted, bridged and unplaced parts are left out (`PartRow.IsModelled`).
  A modelled part's island that AS-3 read as `Nothing` is promoted to `Signal`.
- **Model**: a BOM part number whose row in any workspace `.crlib` attaches a Touchstone file, else a workspace `.sNp`
  whose name begins with it (shortest name wins, the others noted). Dot-folders are not searched. A file with other
  than two ports is not used and the row says so.

### 5.3 The table and its CSV (R-as4-7, R-as4-8)

Columns `Refdes, Kind, Connection, Case, Value, Variable, Model, ModelFile, PartNumber, X, Y, Evidence, Confidence,
Notes`, UTF-8, comma-delimited, natural designator order, LF line ends. Values are written `4.7 Ohm`, `100 pF`,
`1 uH` — the spelling the BOM value reader reads back. `X`/`Y` are in the layout's display unit with the unit stated.
`ModelFile` is relative to the workspace root (the layout's folder when there is none). Confidence: `high` for a placed
instance or a BOM row on a part the placement landed; `low` for a land pattern alone of unknown kind; `medium` otherwise.

Reading back is an overlay on a fresh recognition: `Kind`, `Value`, `Variable`, `Model`, `ModelFile` are applied (a
value clears the variable; a kind change keeps a value only within the same dimension; an SnP file is checked for two
ports); `Connection`, `Case`, `X`, `Y`, `Evidence`, `Confidence` and `PartNumber` are measured and ignored, with a note
where they differ; `Notes` is ignored. An unknown column is a refusal naming it; a designator the board does not have
is reported and ignored. Write then read is the identity.

### 5.4 The report (R-as4-9)

Parts by source (instances, placement, silkscreen, land pattern alone), unknown kinds, unknown values (the variables),
values in the wrong dimension, SnP models, land-pattern ambiguities, shorted / bridged / off-copper parts left out,
multi-pin parts and connectors cut out (with their part numbers, for Import Component), BOM designators and placement
rows not found on the board, sides with no mask or paste layer, and a parts table's notes and unknown designators.

### 5.5 Gates

`LandPatternMatchTests`, `PartReadingTests`, `PartModelResolutionTests`, `PartsTableCsvTests`, and
`PartReadingFieldTests` (`FixtureFact`; the field board's `expected.json` gains `"parts"`, `"series"`, `"shunt"`).

---

## 6. AS-5 — traces to line elements

`LineRecognition.Recognize`, called by `ArtworkRecognition.Recognize` after the parts; the result carries a
`LineRecognitionResult` (`RecognitionResult.Lines`): the elements and the nodes between them. Files: `LineRecognition.cs`
(the orchestration and the report), `LineSegmentation.cs` (one trace → its elements), `LineJunctions.cs`,
`LineTypeChoice.cs` (D13 and the coplanar option, and `LineBinder` — the stackup asked whether a component binds),
`LineElement.cs` (the output records).

### 6.1 The reader (R-as5-1)

**One** trace review per recognition, shared with AS-4's part reading (`RecognitionResult.ReviewRuns` holds it at one).
It is run with a scope of one region round all the copper in scope: a chain a selector chooses is a trace from
`SelectedMinAspect` (2) widths rather than `MinAspect` (4), so a short line between two parts is a line and not a pad.
Its target, tolerance and findings are not read.

What recognition needed and the review did not return was **added to its output records**, computed where the review
already has the geometry:

- `TraceStation.H` — the height the classifier's coplanar threshold is measured against;
- `TraceRun.Corners` (`TraceCorner`) — every join between consecutive pieces that turns by `CornerMinDeg` (15°) or more:
  the point the centre lines meet, the signed turn, the width, and the chamfer measured by walking out along the outward
  bisector to the copper's edge (a chamfer edge pairs with nothing, so the pieces of a mitred and a square corner are the
  same);
- `TraceLayerResult.Junctions` (`TraceJunction`) with `TraceRun.StartJunction` / `EndJunction` — each junction's member
  traces and its centre, the least-squares meeting point of every member piece's centre line, read in the chaining step
  while every member piece is still known.

### 6.2 Line type (R-as5-2, D13)

Each cut is read again from what the review returns — the references either side, both side gaps and H — so the user's
choice applies without touching the review:

| References | Reading |
|---|---|
| below only | MLIN; CPWG under **Auto** when both gaps ≤ `CoplanarGapFactor`·H (default 3, the review's own), under **Gcpw** when both gaps are measured at all; never under **Microstrip** |
| both sides | SLIN; TLIN ("stripline with coplanar ground") when a side gap is within the factor |
| above only | TLIN ("… (reference above)") |
| none | TLIN ("coplanar waveguide (no ground plane)") |

An MLIN, CPWG or SLIN the stackup cannot bind — `SubstrateResolver.ResolveElectrical` with the trace's conductor and its
**measured** reference, or `ResolveStripline` — is a TLIN naming the resolver's reason. A cut with no Z0 is *unsolved*.
A run of one reading shorter than max(2·W, 0.5 mm) takes its neighbours' (both, where they agree; else the longer). A
CPWG whose mean gaps differ by more than 1.5× is a TLIN. Every MLIN and CPWG records the measured gaps, length-weighted,
whatever was chosen (D12, D19).

### 6.3 Segmentation (R-as5-3 … R-as5-5)

One walk along each trace's pieces and joins:

- **Straight.** Pieces of one reading and one width class (`MergeToleranceMicrons`) are one line. A piece shorter than
  max(W, 100 µm) between straight joins takes its longer neighbour's width and reading; its length stays in the line.
- **A straight join** is a jog (one line), a **step** (the lines abut at the join's middle), or — between two microstrip
  pieces of different width — an **MTAPER** (W1, W2, L = the join) when the join is at least 2·W of its narrow end. The
  piece finder sees no piece in a linear ramp (its edges are not parallel), so a taper *is* a join.
- **A corner** in a microstrip region is an **MBEND**: `Angle` the unsigned turn, `Miter` the nearest of the model's
  options (none, 0.5·W, `MicrostripDiscontinuities.MiterCutLength(W, h)`) to the measured chamfer. A 90° corner made as
  two 45° corners is two MBENDs with the diagonal between them an MLIN. Anywhere else a corner is centre-line length.
- **Reference planes.** An MBEND owns its corner square: each adjoining line stops W/2 short of the corner point (so the
  two lines and W make the centre line). A trace's end at a **pad** stops at the pad's edge — where the review's end trim
  already put it. At a **via** it stops at the land's edge. At a **junction** see §6.4. A line left with no length is
  absorbed: its two nodes become one, and it is counted.
- **Taps.** A part's terminal, a via or a port the trace runs *through* — within a width of its centre line, more than a
  width from either end — splits the line there: a shunt part's pad standing on a line is a node in it, not at its end.

### 6.4 Junctions (R-as5-4, R-as5-5)

Three arms that are all MLIN are an **MTEE**: the two most nearly collinear arms are the through line, the branch is on
the right of travel from pin 1 to pin 2 (the model's through along +X, branch along −Y). Four are an **MCROSS**, pins
counter-clockwise from the arm nearest +X. The arm lengths follow the models' own reference planes, which are where their
artwork stops: `MTeePCell` admits a through arm no shorter than W3/2 and a branch no shorter than max(W1, W2)/2, and
`MicrostripTeeModel`'s star network carries "no reference-plane shift beyond what the star itself represents";
`MCrossPCell`'s arms stop half the crossing arms' width out. So each arm runs from the trace's end to the centre, less
half the crossing arm's width. More than four arms, or any arm not microstrip, is a **plain node**: every arm runs to the
centre and the report says so.

A junction of **two** arms is how the review reads a sliver between two collinear pieces (both of its ends meet both
neighbours); the sliver is dropped as a pad-length chain, so the two arms of one type and width class are merged back into
one line and the sliver is counted as absorbed.

### 6.5 Ends and nodes (R-as5-6)

Nodes are named for what they are: `R1.2` (a part's terminal, AS-4's row and terminal), `V4.1` (a kept via's end on one
island), `P1` (a port), `J2` / `J2.3` (a plain junction / an MTEE or MCROSS arm), `T5_2` (between two elements of trace
T5), `O1` (an open end). A trace's end attaches to the nearest part terminal, port or via on its island within
2·W + 1 mm (a via within its land's radius plus a width); else to another loose end within two widths on the island
(a gentle bend the review did not join); else it is an **open end** — an open line, no open-end model, listed. A part
terminal, via or port no line reaches joins the nearest line end on its island, or the island's other attachments.
`LineRecognitionResult.NodeOf(name)` gives the node any of those names ended up as.

### 6.6 Parameters (R-as5-7)

SI, named as the components name them; the substrate is never written (injected at extraction from `SignalLayer` and the
measured `GroundReference` each element carries):

- MLIN `W`, `L`; MBEND `W`, `Angle`, `Miter`; MTEE `W1`–`W3`; MCROSS `W1`–`W4`; MTAPER `W1`, `W2`, `L`;
- CPWG `W`, `L`, `G` (the mean of the two mean gaps); SLIN `W`, `L`;
- TLIN `Z`, `Eeff` (length-weighted over the solved cuts), `L`, `F` (D15's top frequency: the option, else the `.cem`'s
  stop where it is a plain number, else 6 GHz), `Ad` = (π·f/c₀)·(Er/√Eeff)·((Eeff−1)/(Er−1))·tanδ and
  `Ac` = Rs/(Z0·W), both in dB/m. **`Ac` is an estimate**: the strip's own surface resistance over its width, with no
  current crowding, no ground-return loss and no roughness. Er, tanδ and σ are the stackup's between the trace and its
  reference. Every element also carries its drawn `Width`, so a TLIN swapped to another type (D19) keeps W.
- A segment with no solved cut is a TLIN at the nearest solved segment's Z and εeff on the same trace (else the layer's),
  marked unsolved.

### 6.7 Coupled pairs and the report (R-as5-8, R-as5-9)

Two straight segments on one layer, parallel within 2°, edges within 3 mean widths, overlapping for more than λ/20 at the
top frequency (λ from their mean εeff) are one *coupled, modelled uncoupled* finding naming both. Nothing in the circuit
changes. The report adds: elements by type; TLIN fallbacks by reason; segments read as CPWG and as MLIN under the coplanar
reading in force (and how many MLINs had ground both sides); junctions over four arms; bends, junctions and steps outside
microstrip (one finding); open ends; slivers and lines absorbed; coupled pairs; unsolved segments.

### 6.8 Gates

`LineSegmentationTests`, `LineJunctionTests`, `LineTypeChoiceTests`, `LineRecognitionCountersTests` (a 40 mm line solves
one cut; the review runs once) and `LineRecognitionFieldTests` (`FixtureFact`; the field board's `expected.json` gains
`"lines": { "MLIN": [min, max], … }`).

---

## 7. AS-6 — the circuit, the drawing, the target cell

`ArtworkRecognition.Run(input, target, options)` is the one entry point the GUI command and the CLI verb call: recognise,
emit, draw, write. Files: `RecognitionEmit.cs` (the circuit), `RecognitionProvenance.cs` (the component fields and the
provenance block), `RecognitionTarget.cs` (the target and `Run`); in `src/Design/Schematic`, `SchematicTechnology.cs`
(the per-schematic technology) and `ArtworkProvenance.cs`.

### 7.1 The per-schematic technology (R-as6-1, D20)

A `.csch` may carry `TechRef` (relative to the `.csch`, the `.clay`'s spelling) and a `.cnl` a `technology "<path>"`
statement (relative to the `.cnl`; top level, once). `SchematicTechnology.Resolve` takes the document's own reference
first and the workspace default only without one — the order a layout uses — and every stackup injection is handed what
it returns: `NetExtractor` (MLIN family, CPWG, SLIN, VIA/VIAGND, MMIC passives), `CnlTechnologyBinding`, and the
parameter editor's readouts. A reference that does not resolve is an error naming the path, never a fall back to the
workspace default: `check` reports `check.schematic.technology-unresolved`, and a netlist naming a missing file is refused
by the binding. A schematic with a `TechRef` writes the statement into the text it extracts to, relative to the base that text is read
back against (its workspace root, else its own folder — where Simulate writes `netlist.cnl`; Simulate restates it when the
file lands elsewhere);
`NetlistSchematic.Build` turns a netlist's statement back into the drawing's `TechRef`. `TechnologyDivergenceReport`
compares the layout's technology with the schematic's RESOLVED one, and for a schematic with a stackup-bound line compares
the two STACKUPS as well as the layer tables (`StackupComparison`); `explain` reports a schematic's and a netlist's
technology as a `technology` walk step. Without a reference, everything is byte-identical to before.

### 7.2 The circuit (R-as6-2)

`RecognitionEmit.Build` turns the result into a `TestBench`:

- **Instances**: ports as AS-3 named them, parts by designator, lines `TL…`, bends `B…`, tees `TEE…`, crosses `X…`, tapers
  `TP…`, CPWG `CP…`, SLIN `SL…`, TLIN fallbacks `TF…`, vias `V…` / `VG…` — numbered in the order a breadth-first walk
  from port 1 meets them. A designator that collides with a series name keeps it and the series skips it.
- **Nets**: a port's net is its name in lower case, ground `0`, every other `n<k>` in the walk's order. A `Short` part and
  a via under `vias=ground` merge their nodes (with ground, for the via); `Open` and `Ignore` parts are nothing.
- **Lines** carry W, L (in mm), the bend's angle and miter, a TLIN's Z, Eeff, F and losses, and `SignalLayer` /
  `GroundReference` — never a substrate value. **Vias** carry Drill, Pad and `FromLayer` / `ToLayer` (the conductor of
  each end's island within the via's span); `GroundLayer` is left to the injection's default.
- **Parts**: R/L/C with the value, or with the variable's name; an SnP two-port with its file (port 2 on ground for a
  shunt part). Each variable is a global at its transparent start with a `tune` entry: tune on, optimize off, ×0.1…×10,
  or 0…10 Ω / 0…1 nH for the zero-ish series R and L.
- **SP1**: the `.cem`'s sweep, else 100 MHz – 6 GHz, 201 points; `--start/--stop/--npts` through `RecognitionEmitOptions`.
- **The technology statement** names the artwork's `.ctech`.

`RecognitionCircuit.CnlText(dir)` is the `.cnl` with the technology and every model file relative to where it is going.

### 7.3 The drawing (R-as6-3)

`NetlistSchematic.Build(lib, tb, dir, hints)` draws it. With hints (each instance's artwork point, y up), the hangers of
one main-line net are ordered by their projection on the artwork's travel there (read from the path elements either side),
and a two-pin hanger whose copper lies on the left of that travel is drawn above the line. Hints that agree with the
default drawing change no byte of it; no hints is the default code path.

### 7.4 The component fields and the provenance (R-as6-4, R-as6-5)

`EditableComponent.FromArtwork`, `ArtworkAnchor` (DBU points) and `ArtworkMeasured` (line Z0, Eeff, side gaps; a TLIN's
W) are persisted in the `.csch`, written only when set, and never reach the elaborator. They are laid on the drawing by
instance name after `Build`; the drawing's own ground symbols get none. The schematic's `ArtworkSource` block records the
source `.clay` (relative), the scope and its rings, the options, the parts CSV's SHA-256, the version and the time.

### 7.5 The target (R-as6-6) and the L5 commands (R-as6-7)

`NewCell(name)` creates the cell beside the artwork's with `CellCreate.Create` (schematic only; an existing name or one
`NameValidator` rejects is a refusal). `ArtworkCell` writes the artwork cell's primary schematic, offered only while it
has no schematic view. `Replace(cell)` rewrites a primary schematic that carries `ArtworkSource`, after
`WorkspaceCheckpoints.BeforeWrite` (a `SavePoint`, intent "Create Schematic from Artwork"); one without it is refused —
*"<cell>'s schematic was not created from artwork; choose a new cell"*. The `.clay` is never written.

Update Layout from Schematic skips a `FromArtwork` component before resolution (no add, update, orphan report or ground)
and says *"N components model existing artwork — not generated"*. Update Schematic from Layout leaves a placement whose
`SchematicId` or `RefDes` names a `FromArtwork` component alone and says *"C6 is modelled from artwork — unchanged"*.

### 7.6 Gates

`SchematicTechRefTests`, `TechnologyDivergenceReportTests`, `RecognitionEmitTests` (the board checks with 0 errors and
simulates), `NetlistSchematicHintTests`, `RecognitionTargetTests` (the `.clay` byte-identical throughout; the checkpoint
a counter), `FromArtworkSyncTests` — all in `tests/Ui.Tests/Recognition/`.

## 8. AS-8 — Design ▸ Create Schematic from Artwork…

The GUI command is a shell round the same three calls the verb makes, behind `IArtworkRecognitionRunner`
(`src/Ui/Recognition/`): `RecognitionInput.FromFile` on open, `ArtworkRecognition.Circuit` for every preview, and
`ArtworkRecognition.Run` on Create. The interface exists only so a test can record what the dialog hands across.

### 8.1 The dialog's state is the CLI's options (R-as8-2)

Each control fills a field the verb fills from a flag — `RecognitionOptions` (vias, coplanar reading and factor,
`GroundAt` from the layout pick, `TopFrequencyHz` from a stated stop), `RecognitionScope` (the selection's outline as
polygons, or whole), the target, and the sweep. The sweep's composition moved out of `src/Cli/Recognize.cs` into
`RecognitionSweep` so both surfaces state a field-by-field override of the same basis; a field left at the basis is
unstated, exactly as an absent flag is. A placement file's origin need is read from the FILE (read with no origin), so
the three-way choice stays on screen once made; nothing is pre-selected and recognition waits for it.

### 8.2 Table edits are a CSV overlay (R-as8-2, R-as8-3)

`RecognitionInput.PartsCsvText` carries the dialog's held edits as the parts CSV they would be; the recognition lays it
over the board's table with `PartsTableCsv.Read` — the reader `--parts` uses — so a re-run after an option change keeps
every edit without the dialog owning any table logic. Only edited rows are written, and on them the untouched cells are
written as the table has them (an empty Value would clear a value — the CSV's own rule). The provenance hashes the text.
The Value cell's red border is `PartsTableCsv.TryReadValue`, the CSV reader's own value rule.

### 8.3 Cross-probe (R-as8-3, R-as8-5)

`ArtworkCrossProbe` resolves; the layout editor's `ArtworkProbe` overlay draws, through the DRC/LVS finding-marker
routine. A part is marked as `RailPartMarks` marks one (pads plus the placement body where one is stated); a line's
anchor is drawn as a ring that runs out and back along the centre line. Show in Artwork resolves the provenance's
`Layout` against the schematic's folder. Messages gained expandable rows (`IMessageSink.PostItems`, default: the text
alone), whose items act on a double-click.

### 8.4 The MoM refusal (R-as8-6)

`EmRunService.MeshCeilingRefusal` appends `WithoutEmPointer` to the unknown-ceiling refusal only.

### 8.5 Gates

`CreateSchematicFromArtworkViewModelTests`, `ArtworkCrossProbeTests` (`tests/Ui.Tests/Recognition/`) and
`EmRunServiceTests` (`tests/Ui.Tests/Em/`).
