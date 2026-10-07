"""Q6 -- coordinate exactness (brief-oasis-gdstk.md §3). Integer database units -> file -> gdstk -> integer
database units, EXACTLY, at 1 nm and 0.25 nm, for the extremes of the 32-bit range and 10^6 random values,
in all four directions the worker has:

    read  GDSII   a file written HERE, byte by byte              -> the worker's read -> integers
    read  OASIS   a file written HERE (oasis_hand.py's encoder)  -> the worker's read -> integers
    write GDSII   integers -> the worker's write                 -> parsed HERE      -> integers
    write OASIS   integers -> the worker's write (no CBLOCK, no shape detection) -> decoded HERE -> integers

"HERE" is independent of gdstk, so neither direction is gdstk agreeing with itself. Scratch harness code.

    python3 q6.py <dir> [rid] [count]
"""
import json
import math
import os
import random
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import oasis_hand as H  # noqa: E402
from gdstk_client import Worker, read_library, worker_path, write_library  # noqa: E402

VERTS = 1000  # vertices per polygon


# ---- GDSII, independent of gdstk ----------------------------------------------------------------

def real8(v):
    if v == 0:
        return b"\x00" * 8
    sign = 0x80 if v < 0 else 0
    v = abs(v)
    exp = 64
    while v >= 1:
        v /= 16
        exp += 1
    while v < 1 / 16:
        v *= 16
        exp -= 1
    mant = int(round(v * (1 << 56)))
    if mant >= 1 << 56:
        mant >>= 4
        exp += 1
    return bytes([sign | exp]) + mant.to_bytes(7, "big")


def real8_decode(b):
    sign = -1 if b[0] & 0x80 else 1
    exp = (b[0] & 0x7F) - 64
    mant = int.from_bytes(b[1:], "big")
    return sign * mant / (1 << 56) * 16.0 ** exp


def rec(rtype, dtype, data=b""):
    return struct.pack(">HBB", 4 + len(data), rtype, dtype) + data


def gds_file(polys, db_in_user, db_in_m):
    stamp = struct.pack(">12h", 2026, 1, 1, 0, 0, 0, 2026, 1, 1, 0, 0, 0)
    out = rec(0x00, 2, struct.pack(">h", 600)) + rec(0x01, 2, stamp) + rec(0x02, 6, b"LIB\x00")
    out += rec(0x03, 5, real8(db_in_user) + real8(db_in_m))
    out += rec(0x05, 2, stamp) + rec(0x06, 6, b"q6")
    for xy in polys:
        closed = xy + xy[:2]
        out += rec(0x08, 0) + rec(0x0D, 2, struct.pack(">h", 1)) + rec(0x0E, 2, struct.pack(">h", 0))
        out += rec(0x10, 3, struct.pack(f">{len(closed)}i", *closed)) + rec(0x11, 0)
    out += rec(0x07, 0) + rec(0x04, 0)
    return out


def gds_parse(data):
    """Every BOUNDARY's XY (closing point dropped) and the UNITS, read directly from the records."""
    polys, units, i, cur = [], None, 0, None
    while i < len(data):
        n, rtype, _ = struct.unpack(">HBB", data[i:i + 4])
        body = data[i + 4:i + n]
        if rtype == 0x03:
            units = (real8_decode(body[:8]), real8_decode(body[8:16]))
        elif rtype == 0x08:
            cur = True
        elif rtype == 0x10 and cur:
            xy = list(struct.unpack(f">{len(body) // 4}i", body))
            polys.append(xy[:-2] if xy[:2] == xy[-2:] else xy)
        elif rtype == 0x11:
            cur = None
        i += n
        if rtype == 0x04:
            break
    return polys, units


# ---- OASIS, independent of gdstk: the encoder in oasis_hand.py, and a decoder for what gdstk writes -

class Rd:
    def __init__(self, b, i=0):
        self.b, self.i = b, i

    def byte(self):
        v = self.b[self.i]
        self.i += 1
        return v

    def uint(self):
        v, s = 0, 0
        while True:
            c = self.byte()
            v |= (c & 0x7F) << s
            s += 7
            if not c & 0x80:
                return v

    def sint(self):
        u = self.uint()
        return -(u >> 1) if u & 1 else u >> 1

    def real(self):
        t = self.uint()
        if t == 0: return self.uint()
        if t == 1: return -self.uint()
        if t == 2: return 1 / self.uint()
        if t == 3: return -1 / self.uint()
        if t == 4: return self.uint() / self.uint()
        if t == 5: return -self.uint() / self.uint()
        if t == 6: v = struct.unpack("<f", self.b[self.i:self.i + 4])[0]; self.i += 4; return v
        if t == 7: v = struct.unpack("<d", self.b[self.i:self.i + 8])[0]; self.i += 8; return v
        raise ValueError(t)

    def string(self):
        n = self.uint()
        s = self.b[self.i:self.i + n]
        self.i += n
        return s

    def gdelta(self):
        u = self.uint()
        if u & 1 == 0:
            d, m = (u >> 1) & 7, u >> 4
            return [(m, 0), (0, m), (-m, 0), (0, -m), (m, m), (-m, m), (-m, -m), (m, -m)][d]
        x = (u >> 2) * (-1 if u & 2 else 1)
        return x, self.sint()

    def point_list(self, closed):
        t = self.uint()
        n = self.uint()
        pts = [(0, 0)]
        if t in (0, 1):
            horiz = t == 0
            for _ in range(n):
                d = self.sint()
                x, y = pts[-1]
                pts.append((x + d, y) if horiz else (x, y + d))
                horiz = not horiz
            if closed:
                x, y = pts[-1]
                pts.append((pts[0][0], y) if horiz else (x, pts[0][1]))
        elif t == 2:
            for _ in range(n):
                u = self.uint()
                d, m = u & 3, u >> 2
                dx, dy = [(m, 0), (0, m), (-m, 0), (0, -m)][d]
                x, y = pts[-1]
                pts.append((x + dx, y + dy))
        elif t == 3:
            for _ in range(n):
                u = self.uint()
                d, m = u & 7, u >> 3
                dx, dy = [(m, 0), (0, m), (-m, 0), (0, -m), (m, m), (-m, m), (-m, -m), (m, -m)][d]
                x, y = pts[-1]
                pts.append((x + dx, y + dy))
        elif t == 4:
            for _ in range(n):
                dx, dy = self.gdelta()
                x, y = pts[-1]
                pts.append((x + dx, y + dy))
        elif t == 5:
            ddx = ddy = 0
            for _ in range(n):
                dx, dy = self.gdelta()
                ddx += dx
                ddy += dy
                x, y = pts[-1]
                pts.append((x + ddx, y + ddy))
        return pts


def oas_decode_polygons(data):
    """POLYGONs of a file gdstk wrote with no CBLOCK and no shape detection; returns (polygons, unit real
    and its type byte)."""
    assert data[:13] == H.MAGIC
    r = Rd(data, 13)
    assert r.byte() == 1
    r.string()
    unit_type = data[r.i]
    unit = r.real()
    flag = r.uint()
    if flag == 0:
        for _ in range(12): r.uint()
    polys = []
    layer = dt = 0
    gx = gy = 0
    absolute = True
    plist = None
    while r.i < len(data):
        t = r.byte()
        if t == 0: continue
        if t == 2: break
        if t in (3, 5, 7, 9): r.string(); continue
        if t in (4, 6, 8, 10): r.string(); r.uint(); continue
        if t == 13: r.uint(); gx = gy = 0; absolute = True; continue
        if t == 14: r.string(); gx = gy = 0; absolute = True; continue
        if t == 15: absolute = True; continue
        if t == 16: absolute = False; continue
        if t == 21:
            info = r.byte()
            if info & 1: layer = r.uint()
            if info & 2: dt = r.uint()
            if info & 0x20: plist = r.point_list(True)
            if info & 0x10:
                v = r.sint(); gx = v if absolute else gx + v
            if info & 0x08:
                v = r.sint(); gy = v if absolute else gy + v
            assert not info & 0x04, "a repetition in the Q6 file"
            pts = [(gx + x, gy + y) for x, y in plist]
            polys.append([c for p in pts for c in p])
            continue
        raise ValueError(f"record {t} at {r.i - 1}: the decoder covers only what gdstk writes for Q6")
    return polys, unit, unit_type


def oas_file(polys, grid):
    body = H.start(grid) + H.cell_named("q6")
    for xy in polys:
        deltas = [(xy[i] - xy[i - 2], xy[i + 1] - xy[i - 1]) for i in range(2, len(xy), 2)]
        body += H.polygon(1, 0, H.point_list(4, deltas), xy[0], xy[1])
    return H.finish(body)


# ---- the run ------------------------------------------------------------------------------------

def values(count, seed=31):
    lo, hi = -2 ** 31, 2 ** 31 - 1
    ext = [lo, lo + 1, -2 ** 30, -1, 0, 1, 2 ** 30, hi - 1, hi, -1000000007, 999999937]
    rng = random.Random(seed)
    vs = ext + [rng.randint(lo, hi) for _ in range(count)]
    if len(vs) % 2:
        vs.append(0)
    return vs


def chunk(vs):
    return [vs[i:i + 2 * VERTS] for i in range(0, len(vs), 2 * VERTS)]


def flat(polys):
    return [v for p in polys for v in p]


def compare(name, sent, got):
    a, b = flat(sent), flat(got)
    bad = sum(1 for x, y in zip(a, b) if x != y) + abs(len(a) - len(b))
    first = next(((x, y) for x, y in zip(a, b) if x != y), None)
    return {"case": name, "values": len(a), "mismatches": bad, "first_mismatch": first}


def worker_polys(lib):
    return [p["xy"] for c in lib["cells"].values() for p in c["polygons"]]


def main():
    d = sys.argv[1]
    rid = (sys.argv[2] or None) if len(sys.argv) > 2 else None
    count = int(sys.argv[3]) if len(sys.argv) > 3 else 1_000_000
    os.makedirs(d, exist_ok=True)
    vs = values(count)
    polys = chunk(vs)
    results = []
    for grid in (1000, 4000):
        db_in_m = 1e-6 / grid
        tag = f"{grid}"
        # read GDSII
        p = os.path.join(d, f"q6-in-{tag}.gds")
        open(p, "wb").write(gds_file(polys, 1 / grid, db_in_m))
        w = Worker(worker_path(rid))
        lib = read_library(p, "gds", w, tolerance_dbu=0.5)
        r = compare(f"read gds grid {grid}", polys, worker_polys(lib))
        r["nonintegral"] = lib["nonintegral"]
        results.append(r)
        # read OASIS
        p = os.path.join(d, f"q6-in-{tag}.oas")
        open(p, "wb").write(oas_file(polys, grid))
        lib = read_library(p, "oas", w, tolerance_dbu=0.5)
        r = compare(f"read oas grid {grid}", polys, worker_polys(lib))
        r["nonintegral"] = lib["nonintegral"]
        results.append(r)
        # write GDSII
        canon_lib = {"grid_per_um": grid, "cells": {"q6": {"polygons": [{"layer": 1, "datatype": 0, "xy": xy} for xy in polys]}}}
        p = os.path.join(d, f"q6-out-{tag}.gds")
        write_library(canon_lib, p, "gds", w)
        got, units = gds_parse(open(p, "rb").read())
        r = compare(f"write gds grid {grid}", polys, got)
        r["units"] = units
        results.append(r)
        # write OASIS: no CBLOCK, no rectangle/trapezoid detection, so every polygon is a POLYGON record
        p = os.path.join(d, f"q6-out-{tag}.oas")
        write_library(canon_lib, p, "oas", w, {"compression_level": 0, "detect_rectangles": False,
                                                "detect_trapezoids": False, "validation": "none"})
        got, unit, unit_type = oas_decode_polygons(open(p, "rb").read())
        r = compare(f"write oas grid {grid}", polys, got)
        r["start_unit"] = repr(unit)
        r["start_unit_real_type"] = unit_type
        results.append(r)
        w.close()
    for r in results:
        print(json.dumps(r))


if __name__ == "__main__":
    main()
