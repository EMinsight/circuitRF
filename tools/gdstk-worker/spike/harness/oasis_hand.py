"""A hand encoder for small OASIS files (SEMI P39), written for the G0 spike (brief-oasis-gdstk.md §3 Q5).

gdstk reading its own output proves the round trip, not the reader. This writes the encodings gdstk's
writer never emits -- repetition types 5, 7 and 11, every point-list type, implicit modal variables,
XYRELATIVE, reference-number cell and text names, strict name tables, CBLOCK, PROPERTY, XNAME, XELEMENT,
XGEOMETRY -- each fixture beside the content an ideal reader should produce, in the spike's canonical
form (integer database units). Scratch harness code; circuitRF's own, MIT.

    python3 oasis_hand.py <dir>      writes <dir>/hand-*.oas and <dir>/hand-*.expected.json
"""
import json
import os
import struct
import sys
import zlib

# ---- primitives ---------------------------------------------------------------------------------


def uint(v):
    assert v >= 0
    out = bytearray()
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def sint(v):
    return uint((abs(v) << 1) | (1 if v < 0 else 0))


def real_int(v):  # real type 0 (positive integer)
    return b"\x00" + uint(v)


def real_f64(v):  # real type 7 (IEEE double, little-endian)
    return b"\x07" + struct.pack("<d", v)


def string(s):
    b = s.encode("ascii") if isinstance(s, str) else s
    return uint(len(b)) + b


def g_delta(x, y):
    """g-delta form 1 (octangular, one integer) when it can, else form 2 (two integers)."""
    if y == 0 or x == 0 or abs(x) == abs(y):
        if y == 0:
            d, m = (0, x) if x >= 0 else (2, -x)
        elif x == 0:
            d, m = (1, y) if y >= 0 else (3, -y)
        elif x > 0 and y > 0:
            d, m = 4, x
        elif x < 0 < y:
            d, m = 5, -x
        elif x < 0 and y < 0:
            d, m = 6, -x
        else:
            d, m = 7, x
        return uint((m << 4) | (d << 1))
    return uint((abs(x) << 2) | (1 if x < 0 else 0) << 1 | 1) + sint(y)


def g_delta_form2(x, y):
    return uint((abs(x) << 2) | ((1 if x < 0 else 0) << 1) | 1) + sint(y)


def rep(kind, *a):
    """Repetition bytes, types 0..11, arguments as the specification orders them."""
    out = bytes([kind])
    if kind == 0:
        return out
    if kind == 1:
        n, m, sx, sy = a
        return out + uint(n - 2) + uint(m - 2) + uint(sx) + uint(sy)
    if kind == 2:
        n, sx = a
        return out + uint(n - 2) + uint(sx)
    if kind == 3:
        m, sy = a
        return out + uint(m - 2) + uint(sy)
    if kind in (4, 6):
        spaces, = a
        return out + uint(len(spaces) - 1) + b"".join(uint(s) for s in spaces)
    if kind in (5, 7):
        grid, spaces = a
        return out + uint(len(spaces) - 1) + uint(grid) + b"".join(uint(s) for s in spaces)
    if kind == 8:
        n, m, (nx, ny), (mx, my) = a
        return out + uint(n - 2) + uint(m - 2) + g_delta(nx, ny) + g_delta(mx, my)
    if kind == 9:
        n, (dx, dy) = a
        return out + uint(n - 2) + g_delta(dx, dy)
    if kind == 10:
        disp, = a
        return out + uint(len(disp) - 1) + b"".join(g_delta(x, y) for x, y in disp)
    if kind == 11:
        grid, disp = a
        return out + uint(len(disp) - 1) + uint(grid) + b"".join(g_delta(x, y) for x, y in disp)
    raise ValueError(kind)


def rep_offsets(kind, *a):
    """The offsets (including the original at 0,0) a repetition places, per the specification."""
    if kind == 1:
        n, m, sx, sy = a
        return [(i * sx, j * sy) for j in range(m) for i in range(n)]
    if kind == 2:
        n, sx = a
        return [(i * sx, 0) for i in range(n)]
    if kind == 3:
        m, sy = a
        return [(0, j * sy) for j in range(m)]
    if kind in (4, 5, 6, 7):
        if kind in (4, 6):
            grid, spaces = 1, a[0]
        else:
            grid, spaces = a
        out, acc = [(0, 0)], 0
        for s in spaces:
            acc += s * grid
            out.append((acc, 0) if kind in (4, 5) else (0, acc))
        return out
    if kind == 8:
        n, m, (nx, ny), (mx, my) = a
        return [(i * nx + j * mx, i * ny + j * my) for j in range(m) for i in range(n)]
    if kind == 9:
        n, (dx, dy) = a
        return [(i * dx, i * dy) for i in range(n)]
    if kind in (10, 11):
        if kind == 10:
            grid, disp = 1, a[0]
        else:
            grid, disp = a
        out, x, y = [(0, 0)], 0, 0
        for dx, dy in disp:
            x += dx * grid
            y += dy * grid
            out.append((x, y))
        return out
    raise ValueError(kind)


MAGIC = b"%SEMI-OASIS\r\n"


def start(grid=1000, table_flags=None):
    """START with the table-offsets in START (offset-flag 0). table_flags: six (strict, offset) pairs."""
    tf = table_flags or [(0, 0)] * 6
    return MAGIC + b"\x01" + string("1.0") + real_int(grid) + uint(0) + b"".join(uint(s) + uint(o) for s, o in tf)


def end(body_len):
    """END: record id, padding b-string, validation scheme 0; the record is 256 bytes long."""
    fixed = 1 + 1  # id + validation scheme byte
    pad = 256 - fixed
    # a b-string of length L costs len(uint(L)) + L bytes
    for L in range(pad, 0, -1):
        if len(uint(L)) + L == pad:
            return b"\x02" + uint(L) + b"\x00" * L + uint(0)
    raise AssertionError


def finish(body):
    return body + end(len(body))


# ---- record builders ----------------------------------------------------------------------------


def cell_named(name):
    return b"\x0e" + string(name)


def cell_ref(n):
    return b"\x0d" + uint(n)


def cellname(name, refnum=None):
    return (b"\x04" + string(name) + uint(refnum)) if refnum is not None else (b"\x03" + string(name))


def textstring(text, refnum=None):
    return (b"\x06" + string(text) + uint(refnum)) if refnum is not None else (b"\x05" + string(text))


def rectangle(layer=None, datatype=None, w=None, h=None, x=None, y=None, square=False, repetition=None):
    info = (0x80 if square else 0) | (0x40 if w is not None else 0) | (0x20 if h is not None else 0) \
        | (0x10 if x is not None else 0) | (0x08 if y is not None else 0) | (0x04 if repetition else 0) \
        | (0x02 if datatype is not None else 0) | (0x01 if layer is not None else 0)
    out = b"\x14" + bytes([info])
    if layer is not None: out += uint(layer)
    if datatype is not None: out += uint(datatype)
    if w is not None: out += uint(w)
    if h is not None: out += uint(h)
    if x is not None: out += sint(x)
    if y is not None: out += sint(y)
    if repetition: out += repetition
    return out


def point_list(kind, data):
    """Point-list types 0..5 (§7.7). data: the type's own deltas, as the spec lists them."""
    out = bytes([kind]) + uint(len(data))
    if kind in (0, 1):
        out += b"".join(sint(d) for d in data)  # alternating 1-deltas
    elif kind == 2:
        out += b"".join(manhattan_2delta(dx, dy) for dx, dy in data)
    elif kind == 3:
        out += b"".join(octangular_3delta(dx, dy) for dx, dy in data)
    elif kind in (4, 5):
        out += b"".join(g_delta_form2(dx, dy) if (dx and dy and abs(dx) != abs(dy)) else g_delta(dx, dy) for dx, dy in data)
    return out


def manhattan_2delta(dx, dy):
    if dy == 0:
        d, m = (0, dx) if dx >= 0 else (2, -dx)
    else:
        d, m = (1, dy) if dy >= 0 else (3, -dy)
    return uint((m << 2) | d)


def octangular_3delta(dx, dy):
    if dy == 0:
        d, m = (0, dx) if dx >= 0 else (2, -dx)
    elif dx == 0:
        d, m = (1, dy) if dy >= 0 else (3, -dy)
    elif dx > 0 and dy > 0:
        d, m = 4, dx
    elif dx < 0 < dy:
        d, m = 5, -dx
    elif dx < 0 and dy < 0:
        d, m = 6, -dx
    else:
        d, m = 7, dx
    return uint((m << 3) | d)


def polygon(layer=None, datatype=None, plist=None, x=None, y=None, repetition=None):
    info = (0x20 if plist is not None else 0) | (0x10 if x is not None else 0) | (0x08 if y is not None else 0) \
        | (0x04 if repetition else 0) | (0x02 if datatype is not None else 0) | (0x01 if layer is not None else 0)
    out = b"\x15" + bytes([info])
    if layer is not None: out += uint(layer)
    if datatype is not None: out += uint(datatype)
    if plist is not None: out += plist
    if x is not None: out += sint(x)
    if y is not None: out += sint(y)
    if repetition: out += repetition
    return out


def path(layer=None, datatype=None, halfwidth=None, ext=None, plist=None, x=None, y=None, repetition=None):
    """ext: (start_scheme, start_value, end_scheme, end_value); scheme 1 flush, 2 half-width, 3 explicit."""
    info = (0x80 if ext else 0) | (0x40 if halfwidth is not None else 0) | (0x20 if plist is not None else 0) \
        | (0x10 if x is not None else 0) | (0x08 if y is not None else 0) | (0x04 if repetition else 0) \
        | (0x02 if datatype is not None else 0) | (0x01 if layer is not None else 0)
    out = b"\x16" + bytes([info])
    if layer is not None: out += uint(layer)
    if datatype is not None: out += uint(datatype)
    if halfwidth is not None: out += uint(halfwidth)
    if ext:
        ss, sv, es, ev = ext
        out += bytes([(ss << 2) | es])
        if ss == 3: out += sint(sv)
        if es == 3: out += sint(ev)
    if plist is not None: out += plist
    if x is not None: out += sint(x)
    if y is not None: out += sint(y)
    if repetition: out += repetition
    return out


def text(string_or_ref=None, textlayer=None, texttype=None, x=None, y=None, repetition=None):
    info = 0
    body = b""
    if string_or_ref is not None:
        info |= 0x40
        if isinstance(string_or_ref, int):
            info |= 0x20
            body += uint(string_or_ref)
        else:
            body += string(string_or_ref)
    if textlayer is not None: info |= 0x01; body += uint(textlayer)
    if texttype is not None: info |= 0x02; body += uint(texttype)
    if x is not None: info |= 0x10; body += sint(x)
    if y is not None: info |= 0x08; body += sint(y)
    if repetition: info |= 0x04; body += repetition
    return b"\x13" + bytes([info]) + body


def placement(cell=None, x=None, y=None, rot=0, flip=False, repetition=None):
    """PLACEMENT (17): cell is a name, a reference number (int), or None (modal); rot in quarter turns."""
    info = (0x10 if y is not None else 0) | (0x20 if x is not None else 0) | (0x08 if repetition else 0) \
        | ((rot & 3) << 1) | (0x01 if flip else 0)
    body = b""
    if cell is not None:
        info |= 0x80
        if isinstance(cell, int):
            info |= 0x40
            body += uint(cell)
        else:
            body += string(cell)
    if x is not None: body += sint(x)
    if y is not None: body += sint(y)
    if repetition: body += repetition
    return b"\x11" + bytes([info]) + body


def placement_t(cell, x, y, mag=None, angle=None, flip=False, repetition=None):
    """PLACEMENT (18) with magnification and an angle in degrees, both as reals."""
    info = 0x80 | 0x20 | 0x10 | (0x04 if mag is not None else 0) | (0x02 if angle is not None else 0) \
        | (0x08 if repetition else 0) | (0x01 if flip else 0)
    body = string(cell)
    if mag is not None: body += real_int(mag) if float(mag).is_integer() else real_f64(mag)
    if angle is not None: body += real_int(angle) if float(angle).is_integer() and angle >= 0 else real_f64(angle)
    body += sint(x) + sint(y)
    if repetition: body += repetition
    return b"\x12" + bytes([info]) + body


def circle(layer, datatype, r, x, y):
    return b"\x1b" + bytes([0x20 | 0x10 | 0x08 | 0x02 | 0x01]) + uint(layer) + uint(datatype) + uint(r) + sint(x) + sint(y)


def propname(name):
    return b"\x07" + string(name)


def property_record(name_ref, values):
    """PROPERTY (28) with an explicit name reference and an explicit value list (strings, type 10)."""
    info = 0x04 | 0x02 | (len(values) << 4 if len(values) < 15 else 0xF0)
    out = b"\x1c" + bytes([info]) + uint(name_ref)
    if len(values) >= 15: out += uint(len(values))
    for v in values:
        if isinstance(v, int):
            out += b"\x08" + uint(v)
        else:
            out += b"\x0a" + string(v)
    return out


def xname(attr, s):
    return b"\x1e" + uint(attr) + string(s)


def xelement(attr, s):
    return b"\x20" + uint(attr) + string(s)


def xgeometry(attr, layer, datatype, s, x, y):
    return b"\x21" + bytes([0x10 | 0x08 | 0x02 | 0x01]) + uint(attr) + uint(layer) + uint(datatype) + string(s) + sint(x) + sint(y)


def cblock(records):
    comp = zlib.compressobj(6, zlib.DEFLATED, -15)
    data = comp.compress(records) + comp.flush()
    return b"\x22" + uint(0) + uint(len(records)) + uint(len(data)) + data


def layername(kind, name, layer_interval, type_interval):
    """LAYERNAME (11 data, 12 text); an interval is (type, a, b) as §19 lists them."""
    def interval(t, a=0, b=0):
        out = uint(t)
        if t in (1, 2, 3): out += uint(a)
        if t == 4: out += uint(a) + uint(b)
        return out
    return bytes([11 if kind == "data" else 12]) + string(name) + interval(*layer_interval) + interval(*type_interval)


# ---- canonical expectations --------------------------------------------------------------------


def rect_xy(x, y, w, h):
    return [x, y, x + w, y, x + w, y + h, x, y + h]


def exp_rep_from_offsets(offsets):
    return {"offsets": sorted(map(tuple, offsets))}


# ---- the fixtures -------------------------------------------------------------------------------


def fixtures():
    out = {}

    # H1: one placement per repetition type 1..11, each of a child cell holding one 1x1 um square.
    reps = {
        1: (3, 2, 300, 200), 2: (4, 150), 3: (3, 250),
        4: ([100, 250, 50],), 5: (10, [10, 25, 5]),
        6: ([100, 30],), 7: (5, [20, 40]),
        8: (2, 3, (300, 100), (-50, 400)), 9: (4, (120, -70)),
        10: ([(100, 0), (0, 100), (-250, 30)],), 11: (10, [(10, 0), (3, 7), (-25, 3)]),
    }
    body = start() + cell_named("child") + rectangle(1, 0, 1000, 1000, 0, 0)
    body += cell_named("top")
    exp_refs = []
    for k, args in reps.items():
        x0 = k * 10000
        body += placement("child", x0, 0, repetition=rep(k, *args))
        exp_refs.append({"cell": "child", "x": x0, "y": 0, "rotation": 0, "mirror": False, "magnification": 1,
                         "offsets": sorted(rep_offsets(k, *args)), "rep_type": k})
    # type 0: reuse the last (type 11) on a placement with no position change
    body += placement(None, x=200000, repetition=rep(0))
    exp_refs.append({"cell": "child", "x": 200000, "y": 0, "rotation": 0, "mirror": False, "magnification": 1,
                     "offsets": sorted(rep_offsets(11, *reps[11])), "rep_type": 0})
    out["hand-repetitions"] = (finish(body), {
        "grid_per_um": 1000,
        "cells": {"child": {"polygons": [{"layer": 1, "datatype": 0, "xy": rect_xy(0, 0, 1000, 1000)}]},
                  "top": {"refs": exp_refs}}})

    # H2: modal variables, XYRELATIVE, square rectangles, and point-list types 0..5.
    body = start() + cell_named("modal")
    exp = []
    body += rectangle(2, 3, 500, 200, 100, 100); exp.append((2, 3, rect_xy(100, 100, 500, 200)))
    body += rectangle(x=1000);                    exp.append((2, 3, rect_xy(1000, 100, 500, 200)))  # w, h, y, layer modal
    body += rectangle(w=300, square=True, y=2000); exp.append((2, 3, rect_xy(1000, 2000, 300, 300)))
    body += b"\x10"  # XYRELATIVE
    body += rectangle(x=500, y=-100);             exp.append((2, 3, rect_xy(1500, 1900, 300, 300)))
    body += b"\x0f"  # XYABSOLUTE
    # point lists: each polygon starts at (x, y); the list holds the deltas after the first point
    body += polygon(4, 0, point_list(0, [400, 300, -200]), 0, 5000)  # type 0: horizontal first
    # Types 0 and 1 in a POLYGON imply one more point, so both closing edges are manhattan: after
    # h400 v300 h-200 that point is (200, 5000).
    exp.append((4, 0, [0, 5000, 400, 5000, 400, 5300, 200, 5300, 200, 5000]))
    body += polygon(4, 0, point_list(1, [300, 400, -100]), 10000, 5000)  # type 1: v first
    exp.append((4, 0, [10000, 5000, 10000, 5300, 10400, 5300, 10400, 5200, 10000, 5200]))
    body += polygon(4, 0, point_list(2, [(500, 0), (0, 300), (-200, 0), (0, -100)]), 20000, 5000)
    exp.append((4, 0, [20000, 5000, 20500, 5000, 20500, 5300, 20300, 5300, 20300, 5200]))
    body += polygon(4, 0, point_list(3, [(500, 0), (100, 100), (-600, 0)]), 30000, 5000)
    exp.append((4, 0, [30000, 5000, 30500, 5000, 30600, 5100, 30000, 5100]))
    body += polygon(4, 0, point_list(4, [(500, 0), (37, 211), (-537, 0)]), 40000, 5000)
    exp.append((4, 0, [40000, 5000, 40500, 5000, 40537, 5211, 40000, 5211]))
    # type 5: deltas of deltas -- point_i = point_{i-1} + sum of the deltas so far
    body += polygon(4, 0, point_list(5, [(500, 0), (-500, 300), (-123, -77)]), 50000, 5000)
    exp.append((4, 0, [50000, 5000, 50500, 5000, 51000 - 500, 5300, 50500 - 123 + 0, 5300 + 300 - 77]))
    # modal polygon reuse: same point list at a new position
    body += polygon(x=60000)
    exp.append((4, 0, [60000, 5000, 60500, 5000, 60500, 5300, 60377, 5523]))
    out["hand-modal"] = (finish(body), {"grid_per_um": 1000, "cells": {"modal": {"polygons": [
        {"layer": l, "datatype": d, "xy": xy} for l, d, xy in exp]}}})

    # H3: paths with every extension scheme, and a 1-unit spine segment.
    body = start() + cell_named("paths")
    pexp = []
    body += path(5, 0, 50, (1, 0, 1, 0), point_list(4, [(1000, 0), (0, 1000)]), 0, 0)
    pexp.append({"layer": 5, "datatype": 0, "width": 100, "ext": [0, 0], "xy": [0, 0, 1000, 0, 1000, 1000]})
    body += path(halfwidth=50, ext=(2, 0, 2, 0), plist=point_list(4, [(1000, 0)]), x=0, y=3000)
    pexp.append({"layer": 5, "datatype": 0, "width": 100, "ext": [50, 50], "xy": [0, 3000, 1000, 3000]})
    body += path(halfwidth=50, ext=(3, 20, 3, -10), plist=point_list(4, [(1000, 0)]), x=0, y=6000)
    pexp.append({"layer": 5, "datatype": 0, "width": 100, "ext": [20, -10], "xy": [0, 6000, 1000, 6000]})
    body += path(halfwidth=50, ext=(1, 0, 1, 0), plist=point_list(4, [(1, 0), (1000, 0)]), x=0, y=9000)
    pexp.append({"layer": 5, "datatype": 0, "width": 100, "ext": [0, 0], "xy": [0, 9000, 1, 9000, 1001, 9000]})
    out["hand-paths"] = (finish(body), {"grid_per_um": 1000, "cells": {"paths": {"paths": pexp}}})

    # H4: names by reference number, strict tables, TEXTSTRING, TEXT layers, a forward cell reference.
    names = cellname("leaf", 0) + cellname("root", 1)
    texts = textstring("alpha", 0) + textstring("beta", 1)
    # START is followed by the tables (so their offsets are known), then the cells.
    body_cells = cell_ref(1) + placement(0, 100, 200) + text(0, 9, 4, 10, 20) + text(1, x=30) + text(None, x=50, y=60)
    body_cells += cell_ref(0) + rectangle(1, 0, 10, 10, 0, 0)
    # Build START with strict CELLNAME and TEXTSTRING tables at their true offsets.
    start_len = len(start(1000, [(1, 99999), (1, 99999), (0, 0), (0, 0), (0, 0), (0, 0)]))
    cn_off = start_len
    ts_off = cn_off + len(names)
    st = start(1000, [(1, cn_off), (1, ts_off), (0, 0), (0, 0), (0, 0), (0, 0)])
    if len(st) != start_len:  # offsets changed the varint length; recompute once
        start_len = len(st)
        cn_off, ts_off = start_len, start_len + len(names)
        st = start(1000, [(1, cn_off), (1, ts_off), (0, 0), (0, 0), (0, 0), (0, 0)])
    out["hand-names"] = (finish(st + names + texts + body_cells), {"grid_per_um": 1000, "cells": {
        "root": {"refs": [{"cell": "leaf", "x": 100, "y": 200, "rotation": 0, "mirror": False, "magnification": 1,
                           "offsets": [(0, 0)], "rep_type": None}],
                 "labels": [{"layer": 9, "texttype": 4, "text": "alpha", "x": 10, "y": 20},
                            {"layer": 9, "texttype": 4, "text": "beta", "x": 30, "y": 20},
                            {"layer": 9, "texttype": 4, "text": "beta", "x": 50, "y": 60}]},
        "leaf": {"polygons": [{"layer": 1, "datatype": 0, "xy": rect_xy(0, 0, 10, 10)}]}}})

    # H5: placements -- every quarter turn and flip, PLACEMENT_TRANSFORM with magnification and 30 degrees.
    body = start() + cell_named("c") + rectangle(1, 0, 100, 50, 0, 0) + cell_named("t")
    rexp = []
    for i, (rot, flip) in enumerate([(0, False), (1, False), (2, False), (3, False), (0, True), (1, True)]):
        body += placement("c", i * 1000, 0, rot=rot, flip=flip)
        rexp.append({"cell": "c", "x": i * 1000, "y": 0, "rotation": 90 * rot, "mirror": flip, "magnification": 1,
                     "offsets": [(0, 0)], "rep_type": None})
    body += placement_t("c", 0, 5000, mag=2, angle=30)
    rexp.append({"cell": "c", "x": 0, "y": 5000, "rotation": 30, "mirror": False, "magnification": 2,
                 "offsets": [(0, 0)], "rep_type": None})
    body += placement_t("c", 3000, 5000, mag=0.5, angle=90, flip=True)
    rexp.append({"cell": "c", "x": 3000, "y": 5000, "rotation": 90, "mirror": True, "magnification": 0.5,
                 "offsets": [(0, 0)], "rep_type": None})
    out["hand-placements"] = (finish(body), {"grid_per_um": 1000, "cells": {
        "c": {"polygons": [{"layer": 1, "datatype": 0, "xy": rect_xy(0, 0, 100, 50)}]}, "t": {"refs": rexp}}})

    # H6: repetitions on shapes and text.
    body = start() + cell_named("s")
    body += rectangle(1, 0, 10, 10, 0, 0, repetition=rep(1, 3, 2, 100, 100))
    body += polygon(2, 0, point_list(4, [(10, 0), (0, 10)]), 0, 1000, repetition=rep(10, [(50, 0), (0, 50)]))
    body += text("t", 3, 0, 0, 2000, repetition=rep(2, 3, 40))
    out["hand-shape-repetitions"] = (finish(body), {"grid_per_um": 1000, "cells": {"s": {
        "polygons": [{"layer": 1, "datatype": 0, "xy": rect_xy(0, 0, 10, 10), "offsets": sorted(rep_offsets(1, 3, 2, 100, 100))},
                     {"layer": 2, "datatype": 0, "xy": [0, 1000, 10, 1000, 10, 1010],
                      "offsets": sorted(rep_offsets(10, [(50, 0), (0, 50)]))}],
        "labels": [{"layer": 3, "texttype": 0, "text": "t", "x": 0, "y": 2000, "offsets": sorted(rep_offsets(2, 3, 40))}]}}})

    # H7: X-records and properties among ordinary geometry. Everything after them must still arrive.
    body = start() + propname("user_prop") + cell_named("x")
    body += rectangle(1, 0, 10, 10, 0, 0) + property_record(0, ["hello", 7])
    body += xname(1, "an-xname") + xelement(2, "an-xelement")
    body += xgeometry(3, 1, 0, "an-xgeometry", 0, 0)
    body += rectangle(1, 0, 20, 20, 100, 0)
    out["hand-xrecords"] = (finish(body), {"grid_per_um": 1000, "cells": {"x": {
        "polygons": [{"layer": 1, "datatype": 0, "xy": rect_xy(0, 0, 10, 10)},
                     {"layer": 1, "datatype": 0, "xy": rect_xy(100, 0, 20, 20)}]}}})

    # H8: a CBLOCK holding a cell's records, then an uncompressed cell after it.
    inner = cell_named("z1") + rectangle(1, 0, 10, 10, 0, 0) + rectangle(x=50, y=50)
    body = start() + cblock(inner) + cell_named("z2") + rectangle(2, 0, 5, 5, 0, 0)
    out["hand-cblock"] = (finish(body), {"grid_per_um": 1000, "cells": {
        "z1": {"polygons": [{"layer": 1, "datatype": 0, "xy": rect_xy(0, 0, 10, 10)},
                            {"layer": 1, "datatype": 0, "xy": rect_xy(50, 50, 10, 10)}]},
        "z2": {"polygons": [{"layer": 2, "datatype": 0, "xy": rect_xy(0, 0, 5, 5)}]}}})

    # H9: LAYERNAME records of every interval type, data and text.
    body = start()
    body += layername("data", "M1", (3, 10), (3, 0))      # layer 10, datatype 0
    body += layername("data", "M2", (4, 20, 21), (0,))    # layers 20..21, any datatype
    body += layername("text", "M1.T", (3, 10), (3, 1))    # text layer 10, texttype 1
    body += layername("data", "LOW", (1, 5), (2, 3))      # layers <= 5, datatypes >= 3
    body += cell_named("ln") + rectangle(10, 0, 10, 10, 0, 0)
    out["hand-layernames"] = (finish(body), {"grid_per_um": 1000, "layer_names": [
        ["M1", "geometry", 3, 10, 0, 3, 0, 0], ["M2", "geometry", 4, 20, 21, 0, 0, 0],
        ["M1.T", "text", 3, 10, 0, 3, 1, 0], ["LOW", "geometry", 1, 5, 0, 2, 3, 0]],
        "cells": {"ln": {"polygons": [{"layer": 10, "datatype": 0, "xy": rect_xy(0, 0, 10, 10)}]}}})

    # H10: a 0.25 nm grid (4000 per um) written as an integer real, and a coordinate near 2^31.
    big = 2**31 - 1000
    body = start(4000) + cell_named("g") + rectangle(1, 0, 999, 1, big - 999, -big)
    out["hand-grid4000"] = (finish(body), {"grid_per_um": 4000, "cells": {"g": {"polygons": [
        {"layer": 1, "datatype": 0, "xy": rect_xy(big - 999, -big, 999, 1)}]}}})

    return out


if __name__ == "__main__":
    d = sys.argv[1]
    os.makedirs(d, exist_ok=True)
    for name, (data, expected) in fixtures().items():
        with open(os.path.join(d, name + ".oas"), "wb") as f:
            f.write(data)
        with open(os.path.join(d, name + ".expected.json"), "w") as f:
            json.dump(expected, f, indent=1, default=list)
        print(name, len(data), "bytes")
