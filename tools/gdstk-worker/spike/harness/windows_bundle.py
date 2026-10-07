"""Builds the owner's real-Windows session (brief-oasis-gdstk.md §3 Q1, "The owner's confirmation session").

Everything the session needs, so the Windows machine needs no toolchain and no Python: the cross-built workers,
the corpus, and TRANSCRIPTS -- every request frame a macOS run sent, with the session folder written as @ROOT@,
beside the SHA-256 of every reply and every file written. run-session.ps1 replays them and compares.

    python3 windows_bundle.py <out-dir> <corpus-file>...

Scratch harness code.
"""
import hashlib
import json
import os
import shutil
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import q3_fuzz  # noqa: E402
from gdstk_client import Refused, Worker, read_library, write_library  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.environ.get("CRF_GDSTK_CACHE", os.path.expanduser("~/.circuitRF-build/gdstk"))


class Recorder(Worker):
    """A worker whose requests are kept (with the bundle root replaced by @ROOT@) and whose replies are hashed."""

    def __init__(self, root):
        super().__init__()
        self.root = os.path.abspath(root)
        self.frames = []
        self.reply_hashes = []
        orig_stdout_read = self.p.stdout.read
        self.raw = b""

        def read(n):
            b = orig_stdout_read(n)
            self.raw += b
            return b
        self.p.stdout.read = read

    def request(self, obj, blobs=None):
        blobs = blobs or []
        bin_ = b"".join(b for _, _, _, b in blobs)
        sent = dict(obj, blobs=[{"name": n, "type": t, "count": c} for n, t, c, _ in blobs]) if blobs else obj
        text = json.dumps(sent).replace(self.root, "@ROOT@")
        self.frames.append((text.encode(), bin_))
        self.raw = b""
        try:
            return super().request(obj, blobs)
        finally:
            self.reply_hashes.append(hashlib.sha256(self.raw).hexdigest())


def save_job(path, frames):
    with open(path, "wb") as f:
        for j, b in frames:
            f.write(struct.pack("<II", len(j), len(b)) + j + b)


def add_source(out):
    """G1: the worker's source and its Windows build scripts, laid out as the repository has them under src\\,
    so run-session.ps1's step 0 compiles every RID on the Windows machine itself with the build.cmd packaging
    runs. Batch files get CRLF, as a Windows checkout gives them (cmd.exe's label search misreads LF files)."""
    repo = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
    files = ["VERSION", "tools/geometry-worker/find-toolchain.cmd"] + [
        "tools/gdstk-worker/" + f for f in ("build.cmd", "ensure-built.cmd", "recipe.env", "CMakeLists.txt",
                                            "gdstk_worker.cpp", "windows/gdstk-worker.manifest",
                                            "windows/gdstk-worker.rc")]
    for rel in files:
        dst = os.path.join(out, "src", *rel.split("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        data = open(os.path.join(repo, *rel.split("/")), "rb").read()
        if rel.endswith(".cmd"):
            data = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
        open(dst, "wb").write(data)


def main():
    out = os.path.abspath(sys.argv[1])
    corpus = sys.argv[2:]
    if os.path.exists(out):
        shutil.rmtree(out)
    for d in ("bin", "corpus", "jobs", "fuzz", "out"):
        os.makedirs(os.path.join(out, d))
    # CRF_BUNDLE_WORKERS=g1: the product worker each RID ships (tools/gdstk-worker/build/<rid>/gdstk-kernel/, one
    # executable with the UTF-8 manifest built in); otherwise the G0 spike's two variants from the cache.
    g1 = os.environ.get("CRF_BUNDLE_WORKERS") == "g1"
    for rid in ("win-x64", "win-x86", "win-arm64"):
        os.makedirs(os.path.join(out, "bin", rid))
        if g1:
            shutil.copy(os.path.join(HERE, "..", "..", "build", rid, "gdstk-kernel", "gdstk-worker.exe"),
                        os.path.join(out, "bin", rid, "gdstk-worker.exe"))
            continue
        for exe in ("gdstk-worker.exe", "gdstk-worker-utf8.exe"):
            shutil.copy(os.path.join(CACHE, "1.0.1", rid, "spike", exe), os.path.join(out, "bin", rid, exe))
    for f in corpus:
        shutil.copy(f, os.path.join(out, "corpus", os.path.basename(f)))
    plain = {"grid_per_um": 1000, "precision_m": 1e-9,
             "cells": {"top": {"polygons": [{"layer": 1, "datatype": 0, "xy": [0, 0, 1000, 0, 1000, 500, 0, 500]}],
                               "paths": [], "labels": [], "refs": []}}}
    w = Worker()
    write_library(plain, os.path.join(out, "corpus", "plain.gds"), "gds", w)
    write_library(plain, os.path.join(out, "corpus", "plain.oas"), "oas", w)
    w.close()

    expected = []
    # 1. every corpus file read whole, and written back as GDSII and OASIS
    for i, f in enumerate(sorted(os.listdir(os.path.join(out, "corpus")))):
        src = os.path.join(out, "corpus", f)
        fmt = "oas" if f.endswith(".oas") else "gds"
        r = Recorder(out)
        try:
            lib = read_library(src, fmt, r, tolerance_dbu=0.5)
        except Refused:
            r.close()
            continue
        job = {"job": f"{i:03d}-read-{f}", "frames": len(r.frames), "replies": list(r.reply_hashes), "outputs": {}}
        save_job(os.path.join(out, "jobs", job["job"] + ".job"), r.frames)
        r.close()
        expected.append(job)
        canon_lib = {"precision_m": lib["precision_m"], "cells": lib["cells"]}
        for c in canon_lib["cells"].values():
            for p in c["paths"]:
                if p.get("ext") is None:
                    p.pop("ext", None)
        for ofmt in ("gds", "oas"):
            r = Recorder(out)
            dst = os.path.join(out, "out", f"{f}.rt.{ofmt}")
            try:
                write_library(canon_lib, dst, ofmt, r, {"compression_level": 6, "validation": "crc32"})
            except Refused:
                r.close()
                continue
            job = {"job": f"{i:03d}-write-{f}.{ofmt}", "frames": len(r.frames), "replies": list(r.reply_hashes),
                   "outputs": {f"out/{f}.rt.{ofmt}": hashlib.sha256(open(dst, "rb").read()).hexdigest()}}
            save_job(os.path.join(out, "jobs", job["job"] + ".job"), r.frames)
            r.close()
            expected.append(job)
            os.remove(dst)
    json.dump(expected, open(os.path.join(out, "jobs", "expected.json"), "w"), indent=0)

    # 2. the Q3 malformed cases, each with macOS's class and reply hash. G0 used this machine's spike build; G1
    # records them with the worker the transcripts came from (CRF_GDSTK_WORKER), which is the one being shipped.
    if not g1:
        os.environ.pop("CRF_GDSTK_WORKER", None)
    fz = os.path.join(out, "fuzz")
    cases = q3_fuzz.make_cases(fz)
    fexp = []
    for c in cases:
        cls, detail, lib, digest = q3_fuzz.run_case(os.path.join(fz, c["file"]), c["format"], c["validate"])
        fexp.append(dict(c, mac_class=cls, mac_reply_sha256=digest))
    json.dump(fexp, open(os.path.join(fz, "expected.json"), "w"), indent=0)

    if g1:
        add_source(out)
    for f in ("run-session.ps1", "README.txt", "RESULTS-TEMPLATE.md"):
        shutil.copy(os.path.join(HERE, "..", "windows-session", f), os.path.join(out, f))
    print(json.dumps({"bundle": out, "jobs": len(expected), "fuzz_cases": len(fexp)}))


if __name__ == "__main__":
    main()
