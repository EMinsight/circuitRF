# The OpenCASCADE recipe

How the Open CASCADE Technology inside circuitRF's installers is built. `recipe.env` beside this file is
the same recipe as data — the build scripts read it; this is the copy for people, and the one the
written offer in `THIRD-PARTY-NOTICES.md` points at. Source of every value:
[`docs/design/em-3d-f4b-spike-findings.md`](../../../docs/design/em-3d-f4b-spike-findings.md), "The recipe"
(brief 61, verified there by copy-and-paste from an empty directory).

| | |
|---|---|
| **Version** | Open CASCADE Technology **8.0.1**, upstream tag `V8_0_1` (commit `b8f597c6`) |
| **Source** | `https://github.com/Open-Cascade-SAS/OCCT/archive/refs/tags/V8_0_1.tar.gz` |
| **SHA-256** | `0d6913eae4bcc09a3653ceced6dda1aec11c35a1513d4c06762c9b002092c68a` — upstream publishes no checksum; this is the archive brief 61 built and re-downloaded |
| **Modifications** | **None.** No patch is applied, and none may be (brief 61 `R-em3d61-1c`); if one ever were, it would fall under the same written offer |
| **Linking** | Shared libraries, dynamically linked by `tools/geometry-worker` only |

## Configuration

Every module off, the seven leaf toolkits named — `TKBO`, `TKFillet`, `TKShHealing`, `TKMesh`, `TKPrim`,
`TKDESTEP`, `TKXCAF` — which builds **25 toolkits**, and every optional third-party product off, so **no
third-party library is linked** (brief 61 Q2, Q11; confirmed on macOS, owed on Linux and Windows).

```
cmake -S OCCT-8_0_1 -B build -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=<install>
  -DBUILD_LIBRARY_TYPE=Shared
  -DBUILD_MODULE_FoundationClasses=OFF -DBUILD_MODULE_ModelingData=OFF -DBUILD_MODULE_ModelingAlgorithms=OFF
  -DBUILD_MODULE_Visualization=OFF -DBUILD_MODULE_ApplicationFramework=OFF -DBUILD_MODULE_DataExchange=OFF
  -DBUILD_MODULE_Draw=OFF
  "-DBUILD_ADDITIONAL_TOOLKITS=TKBO;TKFillet;TKShHealing;TKMesh;TKPrim;TKDESTEP;TKXCAF"
  -DUSE_FREETYPE=OFF -DUSE_OPENGL=OFF -DUSE_GLES2=OFF -DUSE_TK=OFF -DUSE_TCL=OFF -DUSE_XLIB=OFF -DUSE_TBB=OFF
  -DUSE_RAPIDJSON=OFF -DUSE_DRACO=OFF -DUSE_VTK=OFF -DUSE_FREEIMAGE=OFF -DUSE_FFMPEG=OFF -DUSE_OPENVR=OFF
  -DUSE_EIGEN=OFF -DBUILD_DOC_Overview=OFF -DBUILD_GTEST=OFF -DBUILD_USE_PCH=OFF
  <the per-RID line>
cmake --build build -j <cores>
cmake --install build
```

| RID | the per-RID line | state |
|---|---|---|
| osx-arm64 | `-DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0` | built by `build.sh`, 4 m 49 s; worker `selftest` passes |
| osx-x64 | `-DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0` | built by `build.sh` (cross, from arm64), 4 m 53 s; **not yet run** (no Rosetta on the building Mac) |
| linux-x64, linux-arm64 | `-DCMAKE_INSTALL_RPATH=$ORIGIN` | owed |
| win-x64 / win-arm64 / win-x86 | llvm-mingw's `<triple>-clang`/`-clang++`/`-windres` with `-G Ninja -DCMAKE_SYSTEM_NAME=Windows`, and `-include occt/mingw-compat.h` | x64 and arm64 built by llvm-mingw (cross, from macOS; 25 toolkits, worker links); **not yet run** on Windows; win-x86 not shipped (below) |

**The Linux line is not brief 61's.** Brief 61 verified macOS, where the libraries name each other as
`@rpath/…` and inherit the worker's own run path. An ELF's `DT_RUNPATH` covers only that object's own
dependencies, so on Linux each OCCT library also needs `$ORIGIN` to find its siblings in the shipped
folder. It is a CMake option — the same sources, compiled identically — not a patch.

**Nor is the Windows line.** Windows builds with llvm-mingw (clang, lld, libc++), not Visual Studio, so
OCCT takes its own MinGW path, whose `-Wl,--export-all-symbols` is what exports a C++ vtable one toolkit
needs from another. OCCT 8.0.1 assumes two things MinGW does not supply, and `mingw-compat.h` — our
file, force-included, not a change to any OCCT file — declares them: `<mutex>`, which
`NCollection_IncAllocator.cxx` uses having included only `<shared_mutex>`; and, on ARM64 only,
`posix_memalign`, which `Standard::AllocateAligned` falls through to because its MinGW branch is x86-only.
That one returns plain `malloc` memory, because `Standard::FreeAligned` releases with `free()` on the
same path; 64-bit Windows' `malloc` is 16-byte aligned, every caller in the 25 toolkits asks for 16, and
anything larger is refused rather than returned misaligned. The worker ships llvm-mingw's `libc++.dll`
and `libunwind.dll` beside it — one copy each, shared by every toolkit.

## What ships

The worker and the libraries it loads, read recursively from the binaries themselves (`otool -L`,
`readelf -d`), each copied under the name it is asked for (`libTKernel.8.0.dylib`, `libTKernel.so.8.0`)
with symlinks dereferenced, into one folder, `geometry-kernel/`. On macOS that is the 25 libraries,
56.0 MB uncompressed per architecture, about 20 MB in a `.dmg`. On Windows, every `libTK*.dll` the
install holds plus llvm-mingw's `libc++.dll` and `libunwind.dll`, app-local — read from the x64 build's
import tables, that is the whole closure beyond Windows' own DLLs and the UCRT. The
installed `share/opencascade/resources` tree is not needed and does not ship.

## Which RIDs ship it (D2)

`recipe.env`'s `KERNEL_RIDS`: osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64, win-arm64.
**win-x86 is out until brief 61's Q13 is answered.** Its release notes carry:

> *The 32-bit Windows edition of circuitRF does not include the geometry kernel, so booleans, fillets,
> chamfers and STEP import and export are unavailable in it; every other feature is the same. The 64-bit
> edition includes the kernel.*

## Adopting a new OCCT

Edit `recipe.env` (version, tag, URL, SHA-256 — computed from a download you have checked — and the
source directory name), run `build.sh` / `build.cmd`, and run the worker's `selftest`
(`printf '{"op":"selftest"}\n' | geometry-kernel/geometry-worker`). Then update this file,
`THIRD-PARTY-NOTICES.md` (the version appears in the written offer) and retain the new archive for the
offer (BUILDING.md, release checklist).
