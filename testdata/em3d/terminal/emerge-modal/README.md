# EMerge wave-port transform inputs — brief-emw-1

Runs behind `src/Design/RESOLVED.md` § "EMerge wave ports — EMW-1", written by `tools/emerge-wave-spike/fixtures.py`.
EMerge **3.0.0b2** (circuitRF's own install, its fast sparse solver, 8 threads), element order 2, M4 with 16 GB. No meshes
are committed; `tools/emerge-wave-spike/README.md` gives the command that rebuilds each run.

JSON rather than `.npy`, as `../coupled-microstrip/openems/probes-dn.json` is: the test projects have no plain-array `.npy`
reader, and every double is written in full (Python's `repr`). Complex numbers are `[re, im]`.

| File | Run | What |
|---|---|---|
| `microstrip-thru.json` | `a-ih-1` | Case A: one 1.575 mm microstrip, 15 mm, on the 3D Wave Ports laminate in the Coupled Microstrip's housing, `WavePortIH` at each end; 2, 6, 10 GHz |
| `coupled-microstrip.json` | `b-1-dd` | Case B: the 3D Wave Ports Coupled Microstrip, `ModalPort` with two modes per face, modes re-solved per frequency, the duplicate-mode guard; 2, 5, 7.5, 11 GHz |
| `pair-pec-sides.json` | `c-2-pre` | Case C: the 3D Wave Ports Pair with **PEC** sides 11 strip widths out (the cell's PMC sides are a no-go), two degenerate TEM modes per face from a presolve; 2, 3.5, 10 GHz |
| `coax.json` | `d-modal-2` | Case D: the Launch's coax alone, 10 mm, `ModalPort` at each end; 2, 6, 10 GHz |

Each sample, in EMerge's port-mode order (face by face, mode by mode, which is terminal order):

- `S`: the raw modal S, with EMerge's S corrections (`mw_cap_sp_single`, `mw_cap_sp_col`) off.
- `V`: each terminal path's integral of the total E for each excitation, in EMerge's normalisation (modes carry 1 W), so
  **`ModalTerminalTransform` takes `V / √2`**.
- `ZPv` and `Kn`: each entry's own mode on its own path, and its β.
- `faces`: per face, from the mode fields, `TV` (path, mode), `W` and `Gp`, and the β of EMerge's port condition.
- `expect`: the terminal S from `ModalTerminalTransform`'s arithmetic with G = Fit and the Robin form EMerge needs,
  I = M⁻ᴴ K (2 − C).

With G = Fit and `RealParts`, the product's C# `Solve` reproduces the harness's Python port on all four files to 8e-16.
