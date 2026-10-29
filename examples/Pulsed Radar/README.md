# Pulsed Radar

A 48 V S-band GaN power stage driven by **pulses**: 100 µs of carrier every 1 ms from a pulsed-CW
`MOD_Source`, simulated with the **Envelope** analysis. A pulsed-radar amplifier is specified by what happens
inside a pulse — how far the output **droops** across the top as the channel heats and the drain supply
sags, and how much pulses differ from one another (**pulse-to-pulse stability**). Harmonic balance cannot see
either: it solves one steady state, so the device sits at one temperature and one trap state. The envelope
run steps the device's thermal and trap states, and the supply's storage capacitor, as memory over time.

## radar-gan: the device

The transistor is a **synthetic** GaN HEMT, meant to stand in for the kind of 48 V GaN-on-SiC part a
pulsed S-band amplifier would use: a 12 mm device (`N=48`) carrying about 1 A/mm, with a three-pole
thermal path from the channel to the flange (1.5 °C/W in all) and drain-lag trapping. No real device was
measured: `radar-gan/radar_gan_8x250.cmodel` was built with **Tools ▸ Model Composer…** from simulated data.

## PulsedPA: the stage under CW

The device at `N=48` in a class-AB stage at **3 GHz**, Vgs −2.6 V, Vdd 48 V, matched rather than put into
50 Ω: a low-pass L match on each side, to about 1.5 + j4.5 Ω at the gate and 4 + j4 Ω at the drain, the
power optimum (5.5 + j6 Ω is the efficiency optimum, and dissipates half as much). A stage this size needs stabilising: 1 Ω in series and 10 Ω ∥ 50 pF in
the gate, 5 Ω + 20 pF from the gate to ground, and 50 Ω in the gate feed. Without them it is unstable from
0.1 to 8 GHz. The drain is fed through a choke from a **470 µF storage capacitor**, which the 48 V supply
charges through **0.2 Ω**: both are drawn on the schematic, so the supply sags during a pulse in the
envelope run as it does on a real bench.

A harmonic-balance drive sweep, 20 … 34 dBm: Pout, gain, drain efficiency, PAE and the junction's rise
(`Tj_rise`, read from the T pin, the net `tj`). This is the steady state of a pulse that never ends: the
device at the temperature a CW drive heats it to. **Under a second**; every point converges. Past 34 dBm
the solve fails to converge at most drives, so the sweep stops at the design drive.

| Pin (dBm) | Pout (dBm) | Gain (dB) | PAE (%) | Junction rise (K) |
|---|---|---|---|---|
| 20 | 30.4 | 10.4 | 5.7 | 25 |
| 30 | 42.3 | 12.3 | 25.6 | 66 |
| 32 | 45.4 | 13.4 | 36.6 | 78 |
| 34 | 48.6 | 14.6 | 53.2 | 78 |

## PulsedPA_pCW: the stage on pulses

The same stage on a pulsed-CW source: **100 µs pulses every 1 ms** (10 % duty), 0.5 µs edges, the first
rising edge 10 µs into the record, and the source's `Pavg` the **pulse-top** power. Open the source's
**Design signal…** to see the pulse.

One envelope run of 3 ms holds three whole pulse periods: **about 105 s**, as one segment. Before it starts, the
Messages pane says what it will show: the slowest memory, the drain-lag state's 1 ms emission, needs
about 5 ms to settle, so the run shows **the first pulse from cold**, not a settled train. Simulate opens **PulsedPA_pCW.cdd**: the output power
over the run, the junction's temperature rise over the same time, the first pulse's top zoomed, and the
per-pulse table. The output power is measured at `Iout`, an IProbe in series with the 50 Ω load, and the
pulse figures are its rows; the run also records the voltage at every labeled net, `tj` among them.

At the 34 dBm design drive (pulse-top available power):

| | Pulse 1 | Pulse 2 | Pulse 3 |
|---|---|---|---|
| Top (dBm) | 49.07 | 49.14 | 49.15 |
| Droop, 10–90 % of the top (dB) | 0.26 | 0.30 | 0.30 |
| Phase droop (°) | −0.63 | −0.76 | −0.76 |
| Rise / fall (ns) | 449 / 422 | 414 / 422 | 414 / 422 |

- **The junction heats 47 K across the first pulse**, from its 15 K quiescent rise to 63 K, and the
  storage capacitor sags 0.34 V (0.7 %). Under CW the same drive sits at 78 K.
- **Most of the heat arrives before the droop window opens.** The channel's own 1 µs pole has heated it
  within the first few microseconds (the zoomed plot shows the output falling 0.26 dB over the first 10 µs of the top),
  and the window, 10–90 % of the top, sees the die's 20 µs pole and the start of the package's 0.5 ms one.
- **Pulse-to-pulse: 0.08 dB and 0.22°.** After pulse 1 the junction cools to 4.8 K, below its 15 K
  quiescent value: the drain-lag state captured at the pulse's peak and emits over a millisecond, so the
  quiescent current is lower until it does. That is current collapse, and it is why pulse 1 differs from
  pulses 2 and 3.
- `Pin_avg_dBm`, and `Pavg_dBm` and `DE_pct` (50.4 %) at `Iout`, are averages over the whole 3 ms run, off-time
  included. The run ends a whole period after the last pulse's rising edge, so the supply has recharged the
  storage capacitor that pulse drew on: stopped just after a pulse, `DE_pct` counts the capacitor's share of
  the pulse as free and reads high.

## Settings traded away for speed

| Setting | Here | Why, and what it costs |
|---|---|---|
| Accuracy | `draft` (the envelope step is the signal's own rate, 32.6 ns) | `standard` is 4× the steps. The pulse top is flat and the droop is thermal, so a coarser step costs nothing there; the edges are drawn with 15 steps each instead of 60 |
| Edges | 0.5 µs | a real radar pulse's edges are often ~100 ns, which needs a 4× faster native rate and 4× the steps |
| Record | three pulse periods, 3 ms | a settled train needs about five of the 1 ms drain-lag emission and ten or more pulses |

A sweep of the pulse width is not offered: a carrier is a quoted description, which a sweep variable does
not reach into, so each width is its own run. Edit `PW` on the source and run again.

## Not modelled here

Modulation inside the pulse (chirps, phase codes), pulsing the gate or drain bias with the RF, and radar
range-sidelobe and spectral-mask figures.
