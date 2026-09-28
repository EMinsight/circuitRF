# Thermal spike (brief-em3d-72 §3, Q1–Q8) — SPIKE MATERIAL

Throwaway harnesses and their recorded results. **Nothing in `src/`, `tests/` or `tools/` may import,
copy, reference or build anything here** — a spike that leaves product code behind has made a design
decision nobody reviewed. The answers, with their evidence, are in
[`docs/design/em-3d-f3-spike-findings.md`](../../../docs/design/em-3d-f3-spike-findings.md); this README
says how each was produced and how to reproduce it.

Machine: Apple M4, 10 cores, 16 GB, macOS 27.0. Gmsh 4.15.2 (Homebrew), .NET SDK 10.0.203, and the Python
tools of [`../README.md`](../README.md). Gmsh is always called as a program (`gmsh file.geo -3 …`); its
meshes and logs are git-ignored and regenerate from the committed `.geo`/`.py`.

| Folder | Question | How to reproduce |
|---|---|---|
| `q1-embedded-curve/` | Q1 embedded curves; the line-source consequence | `gmsh a.geo -3 -order 2 -format msh41 -o a2.msh` (and `b`, `c`), then `python check_embedding.py a2.msh …`; `python line_source.py` |
| `q2-embedded-surface/` | Q2 embedded surfaces | `gmsh q2.geo -3 -format msh22 -o q2.msh`, then `python check_q2.py q2.msh strip foot` |
| `q3-interfaces/` | Q3 shared surfaces and their sides | `gmsh q3.geo -3 -format msh22 -o q3.msh`, then `python ../q2-embedded-surface/check_q2.py q3.msh die_flange die_lid` |
| `q4-element-order/` | Q4 P1 against P2 | `python q4.py` (after `../spreading`) |
| `q5-crossover/` | Q5 Cholesky against AMG | `python export_matrices.py`, `dotnet build -c Release ../harness`, `python run_crossover.py` |
| `q6-contrast/` | Q6 IC(0) against AMG at 600:1 | `python export_contrast.py`, then the harness per matrix (below) |
| `q7-mesh-span/` | Q7 0.5 µm to 10 mm | `python q7.py` |
| `q8-wire-foot/` | Q8 the foot's cost | `python q8.py` |
| `harness/` | the C# scratch harness for Q5/Q6 | `dotnet build -c Release testdata/thermal/spike/harness` |

## The harness (`harness/`)

A console program in **no solution file**, built only by hand, in **Release**. One method, one matrix,
one process, one JSON line on stdout — so the driver can read each run's own peak memory with
`/usr/bin/time -l` (`Process.PeakWorkingSet64` reads 0 on macOS). Methods:

- `cholesky` — CSparse 4.3.0's `SparseCholesky` (the version `src/Engine` already uses), AMD ordering on
  A + Aᵀ, factor and one solve;
- `amg [theta]` — CG preconditioned by a **minimal smoothed-aggregation AMG written for this spike** from
  the published algorithm (Vaněk, Mandel & Brezina, *Algebraic multigrid by smoothed aggregation for
  second and fourth order elliptic problems*, Computing 56, 1996): symmetric strength of connection
  (θ = 0.08 by default), three-pass aggregation, a piecewise-constant tentative prolongator smoothed by one
  damped-Jacobi step (ω = 4/3 / ρ(D⁻¹A), ρ by 15 power iterations), Galerkin coarse operators, a dense
  Cholesky below 400 unknowns, V(1,1) with symmetric Gauss–Seidel;
- `ic0` — CG + zero-fill incomplete Cholesky (as ILU(0) of the symmetric matrix), with a diagonal shift if
  a pivot fails;
- `jacobi` — CG + Jacobi.

CG stops at a relative residual of **1e-8**. Matrices under 100,000 unknowns are run three times and the
fastest kept, so JIT and first-touch are not what is timed. The matrices themselves (raw binary CSR, up
to 335 MB each) are written outside the repository, to `$THERMAL_SPIKE_MATRICES`
(default `~/opt/thermal-spike/matrices`), and are not committed.

    H=testdata/thermal/spike/harness/bin/Release/net10.0/ThermalSpikeHarness
    /usr/bin/time -l $H amg ~/opt/thermal-spike/matrices/s5-5.bin

## Recorded results

- `q1-embedded-curve/` — `a.geo` (`Curve{…} In Volume{…}`), `b.geo` (BooleanFragments, feet on the
  box face), `c.geo` (a curved spline wire); `line-source.csv`/`.json`.
- `q2-embedded-surface/q2-entities.txt`, `q3-interfaces/q3-entities.txt` — the entity tables the `.geo`
  printed.
- `q4-element-order/q4.csv`, `q4.json`.
- `q5-crossover/crossover.csv`, `crossover.json`.
- `q6-contrast/contrast.csv`, `contrast.json`, `pyamg.json`.
- `q7-mesh-span/q7.csv`, `q7.json`.
- `q8-wire-foot/q8.csv`, `q8.json`.
