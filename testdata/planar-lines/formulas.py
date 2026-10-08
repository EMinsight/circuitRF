"""The CPWG and SLIN closed forms of docs/design/planar-line-models.md, written a second time, outside circuitRF.

The IMPLEMENTATION reference for brief-artsch-1 (R-as1-6): a table of geometry -> Z0, eeff and attenuation at
three frequencies, which tests/Core.Tests compares with the C# models to 1e-6 relative. It proves the C# is
the formulas; fieldsolve.py is what says whether the formulas are the physics.

Independence is the point, so nothing is shared with the C# beyond the published equations: the complete
elliptic integrals come from scipy.special (ellipk, and ellipkm1 near k = 1) rather than from an
arithmetic-geometric mean, the hyperbolic moduli are evaluated directly, and every constant is restated.

Usage:  python3 -I formulas.py > implementation-references.txt
"""
import cmath
import math
import sys

import numpy as np
import scipy
from scipy.special import ellipk, ellipkm1

C0 = 299792458.0
MU0 = 4e-7 * math.pi
ETA0 = MU0 * C0


def kratio(k, kp):
    """K(k)/K(k') from the modulus and its complement, both given so neither is lost to 1 - k^2."""
    kk = ellipkm1(kp * kp) if k > 0.9 else ellipk(k * k)
    kkp = ellipkm1(k * k) if kp > 0.9 else ellipk(kp * kp)
    return kk / kkp


# -- CPWG ----------------------------------------------------------------------------------------------

def backed_moduli(a, b, h):
    """k3 = tanh(pi a / 2h) / tanh(pi b / 2h) and its complement (Ghione-Naldi conductor backing)."""
    ta, tb = math.tanh(math.pi * a / (2 * h)), math.tanh(math.pi * b / (2 * h))
    k3 = ta / tb
    k3p = math.sqrt(max(0.0, 1.0 - k3 * k3))
    return k3, k3p


def cpw_moduli(a, b):
    k = a / b
    return k, math.sqrt((b - a) * (b + a)) / b


def hammerstad_jensen(w, h, t, er):
    """MLIN's static microstrip (E. Hammerstad and O. Jensen, "Accurate models for microstrip computer-aided
    design," IEEE MTT-S Digest, 1980, pp. 407-409), with their effective-width thickness correction."""
    u = w / h
    if t > 0:
        th = t / h
        coth = 1.0 / math.tanh(math.sqrt(6.517 * u))
        du1 = th / math.pi * math.log(1 + 4 * math.e / (th * coth * coth))
        dur = 0.5 * du1 * (1 + 1.0 / math.cosh(math.sqrt(er - 1)))
        u1, ur = u + du1, u + dur
    else:
        u1 = ur = u

    def a(x):
        return 1 + math.log((x ** 4 + (x / 52) ** 2) / (x ** 4 + 0.432)) / 49 + math.log(1 + (x / 18.1) ** 3) / 18.7

    b = 0.564 * ((er - 0.9) / (er + 3)) ** 0.053
    eeff = (er + 1) / 2 + (er - 1) / 2 * (1 + 10 / ur) ** (-a(ur) * b)
    f = 6 + (2 * math.pi - 6) * math.exp(-(30.666 / u1) ** 0.7528)
    z_air = 60 * math.log(f / u1 + math.sqrt(1 + (2 / u1) ** 2))
    return z_air / math.sqrt(eeff), eeff


def cpwg_caps(w, g, h, t, er):
    """(C, C_air, microstrip?) in units of 2 eps0: Ghione-Naldi with the slot walls, or -- where its air
    capacitance is the larger -- the microstrip's, with the same walls."""
    a, b = w / 2, w / 2 + g
    qc = kratio(*cpw_moduli(a, b))
    qb = kratio(*backed_moduli(a, b, h))
    walls = 2 * 0.7 * t / g if t > 0 else 0.0
    c, ca = qc + er * qb + walls, qc + qb + walls
    zm, em = hammerstad_jensen(w, h, t, er)
    cam = ETA0 / (2 * zm * math.sqrt(em))
    if cam + walls > ca:
        return em * cam + walls, cam + walls, True
    return c, ca, False


def cpwg_z0_air(w, g, h, t):
    # C_air does not depend on er; any er >= 1 gives it.
    return ETA0 / 2 / cpwg_caps(w, g, h, t, 1.0)[1]


def cpwg_static(w, g, h, t, er):
    c, ca, ms = cpwg_caps(w, g, h, t, er)
    return ETA0 / 2 / math.sqrt(c * ca), c / ca


def cpwg_dispersion(f, w, g, h, er, eeff0):
    if f <= 0 or er <= 1:
        return eeff0
    p = math.log(w / h)
    u = 0.54 - 0.64 * p + 0.015 * p * p
    v = 0.43 - 0.86 * p + 0.54 * p * p
    gf = math.exp(u * math.log(w / g) + v)
    fte = C0 / (4 * h * math.sqrt(er - 1))
    s0 = math.sqrt(eeff0)
    s = s0 + (math.sqrt(er) - s0) / (1 + gf * (f / fte) ** -1.8)
    return s * s


def surface_resistance(f, sigma, t):
    """Rs of a strip of thickness t carrying current on both faces: each face is a slab of t/2,
    Re{Zs coth(gamma t/2)}, Zs = (1+j)/(sigma delta). Tends to Rs for t >> delta, to 2/(sigma t) for t << delta."""
    delta = 1.0 / math.sqrt(math.pi * f * MU0 * sigma)
    rs = 1.0 / (sigma * delta)
    if t <= 0:
        return rs, delta
    z = (1 + 1j) * t / (2 * delta)
    return rs * ((1 + 1j) / cmath.tanh(z)).real, delta


def roughness_factor(rough, delta):
    if rough <= 0:
        return 1.0
    r = rough / delta
    return 1.0 + 2.0 / math.pi * math.atan(1.4 * r * r)


def cpwg(f, w, g, h, t, er, sigma, tand, rough):
    """Coplanar branch only at f > 0: on the microstrip branch the model hands dispersion to MLIN's
    Kirschning-Jansen, which this file does not restate, so those cases are tabulated static only."""
    z0s, e0 = cpwg_static(w, g, h, t, er)
    if f > 0 and cpwg_caps(w, g, h, t, er)[2]:
        raise ValueError('microstrip branch: static only')
    e = cpwg_dispersion(f, w, g, h, er, e0)
    z = z0s * math.sqrt(e0 / e)
    if f <= 0:
        return z, e, 0.0, 0.0
    dn = 1e-4 * min(w, g, h, t) if t > 0 else 1e-4 * min(w, g, h)
    dt = 2 * dn if t > 0 else 0.0
    dz = (cpwg_z0_air(w - 2 * dn, g + 2 * dn, h + 2 * dn, t - dt)
          - cpwg_z0_air(w + 2 * dn, g - 2 * dn, h - 2 * dn, t + dt)) / (2 * dn)
    rs, delta = surface_resistance(f, sigma, t)
    ac = rs * roughness_factor(rough, delta) / (2 * z * ETA0) * dz
    ad = 0.0 if er <= 1 else math.pi * er / (er - 1) * (e - 1) / math.sqrt(e) * tand * f / C0
    return z, e, ac, ad


# -- SLIN ----------------------------------------------------------------------------------------------

def centred_exact_air(w, b):
    """Cohn's exact zero-thickness centred stripline, air: (eta0/4) K(k)/K(k'), k = sech(pi w / 2b)."""
    x = math.pi * w / (2 * b)
    k, kp = 1.0 / math.cosh(x), math.tanh(x)
    return ETA0 / 4 * kratio(k, kp)


def wheeler_air(w, b, t):
    """Wheeler 1978, a strip of thickness t centred between planes b apart, air."""
    if t > 0:
        x = t / b
        m = 2.0 / (1.0 + (2.0 / 3.0) * x / (1.0 - x))
        dw = (b - t) * x / (math.pi * (1 - x)) * (
            1 - 0.5 * math.log((x / (2 - x)) ** 2 + (0.0796 * x / (w / b + 1.1 * x)) ** m))
    else:
        dw = 0.0
    r = (b - t) / (w + dw)
    return ETA0 / (4 * math.pi) * math.log(
        1 + 4 / math.pi * r * (8 / math.pi * r + math.sqrt((8 / math.pi * r) ** 2 + 6.27)))


def centred_air(w, b, t):
    return centred_exact_air(w, b) * wheeler_air(w, b, t) / wheeler_air(w, b, 0.0)


def slin_z0_air(w, h1, h2, t):
    z1 = centred_air(w, 2 * h1 + t, t)
    z2 = centred_air(w, 2 * h2 + t, t)
    return 2 * z1 * z2 / (z1 + z2)


def slin(f, w, h1, h2, t, er, sigma, tand, rough):
    z = slin_z0_air(w, h1, h2, t) / math.sqrt(er)
    if f <= 0:
        return z, er, 0.0, 0.0
    dn = 1e-4 * min(w, h1, h2, t) if t > 0 else 1e-4 * min(w, h1, h2)
    dt = 2 * dn if t > 0 else 0.0
    dz = (slin_z0_air(w - 2 * dn, h1 + 2 * dn, h2 + 2 * dn, t - dt)
          - slin_z0_air(w + 2 * dn, h1 - 2 * dn, h2 - 2 * dn, t + dt)) / (2 * dn)
    rs, delta = surface_resistance(f, sigma, t)
    ac = rs * roughness_factor(rough, delta) / (2 * z * ETA0) * dz
    ad = math.pi * math.sqrt(er) * tand * f / C0
    return z, er, ac, ad


CPWG_CASES = [
    # W, G, H, T, Er, Sigma, TanD, Roughness
    (1.0e-3, 0.2e-3, 0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037, 0.0),
    (0.5e-3, 0.15e-3, 0.254e-3, 18e-6, 3.0, 5.8e7, 0.0013, 1e-6),
    (0.3e-3, 0.5e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, 0.0),
    (70e-6, 50e-6, 100e-6, 3e-6, 12.9, 4.1e7, 0.0005, 0.0),
    (0.4e-3, 0.15e-3, 0.2e-3, 0.0, 10.2, 5.8e7, 0.0023, 0.0),
]
# Far coplanar ground: the microstrip branch, static only (see cpwg()).
CPWG_STATIC_CASES = [
    (1.0e-3, 4.0e-3, 1.0e-3, 35e-6, 4.4),
    (0.5e-3, 1.5e-3, 0.25e-3, 18e-6, 3.66),
]
SLIN_CASES = [
    # W, H1, H2, T, Er, Sigma, TanD, Roughness
    (0.3e-3, 0.3e-3, 0.3e-3, 17e-6, 3.5, 5.8e7, 0.004, 0.0),
    (1.2e-3, 0.5e-3, 0.5e-3, 35e-6, 2.2, 5.8e7, 0.0009, 2e-6),
    (0.2e-3, 0.15e-3, 0.3e-3, 17e-6, 3.5, 5.8e7, 0.004, 0.0),
    (0.25e-3, 0.1e-3, 0.4e-3, 35e-6, 3.5, 5.8e7, 0.004, 0.0),
    (0.5e-3, 0.4e-3, 0.4e-3, 0.0, 10.2, 5.8e7, 0.0023, 0.0),
]
FREQS = (1e9, 10e9, 40e9)

if __name__ == '__main__':
    print('# Implementation references for CPWG and SLIN (brief-artsch-1 R-as1-6): the closed forms of')
    print('# docs/design/planar-line-models.md, evaluated by testdata/planar-lines/formulas.py, which shares no')
    print('# code with circuitRF.')
    print(f'# Python {sys.version.split()[0]}, numpy {np.__version__}, scipy {scipy.__version__} '
          '(scipy.special.ellipk / ellipkm1).')
    print('# One row per (geometry, frequency): f (Hz) and SI geometry, then Z0 (ohm), eeff, conductor and')
    print('# dielectric attenuation (Np/m). Z0 and eeff at f = 0 are the static values.')
    print('#')
    print('# kind f W G_or_H1 H_or_H2 T Er Sigma TanD Roughness Z0 Eeff AlphaC AlphaD')
    for case in CPWG_CASES:
        for f in (0.0,) + FREQS:
            z, e, ac, ad = cpwg(f, *case)
            print('CPWG ' + ' '.join(f'{v:.10g}' for v in (f,) + case)
                  + f' {z:.12g} {e:.12g} {ac:.12g} {ad:.12g}')
    for case in CPWG_STATIC_CASES:
        z, e = cpwg_static(*case)
        print('CPWG ' + ' '.join(f'{v:.10g}' for v in (0.0,) + case + (0.0, 0.0, 0.0))
              + f' {z:.12g} {e:.12g} 0 0')
    for case in SLIN_CASES:
        for f in (0.0,) + FREQS:
            z, e, ac, ad = slin(f, *case)
            print('SLIN ' + ' '.join(f'{v:.10g}' for v in (f,) + case)
                  + f' {z:.12g} {e:.12g} {ac:.12g} {ad:.12g}')
