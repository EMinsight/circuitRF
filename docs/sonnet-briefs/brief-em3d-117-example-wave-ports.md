# Brief 117 — example: 3D Wave Ports

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d117-n` ·
**Precedent:** [brief 70](brief-em3d-70-showcase.md) (`3D Connector`: one numbers file quoted by the README and the user
page, a test that holds them together, a Benchmark-tier re-run of the shipped setups)
**Area:** `examples/3D Wave Ports/` (new), `examples/examples.json`, `docs/user/src/new-user-guide/index.md`,
`docs/user/src/reference/em-setup.md`, `docs/user/src/reference/em-solvers.md` (links only), `tests/Ui.Tests/Examples/`
**Depends on:** 115, 116

---

## 0. What this brief delivers

The example the owner asked for: it teaches a newcomer **how to set up and run a wave port on Palace and on openEMS**,
including the case where **two conductors end on one face** and each becomes its own port. It is a new workspace
(overview D11), self-contained because *Tools ▸ Examples* copies a workspace whole.

Edit doc **sources** only. Do not run DocGen or regenerate `docs/user`: the owner does that.

---

## 1. `R-em3d117-1` — the workspace

| Cell | View | What it is |
|---|---|---|
| **Board** | layout, mil | Copied unchanged from `3D Connector` |
| **Flange** | 3D view, mm | Copied unchanged from `3D Connector`, with its `flange.step` |
| **Launch** | 3D view, mm | `3D Connector`'s launch with **its back end open**: the bore and the pin run to the air box's xmin face, and P1 is a **wave port** on that face (pin and housing; one terminal, inferred). P2 stays the board's lumped port, so the cell also shows that the two kinds mix. Setups *Palace* and *openEMS* |
| **Pair** | 3D view, mm | An **edge-coupled stripline pair** (overview D12; brief 113 may have switched it to a microstrip pair): two thin strips between two ground planes joined by side walls, 15 mm long, PTFE filled. A **two-terminal wave port at each end**, so a 4-port result. Setups *Palace* and *openEMS* |

One technology serves all four. Copy `board-and-connector.ctech` and add nothing unless Pair needs a material it lacks.

**The Launch change, precisely.** In `3D Connector` the bore has a floor and P1 bridges a 0.3 mm gap between that floor and
the pin's end (`examples/RESOLVED.md`, brief-em3d-70: "the coax end is a gap port"). Here:

- the housing's bore is cut through the rear face;
- the pin runs to that face;
- the air box's xmin padding is 0, as it already is, so the face is the box face.

This is the comparison the example exists to make. The lumped gap port carried a small capacitance that was in every
`3D Connector` run, and the wave port does not. The README quotes both |S11| curves.

**Pair's dimensions** are stated once as VARs:

- strip width W, gap S, ground spacing b, thickness t (thin, t/b ≤ 0.02, so the closed form applies);
- chosen so Z₀ odd and even straddle 50 Ω (for example Z₀e ≈ 60 Ω, Z₀o ≈ 40 Ω), which gives clearly visible coupling;
- Cohn's Z₀e and Z₀o are computed in the README from the VARs.

Numbering: the left face's terminals are 1 and 2 and the right face's are 3 and 4, with 1–3 and 2–4 on the same strip. That
way **S21 is near-end coupling, S31 is thru and S41 is far-end coupling**, and the README says so in one sentence, because a
4-port's numbering is where readers get lost.

## 2. `R-em3d117-2` — the setups

Each cell has *Palace* and *openEMS* setups over the same band (2–18 GHz, as the connector).

- **Palace**: the Draft preset the connector uses, unless brief 115's checks warn at Draft. If they do, raise only the
  setting needed.
- **openEMS**: the connector's grid settings. The feed extension (116) is automatic, and the README shows the run note that
  reports it.
- Pair's Palace run reports brief 115's checks; the README quotes the reciprocity figure and says why the mismatch warning
  is absent (homogeneous fill, overview §1b).

Any setting lowered for speed is stated in the README with its cost and what it trades.

## 3. `R-em3d117-3` — the numbers, measured once, quoted everywhere

`examples/3D Wave Ports/expected-numbers.json`, in `3D Connector`'s schema:

- the closed forms (coax Z₀ 50.06 Ω; Cohn's Z₀e/Z₀o for Pair);
- the coax impedance Palace reports at the wave port (`Z_PV`), which `3D Connector` could not quote (brief 70's second
  external check, finally possible);
- every run's wall clock and memory;
- the recorded |S| of every shipped run.

The README and the user pages quote these strings, and the test holds them together.

## 4. `R-em3d117-4` — the README: how to set it up, step by step

The README is the tutorial. In order:

1. **What a wave port is**, in two sentences: the face of the box becomes a matched continuation of the line, so no gap and
   no parasitic.
2. **Launch, by hand**:
   - right-click the xmin face region over the coax → *Make Port ▸ Wave*;
   - read the overlay (the voltage-path arrow from the housing to the pin);
   - run *Palace*, run *openEMS*;
   - compare with `3D Connector`'s lumped result. Quote both |S11| figures at 18 GHz.
3. **Pair, by hand**:
   - *Make Port ▸ Wave* on each end face → two numbered arrows per face, and the reference named in the status line;
   - what "terminal S" means (each strip is a port, voltage from the ground to the strip);
   - the numbering sentence from §1;
   - run both; open the Data Display with S31, S21 and S41 from both solvers.
4. **What each solver did**, honestly:
   - Palace solved two modes on one face and circuitRF converted them to terminals (brief 115's report lines);
   - openEMS fed each strip from behind and measured voltage and current (brief 116's note).
5. **Numbers**: the table from §3.
6. **What is deliberately not here**: mixed-mode (D13), a hollow waveguide on openEMS (D10).

The reference chapter is **EM Setup ▸ Wave ports** (`em-setup.md` #wave-ports and #wave-port-terminals), which briefs
114–116 have already brought up to date. The README links it, and does not repeat it.

## 5. `R-em3d117-5` — the user pages (sources only; overview §2a)

The capability itself is documented by 114–116. This brief adds only the walk-through and the links. Edit
`docs/user/src/` only, and do **not** run DocGen or regenerate `docs/user`: the owner does that.

- `new-user-guide/index.md`: a new section after §13, **14 · Wave ports: one port per conductor** {#wave-ports-3d}, in
  §13's style ("ten minutes, nothing to draw"):
  - open the example;
  - run *Launch* on both solvers and compare with §13's lumped result;
  - open *Pair*, see the terminals, run, and plot thru and coupling;
  - one paragraph on what each solver did.

  It quotes the headline numbers from `expected-numbers.json`.
- `em-setup.md` #wave-ports and #wave-port-terminals: one line each, linking the walk-through and the example.
- `em-solvers.md`: where the capability table (116) says openEMS builds wave ports, a link to the example.

## 6. Gate

`tests/Ui.Tests/Examples/` (the existing index test covers the folder and the `examples.json` row both ways):

1. Every `.c3d` opens, elaborates and passes `check` with 0 errors; `explain` lists Pair's four terminals.
2. Each quoted number's Readme text appears in both the README and the new-user-guide walk-through (brief 70's mechanism).
3. **The closed forms are computed in the test** from the VARs: coax Z₀ and Cohn's Z₀e/Z₀o match `expected-numbers.json`.
4. *(Benchmark)* The four shipped runs re-run and hold every recorded |S| to the file's `RegressionTolerance`, as
   `3D Connector`'s gate 6 does.

## 7. Owner check

The overview's §5 walk-through, the first two paragraphs.

## 8. Scope

- No new engine feature. Anything the example needs that 114–116 did not deliver is reported, not patched in here.
- `3D Connector` is untouched.
