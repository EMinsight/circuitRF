# gdstk-worker — resolved

## G1: the worker and its shipping (brief-oasis-gdstk.md §4, 2026-10-06)

**Departures from the geometry worker, each deliberate:**

- **One file, no closure.** gdstk, qhull and zlib are static archives, and the C++ runtime is linked statically on
  Linux and Windows (D3), so `gdstk-kernel/` holds the executable alone. There is no run path to set, and no
  library list to read back. Instead, `ensure-built` reads the binary's own dependency list and **refuses to stage
  a worker that names a library the system does not provide** (`otool -L`, `readelf -d`, `llvm-objdump -p`). A
  toolchain that silently dropped `-static` would otherwise ship a worker asking for a `libc++.dll` that nothing
  installs.
- **gdstk is compiled with the worker, not installed.** As a top-level CMake project, gdstk forces its examples
  and CTest on with a `CACHE FORCE`, so it is added as a sub-project from the cache's unpacked source. The cache
  "install" is therefore the static `deps/` (zlib, qhull) **plus gdstk's source tree**. `ensure-built` checks
  both, and `build.sh` fetches and verifies gdstk's archive on **every** run, so a cache whose source was deleted
  is repaired rather than reported missing by every build. A first worker compile is about 6 s (gdstk's 19
  files); an incremental one, about 2 s.
- **Windows RIDs cross-build from macOS** (`build.sh --rid win-*`, llvm-mingw's macOS release, `CRF_LLVM_MINGW`),
  which is how G0 and G1 built and checked them. Windows itself uses `build.cmd`, through the geometry worker's
  `find-toolchain.cmd` rather than a copy. Its messages say `geometry-worker:` because it is that worker's file.
- **The cache folder is shared with the spike.** `<rid>/spike/` holds the G0 spike's binaries, so `build.sh`
  replaces only what it owns (`deps/`, the two dependency build trees) and never the whole `<rid>/` folder.

**Found while building it:**

- **`std::round`, not `llround`, for coordinates.** gdstk#247 writes a negative explicit repetition offset as
  about 2⁶⁴. `llround` of that is undefined, while `std::round` is defined for every double and gives the same
  answer for every value a real coordinate can take. The off-grid count uses an **absolute** 1e-4 DBU: coordinates
  are bounded by 2³¹, where an ulp is about 5e-7, so a relative tolerance would hide a point genuinely off the
  grid at large coordinates.
- **A file that cannot be opened is checked for first**, before the OASIS guards, so it is `read.open-failed`
  and not `read.truncated`. The owner's Windows session showed a 365-character path without `\\?\` being called
  "truncated".
- **A frame header over 64 MB of JSON or 2 GB of binary ends the worker (exit 4)** after a `frame.too-large`
  reply. The first Windows session's byte-order mark was read as a 297,778,159-byte header, and the worker waited
  for it.
- **Under Wine, `hello` reports `code_page` 65001**, but that does not show the manifest took effect: Wine's own
  default may be the reason. Only a Windows session on a machine whose system code page is not UTF-8 shows that.
  The G0 session's machine was already 65001.
- **Wine's `cmd` prints `Invalid parameter.` once per blank line of `recipe.env`** when `build.cmd` reads it.
  Wine's `for /f` hands blank lines to the loop body, giving `set "="`; Windows' `cmd` skips them. The geometry
  worker's `build.cmd` does the same under Wine. It is harmless, and not a bug in either.
- **osx-x64 is built but has never run**: there is no Rosetta on the machine that built it. The packaging
  script's smoke rule (`CRF_ALLOW_UNSMOKED`) decides whether it ships.
- **The Linux GLIBC floor is the build host's**: 2.36 when built on Debian 12. The packaging host decides it.
- **Testing the `EnsureGdstkWorker` target alone**: its `--dest` is `$(MSBuildProjectDirectory)/$(OutDir)`,
  which assumes a relative `OutDir` (every real build's `bin/…`). Passing an absolute `-p:OutDir=/tmp/x/`
  stages under `src/Ui/tmp/x/`. Use `-p:OutDir=obj/<name>/`. The geometry kernel's target has the same shape.

## G1 on real Windows: three sessions on the owner's ARM64 machine (2026-10-06)

**The machine.** The owner's only Windows machine is a Windows 11 ARM64 VM (the registry's ProductName still says
"Windows 10 Home"; build 26200), with LongPathsEnabled=0, system code page 65001 and Windows PowerShell 5.1. win-x64
and win-x86 run there under ARM64 emulation, which the owner accepts in place of x64 hardware. **The installers are
built there too, so every Windows RID has to COMPILE on an ARM64 host**, and the first session tested only
Mac-built binaries. From the second session on, the bundle (`spike/harness/windows_bundle.py`, `CRF_BUNDLE_WORKERS=g1`)
carries the worker's source and Windows scripts under `src\` (`.cmd` files with CRLF line endings, as a Windows
checkout gives them), and `run-session.ps1` step 0 runs the same `build.cmd --strict --rid <rid>` the packaging
runs. The native build is the one every step tests, and the Mac cross-build is kept beside it as
`gdstk-worker-mac.exe`.

**Results** (`spike/runs/windows-results-g1/`):

- **All three RIDs build on ARM64 with llvm-mingw**: dependencies and worker in 15-28 s per RID from an empty cache.
  The bytes differ from the Mac cross-builds (a different llvm-mingw build), and both pass everything below.
- **`hello` reports `code_page` 65001 on all three.** The machine's own code page is 65001 too, so this still does
  not show that the manifest is what sets it.
- **Jobs: 277/279 on every RID, in every session.** The two that differ are the `081-write-q5-ref-repetitions` pair,
  and **rounding did not remove them** (the prediction was 279/279). The fixture's reference at x = 70000 carries
  `explicit_x [2^64, 2^64]`, which is gdstk#247 read back. Writing it converts an out-of-range double to an integer,
  which is undefined and differs by CPU: a GDSII reference at x = -1 on macOS against 0 on Windows, and an OASIS
  coordinate of about 9.2e18 against 0 (reproduced in `spike/runs/g1-081/`). The G0 last-digit jobs 026 and 033 are
  now identical.
- **Long paths: the WORKER adds `\\?\` now** (`NativePath` in `gdstk_worker.cpp`, used by open and finish-write).
  Session 1 found what G0 found: a plain path of 366-368 characters is refused on all three RIDs, and the `\\?\` form
  works. With LongPathsEnabled=0, the manifest's `longPathAware` does nothing. Leaving the prefix to every client
  was the wrong place for the fix. The worker runs `GetFullPathNameW` (so `..` and `/` are resolved before the
  prefix turns normalisation off) and prefixes any full path of `MAX_PATH - 12` or more (`\\?\UNC\` for a share).
  Shorter paths and paths that already carry `\\?\` or `\\.\` pass through untouched. **The C runtime's `rename`
  then refused the prefixed `.part` -> target with ENOENT** (Wine): the read worked and the `.part` was written,
  but the rename failed. So on Windows, finish-write uses `MoveFileExW(MOVEFILE_REPLACE_EXISTING)` and
  `DeleteFileW` on the UTF-16 path. **Session 2: plain 366-374 characters read and write on all three RIDs, from
  both builds.**
- **The static-import check (D3) had never run on Windows.** `ensure-built.cmd` piped `llvm-objdump` into
  `findstr` inside `for /f ('"...\llvm-objdump.exe" -p "..." ^| findstr /C:"DLL Name:"')`. A `for /f` command that
  begins with a quote has its first and last quote stripped by `cmd /c`, so the program path no longer existed.
  `cmd` printed "The system cannot find the path specified." (in every RID's session-2 build log), the loop saw no
  lines, and the check passed having read nothing. The fix: `llvm-objdump` writes to `imports.txt`, `for /f` runs
  `findstr` over that file (its command line does not begin with a quote), and **zero imports is now a failure**,
  because every Windows program imports KERNEL32. The verdict is printed as "imports checked: N DLLs, all Windows'
  own". Wine's `cmd` cannot run a nested `CMD /C` in `for /f` at all, so this cannot be checked off Windows.
  **Session 3: "imports checked: 14 DLLs, all Windows' own" on all three RIDs** (KERNEL32 and the UCRT's
  `api-ms-win-crt-*`), and no stray `cmd` message.
- **Malformed files: every difference is a case read with the guards off, or an OASIS file with no checksum.**
  Per RID and session, 3-7 unguarded `rich-cblock` truncations or flips crash (0xC0000005) where macOS refuses, or
  read with a different reply. Which ones varies between sessions, RIDs and builds, because the outcome depends on
  memory layout. On x86 a `rich-plain` case HANGS where macOS crashes: in session 1 it was `flip-48` with the guards
  on, in session 2 `truncate-27` with them off, and under Wine both crashed. A crash, a hang and a refusal all reach
  the application as a refusal, provided the client bounds every request with a timeout and kills the worker. **G2's
  client must keep that bound.**
- **Explorer cannot copy a results folder holding a 370-character path.** The owner had to cancel the copy in
  sessions 1 and 2. `run-session.ps1` now deletes each RID's `paths-*` test folders after recording the rows, and
  session 3's results folder copied without complaint.

**Session 3 (the last)**: builds ok in 5-7 s each with a warm cache; jobs 277/279 (the 081 pair) and plain long
paths ok on all three RIDs, from both builds; malformed-file differences only in unguarded `rich-cblock` cases
(arm64 5, including one hang where macOS refuses; x64 1; x86 6). **G1 is complete.**

**Harness notes.** For `g1`, `windows_bundle.py` records the malformed-file expectations with `CRF_GDSTK_WORKER`,
the worker that recorded the transcripts. Before this it reset to the G0 spike build. `replay_session.py` writes
`runs/replay-<rid>[-wine].json` and overwrites the previous one; G0's copies are kept in `runs/g0-replays/`. Spike
workers in `~/.circuitRF-build/gdstk/1.0.1/<rid>/spike/` are the G0 binaries, and the product build never deletes
them. Only `run-session.ps1` steps 0 and 3 depend on the Windows machine; steps 1, 2 and 4 are mirrored by
`replay_session.py` under Wine (`docker/run-check.sh win-x64|win-x86`, with `CRF_GDSTK_WORKER_TEST=1` for the
validate-off cases).
