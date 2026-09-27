# Q11 — the licence checklist, verified against the built tree

Scope: the 25 toolkits the recipe builds (Q2) — their **282 packages** as each toolkit's own `PACKAGES.cmake`
lists them, **10,825 files**. (An earlier pass that derived packages from the object files missed 18
header-only or resource packages, `FlexLexer` among them; this list is from the toolkits' own manifests.)
Every file's first 40 lines were checked for OCCT's LGPL header, and every file was searched for the
markers of other licences (`Permission is hereby granted`, `Redistribution and use in source`, `Permission
to use, copy`, `GNU General Public License`, `public domain`, Apache, Mozilla, Boost).

## 1. What is linked (Q2's closure)

| | osx-arm64 | osx-x64 |
|---|---|---|
| OCCT libraries | 25 (`q2/closure-osx-arm64.txt`) | 25 (`q2/closure-osx-x64.txt`) |
| **Third-party libraries** | **none** — no FreeType, TBB, RapidJSON, Draco, FreeImage, FFmpeg, OpenVR, VTK, Tcl/Tk, Eigen, OpenGL | none |
| Operating-system libraries | `libSystem`, `libc++`, `libobjc`; frameworks AppKit, IOKit, CoreGraphics, CoreFoundation, Foundation (through `TKService`) | the same |

Linux and Windows closures are owed (§1 of the brief). Expect `libstdc++`/`libm`/`libpthread` and, with
`USE_XLIB=OFF`, **no X11** on Linux; the MSVC runtime on Windows.

## 2. Licence files in OCCT's source

| file | what it is |
|---|---|
| `LICENSE_LGPL_21.txt` | the GNU Lesser General Public License, version 2.1, February 1999 — the standard text |
| `OCCT_LGPL_EXCEPTION.txt` | *Open CASCADE exception (version 1.0) to GNU LGPL version 2.1* — object code incorporating material from OCCT's headers may be distributed "under terms of your choice, provided that you give prominent notice in supporting documentation to this code that it makes use of or is based on facilities provided by the Open CASCADE Technology software" |

Both ship in brief 62's `licenses/`, and the "prominent notice" is the About box plus `THIRD-PARTY-NOTICES.md`.

## 3. The copyright holder line for the notice

The standard header, in **10,519** of the 10,825 files (the rest are the files in §4, generated parsers, and
non-source files: each package's `FILES.cmake` manifest, `GUID.txt`, `README.md`):

> This file is part of Open CASCADE Technology software library. This library is free software; you can
> redistribute it and/or modify it under the terms of the GNU Lesser General Public License version 2.1 as
> published by the Free Software Foundation, with special exception defined in the file
> OCCT_LGPL_EXCEPTION.txt.

Copyright lines (the first in each file's header), counted: `Copyright (c) <years> Matra Datavision`
(5,740), `Copyright (c) <years> OPEN CASCADE SAS` (4,358), `Copyright (c) Open CASCADE <year>` (429). The notice line brief 62 writes:

> **Open CASCADE Technology 8.0.1 — Copyright © 1990–2026 Matra Datavision and OPEN CASCADE SAS.**
> Licensed under the GNU LGPL version 2.1 with the Open CASCADE Exception 1.0.

(The years span 1990 — e.g. `TopoDS_Compound.hxx` — to 2026 in the configured sources; OCCT's own header template, `adm/templates/header.in`,
names OPEN CASCADE SAS alone for new files.)

## 4. Files in the configured toolkits that carry a DIFFERENT licence

| file(s) | toolkit | licence | what the notice must do |
|---|---|---|---|
| `Standard/Standard_Strtod.cxx` | TKernel | David M. Gay's `dtoa`/`strtod`, © 1991, 2000, 2001 Lucent Technologies — permissive, "provided that this entire notice is included in all copies … and in all copies of the supporting documentation" | **reproduce the notice** in `THIRD-PARTY-NOTICES.md` |
| `BRepMesh/delabella.cpp`, `delabella.pxx` | TKMesh | **MIT**, © 2018 GUMIX – Marcin Sokalski | **reproduce the MIT notice** |
| `FlexLexer/FlexLexer.h` | TKernel (compiled into TKDESTEP's STEP scanner) | BSD-style, © 1993 The Regents of the University of California | **reproduce the notice** |
| `StepFile/lex.step.cxx` | TKDESTEP | flex-generated scanner — flex places no licence on its output beyond the `FlexLexer.h` notice above | none of its own |
| `StepFile/step.tab.cxx`, `step.tab.hxx` | TKDESTEP | Bison 3.7.4 output: GPL-3 **with the Bison exception** ("you may create a larger work that contains part or all of the Bison parser skeleton and distribute that work under terms of your choice"), inside OCCT's LGPL header | none — the exception applies; worth one line in the notice so a reader who finds "GPL" in the source sees why |
| `Font/Font_DejavuSans_Latin_woff.pxx` | TKService | Bitstream Vera / DejaVu font licence | **not compiled**: it is included only `#ifdef HAVE_FREETYPE`, and the recipe turns FreeType off; `strings libTKService` finds no font. Say nothing, and re-check if FreeType is ever turned on |
| **`GeomConvert/GeomConvert_CurveToAnaCurve.{cxx,hxx}`, `GeomConvert/GeomConvert_SurfToAnaSurf.{cxx,hxx}`, `Geom2dConvert/Geom2dConvert_ApproxArcsSegments.{cxx,hxx}`, `Geom2dConvert/Geom2dConvert_PPoint.{cxx,hxx}`** | **TKGeomBase** | **A proprietary header**, verbatim: *"This file is part of commercial software by OPEN CASCADE SAS, furnished in accordance with the terms and conditions of the contract and with the inclusion of this copyright notice. This file or any part thereof may not be provided or otherwise made available to any third party. No ownership title to the software is transferred hereby."* | **Decided** (below): header taken to be wrong, upstream asked, files kept out of the repo |

### The eight files with a proprietary header

They are in the public LGPL repository, in the released 8.0.1 archive, and compiled into `TKGeomBase` — a
toolkit every other toolkit depends on, so no configuration leaves them out. They entered the public
repository on 2022-05-15 (upstream commit `b47b075a`, "Interface for checking canonical geometry", OCCT 7.7)
and have been in every release since; later commits reformatted them without changing the header. The
repository as a whole is published under `LICENSE_LGPL_21.txt`, and no upstream issue mentions the header
(GitHub issue search, 2026-09-27).

The most likely reading is a header nobody updated when the code was contributed, but **the text of the
header says the opposite of the licence**, and removing the files would mean patching OCCT, which
`R-em3d61-1c` forbids and which would change the licence obligation anyway. This is a finding for the owner,
not a thing the spike can settle. Options, cheapest first:

1. **Ask upstream** — an issue on the OCCT repository asking that the eight headers be brought in line with
   `LICENSE_LGPL_21.txt`. Their answer (or a fixed 8.0.x) settles it; the recipe would then pin that
   release.
2. **Ship on the repository licence** and record the discrepancy in `THIRD-PARTY-NOTICES.md` — the files
   were published by their copyright holder in a repository they license under LGPL-2.1.
3. **Do not ship until 1 is answered.**

**Decided (owner, 2026-09-27):** the header text is taken to be incorrect; upstream was asked the same day —
[OCCT#1564](https://github.com/Open-Cascade-SAS/OCCT/issues/1564). Until it is answered, none of OCCT's files
(these eight included) is put in the circuitRF repository; building circuitRF from source means fetching
OCCT's source oneself with the recipe.

## 5. Checklist for brief 62's `THIRD-PARTY-NOTICES.md` entry

- [ ] OCCT 8.0.1, the copyright line in §3, LGPL-2.1 + Open CASCADE Exception 1.0; both texts in `licenses/`.
- [ ] The corresponding source: a **written offer** (owner, 2026-09-27; brief 62 `R-em3d62-6c` has the text),
      honoured from a retained copy of the exact archive (Q1's SHA-256) kept outside the repository; the
      recipe (Q2) says how it was built. No archive is published while OCCT#1564 is open.
- [ ] The relinking right: the libraries ship as separate, replaceable shared libraries — verified on
      osx-arm64 by swapping in a stripped copy of all 25 and passing `selftest` (`q3/SIZES.md`).
- [ ] Reproduced notices: David M. Gay / Lucent (`Standard_Strtod`), MIT / GUMIX (`delabella`), BSD / UC
      Regents (`FlexLexer.h`); one line on Bison's exception.
- [x] The owner's interim decision on §4's eight files (above); [ ] upstream's answer on OCCT#1564.
- [ ] Linux and Windows closures confirmed third-party-free (owed).
