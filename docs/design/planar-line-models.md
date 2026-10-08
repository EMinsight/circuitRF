# circuitRF — Grounded Coplanar Waveguide and Stripline Models

**Status:** Shipped · **Date:** 2026-10-08 · **Brief:** `docs/sonnet-briefs/brief-artsch-1-cpwg-and-stripline-models.md`
(AS-1 of the Create Schematic from Artwork series)

Two circuit components as first-class as MLIN: **`CPWG`**, the conductor-backed (grounded) coplanar
waveguide, and **`SLIN`**, the stripline, centred or offset. Both bind to a technology's stackup the way
MLIN does, carry conductor and dielectric loss, stamp through `TLineModel.StampUniformLine`, and are
validated against references produced outside circuitRF (`testdata/planar-lines/`). The microstrip family
is specified in `microstrip-models.md`; this note covers only what is new.

Code: `src/Core/Devices/Planar/` (`GroundedCoplanar`, `Stripline`, `EllipticRatio`, `PlanarLineLoss`,
`PlanarLineSynthesis`), `src/Core/Devices/CoplanarLineModel.cs`, `src/Core/Devices/StriplineModel.cs`,
the stackup binding `src/Design/Schematic/PlanarLineSubstrateInjection.cs` with
`SubstrateResolver.ResolveStripline`.

---

## 1. Conventions

- **Lengths are SI**, after the elaborator's unit scale, in MLIN's spelling: `W`, `L`, `T`, `Er`, `TanD`,
  `Sigma`, `Roughness`; CPWG adds `G` and `H`, SLIN `H1` and `H2`.
- **η₀ = µ₀·c**, with µ₀ = 4π·10⁻⁷ (`PlanarLineLoss.Eta0`). The sources' "30π" and "60π" are η₀/4 and η₀/2
  with η₀ rounded to 120π, which is 0.07 % from the constant. The field-solve references see that offset,
  so the models do not carry it. (MLIN's Hammerstad–Jensen keeps its published "60", and CPWG's microstrip
  branch inherits it unchanged — see §2.3.)
- **K(k)/K(k′) by the arithmetic–geometric mean** (`EllipticRatio.Of`): K(k)/K(k′) = AGM(1, k)/AGM(1, k′),
  exact to machine precision. The modulus and its complement are both inputs, because each formula can
  state k′ directly and √(1 − k²) loses every digit of it on a wide line.
- **Out of range is a warning, never a refusal**, reported once per instance through the same
  `MicrostripValidityReporter` MLIN uses and drained into the run's Messages.

## 2. CPWG

### 2.1 Static: Ghione–Naldi with the slot walls

Capacitances in units of 2ε₀ per metre, so Z₀ = (η₀/2)/√(C·C_air) and ε_eff = C/C_air. With a = W/2 and
b = W/2 + G (G. Ghione and C. U. Naldi, "Coplanar waveguides for MMIC applications: effect of upper
shielding, conductor backing, finite-extent ground planes, and line-to-line coupling," IEEE Trans. MTT
35(3), 260–267, 1987):

- the air above, the coplanar map: q_c = K(k)/K(k′), k = a/b;
- the substrate below, to the backing plane: q_b = K(k₃)/K(k₃′), k₃ = tanh(πa/2h)/tanh(πb/2h);
- C = q_c + εr·q_b + w, C_air = q_c + q_b + w.

k₃′ is evaluated in u = e^(−2x) so neither tanh rounds to 1 on a wide line (`GroundedCoplanar.BackedModuli`).

**Thickness — the slot walls, w = 2 × 0.7·t/G.** Each slot's two copper walls add an air capacitance:
the 0.7·t/G term of Gupta, Garg, Bahl and Bhartia's CPW thickness correction ("Microstrip Lines and
Slotlines," 2nd ed., Artech House, 1996, eq. 7.100), here added to *both* capacitances so Z₀ and ε_eff come
from one consistent pair. At h → ∞ this reduces exactly to their CPW ε_eff correction.

The same book's **effective-width form** (W + Δ, G − Δ, Δ = (1.25t/π)(1 + ln(4πW/t))), which the brief
named, was implemented first and measured against the field solve: it over-corrects about two-fold —
**2–7 % low on Z₀** on 18–35 µm copper — where the slot-wall form is within 0.6 %. It was replaced on that
evidence (`src/Core/RESOLVED.md`, AS-1).

### 2.2 The microstrip branch

The coplanar map keeps the strip's upper-side field between the strip and the coplanar grounds. As G
grows, that field reaches the backing plane around the slots instead, and G-N's air capacitance falls
*below* the bare microstrip's: as G → ∞ on W/H = 1, εr 4.4, G-N reads 86.1 Ω where the microstrip is 71.0 Ω. Adding grounded conductors never lowers a
conductor's capacitance, so the true C_air is at least the microstrip's. The model is therefore **the
branch with the larger C_air**, C and C_air both taken from it:

- the coplanar branch (§2.1) while the coplanar ground is close;
- the microstrip branch — MLIN's own `HammerstadJensen.Compute` with the same slot walls added (they vanish
  as t/G) — once it is far. At G → ∞ the model **is** MLIN, statically and at every frequency (§2.3).

Taking the larger of C and of C_air *separately* was measured and rejected: near the crossover it pairs
one branch's C with the other's C_air and ε_eff came out 5.8 % wrong.

### 2.3 Dispersion

- **Coplanar branch:** M. Y. Frankel, S. Gupta, J. A. Valdmanis and G. A. Mourou, "Terahertz attenuation and
  dispersion characteristics of coplanar transmission lines," IEEE Trans. MTT 39(6), 910–916, 1991:
  √ε(f) = √ε₀ + (√εr − √ε₀)/(1 + G_f·(f/f_TE)^−1.8), f_TE = c/(4h√(εr − 1)), G_f = exp(u·ln(W/G) + v),
  u = 0.54 − 0.64p + 0.015p², v = 0.43 − 0.86p + 0.54p², p = ln(W/h). The coefficients were checked against
  scikit-rf's implementation (BSD-3-Clause). Z₀(f) = Z₀(0)·√(ε₀/ε(f)) — the air capacitance held.
  The fit's own parameter range could not be confirmed from an accessible source, so the reporter bounds
  W/G and W/H to a decade either side of 1 rather than quoting one.
- **Microstrip branch:** MLIN's Kirschning–Jansen, called exactly as MLIN calls it. Frankel's G_f goes to
  zero as W/G does, which would put ε(f) at εr for every f > 0 — the reason the branch hands dispersion over.

### 2.4 Loss

- **Conductor:** Wheeler's incremental-inductance rule (H. A. Wheeler, "Formulas for the skin effect,"
  Proc. IRE 30(9), 412–424, 1942), the route by which Owyang and Wu derived the coplanar line's conductor
  loss from its conformal map (IRE Trans. MTT 6, 1958), applied to *this model's own* air impedance:
  α_c = Rs·K_r·∂Z₀,air/∂n / (2·Z₀·η₀), every copper surface receded by dn — W − 2dn, G + 2dn, h + 2dn,
  t − 2dn — and the derivative taken by a central difference of the closed form (dn = 10⁻⁴ of the smallest
  dimension; `PlanarLineLoss`). The backing plane's loss is in it, because the backing plane is in Z₀,air.
- **Rs for a finite thickness:** each face of the strip is a slab t/2 thick, Re{Zs·coth(γt/2)} with
  Zs = (1+j)/(σδ): Rs when t ≫ δ, and 2/(σt) per face — the DC sheet resistance with both faces in parallel —
  when t ≪ δ. Below t ≈ 2δ the skin-effect geometry (current crowding toward the edges) is kept with the
  thin-slab resistance, so the loss there is an estimate.
- **Roughness:** MLIN's Hammerstad–Bekkadal factor, K_r = 1 + (2/π)·arctan(1.4(Δ/δ)²).
- **Dielectric:** the filling factor, MLIN's `MicrostripLoss.DielectricLossNpPerM` with the model's ε_eff(f).

### 2.5 Accuracy and validity

Against the field solve (testdata/planar-lines, README): **Z₀ within 2.3 %** at every geometry measured —
W/H 0.19–2.5, G/H 0.3–6, εr 3–12.9, t/G to 0.18. **ε_eff within 0.9 %** on the coplanar branch, and within
**4.1 %** on the microstrip branch with 35 µm copper: that is Hammerstad–Jensen's thickness correction, as
far off at G/H = 6, where the coplanar ground no longer matters.

| Checked | Range | Why |
|---|---|---|
| W/H | 0.1 – 5 | about twice past what the field solve covered |
| G/H | ≥ 0.1 | a far ground is the microstrip branch, which is MLIN |
| t/G | 0 – 0.2 | the slot-wall term assumes a thin wall in a wide slot; measured to 0.18 |
| W/G, W/H (dispersion) | 0.1 – 10 | Frankel's fitted coefficients; source range unconfirmed |

Hammerstad–Jensen's and Kirschning–Jansen's own ranges are reported too, through the same calls MLIN makes.

## 3. SLIN

### 3.1 Centred — Cohn exact with Wheeler's thickness ratio

- **Zero thickness, exact** (S. B. Cohn, "Characteristic impedance of the shielded-strip transmission
  line," IRE Trans. MTT 2(2), 52–57, 1954): Z₀,air = (η₀/4)·K(k)/K(k′), k = sech(πW/2b), k′ = tanh(πW/2b).
- **Thickness — Wheeler 1978** (H. A. Wheeler, "Transmission-line properties of a strip line between
  parallel planes," IEEE Trans. MTT 26(11), 866–876, 1978): the effective width W′ = W + ΔW,
  ΔW/(b−t) = x/(π(1−x))·{1 − ½ln[(x/(2−x))² + (0.0796x/(W/b + 1.1x))^m]}, m = 2/(1 + ⅔·x/(1−x)), x = t/b,
  into Z₀,air = (η₀/4π)·ln{1 + (4/π)(b−t)/W′·[(8/π)(b−t)/W′ + √(((8/π)(b−t)/W′)² + 6.27)]}.

Wheeler's form is applied as **the ratio of his thick-strip value to his own zero-thickness value**,
multiplying Cohn's exact one: t = 0 is then exact rather than Wheeler's approximation of it, and the
thickness effect is his. Against the field solve: within 0.03 %.

### 3.2 Offset — the parallel combination

Each half is a centred line of plane spacing bᵢ = 2Hᵢ + t; the offset line's capacitance is half of each,
so Z₀ = 2·Z₁Z₂/(Z₁ + Z₂) (B. C. Wadell, "Transmission Line Design Handbook," Artech House, 1991, §3.5.2).
It ignores how the two halves' fringing fields meet at the strip's edges, which grows with the offset:
**+1.2 % at H₂/H₁ = 2, +3.4 % at 4.** The reporter warns past 2. At H₁ = H₂ it *is* the centred line, and
swapping H₁ and H₂ changes nothing.

### 3.3 Dispersion and loss

TEM in a homogeneous dielectric: ε_eff = Er at every frequency and Z₀ = Z₀,air/√Er. Conductor loss by
Wheeler's rule as §2.4 (W − 2dn, H₁ + 2dn, H₂ + 2dn, t − 2dn), with the same finite-thickness Rs and
roughness; dielectric loss tanδ only, α_d = π·f·√εr·tanδ/c.

| Checked | Range | Why |
|---|---|---|
| W/b | 0.05 – 2.5 | about twice past the field solve's 0.11 – 1.2 |
| t/b | 0 – 0.12 | about twice past the field solve's 0.06 |
| max(H₁,H₂)/min | 1 – 2 | the offset combination's 1.5 % |

## 4. Stackup binding

`PlanarLineSubstrateInjection.Build` — at extraction (`NetExtractor`), for a hand-written `.cnl`
(`CnlTechnologyBinding`), in the parameter editor and in the line calculator, so all four agree.

- **CPWG** binds exactly as MLIN: `SignalLayer` (default the top conductor) and `GroundReference` (default
  the nearest ground-designated conductor below, else above) — `H`, `T`, `Er`, `Sigma`, `TanD`, multi-layer
  dielectric thickness-weighted as MLIN's is. `G` is the instance's own; a technology has no gap. A signal
  layer with a plane on *both* sides warns that CPWG models only the backing plane (D13 sends that line,
  a stripline with coplanar ground, to TLIN).
- **SLIN** binds the nearest ground-designated conductors **above and below** the signal layer
  (`SubstrateResolver.ResolveStripline`): `H1`, `H2` as the dielectric to each, `T`, `Sigma`, and `Er`/`TanD`
  thickness-weighted over everything between the two planes. A spread above 10 % of the weighted εr is a
  warning naming each layer and the spread — the model is homogeneous, so it is answering for a mixture.
  SLIN takes `SignalLayer` only; its default is **the topmost conductor with a plane on both sides**,
  because the technology's default signal layer is its top copper, which never has one.
- **A SLIN on a layer with one plane is a refusal** naming the missing plane — in the extractor (a
  conflict), and again when the netlist is read to run (`CnlTechnologyBinding` throws), whether or not the
  line named its layer. A stripline with one plane is a microstrip; simulating it on the fallback board
  would be a silent MLIN. With **no technology at all**, both lines fall back to the models' standalone
  defaults (MLIN's 1.6 mm FR-4; SLIN centred in it, H₁ = H₂ = 0.8 mm), with the reason as a warning, as
  MLIN does.
- **Placement defaults:** a placed CPWG or SLIN has its W synthesised for 50 Ω on its own model and
  binding (`PlanarLineSynthesis`), CPWG's gap rounded in the technology's unit first.

## 5. Where it shows

- **Line Calculator** (`LineCalculator`, `circuitrf impedance --tech`): the model column is MLIN, **CPWG**
  when a gap is given, or **SLIN** when the layer has a plane on each side; a gap on such a layer is a
  stripline with coplanar ground and only the cross-section answers. Synthesis uses the same functions as
  the parameter editor. For a SLIN, the calculator draws both planes in its in-memory layout: the trace
  review takes an undrawn ground layer as an implied plane only below a trace (`src/Design/RESOLVED.md`).
- **Parameter editor** Z0 field: CPWG and SLIN show Z₀ and ε_eff from their own model at the top frequency
  of the schematic's S-parameter analyses (static when there is none); a Z0 typed there synthesises W at that
  frequency.
- **LVS** classifies both as `TransmissionLine`, as every line is.

## 6. Not modelled

Finite coplanar ground width (the coplanar grounds are taken as wide); a lid or upper shield over a CPWG;
coupled CPW or coupled stripline; discontinuities of either family (a CPWG/SLIN bend is centre-line length,
a junction a plain node — D14); ungrounded CPW (TLIN, D13); PCells for either (D18). No physics reference covers dispersion or loss: a
quasi-static solve has neither, so those rest on the implementation table and the published forms.
