# F4b spike — OpenCASCADE, built, measured and tried

**Spike material, not product code** (`docs/sonnet-briefs/brief-em3d-61-kernel-spike.md`). The findings — what
these files answer, and the recommendations for D2 and D12 — are in
`docs/design/em-3d-f4b-spike-findings.md`. Produced on 2026-09-27.

Nothing under `src/`, `tools/`, `packaging/` or `tests/` may include, copy or build anything here. Brief 62
writes the geometry worker; it may *read* the harness.

## Versions, machine, OS

| | |
|---|---|
| Machine | Apple M4, 10 cores, 16 GB; macOS 27.0 (26A428); no Rosetta installed |
| Toolchain | Apple clang 21.0.0, macOS SDK 27.0, CMake 4.4.3, GNU Make 3.81 |
| OCCT | **8.0.1**, built from the upstream tag archive with the recipe (`q2/cmake-options.txt`), Release, shared |
| Gmsh | 4.15.2 (Homebrew; carries OCCT 7.9.3) — Q4, and Q8's foreign STEP file |
| openEMS | 0.37.0-rc3 (openEMS `67d3784`, CSXCAD `dcdb62b`) — Q10 |

OCCT's source, build tree and libraries are **not** committed (1.5 GB of objects per RID). The recipe says how to
get them.

## Layout

```
harness/occt_probe.cpp     the probe: one sub-command per question (selftest, q4-q10, q12, noop)
harness/CMakeLists.txt     finds the OCCT the recipe installed
q1/VERSION.md              which OCCT, its archive, checksum, what changed since 7.9
q2/cmake-options.txt       the recipe's CMake line; closure-<rid>.txt, closure.py (otool -L, recursive)
q3/build-<rid>.log         brief 61 section 4's log per RID (two done, five owed); SIZES.md; selftest-osx-arm64.txt
q4/                        the part in three B-rep versions and STEP; five .geo; run.sh; Gmsh logs; VERDICT.md
q5/RESULTS.md              coincident faces: 9 cases x Unite/Subtract/Intersect x 3 modes
q6/RESULTS.md, *.brep      fillets and chamfers on curved edges and a tangent chain, against Pappus
q7/                        history-<op>.txt (naming maps), hashes-<rid>.txt, same-face-pair.txt, DETERMINISM.md
q8/                        six STEP files (mm/um/mil x AP214/AP242), gmsh-written.{geo,step}, RESULTS.md
q9/run.sh, run-<rid>.txt   failure behaviour, one process per case; RESULTS.md
q10/                       part.stl, part.ply, three hand-written CSXCAD cases, openEMS logs, RESULTS.md
q11/CHECKLIST.md           the licence checklist against the built tree
q12/                       process-start.py/.txt, in-process.txt, RESULTS.md
q13/RESULTS.md             Windows x86 and arm64 -- owed by the owner
```

## Re-running

With OCCT built and installed by the recipe at `<occt>`:

```sh
cmake -S testdata/em3d/f4b/harness -B <build>/probe -DCMAKE_BUILD_TYPE=Release -DOpenCASCADE_DIR=<occt>/lib/cmake/opencascade
cmake --build <build>/probe
P=<build>/probe/occt_probe
cd testdata/em3d/f4b
$P selftest
$P q4 --out . && q4/run.sh
$P q5 --out . ; $P q6 --out . ; $P q7 --out .      # q7 reads q6's .brep files
(cd q8 && gmsh gmsh-written.geo -0) ; $P q8 --out .   # writes q8/gmsh-written.step first
q9/run.sh $P
$P q10 --out .   # then, in q10/: openEMS case-{stl,ply,missing}.xml --debug-PEC (writes et, ht, PEC_dump.vtp; not kept)
$P q12 --out . ; python3 q12/process-start.py $P
```

Every generated results file names the OCCT version and RID it was made with.
