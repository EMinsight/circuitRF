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
