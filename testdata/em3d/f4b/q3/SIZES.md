# Q3 — build time and shipped size, per RID

The **closure** is the set of OCCT libraries the probe (and so the worker) loads — Q2's
`closure-<rid>.txt`: 25 libraries, no third-party library. Sizes are of the real files (`libTK*.8.0.1.dylib`),
not the symlinks. "Compressed" is each format made from a folder holding only those 25 files, with the
compressor each installer uses: `hdiutil -format UDZO` (the `.dmg`, `build-macos.sh`), `zip` default level
(the updater `.zip`), `tar czf` (the Linux `.tar.gz`; a `.deb` with fpm's default gzip lands within a few
kilobytes of it), and `tar cJf` for reference. `strip -x` removes local symbols only; the stripped libraries
were verified to load and pass `selftest` (Q2's replaceability check), so brief 62 may strip.

| RID | How built | Configure | Build | Install | Uncompressed | `strip -x` | `.dmg` (UDZO) | `.zip` | `.tar.gz` | `.tar.xz` |
|---|---|---|---|---|---|---|---|---|---|---|
| osx-arm64 | native, `-j10` | 0:03 | **4:54** | 0:22 | 55,996,152 | 47,563,864 | 20,634,852 (stripped 19,550,933) | 18,179,546 | 18,201,501 | 11,871,108 |
| osx-x64 | cross from osx-arm64, `-j10` | 0:03 | **4:55** | 0:23 | 58,512,944 | 50,337,792 | 23,535,653 (stripped 21,699,435) | 19,641,118 | 19,665,666 | 13,597,384 |
| linux-x64 | *not done — owed by owner* | | | | | | — | | | |
| linux-arm64 | *not done — owed by owner* (native on an arm64 box, or cross from x64) | | | | | | — | | | |
| win-x64 | *not done — owed by owner* (MSVC, native) | | | | | | — | | | |
| win-arm64 | *not done — owed by owner* (MSVC ARM64 cross toolset from x64) | | | | | | — | | | |
| win-x86 | *not done — owed by owner* (MSVC Win32 toolset; Q13) | | | | | | — | | | |

Bytes throughout. Machine: Apple M4, 10 cores, 16 GB, macOS 27.0; nothing else ran during the two builds.

**What this adds to an installer:** about **20 MB per macOS architecture** in the `.dmg` (two `.dmg`s, one per
architecture, so each carries one closure), about **18–20 MB** in the updater `.zip`. Linux and Windows
figures are owed; expect the same order (the code is the same; ELF and PE carry different symbol overhead).

**For comparison only** (brief 61 `R-em3d61-1b`): the package manager's OCCT 7.9.3 on this machine (a Gmsh
dependency) is 67 toolkits and 55 MB, and its `TKService` links FreeType. The recipe's 25 toolkits link
nothing outside the operating system.

**The build tree is not small:** 1.5 GB per RID (object files), plus 290 MB of unpacked source and 35 MB of
installed headers. Brief 62's per-user cache keeps only the installed libraries and headers.
