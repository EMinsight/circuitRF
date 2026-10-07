# Palace modal transform on a shared face — brief-em3d-124

Runs behind `src/Design/RESOLVED.md` § "Palace modal transform on a shared face — brief-em3d-124". Palace **v0.18.1**
(`0dc74cd`, 8 MPI ranks), openEMS **v0.37.0-rc3** (`67d3784`, through `circuitrf em`), Apple M4, 16 GB. No meshes are
committed: each Palace config names `mesh.msh`, rebuilt by `tools/palace-symmetry-spike/` (`gen.py` + `cfg.py` through
`run.sh`; the arguments below). Log launch lines are shortened. The transform is `tools/palace-symmetry-spike/modal.py`,
the comparisons `shared.py`.

## What every shared-face run is

The template is 113-a's `../palace/pair-a-shared-face`: ONE port rectangle per end covering both strips, and on it
**four `WavePort` entries**: 1 and 3 `Mode` 1 `"Active": true` (x = 0 / x = ℓ), 2 and 4 `Mode` 2 `"Active": false`, every
entry its own excitation, `Offset` 0, `MaxSize` 20 on every entry (113's equal-`MaxSize` rule), `VoltagePath` from the
strip (signal) to the ground below it — entries 1 and 3 on line 1, 2 and 4 on line 2. **Departures from 113-a's run, and
why:**
- `MaxSize` 20 stated on every entry (113-a left it unset: 16 and 17). Only `a-shared-r2-default` keeps 113-a's unset
  value; it is the reproduction.
- Meshes `r2eX`: 113-a's r2 density plus 119's second Threshold field on the strips' long edges (SizeMin X, DistMin X,
  DistMax 0.3 mm). **Not 119's r3 e0.02**: the full shared-face model at that density is 762 k tetrahedra, about 5.1 M
  unknowns, which does not fit in 16 GB (119's half-model at r3 e0.02 was 2.57 M). The finest full model run here is
  r2 e0.01 (2.15 M). Route B was re-run at r2 eX so the two routes are compared on equal density.
- Geometry B is 119's **shielded** box (`shield`: sides ±4.25 mm, lid at 3.508 mm, all PEC) with the port covering the
  whole end face (`fullport`), one frequency per run (each run solves 4 excitations; three frequencies in one run would
  pass the brief's five minutes).

## Geometry A: air stripline pair (b 2, W 1.2, S 0.4, t 0.02, ℓ 15 mm, PEC walls 6 mm beyond the wider strip), 5 GHz

| Directory | `run.sh` gen / cfg arguments | ND, wall clock |
|---|---|---|
| `a-shared-r2-default` | `{"geom":"A","mode":"shared","r":2}` / `{"geom":"A","mode":"shared","maxsize":null}` — 113-a's run rebuilt | 293,586, 43 s |
| `a-shared-r2e0.02` | `{"geom":"A","mode":"shared","r":2,"edge":[0.02,0.02,0.3]}` / `{"geom":"A","mode":"shared"}` | 1,206,210, 109 s |
| `a-shared-r2e0.01` | as above with `"edge":[0.01,0.01,0.3]` | 2,147,774, 224 s |
| `a-half{PMC,PEC}-r2e0.02`, `-r2e0.01` | 119's route B at the same densities: `{"geom":"A","mode":"half","r":2,"edge":[…]}` / `{"geom":"A","mode":"half","cut":"PMC"}` (or `PEC`) | 608,532, 15 s / 1,078,506, 28 s each |
| `asym10-shared-r2e0.02`, `-r2e0.01` | line 2 10 % wider: add `"wb":1.32` to both | 1,211,892, 113 s / 2,158,780, 203 s |
| `asym3-shared-r2e0.02` | line 2 3 % wider: `"wb":1.236` | 1,207,424, 112 s |

## Geometry B: microstrip pair (εr 3.5, h 0.508, S 0.3, t 0.017, ℓ 15 mm), shielded

gen: `{"geom":"B","mode":"shared","r":2,"fullport":true,"Y":4.25,"edge":[0.02,0.02,0.3]}` (plus `"wb":1.32` where
asymmetric); cfg: `{"geom":"B","mode":"shared","freqs":[f],"shield":true,"fullport":true}` (plus `"wb":1.32`,
`"losstan":0.02`).

| Directory | What | ND, wall clock |
|---|---|---|
| `b-half{PMC,PEC}-r2e-shield-full` | Route B at the same density, 2 and 6 GHz (119's `b-half*-r2-shield-full` plus the edge field) | 795,662, 42–49 s |
| `bsym-shared-r2e-f{2,4,6}` | Line 1 = line 2 = 1.1 mm, lossless | 1,570,622, 167–181 s |
| `basym-shared-r2e-f{2,4,6}` | Line 2 1.32 mm, lossless | 1,571,870, 162–182 s |
| `basymloss-shared-r2e-f{2,4,6}` | Line 2 1.32 mm, substrate `LossTan` 0.02 | 1,571,870, 199–223 s |

## openEMS (`openems/`)

Each directory is a workspace (`.cws`, `tech.ctech`, `PairB/3d/PairB.c3d`) written by
`tools/palace-symmetry-spike/oems/gen_c3d.py`, run with `circuitrf em PairB/3d/PairB.c3d` and nothing else:
geometry B as PEC boxes 0.017 mm thick (the Palace runs' thickness, so 119's committed `b-half*-r3e-shield-full` stays
the symmetric reference), the substrate and an air box filling 119's shielded box, ±4.25 mm PEC sides, PEC ground and
lid, absorbing x faces; one two-terminal wave port per end referenced to `airbox/zmin`, terminals numbered 1 line-1 near,
2 line-1 far, 3 line-2 near, 4 line-2 far. `CellsPerWavelength` 300, `MinCellUm` 5 (the grid: 490 × 100 × 35 =
1.715 M cells, 9 cells across the substrate). Each run is 4 excitations of about 70 s (decay to −50 dB after 26 k steps),
285 s in all.

| Directory | Lines | Loss | Band |
|---|---|---|---|
| `sym-lossless` | 1.1 / 1.1 mm | none | 2, 6 GHz (the probes are broadband: `probes.py` re-reads them at 4 GHz) |
| `asym-lossless` | 1.1 / 1.32 mm | none | 2, 4, 6 GHz |
| `asym-lossy` | 1.1 / 1.32 mm | tanδ 0.02, **written by circuitRF as a conductivity exact at 4 GHz only** | 2, 4, 6 GHz |

`PairB.s4p` is the run's Touchstone, `em-notes.txt` the run's notes and warnings. `sym-lossless/probes-dn.json` (brief 125) is `probes.py`'s strip → ground re-assembly of its `probes.npz` at 2, 4 and 6 GHz, full precision, which `OpenEmsInterfaceVoltageTests` holds the product's reader and transform to. `probes.npz` holds the probe signals
the re-assembly uses, as (time, value) arrays named `p<j>_port<i>_<signal>` for the run exciting terminal j: the
reference plane's two voltage halves (`u_up` strip → lid, `u_dn` strip → ground) and the two current planes (`ia`, `ib`).
The outer voltage planes, which only feed circuitRF's own line-Z note, are not kept. `oems/probes.py` re-assembles S
from it: with the two halves averaged it reproduces `PairB.s4p` to 2e-10; with `u_dn` alone it uses Palace's voltage
path. **The lid makes circuitRF's openEMS port treat each strip as a stripline**
(a PEC face above it), so these runs' terminal voltage, and every `PairB.s4p` here, is the mean of the strip → ground and strip → lid integrals. Since brief 125 a strip on an interface (substrate below, air above) reads the strip → ground half alone, so a run of these workspaces today writes what `probes.py … dn` re-assembles.
