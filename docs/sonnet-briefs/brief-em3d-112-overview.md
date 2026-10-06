# Brief — 3D EM, eighth series: wave ports on both solvers, terminal wave ports, and two examples

**Status:** Briefed, not built · **Date:** 2026-10-05 · **Decisions: open, each with a recommended default** (§3)
**Design note:** [`docs/design/em-3d.md`](../design/em-3d.md) §2, §4.4 (reference planes), §5.3 (the FDTD port transform).
**Previous briefs:** 1–111. This series is numbered **112–118**.
**Precedent:** [brief 23](brief-em3d-23-wave-ports-and-eigenmodes.md) (Palace wave ports, mode 1 only, eigenmode),
its findings in `src/Design/RESOLVED.md` § "Wave ports and eigenmodes — brief-em3d-23", and
[brief 70](brief-em3d-70-showcase.md) (the `3D Connector` example).
**Area:** `src/Design/ThreeD/` (`C3dDocument`, `C3dPorts`, `C3dProblemAssembly`), `src/Engine/Em3d/` (`Em3dProblem`,
`FdtdPortTransform`, a new terminal transform), `src/Design/Em3d/` (`PalaceConfigWriter`, `PalaceRun`, `GmshGeoWriter`,
`CsxcadWriter`, `OpenEmsRun`, `Em3dRunService`, `Em3dPortMap`), `src/Ui/ThreeD/` (the port tool, the port rows),
`src/Cli/` (`check`, `explain`, `em`), `examples/`, `docs/user/src/reference/`
**Requirement tag:** `R-em3d<n>-<m>` as before.

---

## 0. The short answer

The owner asked for three things:

1. An example that teaches a **wave port**, on **Palace and openEMS**. Every shipped 3D example uses lumped ports.
2. A wave port where **two conductors end on one face** and **each conductor gets its own port** in the `.sNp`.
3. An example of Palace's **eigenmode** solve: a microstrip in a lidded cavity whose resonance is moved out of band by a
   grounded pillar.

Where those stand today:

| Ask | Today | What this series does |
|---|---|---|
| Wave port, Palace | Works (brief 23). Not in any example. | Nothing new in the engine; brief 117 uses it |
| Wave port, openEMS | **Refused** (`Em3dRunService.PalaceOnlyRefusal`) | Brief 116 builds it |
| Two conductors, one face, one port each | **Refused** (`C3dPorts.Wave`: "a wave port's mode runs between two conductors"); `Mode` is hard-coded to 1 (`PalaceConfigWriter.cs` ~270) | Briefs 114 (model), 115 (Palace), 116 (openEMS) |
| Eigenmode | Works from a `.c3d` (brief 23, used by `3D Package`'s *Lid modes*) | Brief 118: an example only |

When the series is done:

- A wave port on a `.c3d` or `.cem` runs on **Palace, openEMS or Both**.
- A wave port whose face is met by **N signal conductors and one reference** (a ground, a shield) is **one port with N
  terminals**. Each terminal is its own numbered port in the Touchstone file, referenced to its own Z0. These are
  **terminal** S-parameters (voltage and current on each conductor), not modal ones.
- Two new example workspaces: **3D Wave Ports** (the connector launch with its back end open and a coax wave port, plus
  a coupled pair with a two-terminal wave port at each end, both on both solvers) and **3D Eigenmode** (the cavity, with
  and without the pillar).

### The rules this series adds

> **1. A terminal is a port.** Everywhere a port number is used (the `.sNp`, the port map, field drives, `explain`), a
> terminal of a multi-terminal wave port is exactly an ordinary port. Nothing downstream of the solver learns that
> terminals exist.
>
> **2. One definition of a wave.** Every S-parameter this series produces comes out of one formula, the one
> `FdtdPortTransform` already uses: `S = √y·(U − Z₀·I)·(U + Z₀·I)⁻¹·√z`. Palace's terminal ports get their `U` and `I` from
> modal data (brief 115); openEMS's from probes (brief 116). No second wave definition is written.
>
> **3. A single-conductor wave port is unchanged.** Every existing `.c3d`, `.cem`, Palace configuration, Gmsh script and
> golden stays byte-identical. Terminals are written only when a port has two or more of them.

---

## 1. Things that are not obvious, resolved here once

**a. Palace's wave-port S is modal, and Palace has no terminal S at all** (schema 1-7-0; `waveportoperator.cpp`
`GetSParameter` projects onto a unit-power mode; `port-I.csv` covers lumped ports only, `postoperatorcsv.cpp` ~1022).
So a per-conductor result cannot be read from Palace. It has to be **computed** from what Palace does write.

**b. Palace allows several modes on one face, but only one of them absorbs.** Two `WavePort` entries may share
`Attributes` if **only one is `"Active": true`** and it has the lowest `Index` (`waveportoperator.cpp:1548`; the `Active` key
came from upstream issue #171 / PR #197, which asked for exactly this even/odd case). An inactive entry is still excited
and still measured, but it adds **no Robin term**. The face therefore absorbs correctly only at the active mode's
wavenumber k₁. Another mode m reflects there by about |(k₁ − k_m)/(k₁ + k_m)|. That error is **zero in a homogeneous
cross-section** (stripline, coax, twinax: every TEM mode shares one k) and about −29 dB for a typical microstrip pair
(√(εeff,e/εeff,o) ≈ 1.07). Brief 113 measures it; brief 115 reports it on every run.

**c. The fact that makes terminal ports possible on Palace.** A wave port's `V_wp[i]` in `port-V.csv` is
`WavePortData::GetVoltage(*E)` (`postoperator.cpp:1747` → `waveportoperator.cpp:1421`). It is the line integral of the
**total solved field** along that entry's `VoltagePath`, written **for every excitation**. So with one entry per terminal,
each with its path from the reference to its own conductor, Palace hands back the terminal-voltage matrix `V` (terminals ×
excitations) **at the face**. With the modal `S`, and unit-power incident modes:

```
V  = T_V · (1 + S_m)            ⇒   T_V = V · (1 + S_m)⁻¹        (the modal voltages, in Palace's own mode basis)
T_I = (T_Vᴴ)⁻¹                   (unit-power, power-orthogonal modes; brief 113 confirms the factor)
U  = T_V · (1 + S_m),   I = T_I · (1 − S_m)   →   rule 2's formula
```

**This needs no knowledge of which mixture of a degenerate pair Palace chose.** `T_V` is recovered in whatever basis
Palace used, as long as its two modes are independent and power-orthogonal. That condition is the risk brief 113 measures
(§1d).

**d. Degenerate modes are where it can go wrong.** In a homogeneous cross-section the two TEM modes share one k, so the
eigensolver may return any basis of that plane. Each `WavePort` entry runs **its own** port eigensolve. If entry 1's mode 1
and entry 2's mode 2 come from differently-configured solves, they need not be orthogonal, and the projection-based modal
S is then wrong. The mitigation is to give every entry on a face **the same `MaxSize` and `SolverType`**, so each solve is
the same Krylov process. The default `MaxSize` is `max(2·Mode, Mode+15)`, which differs between modes 1 and 2. Brief 113
checks whether that makes the bases agree, and brief 115 checks it on every run (reciprocity and passivity of the result).

**e. Mode ranking can pick an evanescent mode.** Palace ranks port modes by |Re kₙ − target| and ignores Im kₙ (upstream
issue #996, open, fix #1019 open). Brief 23's propagation test (|Im kₙ| ≤ 0.1·Re kₙ) already exists in `PalaceRun`. A
terminal port **requires** every one of its modes to be propagating, so for it a failure is an **error**, not a note.

**f. openEMS needs no modes at all.** A terminal is a voltage probe along its path and a current probe around its conductor,
at the reference plane, with a soft source behind it. That is exactly what our lumped port already is, moved onto a
uniform line, and `FdtdPortTransform.Solve` already turns N such ports into S. upstream's own `CoaxialPort` and `MSLPort`
are built this way, and the pinned openEMS 0.37.0-rc3 has every element needed. Its new HDF5 mode files
(`Excitation@WeightFile`, `ProbeBox@ModeFile`) are **not** needed and **not** used: they are mode-matching, real-valued, and
unvalidated upstream for co-located ports.

**g. Where openEMS's source goes.** V and I at a plane characterise everything on the device side of that plane,
whatever the source looks like. The only condition is that the source's evanescent near field has decayed there, or the
line integral of E stops being path-independent. So openEMS **extends the face's cross-section outward** by a feed length
(the box grows on that side, and every solid crossing the face is extruded through it, as solids reaching a PML already
are, `CsxcadWriter.cs` ~31). The source goes at the far end of that extension, and the probes go at the face plus
`Offset`, which is **the same reference plane Palace de-embeds to**. Brief 113 measures the feed length needed.

**h. A hollow waveguide has no terminal.** A port region met by one conductor (WR-90) has no voltage between conductors.
Palace's modal wave port handles it as today. openEMS would need a mode-matching port (analytic TE₁₀), which is **deferred**
(§4), so it stays refused, by name.

---

## 2. The briefs

| # | Title | Depends on | Delivers |
|---|---|---|---|
| 113 | Measure first: the terminal-port spike | — | Numbers that settle §1b–g on the pinned Palace 0.18.1 and openEMS 0.37.0-rc3; go/no-go for 115 and 116. No shipped code |
| 114 | The terminal wave port: document, problem, editor | 113 | `C3dPort.Terminals`, `Reference`; N-conductor inference; `Em3dPort.FaceGroup`/`Mode`; port rows, overlay, `check`, `explain`. Solvers refuse it by name until 115/116 |
| 115 | Palace: terminal S from modal S | 113, 114 | Shared-face `WavePort` entries; `port-V.csv`; the terminal transform; per-run quality checks |
| 116 | openEMS: wave ports by probes | 113 (114 for N > 1) | Feed extension, source, V/I probes; single and multi-terminal; lifts the Palace-only refusal for any port with two or more conductors |
| 117 | Example: 3D Wave Ports | 115, 116 | The connector with its back end open (coax wave port) and a coupled pair (two-terminal wave ports); both solvers; numbers quoted once |
| 118 | Example: 3D Eigenmode | — | Microstrip in a lidded cavity; the same cavity with a grounded pillar; eigenmode plus a driven sweep showing the notch move |

**118 needs no engine work and can be built first.** 116's single-terminal half (the coax) does not need 114 and can be
built right after 113.

### 2a. UI and user docs: where each lands

**Each brief documents its own capability**, in the same change that builds it, so a reference page never describes a
refusal that has already been lifted (today four pages say wave ports are "Palace only"). The two example briefs add only
the walk-throughs. Edit doc **sources** only (`docs/user/src/`). **No brief runs DocGen or regenerates `docs/user`**: the owner does that,
once, after the series. A brief's diff never touches a generated file.

| Brief | UI | User docs (sources) |
|---|---|---|
| 113 | — | — (findings go to `src/Design/RESOLVED.md`) |
| 114 | *Make Port ▸ Wave* makes terminals; port rows with terminal children; overlay arrows; *Set Reference…*; the menu tip | `drawing-in-3d.md` (#simulate's port paragraph, and the port entries in #reference); `em-setup.md` #wave-ports gains a *Several conductors on one face* subsection (the idea, the numbering, the file keys); `cli.md` (`explain`'s terminal lines) |
| 115 | Field-plot *Solution* choices labelled by mode (`Left mode 2`); the run's checks as Messages-panel rows | `em-setup.md` #wave-ports: how Palace does it, and each check and what to do about it; `em-3d.md` (the result's diagnostics keys, the *Solution* row's mode labels); `cli.md` (`em`'s report) |
| 116 | The port-kind tip and the Offset tip lose "Palace"; the 3D view's FDTD grid overlay draws the openEMS feed extension | `em-setup.md` #wave-ports (the "Palace only" sentence goes, and an *On openEMS* paragraph is added); `em-3d.md` (~129, "Palace only"); `em-solvers.md` (the capability table, ~169, and the prose around it) |
| 117 | — | `new-user-guide/index.md`: a new walk-through after §13; `em-setup.md` #wave-ports links it |
| 118 | — | `drawing-in-3d.md` #lid and `em-setup.md` #eigenmodes link it, with the post's explanation; `em-solvers.md` (~267) links it |

---

## 3. Decisions — open, each with the brief's recommended default

| # | Decision | Recommended default | Blocks |
|---|---|---|---|
| D1 | Terminal or modal S for a multi-conductor port | **Terminal** (one port per conductor, its own Z0), which is what the owner asked for. Modal S (even/odd) is not exported in this series | 115, 116 |
| D2 | Where a terminal port lives | **`.c3d` only.** A `.cem`'s ports come from layout edge ports, one conductor each. Single-terminal openEMS wave ports (116) work from both | 114 |
| D3 | File shape of a multi-terminal port | **A `Terminals` list** (`Number`, `Name`, `Conductor`, `Z0`, optional `VoltagePath`, `Flip`) plus a port-level **`Reference`**; the port-level `Number`/`Name`/`Z0`/`Positive`/`Negative`/`VoltagePath` are **not written** when `Terminals` is. A one-terminal port keeps today's spelling (rule 3). Alternative: terminal 1 stays in the port-level fields and `Terminals` holds 2…N, which touches less code but is asymmetric | 114 |
| D4 | Numbers for an inferred terminal | **Never invented by the reader.** A file whose wave port meets 3+ conductors and states no `Terminals` stays refused, with a sentence naming the fix. The editor's *Make Port ▸ Wave* writes the terminals with the next free numbers | 114 |
| D5 | Turning a terminal off (brief 93's `Model`) | **Per port, not per terminal.** A face's modes are solved together, so a half-modelled face is not a smaller problem | 114 |
| D6 | Palace: a terminal port's `Offset` | **Written as 0 to Palace**; circuitRF applies the shift itself, per mode, with each mode's kₙ, before the terminal transform (T_V is invariant along a uniform line). If brief 113 finds Palace does not print kₙ for an inactive entry, a multi-terminal port's `Offset` must be 0 and nonzero is refused | 115 |
| D7 | Palace: the Robin-mismatch warning threshold | **Warn when the predicted reflection of any non-active mode exceeds −30 dB**; brief 113 confirms the threshold against measurement. Never refuse: a microstrip pair is a legitimate thing to simulate, as long as the user is told | 115 |
| D8 | Palace: field plots of a terminal port's run | **Shown per solved mode**, labelled `P3 mode 2`. Superposing the steps into "terminal k driven" (via T_V) is deferred. openEMS runs drive terminals, so its plots are per terminal | 115 |
| D9 | openEMS: the face a wave port lies on | **Lowered absorbing (PML)** whatever the setup states, said in the run's notes when it states otherwise. openEMS has one boundary per face, and a source behind a PEC face has nowhere to go | 116 |
| D10 | openEMS: hollow waveguide (one conductor) | **Still refused**, naming Palace and the deferral (§1h) | 116 |
| D11 | The wave-port example's home | **A new workspace, `3D Wave Ports`**, self-contained (examples are copied whole by *Tools ▸ Examples*), with `Launch` (the connector, back end open) and `Pair`. `3D Connector` is left as it is | 117 |
| D12 | The pair's cross-section | **An edge-coupled stripline pair**: homogeneous (§1b error is zero), and Cohn's closed form gives even/odd Z₀ to check against. If brief 113 finds degenerate modes unusable on Palace, a microstrip pair, with the warning, is the fallback | 117 |
| D13 | Mixed-mode (Sdd/Scc) | **Deferred.** RfCore has no mixed-mode conversion today; the example plots terminal S (thru, near-end and far-end coupling) | 117 |

---

## 4. What is deferred, and why

- **openEMS hollow-waveguide ports** (TE₁₀ mode-matching, ProbeBox types 10/11). Upstream has the elements, but it is a
  different port (modal, no terminal), and nothing in this series needs it.
- **Mixed-mode S-parameters** in RfCore (D13). It is an RfCore feature on its own, useful far beyond 3D EM.
- **Palace field plots as terminal drives** (D8).
- **Terminal ports from a `.cem`** (D2): it needs layout ports that name several edges as one port.
- **openEMS numeric mode files** (§1f): no need while V/I probes give terminal S exactly.

## 5. Owner walk-through, at the end of the series

Open **3D Wave Ports ▸ Launch**, run *Palace* and *openEMS*, and compare |S11| with `3D Connector`'s lumped-port result:
the gap capacitance the lumped port carried is gone. Open **Pair**, look at its two wave ports with two numbered terminals
each, run both setups, and plot S21 (thru), S31 (near-end) and S41 (far-end) from both solvers on one Data Display. Open
**3D Eigenmode**, run *Modes* on both cells, and see the first mode move from inside the line's band to above it.
