"""The spike's client for gdstk-worker (brief-oasis-gdstk.md §5's frames). Scratch harness code.

Reads a library into the CANONICAL form every comparison in the spike uses:

    {"unit_m", "precision_m", "cells": {name: {"polygons": [...], "paths": [...], "labels": [...],
     "refs": [...]}}, "layer_names": [...], "messages": [...], "nonintegral": n}

with every coordinate an exact integer in the file's database units (asserted, and counted when not).
"""
import json
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def worker_path(rid=None):
    import platform
    if rid is None:
        m = platform.machine()
        rid = ("osx-" if sys.platform == "darwin" else "linux-") + ("arm64" if m in ("arm64", "aarch64") else "x64")
    root = os.environ.get("CRF_GDSTK_CACHE", os.path.expanduser("~/.circuitRF-build/gdstk"))
    exe = "gdstk-worker.exe" if rid.startswith("win-") else "gdstk-worker"
    return os.path.join(root, "1.0.1", rid, "spike", exe)


class Refused(Exception):
    def __init__(self, reply):
        super().__init__(f"{reply.get('code')}: {reply.get('detail')}")
        self.reply = reply


class Worker:
    def __init__(self, exe=None, argv=None, stderr=subprocess.DEVNULL):
        # CRF_GDSTK_WORKER (a command line: "wine .../gdstk-worker.exe") wins, so every script runs on any RID.
        if os.environ.get("CRF_GDSTK_WORKER"):
            import shlex
            argv = shlex.split(os.environ["CRF_GDSTK_WORKER"])
        self.p = subprocess.Popen(argv or [exe or worker_path()], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=stderr)
        self.last_frame_bytes = 0
        # A worker under Wine names files the Windows way: CRF_GDSTK_WINE=1 maps /a/b to Z:\a\b.
        self.wine = os.environ.get("CRF_GDSTK_WINE") == "1"

    def path(self, p):
        p = os.path.abspath(p)
        return "Z:" + p.replace("/", "\\") if self.wine else p

    def request(self, obj, blobs=None):
        blobs = blobs or []
        bin_ = b"".join(b for _, _, _, b in blobs)
        if blobs:
            obj = dict(obj, blobs=[{"name": n, "type": t, "count": c} for n, t, c, _ in blobs])
        j = json.dumps(obj).encode()
        self.p.stdin.write(struct.pack("<II", len(j), len(bin_)) + j + bin_)
        self.p.stdin.flush()
        h = self.p.stdout.read(8)
        if len(h) < 8:
            raise EOFError("the worker stopped")
        jl, bl = struct.unpack("<II", h)
        reply = json.loads(self.p.stdout.read(jl))
        data = self.p.stdout.read(bl)
        self.last_frame_bytes = 8 + jl + bl
        if not reply.get("ok"):
            raise Refused(reply)
        out = {}
        at = 0
        for b in reply.get("blobs", []):
            size = {"f64": 8, "u32": 4, "bytes": 1}[b["type"]] * b["count"]
            raw = data[at:at + size]
            at += size
            out[b["name"]] = list(struct.unpack(f"<{b['count']}d", raw)) if b["type"] == "f64" else raw
        reply["_blobs"] = out
        return reply

    def close(self):
        try:
            self.request({"op": "shutdown"})
        except Exception:
            pass
        try:
            self.p.stdin.close()
        except Exception:
            pass
        self.p.wait(timeout=10)


class Counter:
    def __init__(self):
        self.nonintegral = 0

    def i(self, v):
        r = round(v)
        if abs(v - r) > 1e-6:
            self.nonintegral += 1
        return int(r)


def _rep(rep, c):
    if not rep:
        return None
    k = rep["kind"]
    if k == "rectangular":
        return {"kind": k, "columns": rep["columns"], "rows": rep["rows"], "spacing": [c.i(v) for v in rep["spacing"]]}
    if k == "regular":
        return {"kind": k, "columns": rep["columns"], "rows": rep["rows"], "v1": [c.i(v) for v in rep["v1"]],
                "v2": [c.i(v) for v in rep["v2"]]}
    if k == "explicit":
        return {"kind": k, "offsets": [c.i(v) for v in rep["offsets"]]}
    return {"kind": k, "coords": [c.i(v) for v in rep["coords"]]}


def read_library(path, fmt, worker=None, tolerance_dbu=None):
    """Open path through the worker and return the canonical form (see the module docstring)."""
    own = worker is None
    w = worker or Worker()
    try:
        req = {"op": "open", "path": w.path(path), "format": fmt}
        if tolerance_dbu is not None:
            req["tolerance_dbu"] = tolerance_dbu
        o = w.request(req)
        c = Counter()
        lib = {"unit_m": o["unit_m"], "precision_m": o["precision_m"], "cells": {}, "layer_names": o["layer_names"],
               "messages": o["messages"], "error_code": o["error_code"], "tops": [x["name"] for x in o["cells"] if x["top"]],
               "frame_bytes": 0, "notes": {}}
        for cell in o["cells"]:
            r = w.request({"op": "cell", "handle": o["handle"], "name": cell["name"]})
            lib["frame_bytes"] += w.last_frame_bytes
            xy, pxy = r["_blobs"].get("xy", []), r["_blobs"].get("path_xy", [])
            polys, at = [], 0
            for p in r["polygons"]:
                n = p["n"]
                polys.append({"layer": p["layer"], "datatype": p["datatype"],
                              "xy": [c.i(v) for v in xy[at:at + 2 * n]], "rep": _rep(p.get("rep"), c)})
                at += 2 * n
            paths, at = [], 0
            for p in r["paths"]:
                n = p["n"]
                paths.append({"layer": p["layer"], "datatype": p["datatype"], "width": c.i(p["width"]), "end": p["end"],
                              "ext": [c.i(v) for v in p["ext"]] if "ext" in p else None,
                              "xy": [c.i(v) for v in pxy[at:at + 2 * n]], "rep": _rep(p.get("rep"), c)})
                at += 2 * n
            labels = [{"layer": l["layer"], "texttype": l["texttype"], "text": l["text"], "x": c.i(l["x"]), "y": c.i(l["y"]),
                       "anchor": l["anchor"], "rotation": l["rotation"], "magnification": l["magnification"],
                       "mirror": l["mirror"], "rep": _rep(l.get("rep"), c)} for l in r["labels"]]
            refs = [{"cell": x["cell"], "x": c.i(x["x"]), "y": c.i(x["y"]), "rotation": x["rotation"],
                     "magnification": x["magnification"], "mirror": x["mirror"], "rep": _rep(x.get("rep"), c)}
                    for x in r["refs"]]
            lib["cells"][cell["name"]] = {"polygons": polys, "paths": paths, "labels": labels, "refs": refs}
            lib["notes"][cell["name"]] = r["notes"]
        lib["nonintegral"] = c.nonintegral
        w.request({"op": "close", "handle": o["handle"]})
        return lib
    finally:
        if own:
            w.close()


def write_library(lib, path, fmt, worker=None, options=None):
    """Write the canonical form through gdstk. lib needs precision_m (or grid_per_um) and cells."""
    own = worker is None
    w = worker or Worker()
    try:
        grid = lib.get("grid_per_um") or round(1e-6 / lib["precision_m"])
        h = w.request({"op": "begin-write", "format": fmt, "unit_m": 1e-6, "precision_m": 1e-6 / grid,
                       "options": options or {}})["handle"]
        for name, cell in lib["cells"].items():
            xy, pxy, polys, paths = [], [], [], []
            for p in cell.get("polygons", []):
                polys.append({"layer": p["layer"], "datatype": p["datatype"], "n": len(p["xy"]) // 2,
                              **({"rep": p["rep"]} if p.get("rep") else {})})
                xy += p["xy"]
            for p in cell.get("paths", []):
                d = {"layer": p["layer"], "datatype": p["datatype"], "width": p["width"], "end": p["end"],
                     "n": len(p["xy"]) // 2}
                if p.get("ext") is not None:
                    d["ext"] = p["ext"]
                if p.get("rep"):
                    d["rep"] = p["rep"]
                paths.append(d)
                pxy += p["xy"]
            blobs = [("xy", "f64", len(xy), struct.pack(f"<{len(xy)}d", *xy)),
                     ("path_xy", "f64", len(pxy), struct.pack(f"<{len(pxy)}d", *pxy))]
            w.request({"op": "add-cell", "handle": h, "name": name, "polygons": polys, "paths": paths,
                       "labels": cell.get("labels", []), "refs": cell.get("refs", [])}, blobs)
        return w.request({"op": "finish-write", "handle": h, "path": w.path(path)})
    finally:
        if own:
            w.close()


if __name__ == "__main__":
    print(json.dumps(read_library(sys.argv[1], sys.argv[2]), indent=1))
