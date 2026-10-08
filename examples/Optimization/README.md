# Tuning and Optimization

Three test benches, each small enough that a whole optimization takes well under a second. Each one
already has its values set up for tuning, its goals written and an algorithm chosen, so you can open
it and press **Run** in the **Optimizer** panel (tabbed behind Tuning, which is tabbed behind
Analyses) before reading anything else.

**Simulate a bench once first.** Its Data Display, `<cell>.cdd` beside this workspace's `.cws`, then
opens already plotting the quantities its goals read, with each goal's limit in the plot title. Leave
it open: it follows the sliders while you tune and the best point while the optimizer runs.

## LSectionMatch — two values, one goal

A series `L1` and a shunt `C1` matching a 200 Ω load (port 2) to 50 Ω (port 1). As drawn
(`L1` = 5 nH, `C1` = 0.5 pF) the match is poor: |S11| sits between −5.2 and −5.4 dB across the band.

| | |
|---|---|
| Variables | `L1.L` 1–50 nH, `C1.C` 0.1–10 pF, both tune and opt |
| Goal | `Match = dB(SP1.S(1,1))`, **≤ −20 dB** over 0.95–1.05 GHz |
| Algorithm | Gradient (Levenberg–Marquardt), seed 1 |

**Run** finishes in 12 iterations and 39 simulations with the goal met:

| | Start | Best |
|---|---|---|
| `L1.L` | 5 nH | 12.709 nH |
| `C1.C` | 0.5 pF | 1.4851 pF |
| worst \|S11\| in the band | −5.17 dB | −20.00 dB at 1.05 GHz |

**The worst point lands exactly on the limit, and that is how least squares behaves.** The cost is
zero the moment every point meets its limit, so the run stops there rather than going further. The
best point is −23.4 dB at 1.00 GHz and exactly −20.0 dB at the band edge. If you want margin, ask for
it: write −22 dB and run again.

Two presets are saved in the schematic — open **Presets** in the Tuning panel:

- **As drawn** — the starting values.
- **Textbook** — the closed-form L-section for 50 Ω to 200 Ω at 1 GHz (Q = √3: X<sub>L</sub> = 86.6 Ω,
  B<sub>C</sub> = 8.66 mS). It centres the match at 1 GHz, and its worst point in the band is
  −22.3 dB at 1.05 GHz: 2.3 dB of margin the optimizer's answer does not have, because the run stopped
  the moment the goal was met.

Recall one, **Push** it, and run again — or **Reset** in the Optimizer panel to put back what a Push
wrote — to compare how different algorithms get from the same start to the goal.

This is also the file the command line is checked against —
`circuitrf opt LSectionMatch/schematic/LSectionMatch.csch` prints the same values, cost and
simulation count as the panel.

## BandpassFilter — four values, three goals, minimax

A three-resonator bandpass filter: a shunt LC at each end and a series LC between them. The two
shunt resonators read the same two variables, so four VAR rows drive six parts.

| | |
|---|---|
| Variables | `Lp` 0.5–2 nH, `Cp` 10–50 pF, `Ls` 20–80 nH, `Cs` 0.3–1.2 pF |
| Goals | `Passband`: dB(S21) **in −0.5 … 0 dB** over 0.9–1.1 GHz · `StopLow`: **≤ −25 dB** over 0.5–0.7 GHz · `StopHigh`: **≤ −25 dB** over 1.35–2 GHz |
| Algorithm | Minimax, seed 1 |

As drawn the filter is tuned to about 1.13 GHz: dB(S21) is −20.9 dB at the bottom of the passband
and the upper stopband reaches −14.8 dB at 1.35 GHz. **Run** meets all three goals in 12 iterations
and 85 simulations.

**Minimax** lowers the single largest violation rather than the sum of squares, so it levels the
worst points against one another. Here that is the passband: it ends at −0.50 dB at both band edges
and at the bottom of its deepest ripple (1.04 GHz) at once, while the stopbands keep 10 dB (low side,
−35.0 dB) and 6.1 dB (high side, −31.1 dB) to spare. The values it
finds are within 6 % of the textbook 0.5 dB Chebyshev design for this band (0.997 nH, 25.4 pF,
43.6 nH, 0.581 pF).

Two presets are saved in the schematic. Open **Presets** in the Tuning panel:

- **As drawn** — the starting values. Recall it to see where the run started.
- **Equiripple** — the run's best point, with its cost. Recall it, then **Push**, to put it in the
  schematic.

Start tuning (▶), recall *As drawn*, press **Snapshot**, then recall *Equiripple*: the Data Display
shows both responses at once, the earlier one as faded ghosts.

## StabilityAndGain — a complex value tuned by its parts, and a range drawn too narrow

A FET amplifier biased at V<sub>GS</sub> = −1 V, V<sub>DS</sub> = 10 V, with a series resistor
`R1 = Rstab` and a shunt resistor `R2 = Rshunt` at its gate to stabilize it. Port 1's impedance is
the VAR `Zs = polar(50, 0) Ohm`.

| | |
|---|---|
| Variables | `Rstab` **1–3 Ω**, `Rshunt` 20–2000 Ω, `mag(Zs)` 10–200 Ω, `phase(Zs)` −80…80° |
| Goals | `Stable`: `mu(SP1.S)` **≥ 1.05** over 0.5–10 GHz · `Gain`: dB(S21) **≥ 13 dB** over 2–3 GHz |
| Algorithm | Auto (a global search, then a local polish), seed 1 |

As drawn the amplifier is unstable everywhere: μ is between 0.20 (at 1.8 GHz) and 0.54. The gain is
15.5–17.7 dB.

**Two goals on one analysis.** Both read `SP1`, so each simulation answers both. They pull against
each other: the resistors that raise μ also cost gain. μ is a property of the two-port alone, so it
does not depend on `Zs`, but the gain does — the source impedance is what the optimizer uses to win
gain back.

**A complex value tuned by its parts.** `Zs` is not tuned whole. Its **magnitude** and **phase** are
two rows of their own, each with its own slider and range. Moving the magnitude slider keeps the
phase where it is, and moving the phase keeps the magnitude. The optimizer treats them as two
coordinates. **Push** writes `Zs` back whole and in the form it was written: `polar(…, …) Ohm`, not a
rectangular number.

**The range of `Rstab` is drawn too narrow on purpose.** **Run** ends after 53 iterations and 394
simulations with **neither goal met**:

| | Best |
|---|---|
| `Rstab` | **3 Ω — at the top of its range, marked railed** |
| `Rshunt` | 31.8 Ω |
| `Zs` | polar(25.1, 39.0°) Ω |
| worst μ | 0.985 at 7.9 GHz |
| worst gain | 12.66 dB at 3 GHz |

The ⚠ on the `Rstab` row says the best value is at an edge of its range: the optimizer wanted more
than it was allowed. **Widen** doubles the range on that side — 1–3 Ω becomes 1–5 Ω — as one undo
step. Run again and it rails again at 5 Ω (μ 1.041, gain 12.90 dB). Widen once more, to 1–9 Ω, and
the run meets both goals after 25 iterations and 200 simulations:

| | Best |
|---|---|
| `Rstab` | 6.58 Ω |
| `Rshunt` | 49.7 Ω |
| `Zs` | polar(42.4, 21.3°) Ω |
| worst μ | 1.055 at 4.1 GHz |
| worst gain | 13.18 dB at 3 GHz |

A railed variable is a hint, never a stop. The run still finishes, and the range is yours to change.

## Settings

The amplifier's 0.5–10 GHz sweep has 96 points (100 MHz steps). A sweep ten times finer moves its
worst μ at the widened optimum from 1.0552 at 4.10 GHz to 1.0551 at 4.14 GHz.

**The filter's sweep was kept at 151 points (10 MHz steps) to keep each simulation cheap**, and that
setting matters. The optimizer only sees the grid, so it can only hold the passband at −0.5 dB at the
grid points. With 1,501 points (1 MHz steps) the Equiripple preset's worst passband point is
**−0.509 dB at 1.045 GHz**, between two of the coarse points. To hold the limit between grid points,
use the finer sweep and run again (each simulation costs ten times as much), or tighten the goal by
the difference.

The L-section's 21 points over 0.9–1.1 GHz are enough: 2,001 points over the goal's band give the same
worst point, −20.0 dB at 1.05 GHz.
