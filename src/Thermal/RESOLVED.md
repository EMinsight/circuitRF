# `src/Thermal` — findings

## brief-em3d-74 — the solver (2026-09-28)

**Gmsh's second-order node order, checked, not assumed.** Meshing a box at `Mesh.ElementOrder = 2` with Gmsh 4.15.2 and
comparing every mid-edge node with its edge's midpoint: a 10-node tetrahedron's mid-edge nodes are (0,1) (1,2) (0,2)
(0,3) (2,3) (1,3); a 6-node triangle's (0,1) (1,2) (2,0). VTK's quadratic/Lagrange tetrahedron swaps the last two, so the
field writer (src/Design/Thermal/ThermalFieldFiles) swaps them on the way out.

**The quadrature.** Volume: the 14-point rule of degree 5 (positive weights). A P2 stiffness at constant k on a straight
element is only degree 2, but a Newton tangent multiplies k(T_h) into it and a curved element's Jacobian is not constant.
Surface: Dunavant's 6-point degree-4 rule, exact for the P2 Robin mass. Both were checked on every monomial of their
degree (worst relative error 6e-16 and 1.5e-15).

**Determinism by colouring, not by triplet lists.** Elements are coloured greedily in element order on their CORNER nodes
(two elements sharing a mid-edge node share its corners), colours assembled in sequence, each colour in parallel: every
matrix slot receives its contributions in colour order, at most one per colour. The hash is identical at 1 and 8 threads
(gate 6). Per-thread triplet lists would also be deterministic but need a sort and n·100 doubles of memory.

**Temperatures are °C throughout the solver.** Steady conduction with Dirichlet, Robin and flux conditions is invariant
under a shift of the temperature scale and nothing here is radiative, so no kelvin is ever needed; a k(T) function is
called with °C, which is what the material tables state.

**Newton's "residual fallen by 1e-8" is relative to max(‖r₀‖, ‖reduced load‖).** Relative to r₀ alone, a warm start
already at the answer (the previous sweep point, same field) would be asked to fall by 1e-8 of nothing and never stop.
A step that does not lower the residual is halved up to ten times.

**What the gates measured.** S1 exact to 2e-13 K (P1 and P2); S3 to 1e-11 K; S5 at brief 72's 5 µm / 130 µm rung:
−0.024 % peak and −0.059 % mean against the series — brief 72's own independent FEM on that rung gave −0.024 % and
−0.060 % — both solvers agreeing to 9.6e-9 K and the balance closing to 4e-14; S4 in 4 Newton steps each (closed form to
2e-13 of the rise, the silicon table to 3e-7); Q6's contrast rung in 25 PCG + AMG iterations at θ = 0 to 1e-8 (brief 72:
27, and 29 by an independent implementation). The AMG gate asserts ≤ 31.

**An iterative solve that stalls falls back to the direct one and says so** in the run's notes — a result is never the
last iterate of a CG that did not converge.

## brief-em3d-76 — interfaces and a diagonal conductivity (2026-09-28)

**Contacts are found from the tetrahedra, not from tags.** After the lowering's one fragment two touching solids share the
same triangles and nodes (brief 72 Q3), so `ThermalInterfaces.Split` finds a contact as a tetrahedron face two regions
share. Nothing about a contact has to survive Gmsh as a physical group, and a contact the document never names (a
technology material pair) is found the same way.

**Which nodes split: the star, grouped across non-resistive faces.** A node on a resistive face takes the tetrahedra
around it and unions any two that share a face through the node that is not resistive; each group after the first gets a
copy. So at a triple junction a perfect contact among the three keeps the edge's nodes shared, and a node stays single
wherever a perfect path already joins the two sides — gate 2 counts exactly the resistive face's nodes off the junction
edge (20 of them), none on it. A zero resistance splits nothing.

**The interface element is the consistent P2 mass, ∫h(T_A − T_B)(N_i^A − N_i^B).** Where a node was not split, its A and
B entries are one node and its terms cancel exactly, so a partially split face needs no special case.

**The iterative solver's default stop leaves ~1e-8 K at an interface.** S2's jump is exact to 3e-12 K with the direct
solver; PCG + AMG at the default relative residual 1e-10 reads 1.0000000244e1 K. The contrast the interface adds (h = 1e5
against k/Δz) is the cause. Gate 1 runs the iterative leg at 1e-13 and asserts it stayed iterative; the default was left
alone — 1e-8 K is far below anything a user reads.

**Anisotropy is a scalar times fixed axes** (`ThermalConductivity.Axes`), so the k(T) path and its Newton tangent carry it
unchanged. Gate 3: z, x and the slab turned 90° are exact to machine precision.

**`FixedField`** (a Dirichlet value per node from a function of position) is how a submodel's cut faces take the whole
model's solution; it is applied after the ordinary fixed faces, so a node a named face already fixed keeps that value.

## brief-em3d-77 — conductive balance and 1D wires (2026-09-28)

**The wire's coupling to a mould: the well conductance, read on a RING, not at the wire.** Brief 72 Q1 left the choice to
this brief (embedded curve with shared nodes, or non-conforming). Its own evidence decided it: a mesh's temperature AT a line
source is the continuous field at r_eff ≈ 0.12 h for second-order elements, and for a 1 mil wire that is below the wire's
radius at every mesh size brief 72 tried (8.9 µm at h = 80 µm). The Peaceman correction ln(r_eff/r_w)/(2πk) is then
NEGATIVE — no element can supply it — and refining round the wire makes the answer worse. So the wire is non-conforming: a
span element's heat enters the solid at the centreline (through the host element's shape functions there), and the wire is
driven by the host's temperature averaged over a ring of radius r_e = 1.5 host-element sizes round it, through the brief's own
g′ = 2πk / ln(r_e / r_w). A line source's discretisation error is local, so at a ring a couple of elements out the discrete
field is the continuous one, and the log drop inside the ring is exactly the well's. W4 (gold, 1 mil, 1 mm, r_o = 500 µm,
0.5 I*): 2.8e-4 and 3.6e-4 of the rise at rings of 36–65 µm and 83–136 µm (two meshes a factor 2 apart), the two agreeing to
4 mK. The mould's axial conductivity is 1e-3 of its radial one in that gate, because W4's closed form neglects the annulus'
axial conduction, which at r_o = 500 µm is NOT small (k_m r_o² / (k_w r_w²) ≈ 4). The coupling is nonsymmetric (the heat goes
in at the line and the temperature is read at the ring), which the nonsymmetric Newton solve takes anyway; energy is
conserved exactly (the balance closes to 1e-7).

**A perfect bond ties the patch to the HEEL, not the foot.** A foot coupled along its length through a penalty "perfect"
conductance has a contact boundary layer √(σA/(G″w)) — micrometres — that one P2 foot element cannot resolve, and it put a
0.4 % error on gate 9's current share. A perfect bond makes the heel the pad, so the whole patch is tied to the heel node
(a ball the same, plus its own height as a series resistance); the foot then carries nothing along itself and sits at its
pad's temperature. A STATED bond resistance keeps the distributed per-length coupling the brief describes. The penalty for
"perfect" is 1e6 × k_pad / √(patch area) for heat and 1e4 × σ_pad / √(patch area) for current: at 1e6 electrically the φ
residual sits on its own round-off (the conductance times a potential of ~0.05 V is 1e7 × the port's current), and Newton
could not certify convergence. The criterion now knows its floor: a residual counts as converged at 1000 ε of the terms that
make it, whatever 1e-8 of its reference would ask.

**What the gates measured.** W1 (gold, 1 mil, 1 mm, 25 °C, k constant): 4.9e-8, 6.1e-8 and 2.5e-7 of the rise at 0.25, 0.5
and 0.9 I*, in **3, 4 and 7 Newton steps** from a cold start (one fixed-point sweep, then Newton) at the production
tolerance — the owner expected under five away from the limit; 0.9 I* takes seven. Runaway: the sweep 0.5 → 0.9 → 1.13 I*
brackets I* = 3.13706 A in [3.12775, 3.13903] A (0.36 % wide) in 14 solves; the last converged centre is 58,800 °C — the
closed form knows nothing of melting, and neither does a linear ρ. Past I* the linear-ρ equations DO have a solution — with a
negative resistivity in the middle — which is why a conductivity at or below zero rejects a step rather than being solved.
W2 (gold tables, 0.5 of the fold): 2.8e-5 K on a 58 K rise; k(T) off moves the peak by −0.55829 K against −0.55832 K from
the tables' own first integral (a quadrature, not a finite-element solve). Newton against Picard: 1e-8 K on 37 K (Picard 12
sweeps, Newton 3). Current share: I₁/I₂ against R₂/R₁ to 6e-7 — with pads 1000× copper's σ, because a real pad's spreading
resistance under each foot (≈ ρ/4a, 3e-4 Ω) is in series with each wire and moves the share by 6e-4.

**Iterative solves of the coupled Jacobian.** BiCGStab with a block-diagonal preconditioner (one smoothed-aggregation V-cycle
on each diagonal block's symmetric part) converges in 16–36 iterations on every case above, the end-to-end model's 46,694
unknowns included. A start that already IS the answer (no current, every fixed face at one temperature) takes no step: its
residual is round-off against a reduced load of zero, and the first version, asking for a step down from there, failed
the zero-current start of a continuation after minutes of stalled iterations and direct fallbacks.

## brief-em3d-78 — RF harmonic currents in wires (2026-09-28)

**The RF heat is a load with a temperature slope, and nothing else.** Σₙ ½|Iₙ|²R′_ac(fₙ, σ(T)) enters each wire element's T rows
as a heat per unit length, and its derivative (through dσ/dT and the skin depth) enters the tangent as −dq′/dT NᵢNⱼ. It touches
no φ row: the harmonic currents are prescribed per wire (the design layer computes them from wBond's share), so there is no
electrical unknown to solve. A foot lying on its pad takes none: its current passes into the pad, which is what the DC solve
says too, and the wire's current is taken as uniform heel to heel. This project still names no wire physics — R′_ac arrives
as a delegate (`AcResistance`), which the lowering fills from `InternalImpedance`.

**dR′/dσ from N alone.** With N = Z_int/R_dc and z = (1+j)q, dN/dz = 2N/z + z/2 − 2N²/z (from dρ/dz = 1 − ρ² + ρ/z, ρ = I₀/I₁),
and since q ∝ √σ, dR′/dσ = (R′_dc/σ)(−Re N + (q/2) Re N′). The closed form cancels as N → 1, so below q = 0.4 the ascending
series is differentiated term by term, and above q = 25 the asymptotic expansion; each regime is the derivative of the very
function `NormalizedZ` evaluates there, which is what a Newton step needs. Against a central difference: 3e-11 to 1.4e-10 at
5 and 50 skin depths; the assembled load's slope against a central difference of the load, 6e-8 to 3e-7 (the difference's
own truncation). Checking J·v against Δr/2ε instead would have been vacuous: a perfect bond's penalty conductance in the same
rows is ~10⁹ times the RF slope, so a missing slope passes. With k(T) off the secant does not move with T, and (J − S)·v is
exactly −(dL/dx)·v — the RF slope alone.

**What W3 measured.** Gold 1 mil, 1 mm, 25 °C ends, k constant: 5e-8 of the rise at 1 GHz (0.25 I* RMS, 68.9 K) and 5.2e-8 for
2, 4 and 6 GHz together (113.5 K), in 3 Newton steps; the balance closes to 3e-10. wBond's R′_ac against the reference's own
table (SciPy's Bessel functions, independent of circuitRF's) is within 3.0e-7 at every tabulated frequency (1 MHz – 31.6 GHz)
and temperature — the asymptotic branch's ~1e-7 above q = 25, as `InternalImpedance` documents. With σ(T) and k(T) off,
harmonics at F0 and 2F0 superpose to 2e-14 of the rise.

## brief-em3d-80 — the Rth matrix, Z_th(jω), Foster fits and pulses (2026-09-28)

**One system, homogeneous.** `ThermalSmallSignal` takes the steady problem, keeps its matrix (the fixed nodes eliminated, the
convection faces' h·∫NᵢNⱼ, the interface elements) and drops every offset: fixed temperatures, fixed fields and ambients are
0, sources are replaced by one unit load per source. A source's load is scaled to sum to 1 W, so its "Avg" rise is fᵢᵀT —
the load-weighted mean, which for a uniform source IS the area (volume) mean — and R_ij = fᵢᵀK⁻¹fⱼ is symmetric exactly when
K is. "Max" is nodal over the source's triangles or tetrahedra. With k(T) on, the matrix is the Newton tangent about the
given field, nonsymmetric: LU / BiCGStab, and Z_th's iterative path becomes complex BiCGStab.

**The preconditioner is AMG of K + ωC, not of K.** The brief proposed the real AMG of K for COCG. At high ω, K⁻¹(K + jωC) =
I + jωK⁻¹C has eigenvalues growing with ω without bound; with P = K + ωC every preconditioned eigenvalue (k + jωc)/(k + ωc)
lies between 1/√2 and 1 in modulus at every frequency. The hierarchy is rebuilt per frequency (1.2 s of a 26 s sweep below).

**Where a Z_th sweep's time goes (Debug build, what the owner runs).** 22 frequencies × 2 sources on 10,624 unknowns: 24 s of
solves, 1.2 s of AMG builds, 0.1 s of probe reads. COCG needed ≤ 50 iterations (PCG on the real Rth, 25), and each iteration
costs two V-cycles (the real and imaginary parts). Moving COCG's vector loops off `System.Numerics.Complex` onto split
real/imaginary arrays saved only ~10 % — the V-cycles dominate, not the complex arithmetic. CSparse's complex LU (Release-built
even in a Debug run) was WORSE on that mesh: 1.6 s per factorisation, 35 s for the sweep. So the crossover stays the real
one's. The end-to-end gate runs at first order (1.5k unknowns, direct, 1 s) for that reason; gate 3 holds the iterative path.

**The Foster fit's floor on a distributed Z is ~0.9 % at 4 τ per decade.** Z1's silicon slab (an infinite Foster network,
Z ∝ ω^−½ past its corner) fitted by relative-weighted NNLS on the brief's fixed grid gave 1.02 % max error — least squares
minimises the RMS, and the brief's gate is the max. Lawson's reweighting (each frequency's weight × its share of the error,
the best of N passes, still NNLS on the same grid, still Rᵢ ≥ 0) brings it to 0.94 % at 12 passes and 0.90 % at 40. The 1 %
gate therefore sits just above the grid's own floor; a denser τ grid is the lever, not more passes. NNLS solutions are
sparse — 8 of ~45 grid terms survive on Z1. ΣRᵢ = Rth is imposed by a DC row weighted 10⁴ harder and a final rescale of
order 1e-8. A mutual fit is data only, so it gets one pass; a self fit becomes a network and gets the refinement.

**What the gates measured.** S6 at brief 72's 4 µm / 100 µm rung (33,677 unknowns, PCG + AMG): 0.46 % from the finest rung's
matrix, 4.8e-5 from brief 72's own FEM on the same rung (meshed from the same .geo text; not bit-identical meshes), Avg
asymmetry 3.4e-12. Superposition: R·P against a direct solve with every power on, less the zero-power one, 3e-15 on a
two-layer block with a convection face. Z1 on a geometrically graded column (h₀ = 5e-4 L, ×1.15): 1.1e-6 (silicon) and 7.2e-6
(copper) over 30 frequencies from 0.1 Hz to 10 MHz, both paths identical. Z2a/Z2b closed form against the reference: peak
and 2,000-point waveform within 1e-9. The written `.cnl`'s own sparam bench against the network: 2.6e-15.

**A Period needs time units the expression engine does not have.** `1 ms` failed to parse — the unit table carries no time.
Rather than widen the core's table for one field, `C3dThermal.EvaluateTime` lifts a spaced s / ms / us / µs / ns itself.

**PeakPower shares, it does not replace.** The brief's `"PeakPower": "Pdiss"` beside "every source with its own power scaled
by the same waveform" reads as: the stated total is split among the driven sources in proportion to their own powers.
Omitted, each source pulses at its own power. Sources a Z_th does not name are not pulsed and not in the baseline.

## Thermal series review (2026-09-29)

**Newton's update test is floored at a 1 mK span.** At an isothermal point (no power, every boundary at one temperature) the
span is round-off and so is every update, so "update ≤ 1e-6 × span" never held: 30 steps and a "did not converge" warning on
the exact answer — every 0 W sweep point, and every pulse baseline. Gate: `SolverRobustnessTests.KOfT_AtAnIsothermalPoint_…`.

**A floating body is refused before factorisation** (`FloatingRegionsException`): tetrahedra are unioned through shared nodes
and resistive interface pairs, and a component with no fixed node and no h > 0 convection face is singular — Cholesky either
throws or, with a tiny positive pivot, reads the body at 0 °C. Gate: `SolverRobustnessTests.ABodyNothingHolds…`.

**Newton keeps its last finite iterate** when a step is not finite, and a warm start that is not finite falls back to the
constant-k solve. Fixed-face conflicts count distinct nodes, and two entries on ONE tag at different temperatures now count.

**NNLS stops at as many columns as rows** (a band too narrow for the fit's poles read past its rows).
