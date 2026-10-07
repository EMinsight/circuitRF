# gdstk-worker

circuitRF's OASIS and second GDSII route: **gdstk** (https://github.com/heitzmann/gdstk), with **qhull** and
**zlib**, behind a small program that circuitRF starts and speaks to over stdin/stdout. gdstk reads and writes
the bytes. Everything after the bytes (layer reconciliation, the mapping dialog, cell folders, the export plan)
is circuitRF's own C#, shared with the native GDSII route (D4). Brief: `docs/sonnet-briefs/brief-oasis-gdstk.md`.
The G0 spike that decided all of this, and every number behind it, is `docs/design/oasis-gdstk-findings.md`;
its harness is `spike/`.

Why out of process (D1, the geometry worker's reasoning): a file reader is where a hostile or damaged file
crashes something. A crash in the worker costs one import, not the user's unsaved document. A runaway read is
stopped by killing a process. Headless use (`convert`, `check`, the MCP server) needs nothing extra.

## The files

| file | what it is |
|---|---|
| `gdstk_worker.cpp` | the worker: **one C++17 source file**, MIT. Its header states the protocol |
| `CMakeLists.txt` | adds gdstk from the cache as a sub-project and links one static executable |
| `windows/gdstk-worker.manifest`, `.rc` | Windows: the UTF-8 active code page, embedded as the executable's manifest |
| `build.sh` / `build.cmd` | fetch and verify the three archives, build static zlib and qhull into the per-user cache, then the worker. **The only thing that fetches anything** |
| `ensure-built.sh` / `.cmd` | what `dotnet build` runs: reads the cache and nothing else; compiles the worker if stale, checks it links only system libraries, stages `gdstk-kernel/`, or warns once |
| `recipe.env` | the recipe as data: versions, URLs, SHA-256s, CMake options, `KERNEL_RIDS` (the RIDs that ship it) |
| `RECIPE.md` | the same recipe for people, and what ships |
| `RESOLVED.md` | departures from the geometry worker, and what building it found |
| `spike/` | the G0 spike's harness (scratch; `runs/` is git-ignored) |

Windows finds llvm-mingw, CMake and Ninja through the geometry worker's `tools/geometry-worker/find-toolchain.cmd`
(one place looks, not two).

## Building it

```bash
tools/gdstk-worker/build.sh                    # this machine's RID: the dependencies once (seconds), then the worker
tools/gdstk-worker/build.sh --rid osx-x64      # the other Mac architecture
tools/gdstk-worker/build.sh --rid win-x64      # a Windows RID from a Mac or Linux, with llvm-mingw (CRF_LLVM_MINGW)
```
```powershell
tools\gdstk-worker\build.cmd                   # Windows; --rid win-arm64 | win-x86 for the others
```

Needs CMake and a C++ compiler (Xcode's command line tools, `build-essential`; on Windows llvm-mingw and Ninja,
no Visual Studio). A first build of a RID takes **well under a minute** (G0: 9–23 s with gdstk compiled; the
dependencies alone take a few seconds); later runs only recompile the worker when it is stale.

**The cache**, never the repository: `~/.circuitRF-build/gdstk/<version>/` holds the verified archives and the
unpacked sources (gdstk's is compiled with the worker), and `…/<version>/<rid>/` the static `deps/` (zlib, qhull)
and `install.json`, written **last** so an interrupted build is never taken for a finished one. A cross build's
toolchain file is `…/<version>/toolchain-<rid>.cmake`. `CRF_GDSTK_CACHE` moves the cache (no space in the path);
on Windows it defaults to `%LOCALAPPDATA%\circuitRF-build\gdstk`. Each archive is checked against the recipe's
SHA-256 before anything is unpacked, and a mismatch stops the build naming both hashes.

**`dotnet build` never fetches anything** (R-oas-1c). It runs `ensure-built`, which with an empty cache prints one
warning (`GD001`, *the gdstk worker is not built for <rid> …*) and succeeds, with no C++ toolchain and no network.
circuitRF then builds, tests and runs with the worker absent (D6). Skip the step with `-p:CrfSkipGdstkWorker=true`.
The packaging scripts run `build.sh --strict` (`build.cmd` on Windows) per RID and fail when a RID in `KERNEL_RIDS`
lacks the worker, unless `CRF_ALLOW_NO_GDSTK=1`; `tools/CliSmoke` checks it answers `--version` and `selftest`.

## Where it sits

One folder, `gdstk-kernel/`, beside the assemblies (`Contents/MacOS/gdstk-kernel/` in the `.app`), holding **one
file**: `gdstk-worker` (`gdstk-worker.exe` on Windows). gdstk, qhull and zlib are static archives linked into it;
Linux also links libstdc++ and libgcc statically (glibc stays dynamic; the floor is the build host's, 2.36 on
Debian 12), and Windows links llvm-mingw's libc++ and libunwind statically, so no runtime DLL sits beside it.
`ensure-built` refuses to stage a worker that names any library but the system's own (`otool -L`, `readelf -d`,
`llvm-objdump -p`). The folder name has no dot (codesign). On macOS the worker is signed on its own before the
bundle is sealed (`src/Ui/bundleForMacOS.sh`); harmonicaRF and wBond drop the folder.

**Windows paths** (G0 Q3, the owner's real-Windows session): the manifest sets the process's ANSI code page to
UTF-8, so gdstk's `fopen(const char*)` reads the UTF-8 path the protocol carries on a machine of any code page
(Windows 10 1903 or later; `hello` reports `code_page`). **A path of 260 characters or more needs `\\?\`**
(`\\?\UNC\server\share\…` for a share), which **the worker adds itself**, so a client sends any path as it is:
`longPathAware` is honoured only where the machine's `LongPathsEnabled` policy is on, and by default it is off.

## The protocol

Frames exactly as the geometry worker's: `[uint32 jsonLen][uint32 binLen][JSON][bytes]`, little-endian, with the
binary part declared by the JSON's `blobs` array (`f64`, `u32`, `bytes`). A refusal is an ordinary reply,
`{"ok":false,"code":…,"detail":…}`, and the worker keeps running. Diagnostics, gdstk's own `error_logger`
included, go to stderr. **Transfer is per cell**, so no frame holds a whole library (G0 Q7: the largest cell
frame of a 10⁶-polygon library was 1.27 MB).

| request | in | out |
|---|---|---|
| `hello` | `protocol` | `worker`, `gdstk`, `qhull`, `zlib`, `protocol`, `rid` (compiled for); Windows adds `code_page` |
| `open` | `path`, `format` (`gds`, `oas`), `tolerance_dbu` (0.5 recommended) | `handle`, `unit_m`, `precision_m`, `cells[]` (name, counts, `top`), `layer_names[]`, `messages[]` |
| `cell` | `handle`, `name` | `polygons`, `paths`, `labels`, `refs` (each with `rep` as gdstk holds it), `notes` (properties, robust and multi-element paths, `off_grid_rounded`); blobs `xy`, `path_xy` |
| `close` | `handle` | — |
| `begin-write` | `format`, `unit_m`, `precision_m`, `options` (`compression_level`, `detect_rectangles`, `detect_trapezoids`, `validation` `none`/`crc32`/`checksum32`, `standard_properties`, `max_points`) | `handle` |
| `add-cell` | `handle`, a cell in the `cell` reply's shape | — |
| `finish-write` | `handle`, `path` | `bytes`, `cells`, `messages[]`; written to `<path>.part` and renamed, so a failed write leaves no partial file |
| `shutdown` (or `quit`) | — | `ok`, then exit 0 |
| `selftest` | — | a fixed round trip through both formats (`gds_elements`, `oas_elements`, `oas_valid`), for CliSmoke |

**Coordinates** are the file's integer database units, carried as `f64` that ARE integers: a GDSII file is read
with gdstk's unit set to its own precision (factor exactly 1.0), and an OASIS file's values, which arrive 1 ± 1 ulp
off (G0 Q6), are rounded before they are sent. A value off its grid by more than ulps (a CIRCLE's vertices) is
counted in `notes.off_grid_rounded`. G0 Q6 found the conversion exact for 8 × 1,000,012 values on five RIDs.

**Refusal codes**: `request.malformed`, `request.unknown`, `handle.unknown`, `cell.unknown`, `read.open-failed`
(the file cannot be opened: missing or unreadable), `read.truncated` (the OASIS END
record is not where it must be), `read.checksum` (the OASIS CRC32/checksum32 does not match), `read.corrupt` (a
CBLOCK could not be inflated: gdstk parses on regardless, over bytes it never wrote, so nothing after it is
trusted), `read.failed`, `write.missing-cell`, `write.failed`, `selftest.failed`, `worker.exception`, and
`frame.too-large`, after which the worker exits 4.

## Failure

From G0 Q3 (`docs/design/oasis-gdstk-findings.md`):

- **The OASIS guards are always on**: the END-record check, then `oas_validate`, before gdstk reads a byte. With
  them, every damaged CRC32 file and every truncation G0 made was refused. `read_oas` is called with **no**
  error pointer, because with one it stops silently at the first XNAME/XELEMENT/XGEOMETRY.
- **A crash is a crash**: gdstk can SIGSEGV on damaged input the guards cannot see (a file with no signature),
  and the worker does not try to catch it. D1 isolates it; the client reports *the gdstk worker stopped while
  reading …* and creates nothing.
- **A runaway is a kill**: a corrupt CBLOCK size can make gdstk allocate without bound. The client's timeout and
  memory watch stop it (the findings propose 30 s + 1 s per MB, and a memory cap).
- **A frame that announces more than 64 MB of JSON or 2 GB of binary** is answered `frame.too-large` and the worker
  exits 4: the stream is out of step (a stray byte, a byte-order mark), and waiting would be a hang.

**Test switch.** With `CRF_GDSTK_WORKER_TEST=1` in its environment the worker also accepts `"validate": false` on
an OASIS `open` (the spike's unguarded counts), and the ops `crash` (exit 70, answering nothing) and `sleep`
(`seconds`), which the client's crash and timeout gates are tested against. Without it all three are refused.

`gdstk-worker --version` prints the worker's version (circuitRF's `VERSION`), gdstk's, qhull's, zlib's, the
protocol version and the RID it was compiled for, and exits 0.

## Licence

`gdstk_worker.cpp` and everything else in this directory is **MIT**, circuitRF's own licence. gdstk and its
bundled Clipper 6.4.2 are Boost Software License 1.0; qhull has its own licence, whose `COPYING.txt` must travel
with any copy (`licenses/Qhull.txt`, and the notice says where its source is); zlib is under the zlib licence
(`licenses/Zlib.txt`). None is GPL. G0 Q2 read the header of every file compiled in and found none that
contradicts its project's licence. **Nothing of theirs is in this directory or anywhere in the repository**;
building from source fetches it with the recipe. `THIRD-PARTY-NOTICES.md` §4 lists all four.
