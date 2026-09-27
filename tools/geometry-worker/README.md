# geometry-worker

circuitRF's geometry kernel: **Open CASCADE Technology** (OCCT) behind a small program that circuitRF
starts and speaks to over stdin/stdout. Booleans, fillets and chamfers, and STEP import and export run
here — never in circuitRF's own process. Brief 62 (`docs/sonnet-briefs/brief-em3d-62-geometry-worker-and-shipping.md`)
built the program and its shipping; brief 63 builds the client, discovery and the protocol proper.

Why out of process (series overview §1c): a crash inside the kernel costs one operation, not the user's
unsaved document; a runaway operation is stopped by killing a process, which always works; the licence
boundary is a file boundary; and headless use (`check`, `em`, the MCP server) is free.

## The files

| file | what it is |
|---|---|
| `geometry_worker.cpp` | the worker — **one C++17 source file**, MIT |
| `CMakeLists.txt` | finds the OCCT the cache holds; sets the run path to the worker's own folder |
| `build.sh` / `build.cmd` | builds OCCT into the per-user cache if it lacks it, then the worker. **The only thing that fetches OCCT** |
| `ensure-built.sh` / `.cmd` | what `dotnet build` runs: reads the cache and nothing else; compiles the worker if stale and copies `geometry-kernel/` beside the assemblies, or warns once |
| `occt/recipe.env` | the recipe as data: version, URL, SHA-256, CMake options, and D2's `KERNEL_RIDS` |
| `occt/RECIPE.md` | the same recipe for people, and what ships |

## Building it

```bash
tools/geometry-worker/build.sh                 # this machine's RID: OCCT once (~5 min), then the worker
tools/geometry-worker/build.sh --rid osx-x64   # the other Mac architecture
```
```powershell
tools\geometry-worker\build.cmd                # Windows; --rid win-arm64 | win-x86 for the others
```

Needs CMake and a C++ compiler — Xcode's command line tools, `build-essential`, or Visual Studio 2022+
with the C++ workload. The first build of a RID takes about 5 minutes on 10 cores and leaves ~1.5 GB of
build tree in the cache (deletable); every later run finds the cache's `install.json` and only compiles
the worker, in seconds.

**The cache**, never the repository: `~/.circuitRF-build/occt/<version>/` holds the verified archive and
the unpacked source, and `…/<version>/<rid>/` the build, the install and `install.json`, written **last**
so an interrupted build is never taken for a finished one. `CRF_OCCT_CACHE` moves it; the path may not
contain a space. The download is checked against the recipe's SHA-256 before anything is unpacked, and a
mismatch stops the build naming both hashes.

**`dotnet build` never builds OCCT** (`R-em3d62-3d`, `-3f`). It runs `ensure-built`, which with an empty
cache prints one warning — *the geometry kernel is not built …* — and succeeds, with no C++ toolchain and
no network needed. circuitRF then builds, tests and runs with the kernel absent: its commands are shown
disabled, and every test that needs the worker skips with a reason. Skip the step entirely with
`-p:CrfSkipGeometryWorker=true`. The packaging scripts run `build.sh --strict` per RID and fail when a
shipping RID lacks the kernel, unless `CRF_ALLOW_NO_KERNEL=1`.

**Cross-building.** macOS builds both architectures from either Mac. Linux builds its own architecture;
the other needs `CRF_OCCT_TOOLCHAIN_FILE` naming a CMake toolchain file (used for OCCT and the worker
alike). Windows' Visual Studio generator targets x64, ARM64 and Win32 from any Windows machine.

## Where it sits

One folder, `geometry-kernel/`, beside the assemblies (`Contents/MacOS/geometry-kernel/` in the `.app`):
the worker and **exactly** its library closure, read from the binaries themselves and copied under the
names they are asked for, symlinks dereferenced. It finds them beside itself and nowhere else:

| | how | checked by |
|---|---|---|
| macOS | install names `@rpath/libTK*.8.0.dylib`, worker `LC_RPATH` = `@loader_path` | `CMakeLists.txt`; `PackagingScriptTests` |
| Linux | worker and libraries `RUNPATH` = `$ORIGIN` (`recipe.env`'s Linux line) | the same |
| Windows | DLLs beside `geometry-worker.exe`; the MSVC runtime app-local | — |

The run path is set on the build-tree binary too, so a worker that could only find OCCT because the cache
happened to be on the machine is impossible by construction; `ensure-built` runs `--version` from the
staged folder as the proof. No directory name under it may contain a dot (codesign reads such a directory
under `Contents/MacOS` as a nested bundle). On macOS every library and the worker are signed individually
before the bundle is sealed (`src/Ui/bundleForMacOS.sh`).

## The protocol — the skeleton (brief 62)

Line-delimited JSON: one request per line on stdin, one response per line on stdout, diagnostics on
stderr. Brief 63 extends it (it may add a binary payload for tessellations).

| request | response |
|---|---|
| `{"op":"hello","protocol":1}` | `{"ok":true,"worker":"<VERSION>","occt":"8.0.1","protocol":1,"modules":[…]}` |
| `{"op":"box","size_um":[x,y,z]}` | `{"ok":true,"solids":1,"faces":6,"volume_um3":…}` |
| `{"op":"selftest"}` | `{"ok":true,"faces":8,"valid":true,"step_roundtrip":true,…}` — a block minus a cylinder, a 25 µm fillet on the bore's rim, and a STEP write and read back in memory; the volume is checked against Pappus |
| `{"op":"quit"}` | `{"ok":true}`, then exit 0 |
| anything else | `{"ok":false,"error":"<sentence>"}` — and the worker keeps running |

`geometry-worker --version` prints `geometry-worker <VERSION>` and `occt <version>` and exits 0, so a
person, the About box and `tools/CliSmoke` can ask without speaking the protocol.

**The OCCT it loaded, not the one it was built against.** The worker compares the version compiled into
it (`OCC_VERSION_COMPLETE`) with the loaded `TKernel`'s own (`OCCT_Version_String_Complete()`). On a
mismatch `--version` says so and exits 1, and every request but `quit` is refused with a sentence naming
both — never a silent run against the wrong library.

Nothing but a response ever reaches stdout: at start-up the worker keeps a private copy of the stdout
descriptor for the protocol and points descriptor 1 at stderr, so anything OCCT prints lands there.

## Failure

Written from brief 61 Q9 (`docs/design/em-3d-f4b-spike-findings.md`, "Crash posture"):

- **A C++ exception** — `Standard_Failure` (which derives from `std::exception` in OCCT 8.0) or any
  `std::exception` — is caught at the request boundary, answered with `ok:false`, and the worker carries on.
- **A fault inside the kernel** is turned into an exception: the worker installs `OSD::SetSignal(false)`
  and wraps each request in `OCC_CATCH_SIGNALS`, which is what Q9 found to work — after a caught SIGSEGV
  the spike's process ran a boolean and a fillet correctly. **The worker still exits after one** (status 3,
  after answering the request with a refusal): a handler cannot vouch for a heap after a wild write, and a
  restart costs 40 ms (Q12). Brief 63 restarts it.
- **Cancellation is by killing the process.** A fillet never polls a user break (Q9), so nothing here
  promises cooperative cancel.

## Licence

`geometry_worker.cpp` and everything else in this directory is **MIT**, circuitRF's own licence, and
carries the prominent notice the Open CASCADE Exception asks for: *this program uses facilities provided
by Open CASCADE Technology*. It includes OCCT's headers and links OCCT's shared libraries dynamically;
**nothing of OCCT's is in this directory or anywhere in the repository**, and `.gitignore` carries the
backstop. Anyone building circuitRF from source fetches OCCT's source themselves, with the recipe.

OCCT is LGPL-2.1-only with the Open CASCADE Exception 1.0. The installers carry it unmodified, as shared
libraries in one replaceable folder; the licence texts are in `licenses/`; the corresponding source is
available under the **written offer** in `THIRD-PARTY-NOTICES.md` (not a published archive, while
[OCCT#1564](https://github.com/Open-Cascade-SAS/OCCT/issues/1564) — eight files whose header contradicts
the licence — is open). `tests/Firewall.Tests/OcctBoundaryTests.cs` holds the boundary: no managed
assembly, project file or P/Invoke reaches OCCT, no OCCT source is vendored, and the notice is present.
