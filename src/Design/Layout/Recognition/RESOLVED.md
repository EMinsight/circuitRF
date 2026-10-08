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
