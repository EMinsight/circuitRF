"""The 1D wire: closed forms and two independent numerical solutions (brief-em3d-72 §2b). Spike material.

The wire's steady equation, s the arc length from end a (0) to end b (L), T in degC:

    d/ds( k(T) A dT/ds ) + q'(T) = 0,      T(0) = T_a,  T(L) = T_b

with q'(T) the heat per unit length. Two numerical solutions are used as mutual checks of every
table-driven reference, and of the closed forms: scipy's collocation BVP solver, and shooting from the
centre (equal ends only — the solution is then symmetric, T'(L/2) = 0) with a high-order IVP integrator.
"""
from __future__ import annotations

import numpy as np
from scipy.integrate import solve_bvp, solve_ivp
from scipy.optimize import brentq
from scipy.special import jve

MU0 = 4e-7 * np.pi  # the wire metals are non-magnetic; mu0 to 1e-9 is immaterial here
T0 = 20.0           # degC — the reference temperature of rho0 and alpha


def area(d):
    return np.pi * d * d / 4.0


# ---- W1: linear rho(T), constant k, equal ends ------------------------------------------------------

def i_star(d, L, rho0, alpha, k):
    """The runaway current: beta L / 2 = pi / 2."""
    return (np.pi / L) * area(d) * np.sqrt(k / (rho0 * alpha))


def i_fuse(d, L, rho0, alpha, k, T_end, T_melt):
    """The centre reaches T_melt: cos(beta L / 2) = r."""
    r = (T_end - T0 + 1 / alpha) / (T_melt - T0 + 1 / alpha)
    return (2 * area(d) / L) * np.sqrt(k / (rho0 * alpha)) * np.arccos(r)


def beta(I, d, rho0, alpha, k):
    A = area(d)
    return np.sqrt(I * I * rho0 * alpha / (k * A * A))


def w1(s, I, d, L, rho0, alpha, k, T_end):
    """Equal ends. x = s - L/2 from the centre."""
    b = beta(I, d, rho0, alpha, k)
    th_end = T_end - T0 + 1 / alpha
    return T0 - 1 / alpha + th_end * np.cos(b * (s - L / 2)) / np.cos(b * L / 2)


def w1b(s, I, d, L, rho0, alpha, k, T_a, T_b):
    """Unequal ends: theta = [theta_a sin(beta (L - s)) + theta_b sin(beta s)] / sin(beta L)."""
    b = beta(I, d, rho0, alpha, k)
    ta, tb = T_a - T0 + 1 / alpha, T_b - T0 + 1 / alpha
    return T0 - 1 / alpha + (ta * np.sin(b * (L - s)) + tb * np.sin(b * s)) / np.sin(b * L)


def w1b_hot_spot(I, d, L, rho0, alpha, k, T_a, T_b):
    """Where dT/ds = 0 inside the wire (theta_b cos(beta s) = theta_a cos(beta (L - s))), else the hotter end."""
    b = beta(I, d, rho0, alpha, k)
    ta, tb = T_a - T0 + 1 / alpha, T_b - T0 + 1 / alpha
    g = lambda s: tb * np.cos(b * s) - ta * np.cos(b * (L - s))  # proportional to dT/ds
    if g(0) * g(L) < 0:
        s = brentq(g, 0, L, xtol=1e-16, rtol=1e-15)
    else:
        s = 0.0 if T_a >= T_b else L
    return s, float(w1b(s, I, d, L, rho0, alpha, k, T_a, T_b))


def parabola(s, I, d, L, rho, k, T_a, T_b):
    """Constant rho (alpha -> 0): T = T_a + (T_b - T_a) s/L + (I^2 rho / (2 k A^2)) s (L - s)."""
    A = area(d)
    return T_a + (T_b - T_a) * s / L + (I * I * rho / (2 * k * A * A)) * s * (L - s)


# ---- W4: lateral conductance g' into a coaxial mould, linear rho, constant k -----------------------

def g_coax(k_mould, r_wire, r_outer):
    return 2 * np.pi * k_mould / np.log(r_outer / r_wire)


def i_star_mould(d, L, rho0, alpha, k, g):
    A = area(d)
    return np.sqrt((A / (rho0 * alpha)) * (g + k * A * np.pi ** 2 / L ** 2))


def w4(s, I, d, L, rho0, alpha, k, g, T_amb, T_a, T_b):
    """k A T'' + I^2 rho0 [1 + alpha (T - T0)] / A - g (T - T_amb) = 0; ends T_a, T_b.
    m^2 = (g - I^2 rho0 alpha / A) / (k A);  T = T_p + [(T_a - T_p) S(m (L - s)) + (T_b - T_p) S(m s)] / S(m L),
    S = sinh for m^2 > 0, sin (with |m|) for m^2 < 0. Equal ends reduce to the cosh/cos form of the brief."""
    A = area(d)
    c = I * I * rho0 * alpha / A
    m2 = (g - c) / (k * A)
    Tp = (g * T_amb + I * I * rho0 * (1 - alpha * T0) / A) / (g - c)
    m = np.sqrt(abs(m2))
    S = np.sinh if m2 > 0 else np.sin
    return Tp + ((T_a - Tp) * S(m * (L - s)) + (T_b - Tp) * S(m * s)) / S(m * L), m2, Tp


# ---- W3: the exact internal impedance of a round wire -----------------------------------------------

def r_ac_per_m(f, d, rho):
    """Re Z' of a round wire, Z' = (gamma rho / (2 pi a)) J0(gamma a) / J1(gamma a), gamma = (1 - j)/delta.
    f = 0 returns the DC value rho / A. jve (exponentially scaled) keeps the ratio finite at large a/delta."""
    a = d / 2
    if f == 0:
        return rho / area(d)
    delta = np.sqrt(rho / (np.pi * f * MU0))
    gam = (1 - 1j) / delta
    z = (gam * rho / (2 * np.pi * a)) * jve(0, gam * a) / jve(1, gam * a)
    return float(z.real)


def skin_corner_hz(d, rho):
    """The frequency at which the skin depth equals the radius."""
    a = d / 2
    return rho / (np.pi * MU0 * a * a)


# ---- the numerical solutions ------------------------------------------------------------------------

def solve_bvp_wire(L, A, k_of_T, q_of_T, T_a, T_b, n=401, tol=1e-8):
    """Collocation, in the scaled variable x = s/L so both unknowns are O(kelvin):
    y = [T, G], G = L F / (k_ref A) with F = k(T) A dT/ds the heat flow toward +s;
    dT/dx = G k_ref / k(T),  dG/dx = -L^2 q'(T) / (k_ref A). Returns a callable T(s)."""
    k_ref = float(np.mean(k_of_T(np.array([T_a, T_b]))))
    f = lambda x, y: np.vstack([y[1] * k_ref / k_of_T(y[0]), -L * L * q_of_T(y[0]) / (k_ref * A)])
    bc = lambda ya, yb: np.array([ya[0] - T_a, yb[0] - T_b])
    x = np.linspace(0, 1, n)
    y0 = np.vstack([T_a + (T_b - T_a) * x, np.full_like(x, T_b - T_a)])
    # A near-runaway profile (0.99 I*) can exhaust the node budget at the tightest tolerance; the
    # tolerance actually reached is part of the answer, so it is returned rather than hidden.
    for t in (tol, tol * 10, tol * 100):
        sol = solve_bvp(f, bc, x, y0, tol=t, bc_tol=1e-10, max_nodes=200_000)
        if sol.success:
            fn = lambda s, sol=sol: sol.sol(np.asarray(s) / L)[0]
            fn.tol = t
            return fn
    raise RuntimeError(sol.message)


def shoot_centre(L, A, k_of_T, q_of_T, Tc):
    """Integrate from the centre (T = Tc, F = 0) to the end; returns T at the end."""
    f = lambda s, y: [y[1] / (k_of_T(y[0]) * A), -q_of_T(y[0])]
    r = solve_ivp(f, (0, L / 2), [Tc, 0.0], method="DOP853", rtol=1e-13, atol=1e-13)
    return r.y[0, -1]


def solve_shoot_symmetric(L, A, k_of_T, q_of_T, T_end, T_hi):
    """Equal ends. The centre temperature is the LOWEST Tc whose trajectory ends at T_end (the physical
    branch): scan upward from T_end for the first sign change, then brentq."""
    g = lambda Tc: shoot_centre(L, A, k_of_T, q_of_T, Tc) - T_end
    grid = np.linspace(T_end, T_hi, 400)
    prev = g(grid[0])
    for lo, hi in zip(grid[:-1], grid[1:]):
        cur = g(hi)
        if prev <= 0 <= cur or cur <= 0 <= prev:
            return brentq(g, lo, hi, xtol=1e-12, rtol=1e-15)
        prev = cur
    raise RuntimeError("no steady state below T_hi")


def profile_symmetric(L, A, k_of_T, q_of_T, Tc, s):
    """T(s) of the symmetric solution with centre Tc, at the given s (0..L)."""
    f = lambda x, y: [y[1] / (k_of_T(y[0]) * A), -q_of_T(y[0])]
    x = np.minimum(np.abs(np.asarray(s, dtype=float) - L / 2), L / 2)
    xu, inv = np.unique(x, return_inverse=True)
    r = solve_ivp(f, (0, L / 2), [Tc, 0.0], method="DOP853", rtol=1e-13, atol=1e-13, t_eval=xu)
    return r.y[0][inv]
