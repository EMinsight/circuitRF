"""Q3 -- malformed input (brief-oasis-gdstk.md §3). For each of three fixtures: 50 truncations at random
offsets, 50 single-byte flips, and 5 files of random bytes. Every case runs in a FRESH worker process, reads
the whole library (open + every cell), and is classed as one of

    refused          the worker answered ok:false (a clean refusal)
    equal            read, and equal to the unmodified fixture (the mutation changed nothing readable)
    wrong-silent     read, DIFFERENT from the fixture, and gdstk said nothing   <- the finding that matters
    wrong-messaged   read, different, with a gdstk message in the reply
    crash            the worker died (its exit status / signal recorded)
    hang             no answer in 10 s; killed

Fixtures: rich.gds (GDSII); rich-cblock.oas (CBLOCK level 6, CRC32) and rich-plain.oas (no CBLOCK, no
signature), each OASIS fixture read with the worker's guards (END-record check, oas_validate) and without.
The cases are files (make_cases), so the owner's Windows session replays exactly these. Scratch harness code.

    python3 q3_fuzz.py <dir> [seed]          (worker from CRF_GDSTK_WORKER or this machine's build)
"""
import hashlib
import json
import os
import random
import sys
import threading

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import canon  # noqa: E402
import q4  # noqa: E402
from gdstk_client import Refused, Worker, read_library, write_library  # noqa: E402

TIMEOUT = 10.0
PLANS = [("rich.gds", "gds", True), ("rich-cblock.oas", "oas", True), ("rich-cblock.oas", "oas", False),
         ("rich-plain.oas", "oas", True), ("rich-plain.oas", "oas", False)]


def rich_library():
    """Every Q4 simple fixture's cells in one library, each cell renamed by its fixture."""
    cells = {}
    for name in ("polygon7", "path-ends", "labels", "sref-transform", "aref-3x2", "hierarchy", "layers-datatypes"):
        for cname, c in q4.SIMPLE[name]["cells"].items():
            c2 = json.loads(json.dumps(c))
            for r in c2.get("refs", []):
                r["cell"] = f"{name}.{r['cell']}"
            cells[f"{name}.{cname}"] = c2
    return {"grid_per_um": 1000, "precision_m": 1e-9, "cells": cells}


class Hashing(Worker):
    """A worker whose reply bytes are hashed, so another RID's answer can be compared without canonicalising."""

    def __init__(self):
        super().__init__()
        self.h = hashlib.sha256()
        orig = self.p.stdout.read

        def read(n):
            b = orig(n)
            self.h.update(b)
            return b
        self.p.stdout.read = read


def run_case(path, fmt, validate):
    """Read in a fresh worker with a 10 s watchdog: (class, detail, library or None, sha256 of every reply)."""
    w = Hashing()
    timer = threading.Timer(TIMEOUT, w.p.kill)
    timer.start()
    orig = w.request

    def request(obj, blobs=None):
        if obj.get("op") == "open" and not validate:
            obj = dict(obj, validate=False)
        return orig(obj, blobs)
    w.request = request
    try:
        lib = read_library(path, fmt, w, tolerance_dbu=0.5)
        return "read", None, lib, w.h.hexdigest()
    except Refused as r:
        return "refused", r.reply.get("code"), None, w.h.hexdigest()
    except UnicodeDecodeError as e:
        return "protocol-error", str(e)[:80], None, w.h.hexdigest()
    except (EOFError, BrokenPipeError, ValueError, OSError):
        w.p.wait()
        if not timer.is_alive():
            return "hang", None, None, None
        return "crash", w.p.returncode, None, None
    finally:
        timer.cancel()
        try:
            w.p.kill()
        except Exception:
            pass
        w.p.wait()


def mutate(data, kind, rng):
    if kind == "truncate":
        k = rng.randrange(1, len(data))
        return data[:k], {"offset": k}
    if kind == "flip":
        k = rng.randrange(len(data))
        v = rng.randrange(256)
        while v == data[k]:
            v = rng.randrange(256)
        return data[:k] + bytes([v]) + data[k + 1:], {"offset": k, "from": data[k], "to": v}
    return bytes(rng.randrange(256) for _ in range(len(data))), {"bytes": len(data)}


def make_cases(d, seed=2026):
    """The fixtures and every mutated case, as files in d. Returns the case list (each fixture's base first)."""
    os.makedirs(d, exist_ok=True)
    lib = rich_library()
    w = Worker()
    write_library(lib, os.path.join(d, "rich.gds"), "gds", w)
    write_library(lib, os.path.join(d, "rich-cblock.oas"), "oas", w, {"compression_level": 6, "validation": "crc32"})
    write_library(lib, os.path.join(d, "rich-plain.oas"), "oas", w, {"compression_level": 0, "validation": "none"})
    w.close()
    cases = []
    for fixture, fmt, validate in PLANS:
        key = fixture + ("" if validate else " (no validate)")
        cases.append({"file": fixture, "format": fmt, "validate": validate, "fixture": key, "kind": "base", "info": {}})
        data = open(os.path.join(d, fixture), "rb").read()
        rng = random.Random(f"{seed}-{fixture}")  # the same mutations with and without the guards
        stem = fixture.rsplit(".", 1)[0]
        for kind, n in (("truncate", 50), ("flip", 50), ("random", 5)):
            for i in range(n):
                m, info = mutate(data, kind, rng)
                name = f"{stem}-{kind}-{i:02d}.{fmt}"
                with open(os.path.join(d, name), "wb") as f:
                    f.write(m)
                cases.append({"file": name, "format": fmt, "validate": validate, "fixture": key, "kind": kind, "info": info})
    return cases


def main():
    d = sys.argv[1]
    seed = int(sys.argv[2]) if len(sys.argv) > 2 else 2026
    cases = make_cases(d, seed)
    base = {}
    summary = {}
    for c in cases:
        cls, detail, lib, _ = run_case(os.path.join(d, c["file"]), c["format"], c["validate"])
        if c["kind"] == "base":
            assert cls == "read", f"the unmodified {c['file']} did not read"
            base[c["fixture"]] = lib
            summary[c["fixture"]] = {"bytes": os.path.getsize(os.path.join(d, c["file"])), "counts": {}, "notable": []}
            continue
        if cls == "read":
            b = base[c["fixture"]]
            diffs = canon.diff({"precision_m": b["precision_m"], "cells": b["cells"]},
                               {"precision_m": lib["precision_m"], "cells": lib["cells"]})
            cls = "equal" if not diffs else ("wrong-messaged" if lib["messages"] else "wrong-silent")
        s = summary[c["fixture"]]
        s["counts"].setdefault(c["kind"], {}).setdefault(cls, 0)
        s["counts"][c["kind"]][cls] += 1
        if cls not in ("refused", "equal"):
            s["notable"].append({"file": c["file"], "kind": c["kind"], **c["info"], "class": cls, "detail": detail})
    for k, s in summary.items():
        print(json.dumps({"fixture": k, "bytes": s["bytes"], "counts": s["counts"]}), flush=True)
    json.dump(summary, open(os.path.join(d, "q3-summary.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
