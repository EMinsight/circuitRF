"""Replays a Windows session bundle (windows_bundle.py) the way run-session.ps1 does, so its recorded hashes are
known to hold off macOS before the owner's real-Windows session (brief-oasis-gdstk.md §3 Q1). It mirrors the
script's steps 1, 2 and 4: --version and selftest, every recorded job (reply frames and written files compared
by SHA-256), and every malformed file (class, and for read/refused the hash of every reply). Step 3, the path
cases, is real Windows only and is not mirrored. Scratch harness code.

    python3 replay_session.py <bundle-dir> <rid> [--worker "<command line>"]

<rid> picks bin/<rid>/gdstk-worker.exe; under Wine (CRF_GDSTK_WINE=1) the command is "wine <exe>" and the
bundle root is handed to the worker as Z:\\..., exactly the substitution the script makes for C:\\....
--worker overrides the command (a macOS or Linux worker, to check the replayer itself).
"""
import hashlib
import json
import os
import shlex
import struct
import subprocess
import sys
import threading

TIMEOUT = 10.0


class Timeout(Exception):
    pass


class W:
    def __init__(self, argv):
        self.p = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        self.sha = hashlib.sha256()
        self.timed_out = False

    def send(self, j, b=b""):
        """One frame out, one reply frame back, bounded by TIMEOUT as Send-Frame bounds it."""
        timer = threading.Timer(TIMEOUT, self._kill)
        timer.start()
        try:
            self.p.stdin.write(struct.pack("<II", len(j), len(b)) + j + b)
            self.p.stdin.flush()
            h = self.p.stdout.read(8)
            if len(h) < 8:
                raise EOFError
            jl, bl = struct.unpack("<II", h)
            js = self.p.stdout.read(jl)
            bs = self.p.stdout.read(bl)
            if len(js) < jl or len(bs) < bl:
                raise EOFError
        except (BrokenPipeError, OSError, EOFError):
            if self.timed_out:
                raise Timeout()
            raise EOFError
        finally:
            timer.cancel()
        frame = h + js + bs
        self.sha.update(frame)
        return frame, json.loads(js.decode("utf-8"))

    def _kill(self):
        self.timed_out = True
        self.p.kill()

    def stop(self):
        try:
            self.p.kill()
        except Exception:
            pass
        self.p.wait()


def main():
    bundle = os.path.abspath(sys.argv[1])
    rid = sys.argv[2]
    wine = os.environ.get("CRF_GDSTK_WINE") == "1"
    if "--worker" in sys.argv:
        argv = shlex.split(sys.argv[sys.argv.index("--worker") + 1])
    else:
        exe = os.path.join(bundle, "bin", rid, "gdstk-worker.exe")
        argv = (["wine"] if wine else []) + [exe]
    root = "Z:" + bundle.replace("/", "\\") if wine else bundle
    root_json = json.dumps(root)[1:-1]
    sha = lambda b: hashlib.sha256(b).hexdigest()  # noqa: E731
    out = {"rid": rid, "worker": argv}

    # 1. version and selftest
    v = subprocess.run(argv + ["--version"], capture_output=True, timeout=60)
    out["version"] = v.stdout.decode(errors="replace").strip()
    w = W(argv)
    _, r = w.send(b'{"op":"selftest"}')
    w.stop()
    out["selftest"] = {k: r.get(k) for k in ("ok", "gds_elements", "oas_elements", "oas_valid")}
    print(json.dumps({"version": out["version"], "selftest": out["selftest"]}), flush=True)

    # 2. recorded jobs
    expected = json.load(open(os.path.join(bundle, "jobs", "expected.json")))
    same, diff = 0, []
    for job in expected:
        data = open(os.path.join(bundle, "jobs", job["job"] + ".job"), "rb").read()
        w = W(argv)
        ok, at, i, why = True, 0, 0, []
        try:
            while at < len(data):
                jl, bl = struct.unpack_from("<II", data, at)
                text = data[at + 8:at + 8 + jl].decode("utf-8").replace("@ROOT@", root_json)
                b = data[at + 8 + jl:at + 8 + jl + bl]
                at += 8 + jl + bl
                frame, _ = w.send(text.encode("utf-8"), b)
                if sha(frame) != job["replies"][i]:
                    ok = False
                    why.append(f"reply {i}")
                i += 1
            for rel, h in job["outputs"].items():
                f = os.path.join(bundle, rel)
                got = sha(open(f, "rb").read()) if os.path.exists(f) else "missing"
                if got != h:
                    ok = False
                    why.append(f"{rel} {got[:12]}")
                if os.path.exists(f):
                    os.remove(f)
        except (EOFError, Timeout) as e:
            ok = False
            why.append(type(e).__name__)
        w.stop()
        if ok:
            same += 1
        else:
            diff.append({"job": job["job"], "why": why})
    out["jobs"] = {"same": same, "total": len(expected), "different": diff}
    print(json.dumps({"jobs_same": same, "jobs_total": len(expected), "different": diff}), flush=True)

    # 4. malformed files
    cases = json.load(open(os.path.join(bundle, "fuzz", "expected.json")))
    same, diff, classes = 0, [], {}
    for c in cases:
        path = os.path.join(bundle, "fuzz", c["file"])
        p = "Z:" + path.replace("/", "\\") if wine else path
        w = W(argv)
        cls, digest, detail = "read", None, None
        try:
            req = {"op": "open", "path": p, "format": c["format"], "tolerance_dbu": 0.5}
            if not c["validate"]:
                req["validate"] = False
            _, r = w.send(json.dumps(req).encode())
            if not r["ok"]:
                cls, detail = "refused", r.get("code")
            else:
                for cell in r["cells"]:
                    _, cr = w.send(json.dumps({"op": "cell", "handle": r["handle"], "name": cell["name"]}).encode())
                    if not cr["ok"]:
                        cls, detail = "refused", cr.get("code")
                        break
                if cls == "read":
                    w.send(json.dumps({"op": "close", "handle": r["handle"]}).encode())
            digest = w.sha.hexdigest()
        except Timeout:
            cls = "hang"
        except EOFError:
            w.p.wait()
            cls, detail = "crash", w.p.returncode
        w.stop()
        classes[cls] = classes.get(cls, 0) + 1
        match = cls == c["mac_class"] and (cls not in ("read", "refused") or digest == c["mac_reply_sha256"])
        if match:
            same += 1
        else:
            diff.append({"file": c["file"], "fixture": c["fixture"], "here": cls, "detail": detail, "macos": c["mac_class"],
                         "same_class": cls == c["mac_class"]})
    out["fuzz"] = {"same": same, "total": len(cases), "classes": classes, "different": diff}
    print(json.dumps({"fuzz_same": same, "fuzz_total": len(cases), "classes": classes, "different": diff}), flush=True)
    json.dump(out, open(os.path.join(os.path.dirname(bundle), f"replay-{rid}{'-wine' if wine else ''}.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
