# Model Composer

Three device models built from data with **Tools ▸ Model Composer…**, and the circuits that use them. Every data file
here is **synthetic**: circuitRF simulated it from a device whose equations are known, and each data set's note says so.
That is what makes the example checkable. The model can be compared with the device it was built from, which measured
data never allows.

Open a `.cmc` from the project tree to see its steps, its plots and its results.

## synthetic-gan

A GaN FET, four fingers of 125 µm, measured as a lab would measure one:

| Data set | What it is |
|---|---|
| `dc_25C` | DC I–V at 25 °C, Vgs −5 … 1.4 V, Vds 0 … 20 V. The junction heats, and drain lag sits at its DC state. |
| `pulsed_q0_25C`, `pulsed_q0_125C` | Pulsed I–V from a quiescent point of (−6 V, 0 V), at two chuck temperatures. |
| `pulsed_q10_25C`, `pulsed_q20_25C` | Pulsed I–V from quiescent drain voltages of 10 V and 20 V. |
| `pulsed_dp20_25C` | A double pulse: a 50 µs pre-pulse to 20 V sets the traps, then the measuring pulse 1 µs later. |
| `s_25C`, `s_85C` | S-parameters at 49 biases and two temperatures, 0.5 … 20 GHz. |

The data is at the **probe** plane. The pads come off with the open and short dummies in `pads/`, and the network is the
`GaNTwoShell` cell, the stock GaN two-shell network placed in this workspace as a cell of its own.

The document carries every step:

- **Network**: `Lg`, `Ld` and `Ls` fitted by the multi-bias method; every other value is held at the stock value.
- **Thermal**: self-heating with Rth = 25 °C/W and τth = 10 µs, typed rather than extracted (see the note below).
- **Current**: ANN `Id` and `Ig`, with the junction temperature and the drain-lag state as inputs.
- **Traps**: CaptureEmission drain lag at its defaults, from the three trap-setting levels 0, 10 and 20 V. The
  pulses from 10 V that go above 10 V lie beyond their trap-setting bias and are kept for validation only.
- **Charge**: adjoint-trained ANN charges from both S sets.

The exported model is `gan_4x125.cmodel`. Two test benches place it as an **8-finger** device (`N=8`), so its core is
doubled, its series resistances halved and its Rth halved:

| Bench | What it runs |
|---|---|
| `GanPowerSweep` | A class-AB stage at 2 GHz, Vgs −0.8 V, Vdd 8 V, 50 Ω in and out, drive swept −4 … 22 dBm |
| `GanLoadpull` | The same device over 49 loads centred between its maximum-power and maximum-efficiency loads, each driven to 3 dB compression |
| `GanVerilogA` | The same device twice on identical bias networks: as the `.cmodel` on ports 1 and 2, and as `gan_4x125.va` on ports 3 and 4. A Vgs sweep, S-parameters and a drive sweep 0 … 16 dBm, each measure in pairs. Needs a Verilog-A compiler |

`gan_4x125.va` is the same model as Verilog-A source, for another simulator: **Export Verilog-A…** on the Export step,
or `circuitrf model export synthetic-gan/gan_4x125.cmodel -o synthetic-gan/gan_4x125.va`.

**Settings chosen for this example**, with what a real job would use:

| Setting | Here | A real job |
|---|---|---|
| Data points | ≤ 150 per I–V set, 49 S biases, 12 frequencies | Denser near the knee and pinch-off; 50+ frequencies |
| Rth | Typed, 25 °C/W | Extracted from pulsed I–V at two or more chuck temperatures, when drain lag is not modelled; typed from a thermal simulation when it is |
| `IgFloor` | 1e-7 | The default 1e-4 suits a gate whose leakage is not needed. This example's gate current spans ten decades, and at pinch-off the default's resolution is microamps |
| Network | Three inductances free | Every value free that the data determines; check the ± and the ⚠ marks |
| Drain bias | 8 V. The drain swings to about twice its bias, and this data ends at Vds 20 V | The rated drain voltage, with I–V data reaching past twice it |

**Why Rth is typed.** A pulse from a 0 V drain leaves the drain-lag state half a volt short of its DC value, because
capture slows as the gap closes. Rth extraction compares pulsed and DC currents at the same bias, and reads that half
volt as heating. With drain lag modelled, type Rth.

**The data reaches −5 V and +1.4 V on the gate on purpose.** In the power sweep the gate swings from about −4.4 V into
forward conduction. A model is continued linearly outside its data, with an added conductance, and a gate that swings
past the last measured Vgs is no longer the device.

## passive-spiral

Six spiral inductors of 1.5 … 6.5 turns, as two-port S-parameters from 0.2 to 20 GHz. `spiral.cmc` fits the library's
`spiral` topology to all six at once, with one law per element value in the turns. Its substrate network under port 2
is tied to the one under port 1, and the 3.5-turn spiral is held out and predicted. It exports the cell `SpiralFamily`,
whose one parameter is `turns`. The element values are variables on the cell's own schematic, each a law of `turns`
written in nH, fF and Ω: `x` is `turns` normalised to the fitted range, and `Ls`, `Cs`, `Cox1` and the rest are
polynomials in `x`.

`SpiralFilter` places that cell at **3 turns**, between two measured spirals, in a π low-pass between 50 Ω ports. Set
`turns` outside 1.5 … 6.5 and the run warns once.

## simulated-tables

`tables.npy` is what a simulator writes for a transistor: per bias, the terminal voltages and currents, the intrinsic
voltages its internal nodes carry, and Y at 1 GHz. `fet_tables.cmc` imports it at the device plane. Because the tables
also carry intrinsic voltages, de-embedding checks its own computed ones against them, and the Data step reports the
largest difference. The network is held at the stock values the tables were simulated with. The data is at one
temperature, so the model has no temperature dependence.

## Headless

Every step above is a `circuitrf model` noun:

```
circuitrf model deembed synthetic-gan/gan_4x125.cmc
circuitrf model fit synthetic-gan/gan_4x125.cmc --step all
circuitrf model validate synthetic-gan/gan_4x125.cmc
circuitrf model export synthetic-gan/gan_4x125.cmc -o synthetic-gan/gan_4x125.cmodel
circuitrf model export synthetic-gan/gan_4x125.cmodel -o synthetic-gan/gan_4x125.va
circuitrf hb GanPowerSweep/schematic/GanPowerSweep.csch
```
