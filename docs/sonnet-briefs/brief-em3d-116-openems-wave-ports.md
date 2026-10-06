# Brief 116 — openEMS: wave ports by probes

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d116-n`
**Area:** `src/Design/Em3d/CsxcadWriter.cs` (ports ~255–281, the PML continuation ~31, the head ~501), the FDTD grid
(`FdtdGrid*`, fixed lines at the reference plane and the feed's end), `src/Design/Em3d/OpenEmsRun.cs` (probe pairs, unchanged
in shape), `src/Design/Em3d/Em3dRunService.cs` (`PalaceOnlyRefusal` ~105–122), `src/Engine/Em3d/FdtdPortTransform.cs`
(unchanged, gate 6), `src/Cli/` (`explain`), `src/Ui/` (two tips, the FDTD grid overlay), `docs/user/src/reference/` (`em-setup.md`, `em-3d.md`, `em-solvers.md`)
**Depends on:** 113 (go). The single-terminal half needs nothing else. The multi-terminal half needs 114 · **Blocks:** 117

---

## 0. What this brief delivers

A wave port on openEMS, from a `.c3d` or a `.cem`, for any port region met by **two or more conductors**: a coax, a
microstrip, a stripline, and (with 114) a coupled pair with N terminals. The result is **terminal** S referred to each
port's own Z0, computed by the same `FdtdPortTransform.Solve` every openEMS lumped port already uses.

Today's refusal (`Em3dRunService.PalaceOnlyRefusal`: "only Palace builds wave ports in this version") is lifted for those
ports. It stays, reworded, for a hollow waveguide (overview D10).

How it works is the overview's §1f–g. **No mode is computed**, by circuitRF or by openEMS: a terminal is a voltage probe
and a current probe on a uniform line, fed from behind.

---

## 1. `R-em3d116-1` — the feed extension

**`R-em3d116-1a`** For each air-box face carrying a wave port, the lowering grows the box outward on that side by the
**feed length** L that brief 113 measured (its rule: N cells or a multiple of the port region's largest transverse size,
whichever brief 113 found holds). Every solid that **crosses the face**, conductor or dielectric, inside the port's
rectangle or not, is extruded through the extension. That is the same treatment `CsxcadWriter` already gives a solid
reaching an absorbing face (~31), extended by L before the PML begins. The extension is therefore a uniform continuation of
the face's own cross-section, which is what a wave port means.

**`R-em3d116-1b`** The face is lowered **absorbing (PML)** (overview D9). When the setup stated another kind for it, the
run's notes say so: "The xmin face carries wave port 'P1', so openEMS terminates it in PML behind a 3.2 mm feed; the
setup's PEC applies to Palace only."

**`R-em3d116-1c`** The grid gets fixed lines at the reference plane (face + `Offset`) and at the source plane (the
extension's outer end, inside the PML's inner edge), so probes and source sit on lines, as the lumped port's do.

**`R-em3d116-1d`** Two wave ports on one face are allowed (two coax pins, say). Their regions must not overlap, as today.
One extension serves the face.

## 2. `R-em3d116-2` — a terminal's elements

For each terminal (a one-terminal wave port is one terminal):

- **Source** (in that terminal's run file only, as the lumped excitation is today): `Excitation Type="0"` (soft E), along
  the terminal's voltage path, lying in the source plane, from the reference to the conductor. The polarity convention is
  the lumped port's (`ExcitationProperty` ~567).
- **Voltage probe** `port<k>_u`: `ProbeBox Type="0"`, weight −1, along the voltage path at the reference plane. That is
  the lumped port's probe, moved.
- **Current probe** `port<k>_i`: `ProbeBox Type="1"`, normal to the face, at the reference plane, its box the terminal
  conductor's cross-section there grown by one cell on every side. The sign is chosen so I flows **into** the device.
- A box that would touch or enclose another conductor is refused before openEMS runs. The sentence names both conductors
  and says what brief 113 found happens (for example, a coax pin whose shield bore is one cell larger than the pin).

The probe names are today's, so `OpenEmsRun`'s pairing and `FdtdPortTransform.Solve` take them unchanged (gate 6).

## 3. `R-em3d116-3` — refusals and reports

- **Hollow waveguide** (the region meets one conductor): "Port 3 is a wave port met by one conductor (a hollow waveguide).
  openEMS needs a mode-matching port for that, which circuitRF does not build yet; run it on Palace." The remedy names the
  setup field and `--solver palace`, as today.
- **Feed too thin**: a reference plane closer to the source than brief 113's minimum (only possible if a later edit makes L
  configurable) is refused, naming both distances.
- `explain` prints, per wave port on openEMS: the feed length, the source plane, the reference plane, and each terminal's
  probe boxes, in the display unit.
- Eigenmode stays Palace-only (FDTD has no eigensolver); that refusal is unchanged.

## 4. `R-em3d116-4` — Both

A `Both` setup with wave ports now runs on both solvers. Their results are directly comparable because both are referred to
**the same reference plane** (face + `Offset`) and **the same Z0** (overview rule 2): Palace through brief 23's
renormalisation or brief 115's transform, openEMS through probes.

## 4a. `R-em3d116-5` — UI

- **Text that says "Palace" goes:**
  - `EmSetupEditorViewModel.Port3DKindTip` (`EmSetupEditorViewModel.Eigen.cs` ~92, "…and only Palace builds one") says
    both solvers build one, except that openEMS cannot feed a hollow waveguide;
  - the `.cem` port table's Offset tip (`EmSetupEditorView.axaml`, "Palace de-embeds the line between") says the
    reference plane is where both solvers measure.

  Grep `src/Ui` for any other wave-port text naming one solver.
- **The feed extension is visible.** The 3D view's FDTD grid overlay, shown for an openEMS setup, draws the extension
  beyond the air-box face (the grid continues through it) and marks the source plane. The source then has a visible place,
  and the grown box is not a surprise in the cell count. The air box itself is drawn as today, because the extension is
  the lowering's and not the document's.

## 4b. `R-em3d116-6` — user docs (sources only; overview §2a)

Edit `docs/user/src/` only. Do **not** run DocGen or regenerate `docs/user`; the owner does that.

- `em-setup.md` **#wave-ports**:
  - the sentence "Palace only; a setup naming openEMS or both solvers is refused, and so is its `check`" is replaced by
    *On openEMS*, which says: the line is fed from a short extension behind the face; the face is absorbing for openEMS
    whatever the setup says (D9); voltage and current are measured at the reference plane, so the result is referred to
    Z0 directly with no renormalisation; a hollow waveguide is refused (D10);
  - `OffsetUm`'s bullet says both solvers use the same plane.

  **#wave-port-terminals** gains its own *On openEMS* sentence (each terminal is fed and measured on its own).
- `em-3d.md` ~129: "A **wave port** (`Ports3D`, Palace only)" loses "Palace only" and gains the hollow-waveguide exception.
- `em-solvers.md`: the capability table (~169), where openEMS's column reads "lumped and wave ports"; and wherever the
  page contrasts the two solvers on ports, one sentence on how each builds a wave port (a port mode on the face, against a
  fed and probed line).
- `drawing-in-3d.md`: any wave-port sentence naming Palace alone.

## 5. Gate

`tests/Ui.Tests/Em3d/OpenEmsWavePortTests.cs`.

1. **Lowering, no solver.** A coax wave port's XML: the box grown by L on its face, the pin, PTFE and shield extruded
   through it, PML on that face, a source on the voltage path at the source plane in run 1 only, `port1_u` and `port1_i` at
   the reference plane, the current box enclosing the pin and clear of the shield. Golden committed.
2. **Replay, coax** (brief 113's probe files through `OpenEmsRun`'s readers and `FdtdPortTransform`): Z = U/I against
   50.06 Ω within 0.5 Ω; ∠S21 = −βℓ within 1°.
3. **Replay, stripline pair**: terminal S against Cohn's closed form, and against brief 115's Palace replay of the same
   geometry, within the tolerance brief 113 recorded (target 0.1 dB / 1°).
4. **Refusals**: hollow waveguide on openEMS; a current box touching a second conductor. Both before openEMS starts.
5. **Byte identity**: every existing openEMS golden (`testdata/em3d/openems-goldens`) with lumped ports is unchanged.
6. **`FdtdPortTransform` untouched**: its file has no diff (a source scan, as other gates in this repo do).
7. **Real run** *(openEMS; Benchmark if > 5 s)*: the coax, end to end through `circuitrf em`, within gate 2's tolerance.

## 6. Owner check

- Run the 3D Connector's launch with a wave port on the coax end on openEMS (the example itself is brief 117's), and
  read the feed note in the run report.

## 7. Scope

- No mode-matching port, no HDF5 mode files (overview §1f). No hollow waveguide on openEMS (D10).
- No change to openEMS lumped ports.
