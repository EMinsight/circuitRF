# Terminal wave-port fixtures — brief-em3d-113-a

Runs behind `src/Design/RESOLVED.md` § "Terminal wave ports — brief-em3d-113-a". Brief 113's fixtures were deleted
before this redo; nothing here descends from them. Palace **v0.18.1** (8 MPI ranks), openEMS **v0.37.0-rc3**
(`67d3784`, 8 threads), Apple M4, 16 GB. No meshes are committed: each Palace config names `mesh.msh`, and the
generator and its parameters are given below so the mesh can be rebuilt. Log launch lines are shortened.

## Palace

Every pair and coax mesh comes from a Gmsh-Python transcription of the upstream example's own Julia generator. The coax
transcription was checked by regenerating the shipped `examples/coaxial/mesh/coaxial.msh` (256 hexahedra) and
re-running `coaxial_lumped_wave.json` on it: port-S.csv identical to 12 digits.

| Directory | Descends from | Departures from the example, and why |
|---|---|---|
| `cpw-example/` | `examples/cpw/cpw_wave_uniform.json`, unchanged, on the shipped `cpw_wave_0.msh` | None. `port-S.csv` and the log only (the config is upstream's). 64 s, ND 117,764 |
| `coax/` | `examples/coaxial/coaxial_lumped_wave.json` + `mesh/mesh.jl` (`generate_coaxial_mesh`, refinement 2, order 2) | The 3D Connector's coax: pin ⌀ 0.4 mm, bore ⌀ 1.34 mm, εr 2.1, length 10 mm (example: RG-401, εr 2.08, 40 mm). LossTan 0 (example 4e-4), so ∠S21 is compared with −βℓ directly. **Both ends are wave ports** (the example's port 1 is a 100 Ω lumped port), each with a `VoltagePath` pin → shield, signal first. Samples 2/6/10 GHz. 4.4 s, ND 25,680 |
| `coax-offset/` | `coax/` | Port 2 `Offset` 1.0 mm. Only that |
| `pair-a-per-line/` | `examples/cpw/mesh/mesh.jl` (`generate_cpw_wave_mesh`) + `cpw_wave_uniform.json` | Geometry A: air stripline pair, grounds b = 2 mm apart, W 1.2, S 0.4, t 0.02 mm, ℓ 15 mm, PEC side walls 6 mm beyond the strips. **One port rectangle per strip, the end face split at the midline**, as the cpw example does; but the rectangles run to the side walls (the cpw's stop 100 µm into the ground metal, which suits a CPW whose field is in its slots, not a stripline whose field spreads ~b sideways). No substrate; the box's top/bottom are the grounds; metal volume removed as upstream does. Sizes: near-strip 1.2·2⁻ʳ mm, far 2.0·2⁻ʳ mm, DistMin W, DistMax 2b (upstream: 1.5·W·2⁻ʳ, h·2⁻ʳ, W, 0.9·h_box). This is r = 2. All four ports excited; `VoltagePath` strip → lower ground on each. 5 GHz only. 46 s, ND 294,656 |
| `pair-a-shared-face/` | `pair-a-per-line/` | Brief §3d: ONE port rectangle per end covering both strips; two `WavePort` entries on it, `Mode` 1 (`Active`) and `Mode` 2 (`"Active": false`), as the config reference describes those keys. 44 s, ND 294,792 |
| `pair-b-per-line/` | `pair-a-per-line/` | Geometry B: microstrip pair on εr 3.5, h 0.508, W 1.1, S 0.3, t 0.017 mm, ℓ 15 mm. Substrate on a PEC ground; air box 3 mm beyond and above, absorbing (order 1) like the cpw's far field; port rectangles ground → 2.54 mm above the substrate, 2.54 mm beyond the outer strip edges, the rest of each end face PEC. Near-strip 1.1·2⁻², far 2.0·2⁻² mm. 2 and 6 GHz. 75 s, ND 284,656 |

## openEMS

| Directory | Descends from | Departures, and why |
|---|---|---|
| `coax-cylindrical/` | `matlab/examples/waveguide/Coax_CylinderCoords.m` | The 3D Connector's coax (r 0.2/0.67 mm, εr 2.1, 10 mm). **141 azimuth lines** (example 71: Z then reads +0.72 Ω, first order in Δα). Radial and axial cell 20 µm = r_i/10 (as the example). Gauss f0 6, fc 4 GHz (the example's f0 = fc reaches DC, and a static field on a closed coax never decays, so the end criterion is unreachable). Probes laid out as upstream's line ports lay them: voltage planes n−1, n, n+1 at two stations (z ≈ 2.5 and 7.5 mm), current discs at the half-cells between, so ports.py's own β/Z_L formulas apply. `model.xml` is the Python interface's `Write2XML` dump, and the probe files are from **replaying that file** (it takes a different time step from the in-memory run, 2.52e-14 against 3.65e-14 s; the result agrees to 0.01 Ω / 0.01°) |
| `pair-a/exc1…exc4/` | `matlab/examples/transmission_lines/Stripline.m` (`AddStripLinePort` via the Python `StripLinePort`), S assembled as `directional_coupler.m` / `calc_ypar.m` do | Geometry A with **t = 0** (a StripLinePort is a sheet), air, h = 1 mm each side. PML_8 on x, **PMC on y** at ±(10·W + (S+W)/2), PEC z (the example's boundaries). Grid: the example's construction at λ/100 (example λ/50) and 8 cells per strip height (example 4), x at λ/100; edges by the 1/3–2/3 rule at res/4. L 45 mm, FeedShift 6 mm, MeasPlaneShift 15 mm → planes 14.80 mm apart. One run per excited port. `model.xml` is written in **`CsxcadWriter`'s shape** (metres, integer probe weights, flat excitation weights, R() round-trip numbers, half-cell probe edges written exactly); voltage probes therefore carry weight 1 where upstream's carry 0.5 — halve `ut_*1 + ut_*2` |

The 2D references (Laplace, finite volume, graded to 0.3 µm at the edges) are in `RESOLVED.md`, not here.
