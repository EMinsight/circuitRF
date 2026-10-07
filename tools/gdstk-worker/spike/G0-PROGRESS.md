# G0 spike — progress notes (working file; fold into docs/design/oasis-gdstk-findings.md, then delete)

**2026-10-06: COMPLETE and folded into `docs/design/oasis-gdstk-findings.md`; Remaining 1-5 done. Safe to delete.**

Brief: `docs/sonnet-briefs/brief-oasis-gdstk.md`. Only G0 is in scope; nothing past G0 until the owner reads the
findings. Nothing committed. Another agent works in the repo on MCP; G0 touches only `tools/gdstk-worker/spike/`
(new) plus, at the end, `docs/design/oasis-gdstk-findings.md`, `src/Design/RESOLVED.md`, `testdata/interchange/gdstk/`.

## Environment
- Cache: `~/.circuitRF-build/gdstk/1.0.1/` (archives, `source/`, `<rid>/deps`, `<rid>/spike/` binaries, `build.log`,
  `gdsiidump/` = GdsiiDump build). llvm-mingw 20260922 at `~/.circuitRF-build/toolchains/llvm-mingw-20260922-ucrt-macos-universal`
  (archive SHA-256 52e5f5a7b131021d0c39a37a38fa380a1da7885cd04bd61afd0cd4ecfb8bc1f3).
- Docker Desktop only (`--context desktop-linux`). Images: `crf-gdstk-linux` (Debian 12 arm64, cross g++, CMake 3.31.8),
  `crf-gdstk-wine` (amd64 Debian 12, wine 8.0, wine64+wine32, python3). `docker/run-linux.sh` builds linux RIDs;
  `docker/run-check.sh <rid> "<cmd>"` runs a harness command on linux-arm64 / linux-x64 / win-x64 / win-x86 (Wine).
  Docker Desktop got wedged once after a full disk; relaunch = `open /Applications/Docker.app` (it is quarantined, so it
  runs translocated). No Rosetta on this Mac -> osx-x64 is build-only. Wine cannot run ARM64 -> win-arm64 build-only.
- Rebuild a RID: `tools/gdstk-worker/spike/build.sh <rid>` (osx-*, win-*); linux via `docker/run-linux.sh`.
  GdsiiDump: `dotnet build tools/gdstk-worker/spike/GdsiiDump -c Release -o ~/.circuitRF-build/gdstk/1.0.1/gdsiidump`.

## Done (numbers are in tools/gdstk-worker/spike/runs/, git-ignored)
- **Q1 build**: all 7 RIDs build statically with NO patch to gdstk; gdstk added via add_subdirectory (as top-level it
  CACHE-FORCEs its examples on); its own find_package finds our static zlib/qhull (`Found ZLIB .../deps/lib/libz.a`,
  `QHULL found ... libqhullstatic_r.a`, manual mode). qhull needs `-DCMAKE_POLICY_VERSION_MINIMUM=3.5` under CMake 4.
  First full builds: osx-arm64 14 s, osx-x64 19 s, win-x64 17 s, win-x86 21 s, win-arm64 23 s, linux-arm64 10 s,
  linux-x64 9 s (incremental ~4 s). Worker sizes: osx-arm64 990 KB, osx-x64 1.08 MB, win-x64 2.51 MB, win-x86 2.58 MB,
  win-arm64 2.09 MB, linux-arm64 2.27 MB, linux-x64 2.28 MB. Deps: macOS libSystem + libc++ only; Windows KERNEL32 +
  api-ms-win-crt-* only (no libc++.dll); Linux libc/libm (+ld) only, GLIBC floor 2.36 = Debian 12 build host. 0 compiler
  warnings. Exact CMake lines: `+ cmake` lines in each `<rid>/build.log`. Recipe: `spike/recipe.env` (SHA-256s computed by us;
  upstream publishes none; zlib publishes .asc). Versions: gdstk 1.0.1, qhull 2020.2 (qhull_r 8.0.2), zlib 1.3.2.
- **Q2 licences**: gdstk Boost (the e-beam data-format module .cpp/.hpp carries NO header — covered by LICENSE, not contradictory; layername.cpp
  Boost header, no copyright line); bundled Clipper = **Clipper 1 v6.4.2** (Boost); qhull own licence (COPYING.txt must ship;
  random_r.c, rboxlib_r.c, usermem_r.c no header, covered); zlib licence, all 15 compiled files point to zlib.h. No GPL.
  Raise with owner: Linux statically links libstdc++/libgcc (GPLv3 + GCC Runtime Library Exception — permitted); Windows
  statically links llvm-mingw libc++/libunwind (Apache-2.0 WITH LLVM-exception). licenses/ has no Boost text (Clipper2 listed
  without one); Boost needs no text for binary-only distribution.
- **Q3 fuzz** (macOS, final worker, `runs/q3-osx-arm64/q3-summary.json`): guarded worker — GDSII 105 cases: truncate 50 refused;
  flip 17 equal / 19 wrong-silent / 6 wrong-messaged / 8 refused; random 5 refused (GDSII has no checksum: silent wrong =
  a valid different file). rich-cblock.oas (CRC32) guarded: 105/105 refused. rich-plain.oas (no signature) guarded: truncate 50
  refused; flip 12 refused/10 equal/6 crash/16 wrong-silent/6 wrong-messaged. Unguarded gdstk: truncations SIGSEGV 11-12/50,
  flips crash 6-9/50, one CBLOCK-size flip = real runaway (100% CPU, RSS > 1.8 GB; bounded only by kill). Linux matches macOS
  (one flip case differs by class). Windows numbers must be RE-RUN (binaries changed mid-run). Crashes are SIGSEGV.
  Paths (q3_paths.py): macOS + linux-arm64 all pass (space, é, Ω, CJK, 352-char path; read+write gds+oas). Windows = owner session.
- **Q4** (`runs/q4/q4-osx-arm64.jsonl`): all 11 §8a simple cases agree in all 4 directions + both readers, except label
  mirror/magnification (ours: model can't hold; reader drops mirror with diagnostic, magnification silently). FINDINGS (ours,
  report to owner as separate items, do NOT fix): (1) GdsiiReader collapses a legal rotated AREF whose column vector is along
  y (gdstk writes COLROW (2,3) XY (0,0, 0,3000, 6000,0) for a 90° array) -> all 6 instances at origin, diagnostic says
  "approximated"; (2) layers/datatypes > 32767 wrap negative (Int16) — gdstk reads 40000; (3) GDSII BOX elements silently
  dropped (gdstk reads them as polygons); (4) GdsiiWriter UNITS first real = 1e-6 (user unit in metres) where the spec wants
  database unit in user units (0.001) — geometry unaffected; (5) PATHTYPE 4 custom extensions approximated (known, diagnosed);
  (6) label texttype other than 0/1 lost. gdstk side: rotated-AREF convention is legal; label 45°/30° SREF/8000-vertex
  polygon/1-DBU segment/odd width all agree in GDSII.
- **Q5** (`runs/q5/`): hand-encoded OASIS (`harness/oasis_hand.py`, independent writer) — all 10 fixtures EQUAL: repetition
  types 0-11, point lists 0-5, modal variables, XYRELATIVE, refnum names + strict tables, PLACEMENT_TRANSFORM (mag, 30°),
  shape/text repetitions, X-records, CBLOCK, LAYERNAME (all interval types), 0.25 nm grid + near-2^31 coordinate.
  gdstk-written (`q5_gdstk.py`): rect/poly/trapezoids/placements/repetitions/hierarchy/deflate 0,9/checksum32/std props equal.
  SILENT LOSSES on OASIS write: round path -> flush; odd width 3 -> 4 (half-width integer); label rotation/mag/mirror dropped
  (OASIS has none); gdstk#247 still open: negative explicit offsets written as ~2^64 (circuitRF arrays orthogonal: negative
  pitch -> type 8, reads back fine). CIRCLE -> polygon, vertex count follows read tolerance (r=50 µm: 703 @0.5 DBU, 4968
  @0.01); vertices off-grid -> worker must round + count. TRAPEZOID/CTRAPEZOID only checked gdstk-against-itself.
  read_oas WITH an error pointer stops at first XNAME/XELEMENT/XGEOMETRY (UnsupportedRecord, a "warning"): 1 of 2 polygons,
  no failure (probe `errptr`); worker passes no pointer and checks END reached (library.name set only at END).
- **Q6** (`runs/q6/`): exact — 8 cases × 1,000,012 values (extremes ±2^31 + 10^6 random), read/write × gds/oas × 1 nm/0.25 nm,
  0 mismatches on osx-arm64, linux-arm64, linux-x64, win-x64, win-x86. Read: gds with unit=precision (factor exactly 1.0);
  oas with unit=precision (factor ~1±ulp, 566,644 values non-integral by ulps, llround exact). Write: coordinates n/(unit/precision),
  gdstk llround(x*scaling). Finding: write_oas cannot write a 1 nm or 0.25 nm grid as an integer real (no double p gives
  1e-6/p == 1000 or 4000; searched ±2000 ulps) -> START unit = IEEE double 1000.0000000000001 / 4000.0000000000005 (100,
  10000, 1e5, 1e6 per µm are exact). Map-around: import must round DbuPerMicron to the integer.
- **Q7** (Release, osx-arm64): q7a 10^6 polygons/101 cells: OASIS 8.0 MB read 0.13 s 215 MB peak; GDSII 80 MB read 0.22 s
  230 MB; through worker per-cell transfer 0.50 s, 127 MB of frames total, max frame 1.27 MB, worker peak 217/232 MB.
  q7b one 1000×1000 rectangle repetition: 316 bytes, read <1 ms, expansion to 10^6 polygons 45-66 ms, 198 MB. Proposed
  R-oas-4c limit: 10^6 expanded shapes per import (owner decides).
- **Q8** (`runs/q8/`): all corpus writes deterministic (twice identical; cross-platform byte-identical — see platform check);
  oas_validate accepts all; extensions == width/2 come back as halfwidth (same shape); circuitRF's wrapped negative layer
  becomes layer 0 in the worker (G2 must refuse negative layers). Sizes: tiny files grow (END = 256 bytes); q7a 80 MB -> 8.0 MB;
  8000-vertex polygon 64 KB -> 489 B.
- **Platform check** (`harness/platform_check.py`, 91 corpus files): linux-arm64 91/91 identical to osx-arm64; linux-x64, win-x64,
  win-x86 (Wine) 90/91 — the one is q5-ref-repetitions.oas, the #247 corrupt-offset file (llround of out-of-range values
  differs by libm). selftest ok everywhere.

## Worker changes the spike forced (G1 must keep them)
- No error pointer to read_oas; END-reached check; OASIS guards before oas_precision: END-record structural check
  (`EndRecordProblem`, last 256 bytes) + oas_validate (CRC32/checksum32); `"validate": false` disables (Q3 only).
- Replies always valid UTF-8 (non-UTF-8 name bytes -> \u00XX Latin-1) — earlier "hangs" were the client failing to decode.
- gdstk error_logger captured per call; Windows must NOT use tmpfile() (drive root; under Wine every message was lost) ->
  %TEMP% file "w+bTD". windows.h needs WIN32_LEAN_AND_MEAN + NOGDI (wingdi Polygon clashes with gdstk::Polygon).
- begin-write takes unit_m + precision_m (§5); spine tolerance 0.1 DBU on write; read tolerance 0.5 DBU recommended
  (keeps 1-DBU segments: gdstk removes points with distance < tolerance, strict; issue #277 is the float version of this).
- Windows variant `gdstk-worker-utf8.exe` = same + manifest (activeCodePage UTF-8, longPathAware) for the Q3 path question.

## Remaining
1. Build the Windows session bundle: `python3 harness/windows_bundle.py runs/windows-session $(cat runs/corpus-list.txt)`
   (needs q3_fuzz.make_cases/run_case — just rewritten, untested; windows_bundle.Recorder untested). Then dry-run
   `run-session.ps1` is impossible here (no PowerShell? check `pwsh`); at least test the replay logic with a Python
   replayer under Wine (`docker/run-check.sh win-x64`) so job hashes are known to match off-macOS, then zip for the owner.
2. Re-run Q3 fuzz on linux-arm64, linux-x64, win-x64, win-x86 with the final worker (`docker/run-check.sh <rid>
   "python3 harness/q3_fuzz.py runs/q3-<rid>"`).
3. Spike README (`tools/gdstk-worker/spike/README.md`: each script and what it answers).
4. Deliverables: `docs/design/oasis-gdstk-findings.md` (Q1–Q8, every number above, recipe as data, licence table, go/no-go),
   `src/Design/RESOLVED.md` section "OASIS and gdstk — spike", `testdata/interchange/gdstk/` corpus (<50 KB each, written once:
   the q4 `*.circuitrf.gds`, `*.gdstk.gds`, `*.gdstk.oas` + hand fixtures? — hand fixtures were written by circuitRF's harness,
   allowed) + README naming the writer of each.
5. Go/no-go: expected "go with conditions" pending the owner's Windows session (criteria 1 and 3 need real Windows).
   Conditions: round paths / odd widths / label transforms / circles / properties+X-records as G4 messages; #247 never hit
   (circuitRF writes no explicit repetitions); OASIS grid rounding on import; validation guards on.
6. Report to owner: the 6 "ours" GDSII findings, the Windows session request, gdstk upstream candidates (read_oas error-pointer
   stop; #247; unbounded allocation on corrupt CBLOCK; crash on truncation; write_oas non-integer grid) — owner decides on upstream.
