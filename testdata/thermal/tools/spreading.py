"""Rectangular heat sources on a layered rectangular flux channel: the Fourier-series solution
(brief-em3d-72 S5, S6). Spike material.

Domain 0 < x < a, 0 < y < b, layers stacked downward from the top face z = 0; every side face
insulated, the bottom face held at T_bottom (the h -> infinity limit of a convective sink). Heat enters
the top face as uniform flux over rectangles. Expanding in cos(m pi x/a) cos(n pi y/b), each mode obeys
theta'' = lambda^2 theta in each layer, lambda^2 = (m pi/a)^2 + (n pi/b)^2, with theta and k theta'
continuous between layers; its top-face temperature is the mode's flux coefficient times the stack's
input impedance Z(lambda), built from the bottom up:

    Z_bottom-layer = tanh(lambda t)/(k lambda),   Z_above = (Z + tanh(lambda t)/(k lambda)) / (1 + Z k lambda tanh(lambda t)),

and Z(0) = sum t/k (the one-dimensional resistance). This is the series of Muzychka, Culham and
Yovanovich's compound-flux-channel papers with their sink conductance taken to infinity; it is derived
here, not copied, and the README says so.
"""
from __future__ import annotations

import numpy as np


def stack_impedance(lam, layers):
    """layers: list of (thickness, k) from the TOP down. lam: array of lambda >= 0."""
    lam = np.asarray(lam, float)
    Z = None
    for t, k in reversed(layers):
        with np.errstate(divide="ignore", invalid="ignore"):
            th = np.tanh(lam * t)
            zl = np.where(lam > 0, th / (k * lam), t / k)
            if Z is None:
                Z = zl
            else:
                Z = np.where(lam > 0, (Z + zl) / (1 + Z * k * lam * th), Z + t / k)
    return Z


def _cos_integral(p, lo, hi, L):
    """int_lo^hi cos(p pi x / L) dx for each integer p (p = 0 gives hi - lo)."""
    p = np.asarray(p, float)
    with np.errstate(divide="ignore", invalid="ignore"):
        v = (L / (p * np.pi)) * (np.sin(p * np.pi * hi / L) - np.sin(p * np.pi * lo / L))
    return np.where(p == 0, hi - lo, v)


class Channel:
    def __init__(self, a, b, layers, M, N):
        self.a, self.b, self.layers = a, b, layers
        self.m = np.arange(M + 1)
        self.n = np.arange(N + 1)
        lam = np.sqrt((self.m[:, None] * np.pi / a) ** 2 + (self.n[None, :] * np.pi / b) ** 2)
        self.Z = stack_impedance(lam, layers)
        self.em = np.where(self.m == 0, 1.0, 2.0)
        self.en = np.where(self.n == 0, 1.0, 2.0)

    def modes(self, rect, power):
        """Top-face temperature mode coefficients for a uniform flux rectangle (x0, x1, y0, y1) carrying
        `power` watts."""
        x0, x1, y0, y1 = rect
        q = power / ((x1 - x0) * (y1 - y0))
        cx = _cos_integral(self.m, x0, x1, self.a) * self.em / self.a
        cy = _cos_integral(self.n, y0, y1, self.b) * self.en / self.b
        return q * np.outer(cx, cy) * self.Z

    def point(self, coef, x, y):
        cx = np.cos(self.m * np.pi * x / self.a)
        cy = np.cos(self.n * np.pi * y / self.b)
        return float(cx @ coef @ cy)

    def line_x(self, coef, xs, y):
        cy = np.cos(self.n * np.pi * y / self.b)
        v = coef @ cy
        return np.cos(np.outer(xs, self.m) * np.pi / self.a) @ v

    def mean(self, coef, rect):
        x0, x1, y0, y1 = rect
        ix = _cos_integral(self.m, x0, x1, self.a)
        iy = _cos_integral(self.n, y0, y1, self.b)
        return float(ix @ coef @ iy) / ((x1 - x0) * (y1 - y0))
