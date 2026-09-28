# The four bond-wire metals over temperature (R-em3d72-4)

Electrical resistivity ρ(T) and thermal conductivity k(T) of **gold, copper, aluminium and silver**,
0 °C to the melting point, with the melting point, density and specific heat at 25 °C. One file per
metal. Brief 73 puts these tables into `generic-materials.cmat`; the wire references (`wire-*`) read them.

## Files

| File | What |
|---|---|
| `gold.csv`, `copper.csv`, `aluminium.csv`, `silver.csv` | `T [degC]`, `rho [Ohm m]`, `k [W/(m K)]`, then where each value came from: `tabulated`, `interpolated` (linear in T between the two neighbouring tabulated rows), or `estimated-by-source` (a value the source itself labels extrapolated or estimated) |
| `constants.json` | per metal: melting point, density, specific heat, and the derived ρ₂₀, α₂₀ and k₂₀ that the W1 references use |
| `checks.json` | the checks of R-em3d72-4 (below), as numbers |
| `make_metals.py` | the transcription and the merge; `python make_metals.py` regenerates all of the above |

## Sources

These are the **recommended values for the pure, bulk, annealed metal** in the reference-data
literature, transcribed by hand into `make_metals.py`:

- **ρ(T), copper, gold and silver:** R. A. Matula, *Electrical resistivity of copper, gold, palladium,
  and silver*, J. Phys. Chem. Ref. Data **8**(4), 1147–1298 (1979). Total resistivity of the
  well-annealed high-purity metal, corrected for thermal expansion.
- **ρ(T), aluminium:** P. D. Desai, H. M. James and C. Y. Ho, *Electrical resistivity of aluminum and
  manganese*, J. Phys. Chem. Ref. Data **13**(4), 1131–1172 (1984).
- **k(T), all four:** C. Y. Ho, R. W. Powell and P. E. Liley, *Thermal conductivity of the elements: a
  comprehensive review*, J. Phys. Chem. Ref. Data **3**, Supplement 1 (1974). The same recommended
  values are reproduced in the CRC Handbook of Chemistry and Physics' table of the thermal conductivity
  of metals as a function of temperature.
- **Melting point** (the ITS-90 fixed point), **density** and **specific heat at 25 °C:** CRC Handbook
  of Chemistry and Physics.

The numbers were read from a published compilation that reproduces each paper's recommended values and
cites the paper per value; the papers above are the sources. The compilation's page was not checked
against the papers themselves (they are behind a subscription), so a transcription error in it would be
carried here — a question for anyone holding the papers, not a known problem.

Each table stops at **its source's** melting temperature (a solid-phase value at the melting point),
which predates ITS-90 and sits up to 0.25 K above the ITS-90 fixed point in `constants.json` — gold's ρ
table ends at 1064.43 °C, the fixed point is 1064.18 °C. The rows below 20 °C (0 °C, and k's
0.05 °C row) are kept only so that interpolation to 20 °C is between tabulated neighbours.

## Pure bulk metal, not bond wire

Bond wire is drawn, often lightly doped (gold wire commonly carries tens of ppm of beryllium or calcium
for loop stiffness; "4N" is 99.99 %), and not always annealed. Every one of those raises ρ₂₀ above the
pure-metal value while leaving **dρ/dT almost unchanged** (Matthiessen's rule: an impurity adds a
temperature-independent term). No wire-specific source was consulted for this brief, so no wire-specific
difference is stated; the checks below show the rule at work in the values `generic-materials.cmat`
already carries.

## Derived quantities (in `constants.json`)

- **ρ₂₀** is the table at 20 °C by its own linear interpolation.
- **α₂₀** is the **tangent** at 20 °C, divided by ρ₂₀: a quadratic through the tabulated rows from
  250 K to 350 K (the 250 K row is used for this fit only and is not in the CSV), differentiated at 20 °C.
- **k₂₀** is the table at 20 °C.

## The checks (numbers in `checks.json`)

**Gold's k(T) against the owner's figures.** At 125 °C the table gives **311.1 W/(m·K)** against the
owner's 312 (−0.3 %); at 927 °C it gives **255.0** against 262 (**−2.7 %**). Both inside the "few percent
between references" the brief expects; nothing to raise.

**ρ₂₀ and α₂₀ against `generic-materials.cmat`** (a disagreement over 1 % is reported, not resolved —
brief 73's table-wins rule decides which number is in force):

| Metal | ρ₂₀ table | ρ₂₀ cmat | Δρ₂₀ | α₂₀ table | α₂₀ cmat | Δα₂₀ | Δ(dρ/dT) |
|---|---|---|---|---|---|---|---|
| gold | 2.215e-8 | 2.439e-8 | **+10.1 %** | 0.003699 | 0.0034 | **−8.1 %** | +1.2 % |
| copper | 1.679e-8 | 1.724e-8 | **+2.7 %** | 0.004028 | 0.0039 | **−3.2 %** | −0.6 % |
| aluminium | 2.652e-8 | 2.653e-8 | +0.03 % | 0.004445 | 0.0039 | **−12.3 %** | **−12.2 %** |
| silver | 1.588e-8 | 1.587e-8 | −0.04 % | 0.003794 | 0.0038 | +0.2 % | +0.1 % |

Gold and copper are Matthiessen's rule exactly: the `.cmat` ρ₂₀ is a less pure metal's (gold's record
says so — "representative of deposited gold"; copper's is 100 % IACS, the annealed-commercial standard)
and its α₂₀ is lower by the same factor, so the **absolute slope dρ/dT agrees within 1.2 %**. Either pair
describes a real metal; they are different metals. Aluminium is different in kind: its ρ₂₀ agrees and its
**slope is 12 % low** — 0.0039 /K is the coefficient usually quoted for conductor-grade aluminium alloy,
not the pure metal the ρ₂₀ belongs to. Under brief 73's table-wins rule, a wire of this table's aluminium
heats with a 12 % steeper ρ(T) than the `.cmat` coefficient says.

**Is ρ(T) linear enough for W1's closed form?** The largest deviation of the table from the 20 °C
tangent line ρ₂₀[1 + α₂₀(T − 20)], anywhere up to the melting point, is the table's upward curvature:

| Metal | from the 20 °C tangent | where | from the best straight line |
|---|---|---|---|
| gold | 19.5 % | at the melting point | 17.5 % |
| copper | 12.7 % | at the melting point | 12.1 % |
| aluminium | 3.5 % | at the melting point | 5.0 % |
| silver | 13.7 % | at the melting point | 10.1 % |

Below ~300 °C every metal is within ~1–2 % of its tangent, which is why W1's linear ρ(T) is a fair
model for a wire in normal operation and a poor one for predicting **fusing**: at the centre-melt point
gold's real resistivity is ~20 % above the linear model's, so a linear-ρ fusing current is optimistic.
That is the reason W2 uses the tables.
