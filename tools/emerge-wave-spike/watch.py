"""Run a command under a memory, free-disk and time watchdog (brief-emw-1 §0).

A Gmsh runaway inside an EMerge run once grew to ~8 GB in under half a second and swapped the disk full, so this polls
every 0.1 s and kills the whole process group at the first of: resident memory over --rss-gb, free disk on the volume
of the working directory under --free-gb, or wall clock over --minutes. Standard library only.

    python3 watch.py [--rss-gb 4] [--free-gb 10] [--minutes 15] -- <command> [args...]

Exit status: the command's own, or 137 when the watchdog killed it (the reason on stderr).
"""
import os, shutil, signal, subprocess, sys, time


def tree_rss_kb(root):
    out = subprocess.run(["ps", "-A", "-o", "pid=,ppid=,rss="], capture_output=True, text=True).stdout
    kids, rss = {}, {}
    for line in out.splitlines():
        p = line.split()
        if len(p) != 3:
            continue
        pid, ppid, kb = int(p[0]), int(p[1]), int(p[2])
        kids.setdefault(ppid, []).append(pid)
        rss[pid] = kb
    total, stack = 0, [root]
    while stack:
        pid = stack.pop()
        total += rss.get(pid, 0)
        stack.extend(kids.get(pid, []))
    return total


def main(argv):
    rss_gb, free_gb, minutes = 4.0, 10.0, 15.0
    if "--" not in argv:
        sys.exit(__doc__)
    cut = argv.index("--")
    opts, cmd = argv[:cut], argv[cut + 1:]
    for k in range(0, len(opts), 2):
        if opts[k] == "--rss-gb": rss_gb = float(opts[k + 1])
        elif opts[k] == "--free-gb": free_gb = float(opts[k + 1])
        elif opts[k] == "--minutes": minutes = float(opts[k + 1])
    started = time.time()
    proc = subprocess.Popen(cmd, start_new_session=True)
    peak, why = 0, None
    while proc.poll() is None:
        kb = tree_rss_kb(proc.pid)
        peak = max(peak, kb)
        free = shutil.disk_usage(os.getcwd()).free / 2 ** 30
        if kb > rss_gb * 2 ** 20:
            why = f"resident memory {kb / 2 ** 20:.2f} GB over {rss_gb} GB"
        elif free < free_gb:
            why = f"free disk {free:.1f} GB under {free_gb} GB"
        elif time.time() - started > minutes * 60:
            why = f"wall clock over {minutes} min"
        if why:
            os.killpg(proc.pid, signal.SIGKILL)
            proc.wait()
            print(f"watch.py: killed: {why} (peak {peak / 2 ** 20:.2f} GB)", file=sys.stderr)
            return 137
        time.sleep(0.1)
    print(f"watch.py: exit {proc.returncode}, {time.time() - started:.1f} s, peak {peak / 2 ** 20:.2f} GB", file=sys.stderr)
    return proc.returncode


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
