---
title: Vias
slug: reference/vias.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Vias
lede: VIA and VIAGND put a plated via into a schematic with no EM run — its inductance, resistance and capacitance worked out from the stackup, the formulas they come from, where they hold, and how far they land from a 3D solve.
keywords: via, VIA, VIAGND, plated through hole, PTH, layer change, stub, antipad, Goldfarb, Pucel, Johnson, Graham, ground via, pad capacitance
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">The two components</a></li>
<li><a href="#stackup">What comes from the stackup</a></li>
<li><a href="#model">The model</a></li>
<li><a href="#validity">Where it holds</a></li>
<li><a href="#measured">Against a 3D solve</a></li>
<li><a href="#shunt">Replacing a hand-added shunt capacitor</a></li>
<li><a href="#layout">In the layout</a></li>
</ol>
</nav>

## The two components {#what}

A line that changes layers passes through a via, and a shunt part returns to ground through one. Both
are real inductance, resistance and capacitance, and without a via component the only way to include
them is a hand-built series R–L with values guessed for the board. **VIA** and **VIAGND** compute those
values from the technology's stackup instead.

| Component | Pins | What it is |
|---|---|---|
| [VIA](components.html#via) | A on `FromLayer`, B on `ToLayer` | a signal via changing layers, referenced to ground as MLIN is |
| [VIAGND](components.html#viagnd) | A on `FromLayer` | a via from a pad down to a ground plane; its far end is ground |

Both are in the **Microstrip** filter of the Library Palette. The symbol is drawn as the barrel it is:
the upper pad with the drill opening, the plated barrel, and for VIA the lower pad, for VIAGND the plane
it lands on.

{{symbol: via}}

{{symbol: via-gnd}}

## What comes from the stackup {#stackup}

A via instance on a technology is described by two layer names and four dimensions, and **every one of
them may be left empty**:

- `FromLayer`, `ToLayer` (VIAGND: `GroundLayer`) are picked from the stackup, as a microstrip's
  `SignalLayer` is. Empty, `FromLayer` is the top conductor; `ToLayer` is the farthest conductor a
  drill reaches that is not a ground plane; `GroundLayer` is the nearest ground plane beneath.
- `Drill` and `Pad` empty take the technology's own via drill and pad.
- `Plating` empty takes the via layer's wall thickness.
- `Antipad`, the clearance in every plane the barrel passes, is the pad plus 0.3 mm when empty. No
  technology states an antipad, so the run's Messages always name this one as a default. So do any of
  the others the technology does not state.

Everything else is resolved and never typed: the **barrel length**, measured between the two
conductors' mid-planes; the **drill** it belongs to (the shortest via layer whose span covers both
conductors, so a blind via is preferred to a through via); the **stub** where that drill runs past a
conductor; the copper's conductivity; and **every ground plane the barrel passes**, each with the
length of barrel it owns (below). The parameter dialog prints what these came to, next to the L, R and
C they give.

In a `.cnl` with no technology, every value is typed:

```
VIA:V1    a b  Drill=0.3mm Pad=0.6mm Antipad=0.9mm H=1.2mm Er=4.4 Planes=1 Tp1=0.6mm
VIAGND:G1 a 0  Drill=0.3mm Pad=0.6mm Antipad=0.9mm H=1.6mm Er=4.4
```

`H` is the barrel length. `Planes=n` with `Tp1..Tpn` and `Erp1..Erpn` lists the planes passed; a stub
past B is `Hstub` with `StubPlanes`, `Tsp`, `Ersp`, and one past A is `HstubA` with `StubAPlanes`,
`TspA`, `ErspA`. `Solid=1` makes a filled barrel. `C=` replaces the computed capacitance outright, and
`IncludeC=false` drops it.

## The model {#model}

**VIA** is a symmetric T: half the barrel's `R + jωL`, the capacitance of every plane it passes to
ground, then the other half. A stub hangs off the end it continues past, as its own `R + jωL` into the
capacitance of the planes it passes. **VIAGND** is the barrel's `R + jωL` to ground with the pad's
capacitance across it, and any plane it passes on the way down at the barrel's midpoint.

**Inductance** — M. E. Goldfarb and R. A. Pucel, "Modeling via hole grounds in microstrip", *IEEE
Microwave and Guided Wave Letters* 1(6), pp. 135–137, 1991, with `r` the drill radius and `h` the barrel
length:

```
L = (µ₀/2π) · [ h·ln( (h + √(r² + h²)) / r ) + 1.5·( r − √(r² + h²) ) ]
```

For a long barrel this tends to `(µ₀/2π)·h·[ln(2h/r) − 3/2]`, which is half a barrel-length of `µ₀/2π`
below Grover's surface-current inductance of a straight tube. It is a partial self-inductance, with the
return current some distance away. A return via close by lowers the loop through a mutual term this form
does not have, so beside one it reads high. A signal via with stitching vias round it is not modelled.

**Resistance** — the same paper. `t` is the plating:

```
R = R_dc · √(1 + f/f_δ)      R_dc = h / (σ·π·(r² − (r − t)²))      f_δ = 1 / (π·µ₀·σ·t²)
```

A filled barrel is a rod, `R_dc = h/(σπr²)`, with `t = r/2` in `f_δ`, which gives the rod's own surface
resistance at high frequency.

**Capacitance** — H. Johnson and M. Graham, *High-Speed Digital Design* (1993), §7.1, with the pad `D₁`,
the antipad `D₂` and `T` in inches:

```
C ≈ 1.41 · εr · T · D₁ / (D₂ − D₁)   pF
```

The source offers this as an estimate, and it is one. Its `T` is a length of barrel: C grows with it,
and it was written for a whole board of planes. circuitRF applies it **once per plane the barrel
passes**, with `T` the barrel that plane owns: half the dielectric to the neighbouring conductor on each
side. On a board of many planes the terms add back to the board thickness, and a single plane is not
charged for barrel that runs past other conductors.

## Where it holds {#validity}

A lumped T is a fair model while the whole drill is under a twentieth of a wavelength in the densest
dielectric it crosses. The parameter dialog shows that frequency. A run that goes past it posts **one
warning for the instance** and computes anyway. That is an extrapolation, and the warning says so.
Above it, an EM solve of the via is the reference.

## Against a 3D solve {#measured}

The reference is an independent Palace solve of one transition, committed with circuitRF's test data
and never regenerated by circuitRF: a 400 µm microstrip on top of a 505 µm, εr 3.66 laminate, a solid
300 µm via with 600 µm pads through a 900 µm clearance in the middle ground plane, and an inverted
microstrip underneath (order-2 elements). The same transition as MLIN + VIA + MLIN, every value from the
stackup:

| | 1.2 GHz | 3.6 GHz | 6 GHz |
|---|---|---|---|
| \|S21\| error | −0.020 dB | −0.031 dB | −0.031 dB |
| ∠S21 error | +0.12° | +0.17° | +0.15° |
| \|S11\|, model | −37 dB | −28 dB | −25 dB |
| \|S11\|, Palace | −44 dB | −43 dB | −44 dB |

Through the via, the model agrees to 0.03 dB and 0.2°. **Its match is worse than the real via's.**
Taking the lines out of the Palace result leaves the transition's own shunt capacitance at **34 fF**
(0.5–1 GHz), counting both pads. The Johnson–Graham term here is **81 fF**, **2.4 times** that. Its
series inductance comes out near 220 pH against Goldfarb and Pucel's **72 pH**, though that figure also
carries the solve's port sheets. The two errors partly cancel in S21 and add in S11. Read a via's
return loss from this model as an estimate only.

Taking each plane's whole neighbouring dielectric as `T` instead (400 µm here) put ∠S21 4° out at 6 GHz.
That is why each plane gets half of it.

## Replacing a hand-added shunt capacitor {#shunt}

A common approximation on FR-4 is a fixed capacitance, around 1.4 pF, added in parallel with every
ideal part that goes to ground, to stand for its pad and via. VIAGND computes the via's share from the
board. On the shipped two-layer FR-4 technology (70 mil), at its own 12 mil drill and 24 mil pad, a
default VIAGND from Top Copper to the Bottom Copper plane is:

- **0.65 nH** in series with **1.4 mΩ** (DC) to ground;
- **0.88 pF** from the pad to ground in parallel, with the default 0.3 mm antipad ring;
- valid to **3.9 GHz**, where its 1.8 mm barrel is a twentieth of a wavelength.

So place the part, then a VIAGND under its grounded pin, and drop the fixed capacitor. The remainder of
a fitted 1.4 pF belongs to the part's own pads and body, which the via does not model. The pad term is
the Johnson–Graham estimate above, taken over the dielectric between the pad and the plane. The plane
the via lands on has no clearance of its own, so on a board like this treat the 0.88 pF as an upper
estimate.

## In the layout {#layout}

**Update Layout from Schematic** draws a VIA as its drill on the stackup's via layer, with a pad on
`FromLayer` and one on `ToLayer`. The layers between get no pads. Its two pins are both at the drill,
one on each layer, so a line on either layer that ends there connects to it. The layout is compared
against the schematic as a via device, with its two ends on separate nets.

The planes the via passes are not part of it. They are the stackup's planes, drawn once for every line
that returns through them by Update Layout itself, and again by **Draw Ground Pour** after you move things.
The pour cuts the via's own `Antipad` out of every plane its drill passes and does not land on, and
leaves a VIAGND joined to the plane it lands on.
