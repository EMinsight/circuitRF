---
title: The WSProbe
slug: reference/wsprobe.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > The WSProbe
lede: Nodal transfer functions for stability and in-situ design — the wsp matrix, the driving-point functions, the bidirectional impedances, the published stability margin, and an appendix on stability itself.
keywords: WSProbe, Winslow, stability, driving point impedance, H0, Y0, loop gain, bidirectional impedance, NDF, Ohtomo, Kurokawa, envelope, stability margin, return difference, Rollett, Barkhausen, Bode, Middlebrook, Tian, bifurcation, normalized determinant function
---

A **WSProbe** is a two-terminal element you drop into a node. It is electrically a 0 V short, it
changes no other answer in the run, and what it produces is not a number but a matrix — from which
every stability quantity on this page is arithmetic.

circuitRF implements T. A. Winslow, *General Circuit Analysis Using The WSProbe* (2023), and the two
EuMIC papers that followed it. **Every quantity below carries that work's own name and its own
equation number**, so a reader who has the documents can find `H0`, `Y0`, `ZG`, `ZL`, `LG`, `F`,
`SM_Y0`, `SM_H0` and `wsp(2i−1, 2j)` here under exactly those spellings. Citations are `(Eq. n)` for
the 2023 document, `(M-Eq. n)` for the 2024 stability-margin paper and `(E-Eq. n)` for the 2025
envelope paper; all three are listed in [References](#references).

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What the WSProbe is</a></li>
<li><a href="#placing">Placing it</a></li>
<li><a href="#computes">What it computes</a></li>
<li><a href="#reading">Reading the results</a></li>
<li><a href="#stability-margin">The stability margin</a></li>
<li><a href="#metrics">The derived metrics</a></li>
<li><a href="#pairs">Probe pairs: a block in situ</a></li>
<li><a href="#global">Global stability: bifurcation and Ohtomo</a></li>
<li><a href="#envelope">The stability envelope</a></li>
<li><a href="#hb">Under harmonic balance</a></li>
<li><a href="#ndf">NDF — the normalized determinant function</a></li>
<li><a href="#headless">From the command line</a></li>
<li><a href="#caveats">Caveats, in the documents' own words</a></li>
<li><a href="#references">References</a></li>
<li><a href="#appendix-stability">Appendix A — Stability, from the beginning</a></li>
</ol>
</nav>

## What the WSProbe is {#what}

{{symbol: wsprobe}}

Two terminals and an orientation: **`G`** is the generator side and **`L`** the load side. Placed
into a node, it splits that node into a G-side terminal and an L-side terminal, exactly as an
[IProbe](components.html#iprobe) does. Electrically it is a 0 V short.

**It perturbs nothing, and it needs one analysis sweep** (§4, p. 33: "completely nonperturbative …
requiring only a single analysis sweep"). A design simulates identically with every WSProbe deleted,
and the S-parameters, the operating point and every measurement of a probed run are the numbers the
unprobed run gives. What changes is only what the run *reports*.

What it reports is the **`wsp` matrix**. For each probe the analysis applies, mathematically, two
vanishingly small auxiliary generators — a series voltage `vS` in the probe's own branch and a shunt
current `iP` into its G-side node — and reads, at *every* probe, two responses: the branch current
`iS` flowing G → L, and the G-side node voltage `vP`.

<figure class="fig">
<svg viewBox="0 0 640 250" role="img" aria-label="The two injections a WSProbe applies and the two responses it reads" style="width:100%;height:auto">
  <g fill="none" stroke="var(--text)" stroke-width="1.6" stroke-linecap="round">
    <!-- G side network box -->
    <rect x="24" y="70" width="120" height="90" rx="6" stroke="var(--border)" stroke-width="1.4" fill="var(--surface)"/>
    <!-- L side network box -->
    <rect x="496" y="70" width="120" height="90" rx="6" stroke="var(--border)" stroke-width="1.4" fill="var(--surface)"/>
    <!-- the probe branch -->
    <path d="M144 100 H 268"/>
    <path d="M372 100 H 496"/>
    <!-- series source symbol -->
    <circle cx="320" cy="100" r="26"/>
    <path d="M294 100 H 268 M346 100 H 372"/>
    <path d="M304 100 H 316 M310 94 V 106" stroke-width="1.4"/>
    <path d="M328 100 H 340" stroke-width="1.4"/>
    <!-- shunt current source into the G node -->
    <path d="M204 100 V 200"/>
    <circle cx="204" cy="200" r="22"/>
    <path d="M204 222 V 236 H 190 M204 236 H 218" stroke-width="1.4"/>
    <path d="M204 214 V 186 M197 194 L204 186 L211 194" stroke-width="1.4"/>
    <!-- node dots -->
    <circle cx="204" cy="100" r="4" fill="var(--text)" stroke="none"/>
    <circle cx="440" cy="100" r="4" fill="var(--text)" stroke="none"/>
  </g>
  <g fill="var(--text)" font-family="var(--font-sans)" font-size="14">
    <text x="84" y="111" text-anchor="middle" fill="var(--muted)">G-side</text>
    <text x="84" y="129" text-anchor="middle" fill="var(--muted)">network</text>
    <text x="556" y="111" text-anchor="middle" fill="var(--muted)">L-side</text>
    <text x="556" y="129" text-anchor="middle" fill="var(--muted)">network</text>
    <text x="320" y="66" text-anchor="middle">vS</text>
    <text x="234" y="205">iP</text>
    <text x="180" y="90" text-anchor="end">G</text>
    <text x="462" y="90">L</text>
    <text x="204" y="82" text-anchor="middle" fill="var(--accent)">vP</text>
    <text x="418" y="90" text-anchor="end" fill="var(--accent)">iS →</text>
  </g>
  <g stroke="var(--accent)" stroke-width="1.4" fill="none">
    <path d="M404 100 h 22 m-8 -5 l 8 5 l -8 5"/>
  </g>
</svg>
<figcaption>The two injections and the two readings, for one probe. <code>vS</code> is a series
voltage in the probe's own branch, minus at <code>G</code> and plus at <code>L</code>;
<code>iP</code> is a shunt current injected into the G-side node. The responses read at
<em>every</em> probe are the branch current <code>iS</code>, flowing G to L, and the G-side node
voltage <code>vP</code>. (Redrawn for this page; the reference document's Fig. 16 is the original.)</figcaption>
</figure>

Normalising those four combinations gives the four transfer functions per probe pair, and with `N`
probes they stack into one **2N × 2N matrix, 1-based, rows = stimulus probe, columns = response
probe** (Eq. 31–36).

## Placing it {#placing}

Drop it onto the wire whose node you want to look at. The wire is **cut** between its two pins,
exactly as it is for an [IProbe](components.html#iprobe) — the probe breaks into the run instead of
being shorted by it.

**The letters are the orientation, and they are the pin order.** Rotating or mirroring the symbol
moves the drawing and never the pin order, so swapping `G` and `L` means swapping the *wires* —
which swaps every `G`/`L`-labelled output and negates nothing else.

**Where to put probes.** The reference document's own advice (§4, p. 36): on the terminals of active
devices — the nodes "where the transfer functions have the greatest chance of detecting … the
Kurokawa signature". A gate node, a drain node, and the two terminations if you intend to use
[the envelope](#envelope).

**The label is the instance name**, and a probe inside a sub-cell carries its full instance path:
`X1.GATE`. Probes work at any depth of hierarchy (§4.3).

In a [netlist](netlist.html) the spelling is two nets, `G` first:

```text
WSProbe:GATE   n_gate   n_gate_int
```

Each probe is given an **`idx`** at elaboration, in flattened netlist order, 1-based. That number is
what the document's equations index by and what a `measure` line types, so it is reported rather than
guessed: `circuitrf explain --analysis` lists it, the trace card shows it beside every probe label,
and the run writes a `__WspProbes` metadata cube carrying the label ↔ idx pairs.

## What it computes {#computes}

The **`wsp` matrix** (Eq. 31–34), for probes `i` (stimulus) and `j` (response):

```text
wsp(2i−1, 2j−1) = iS_j / vS_i      series current response to series voltage    (= Y0 when i = j)
wsp(2i−1, 2j  ) = vP_j / vS_i      shunt voltage   response to series voltage
wsp(2i,   2j−1) = iS_j / iP_i      series current  response to shunt current
wsp(2i,   2j  ) = vP_j / iP_i      shunt voltage   response to shunt current    (= H0 when i = j)
```

**The indexing rule is stated once and then used everywhere** (Eq. 33): odd rows and columns are the
series half, even rows and columns the shunt half; the row is the probe that was driven and the
column is the probe that was read. The matrix is stored 1-based with integer `row`/`col` axis
values, so `wsp(3, 13)` in the document is `wsp(3, 13)` here with no index arithmetic in between:

```text
SP1.wsp                → the whole cube, over {freq, row, col}
SP1.wsp(3, 13)         → one element traced over frequency        (Eq. 36)
SP1.idx("GATE")        → the probe's idx, as an integer
```

### The six default outputs

Every probe writes six named results, computed by the engine from `wsp` at run time — through the
same library functions the trace card and a `measure` line call, so a run's `ZG:GATE` cube and a
card's `wsp_ZG` of the same probe are bit-identical by construction:

| Output | What it is | Equation |
|---|---|---|
| `H0` | driving-point impedance at the node, `vP/iP` | Eq. 50 |
| `Y0` | driving-point admittance in the branch, `iS/vS` | Eq. 52 |
| `ZG` | the impedance looking out of `G`, under series stimulus | Eq. 67 |
| `ZL` | the impedance looking out of `L`, under series stimulus | Eq. 68 |
| `LG` | the bilateral (Tian) loop gain around the probe | Eq. 92 |
| `F` | the return difference, `F = 1 − LG` | — |

Two more come with them — `SM_Y0` and `SM_H0`, [the stability margin](#stability-margin).

Their cubes are named the way an ammeter's are (`I:IP1`): `H0:GATE`, `Y0:GATE`, `ZG:GATE`, `ZL:GATE`,
`LG:GATE`, `F:GATE`, `SM_Y0:GATE`, `SM_H0:GATE`, each over `{freq}`, with any parametric sweep axis
prepended.

<div class="callout note">
<span class="label">Everything else is arithmetic on one 2 × 2 block</span>
<p>The four entries of one probe's own diagonal block reduce it to a <b>two-port</b> — port 1 the
G-side terminal, port 2 the L-side terminal — as an admittance matrix (Eq. 44) or an impedance
matrix (Eq. 48). <code>ZG</code>, <code>ZL</code>, <code>YG</code>, <code>YL</code>, every loop gain,
the open-port immittances and both stability margins are a few lines over that two-port. So is
<a href="derived-metrics.html#stability">Rollett's K, μ, |Δ|, MAG/MSG and the stability
circles</a> — the reduced two-port is an ordinary network source, which is what lets you apply a
two-port stability criterion to a stage <em>inside</em> an amplifier.</p>
</div>

## Reading the results {#reading}

### The primary metric: two polar plots

Plot **`1/H0`** and **`1/Y0`** on a polar chart and look for **Kurokawa's start-up signature**
(Eq. 107 and Eq. 108). In words, three conditions at one frequency:

1. the real part is zero or negative;
2. the imaginary part is zero — the locus is *on* the negative real axis;
3. the imaginary part is **increasing** with frequency — the crossing is **clockwise**.

The third is the whole test. A counter-clockwise crossing is the steady-state side of an ordinary,
*stable* resonance, and reporting it would flag every resonant circuit ever drawn.

**Check both.** A zero can mask the pole in one of the two functions, but never in both (§4.10,
pp. 73–79). The document's own pair of examples is the demonstration: an unstable **series**
resonator shows the signature in `1/Y0` alone, and an unstable **parallel** resonator in `1/H0`
alone.

{{ui: wsprobe-resonator-polar}}

**The trace card does the looking for you.** Pick `1/H0` or `1/Y0` in the card's WSProbe section and
it reports the crossing frequencies it found — or `none`, which is a real answer and is printed as
one. **Mark crossings** puts a marker at each.

{{ui: plot-inspector-wsprobe}}

### The loop gains

Every loop gain on this page — including the probe's own default `LG` and its return difference
`F = 1 − LG` — is offered, and every one of them carries the reference document's caveat at full
weight (§4.4, p. 50):

> Loop gains "are not fundamental circuit quantities and are therefore not rigorous stability
> measurements … not necessarily wrong, just incomplete."

They are diagnostic and they are useful. They are not the answer. The driving-point functions are,
because `H0` and `Y0` carry the whole network determinant in their denominators (Eq. 26–30) and a
loop gain does not.

The case that makes this concrete is the one with **no feedback at all**: two blocks joined only
through the probe. Every loop gain there is exactly 0 — there is no loop — and yet the node can
oscillate, because the instability is between the two blocks rather than around a loop. That is
Kurokawa's own case (pp. 62–63), and what carries the signature is the **open-port immittances**
`Zop` and `Yop` (Eq. 89, 90) and the driving-point functions themselves.

### The bidirectional impedances

`ZG` and `ZL` are the impedances the two sides present under **series-voltage** stimulation. `YG` and
`YL` (Eq. 72–78) are the admittances they present under **shunt-current** stimulation.

<div class="callout warning">
<span class="label">ZG is not 1/YG</span>
<p>They agree only when there is <em>no feedback across the probe</em> — <code>y12 = y21 = 0</code>
(Eq. 79–82; §4.5, p. 56). Reading one as the reciprocal of the other is the single most common
misreading of this probe. Only the <b>sums</b> are fundamental:
<code>ZG + ZL = 1/Y0</code> (Eq. 69) and <code>YG + YL = 1/H0</code> (Eq. 77).</p>
<p>Feedback around a probe means a path joining its two sides <em>other than through the probe
itself</em>. Ground does not count: two one-ports that share only the reference have
<code>z12 = 0</code>, and a probe with a dangling one-port on its G side has
<code>ZG = 1/YG</code> exactly however much feedback the rest of the network carries.</p>
</div>

## The stability margin {#stability-margin}

A driving-point locus answers *yes* or *no*. It cannot answer **how close**, and the reason is that
it has **units**: `1/H0` is an admittance and its trajectory in the complex plane is set by the
node's impedance level, so two devices of very different periphery tuned to the same Rollett `K`
give loci differing by orders of magnitude. That is the problem the 2024 paper [M] set out to fix.

Its starting point is that both driving-point functions are **sums of two bidirectional
immittances**:

```text
1/H0 = YG + YL          (M-Eq. 1)
1/Y0 = ZG + ZL          (M-Eq. 2)
```

and that Kurokawa's condition on each is a statement about the *relative* size of the two halves —
the real parts cancelling, the imaginary parts cancelling. Normalising each half against the other
gives four bounded, unitless proxies (M-rY, M-iY, M-rH, M-iH):

```text
rY = 0                          if  Re ZG + Re ZL ≤ 0
   = ½ (1 + Re ZL / Re ZG)      if |Re ZG| ≥ |Re ZL|
   = ½ (1 + Re ZG / Re ZL)      otherwise

iY = ½ (1 + Im ZL / Im ZG)      if |Im ZG| ≥ |Im ZL|
   = ½ (1 + Im ZG / Im ZL)      otherwise

rH, iH:  the same two functions over Re YG, Re YL and over Im YG, Im YL
```

and the two margins are their means:

```text
SM_Y0 = ½ (rY + iY)             (M-Eq. 9)
SM_H0 = ½ (rH + iH)             (M-Eq. 10)
```

**Both are required** ([M] §IV). `SM_Y0` is the margin on the series stimulus and `SM_H0` on the
shunt one; a series-resonant instability is seen by one and a parallel-resonant one by the other.
That is the pole masking of [§4](#reading) again, in margin form.

### How to read it

- **It is bounded, `[0, 1]`, and unitless.** Scaling `ZG` and `ZL` by the same positive real leaves
  `SM_Y0` unchanged, which is the cross-node comparability the margin exists for.
- **dB here is `20·log10`** — the ordinary `dB(…)` transform, the one circuitRF applies to every
  unitless magnitude. So [M]'s rule of thumb, *investigate any sudden decrease below −15 dB*, is
  **0.178** linear. (Under a `10·log10` convention the same rule would read 0.032; both numbers are
  printed here so that nobody is misled silently by whichever convention they assumed.)
- **A conjugate-matched node reads −6 dB, not 0.** `SM_Y0 = 1` means `ZL = ZG` with positive real
  part; `ZL = conj(ZG)` gives `rY = 1`, `iY = 0` and `SM_Y0 = 0.5`, which is −6.02 dB. A designer who
  expects a matched node to top the scale will read a perfectly good node as a problem.
- **Below −12 dB, one side of the node presents negative resistance.** If `Re ZG > 0` and
  `Re ZL > 0` then `rY` is above 0.5 and `SM_Y0 ≥ 0.25`, which is −12.04 dB. So anything under that
  line *certifies* negative resistance at that frequency — it does not merely suggest it.
- **The margin is a distance; the crossing search is the detector.** Kurokawa's third condition —
  the clockwise sense — is deliberately not in the margin, which measures distance to the first two
  only. So the trace card prints a margin's minimum **and** the Kurokawa reading of the driving-point
  function that pairs with it: `SM_Y0` with `1/Y0`, `SM_H0` with `1/H0`. Reading one without the
  other is how a −40 dB margin at a frequency nothing actually crosses gets mistaken for an
  oscillation.
- **A probe against a purely resistive termination reads the resonance on the other side only.** The
  proxy normalises one reactance *by the other*; a resistive side has none to offer, so `iY` sits at
  0.5 and the margin on that stimulus is flat. That is the definition working, not a defect — but it
  decides where you put the probe.

### What it looks like

The figure is a series resonator with the reactance **split across the probe**: a 10 Ω source
termination and 1 nH on the G side, 10 pF and a negative resistance on the L side, resonant at
`f0 = 1.5915 GHz`. With `R1 = −5 Ω` the circuit is stable at that termination, and the margins still
have a great deal to say.

{{ui: wsprobe-margin-resonator}}

Three readings, all from the fixture's own 2001-point sweep:

- `SM_Y0` notches to **−18.06 dB at 1.5913 GHz** — at `f0`, and its depth is set by the
  negative-resistance ratio.
- `SM_H0`'s minimum is **−19.85 dB at 1.7337 GHz** — a *different* frequency, the one at which
  `Re(YG + YL)` changes sign, although the impedance sum is positive everywhere. That is the pole
  masking, made visible on a circuit with one loop in it.
- The stable case is already below [M]'s −15 dB rule at `f0`. A node one negative-resistance step
  from oscillating genuinely *has* little margin.

<div class="callout note">
<span class="label">The plot draws the two lines you read against</span>
<p>A rectangular plot carrying a margin trace draws the analysis' own <b>threshold</b> and the
<b>−12 dB floor</b> beneath it, once per distinct level. A polar plot carrying any WSProbe trace
marks <b>both</b> critical points — the origin, which a driving-point locus is read against, and
<b>+1</b>, which a loop gain is read against.</p>
</div>

### The threshold message

`MarginThreshold=<dB>` on the S-parameter or harmonic-balance directive (default **−15**, [M]'s own
rule; `MarginThreshold=none` disables it) produces one **note** per probe whose `min(SM_Y0, SM_H0)`
falls below it:

```text
[circuitRF] WSProbe 'P': stability margin below −15 dB (0.178): SM_Y0 = −18.1 dB at 1.59125 GHz,
SM_H0 = −19.8 dB at 1.73375 GHz. A margin below −12 dB means one side of the node presents negative
resistance there.
```

It is a **note, not a warning**, and the resonator above is exactly why: that circuit is stable and
fires it. The message means *look here*, not *this is wrong*. `circuitrf explain --analysis` prints
the effective threshold beside the probe list, because the default is circuitRF's and not the
paper's to state.

<div class="callout note">
<span class="label">Where the margin came from</span>
<p>[M]'s own demonstration is an amplifier that passed every polar check and Rollett's criterion in
simulation, and oscillated at 1.16 GHz when it was built. Re-read as a margin, the
<em>original small-signal simulation</em> showed a −50 dB collapse at 1.06 GHz that no binary check
had a way to report. A 10 Ω de-Q resistor fixed it.</p>
</div>

## The derived metrics {#metrics}

Everything the reference documents derive from `wsp` is available in two places that are one
implementation: the **trace card's WSProbe section**, and a **`measure` line**. They are *derived
metrics*, in the sense [the derived-metrics page](derived-metrics.html) uses the term — quantities
computed from a result rather than read out of it.

Arguments are the document's, in its own order. A `wsp` cube, a probe index, a 2×2 network, or a
scalar-per-frequency cube:

```text
measure  ZGgate  = wsp_ZG(SP1.wsp, SP1.idx("GATE"))
measure  Yred    = wsp_yparam(SP1.wsp, SP1.idx("GATE"))
measure  margin  = wsp_stability_margin(SP1.wsp, SP1.idx("GATE"))
```

### One probe

| Function | Equation | Returns |
|---|---|---|
| `wsp_yparam(wsp, idx)` | Eq. 44 | the reduced two-port as `[Y]`, S |
| `wsp_zparam(wsp, idx)` | Eq. 48 | the reduced two-port as `[Z]`, Ω |
| `wsp_H0(wsp, idx)`, `wsp_Y0(wsp, idx)` | Eq. 50, 52 | Ω, S |
| `wsp_ZG`, `wsp_ZL` | Eq. 67, 68 | Ω — bidirectional impedances |
| `wsp_YG`, `wsp_YL` | Eq. 76 | S — bidirectional admittances |
| `wsp_zop`, `wsp_yop` | Eq. 89, 90 | Ω, S — the open-port immittances |
| `wsp_loopgain(Y, kind [, Z0])` | Eq. 92, 96–104 | one of eight loop gains |
| `wsp_nodal_gamma(wsp, idx)` | App. E.7 | the nodal conjugate reflection coefficient |
| `wsp_unstable_freq_kurokawa(T)` | Eq. 107, 108 | the crossing frequencies, in Hz |
| `encirculations(SP)`, `enc(SP)` | App. E.3 | the running encirclement count |
| `wsp_impedance(wsp, i, j [, stimulus])` | Eq. 37, 196, 197 | Ω — the even-mode transfer impedance |
| `wsp_gain(wsp, S, G, D)` | App. D | dB |
| `GainDEFs(GamS, SM, GamL)` | Eq. 203–208 | `GT_dB`, `GP_dB`, `GA_dB`, `Gmax_dB` |

The `kind` strings for `wsp_loopgain` are the document's own: `BI` (bilateral / Tian, the default
`LG`), `UNI` (forward synthetic circulator; `FOR` is accepted for it too), `REV`, `HST` (Hurst),
`MB` and `MBR` (Middlebrook forward and reverse), `GFT` and `GFTR` (general feedback theorem).
`Z0` defaults to 50 Ω and is read only by the two circulator kinds.

### The stability margin

| Function | Equation | Returns |
|---|---|---|
| `wsp_rY`, `wsp_iY`, `wsp_rH`, `wsp_iH` | M-rY, M-iY, M-rH, M-iH | the four proxies, `[0, 1]` |
| `wsp_SM_Y0(wsp, idx)`, `wsp_SM_H0(wsp, idx)` | M-Eq. 9, 10 | the two margins, `[0, 1]` |
| `wsp_stability_margin(wsp, idx)` | — | `min(SM_Y0, SM_H0)` |
| `wsp_sm_z(ZG, ZL)`, `wsp_sm_y(YG, YL)` | M-Eq. 9, 10 | the same formula over any pair you supply |

### Two probes, and a set of them

| Function | Equation | Returns |
|---|---|---|
| `wsp_yparam2(wsp, idx1, idx2)` | Eq. 137–139 | the eight entries `y11 … yf22` |
| `wsp_yparam2(wsp, idx1, idx2, "inner"\|"feedback")` | Eq. 137–139 | that block alone, as a two-port |
| `wsp_yparam2_residual(wsp, idx1, idx2)` | — | the pair's own diagnostic; see [§6](#pairs) |
| `wsp_block_calc(wsp, idx1, idx2 [, Z0])` | Eq. 140–151 | sixteen: `s11 … LGM` |
| `wsp_block_breakout`, `wsp_fb_breakout` | App. E.5, E.6 | either block as a two-port at `Z0` |
| `wsp_block_design`, `wsp_fb_design` | App. E.8, E.9 | either block renormalised in situ |
| `wsp_bifurcate(wsp, form, side [, probes])` | Eq. 161–176 | the G-side or L-side subnetwork |
| `wsp_YA`, `wsp_YF`, `wsp_ZA`, `wsp_ZF` | Eq. 161–176 | the same, under the document's own names |
| `wsp_loopgain_ohtomo(wsp, probes [, active, Z0])` | Eq. 177–180 | the global loop gains `G_i` |
| `wsp_unstable_freq_loopgain(G)` | §7, p. 110 | crossings of the `+1` point, in Hz |
| `wsp_ymatrix(wsp [, probes])` | Eq. 182–186 | the network's own Y at the probe nodes |
| `wsp_ndf(wsp_active, wsp_passive [, probes])` | Eq. 186 | the probe route to the NDF |

### The envelope

| Function | Returns |
|---|---|
| `wsp_terminate(wsp, idxS, YS, idxL, YL [, YSo, YLo])` | the whole `wsp` of the re-terminated circuit |
| `wsp_loadpull(wsp, idxS, idxL, idx, gammaS, gammaL [, Z0])` | `1/H0'` and `1/Y0'` over the Γ grids |
| `wsp_loadpull_unstable(…)` | the Kurokawa crossings found at each termination |
| `wsp_loadpull_margin(…)`, `wsp_loadpull_margin_env(…)` | the margin over the grid, and its minimum per point |
| `wsp_loadpull_ndf(…)`, `wsp_loadpull_ndf_enc(…)` | the NDF over the grid, and its encirclement count |

### Small utilities the document's appendix carries

`wsp_zsrc(R, C [, f])` and `wsp_zprc(R, C [, f])` build a series or parallel RC impedance;
`z_to_pr`/`z_to_pc`/`z_to_pl`/`z_to_sr`/`z_to_sc`/`z_to_sl` and their `y_to_…` twins convert an
immittance into the element it is equivalent to; `wsp_rc_renorm_s` and `wsp_zo_renorm_s` renormalise
an S-matrix to an RC or to a complex reference; `_dB(M)` is `10·log10|M|`.

<div class="callout note">
<span class="label">The immittance models return farads and henries</span>
<p>The reference document's <code>y_to_pc</code> returns <b>picofarads</b> and its
<code>y_to_pl</code> nanohenries. circuitRF's return <b>farads</b> and <b>henries</b>, under the same
names. Every trace and every derived metric in this program is base SI, and a scale factor hidden
inside a function is exactly the class of mistake that is invisible on a plot. If you are checking a
number against the document's own listing, that factor of 10<sup>12</sup> is the difference.</p>
</div>

## Probe pairs: a block in situ {#pairs}

Two probes in the generator → load orientation bracket a network. `wsp_yparam2` recovers **both** the
block between them (`y11 … y22`) and the block that closes around them (`yf11 … yf22`) — Eq. 137–139
— from the same `wsp` the single-probe metrics come from, with the circuit intact and nothing
disconnected.

`wsp_block_calc` then reports sixteen quantities of that pair (Eq. 140–151): the S-parameters of the
inner block and of the feedback block at a reference `Z0` (`s11 … sf22`), four return differences
(`F_LGa`, `F_LGf`, `F_LGH`, `F_LGM`) and the four loop gains they came from (`LGa`, `LGf`, `LGH`,
`LGM`). Every one of them is on the trace card's list.

**Why this matters more than the list suggests:** the inner block comes back as an ordinary two-port
network. So `μ`, `K`, `|Δ|`, MAG/MSG and the [stability circles](derived-metrics.html#circles) can be
applied to **a stage inside the amplifier** — measured where it actually sits, loaded by what
actually loads it, rather than by simulating it on its own between two 50 Ω terminations.

`wsp_block_design` and `wsp_fb_design` go one step further and hand back the block renormalised
**in situ** to the RC each side actually presents, which is the form a matching-network calculation
wants.

<div class="callout note">
<span class="label">The residual: is this pair actually bracketing a block?</span>
<p><code>wsp_yparam2_residual</code> is circuitRF's, not the document's. It solves the same two
blocks a second time from the <em>shunt</em> stimuli — an independent set of equations with the same
side bookkeeping — and reports how far the two answers differ, relative.</p>
<p>Round-off (~5e-16) when the pair genuinely brackets a two-port. Order 0.1 or worse when something
joins the inner region to the outside <em>around</em> the probes, which is the case in which the
eight numbers are describing a configuration you do not have. A shunt from an inner node to ground
does <b>not</b> raise it: ground is not a coupling path, and such an element is simply part of the
inner block's own <code>y11</code>.</p>
</div>

<div class="callout warning">
<span class="label">Hurst's form wants a simple pair</span>
<p>The reference document's own warning (p. 89): the Hurst loop gain "is most meaningful when the
probe pair is closest to and capture a single dependent source — such as with a transistor. I don't
recommend using a complex network between the probe pair."</p>
</div>

## Global stability: bifurcation and Ohtomo {#global}

With several probes all oriented the same way, the network splits into two subnetworks — everything
on the **G** side of the probes and everything on the **L** side — and each is an N-port recoverable
from `wsp` alone (Eq. 161–176). That is **bifurcation**.

`wsp_bifurcate(wsp, "Y"|"Z", "G"|"L")` names the side explicitly, and the document's four function
names are aliases with the document's own sides:

| Document's name | Form | Side |
|---|---|---|
| `wsp_YA` | admittance | G |
| `wsp_YF` | admittance | L |
| `wsp_ZA` | impedance | **L** |
| `wsp_ZF` | impedance | **G** |

**The Y-form and the Z-form put the "active" network on opposite sides.** That is not a slip in the
document; it is a consequence of how each derivation is set up. It is also a trap, which is why
circuitRF's own primitive takes the side as an argument and the aliases carry the document's sides
rather than a tidier scheme. `wsp_YA` and `wsp_ZA` describe *different* subnetworks.

**Two requirements, neither of which anything can check from `wsp`:**

- every probe in the set must have the named subnetwork on the **same side** (§6, p. 100). Two probes
  whose named sides meet give either a singular system or a matrix describing nothing;
- two probes whose same-side terminals share a node give a singular result — two ports on one node
  have no admittance matrix. Put each probe on its own node.

### Ohtomo's global loop gains

`wsp_loopgain_ohtomo(wsp, probes)` takes the two subnetworks as scattering matrices at `Z0`, forms
`M = SP·SA − I`, and returns one loop gain per probe (Eq. 177–180):

```text
G_i = 1 + |M_{N−i+1}| / |M_{N−i}|        M_0 ≡ 1
```

where `M_{N−i+1}` is the trailing principal submatrix on rows and columns `i … N`.

**The critical point is `+1`, not the origin.** `wsp_unstable_freq_loopgain(G)` looks for `|G| ≥ 1`
with the phase crossing zero **clockwise** — the mirror of the Kurokawa search, around `+1` instead
of on the negative real axis. The trace card's Ohtomo trace draws the `+1` reference mark for you.

**Why the method is global rather than one more loop gain.** The `G_i` telescope:

```text
Π (G_i − 1) = det(M)
```

and `det(M)` is the Nyquist determinant of the closed loop of travelling waves. So the *sum* of the
encirclements of `+1` by the `G_i` equals the encirclements of the origin by `det(M)` — whatever the
probe order and whichever side you called active, even though the individual `G_i` all change. With
one probe it reduces to `ΓP·ΓA`, [Jackson's stability index](#appendix-stability).

<div class="callout warning">
<span class="label">Ohtomo's prerequisite</span>
<p>The method assumes <b>each subnetwork is stable on its own</b> (§7, p. 108): one side purely
active with no terminations that could form a loop of their own, the other purely passive. Nothing
in <code>wsp</code> can check that, and no message will tell you it has been violated.</p>
</div>

## The stability envelope {#envelope}

The question the envelope answers is *what happens to this node's stability when the source and load
are not 50 Ω*. It is the stability counterpart of a load-pull, and — this is the point — **it costs
no re-simulation at all**.

Adding a shunt admittance at a probe node is a **rank-1 update of the whole `wsp` matrix**, because
every response to an injection at that node is already in it. Applied once at the source probe and
once at the load probe, that yields the *complete* `wsp` of the re-terminated circuit — every `H0'`,
`Y0'`, `ZG'`, `ZL'`, loop gain, margin and Ohtomo gain — from the one sweep you already ran (§9,
p. 120; [E] §IV). It works on a large-signal `wsp` from harmonic balance exactly as it does on a
linear one.

**The precondition, which is checked and refused rather than assumed.** The source probe must sit
**directly at its termination with `G` facing it**, and the load probe directly at its termination
with `L` facing it, so that the termination is a pure shunt at that node. circuitRF records each
probe's neighbouring top-level `Term`/`Port` and compares the probe's own bidirectional impedance on
that side against the declared `Z`. A probe with nothing at that node is refused; a probe whose `ZG`
disagrees with the declared `Z` is refused **naming both numbers**. That is what feedback across the
probe (§9, p. 119), or a series element between the probe and its termination, produces — and being
told is the intended outcome, because the update would otherwise be re-terminating something other
than the termination.

{{ui: wsprobe-envelope-card}}

The card takes a **ladder** of `|Γ|` per side rather than one magnitude, and an angular step. That is
deliberate: the interesting reading is usually three rungs either side of the ρ at which something
changes, seen at once, rather than two runs and a memory of what the last picture looked like. It
also prints how many terminations it is about to compute, before it computes them.

### The four things you can read off it

| Metric | What it is |
|---|---|
| `1/H0env`, `1/Y0env` | the driving-point loci of the re-terminated circuit, per termination |
| `unstable` | the Kurokawa crossings found at each termination, counted |
| `SMenv` | the [stability margin](#stability-margin)'s minimum over frequency, per termination ([E] §II) |
| `NDFenc` | the [NDF](#ndf)'s origin encirclements at each termination, from a passivated run ([E] §III) |

The last two together are the comparison the 2025 paper [E] makes: the NDF is **binary** — a
termination is either encircled or it is not — while the margin is a **distance**, and the distance
carries information the count does not.

{{ui: wsprobe-margin-envelope-ohtomo}}

The figure sweeps a two-device parallel amplifier's source phase at `ρ = 0.9` with both quantities on
one plot. The numbers are that fixture's own — a redrawn topology with circuitRF's own element
values, not the paper's circuit — and the shape is what matters: **terminations at which the
encirclement count has already returned to zero while the margin is still tens of dB down.** Those
are the terminations a binary check calls fine.

<div class="callout warning">
<span class="label">The envelope's NDF is the REDUCED one</span>
<p>It is taken over the probed nodes, and it is complete only if the probe set covers every node
that can hide a pole (§8, pp. 112–113). The Ohtomo fixture is the demonstration: with probes on the
source, one gate and the load, the reduced NDF reads <b>zero</b> encirclements at a start-up the
Kurokawa search reports — and adding a fourth probe on the <em>other</em> gate, with nothing else
changed, makes the same NDF read 2. The odd mode is differential across the two gates and only one
of them carried a probe.</p>
<p>The same fixture carries a second lesson about what an envelope can see at all: an odd mode sees
both ports as virtual grounds, so <b>no source or load termination reaches it</b>. A VSWR sweep is
structurally blind to that class of instability, which is why a two-device amplifier needs a
balancing resistor rather than a better match.</p>
</div>

<div class="callout note">
<span class="label">NDFenc counts turns; the NDF summary counts poles, and they differ by two</span>
<p><code>NDFenc</code> (and <code>wsp_loadpull_ndf_enc</code>) is the <b>swept locus's own net
turn</b>, over <code>&omega; &ge; 0</code>. The <code>NDF: n right-half-plane pole(s)</code> line an
<code>NDF=yes</code> run prints is the count around the <em>closed</em> Nyquist contour, and
Platzker's <code>NDF(&minus;&omega;) = conj NDF(&omega;)</code> makes the unswept arm turn through
the same angle — so <b>poles = 2 &times; turns</b>. A conjugate pair, which is what an oscillator
has, is one turn here and two poles there. The two always agree about <em>whether</em> a termination
is unstable, which is what a non-zero <code>NDFenc</code> means; they are not the same number, and
neither is wrong.</p>
</div>

## Under harmonic balance {#hb}

Everything above computes `wsp` from an S-parameter analysis, which linearises the nonlinear devices
at their **DC operating point**. That answers whether the design is stable with nothing driving it.
It cannot answer the question a power-amplifier designer actually asks — *is it stable at the drive
level it ships at* — and it cannot see a **parametric** instability at all, because that one lives at
half the fundamental and an unpumped circuit has no fundamental.

Harmonic balance answers both, and **every derived metric on this page applies to the result with no
change at all** (§4.2, p. 43: "Linear analysis, harmonic balance, or AC analysis can all be
accommodated").

### What "tickle" means here

The circuit is driven hard by its HB tones. At the converged operating point the nonlinear devices
are *periodically time-varying* conductances and capacitances, and the probe injects a vanishingly
small series voltage or shunt current at a swept frequency `ssfreq` — the tickle — which is in
general not on the HB grid at all. The response comes back at every sideband of the fundamental; what
`wsp` carries is the response at `ssfreq` itself.

Ask for it on the harmonic-balance directive:

```text
analysis HB1 type=hb Tone=RFfreq MaxHarm=7 \
     SSStart=0.1 SSStop=10 SSNpts=991 SSUnit=GHz [SSStep=…] [SSLog=true] [SSMaxHarm=K] \
     [MarginThreshold=<dB>|none]
```

`SSUnit` applies to start, stop and step alike — one unit for the whole sweep, as everywhere else in
circuitRF. **Absent `SSStart`/`SSStop` there is no small-signal solve**, and the run is exactly the
run it was before: a WSProbe with no `SS*` keys is not an error, it is a transparent element, and the
run says once that it carried no transfer functions.

The cubes are the same ones, over `{ssfreq}` instead of `{freq}`: `HB1.wsp`, `HB1.H0("GATE")`,
`HB1.idx("GATE")` and the rest resolve exactly as their `SP1.*` counterparts do. Under a drive sweep
the sweep axis is prepended, which is what gives you a **fan**.

{{ui: wsprobe-hb-fan}}

### What to look for

- **At low drive the harmonic-balance answer tends to the S-parameter answer.** The two analyses
  agree at the bottom of the drive sweep, which is the sanity check that the fan is the same quantity
  you have been reading all along.
- **A right-half-plane pole of the driven circuit** shows Kurokawa's signature on `1/H0(ssfreq)` and
  `1/Y0(ssfreq)`, at a tickle frequency near its imaginary part. Same search, different cube.
- **A parametric (sub-harmonic) instability** appears at `ssfreq ≈ f0/2`, and at its images
  `f0/2 + k·f0`. **This is the case no linear analysis can see**, and it is the reason to sweep across
  drive rather than to run at one level.
- **The margin under drive.** A `SM_Y0` fan that collapses only above some drive level is a
  drive-dependent instability; the parametric case reads as a notch at `f0/2` that is simply absent
  at low drive. `MarginThreshold` is applied at each operating point.

<div class="callout note">
<span class="label">One frequency this analysis refuses to answer</span>
<p>A tickle <b>commensurate with the fundamental at order 2</b> — <code>ssfreq</code> equal to 0,
<code>f0/2</code>, <code>f0</code>, <code>3f0/2</code>, … — is reported as <b>NaN with a warning</b>,
never as a plausible number. There the sideband family and its negation are the same set of
frequencies, so the two are not independent and the formulation would be solving half the problem.</p>
<p>The practical consequence, which is a property of the method and not a defect: <b>a parametric
instability at <code>f0/2</code> is found by the grid points either side of it.</b> The pole pair
approaching the axis shows its crossing at the samples that bracket it, and a grid that happens to
land on <code>f0/2</code> exactly loses that one sample and nothing else. An odd point count over the
same span usually avoids it, and the warning says so.</p>
</div>

### Cost, and what is refused

The small-signal solve is a dense factorisation per tickle frequency, shared by all the probe
injections at that frequency, plus a linear-partition extraction per sideband. So the cost scales
with the tickle-frequency count, the sideband count and the probe count — not with the drive sweep,
which reuses each operating point.

**Two choices you make on the directive change what it costs, and one of them is free.** The linear
part of the circuit is the same at every drive level, so every sparse factorisation of the sweep is
done once and reused across the whole drive sweep — and a sideband `ssfreq + k·f0` of one tickle
point is *literally the same frequency* as a sideband of another whenever the tickle step divides the
fundamental. **A grid step of `f0/m` therefore shares most of its sideband work between points**,
which on a long sweep is worth several times over. circuitRF never moves your grid to get it: a
frequency you wrote is a frequency it uses, so the run instead reports how many distinct sideband
frequencies your grid landed on and what an aligned step would have bought. `SSNpts` chosen so the
step comes out at `f0/200` costs nothing and is the free half.

**`SSMaxHarm` is the half that is not free.** It truncates the sideband set, and truncating it
cuts the sideband extractions linearly and the dense conversion matrix *cubically* — which is why it
is tempting. Measured on a single device driven into compression, `1/H0` computed at `SSMaxHarm = K −
2` was 7 % from the untruncated answer at the median tickle frequency and 27 % from it at the worst
one, and the error does not fall monotonically as you raise it (truncation removes mixing paths; it
is not a series expansion of the answer). The default is `MaxHarm` for that reason. Lower it to
explore quickly, raise it back before you believe a margin — and the run says so once whenever it is
below `MaxHarm`.

Long tickle grids are run in parallel, several points at a time, exactly as an S-parameter sweep is;
`MaxParallelism` governs both and the result does not depend on it.

Three refusals, each by name: **three or more tones** (one and two are supported); the
**rectangular-FFT two-tone path** rather than the default lattice; and a conversion system above
2000 unknowns, which states which of `SSMaxHarm`, `MaxHarm` and `MaxMixOrder` is binding. A run that
allocated gigabytes and was killed with nothing said would be the worse outcome.

**Loadpull does not run the small-signal solve**, and does not silently do so: at every termination
of a grid it would multiply the run by the tickle count. A probed loadpull says once where the
small-signal sweep lives and points at [the envelope](#envelope), which is the reference document's
own replacement for a stability loadpull and costs no extra harmonic-balance solves at all.

## NDF — the normalized determinant function {#ndf}

```text
NDF = Δ / Δ0 = |Y| / |Y_passive|          (Eq. 181)
```

`Y` is the network's admittance matrix with every termination, bias network and bypass included and
every independent source off. `Y_passive` is the *same* matrix with every dependent source, negative
resistance and non-Foster element rendered passive. `Δ0` then has no right-half-plane zeros by
construction, so by the argument principle the **clockwise encirclements of the origin by `NDF(jω)`
count the network's right-half-plane poles** (§8, p. 112).

**circuitRF can build `Δ0`, and the reference document's reader usually cannot.** The document is
explicit that the passive determinant "requires having precise access to the transconductance
elements in all active devices" (§8, p. 113) — which a black-box vendor model withholds. Every
built-in active model here is circuitRF's own and knows its controlled sources exactly, so the NDF is
a native analysis rather than a construction you assemble by hand.

Turn it on with a knob on the S-parameter directive:

```text
analysis SP1 type=sparam start=1 stop=100000 npts=2001 log Unit=MHz  NDF=yes
```

### What the run reports

```text
NDF: 2 right-half-plane pole(s)  (net clockwise encirclement 1.989; NDF(100 GHz)=1 ∠ 1.8)
```

Five properties of a correctly-built NDF, and circuitRF checks the three that a run can get wrong:

| Property | How it is reported |
|---|---|
| It has zeros only, no poles | holds by construction |
| No counter-clockwise encirclement | `ndf.counterclockwise` if the running count falls back a whole turn |
| `NDF → 1` at high frequency | `ndf.no-asymptote` if it is still closing on 1 at the top of the sweep — extend it upward |
| | `ndf.constant-asymptote` if the limit is simply not 1, quoting the limit |
| `NDF(−ω) = conj NDF(ω)` | holds by construction |
| It is real at DC | `ndf.dc-imaginary` — extend the sweep downward |

<div class="callout note">
<span class="label">A constant asymptote is not a failure</span>
<p>Passivating a <b>negative resistance</b> changes an element <em>value</em> — <code>R → |R|</code>
— and an element value can be a <em>factor</em> of the network determinant where a dependent source
is only ever a <em>term</em> in it. So the NDF of such a circuit tends to a constant that is not 1.
The pole count is unaffected: a constant turns through no angle.</p>
</div>

### What is passivated, exactly

| Kind | What happens |
|---|---|
| Passives, lines, transformers, ideal blocks, probes, terminations, sources | as-is; sources are already off, and the ordinary stamp *is* the passive stamp |
| Two-terminal nonlinearities (diode, C(V)) | linearised at bias; they are passive at any bias |
| `R`, `Z_Port` | `R → \|R\|`; the real part of a port impedance likewise |
| `VCCS`, `VCVS` | gain → 0 (a zero-gain controlled voltage source is still a short, so the branch stays) |
| `Amp`, `Mixer` (system blocks) | forward gain → 0; the mixer's conversion terms *and* its LO leak |
| Every built-in transistor family | the transconductance → 0. Output conductance, junction conductances and capacitances all stay at bias |
| `SDD` | **you name a scaling variable**: `PassiveVars="NDFgm"` |
| Verilog-A, external devices | **you name an instance parameter**: `PassiveParams="X1.gmscale"` |
| `SnP`, `Chain` | passive if σ<sub>max</sub> ≤ 1 at every sampled frequency — otherwise **refused by name** |

An S-parameter block with gain hides its dependent sources, and there is no honest way to passivate
it. That is a refusal naming the instance, never a silently passive-looking NDF.

<div class="callout warning">
<span class="label">The shape of an SDD's passivation matters</span>
<p><code>I[2,0] = NDFgm*Ids(_v1,_v2)</code> is the <b>wrong</b> shape. It scales the whole drain
current, output conductance included, so <code>Δ0</code> would be a <em>different circuit</em> rather
than the same one with its controlled source removed. Freeze the <b>controlling voltage</b>
instead:</p>
<p><code>I[2,0] = Ids(NDFgm*_v1 + (1 − NDFgm)*Vgs0, _v2)</code></p>
<p>At <code>NDFgm = 0</code> the drain current no longer responds to the gate voltage while staying
evaluated at the same bias — which is exactly what a built-in FET's own passivation does.</p>
<p>A <code>PassiveVars</code> name that is not a global, or that no device reads, is a refusal.
<b>A scaling variable that scales nothing is the classic silent failure of a hand-built NDF.</b></p>
</div>

`circuitrf explain --analysis` lists the passivation each instance will use, which is how to see a
refusal coming without running anything:

```text
      NDF: yes; 24 instance(s), 3 carrying activity to passivate
      NDF M1 (VCCS) activeExact: exact — G → 0
      NDF X1 (SDD)  activeUserScaled: needs PassiveVars= naming a global this device reads
```

### K is not enough

The reason to have this at all is that a two-port stability factor is computed from the **reduced**
two-port and can say nothing about a pole the reduction hides.

{{ui: wsprobe-ndf-k}}

The figure's circuit is a two-port whose terminal S-parameters are those of a well-behaved 6 dB pad —
`K > 1` and `|Δ| < 1` at every frequency in the band — wrapped around an internal loop that
oscillates. The loop is coupled to the through path by 1 fF, a thousandth of the tank capacitance:
far too weak to move the terminal S-parameters measurably, and entirely irrelevant to whether the
loop oscillates, because a start-up condition is a property of the loop and not of what is watching
it. **The NDF reads 2 right-half-plane poles.** The stable and unstable versions of the same circuit
have terminal S-parameters that agree to 1e-4, so the two-port metrics were not simply given
different data.

### The probe route

`wsp_ndf(wsp_active, wsp_passive [, probes])` (Eq. 186) computes the same ratio from the reduced
matrices at the probe nodes, using a passivated run's `wsp` beside the ordinary one. It is the route
the reference document describes and it is a genuine cross-check on the native calculation — with
the reduced-NDF caveat of [§8](#envelope): it is complete only over the nodes you probed.

A run with `NDF=yes` **and** probes writes both cubes, `wsp` and `wsp_passive`, so the comparison
needs one run rather than two.

## From the command line {#headless}

Everything on this page runs with no display. See [the CLI reference](cli.html#sparam).

**Run a probed sweep, with the NDF:**

```text
circuitrf sparam amp.cnl
```

with `NDF=yes` on the directive. One line per probe follows the S summary:

```text
WSProbe P idx=1  H0(0.5 GHz)=11.597 ∠ -1.4  ZG(0.5 GHz)=10.482 ∠ 17.4
                 SM_Y0 min −18.1 dB @ 1.59125 GHz  SM_H0 min −19.8 dB @ 1.73375 GHz
```

`--json` carries the same under `wsprobes` (label, idx, and each margin's minimum and frequency —
**linear**, because dB is a display convention and a document should carry the number) and under
`ndf`. `-o out.npy` / `.mat` / `.txt` carry every cube.

**Measure anything on this page**, evaluated identically in the window and headlessly:

```text
measure  SMgate  = wsp_stability_margin(SP1.wsp, SP1.idx("GATE"))
measure  fosc    = wsp_unstable_freq_kurokawa(1 / SP1.H0("GATE"))
measure  Kin     = wsp_yparam2(SP1.wsp, SP1.idx("G1"), SP1.idx("D1"), "inner")
```

**See what will happen before it does:**

```text
circuitrf explain amp.cnl --analysis
```

lists each probe with its `idx` and both terminal nets, the effective `MarginThreshold`, and the
passivation every instance will use.

**Check the document:**

```text
circuitrf check amp.cnl
```

warns `wsprobe.shorted` when a probe's two nets are the same net — the headless twin of the
schematic editor's own wire cut.

**A picture, with no data display to author first:**

```text
circuitrf plot amp.npy -o margin.svg \
    --trace cube=SP1.wsp,probe=GATE,metric=SM_Y0,y=db20
```

`probe=` is what turns a `cube=…wsp` trace into a probe metric; `metric=` takes the document's own
spelling. Case is load-bearing in this notation and is not folded away — `LGF` is one probe's forward
circulator loop gain and `LGf` is a probe *pair's* feedback-as-synthetic-FET loop gain, and quietly
answering one with the other would be worse than a refusal.

<div class="callout warning">
<span class="label">A port-less run has no Touchstone</span>
<p>A circuit with WSProbes and no <code>Term</code> or <code>Port</code> is a legitimate run — the
reference document's own fixtures are like this — and it writes every <code>wsp</code> cube. Asking
for a <code>.sNp</code> from it is refused, naming the spellings that <em>do</em> carry the result.
Write <code>.npy</code>.</p>
</div>

## Caveats, in the documents' own words {#caveats}

One list, because each of these is a limit of the method rather than of this implementation.

- **Loop gains are incomplete.** Every one of them, including `LG` and `F`. They "are not
  fundamental circuit quantities and are therefore not rigorous stability measurements … not
  necessarily wrong, just incomplete" (§4.4, p. 50).
- **Pole masking.** A zero can hide the pole in `1/H0` or in `1/Y0`, though never in both. Check both
  (§4.10, pp. 73–79) — and the same applies to `SM_H0` and `SM_Y0`.
- **A sweep can step over a crossing.** An empty crossing list means *no crossing was sampled*, not
  *the circuit is stable*. A resonance narrower than the grid step is invisible to any method that
  samples.
- **The envelope's precondition.** Both terminations must carry a probe directly at them, with the
  right terminal facing them. Otherwise the update is re-terminating something else, and circuitRF
  refuses rather than guessing.
- **A source/load envelope cannot see every mode.** An odd mode across two devices presents virtual
  grounds at both ports; no source or load termination reaches it.
- **The reduced NDF sees only what you probed.** Complete only if the probe set covers every node
  that can hide a pole (§8, pp. 112–113).
- **Ohtomo's prerequisite.** Each subnetwork must be stable on its own (§7, p. 108). Nothing checks
  it.
- **Circuit fidelity.** The document's own last word (p. 113): "no stability detection method will
  capture an instability that is mathematically NOT contained in your simulation construction."
- **The margin is a distance, not a detector.** Kurokawa's clockwise-sense condition is not in it.
  Read the margin's minimum and the crossing search together — the trace card prints them on one
  line for that reason.
- **The margin's dB convention is circuitRF's.** `20·log10`, so [M]'s −15 dB rule of thumb is 0.178
  linear. The paper does not state a convention.

## References {#references}

**The primary reference.**

> T. A. Winslow, *General Circuit Analysis Using The WSProbe*, January 29, 2023. Public technical
> report, 172 pages. Cited above as `(Eq. n)`, `(§n)` and by page.

**The two papers that followed it.**

> **[M]** T. A. Winslow, "A Novel Stability Margin for Transfer Functions," *Proc. 19th European
> Microwave Integrated Circuits Conference (EuMIC)*, Paris, September 2024, pp. 291–294.
> DOI [10.23919/EuMIC61603.2024.10732614](https://doi.org/10.23919/EuMIC61603.2024.10732614).
> Cited as `(M-Eq. n)`, and its four unnumbered proxy displays as `(M-rY)`, `(M-iY)`, `(M-rH)`,
> `(M-iH)`.

> **[E]** T. A. Winslow, "Stability Envelope Using Nodal Transfer Functions," *Proc. 20th European
> Microwave Integrated Circuits Conference (EuMIC)*, Utrecht, September 2025, pp. 254–257.
> DOI [10.23919/EuMIC65284.2025.11233915](https://doi.org/10.23919/EuMIC65284.2025.11233915).
> Cited as `(E-Eq. n)`.

**The earlier and adjacent work.**

> T. A. Winslow, "A Novel CAD Probe for Bidirectional Impedance and Stability Analysis," *2018
> IEEE/MTT-S International Microwave Symposium (IMS)*.
> DOI [10.1109/MWSYM.2018.8439210](https://doi.org/10.1109/MWSYM.2018.8439210). The original probe
> paper, which the 2023 document supersedes mathematically.

> T. A. Winslow, "Introduction to the WSProbe," *2025 IEEE BiCMOS and Compound Semiconductor
> Integrated Circuits and Technology Symposium (BCICTS)*.
> DOI [10.1109/BCICTS63111.2025.11211431](https://doi.org/10.1109/BCICTS63111.2025.11211431).

> T. A. Winslow, *Stability Analysis Methods for Microwave Power Amplifiers: A Modern Perspective*,
> IMS 2024 keynote.

<div class="callout note">
<span class="label">Two printed equations this page cites only in corrected form</span>
<p><b>E-Eq. 11</b> — the bidirectional impedances under mismatch — has its generator and load
numerators swapped as printed. <b>M-Eq. 5</b> and <b>M-Eq. 6</b> print Z-forms of <code>YG</code> and
<code>YL</code> whose off-diagonal terms are swapped; the Y-forms beside them are right and are what
circuitRF implements. Both were found numerically: on a reciprocal network the difference is
invisible, and on a non-reciprocal one it is order 1. Every quantity on this page is the corrected
form.</p>
</div>

### The stability literature this page's appendix draws on

The bibliography of the reference document's own §2–§3, which is the shape of
[Appendix A](#appendix-stability). Bibliographic entries only.

- H. W. Bode, *Network Analysis and Feedback Amplifier Design*, Van Nostrand, 1945.
- H. Barkhausen, *Lehrbuch der Elektronen-Röhren*, Band 3: *Rückkopplung*, Hirzel, 1935.
- J. M. Rollett, "Stability and Power-Gain Invariants of Linear Twoports," *IRE Trans. Circuit
  Theory*, vol. CT-9, no. 1, pp. 29–32, March 1962.
- M. L. Edwards and J. H. Sinsky, "A New Criterion for Linear 2-Port Stability Using a Single
  Geometrically Derived Parameter," *IEEE Trans. Microwave Theory Tech.*, vol. 40, no. 12,
  pp. 2303–2311, December 1992.
- K. Kurokawa, "Some Basic Characteristics of Broadband Negative Resistance Oscillator Circuits,"
  *Bell System Technical Journal*, vol. 48, no. 6, pp. 1937–1955, July–August 1969.
- A. Platzker, W. Struble and K. T. Hetzler, "Instabilities Diagnosis and the Role of K in Microwave
  Circuits," *IEEE MTT-S International Microwave Symposium Digest*, pp. 1185–1188, 1993.
- W. Struble and A. Platzker, "A Rigorous yet Simple Method for Determining Stability of Linear
  N-Port Networks," *15th Annual GaAs IC Symposium Digest*, pp. 251–254, 1993.
- R. W. Jackson, "Rollett Proviso in the Stability of Linear Microwave Circuits — A Tutorial,"
  *IEEE Trans. Microwave Theory Tech.*, vol. 54, no. 3, pp. 993–1000, March 2006.
- R. D. Middlebrook, "Measurement of Loop Gain in Feedback Systems," *International Journal of
  Electronics*, vol. 38, no. 4, pp. 485–512, 1975.
- M. Tian, V. Visvanathan, J. Hantgan and K. Kundert, "Striving for Small-Signal Stability," *IEEE
  Circuits and Devices Magazine*, vol. 17, no. 1, pp. 31–41, January 2001.
- P. J. Hurst, "A Comparison of Two Approaches to Feedback Circuit Analysis," *IEEE Trans.
  Education*, vol. 35, no. 3, pp. 253–261, August 1992.
- M. Ohtomo, "Stability Analysis and Numerical Simulation of Multidevice Amplifiers," *IEEE Trans.
  Microwave Theory Tech.*, vol. 41, no. 6/7, June/July 1993.
- A. Suarez, *Analysis and Design of Autonomous Microwave Circuits*, Wiley, 2009.
- S. A. Maas, *Nonlinear Microwave and RF Circuits*, 2nd ed., Artech House, 2003.
- A. Anakabe, N. Ayllon, J.-M. Collantes, A. Mallet, G. Soubercaze-Pun and K. Narendra,
  "Automatic Pole-Zero Identification for Multivariable Large-Signal Stability Analysis of RF and
  Microwave Circuits," *40th European Microwave Conference*, 2010.
- S. Narhi and M. Valtonen, "Stability Envelope — A New Tool for Generalised Stability Analysis,"
  *IEEE MTT-S International Microwave Symposium Digest*, 1997.
- M. Roberg and J. Hoversten, "Nonlinear Stability Analysis of Power Amplifiers Using the Stability
  Envelope," *IEEE Topical Conference on RF/Microwave Power Amplifiers*, 2015.

## Appendix A — Stability, from the beginning {#appendix-stability}

This appendix is the road that leads to the WSProbe. It follows the order the reference document's
own §2–§3 takes, one short section per method, and each ends with **in circuitRF:** — where that
quantity is, or that it is not here and why.

It is written to be read straight through by someone who has never had to argue with a stability
result. Nothing in it is required in order to use the probe.

### A.1 What "stable" means {#a-bibo}

A linear circuit is **stable** when every bounded excitation produces a bounded response — BIBO
stability. Equivalently, and more usefully: the poles of every transfer function of the network lie
strictly in the left half of the complex `s` plane. A pole with a positive real part is a response
that grows on its own, and it is a pole of the *network*, not of one transfer function — which is
why a quantity computed from a two-port view of a circuit can miss one.

Everything that follows is a way of answering "are there right-half-plane poles?" without factoring
a polynomial you do not have.

**In circuitRF:** the whole of this page.

### A.2 Negative resistance {#a-negr}

The oldest microwave answer, and the one that still frames the language. An oscillator is a
one-port with negative resistance facing a passive load; it starts up when the total resistance seen
around the loop is negative and the total reactance cancels (Eq. 9–10).

<figure class="fig">
<svg viewBox="0 0 520 170" role="img" aria-label="A negative-resistance one-port facing a passive load" style="width:100%;height:auto">
  <g fill="none" stroke="var(--text)" stroke-width="1.6" stroke-linecap="round">
    <rect x="40" y="40" width="170" height="90" rx="6" stroke="var(--border)" stroke-width="1.4" fill="var(--surface)"/>
    <rect x="310" y="40" width="170" height="90" rx="6" stroke="var(--border)" stroke-width="1.4" fill="var(--surface)"/>
    <path d="M210 62 H 310 M210 108 H 310"/>
    <circle cx="260" cy="62" r="3.5" fill="var(--text)" stroke="none"/>
    <circle cx="260" cy="108" r="3.5" fill="var(--text)" stroke="none"/>
  </g>
  <g font-family="var(--font-sans)" font-size="14" fill="var(--text)">
    <text x="125" y="80" text-anchor="middle">active</text>
    <text x="125" y="100" text-anchor="middle">Z(ω, A) = −R + jX</text>
    <text x="395" y="80" text-anchor="middle">passive</text>
    <text x="395" y="100" text-anchor="middle">ZL(ω) = RL + jXL</text>
    <text x="260" y="150" text-anchor="middle" fill="var(--muted)">start-up: R &gt; RL and X + XL = 0</text>
  </g>
</svg>
<figcaption>Kurokawa's two-block picture. The whole of the WSProbe's primary metric is this condition
written at an arbitrary internal node rather than at a designed oscillator's port.</figcaption>
</figure>

Kurokawa's contribution was to make the *sense* of the crossing part of the condition (Eq. 105–108):
it is not enough for the reactance to cancel where the resistance is negative — the imaginary part
must be *increasing* through the crossing, which distinguishes a growing solution from the ordinary
steady-state resonance every passive tank has.

**In circuitRF:** this is [the primary metric](#reading) — the three conditions applied to `1/H0` and
`1/Y0` at any node, by `wsp_unstable_freq_kurokawa` and the trace card's own reading.

### A.3 Rollett's K, and the proviso {#a-rollett}

For a two-port, Rollett's factor and the auxiliary condition (Eq. 5–8):

```text
K = (1 − |S11|² − |S22|² + |Δ|²) / (2·|S12·S21|)          Δ = S11·S22 − S12·S21
unconditionally stable  ⟺  K > 1  AND  |Δ| < 1
```

`K > 1` alone is not a criterion, which is why circuitRF offers `|Δ|` as its own trace rather than
folding it in.

**The proviso is the part that gets forgotten.** Rollett's derivation assumes the two-port has **no
right-half-plane poles when its ports are terminated in the reference impedance**. `K` and `|Δ|` are
computed from the terminal S-parameters, and terminal S-parameters are a *reduction* of the network.
A pole the reduction hides is a pole `K` cannot see — and [§10's example](#ndf) is a two-port whose
terminal S-parameters are those of a 6 dB pad and which oscillates.

**In circuitRF:** `K`, `|Δ|`, `μ` and `μ′` are on
[the derived-metrics page](derived-metrics.html#stability), for any network source — including the
reduced two-port at a probe, and the inner block of a [probe pair](#pairs). The proviso is what the
[NDF](#ndf) exists to discharge.

### A.4 Barkhausen's loop gain {#a-barkhausen}

Break the loop, inject, measure what comes back: oscillation when the returned signal equals the
injected one, `T = 1` at zero phase (Eq. 11–13).

It is the intuition everyone carries and it is not a criterion. **You cannot break the loop of a
circuit that is biased through it**, real circuits have more than one loop, and the answer depends on
where you broke it. Every objection the modern methods raise is an objection to this step.

**In circuitRF:** all eight `wsp_loopgain` kinds — none of which requires breaking anything, because
the injections are mathematical. And all eight carry [the same caveat](#reading).

### A.5 Bode's return ratio and return difference {#a-bode}

Bode's reformulation is the one that survives. For a dependent source of gain `k` in a network whose
determinant is `Δ`, define the **return difference**

```text
F = Δ / Δ0                                              (Eq. 14–16)
```

where `Δ0` is the same determinant with that source's gain set to zero. `F = 1 + T`, where `T` is the
**return ratio** — the loop gain, but defined as a ratio of two determinants of the *whole* network
rather than as something you measure after cutting a wire.

<figure class="fig">
<svg viewBox="0 0 560 200" role="img" aria-label="Bode's return ratio: a dependent source with the rest of the network as its return path" style="width:100%;height:auto">
  <g fill="none" stroke="var(--text)" stroke-width="1.6" stroke-linecap="round">
    <rect x="200" y="24" width="160" height="66" rx="6" stroke="var(--border)" stroke-width="1.4" fill="var(--surface)"/>
    <rect x="200" y="118" width="160" height="58" rx="6" stroke="var(--border)" stroke-width="1.4" fill="var(--surface)"/>
    <path d="M200 57 H 120 V 147 H 200"/>
    <path d="M360 57 H 440 V 147 H 360"/>
    <path d="M148 57 h 0 M132 51 l 8 6 l -8 6" stroke-width="1.4"/>
    <path d="M412 147 h 0 M428 141 l -8 6 l 8 6" stroke-width="1.4"/>
  </g>
  <g font-family="var(--font-sans)" font-size="14" fill="var(--text)">
    <text x="280" y="55" text-anchor="middle">dependent source</text>
    <text x="280" y="75" text-anchor="middle" fill="var(--muted)">gain k</text>
    <text x="280" y="145" text-anchor="middle">the rest of the network</text>
    <text x="280" y="164" text-anchor="middle" fill="var(--muted)">the return path</text>
    <text x="280" y="196" text-anchor="middle" fill="var(--muted)">F = Δ / Δ₀ = 1 + T</text>
  </g>
</svg>
<figcaption>The return difference of one dependent source: the determinant of the network with the
source active, over the determinant with its gain set to zero. Nothing is disconnected.</figcaption>
</figure>

**In circuitRF:** `F = 1 − LG` is one of every probe's [six default outputs](#computes), and the four
`F_LG…` return differences of a [probe pair](#pairs) are on the card. The sign convention here is the
reference document's — it defines `LG = −T` — so `F = 1 − LG` and Bode's `F = 1 + T` are the same
number.

### A.6 The NDF {#a-ndf}

Bode's return difference for **one** source generalises to a whole network of them. Take the
determinant with every dependent source active over the determinant with every one of them
passivated (Eq. 17): the ratio has no poles, and its zeros are the network's poles. Count the
clockwise encirclements of the origin and you have counted the right-half-plane poles — rigorously,
for an N-port, with no proviso and no loop to break.

Struble and Platzker's sequential form makes the same object out of return differences taken one
device at a time, which is a useful way to see *which* device is responsible.

**In circuitRF:** [a native analysis](#ndf), `NDF=yes`, exact for every built-in active model. The
reason the reference document treats the NDF as difficult and this program does not is entirely that
circuitRF owns its own device models and a black-box model withholds exactly what `Δ0` needs.

### A.7 Jackson's stability index {#a-jackson}

A reformulation of the proviso as a measurement rather than an assumption: sum the reflections around
the loop formed by the two subnetworks either side of a plane, and read the result as a Nyquist locus
(Eq. 18–20, App. B). With one plane it is the product of the two reflection coefficients looking each
way.

**In circuitRF:** it is [Ohtomo's `G_i`](#global) at `N = 1` — `wsp_loopgain_ohtomo` over a
single-probe set returns exactly `ΓP·ΓA`.

### A.8 Middlebrook's double injection {#a-middlebrook}

The measurement answer to "you cannot break the loop": inject a small **voltage** in series and a
small **current** in shunt at the same point, without disconnecting anything, and combine the two
results (Eq. 21–23):

```text
1/T = 1/Tv + 1/Ti
```

`Tv` from the series injection, `Ti` from the shunt one. It is the first method that gets a loop gain
out of an intact circuit, and it is the direct ancestor of what the WSProbe does.

<figure class="fig">
<svg viewBox="0 0 560 210" role="img" aria-label="Double injection: a series voltage and a shunt current at one node" style="width:100%;height:auto">
  <g fill="none" stroke="var(--text)" stroke-width="1.6" stroke-linecap="round">
    <path d="M60 70 H 220"/>
    <circle cx="256" cy="70" r="26"/>
    <path d="M282 70 H 460"/>
    <path d="M240 70 H 248 M244 64 V 76" stroke-width="1.4"/>
    <path d="M264 70 H 272" stroke-width="1.4"/>
    <path d="M150 70 V 160"/>
    <circle cx="150" cy="160" r="22"/>
    <path d="M150 182 V 194 H 138 M150 194 H 162" stroke-width="1.4"/>
    <path d="M150 174 V 148 M143 156 L150 148 L157 156" stroke-width="1.4"/>
    <circle cx="150" cy="70" r="4" fill="var(--text)" stroke="none"/>
  </g>
  <g font-family="var(--font-sans)" font-size="14" fill="var(--text)">
    <text x="256" y="36" text-anchor="middle">series voltage → Tv</text>
    <text x="182" y="166">shunt current → Ti</text>
    <text x="80" y="60" fill="var(--muted)">loop</text>
    <text x="430" y="60" text-anchor="end" fill="var(--muted)">loop</text>
  </g>
</svg>
<figcaption>One node, two injections, nothing disconnected. A WSProbe applies exactly these two
stimuli — and reads two responses at every probe rather than one at the injection point, which is
what turns a loop gain into a matrix.</figcaption>
</figure>

**In circuitRF:** `wsp_loopgain(…, "MB")` and `"MBR"`. `Tv` and `Ti` are not offered individually —
the combination is the quantity, and the document's own sign chain for the pair is inconsistent as
printed, so circuitRF implements the closed form that a direct derivation confirms.

### A.9 Tian's bilateral return ratio {#a-tian}

Middlebrook's combination assumes the injection point is unilateral. Tian's correction removes that
assumption and gives the **bilateral** return ratio, which is exact at any node of any network
(Eq. 24–25). It is the loop gain circuitRF reports as `LG`:

```text
LG = −(y12 + y21) / (y11 + y22) = (z12 + z21) / (z11 + z22)      (Eq. 53–60, 92)
```

over the reduced two-port at the probe.

**In circuitRF:** every probe's default `LG`, and `wsp_loopgain(…, "BI")` — one implementation, so
the run's cube and the card's function are the same number.

### A.10 Auxiliary generators and pole-zero identification {#a-auxgen}

The modern industrial answer to large-signal stability: attach a small auxiliary generator at a node,
sweep it, and identify the poles and zeros of the resulting transfer function from the fitted
response. It is rigorous and it works on driven, nonlinear circuits.

Its price is that a **transfer function has zeros as well as poles**, and a zero of the particular
function you fitted can cancel the pole you were looking for. That is why the identification is run
at several nodes and with more than one observable.

**The connection to this page is Eq. 26–30.** By Cramer's rule, the driving-point impedance at node
`n` is a *cofactor* over the network determinant, `Zn = Cof11/|Y|`, and a general transfer impedance
is `Hij = Cofij/|Y|`. So `H0` and `Y0` carry the **whole network determinant** in their denominators
— every pole of the network is in them — while their numerators are cofactors, which is exactly where
a masking zero can live. That is the algebraic reason [both must be checked](#reading), and the
reason a loop gain, which is a ratio of cofactors, carries no determinant at all.

**In circuitRF:** not available as a pole-zero identification. `H0` and `Y0` are the two functions
that carry the determinant, and the [margin](#stability-margin) is what makes their reading a
distance rather than a verdict.

### A.11 The S-parameter probe, and why it fails with feedback {#a-sprobe}

A natural idea: put a port at the node, measure `S11`, and read the impedance each way. It works
when the two sides of the node are otherwise unconnected, and it fails as soon as they are not —
because a single reflection coefficient at a node cannot separate what is on one side from what is on
the other when a path joins them around the port.

This is the same statement as [`ZG ≠ 1/YG`](#reading): under feedback there are *two* impedances at
that node and one measurement cannot report both.

**In circuitRF:** `ZG`, `ZL`, `YG` and `YL` — four numbers where the naive probe has one, from a
single sweep, correct under any amount of feedback.

### A.12 Ohtomo, and the multi-device case {#a-ohtomo}

A parallel-combined amplifier has a mode the ports cannot see. Ohtomo's method splits the network at
several planes at once, forms the wave-domain loop matrix and reads its principal minors as a set of
global loop gains whose product is the Nyquist determinant of the whole loop.

**In circuitRF:** [`wsp_loopgain_ohtomo`](#global), over any probe set, with the active side named
explicitly.

### A.13 The stability envelope {#a-envelope}

Närhi and Valtonen's insight was that "is it stable" is the wrong question for a circuit that will be
sold into an unknown VSWR: the useful answer is the *set of terminations* at which it remains stable.
Roberg and Hoversten carried it into the large-signal regime.

What [E] changed is the cost and the resolution. The 2025 paper's envelope is computed from the
**already-solved** `wsp` matrix — no re-simulation per termination — and it reports [the
margin](#stability-margin) rather than a binary verdict, so the envelope has contours instead of a
boundary ([E] §I).

**In circuitRF:** [the envelope](#envelope), including the NDF over the same grid for the comparison
[E] makes.

### A.14 How four transfer functions reproduce all of it {#a-summary}

The reference document's own summary (§3.12) is that the four transfer functions per probe are
sufficient for every method above, at any node, from one sweep:

| Method | Recovered from `wsp` as |
|---|---|
| Rollett's K, μ, \|Δ\|, stability circles | the reduced two-port at the probe (Eq. 44/48), fed to the ordinary [network metrics](derived-metrics.html#stability) |
| Barkhausen / a broken loop | any of the eight `wsp_loopgain` kinds |
| Bode's return difference | `F = 1 − LG`, and the four `F_LG…` of a probe pair |
| Middlebrook's double injection | `"MB"`, `"MBR"` |
| Tian's bilateral return ratio | `LG`, the default |
| Hurst's two-block form | `"HST"`, and `LGH` for a pair |
| Jackson's stability index | Ohtomo's `G_i` at one probe |
| Ohtomo's global loop gains | `wsp_loopgain_ohtomo` over a probe set |
| The NDF, reduced | `wsp_ndf` over a probe set, against a passivated run |
| Negative resistance at a node | `ZG`, `ZL`, `YG`, `YL` — four numbers, not one |
| Kurokawa's start-up condition | `1/H0` and `1/Y0`, the two that carry the determinant |
| The stability envelope | the rank-1 re-termination of the whole matrix |

### A.15 And then: how close? {#a-howclose}

Every method above answers a **yes/no** question. That is the right question for a circuit you are
about to build once. It is the wrong one for a circuit that will be built ten thousand times across a
process corner, because it says nothing about which of two stable designs is one component tolerance
from oscillating.

[M] §I is the step from *is it stable* to *how close*, and it is the last thing this page has to say
about the subject: the two driving-point functions are each a sum of two immittances, and the
normalised comparison of those two halves is a bounded, unitless distance that means the same thing
at every node of every circuit.

**In circuitRF:** [`SM_Y0` and `SM_H0`](#stability-margin), beside the six defaults, on every probe,
in every analysis, at no extra cost.
