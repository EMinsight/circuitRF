# Brief EMW-0 — Wave ports and terminal groups on EMerge: overview

**Series:** EMW-0 overview · EMW-1 spike (go/no-go) · EMW-2 single-terminal wave ports · EMW-3 terminal groups ·
EMW-4 the example. EMW-2…4 are outlined here and **briefed in full only after EMW-1's go/no-go**, as brief 124's spike
held brief 115. · **Tag:** `R-emw<n>-<k>` · **Depends on:** EMI-5 (an EMerge run exists) · **Origin:** owner request,
2026-10-21

---

## 1. Why a separate series

Wave ports, and above all **terminal groups** (several conductors ending on one port face, each its own port), took
briefs 113–117, 121 and 124 for Palace. The result lives in two places that are solver-neutral by design:

- the **document and problem model** of a terminal wave port (brief 114: `Em3dPort` with `Kind = Wave`, terminals,
  reference planes), which EMerge inherits for free through EMI-4's lowering;
- **`ModalTerminalTransform`** (`src/Engine/Em3d`, brief 115/124): pure functions turning a face's **modal** S into
  **terminal** S, from four inputs per frequency — the modal S, the matrix V of total-field line integrals along each
  terminal's path for each excitation, each mode's Z_PV, and each mode's wavenumber kₙ. Its Gram correction handles
  degenerate modes; its Robin correction K handles Palace's single-mode boundary term on inactive modes.

The expensive part was finding out what Palace does and deriving those corrections. For EMerge that work is a
**spike**: find out what EMerge does, then decide whether the transform is reused as is, reused with K = 1 (if EMerge
gives every mode its own boundary term), or not needed at all.

## 2. What EMerge offers (read from 3.0.0b2's source, 2026-10-21; EMW-1 verifies)

- **A wave port with its own 2D mode solve** on the port face (`ModalPort`), TE/TM/TEM or any, mode impedance by
  power-voltage, power-current or voltage-current — **PV is circuitRF's convention**.
- **Several modes on one face** (`number_of_modes`), with an alignment call that fixes the basis of degenerate modes by
  direction, and 3.0 keeps mode signs consistent between meshes.
- **Per-mode port properties after the solve**: each mode's β and Z₀ are stored with the result.
- **Field evaluation anywhere in the mesh, per excitation** — so the driver can compute V itself (each terminal path's
  line integral of the total E, for each excitation), the input Palace provided as `V_wp`.
- **Analytic ports** for rectangular waveguide and coax, which may serve the 3D Connector Launch's coax directly.
- What it does **not** have: a notion of terminals that turns a multi-conductor face into per-conductor ports. Its
  terminal assignment draws one impedance integration line per port. That is exactly the gap the transform fills.

**Why this matters beyond EMerge:** the Wave Ports example's **Pair** has no Palace setup, because Palace returns a
homogeneous face's two equal-speed modes in an arbitrary mixture that changes on every solve. If EMerge's mode
alignment gives a stable basis, Pair gets a second solver beside openEMS. EMW-1 answers that.

## 3. The plan

| Brief | Builds | Depends on |
|---|---|---|
| EMW-1 | Spike: four cases, the questions in its §2, go/no-go per case | EMI-5 |
| EMW-2 | Single-terminal wave ports (one conductor per face, and coax): lowering, renormalisation to Z₀ (`RenormaliseWavePorts`), reference-plane shifts, refusals | EMW-1 go on cases A and D |
| EMW-3 | Terminal groups: N modes per face, V from the driver, the transform (reused; K and G as EMW-1 found), every check `PalaceTerminalS` reports | EMW-1 go on B (and C) |
| EMW-4 | `examples/3D Wave Ports/`: EMerge setups for Launch, Coupled Microstrip and — if C went — Pair; Compare runs; numbers in `expected-numbers.json` | EMW-2, EMW-3 |

Until EMW-2/3 land, EMI-4 refuses a wave port on EMerge by name and points at the solvers that run it, and EMI-6's
Compare greys EMerge out for such a setup with that sentence.

## 4. Standing rules

EMI-0 §6 applies. In particular: the spike's harness and scratch runs live under `tools/emerge-wave-spike/` with a
git-ignored `runs/`; committed fixtures under `testdata/em3d/terminal/emerge-modal/`; findings in
`src/Design/RESOLVED.md`. Nothing from EMerge's source is copied; reading it to understand behaviour is fine.
