# Brief 131 — Expressions in a wire's points

**Tag:** `R-em3d131-n`
**Status:** Written 2026-10-09, not started. The owner decides §3's three questions before work begins.
**Area:** `src/Design/ThreeD/` (`C3dBindings`, the `.c3d` JSON contract, `C3dElaborator`, `C3dWires`), `src/Ui/ThreeD/`
(the Inspector's wire rows, the wire edits in `C3dEditorViewModel.Wires.cs` / `.WireShape.cs`, the drag rule in
`.Expressions.cs`), the `3d-view` reference topic. Findings in `src/Design/RESOLVED.md` and `src/Ui/ThreeD/RESOLVED.md`.
**Depends on:** brief 51 (dimensions as expressions), brief 50 (bond wires), 3D editor round 4 (wire arrays, loop
height and span).

---

## 0. The question

The owner wants a bond wire's geometry to be driven by the document's variables, as a box's size or a cylinder's
radius already is. Example: a loop apex at `h_loop`, or a foot at `x_pad + 50um` that follows the pad when `x_pad`
changes.

Today a wire binds exactly one field, `DiameterUm`, plus its array's `Count` and `Pitch`. Brief 51 deliberately left
every point list as numbers (§2b of that brief, "Binding a point list is a later brief"). This is that brief, for wires
only. Outlines, holes, polylines and polyhedron vertices stay numbers.

**Why it is not a one-line addition to `C3dBindings.Fields`:**

- the binding table holds **fixed-arity** fields (`Size` is 3 numbers, always). A wire's `Points` is a list of any
  length, and the Inspector can now insert and remove points;
- four edits **rewrite every point**: Loop height, Span, Re-seat Wire Ends, and Move/Rotate/Mirror of a wire (a wire
  has no placement, so a transform is applied to its points, `C3dWires` ~l.450). Each has to meet a bound component
  somehow;
- an end's **z is not the user's**. It is the top of the pad under it, re-seated on every edit (brief 50 R-em3d50-3c).

## 1. What exists to build on

- `C3dBindings`: the field table, `FieldsOf`, the expression map `IC3dBindable.Exprs` (keyed by property, one slot per
  component), and the JSON contract that writes an expression inline in place of a number.
- The drag rule, `PreviewThroughNames` (brief 51 R-em3d51-6): a gesture that would change a bound component writes the
  NAME when the expression is a bare variable, and is otherwise refused at the field it reaches first. Vertex mode's
  Move (G) on a wire already goes through it (`FaceToolChanged`), and does nothing for points today because no point is
  bound.
- `C3dDimension.Parse` and `C3dDimensionField`: the Inspector's number-or-expression field, with the error text and
  Esc behaviour.
- **The renumbering fallback, already in place (2026-10-09).** `C3dEditorViewModel.RenumberPointExpressions` moves
  every `Points[k]…` expression-map entry when a point is inserted or removed. Its own point's entries go with a removed
  point. Any other `Points` key shape **refuses** the insert or remove rather than guessing. The test is
  `EditorRound3PropertiesTests.AddingOrRemovingAPoint_RenumbersPerPointExpressions_AndRefusesAShapeItCannotRenumber`.
  **The key shape chosen in §2 must be the one that function renumbers.** If §2 picks another shape, change the
  function and its test in the same commit.

## 2. Design

### R-em3d131-1 — the file format and the key

Each point is written as today, `[x, y, z]`, and any component may be an expression string, exactly as `Size` is:

```jsonc
"Points": [[50000, 30000, 20000], ["x_mid", 30000, "h_loop + 20um"], [650000, 30000, 20000]]
```

In memory, the map entry for point *k* is keyed **`Points[k]`** with 3 slots, and its field paths are `Points[k][0..2]`.
On screen the path is `w1 point 2 z` (1-based, as the Inspector labels rows). A file with no expression in its points
is **byte-identical** to today's. That is the gate's first assertion, on every shipped `.c3d`
(`TerminalWavePortTests.Gate5_EveryShippedAndFixtureC3d_ReSavesUnchanged` already walks them).

`C3dBindings` gains a **list spec**: a property whose value is a list of fixed-arity elements. `FieldsOf` enumerates
it from the owner's current count. Nothing else in the table changes shape.

### R-em3d131-2 — evaluation

Points are resolved where every other bound field is resolved, **before** `C3dWires.Resolve` sees the wire, so pad
lookup, loop measurement and the solver only ever see numbers. An unresolvable point is a refusal naming the wire, the
point and the component, as any other field's is.

### R-em3d131-3 — the ends' z

An end's z is the top of its pad. See §3 Q1.

### R-em3d131-4 — the edits that rewrite points

Every one of these goes through the drag rule, unchanged. A component it would change that holds a bare name writes
the name; any other expression refuses the edit, naming the field.

| Edit | Components it changes |
|---|---|
| Inspector typed point | that point's three |
| Vertex-mode Move (G) of a point | that point's three |
| Loop height | the z of every interior point |
| Span | the end's x and y (and its seated z) |
| Re-seat Wire Ends | the ends' z |
| Move / Rotate / Mirror / Align of a wire | every component of every point |
| Add point (Inspector +▲/+▼) | none. The new point is a plain number from the cubic; later points' entries renumber |
| Remove point | none. That point's entries are dropped; later points' entries renumber |

### R-em3d131-5 — the Inspector

Each point row's x, y and z become `C3dDimensionField`s: number or expression, the same parse, error text, Esc revert
and "bound" styling as every other dimension field. The row's +▲ / +▼ / − buttons stay.

### R-em3d131-6 — check, explain, MCP

`check` reports an unresolvable point as it reports any field. `explain --tunables` lists bound point components.
`tune` lines on them work through the existing machinery (verify; do not assume). The `3d-view` reference topic
documents the shape in R-em3d131-1.

## 3. Owner decisions needed before starting

**Q1 — may an end's z be bound?**
- (a) **No (recommended).** The z is the pad's and is re-seated on every edit. A z expression on an end is refused on
  read, with a sentence. x and y may be bound.
- (b) Yes. The expression wins, and a mismatch with the pad becomes the elaboration's ordinary "end is not on a pad"
  refusal.

**Q2 — Move/Rotate of a wire whose points hold non-bare expressions.**
- (a) **Refuse, naming the first bound field (recommended).** This is the drag rule as it stands.
- (b) Rewrite each expression as `(<expr>) + dx`. This is new behaviour that brief 51 rejected for every other field.

**Q3 — scope.** Wires only (recommended), or polylines and polyhedron vertices too? They share the list-spec work in
`C3dBindings`, but each has its own editor paths to audit against R-em3d131-4.

## 4. Gates

1. Every shipped and fixture `.c3d` re-saves byte for byte.
2. A wire with `h_loop` on its apex z: changing `h_loop` changes the elaborated loop height. The assembly loop height
   measured by `C3dWires.Resolve` follows (the `WireGateTests` Gate 2 pattern).
3. A foot x bound to `x_pad + 50um`: changing `x_pad` moves both the pad and the foot, and the wire is not refused.
4. The drag rule, once per row of R-em3d131-4's table: a bare name is rewritten, and a compound expression is refused
   with the field named. One test per edit kind, not per component.
5. Insert and remove on a bound wire carry later points' expressions (already held by the fallback's test) and survive
   undo/redo.
6. Q1's chosen behaviour for an end's z.
7. `check` on a `.c3d` with an unresolvable point exits 1, naming wire, point and component.

Run the targeted classes only (`EditorRound3PropertiesTests`, `WireGateTests`, `WireArrayTests`, the brief-51 test
classes, `TerminalWavePortTests.Gate5`), not all of `Ui.Tests`.

## 5. Out of scope

- Outlines, holes, polylines and polyhedron vertices (unless Q3 says otherwise).
- Loop height and span as stored, bindable fields. They stay derived from the points (brief 50: a wire's points are
  its only truth). Binding the apex z is how a loop height is parameterised.
- A wire's points following a bound pad automatically. That is what an expression on the foot is for.
