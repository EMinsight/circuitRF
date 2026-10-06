# Palace terminal ports by symmetry — the brief-em3d-119 harness

The scripts behind `testdata/em3d/terminal/palace-symmetry/` and `src/Design/RESOLVED.md` § "Palace terminal ports by
symmetry — brief-em3d-119". **Not part of the application:** Python, not in `circuitrf.slnx`, and like the rest of
`tools/` it references no project in this repository. It is kept so a re-run (a new Palace version, or brief 115's gates)
starts from the measured setup instead of a rebuild. 113-a's harness was lost with its session, and this one had to be
re-derived from 113-a's README.

## Needs

- Palace on `PATH` (the spack `palace` wrapper; the runs used v0.18.1, `0dc74cd`, 8 ranks) and its `mpirun`.
- Gmsh's Python module: `export PYTHONPATH=/opt/homebrew/lib` with Homebrew's gmsh 4.15.
- numpy and scipy.

A machine's own `PATH` lines can go in an `env.sh` beside `run.sh`, which sources it when present. `env.sh` and `runs/`
are git-ignored.

## The scripts

| Script | What it does |
|---|---|
| `gen.py` | The meshes: geometry A (air stripline pair) and B (microstrip pair), port faces `touch` / `gap` / `half`, `r` refinement, `edge` = [size, distmin, distmax] strip-edge field, `wa`/`wb` strip widths, `fullport`. A Gmsh-Python transcription of Palace's `examples/cpw/mesh/mesh.jl` |
| `cfg.py` | The Palace config for a mesh: which ports, which excited, `cut` PMC/PEC, `shield` (B's absorbing faces PEC), `fullport` |
| `run.sh` | Mesh, config and Palace into `runs/<name>/` |
| `ana.py` | Readers for `port-S.csv` / `port-Z.csv`, renormalisation, the §3 combination, the ideal coupled line, line extraction |
| `routeA.py` | `routeA.py <even run> <odd run> [f] [half]`: combine and compare with A's 2D reference (works for both routes) |
| `routeB_B.py` | Geometry B's half-models against a quasi-static reference (`QSREF=Ze,εe,Zo,εo`) |
| `portref.py` | Combined S against an ideal line built from Palace's own port Z_PV and kₙ |
| `asym.py` | §4d: the exact asymmetric 4-port from the 2D C matrix, the formula's own error, and route A's runs against it |
| `fv2d.py`, `qs2d.py` | 2D finite-volume Laplace: homogeneous air (C matrix, any cross-section) and quasi-static with a dielectric |

## Reproducing the fixtures

Each fixture directory's name is the `run.sh` name. For example:

```sh
./run.sh a-touch-r3       '{"geom":"A","mode":"touch","r":3}'                       '{"geom":"A","mode":"touch"}'
./run.sh a-gap0.075-r3    '{"geom":"A","mode":"gap","g":0.075,"r":3}'               '{"geom":"A","mode":"gap"}'
./run.sh a-halfPMC-r3e0.02 '{"geom":"A","mode":"half","r":3,"edge":[0.02,0.02,0.3]}' '{"geom":"A","mode":"half","cut":"PMC"}'
./run.sh b-halfPMC-r3e-shield-full '{"geom":"B","mode":"half","r":3,"fullport":true,"edge":[0.02,0.02,0.3]}' \
         '{"geom":"B","mode":"half","cut":"PMC","freqs":[2.0,6.0],"shield":true,"fullport":true}'
./run.sh asym-gap0.075-r3 '{"geom":"A","mode":"gap","g":0.075,"r":3,"wb":1.32}'     '{"geom":"A","mode":"gap","wb":1.32,"excite":[1,3]}'
python3 routeA.py a-halfPMC-r3e0.02 a-halfPEC-r3e0.02 5.0 half
```

Every fixture's `config.json` is exactly what `cfg.py` wrote, so the config JSON for any other run can be read off it.
**Before trusting a rebuilt mesh, reproduce a committed fixture first**: `a-touch-r2` should give Z_PV 68.8686 Ω.
