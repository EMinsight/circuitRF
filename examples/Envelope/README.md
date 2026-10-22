# Envelope

The 48 V GaN power amplifier from the Harmonic Balance example, driven by a **modulated** signal —
one NR 20 MHz carrier (30 kHz subcarriers, 64QAM, unclipped) — and simulated with the **Envelope
(modulated)** analysis. The FET is the same reusable sub-cell.

## The source: `SRC1`, a MOD_Source

A `MOD_Source` plays a modulated signal around `Fc` at an average power `Pavg`, behind `Z`. Each
`Carrier[i]` is one line of text describing one carrier — standard, bandwidth, subcarrier spacing,
modulation, offset from `Fc`, relative power and random seed:

    NR;BW=20e6;SCS=30e3;Mod=64QAM;Off=0;P=0;Seed=1

Select it and press **Design signal…** in the inspector to edit the carriers in the Signal
Designer: presets (LTE 20, NR 20, NR 100, two carriers at any spacing, four contiguous LTE), the
spectrum and CCDF of the signal, and its PAPR and ideal ACLR. The signal is generated the first time
it is needed and cached, so a sweep never generates it twice. `Fc = RFfreq` and `Pavg = Pin` are
expressions, so the variables block drives them and a sweep of `Pin` sweeps the drive.

## NR20_ACPR

One envelope run: 200 µs of the signal at `Pin` = 20 dBm average. The step is chosen from the
signal's sample rate (`Accuracy = standard` is four steps per sample), and the record is split into
time segments that run in parallel. The result holds, per probe (`Vin`, `Vout`):

| Cube | Shows |
|---|---|
| `ACLR` | channel power and ACPR1/ACPR2, lower and upper, per carrier (dBc) |
| `PSD` | the output spectrum (dBm/Hz) |
| `V` | the complex envelope at every harmonic of `Fc`, against time |
| `AMAM`, `AMPM` | gain and phase against instantaneous input power |
| `Pout_avg_dBm`, `Gt_dB`, `Pdc_W`, `DE_pct`, `NMSE_dB` | the scalars a PA designer reads first |

Simulate opens **NR20_ACPR.cdd**, the display laid out beside the bench: the spectrum at the drain and
the gate, AM-AM (mean, minimum and maximum gain in each 0.5 dB input-power bin), AM-PM, and 2 µs of the
envelope at the fundamental.

## NR20_ACPR_vs_Pin

The same bench with the envelope inside a parametric sweep of `Pin`, 10 → 26 dBm in 4 dB steps:
ACPR1 and ACPR2 against drive, the curve a linearity budget is read from. The sweep points run in
parallel with each other. **NR20_ACPR_vs_Pin.cdd** plots ACPR1/ACPR2 (both sides), output power and gain,
drain efficiency and DC power against `Pin`, and the AM-AM of every drive level on one axis — they lie on
one curve, because AM-AM against the instantaneous input power does not depend on the average drive.

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
put them within a decibel. `Vout` is the load and `Vd` the drain; the display reads `Vout`.
