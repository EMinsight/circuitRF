---
title: The Signal Designer
slug: reference/signal-designer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Signal Designer
lede: The signal a MOD_Source plays - LTE, NR, CW, a pulsed-CW train, FSK or your own I/Q file - with its spectrum, CCDF and EVM before you simulate.
keywords: Signal Designer, MOD_Source, pulsed CW, pCW, pulse, radar, PRI, PRF, duty, pulse-top power, FSK, CPFSK, GFSK, GMSK, MSK, PN9, PN15, wireless M-Bus, metering, modulated source, LTE, NR, OFDM, I/Q, IQ file, custom waveform, Load Custom, sample rate, PAPR, CCDF, clipping, ACLR, ACPR, EVM, constellation, envelope, export
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
<li><a href="#pulse">Pulsed CW</a></li>
<li><a href="#fsk">FSK</a></li>
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

**Tools ▸ Signal Designer** opens the Designer on its own, with no source behind it and no workspace
needed. It starts on one LTE 20 MHz carrier. Design a signal there, or read one in with
[Load custom](#custom) to see its spectrum and CCDF, and **Export signal** to save it as an I/Q file.
There is nothing to apply it to, so **Apply** and **Apply & Close** are not shown, and a file you load is
remembered by its full path. Several can be open at once, and each is listed in the **Window** menu.

{{ui: signal-designer}}

## The Source box {#source}

- **Centre Fc** and **Pavg (dBm)** — the centre frequency the carriers sit around, and the total average
  available power behind the source impedance. Both accept a number or an expression (`RFfreq`, `Pin`),
  which the Designer writes back exactly as typed, so a swept source stays swept. For a
  [pulsed-CW](#pulse) source the field reads **Pulse-top power (dBm)**: the power on the flat top of the
  pulse, with the record average and the power duty shown beneath it.
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
the centre-to-centre spacing in the field beside it. **Pulsed CW** is one [pulse train](#pulse), and
**Wireless M-Bus** opens a menu of [FSK](#fsk) metering modes.

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

Each card is one carrier: its standard (**LTE**, **NR**, **CW**, **PCW**, **FSK** or **FILE**), channel bandwidth, NR
subcarrier spacing, modulation, offset from `Fc`, relative power and random-data seed. **+** adds a
carrier beside the last; **×** removes one. With a single carrier, **Power** is disabled — it is relative
to the others, and `Pavg` is the total. Carriers more than three channel bandwidths apart get their own
envelope fundamental in the simulation. Click a card to show that carrier on the EVM tab.

A **FILE** card puts an I/Q file beside generated carriers, at its own offset and relative power. Mixed
with LTE or NR carriers, the file must last a whole number of 0.5 ms slots.

{{ui: signal-designer-2c}}

## Pulsed CW {#pulse}

A **PCW** card is a pulse train: a CW carrier switched on and off, the stimulus a pulsed-radar amplifier is
specified with. The **Pulsed CW** preset replaces the carriers with one, or choose **PCW** in a card's standard
list. The record is one pulse repetition interval, which repeats, so the Envelope analysis's
**Stop** sets how many pulses are simulated. Its fields take a number in µs, or a number with its unit
(`50 us`, `0.5 ms`, `500 ns`):

| Field | Meaning | Default |
|---|---|---|
| **PRI** | pulse repetition interval. **PRF** is shown beside it | 100 µs |
| **Width** | pulse width, from the 50 % point of the rising edge to the 50 % point of the falling edge. The duty is shown beside it | 10 µs |
| **Rise**, **Fall** | the 10–90 % times of the raised-cosine edges | 100 ns |
| **Delay** | the off-time before the rising edge leaves zero. Empty centres the pulse in the PRI, with equal off-time either side | centred |

The envelope is exactly zero in the off-time and exactly one on the flat top. A pulse that cannot be
generated is refused on the field it names: a width as long as the PRI, an edge longer than half the width,
a pulse that ends past the PRI, a zero Delay, or a PRI that no 1.92 MHz × 2ᵏ sample rate divides into
whole samples. Delay must be greater than zero because an envelope run starts from harmonic balance at
the first sample of the record: in the off-time that is the quiescent point, so the first pulse starts
cold.

**Power is the pulse top.** A pulsed-CW record is normalised to unit power on the flat top, so the
source's `Pavg` is the pulse-top available power — the number a radar specification states — and
`Pin_avg_dBm` and `Pout_avg_dBm` in the results stay averages over the whole run, off-time included. Harmonic balance
and loadpull play a MOD_Source as a CW tone at `Pavg`, which for a pulse is the top: the steady state of
a pulse that never ends.

**Its bandwidth is its edges'.** The faster edge sets the carrier's bandwidth (twice the band holding
99 % of a raised-cosine edge's power, over 0.9, as an I/Q file's is measured), and the envelope rate, the
preview's span and the sample rate follow from it. The native rate is the smallest 1.92 MHz × 2ᵏ that
puts at least ten samples in the faster edge and a whole number in the PRI.

A pulsed carrier is the source's only carrier: a pulse beside a modulated carrier has no clear power or
record. With one on the card, **Record**, **Clip to PAPR**, **Spectral shaping** and **+** are disabled:
the record is the PRI, and a pulse is neither clipped nor spectrally shaped. A pulse draws no random data,
so its card has no **Seed**.

In the netlist the card is

```
Carrier[1]="PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0"
```

and two keys the card does not show set the [droop window](simulations.html#envelope-pulse):
`MeasureStart` and `MeasureEnd`, fractions of the flat top (0.1 and 0.9 by default).

Not modelled: modulation inside the pulse (linear FM chirps and phase codes, which real radar pulses
usually carry — the natural next addition), and pulsing the gate or drain bias with the RF, which is a
pulsed bias source rather than a signal.

## FSK {#fsk}

An **FSK** card is binary frequency-shift keying with continuous phase: each bit holds the frequency at
`Fc + Dev` (a 1) or `Fc − Dev` (a 0) for one bit period, and the phase never jumps. Choose **FSK** in a
card's standard list, or a mode from the **Wireless M-Bus** preset.

| Field | Meaning | Default |
|---|---|---|
| **Bit rate (kb/s)** | the bit (chip) rate, Rb | 100 |
| **Deviation (kHz)** | the peak frequency deviation, Dev | 45 |
| **Filter** | **None (CPFSK)**: the frequency steps at each bit edge. **Gaussian**: the steps are smoothed by a Gaussian filter of bandwidth-time product **BT** | None |
| **Data** | **PN9** (511 bits), **PN15** (32,767 bits), or **Fixed bits**, a pattern of 0s and 1s you type | PN9 |

The line under the fields gives the modulation index h = 2 Dev / Rb, what the settings make, the bits
and the record. With a Gaussian filter it is **GFSK**, and with h = 0.5 it is **GMSK**: GSM's is
270.833 kb/s, ±67.708 kHz, BT 0.3. Without the filter h = 0.5 is MSK.

The **Wireless M-Bus** menu (EN 13757-4) sets one FSK carrier on PN9 for a metering mode and moves
**Centre Fc** to its channel. If Fc is an expression, the menu leaves it as you typed it.

| Mode | Direction | Fc | Chip rate | Deviation | Filter |
|---|---|---|---|---|---|
| C1 | meter → other | 868.95 MHz | 100 kchip/s | ±45 kHz | Gaussian, BT 0.5 |
| C2 | other → meter | 869.525 MHz | 50 kchip/s | ±25 kHz | Gaussian, BT 0.5 |
| T1 | meter → other | 868.95 MHz | 100 kchip/s | ±50 kHz | none |
| T2 | other → meter | 868.3 MHz | 32.768 kchip/s | ±50 kHz | none |

The data is a test pattern, not a telegram. There is no preamble, sync word or 3-out-of-6 coding,
because an RF front end responds to the modulation and does not depend on what the bits mean.

**The record is one period of the data**: 5.11 ms for PN9 at 100 kb/s, 80 µs for an eight-bit pattern at
the same rate. It repeats, so the Envelope analysis's **Stop** sets how much is simulated. Over a record
the phase gains π h for each 1 and loses it for each 0, and that rarely adds up to a whole turn: PN9 has one
more 1 than 0s. So the record is closed by moving the whole carrier by at most half of one over the record,
98 Hz for PN9 at 100 kb/s. The seam is then as smooth as any bit edge.

**Bandwidth and rate.** The carrier's channel bandwidth is Carson's, 2 (Dev + Rb): 290 kHz for C1. The
preview's span, the envelope rate and the ACPR bands follow from it. The record is sampled at Rb × 2ᵏ,
at least 32 samples per bit and eight times the bandwidth. A 511-bit record holds a whole number of
samples at none of the standard sample rates, so while an FSK carrier is on the card the Source box's
list offers **Native rate**, second after Auto, and Auto uses it.

FSK has a constant envelope: its PAPR is 0 dB, and its CCDF is a step at 0 dB. As with a pulse, it is
the source's only carrier, and **Record**, **Clip to PAPR**, **Spectral shaping** and **+** are
disabled. A PN sequence draws no random data, so the card has no **Seed**. The second preview tab reads
**Frequency** and shows the instantaneous frequency over the first 64 bits, where the Gaussian
filter's rounding of each bit edge can be seen.

In the netlist the card is

```
Carrier[1]="FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0"
```

Not modelled: transmitting in bursts with a duty cycle (a metering transmitter sends a few milliseconds
every several seconds), and AM or PM.

## The spectrum and the CCDF {#preview}

The **Spectrum** is the composite power spectral density against the offset from `Fc`, framed on the
occupied channels; for a pulsed-CW source it is the train's. The **CCDF** is the probability that the instantaneous power exceeds the average by
more than a given margin, against the Gaussian-noise reference (dashed). A clipped signal's curve bends
down well inside the reference. A pulse train's is a step: flat at about its duty, then nothing past its peak-to-average,
which is 1 / its power duty (10 dB at 10 %). Below the plots: the carrier count and instantaneous bandwidth, the record
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
reference symbols, so they have no EVM. A pulsed-CW signal has none either: for one, this tab reads
**Pulse** and holds the pulse itself, |envelope| against time over one PRI, which zooms onto an edge like
any plot. For an [FSK](#fsk) signal it reads **Frequency** and holds the instantaneous frequency.

{{ui: signal-designer-evm}}

## Export signal {#export}

**Export signal** (the button at the top of the left pane) saves the signal as a two-column `I Q` text
file at a location you choose. It holds one record of the composite of all carriers, around `Fc`, at the
Source box's sample rate and unit mean power — a custom I/Q file is resampled to that rate, after
clipping. The header states the sample rate (in MS/s, Hz and sample period),
the sample count and record, Fc, Pavg, PAPR, clipping, shaping and each carrier, followed by the
`# sample_rate_hz`, `# channel_bw_hz`, `# samples` and `# columns I Q` lines. This is the format
**Load custom** reads, so an exported signal loads back unchanged.
