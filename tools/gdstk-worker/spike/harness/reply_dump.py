"""Decodes the replies to recorded session jobs (windows_bundle.py) into JSON with every f64 at full precision
(repr), so a job whose reply HASH differs on another RID can be explained value by value. Run it on each RID
and diff the outputs. Scratch harness code.

    python3 reply_dump.py <bundle-dir> <out.json> <job-name>... [--worker "<command line>"]

The worker is bin/<rid> under Wine as replay_session.py picks it when --worker is not given and CRF_GDSTK_RID
names the rid; otherwise --worker.
"""
import json
import os
import shlex
import struct
import subprocess
import sys


def main():
    args = sys.argv[1:]
    argv = None
    if "--worker" in args:
        i = args.index("--worker")
        argv = shlex.split(args[i + 1])
        del args[i:i + 2]
    bundle, out, jobs = os.path.abspath(args[0]), args[1], args[2:]
    wine = os.environ.get("CRF_GDSTK_WINE") == "1"
    if argv is None:
        argv = (["wine"] if wine else []) + [os.path.join(bundle, "bin", os.environ["CRF_GDSTK_RID"], "gdstk-worker.exe")]
    root = "Z:" + bundle.replace("/", "\\") if wine else bundle
    root_json = json.dumps(root)[1:-1]
    result = {}
    for job in jobs:
        data = open(os.path.join(bundle, "jobs", job + ".job"), "rb").read()
        p = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        at, replies = 0, []
        while at < len(data):
            jl, bl = struct.unpack_from("<II", data, at)
            text = data[at + 8:at + 8 + jl].decode().replace("@ROOT@", root_json)
            b = data[at + 8 + jl:at + 8 + jl + bl]
            at += 8 + jl + bl
            p.stdin.write(struct.pack("<II", len(text.encode()), len(b)) + text.encode() + b)
            p.stdin.flush()
            rjl, rbl = struct.unpack("<II", p.stdout.read(8))
            reply = json.loads(p.stdout.read(rjl))
            raw = p.stdout.read(rbl)
            blobs, k = {}, 0
            for bd in reply.get("blobs", []):
                size = {"f64": 8, "u32": 4, "bytes": 1}[bd["type"]] * bd["count"]
                chunk = raw[k:k + size]
                k += size
                blobs[bd["name"]] = [repr(v) for v in struct.unpack(f"<{bd['count']}d", chunk)] if bd["type"] == "f64" else chunk.hex()
            replies.append({"json": json.dumps(reply, sort_keys=True), "blobs": blobs})
        p.kill()
        p.wait()
        result[job] = replies
    json.dump(result, open(out, "w"), indent=0)


if __name__ == "__main__":
    main()
