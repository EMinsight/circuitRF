# EMerge wave ports (brief-emw-1) — the spike's harness

The scripts behind `src/Design/RESOLVED.md` § "EMerge wave ports — EMW-1" and `testdata/em3d/terminal/emerge-modal/`.
**Not part of the application:** Python, not in `circuitRF.slnx`, referencing no project in this repository. It is kept so
a re-run (a new EMerge version, or EMW-2's and EMW-3's gates) starts from the measured setup instead of a rebuild.

## Needs

- An EMerge environment: circuitRF's own install (Settings ▸ Solvers, or `circuitrf solver install emerge`) puts one at
  `~/.circuitRF/solvers/emerge/<version>/env`; `run.sh` uses its Python unless `EMERGE_PY` names another.
- `python3` for the watchdog (standard library only).
- `report.py`'s case A reference imports `qs2d.py` from `../palace-symmetry-spike/` (2D quasi-static Laplace).

## The scripts

| Script | What it does |
|---|---|
| `run.sh <case> <name> '<json>' [GB] [min]` | One case under the watchdog into `runs/<name>/`, its log at `runs/<name>.log` |
| `watch.py` | The watchdog: polls every 0.1 s and kills the whole process group over a resident-memory cap (default 4 GB; the runs used 9), under 10 GB of free disk, or over a time cap |
| `cases.py` | The four cases (A microstrip thru, B Coupled Microstrip, C Pair, D the Launch's coax), built in Gmsh through EMerge's geometry classes; the solve; and per frequency the raw modal S, each terminal path's integral of the total E for each excitation, and from the mode fields each face's T_V, E-overlap W, power Gram and β. Options in its docstring |
| `xform.py` | `csharp`: a line-for-line port of `ModalTerminalTransform.Solve`, plus the Robin form EMerge needs (`robin="source"`, K (2 − C)). `exact`: the transform from the mode fields alone. The references (ideal line, ideal coupled line, Touchstone reader) |
| `report.py <runs...>` | Every transform variant against the case's references: the closed-form or quasi-static line (A, D), Palace's committed terminal S (B), Cohn's ideal coupled line (C) |
| `fixtures.py <run> <name>` | A run's transform inputs as `testdata/em3d/terminal/emerge-modal/<name>.json` |

## The runs behind the findings

`runs/` is git-ignored. These recreate it (M4 with 16 GB: a few seconds to half a minute each):

```
./run.sh D d-modal-2 '{"hmax":0.3,"curv":20}' 9
./run.sh D d-coax-2  '{"hmax":0.3,"curv":20,"port":"coax"}' 9
./run.sh A a-1       '{"edge":0.1}' 9
./run.sh A a-2       '{"edge":0.05,"res":0.1}' 9
./run.sh A a-ih-1    '{"edge":0.1,"port":"ih"}' 9
./run.sh B b-1       '{"edge":0.1}' 9
./run.sh B b-2       '{"edge":0.05}' 9
./run.sh B b-1-nomix '{"edge":0.1,"mixed":false}' 9
./run.sh B b-1-dd    '{"edge":0.1,"dedupe":true}' 9
./run.sh B b-1-adapt    '{"edge":0.1,"sweep":"adaptive","nsamp":5}' 9 20
./run.sh B b-1-adapt-dd '{"edge":0.1,"sweep":"adaptive","nsamp":5,"dedupe":true}' 9 20
./run.sh C c-1       '{"edge":0.1}' 9
./run.sh C c-pre     '{"edge":0.1,"presolve":6,"neff":1.3,"tem":true}' 9
./run.sh C c-2-pre   '{"edge":0.07,"presolve":6,"neff":1.3,"tem":true}' 9
./run.sh C c-pre-align '{"edge":0.1,"presolve":6,"neff":1.3,"tem":true,"align":true}' 9
./run.sh C c-pmc     '{"edge":0.1,"presolve":6,"neff":1.3,"tem":true,"sides":"pmc"}' 9
```

The fixtures: `fixtures.py a-ih-1 microstrip-thru`, `b-1-dd coupled-microstrip`, `c-2-pre pair-pec-sides`,
`d-modal-2 coax`.
