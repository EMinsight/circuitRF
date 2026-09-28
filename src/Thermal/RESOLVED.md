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
