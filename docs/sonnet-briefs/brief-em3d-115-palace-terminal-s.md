# Brief 115 — Palace: terminal ports by mirror symmetry

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d115-n`
**Rewritten 2026-10-06** from [brief 119](brief-em3d-119-palace-magnetic-wall-spike.md)'s go (route B). The earlier
text, terminal S from several modes on one shared face, was a no-go in brief 113-a and is gone.
**Area:** `src/Engine/Em3d/` (a new `Em3dMirror`: find the plane, check the problem, build the half; a new
`MirrorPortTransform`: even and odd S → terminal S), `src/Engine/Em3d/Em3dProblem.cs` (`Validate`, a clip flag),
`src/Design/Em3d/GmshGeoWriter.cs` (clip to the air box), `src/Design/Em3d/PalaceConfigWriter.cs` (unchanged keys, a cut
face listed under `PMC` or `PEC`), `src/Design/Em3d/Em3dRunService.cs` (`TerminalPortRefusal`, `ExecutePalace`: two
solves on one mesh), `src/Cli/` (`em`'s report, `explain`), the field-plot step labels, `docs/user/src/reference/`
(`em-setup.md`, `em-3d.md`, `cli.md`)
**Depends on:** 114, 119 · **Blocks:** the example brief this brief asks for (§9)

---

## 0. What this brief delivers

A two-terminal wave port runs on Palace when the problem is a mirror image of itself across a plane between its two
terminals. circuitRF cuts the problem at that plane and solves the half twice on one mesh: once with the cut face `PMC`
(the even mode) and once with it `PEC` (the odd mode). It then combines the two into terminal S, one port per terminal,
each at its own Z0, in the same `.sNp` an openEMS run writes. Anything else is still refused, by name, pointing to
openEMS.

Read first: `src/Design/RESOLVED.md` § "Palace terminal ports by symmetry — brief-em3d-119", the whole section. Its
harness is `tools/palace-symmetry-spike/`, and its fixtures are `testdata/em3d/terminal/palace-symmetry/`. The numbers
this brief's gates hold to come from there.

**Why this route and not another (119, measured):** every quantity it reads is documented Palace, with no touching
ports and no multi-mode face. On geometry A it met 0.05 dB / 0.5° with max |ΔS| 0.0012 and σ within 0.0001 of 1, at
about 100 s a half. The PMC-strip route (A) costs twice as much and hides a wrong half from σ. Do not build it.

---

## 1. `R-em3d115-1` — recognising a symmetric problem (numeric layer, pure)

`Em3dMirror.Find(Em3dProblem)` returns the plane, or the reason there is none, as a sentence naming what is wrong. It
runs only when the problem has a terminal group (`Em3dPort.FaceGroup`) and the solver is Palace.

**`R-em3d115-1a` The plane.** From the first group: the two terminals' conductors, their footprints on the port face,
and the in-face axis along which their centres differ. The plane is normal to that axis, through the midpoint. It must
be normal to a world axis (x, y or z), because the air box is cut along it. Refuse: a group with other than two
terminals (*"Port 'xmin' has three terminals; Palace runs terminal ports by mirror symmetry, which takes two"*), and a
pair separated along both in-face axes.

**`R-em3d115-1b` The check.** Everything the solve sees must map onto itself, or onto something equal, under the
mirror, to a tolerance of one DBU (or 1e-9 m for a problem with no DBU):
- the air box: its extent, and the kinds of the two faces the plane separates (`YMin` ↔ `YMax`, and so on); the faces
  the plane crosses are unchanged by it;
- every solid: its mirror is itself or another solid with the same material and role. Mirror `Em3dBox`,
  `Em3dExtrudedPolygon`, `Em3dCylinder`, `Em3dSphere`, `Em3dTruncatedSphere` and `Em3dSweep` by value. An
  `Em3dShapeSolid` is checked through its `Operands` when it has them. **A kernel solid with no operands is refused by
  name**, because nothing short of the kernel can prove its mirror. Say so in the sentence. Do not approximate it with a
  bounding box;
- every sheet, mesh region and material assignment, the same way; precedence (`Em3dPrecedence`) of mirror partners
  equal;
- every port is a two-terminal group whose terminals mirror each other: rectangles (the group's one rectangle maps onto
  itself), conductors, **equal Z0**, and voltage paths that are mirror images (so a `Flip` on one terminal and not the
  other is refused, naming it). **A one-terminal port, or a lumped port, in the same problem is refused** (*"… port 'P5'
  has no mirror partner…"*). Two one-terminal ports that are each other's mirror would work the same way. That is not
  in this brief; refuse it and note it in `RESOLVED.md`;
- every group's two terminals lie on opposite sides of the SAME plane.

Report the FIRST thing that fails, by its document name. 119 measured why a "nearly symmetric" warning is the wrong
answer: a 1.3 % width difference already costs 0.05 dB, and nothing in the result can detect it (σ stays 1.000).

**`R-em3d115-1c` The half.** `Em3dMirror.Half(problem, plane, Em3dBoundaryKind cut)` returns an ordinary problem:
- the air box ends at the plane, and its face there is `cut` (`Pmc` or `Pec`). The kept side is the side holding the
  lowest-numbered terminal of the lowest-numbered group (in the 3D Wave Ports example, P1's side);
- solids and sheets are unchanged, and the problem carries a new `ClipToAirBox` (init-only, default false). `Validate`'s
  "extends outside the air box" checks are skipped for what the clip removes, and only when the flag is set. The flag
  is set nowhere else;
- each group becomes ONE ordinary wave port (`FaceGroup` null): its rectangle clipped to the half, the kept terminal's
  conductor, voltage path and Z0. Ports are numbered 1…G in group order, and the kept terminal's document number goes
  in `SourceNumber` / `SourceLabel` (brief 93's renumbering), so the port map reads correctly.

## 2. `R-em3d115-2` — lowering and running

**`R-em3d115-2a` Gmsh.** With `ClipToAirBox`, `GmshGeoWriter` intersects every solid and every sheet with the air box
(`BooleanIntersection`, the tool kept) before the background difference and the fragments. Conductors are clipped
before their `Recursive Delete`. Without the flag the script is **byte for byte** today's (gate 6). The `R-em3d3-6b`
comment ("clips nothing") gains its one exception.

**`R-em3d115-2b` Palace.** The two halves differ only in the cut face's kind, so `PalaceConfigWriter` lists that face's
attribute under `PMC` in one config and `PEC` in the other, and nothing else differs between them. **Gmsh runs once.**
Palace runs twice on that mesh, in two run directories (e.g. `even/` and `odd/` under today's Palace run directory, or
whatever `PalaceRun.Mesh`'s reuse makes simplest). One run's progress reads as two stages, and cancelling either cancels
the run.

**`R-em3d115-2c` Each half is read exactly as today** (`ReadPortS`, `ReadPortZ`, `RenormaliseWavePorts` to the kept
terminal's Z0, the evanescence and second-mode checks per half). Nothing about a one-terminal wave port changes.

**`R-em3d115-2d` Refusals.** `TerminalPortRefusal` keeps what it can judge from the document (a group with other than
two terminals on Palace). The symmetry check runs in the Palace lowering, before Gmsh, and its sentence is the refusal.
Every refusal still names openEMS as the solver that builds any terminal port. On `Both`, a symmetric problem runs both
solvers. An asymmetric one runs openEMS alone, with today's skip note carrying the symmetry sentence as its reason.

## 3. `R-em3d115-3` — the combination (numeric layer, pure)

`MirrorPortTransform.Combine(Se, So, groups)`: arrays in, arrays out, no file. `Se` and `So` are the two halves' G × G
S matrices at the terminals' Z0 (already renormalised, §2c). For groups a and b with kept terminals kₐ and mirrored
terminals mₐ:

```
S[kₐ,k_b] = S[mₐ,m_b] = (Se[a,b] + So[a,b]) / 2
S[kₐ,m_b] = S[mₐ,k_b] = (Se[a,b] − So[a,b]) / 2
```

Then reorder to document port numbers. This is brief 119's §3 for G groups; `tools/palace-symmetry-spike/ana.py`'s
`combine` is the two-group case. Renormalising each half before combining is exact, because the two terminals of a
group share Z0 (§1b).

## 4. `R-em3d115-4` — what every run checks and reports

In `em`'s output, the `.sNp` provenance header and the run's notes (the Messages panel already shows these):

| Check | Outcome |
|---|---|
| The plane | **Note**, always: *"Terminal ports by mirror symmetry about y = 0 mm: two half-model solves (PMC, then PEC) on one mesh; terminals P1, P3 solved, P2, P4 their mirror images."* |
| Power balance of each half, max over columns and frequencies of 1 − Σᵢ\|Sᵢⱼ\|² | **Reported** per half. On a problem with no loss mechanism (every conductor perfect, every dielectric lossless, no absorbing face) a half above **0.002** is a **warning**. With an absorbing face, the even half more than **0.01** below the odd half at any frequency is a **warning**: *"The even half loses N % more power than the odd half at f GHz. The even mode spreads furthest from the line and is reaching the absorbing faces (…), which load it. Move them further out, or make them PEC or PMC if the structure is shielded."* (119: 5.5–11 % against 0.1–0.7 %; with PEC walls ≤ 0.13 % on both) |
| σ_max and σ_min of the terminal S | **Reported**; σ_max > 1 + 0.002 is a **warning** (passivity) |
| Reciprocity ‖S − Sᵀ‖/‖S‖ | **Reported** |

The diagnostics `.npy` adds, per run: each half's S at its own Z_PV and its Z_PV (`<key>.palace_even_S`,
`…_even_Zpv`, and the odd pair), so a disputed result can be re-derived without re-running.

## 5. `R-em3d115-5` — explain, fields, UI

- **`explain`** on a `.c3d` with a Palace setup and a terminal port prints the plane and the kept terminals (the §4
  note), or the refusal sentence. `check` does not build a `.c3d`'s problem today (114's note); leave that as it is.
- **Field plots (D8):** a step of such a run is a half and a mode: *Solution* reads `xmin even` / `xmin odd`, and the
  plot draws the half that was solved. If the field-plot path cannot read a second run directory without rework,
  refuse a field plot on such a run with a sentence saying why, and record it in `RESOLVED.md`. **No mirrored display,
  no terminal-drive superposition.**
- **No new editor UI.** Nothing is declared: the plane is found.

## 6. `R-em3d115-6` — user docs (sources only; overview §2a)

Edit `docs/user/src/` only. Do **not** run DocGen; the owner does that.
- `em-setup.md` **#wave-port-terminals** gains *On Palace*: two terminals, a problem that is its own mirror image, two
  half-model solves, combined into the same terminal S openEMS gives. What is refused and why (a 1 % asymmetry is
  already a 0.04 dB error nothing can detect). The absorbing-face warning and what to do. Field plots are per half and
  per mode.
- `em-3d.md`: the diagnostics keys (§4), the field-plot *Solution* labels.
- `cli.md`: `em`'s report lines and `explain`'s line.

## 7. Gate

`tests/Engine.Tests/Em3d/MirrorPortTests.cs` and `tests/Ui.Tests/Em3d/PalaceTerminalPortTests.cs`. One test per claim;
run the classes you touch, not the suites.

1. **Find.** A synthetic problem shaped like the 3D Wave Ports Pair (boxes and sheets) finds y = 0. Each of these is
   refused, naming the item: strip b 1 % wider; a third terminal; a kernel solid with no operands; a flipped terminal;
   a lone one-terminal port beside the group.
2. **Half.** The half's air box, cut face kind, one port per group with `SourceNumber` set, and `ClipToAirBox` set.
3. **Combine, closed form.** Ideal even/odd 2-ports (two groups) give the ideal coupled-line 4-port to 1e-12, in
   document port order.
4. **Replay.** 119's `a-half{PMC,PEC}-r3e0.02` `port-S.csv` / `port-Z.csv` through `PalaceRun`'s readers,
   `RenormaliseWavePorts` and `Combine` reproduce 119's numbers (thru −1.241 dB / −90.07°, near-end −17.142 dB, far-end
   −22.410 dB / 89.78°, S11 −6.508 dB) to 0.001 dB / 0.01°. Those runs excited port 1 only: the test fills each half's
   2 × 2 by the line's end-to-end symmetry, and says so.
5. **Lowering golden.** The Pair's two configs differ only in the cut face's attribute under `PMC` / `PEC`, and its
   `.geo` carries the intersections. Golden committed.
6. **Byte identity.** Every existing Palace golden (`.geo` and config) is unchanged.
7. **Real run** *(Palace; `Category=Benchmark`)*. The 3D Wave Ports **Pair** cell with a Palace setup the TEST builds
   (do not edit the example; §9), through the run service, at 2, 10 and 18 GHz. Against Cohn's ideal coupled line
   (57.511 / 43.100 Ω in PTFE, the example's `expected-numbers.json`): **0.05 dB / 0.5° on every entry above −30 dB,
   |ΔS| ≤ 0.006 on the rest, σ_max and σ_min within 0.002 of 1, each half under 5 minutes.** Reach it with the
   existing mesh controls (the Palace preset, mesh regions along the strip edges). 119 found the strip-edge
   refinement is what makes the bar. Record the settings and the wall clock: §9's brief ships them. **If the existing
   controls cannot reach the bar inside 5 minutes a half, stop and report to the owner.** Do not add a mesh control in
   this brief.
8. **Checks.** Doctored half results: an even half losing 5 % beside an absorbing face warns with the face named; a
   lossless problem whose half loses 0.5 % warns; a passive, lossless pair warns nothing.

## 8. Owner check

- Run the Pair on Palace with gate 7's setup. Read the symmetry note and the power-balance lines. Plot S21/S31/S41
  over the openEMS run.

## 9. `R-em3d115-9` — then write the example brief

The 3D Wave Ports example says Pair "runs on openEMS only". Once gates 1–8 pass and the owner check is done, **write a
NEW brief**: `docs/sonnet-briefs/brief-em3d-<next free number>-example-wave-ports-palace.md` (124 at the time of
writing), and add it to the overview's §2 table. Do not edit the example in this brief. That brief updates
`examples/3D Wave Ports/` so it shows terminal ports on both solvers:
- **Pair** gains a Palace setup with gate 7's mesh settings, and its README section says how to run it on Palace,
  that Palace solves two half-models and why (one paragraph, no history), what Palace refuses (an asymmetric pair), and
  the two solvers' results side by side.
- `expected-numbers.json` gains the Palace run's recorded |S|, tolerance and wall clock, and
  `tests/Ui.Tests/Examples/Em3dWavePortsExampleTests.cs` (its gate 4, `Category=Benchmark`) re-runs it.
- The new-user guide's §14 (`#wave-ports-3d`) and `em-setup.md`'s example pointer follow.
- Rule from 117: the example's numbers come from a run, never from the brief.

## 10. Scope

- Two terminals, one mirror plane normal to a world axis, wave ports only. No N > 2, no asymmetric pair, no
  mirror-paired one-terminal ports, no PMC-strip route.
- openEMS is untouched. No mixed-mode export (D13). No terminal-drive field plots or mirrored field display (D8).
- If Palace fixes issue #328 upstream, see `RESOLVED.md` § brief-em3d-119, "If upstream fixes this": a native route
  replaces this one.
