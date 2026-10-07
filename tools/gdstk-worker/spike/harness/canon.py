"""Semantic equality for the spike (brief-oasis-gdstk.md §8b), over the canonical form gdstk_client.py
and GdsiiDump produce. Scratch harness code.

Per cell, multisets of
  polygons  (layer, datatype, vertex cycle: start at the smallest vertex, counter-clockwise)
  paths     (layer, datatype, spine, width, end)
  labels    (layer, texttype, text, origin, rotation, mirror, magnification)
  refs      (cell, origin, rotation, mirror, magnification, the placements its repetition makes)
and the same database unit. Integers compare exactly; angles and magnifications within 1e-12 (rounded
to 9 decimals here). A shape repetition is compared by the offsets it places, so an OASIS type-2
repetition equals the same row written as type 10. diff() lists EVERY difference, not the first.
"""
from collections import Counter


def cycle(xy):
    pts = [(xy[i], xy[i + 1]) for i in range(0, len(xy), 2)]
    if len(pts) > 1 and pts[0] == pts[-1]:
        pts = pts[:-1]
    # drop consecutive duplicates
    out = []
    for p in pts:
        if not out or out[-1] != p:
            out.append(p)
    if len(out) > 1 and out[0] == out[-1]:
        out.pop()
    area = sum(out[i][0] * out[(i + 1) % len(out)][1] - out[(i + 1) % len(out)][0] * out[i][1] for i in range(len(out)))
    if area < 0:
        out = out[::-1]
    k = out.index(min(out))
    return tuple(out[k:] + out[:k])


def offsets(rep):
    """The placements a repetition makes, including the original (0, 0), sorted."""
    if not rep:
        return ((0, 0),)
    k = rep["kind"]
    if k == "rectangular":
        sx, sy = rep["spacing"]
        pts = [(i * sx, j * sy) for j in range(rep["rows"]) for i in range(rep["columns"])]
    elif k == "regular":
        (ax, ay), (bx, by) = rep["v1"], rep["v2"]
        pts = [(i * ax + j * bx, i * ay + j * by) for j in range(rep["rows"]) for i in range(rep["columns"])]
    elif k == "explicit":
        o = rep["offsets"]
        pts = [(0, 0)] + [(o[i], o[i + 1]) for i in range(0, len(o), 2)]
    elif k == "explicit_x":
        pts = [(0, 0)] + [(c, 0) for c in rep["coords"]]
    elif k == "explicit_y":
        pts = [(0, 0)] + [(0, c) for c in rep["coords"]]
    else:
        raise ValueError(k)
    return tuple(sorted(pts))


def r9(v):
    return round(float(v), 9) + 0.0


def norm_angle(a):
    a = r9(a) % 360.0
    return 0.0 if abs(a - 360.0) < 1e-9 else r9(a)


def key_polygon(p):
    return ("polygon", p["layer"], p["datatype"], cycle(p["xy"]), offsets(p.get("rep")) if "offsets" not in p else tuple(sorted(map(tuple, p["offsets"]))))


def key_path(p):
    return ("path", p["layer"], p["datatype"], tuple(p["xy"]), p["width"], p["end"], tuple(p["ext"]) if p.get("ext") else None,
            offsets(p.get("rep")))


def key_label(l):
    offs = tuple(sorted(map(tuple, l["offsets"]))) if "offsets" in l else offsets(l.get("rep"))
    return ("label", l["layer"], l.get("texttype", 0), l["text"], l["x"], l["y"], norm_angle(l.get("rotation", 0)),
            bool(l.get("mirror", False)), r9(l.get("magnification", 1)), offs)


def key_ref(r):
    offs = tuple(sorted(map(tuple, r["offsets"]))) if "offsets" in r else offsets(r.get("rep"))
    return ("ref", r["cell"], r["x"], r["y"], norm_angle(r.get("rotation", 0)), bool(r.get("mirror", False)),
            r9(r.get("magnification", 1)), offs)


def keys(cell, opts=None):
    opts = opts or {}
    out = Counter()
    for p in cell.get("polygons", []):
        out[key_polygon(p)] += 1
    for p in cell.get("paths", []):
        out[key_path(p)] += 1
    for l in cell.get("labels", []):
        k = key_label(l)
        if opts.get("ignore_label_texttype"):
            k = k[:2] + (None,) + k[3:]
        out[k] += 1
    for r in cell.get("refs", []):
        out[key_ref(r)] += 1
    return out


def diff(a, b, name_a="a", name_b="b", opts=None):
    """Every difference between two canonical libraries, as sentences. Empty = semantically equal."""
    out = []
    if "precision_m" in a and "precision_m" in b and a["precision_m"] and b["precision_m"]:
        if abs(a["precision_m"] / b["precision_m"] - 1) > 1e-12:
            out.append(f"database unit: {name_a} {a['precision_m']!r} m, {name_b} {b['precision_m']!r} m")
    ca, cb = a["cells"], b["cells"]
    for n in sorted(set(ca) - set(cb)):
        out.append(f"cell {n!r} only in {name_a}")
    for n in sorted(set(cb) - set(ca)):
        out.append(f"cell {n!r} only in {name_b}")
    for n in sorted(set(ca) & set(cb)):
        ka, kb = keys(ca[n], opts), keys(cb[n], opts)
        for k, v in sorted((ka - kb).items(), key=str):
            out.append(f"{n}: {v}x only in {name_a}: {k}")
        for k, v in sorted((kb - ka).items(), key=str):
            out.append(f"{n}: {v}x only in {name_b}: {k}")
    return out
