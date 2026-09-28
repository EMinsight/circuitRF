# tools/geometry-worker — resolved findings

## Windows builds with llvm-mingw, not Visual Studio (2026-09-27)

The first Windows run of `build-windows.ps1` stopped at `'cmake' is not on PATH`. The owner asked for a
build that needs no Visual Studio (a large install) and a script that offers to install what is missing.
Windows now builds OCCT and the worker with **llvm-mingw + CMake + Ninja**; `find-toolchain.cmd` finds
them, and `build-windows.ps1` offers `winget install` for whatever it reports missing.

Verified by cross-building from macOS with llvm-mingw 20260922: win-x64 and win-arm64 each produce all
25 toolkits and a linked worker. **Not yet run on Windows** — the scripts' cmd logic has only been read.

**zig cc was tried first and does not work for OCCT as shared libraries.** Three findings, each from a
throwaway build:

- **zig silently drops `-Wl,--export-all-symbols`** (and `-rdynamic` does nothing for COFF). OCCT marks
  members, not classes, `Standard_EXPORT`, so under the Itanium ABI MinGW uses, a class's vtable is
  exported only by export-all. Without it, `TKMath` fails on `vtable for Standard_Transient`.
- **Defining `HAVE_NO_DLL` to fall back on lld's auto-export** exports the CRT's `atexit` from every DLL,
  which then collides in each importer.
- **`CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS` does not engage** for this toolchain, and `cmake -E __create_def`
  lists functions but not vtables.

And beyond linking: zig links a private libc++ into every DLL. OCCT throws across DLLs; llvm-mingw's
single shared `libc++.dll` avoids the question entirely.

**CMake does not set `MINGW` for zig's compiler** (Clang, platform MinGW), so OCCT's own MinGW branch is
skipped; it does for llvm-mingw's.

**OCCT 8.0.1 has two MinGW gaps**, closed by the force-included `occt/mingw-compat.h`, not by editing
OCCT: `<mutex>` (MSVC's `<shared_mutex>` pulls it in, libc++'s does not), and on ARM64 only
`posix_memalign` (the aligned allocator's MinGW branch is x86-only). The shim must return plain `malloc`
memory because `FreeAligned` releases with `free()` on that path; see `occt/RECIPE.md`.

**MinGW names the libraries lib-first** (`libTKernel.dll`), so the staging glob is `libTK*.dll`; the old
`TK*.dll` would have staged nothing, silently.

**winget's llvm-mingw package sets `ArchiveBinariesDependOnPath`**: it puts the real `bin` on PATH rather
than symlinks. That matters — the `<triple>-clang.exe` wrappers find clang beside their own path. A shell
opened before the install does not see it, which is why `find-toolchain.cmd` also looks in winget's
package folders, and `build-windows.ps1` re-reads the stored PATH after installing.

**Known difference, not fixed: a fault inside OCCT ends the Windows worker.** OCCT compiles most of
`OSD::SetSignal`'s Windows handling out under `__MINGW32__` (`OSD_signal.cxx`), so `OCC_CATCH_SIGNALS`
does not turn an access violation into a `Standard_Failure` there as it does on macOS and Linux (brief 61
Q9). The worker is a separate process, so circuitRF survives it; the operation in flight does not, and
the worker restarts rather than answering with a refusal. Not yet observed — nothing has run on Windows.
