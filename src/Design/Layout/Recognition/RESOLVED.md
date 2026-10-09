# `src/Design/Layout/Recognition/` — findings

Create Schematic from Artwork — `docs/design/artwork-to-schematic.md`, `docs/sonnet-briefs/brief-artsch-0-overview.md`.

---

## AS-3 — the board graph

### The trace review's pour rule cannot be read literally at the piece level

The brief asks that "small island" mean *not a pour by the trace review's own pour rule*, so there is one definition.
That rule (`TraceImpedanceAnalysis.FindLayer`) answers a different question — **which islands hold traces worth
reviewing** — and two of its clauses give the wrong answer for ground:

- **An island with no trace strips in it is never a pour** (`pieceArea <= 0 → continue`). A solid plane has no pair of
  facing edges within the widest-trace limit, so it has no strips, and the literal rule would read the board's plane
  as *not* ground copper.
- **An island carrying four vias or more is a pour.** A shunt part's ground pad with six vias is exactly that, so the
  literal rule would make the pad ground copper, its vias stitching, and the VIAGNDs the overview exists to keep would
  vanish — the brief's own gate (six vias → four VIAGND, two counted) cannot pass under it.

So recognition keeps the rule's **numbers and its pad rule** and reads them per piece (`BoardCopper`, `BoardGround`):
ground copper is the chosen piece, or copper **wider than any trace** (it survives erosion by half of
`TraceImpedanceAnalysis.WidestTraceDbu` — the review's own `WidthRange`, exposed for this, not re-derived), or copper
carrying `PourViaCount` vias that is **not pad-sized** by the review's `MinAspect` rule. The via count still catches a
narrow ground strip with a row of vias; it no longer catches a pad. The width limit and both constants are the
review's, so the two cannot drift apart.

### The partition records only barrels that met two conductors

`ConnectivityPartition.Barrels` keeps a barrel that touched two pieces or more — what the union-find needed. A via
landing on one pad and an undrawn reference plane touched **one** drawn conductor, so it is not there. `BoardCopper`
locates those through the partition's own point lookup (`CopperPieces.IndexAt` on each spanned conductor), so the
pad still knows its via; that lookup is not a second connectivity model — it decides no join.
`CopperPieces` now carries `Barrels` (read through a new `DrcConnectivity.Partition`, the same cached answer every
other form reads), so the partition is computed once: `RecognitionCountersTests` holds it at one extraction for a
200-via board.

### An any-layer point lookup answers with the plane

`CopperPieces.IndexAt(x, y, null)` returns the lowest-numbered piece under the point, and under a line on Top that is
often the Bottom plane. `BoardGraph.IslandAt` without a layer therefore searches each layer an island is on, and
`BoardCopper.ConductorAt` searches the stated layer first and then the conductors in stackup order.

### A scope is a second partition, and ground still comes from the first

Clipping the copper and partitioning it again is what makes "copper outside the scope is not read" true — an island
that leaves the selection and re-enters is two islands, as it must be. Ground is never re-read from the clipped copper:
each clipped piece takes the class of the whole-board piece under one of its own vertices, so a pour the selection cuts
in two stays one ground. A crossing is found where the **original** copper continues in a thin band just outside the
boundary, so a clipped edge is a cut and a piece that merely stops inside is not.

### A connector designator needs a digit after the letter

R-as3-6 says a connector's designator *starts* with J, P or X. Read literally, `PS1` (a power supply) and `PWR1` are
connectors. `PortDiscovery.IsConnectorRefdes` requires the letter to be followed by a digit.

---

## AS-4 — parts and the parts table

### The land-pattern references are generated on a technology of their own

`ChipLandPatternGenerator` refuses a technology whose top conductor does not sit directly on a dielectric — a board
with a via row between Top and its core in the stackup list reads as "not a laminate surface" and gets no lands. So the
references are generated once on a minimal two-row technology (front copper on a core, a soldermask by purpose) inside
`LandPatternMatch.References`, and the test boards' own land helper strips the via row for the same reason. The board
under recognition is never asked to be generator-friendly.

### A 30 % scaling is not a guaranteed miss

The case table is dense. An 0603 grown by 30 % lies within 20 % of the two-pad crystal's least-density land
(`XTAL3216@L`, RMS 0.12), an 0805 shrunk by 30 % fits `0603@L`, and an 0402 shrunk by 30 % fits `0201@M`. Those are
correct readings of geometry no table could tell apart, which is why the runner-up note exists. The gate scales an 0805
up, which fits nothing.

### "In a row" had to be strict

The first rule (a like pad on the pair's line within 0.5–1.5 pitches) rejected two chip parts placed end to end at
about one pitch, which is an ordinary layout. A package row is pads of one size at one gap, so the rule is now: same
size within 10 %, on the line, and the gap to the nearer end equal to the pair's own gap within 10 %.

### `PartModelResolution` is taken

railRF's `PartModelResolution` record (one part number in one `.crlib`) already lives in `CircuitRF.Design.RailRf`, and
recognition calls it. The brief's file name is kept; the class is `PartModelResolver`, so a file importing both
namespaces compiles.

### `NF` is a not-fitted marker only in capitals

`BomTablePaste`'s do-not-populate words do not include `NF`, and adding it there would make a pasted table's unit
column (`nF`) read as not fitted — its comparison lower-cases. The bill-of-materials check accepts the exact capital
`NF` beside the shared recogniser instead.

### A placement row lands by the body box railRF draws

A Gerber-only board has no pads by designator, so `PdnAttachments` finds none for a placement row; `RailPartMarks.For`
still returns the body box (the row's point ± `PadReachDbu`), and that box is what decides which land pattern the row
names — the same box the railRF window marks the part with.

---

## AS-5 — traces to line elements

### The review drops a short line between two parts unless something selects it

A chain shorter than `MinAspect` (4) widths is kept by the trace review only where a selector chooses it — a wide line
cut into sections by series parts is two to four widths long between them. Recognition runs the review with one region
round all the copper, so every chain is chosen and `SelectedMinAspect` (2) applies. AS-4's part reading had its own lazy
run with no scope; it now shares this one, which is what holds the review at one run per recognition.

### A taper is a gap, not a piece

The piece finder pairs edges anti-parallel within 2°; a linear ramp's edges are tilted by atan((W2−W1)/2L) — 26.6° for
the gate's 1 mm ramp from 0.5 to 1.5 mm — so the ramp has no piece and the chain joins straight across it. The join is
therefore what an MTAPER is read from. The gate's ramp is exactly 2·W of its narrow end, so the brief's "more than 2·W"
is read as "at least 2·W" (with 1 µm of slack); anything shorter is a step.

### A wide end piece is trimmed as a land

The review's end trim takes a chain's last piece off as a pad when it is 1.2× wider than its neighbour and shorter than
four of its own widths. A taper's wide side therefore has to run on for at least four widths, or it is read as a pad and
the trace ends at the ramp.

### A sliver between two collinear pieces reads as a junction

Both ends of a piece shorter than its neighbours' join distance (1.25 widths) meet both neighbours, so each end has two
candidates and all three are junction ends; the sliver is then dropped as a chain shorter than two widths. Its length is
not lost — each neighbour runs to the junction's centre — but it is two lines. Recognition merges a two-arm junction of one
type and width class back into one line, and counts the sliver as absorbed.

### A pad standing on a line does not end the trace

A shunt part whose pad sits on a line (AS-4's board: C2 on R1's output line) leaves the line's edges unbroken, so the trace
runs straight through the pad and the part's terminal is in the middle of it. Attaching that terminal to the nearest
trace END put the part at the open end of the line, 6 mm away. Recognition now splits a line at any terminal, via or
port within a width of its centre line and more than a width from either end ("taps").

### A chamfer is invisible in the pieces

A mitred corner's 45° edge has no anti-parallel partner, and for any chamfer up to a full width the outer edge still runs
past the inner corner, so the pieces of a mitred and a square corner are identical. The chamfer is measured in the review
(`TraceCorner.CutLeg`) by walking out from the corner point along the outward bisector to the copper's edge: a leg m brings
that edge m·cos(θ/2) nearer than the sharp corner's (W/2)/cos(θ/2).

### The square corner's pieces already end at the reference planes

For a right-angle bend the inner edges end W/2 short of the corner point on each side, and for a T the through pieces end
at the branch's edges and the branch at the through line's — exactly the MBEND's corner square and the MTEE's arm planes.
Recognition still measures every end against the corner point or the junction centre explicitly, because at any other
angle the pieces end somewhere else (a 45° bend's inner corner is 0.21·W from the corner point, not 0.5·W).

## AS-6 — emit, the target cell, the per-schematic technology (brief-artsch-6, 2026-10-08)

### The extracted `.cnl` names its technology relative to the WORKSPACE ROOT, not to the `.csch`

A schematic's `TechRef` is relative to the `.csch`, but the text a schematic extracts to is read back against
`SchematicCircuit.ReferenceBaseOf` — the schematic's workspace root, else its own folder — which is also where Simulate
writes `netlist.cnl` and what every other relative reference in that text (a Touchstone file) resolves against. So the
`technology` statement is relative to that base (`SchematicTechnology.NetlistRef`); a `.csch`-relative spelling would
resolve against the wrong folder whenever the schematic is not at the root. Simulate's `WriteNetlist` restates it for
where the file actually lands (`SchematicTechnology.Rebase`) — another workspace's root, or the scratch folder with no
workspace open. A first version wrote the absolute path; the owner requires a relative one. Recognition's own `.cnl`
(`RecognitionCircuit.CnlText`) is relative to where it is going.

### A layer name is written bare when it can be

`SignalLayer="Top"` reads correctly in a `.cnl` (the binding unquotes it), but the drawing carries the expression through
as text and a schematic's extractor does NOT unquote — so a drawn MLIN would have asked for a layer called `"Top"`, quotes
and all. The emit writes the name bare and quotes only a name with a space or a quote in it.

### A TechRef that does not resolve never falls back to the workspace default

`SchematicTechnology` answers no technology and an error, and the extraction lists it among its conflicts; `check`
promotes it to `check.schematic.technology-unresolved` (an error) and drops the duplicate warning. For a `.cnl` the
binding refuses the netlist outright, which `check` reports as unreadable. Falling back would compute every recognised
line on the workspace default's substrate, which is the failure D20 exists to remove.

### The divergence report compares stackups too (`StackupComparison`)

`TechnologyDivergenceReport` asked only `ExternalWorkspaceGate.CompareTechnologies`, which compares the two technologies'
LAYER TABLES — the placement gate's question, what a layout view means, and it leaves the stackup out on purpose. Two
technologies with the same layers and different substrates (1.6 mm of FR-4 against 0.5 mm of εr 3.5, AS-6's own gate)
compared equal, so a schematic and its layout on those two said nothing. Found writing `TechnologyDivergenceReportTests`;
the owner ruled the stackup must be compared. `StackupComparison.Difference` (`src/Design/Layout`) compares the conductor
and dielectric entries in order — kind, thickness, εr, tanδ, μr, conductivity, ground designation — and the two
boundaries, not names or drawing layers. The report asks it only for a schematic holding a stackup-bound line, since a
footprint resolves layers alone; the placement gate is unchanged.

### The checkpoint before a headless write moved below the firewall

`Optimize.CheckpointBefore` (`opt --save-preset`, the `yield` writes) is now `WorkspaceCheckpoints.BeforeWrite` in
`src/Design/Revision`, with the CLI's helper delegating to it, because recognition's replace needs the same floor and the
GUI command will call it from `src/Ui`. A replace takes it as a `SavePoint` with the intent "Create Schematic from
Artwork"; a workspace with no history records nothing and the replace goes ahead.

## AS-7 — the `recognize` verb and the MCP tool

### A layer name with a space was drawn into the schematic WITH its .cnl quotes

`RecognitionEmit.AddLayer` quotes a layer name holding a space (`SignalLayer="Top Copper (1 oz)"`) because the `.cnl`
line would otherwise split there, and `NetlistSchematic.Build` copied every override expression verbatim — so the drawn
`.csch` stored `"Top Copper (1 oz)"` quotes and all. A schematic stores layer names BARE (the extraction reads them raw
and quotes them as it writes the `.cnl`), so the substrate binding looked for a conductor named with the quotes, warned,
and bound the default layer. On the shipped LVS example that was 17 warnings from `check` on a freshly recognised
schematic; the `.cnl` written by `-o` was clean. Invisible to every AS-3…AS-6 gate because the synthetic technology's
layers are `Top`/`Bottom`. Fixed in `NetlistSchematic.Build` (it unquotes `SignalLayer`, `GroundReference` and the via
layer parameters), which also fixes `netlist --to-schematic` on any `.cnl` with such a name; gate
`NetlistToSchematicTests.AQuotedLayerName_IsStoredBare`.

### `ArtworkRecognition.Circuit` — recognise and emit, write nothing

The verb's `-o` path and its read-only default need the circuit without a target; `Run` always writes. `Circuit` is
`Run`'s first half (the emit-omissions finding included) and `Run` now calls the same private `Emit`, so the `.cnl` the
two paths write is one function's output. `RecognitionTarget.NewCellDir` is where a new cell goes, shared by `Run` and the
verb's `--replace` question rather than worked out twice.

## AS-8 — the GUI command

### The dialog's edits travel as CSV text, not as a second table model

The dialog re-runs recognition on every option change and rebuilds its rows from the result, so an edit stored on a row
would be lost. It is held instead as the override a parts CSV would carry and handed over as
`RecognitionInput.PartsCsvText`, laid over by the same `PartsTableCsv.Read` the CLI's `--parts` uses. One trap: on an
edited row every unedited cell must be written as the table already has it, because an EMPTY Value cell is the CSV's
"clear the value" — writing only the edited column would silently clear every other edited row's value.

### A placement file's origin need comes from the file, read with no origin

Reading with the chosen origin reports `OriginEvidence.Chosen`, which reads as "no origin needed" — the choice would
disappear the moment it was made. The need is decided from a read with no origin, then the file is re-read with the
choice.

### The sweep composition moved below the firewall

`RecognitionSweep` (`Parse`, `Basis`, `Compose`) is what `--start/--stop/--npts` did inside `src/Cli/Recognize.cs`;
the dialog's Frequency fields compose the same way, so a field left at the basis is unstated on both surfaces. The verb's
source-scan allow list names the two types.

### No `EmRunServiceTests` existed

The brief named it as existing; the run-level refusal tests live in `PortClearanceRefusalTests` and
`EmCeilingRefusalTests` (the latter tests the mesher, not the run). The new class drives `EmRunService.Run` on the
committed `testdata/portcal` fixtures — `separated-pair` meshed past the ceiling (refused before any fill, ~0.3 s) and
`offset-pair`'s port-clearance refusal as the "other" case.
