# Brief 115 — Palace: terminal S from modal S

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d115-n`
**Area:** `src/Design/Em3d/PalaceConfigWriter.cs` (the `WavePort` array, ~256–284), `src/Design/Em3d/PalaceRun.cs`
(`ReadPortS`, `ReadPortZ`, a new `ReadPortV`, `ParseWaveMode`, `SecondModes`), `src/Engine/Em3d/` (a new
`TerminalPortTransform`, and `FdtdPortTransform`'s wave formula exposed for it), `src/Design/Em3d/Em3dRunService.cs`
(`RenormaliseWavePorts` ~873, the refusal from 114), `src/Render/Scene3D/Fields/FieldDrive.cs` (the mode label, D8),
`src/Cli/` (`em`'s report, `explain`), `src/Ui/ThreeD/C3dPropertiesViewModel.FieldPlot.cs`, `docs/user/src/reference/` (`em-setup.md`, `em-3d.md`, `cli.md`)
**Depends on:** 113 (go), 114 · **Blocks:** 117

---

## 0. What this brief delivers

A multi-terminal wave port runs on Palace. The `.sNp` has one port per terminal, each referred to its own Z0. Every run
also checks its own result (reciprocity, passivity, modes propagating, absorption mismatch) and reports what it found.

The method is the overview's §1c. Read §1b–e there before starting, and brief 113's findings in `src/Design/RESOLVED.md`,
which may have changed a default below.

---

## 1. `R-em3d115-1` — what Palace is given

For each `FaceGroup` of N terminals, N `WavePort` entries:

| Key | Value |
|---|---|
| `Index` | the terminal's port number (as today: the result's number) |
| `Attributes` | the group's one face attribute, **the same on every entry** |
| `Mode` | the terminal's `Mode` (1…N), replacing today's hard-coded `1` (~270) |
| `Active` | written **only** as `false`, on modes 2…N (Palace's default is true; mode 1 keeps today's bytes) |
| `Excitation` | the entry's own index, as today |
| `VoltagePath` | from the reference to this terminal's conductor |
| `Offset` | **0** (overview D6); circuitRF applies the shift |
| `MaxSize` | the same value on every entry of a group when brief 113 found it necessary (overview §1d); otherwise not written |

A one-terminal port writes exactly today's entry (rule 3, gate 7).

**`R-em3d115-1b`** Palace orders entries sharing a face by `Index`, and the active one must have the lowest
(`waveportoperator.cpp:1548`). So the terminal with mode 1 must have the group's **lowest port number**. 114's editor already
numbers in mode order. A hand-written file that breaks this is renumbered **in mode assignment only**: the lowest-numbered
terminal takes mode 1. Port numbers are never changed, and `explain` says which terminal carries which mode.

## 2. `R-em3d115-2` — what is read back

- `port-S.csv` as today, which is the **modal** S for a group's ports.
- **`port-V.csv`** (new reader `PalaceRun.ReadPortV`, columns found **by name**: `Re{V_wp[i]…} (V)` / `Im{…}` per excitation,
  `postoperatorcsv.cpp` ~1072–1096; the header is committed from brief 113's fixture). These are the total-field terminal
  voltages at the face.
- Each mode's kₙ from the log (`ParseWaveMode`), for the shift and the checks. If brief 113 found no line for inactive
  entries, D6's fallback applies: a nonzero `Offset` on a multi-terminal port is refused before Gmsh.
- `port-Z.csv` as today, for the one-terminal ports.

## 3. `R-em3d115-3` — the transform (numeric layer)

**`R-em3d115-3a`** A new pure class, `src/Engine/Em3d/TerminalPortTransform.cs` (arrays in, arrays out, no file):

1. Per frequency and per group: T_V = V·(1 + S_m)⁻¹ and T_I = (T_Vᴴ)⁻¹ × the factor brief 113 measured.
2. Shift S_m to the reference plane per mode with each mode's kₙ (D6), using the convention Palace's own `Offset` would
   have used (`S̃ij = Sij·e^{ik_i d}·e^{ik_j d}`, a positive d moving the plane toward the device).
3. Assemble the whole problem's U and I, block-diagonal over ports:
   - a group contributes T_V and T_I;
   - a one-terminal wave port contributes √Z_PV and 1/√Z_PV;
   - a lumped port contributes √R and 1/√R (Palace's lumped S is referred to its R).

   Then U = T_V·(1 + S), I = T_I·(1 − S).
4. **S = √y·(U − Z₀·I)·(U + Z₀·I)⁻¹·√z**, by calling the formula `FdtdPortTransform` already uses (exposed as one shared
   function, not copied). This is overview rule 2.

**`R-em3d115-3b`** A run with **no** group keeps today's path (`RenormaliseWavePorts`) unchanged. Gate 4 holds the new
path to the old one on a one-terminal problem anyway, so the two definitions provably agree.

## 4. `R-em3d115-4` — every run checks itself

Reported in `em`'s output, in the `.sNp` provenance header, and in the run's notes. Thresholds come from brief 113's data:

| Check | Outcome |
|---|---|
| Any mode of a group not propagating (|Im kₙ| > 0.1·Re kₙ, brief 23's test) at any frequency | **Error**: "Port 'Left' mode 2 is evanescent at 14 GHz (kₙ = …); its terminal S would be wrong. Palace may have ranked an evanescent mode above a propagating one…" (overview §1e). No `.sNp` is written |
| cond(1 + S_m) or cond(T_V) above the threshold | **Error**, naming the port and frequency and saying the face's modes are not independent (overview §1d) |
| Predicted reflection of a non-active mode, |(k₁ − k_m)/(k₁ + k_m)|, above −30 dB (D7) | **Warning**, with the dB figure and "the face absorbs mode m imperfectly; terminal S carries an error of about this size" |
| Reciprocity ‖S − Sᵀ‖/‖S‖ | Always **reported**; a **warning** above the threshold |
| Passivity: largest singular value of S > 1 + ε | **Warning**, with the frequency |

## 5. `R-em3d115-5` — results and fields

- The `.sNp` header line for a group: `ports 1-2: terminal wave port 'Left', terminal S from Palace's modal S and the
  terminal voltages at the face`.
- The diagnostics `.npy` adds, per group: the modal S (`<key>.palace_modal_S`), T_V, each mode's kₙ, and the check
  figures, so a disputed result can be re-derived without re-running.
- **Field plots (D8):** a Palace step of a group is labelled by its mode, `Left mode 2`, not by a terminal, and
  `FieldDrive` refers its power to that mode's unit incident power (which is what Palace solved). Terminal-drive
  superposition is deferred.

## 5a. `R-em3d115-6` — UI

- **Field-plot *Solution* choices** (`C3dPropertiesViewModel.FieldPlot.cs`, the labels from `FieldPlotResolver`): a group's
  steps read `Left mode 1`, `Left mode 2`. A one-terminal port's step keeps today's label.
- **The run's checks** reach the Messages panel through the run's existing notes and warnings, one row per check (§4), each
  naming the port and the frequency. Nothing new is drawn. The point is that a warning is seen where every other run
  warning is seen, not only in the `.sNp` header.

## 5b. `R-em3d115-7` — user docs (sources only; overview §2a)

Edit `docs/user/src/` only. Do **not** run DocGen or regenerate `docs/user`; the owner does that.

- `em-setup.md` **#wave-port-terminals** (114's subsection) gains *On Palace*, in plain terms:
  - Palace solves the face's modes and circuitRF turns them into one port per conductor, so the result is still terminal
    S at each terminal's Z0;
  - each check in §4, what it means and what to do about it. For the absorption warning: it is expected on a microstrip
    pair, it is absent in a uniform fill, and the dB figure is the size of the error. For an evanescent mode: shrink the
    region or lower the sweep's top;
  - field plots are per mode, not per terminal (D8), with one sentence on why.
- `em-3d.md`: the results table gains the diagnostics keys (§5), and the field-plot *Solution* row (~331–350) gains the
  mode labels.
- `cli.md`: `em`'s report lines for a group.

## 6. Gate

`tests/Ui.Tests/Em3d/PalaceTerminalPortTests.cs` and `tests/Engine.Tests/Em3d/TerminalPortTransformTests.cs`.

1. **Transform, closed form (no solver).** Synthesise modal data for an ideal symmetric coupled section (even/odd Z₀ and β,
   the modal basis rotated by an arbitrary angle, since Palace's basis is arbitrary when modes are degenerate). The
   transform returns the ideal terminal 4-port to 1e-12 **whatever the rotation**.
2. **Replay, stripline pair.** Brief 113's committed `port-S/V/Z.csv` and log for geometry A, read through the real readers
   and transform. Terminal S against Cohn's closed form within the tolerance brief 113 measured (target 0.05 dB / 0.5°).
3. **Replay, microstrip pair.** Geometry B: the transform's result matches brief 113's hand calculation to 1e-9, and the
   mismatch warning fires with the figure 113 recorded.
4. **One terminal, two paths.** Brief 23's microstrip wave-port fixture through the new transform equals
   `RenormaliseWavePorts`' result to 1e-6.
5. **Config.** A two-terminal group writes two entries with one attribute, `Mode` 1/2, `Active: false` on the second only,
   `Offset` 0, and equal `MaxSize` if required. Golden committed.
6. **Checks.** Each of 4's outcomes, from doctored fixtures: an evanescent mode-2 kₙ fails the run with no `.sNp`; a
   singular T_V fails; an asymmetric S warns.
7. **Byte identity.** Every existing Palace golden and every one-terminal wave-port config is unchanged.
8. **Real run** *(Palace; Benchmark if > 5 s)*. The stripline pair end to end through `circuitrf em`, within gate 2's
   tolerance.

## 7. Owner check

- Run the pair on Palace, read the checks in the report, and plot S21/S31/S41.

## 8. Scope

- Palace only. No mixed-mode export (D13). No terminal-drive field plots (D8).
