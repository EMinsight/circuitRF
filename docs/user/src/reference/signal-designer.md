---
title: The Signal Designer
slug: reference/signal-designer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Signal Designer
lede: The modulated signal a MOD_Source plays - LTE, NR, CW or your own I/Q file - with its spectrum, CCDF and EVM before you simulate.
keywords: Signal Designer, MOD_Source, modulated source, LTE, NR, OFDM, I/Q, IQ file, custom waveform, Load Custom, sample rate, PAPR, CCDF, clipping, ACLR, ACPR, EVM, constellation, envelope, export
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#opening">Opening the Designer</a></li>
<li><a href="#source">The Source box</a></li>
<li><a href="#presets">Presets</a></li>
<li><a href="#custom">A custom I/Q file</a></li>
<li><a href="#waveform">Clipping and spectral shaping</a></li>
<li><a href="#carriers">Carriers</a></li>
<li><a href="#preview">The spectrum and the CCDF</a></li>
<li><a href="#evm">The EVM tab</a></li>
<li><a href="#export">Export signal</a></li>
</ol>
</nav>

## Opening the Designer {#opening}

The Signal Designer edits the signal a [MOD_Source](components.html#modsource) plays in an
[Envelope analysis](simulations.html#envelope). Select the source and press **Design signal…** in the
inspector. The left of the window defines the signal; the right previews it. Every edit previews by
itself after a short pause.

Generation runs in the background — shown in the window's footer and on a live row of the Messages pane —
and is cached. A simulation of the same signal reuses the cached waveform instead of generating it again.
**Apply** writes the signal to the source as one undoable step; **Apply & Close** does the same and closes
the window. At the top of the left pane, **Export signal** saves the signal as an I/Q file and **?**
opens this page.

{{ui: signal-designer}}

## The Source box {#source}

- **Centre Fc** and **Pavg (dBm)** — the centre frequency the carriers sit around, and the total average
  available power behind the source impedance. Both accept a number or an expression (`RFfreq`, `Pin`),
  which the Designer writes back exactly as typed, so a swept source stays swept.
- **Record (ms)** — the periodic record length, in whole 0.5 ms slots. ACPR settles within about 0.1 dB
  from 0.5 ms; a longer record tightens the statistics.
- **Sample rate** — the rate of the preview and of the export. **Auto** picks the recommended rate for the
  signal (122.88 MS/s for LTE 20 MHz); the list holds the standard rates from 7.68 to 1966.08 MS/s, and,
  while a custom I/Q file is loaded, **File rate** — the file's own. The
  line under it shows the rate in use, the sample period and the samples per record. A rate that cannot
  hold the signal is refused, naming the lowest one that can. It is a Designer setting: it is not written
  to the source, and the simulation picks its own time step. It is not the
  [file sample rate](#custom) of a custom I/Q file, which says how the file was recorded.

## Presets {#presets}

One click replaces the carriers with a common case: **LTE 20 MHz**, **NR 20 MHz (30 kHz)**,
**NR 100 MHz (30 kHz)**, **4 x LTE 20 contiguous**, or **2C NR 20 at spacing** — two NR 20 MHz carriers at
the centre-to-centre spacing in the field beside it.

## A custom I/Q file {#custom}

The **Custom I/Q** card makes an I/Q file you supply the whole signal: a capture from an instrument, a
waveform you wrote yourself, or one you exported here earlier. Press the file button at the left of the
card and pick the file. It is played around `Fc` at the power `Pavg` sets, exactly as a generated signal
would be. While no file is loaded the card is empty and the carriers make the signal.

{{ui: signal-designer-custom}}

**The file.** Plain text, one sample per line: the in-phase and quadrature values, separated by spaces, a
tab or a comma. Lines starting with `#` or `%` are comments, and two of them are read:

- `# sample_rate_hz 200e6` — the rate the samples were taken at;
- `# channel_bw_hz 40e6` — the channel bandwidth. Without it, the bandwidth is measured from the file:
  twice the furthest edge of the band holding 99 % of the power, over 0.9.

The scale does not matter: the samples are normalised to unit mean power and `Pavg` sets the level. The
file is one periodic record, so its length is the record: 200,000 samples at 200 MS/s play a 1 ms
record, and the last sample wraps to the first.

**The Custom I/Q card** shows the file, its sample rate, and what was read: the samples, the record and the
channel bandwidth.

- **File sample rate (MHz)** is the rate the file's samples were recorded at. It is filled from the file's `# sample_rate_hz` line when it has one. A file with no
  such line cannot be played until you enter its rate here — the footer says so. A rate you enter wins
  over the header. Type a number in MHz (`122.88`) or a number with its own unit (`1.5 GHz`,
  `30720000 Hz`). An entry that is not a rate is outlined, its tooltip says why, and the signal keeps the
  last valid rate.
- The **×** at the right of the file removes it. The signal goes back to the carriers it had before you loaded the
  file (one LTE 20 MHz carrier if the source was opened on a file), and everything the file disabled is
  enabled again. **Apply** then takes the file's reference out of the schematic.

**What is disabled, and why.** While a custom file is loaded, the **Presets**, the **Carriers** and
**Record** are greyed: the file is the whole signal and its length is the record. **Spectral shaping** is
greyed too, as it shapes generated OFDM symbols. Everything else still applies:

- **Clip to PAPR** clips the file to a target peak-to-average ratio, by the same in-band peak cancellation
  a generated carrier gets;
- **Centre Fc**, **Pavg** and the preview's **Sample rate**;
- the spectrum, the CCDF and **Export signal**.

**Two sample rates, two jobs.** The **file sample rate** describes the file and is saved with the source.
The Source box's **Sample rate** is the rate the Designer previews and exports at, and is not saved. A
custom file is resampled to the Source rate for the preview and the export — band-limited and exact, since
the record is periodic — so a file recorded at 200 MS/s exported at 245.76 MS/s holds the same 1 ms
record in 245,760 samples. To preview and export the file at its own rate, with no resampling at all,
choose **File rate** in the Source box's list: it is offered, second after Auto, for as long as a custom
file is loaded, and follows the file sample rate when you change it. A Source rate that does not give a
whole number of samples over the file's record is refused.

The EVM tab has nothing to show for a custom file: it carries no reference symbols to compare against.

**Where the reference is kept.** The schematic stores the file the way it stores a Touchstone file:
relative to the workspace when the file is inside it, or at most two folders above it, and as a full path
otherwise, so a workspace you move or share keeps finding it. In the netlist the source's carrier reads:

```
Carrier[1]="FILE;File=iq/sc_64qam_40Msym.txt;Fs=200MHz;Off=0;P=0;Seed=1"
```

**Editing the file.** A file changed on disk is read again on the next preview or run; the cache never
serves the old one.

**In the Envelope analysis** the record is stepped at the usual LTE / NR rate (1.92 MHz × 2ᵏ) when that
divides it. When it does not — 12,345 samples at 100 MS/s, say — the step is the file's own rate × 2ᵏ,
which always gives a whole number of samples per record.

## Clipping and spectral shaping {#waveform}

- **Clip to PAPR (dB)** — off, the signal keeps its natural peak-to-average ratio (about 11–12 dB for
  OFDM). On, iterative in-band peak cancellation brings the peak down to the target, measured at 8×
  oversampling so peaks between samples count too. Clipping adds in-band error, which the EVM tab shows.
- **Spectral shaping floor (dBc)** — a ceiling outside the occupied band, so the source's own ACLR sits
  far below the amplifier's and what the simulation reports is the amplifier.

## Carriers {#carriers}

Each card is one carrier: its standard (**LTE**, **NR**, **CW** or **FILE**), channel bandwidth, NR
subcarrier spacing, modulation, offset from `Fc`, relative power and random-data seed. **+** adds a
carrier beside the last; **×** removes one. With a single carrier, **Power** is disabled — it is relative
to the others, and `Pavg` is the total. Carriers more than three channel bandwidths apart get their own
envelope fundamental in the simulation. Click a card to show that carrier on the EVM tab.

A **FILE** card puts an I/Q file beside generated carriers, at its own offset and relative power. Mixed
with LTE or NR carriers, the file must last a whole number of 0.5 ms slots.

{{ui: signal-designer-2c}}

## The spectrum and the CCDF {#preview}

The **Spectrum** is the composite power spectral density against the offset from `Fc`, framed on the
occupied channels. The **CCDF** is the probability that the instantaneous power exceeds the average by
more than a given margin, against the Gaussian-noise reference (dashed). A clipped signal's curve bends
down well inside the reference. Below the plots: the carrier count and instantaneous bandwidth, the record
and the rate, the PAPR at 0.01 % and the peak, and each carrier's ideal ACPR.

## The EVM tab {#evm}

For each LTE or NR carrier, the EVM tab demodulates the record the source plays (after clipping and
shaping) and compares every resource element with its ideal QAM point. One complex gain is fitted over the
record, with no per-subcarrier equaliser. The tab shows:

- the rms EVM in % and dB, and the peak EVM;
- which signal was measured: carrier, standard, bandwidth, modulation, offset, seed, record and clipping.
  The tab has no picker of its own. Click a carrier card on the left to show that carrier, and press
  **Calculate** to generate the signal on the left and measure it again;
- the chosen carrier's constellation over its ideal points;
- the envelope power around the record's peak, with the average and the peak as flat lines. A black arrow
  between them is labelled with the peak-to-average ratio. The **Window** field sets how long a stretch is
  shown (2 µs by default). It only redraws the plot, and Apply never writes it to the source.

An unclipped signal reads close to 0 %. Clipping is what moves it. CW and I/Q-file carriers carry no
reference symbols, so they have no EVM.

{{ui: signal-designer-evm}}

## Export signal {#export}

**Export signal** (the button at the top of the left pane) saves the signal as a two-column `I Q` text
file at a location you choose. It holds one record of the composite of all carriers, around `Fc`, at the
Source box's sample rate and unit mean power — a custom I/Q file is resampled to that rate, after
clipping. The header states the sample rate (in MS/s, Hz and sample period),
the sample count and record, Fc, Pavg, PAPR, clipping, shaping and each carrier, followed by the
`# sample_rate_hz`, `# channel_bw_hz`, `# samples` and `# columns I Q` lines. This is the format
**Load custom** reads, so an exported signal loads back unchanged.
