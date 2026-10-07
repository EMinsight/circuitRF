# The gdstk worker's recipe

`recipe.env` beside this file is the recipe as data, read (never sourced) by `build.sh`, `build.cmd`, the
packaging scripts and `PackagingScriptTests`. This page says the same for people. The G0 spike measured
everything here (`docs/design/oasis-gdstk-findings.md`, Q1 and Q2).

## What is fetched

| | version | archive | SHA-256 |
|---|---|---|---|
| gdstk | 1.0.1 | https://github.com/heitzmann/gdstk/archive/refs/tags/v1.0.1.tar.gz | `7819120177050db4ef8bd3394158c7cecc4555640d50f634f957eafdcbcd7984` |
| qhull | 2020.2 (qhull_r 8.0.2, which gdstk's `find_package(Qhull 8)` asks for) | https://github.com/qhull/qhull/archive/refs/tags/2020.2.tar.gz | `59356b229b768e6e2b09a701448bfa222c37b797a84f87f864f97462d8dbc7c5` |
| zlib | 1.3.2 | https://github.com/madler/zlib/releases/download/v1.3.2/zlib-1.3.2.tar.gz | `bb329a0a2cd0274d05519d61c667c062e06990d72e125ee2dfa8de64f0119d16` |

**Upstream publishes no SHA-256 for any of them** (zlib publishes a detached `.asc`). Each value was computed by
the G0 spike from the archive it downloaded and built; a download that does not match is never unpacked.

## How it is built

1. zlib, static: `-DZLIB_BUILD_SHARED=OFF -DZLIB_BUILD_STATIC=ON -DZLIB_BUILD_TESTING=OFF`, installed into
   `<cache>/<version>/<rid>/deps`.
2. qhull, the reentrant static library only (`qhullstatic_r`): `-DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON
   -DCMAKE_POLICY_VERSION_MINIMUM=3.5` (qhull declares `cmake_minimum_required(VERSION 3.0)`, which CMake 4
   refuses unless told; a CMake setting, not a patch). Its headers and archive are copied into `deps`; its own
   install would build every application, which nothing uses. gdstk calls it from one function (`convex_hull`).
3. gdstk is **not installed**: `CMakeLists.txt` adds the unpacked source as a sub-project
   (`add_subdirectory(… EXCLUDE_FROM_ALL)`), because as the top-level project gdstk forces its examples and CTest
   on with a `CACHE FORCE` no `-D` can undo. gdstk's own `find_package` then finds zlib and qhull in `deps`
   (`ZLIB_ROOT`, `QHULL_ROOT`, `CMAKE_PREFIX_PATH`). **No patch, no define, and no edit to any gdstk file.**
4. The worker links all three statically: `-static-libstdc++ -static-libgcc` on Linux, `-static` with llvm-mingw
   on Windows, the system libc++ on macOS (deployment target 13.0, circuitRF's own).

Per RID, every hermetic option (`-DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF`,
`-DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF`) keeps a system zlib or qhull from ever being linked. macOS adds
`CMAKE_OSX_ARCHITECTURES`; a cross build adds the toolchain file `build.sh` writes into the cache (llvm-mingw
**20260922** for every Windows RID, the distribution's cross g++ for the other Linux architecture).

## What ships

`gdstk-kernel/gdstk-worker[.exe]`, one file, on every RID in `KERNEL_RIDS`: osx-arm64, osx-x64, linux-x64,
linux-arm64, win-x64, win-x86, win-arm64. It depends only on the system:

| | its dependencies | size (1.0.5) |
|---|---|---|
| macOS | `libSystem.B.dylib`, `libc++.1.dylib` | arm64 1.01 MB, x64 1.09 MB |
| Linux | `libc.so.6`, `libm.so.6`, the loader; GLIBC ≥ 2.36 when built on Debian 12 | x64 2.29 MB, arm64 2.27 MB |
| Windows | `KERNEL32.dll` and the UCRT (`api-ms-win-crt-*`, part of Windows 10 and later); the UTF-8 manifest embedded | x64 2.52 MB, x86 2.58 MB, arm64 2.10 MB |

Licences, and the notice text: `THIRD-PARTY-NOTICES.md` §4, `licenses/Qhull.txt`, `licenses/Zlib.txt`.
