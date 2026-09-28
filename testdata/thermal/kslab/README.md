# S4 — slab with temperature-dependent conductivity

**Pins:** Newton on k(T) — a coefficient form and a table form. **Used by:** brief 74.

## Physics

A 500 µm slab, lateral faces insulated, bottom (z = 0) held at 25 °C, a flux q″ = 4e7 W/m² into the top.
With no volumetric source, k(T) dT/dz = q″ everywhere.

**The Kirchhoff transform.** U(T) = ∫_{T_ref}^{T} k(T′) dT′ turns the equation linear: dU/dz = q″, so
U(z) = U(T_b) + q″ z, and T(z) = U⁻¹(U(z)).

**S4a — k = k₀ / (1 + β (T − T₀))**, k₀ = 150 W/(m·K), T₀ = 25 °C, β = 0.004 /K (a silicon-like fall with
temperature). Then U = (k₀/β) ln(1 + β(T − T₀)), inverted in closed form:

    T(z) = T₀ + (exp(β U(z) / k₀) − 1) / β

T(L) = **201.15 °C**, against 158.33 °C if k were held at k₀ — the size of the error a solver that ignores
k(T) makes here.

**S4b — k(T) a table**: silicon, from Ho, Powell & Liley, *Thermal conductivity of the elements: a
comprehensive review*, J. Phys. Chem. Ref. Data **3**, Suppl. 1 (1974), 0.05–726.85 °C, in
`silicon-k.csv`, interpolated **piecewise-linearly in T**. Solved with SciPy's collocation BVP solver
(`solve_bvp`, T and the heat flux as unknowns, relative tolerance 1e-8); the same problem is also solved
by the Kirchhoff transform of the piecewise-linear table (exact trapezoids, inverted by `brentq`), and
the two agree to **2.7e-10 K**. T(L) = **212.05 °C**.

## Files

`kslab-S4a.csv`, `kslab-S4b.csv` — `z [m]`, `T [degC]` at 101 points. `silicon-k.csv` — the table.
`kslab.json` — parameters, T(L), and the BVP-versus-Kirchhoff difference.

## Regenerate

    ~/opt/thermal-spike/venv/bin/python make_kslab.py
