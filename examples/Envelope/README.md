# Envelope

The 48 V GaN power amplifier from the Harmonic Balance example, driven by a **modulated** signal —
one NR 20 MHz carrier (30 kHz subcarriers, 64QAM, unclipped) — and simulated with the **Envelope
(modulated)** analysis. The FET is the same reusable sub-cell.

## The source: `SRC1`, a MOD_Source

A `MOD_Source` plays a modulated signal around `Fc` at an average power `Pavg`, behind `Z`. Each
`Carrier[i]` is one line of text describing one carrier — standard, bandwidth, subcarrier spacing,
modulation, offset from `Fc`, relative power and random seed:

    NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1

Select it and press **Design signal…** in the inspector to edit the carriers in the Signal
Designer: presets (LTE 20, NR 20, NR 100, two carriers at any spacing, four contiguous LTE), the
spectrum and CCDF of the signal, and its PAPR and ideal ACLR. The signal is generated the first time
it is needed and cached, so a sweep never generates it twice. `Fc = RFfreq` and `Pavg = Pin` are
expressions, so the variables block drives them and a sweep of `Pin` sweeps the drive.

## NR20_ACPR

One envelope run: 200 µs of the signal at `Pin` = 20 dBm average. The step is chosen from the
signal's sample rate (`Accuracy = standard` is four steps per sample), and the record is split into
time segments that run in parallel.

The run records a voltage at every labeled net (`VDD`, `Vin`, `Vout`) and a power at every IProbe:
`Iout` in front of the output DC block and its 50 Ω load, `Iin` at the gate, `IDC` in the drain supply.

| Cube | At | Shows |
|---|---|---|
| `V`, `VSD` | each net | the complex envelope at every harmonic of `Fc` against time, and the voltage spectrum (dBV/Hz) |
| `PSD`, `ACLR` | each IProbe | the delivered power spectrum (dBm/Hz); channel power and ACPR1/ACPR2, lower and upper (dBc) |
| `Pavg_dBm`, `Gt_dB`, `DE_pct`, `NMSE_dB`, `PAPR_dB` | each IProbe | at `Iout`: output power, gain, drain efficiency, error, PAPR |
| `AMAM`, `AMPM` (and `_min`, `_max`) | each IProbe | gain and phase against instantaneous input power |
| `Gt_t_dB`, `Pin_inst_t_dBm` | each IProbe | the memory cloud: every sample's gain and drive |
| `Pin_avg_dBm`, `Pdc_W` | the run | the available drive and the DC power |

The `MeasPA` block adds three measure lines from the probe powers: `Pin_del_dBm`, the power delivered
into the gate network; `Gp_dB`, the power gain from that to the load; and `PAE_pct`.

Simulate opens **NR20_ACPR.cdd**, the display laid out beside the bench: the power spectrum delivered at
`Iout` and `Iin` with the voltage spectrum at `Vout` on the right axis, AM-AM (mean, minimum and maximum
gain in each 0.5 dB input-power bin) and AM-PM at `Iout`, 2 µs of the envelope at the fundamental, the
power, gain and efficiency table with the three measure lines, ACLR, and the memory cloud.

The figures at `Iin` are the power the gate network actually takes in. Its adjacent channels read blank:
the distortion there flows from the device back toward the source, which is a power flowing the other way
through the probe.

## NR20_ACPR_vs_Pin

The same bench with the envelope inside a parametric sweep of `Pin`, 10 → 26 dBm in 4 dB steps:
ACPR1 and ACPR2 against drive, the curve a linearity budget is read from. The five points run at the
same time, each split into fewer time segments so they share the cores. **NR20_ACPR_vs_Pin.cdd** plots
ACPR1/ACPR2 (both sides), output power and gain, drain efficiency and DC power against `Pin`, all at
`Iout`, and the AM-AM of every drive level on one axis — they lie on one curve, because AM-AM against the
instantaneous input power does not depend on the average drive. The table lists the `MeasPA` lines
against `Pin` as well: a measure line stacks over a sweep with no edit. The memory plot draws one cloud
per drive level.

## Two carriers far apart

Change the source to two carriers (the Signal Designer's **2C NR 20 at spacing** preset, e.g. 400 MHz)
and run again. Carriers more than three channel bandwidths apart each get their own envelope
fundamental, so the step stays at the single carrier's rate however wide the spacing is, and the
`IMD` cube reports the carriers, IM3, IM5, IM2 and the second harmonics. Keep `Max mix order` at 7:
at 5 the IM5 products are truncated by about a decibel.

To simulate the two carriers as ONE envelope instead — the whole signal in a single time vector around
`Fc`, spanning both carriers and their IM3 — set **Envelope** to `single` on the analysis card. The step
then follows the spacing rather than the channel: about 16× more steps at 400 MHz, so use `fine`.
Carriers, ACPR, IM3L and IM2 agree with the default lattice run. Products far from `Fc` (IM5, and
IM3 on the far side) depend on where the single envelope is centred, so read those from the lattice.

## `NR20_2C_400MHz_single`: two NR 20 carriers 400 MHz apart on a narrowband test PA

This cell is a ready-made two-carrier case: NR 20 + NR 20 at ±200 MHz around 2 GHz, Pin 14 dBm, one
envelope (`Envelope=single`). It uses a different amplifier from the cells above, `TestPA_FET`. The drain
current is the same, and the cell adds the gate and drain charges, Cgd and Cds, and a 50 Ω gate shunt.
The output is narrowband: a 20 nH drain line into a 5 nF bypass, then 20 nH + 0.3166 pF in series,
resonant at 2.0 GHz, into 50 Ω. Its gain therefore falls away on both sides of 2 GHz, unevenly, and the
two carriers come out about 5 dB apart at the load (C1 ≈ 18.7 dBm, C2 ≈ 23.5 dBm). A broadband PA would
put them within a decibel. `Vout` is the load and `Vd` the drain. `Iout` sits in front of the load, so
its power, ACLR, AM-AM and IMD are what reaches the 50 Ω.
