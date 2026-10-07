# Coupled Microstrip, openEMS and Palace — brief-em3d-125

The 3D Wave Ports example's *Coupled Microstrip* cell (strips 1.2 and 2 mm, 0.3 mm apart, copper sheets on the 20 mil
PTFE-glass laminate, 15 mm, PEC floor, sides at ±4.5 mm and lid 3 mm above the laminate), run by brief 115 through
`circuitrf em` with each of the cell's two setups, 2–12 GHz in 21 points. Behind `src/Design/RESOLVED.md` §
"openEMS: a microstrip terminal under a PEC lid — brief-em3d-125" and § brief-em3d-115 R-em3d115-8.

| File | What |
|---|---|
| `openems/probes.npz` | That openEMS run's reference-plane probes, packed as `../palace-modal/openems/*/probes.npz` are: arrays `p<j>_port<i>_<signal>` (time, value) for the run exciting terminal j, signals `u_up` (strip → lid), `u_dn` (strip → floor), `ia`, `ib` |
| `openems/CoupledMicrostrip.s4p` | That run's Touchstone, written before brief 125: each terminal's voltage the mean of the two halves. `tools/palace-symmetry-spike/oems/probes.py <probes.npz> mean` reproduces it to 1.2e-10 |
| `openems/probes-dn.json` | `probes.py`'s re-assembly of the same probes with the strip → floor voltage alone, at all 21 frequencies, full precision: what `OpenEmsInterfaceVoltageTests` holds the product's reader and transform to (1e-9) |
| `palace/CoupledMicrostrip.s4p` | The Palace run's Touchstone (element order 2, no refinement passes, the four edge mesh regions), the reference |

Terminals: 1 and 2 at x = 0 (strip a, strip b), 3 and 4 at x = 15 mm.
