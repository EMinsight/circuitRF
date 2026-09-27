#!/usr/bin/env python3
# brief-em3d-61 Q2 -- recursive dependency closure of a Mach-O executable (otool -L), spike material
import subprocess, sys, os, re
root, libdir = sys.argv[1], sys.argv[2]
seen, order, system = set(), [], set()
def deps(path):
    out = subprocess.run(['otool', '-L', path], capture_output=True, text=True).stdout.splitlines()[1:]
    for line in out:
        name = line.strip().split(' (')[0]
        yield name
def resolve(name, frm):
    if name.startswith('@rpath/'): return os.path.join(libdir, name[len('@rpath/'):])
    if name.startswith('@loader_path/'): return os.path.join(os.path.dirname(frm), name[len('@loader_path/'):])
    return name
stack = [root]
while stack:
    p = stack.pop()
    for n in deps(p):
        r = resolve(n, p)
        if r.startswith('/usr/lib/') or r.startswith('/System/'):
            system.add(n); continue
        real = os.path.realpath(r)
        if real in seen or real == os.path.realpath(p): continue
        seen.add(real); order.append(real); stack.append(real)
tot = 0
for p in sorted(order):
    s = os.path.getsize(p); tot += s
    print(f'{os.path.basename(p):40s} {s:>10d}')
print(f'{"TOTAL (" + str(len(order)) + " libraries)":40s} {tot:>10d}')
print('system:', ' '.join(sorted(system)))
