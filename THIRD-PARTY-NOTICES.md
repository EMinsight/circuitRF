# Third-party notices

circuitRF's own source code is released under the MIT License (see [`LICENSE`](LICENSE)). That grant
covers the code in this repository and nothing else. The distribution — and the installers built from
it — also contains third-party components under their own terms, listed here.

**Three of these are copyleft**, two weakly and one at file scope. None restricts circuitRF's own
MIT licensing, but each carries obligations that travel with any binary you redistribute, so they are
listed first and in full.

---

## 1. CSparse.NET — LGPL-2.1-only

| | |
|---|---|
| **Component** | CSparse.NET 4.3.0 |
| **Copyright** | Christian Woltering © 2012–2025 |
| **Licence** | GNU Lesser General Public License, version 2.1 **only** (not "or later") |
| **Licence text** | [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt) |
| **Source** | https://github.com/wo80/CSparse.NET |
| **Used by** | `src/Engine` — sparse complex LU for MNA, harmonic balance, S-parameters, and the AIM accelerator |

### What this means if you redistribute a circuitRF binary

circuitRF's packaged installers are built with `SelfContained` and `PublishSingleFile`, so
`CSparse.dll` is bundled into the published host rather than sitting beside it as a separate,
replaceable file. LGPL-2.1 §6 requires that whoever receives such a combined work be able to modify
CSparse.NET and relink it into a working program.

**That requirement is satisfied here by publication of complete source.** Everything needed to
substitute a modified CSparse.NET and rebuild circuitRF is in this repository: change the
`PackageReference` in `src/Engine/CircuitRF.Engine.csproj` — or drop in a modified assembly — and run
the build described in [`BUILDING.md`](BUILDING.md). No part of circuitRF is withheld, obfuscated, or
distributed in a form that would prevent relinking.

If you redistribute circuitRF binaries yourself, you inherit that obligation: ship this notice, ship
the LGPL text, and either accompany the binaries with the source or point recipients at it.

CSparse.NET is used through a narrow interface (five files: `MnaSystem`, `NonlinearDcEngine`,
`SParameterEngine`, `HbLinearExtractor`, `PlanarAim`), so a distribution that cannot accept an LGPL
component can replace it without touching the rest of the engine.

---

## 2. Open CASCADE Technology — LGPL-2.1-only, with the Open CASCADE Exception 1.0

| | |
|---|---|
| **Component** | Open CASCADE Technology (OCCT) 8.0.1 — 25 of its toolkits, as shared libraries |
| **Copyright** | © 1990–2026 Matra Datavision and OPEN CASCADE SAS |
| **Licence** | GNU Lesser General Public License, version 2.1 **only**, with the **Open CASCADE Exception 1.0** |
| **Licence text** | [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt) and [`licenses/OCCT-exception-1.0.txt`](licenses/OCCT-exception-1.0.txt) |
| **Source** | https://github.com/Open-Cascade-SAS/OCCT — tag `V8_0_1`; the build is described in [`tools/geometry-worker/occt/RECIPE.md`](tools/geometry-worker/occt/RECIPE.md) |
| **Used by** | `tools/geometry-worker` — the geometry kernel (booleans, fillets and chamfers, STEP import and export), a separate program circuitRF starts |

**circuitRF uses facilities provided by Open CASCADE Technology.** That sentence is the prominent notice
the Open CASCADE Exception asks for: the geometry worker's object code includes material from OCCT's
header files, which the exception lets circuitRF distribute on terms of its own choosing — the MIT
License — provided this notice is given. It is also shown in circuitRF's **Acknowledgments** dialog
(About ▸ Acknowledgments…).

### What this means if you redistribute a circuitRF binary

Every circuitRF installer that includes the geometry kernel carries OCCT's libraries **unmodified**
and **dynamically linked**, in one folder beside the application — `geometry-kernel/` (inside the
`.app`, `Contents/MacOS/geometry-kernel/`). No managed part of circuitRF references them; only the worker
program in that folder loads them. LGPL-2.1 §6 requires that whoever receives the combined work be able
to substitute a modified OCCT: replace the libraries in that folder with a build of your own from the
same version (the recipe says how the shipped ones were built) and the worker loads yours. On macOS,
replacing a signed library breaks the application's signature; re-sign it with your own identity.
LGPL-2.1 asks for nothing more there — it has no "installation information" clause.

**The corresponding source is available under this written offer** (LGPL-2.1 §6(c)):

> *circuitRF binaries include Open CASCADE Technology 8.0.1, unmodified, as shared libraries. For at
> least three years after the last circuitRF release that includes this version, the circuitRF project will
> give anyone who received such a binary a complete machine-readable copy of the corresponding source code
> of Open CASCADE Technology 8.0.1, for a charge no more than the cost of providing it. To request it,
> open an issue on the circuitRF project's issue tracker titled "OCCT source request".*

As a convenience, and not as the fulfilment of that offer: the source is upstream's tag `V8_0_1`,
whose archive (`https://github.com/Open-Cascade-SAS/OCCT/archive/refs/tags/V8_0_1.tar.gz`) has SHA-256
`0d6913eae4bcc09a3653ceced6dda1aec11c35a1513d4c06762c9b002092c68a`. circuitRF's own repository contains
no OCCT source; building circuitRF from source fetches it with the recipe.

If you redistribute circuitRF binaries yourself, you inherit these obligations: ship this notice and both
licence texts, keep the libraries replaceable, and make the corresponding source available yourself —
accompanying the binaries, or by a written offer of your own.

*Why an offer rather than a published archive.* Eight files compiled into OCCT's `TKGeomBase` toolkit
carry a header that contradicts the licence under which OCCT publishes them; upstream has been asked to
correct it ([OCCT#1564](https://github.com/Open-Cascade-SAS/OCCT/issues/1564)). Until that is answered,
circuitRF republishes none of OCCT's source files and provides the source on request.

### Notices OCCT carries for code of other origin

These files are compiled into the shipped libraries and carry licences of their own, reproduced here
as each requires.

**`strtod`** (in `TKernel`, adapted by OCCT from netlib):

> The author of this software is David M. Gay.
>
> Copyright (c) 1991, 2000, 2001 by Lucent Technologies.
>
> Permission to use, copy, modify, and distribute this software for any purpose without fee is hereby
> granted, provided that this entire notice is included in all copies of any software which is or
> includes a copy or modification of this software and in all copies of the supporting documentation
> for such software.
>
> THIS SOFTWARE IS BEING PROVIDED "AS IS", WITHOUT ANY EXPRESS OR IMPLIED WARRANTY. IN PARTICULAR,
> NEITHER THE AUTHOR NOR LUCENT MAKES ANY REPRESENTATION OR WARRANTY OF ANY KIND CONCERNING THE
> MERCHANTABILITY OF THIS SOFTWARE OR ITS FITNESS FOR ANY PARTICULAR PURPOSE.

**DELABELLA** (the Delaunay triangulator in `TKMesh`) — MIT License:

> DELABELLA - Delaunay triangulation library
> Copyright (C) 2018 GUMIX - Marcin Sokalski
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
> and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
> LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN
> NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
> WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
> SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

**`FlexLexer.h`** (the interface of the STEP reader's scanner, in `TKDESTEP`):

> Copyright (c) 1993 The Regents of the University of California. All rights reserved.
>
> This code is derived from software contributed to Berkeley by Kent Williams and Tom Epperly.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted
> provided that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this list of conditions and
>    the following disclaimer.
> 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions
>    and the following disclaimer in the documentation and/or other materials provided with the
>    distribution.
>
> Neither the name of the University nor the names of its contributors may be used to endorse or
> promote products derived from this software without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED ``AS IS'' AND WITHOUT ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, WITHOUT
> LIMITATION, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE.

The STEP reader's parser (`step.tab.cxx`, in `TKDESTEP`) was generated by Bison. Its skeleton is GPL-3
**with the Bison exception**, which permits distributing a larger work containing the parser skeleton
under terms of one's choice; that is why a reader of OCCT's source finds "GPL" there, and it places no
obligation on circuitRF or on anyone redistributing it.

---

## 3. OSDI header — MPL-2.0

| | |
|---|---|
| **Component** | `osdi.h`, the OSDI ABI header from the ngspice OSDI component |
| **Copyright** | © 2022 SemiMod GmbH |
| **Licence** | Mozilla Public License 2.0 |
| **Licence text** | [`licenses/MPL-2.0.txt`](licenses/MPL-2.0.txt) |
| **In this repo** | [`tools/osdi-worker/osdi.h`](tools/osdi-worker/osdi.h) — vendored verbatim |

MPL-2.0 is copyleft at **file** scope. The file may sit inside an MIT-licensed project, which is
exactly what MPL §3.3 contemplates, but the file itself remains under the MPL and **may not be
relicensed under MIT**. Its header comment is part of the licence and must not be removed or altered.
Modifications to that file, if any are ever made, stay under the MPL and their source must be made
available.

Nothing else in `tools/osdi-worker/` is derived from ngspice: `osdi_worker.c` is first-party code
written against the published ABI this header describes.

---

## 4. Permissively licensed components

None of these impose obligations beyond retaining their notices.

### Libraries

| Component | Licence | Project |
|---|---|---|
| Avalonia (`Avalonia`, `.Desktop`, `.Skia`, `.Themes.Fluent`, `.Controls.ColorPicker`, `.Fonts.Inter`, `.Headless`) | MIT | https://avaloniaui.net/ |
| SkiaSharp (+ `NativeAssets.Win32`) | MIT | https://github.com/mono/SkiaSharp |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Dock.Avalonia (+ `Model.Mvvm`, `Themes.Fluent`) | MIT | https://github.com/wieslawsoltes/Dock |
| Material.Icons.Avalonia | MIT | https://github.com/SKProCH/Material.Icons |
| Vortice.Direct3D11, `.DXGI`, `.D3DCompiler`, `.DirectX`, `.Mathematics`, Vortice.Vulkan — the 3D view's managed Windows and Linux GPU bindings (no native code; the OS supplies the drivers) | MIT | https://github.com/amerkoleci/Vortice.Windows |
| SharpGen.Runtime, `.COM` (pulled in by Vortice) | MIT | https://github.com/SharpGenTools/SharpGenTools |
| NumFlat | MIT | https://github.com/sinshu/numflat |
| FftFlat | MIT | https://github.com/sinshu/FftFlat |
| PureHDF | MIT | https://github.com/Apollo3zehn/PureHDF |
| Svg.Skia | MIT | https://github.com/wieslawsoltes/Svg.Skia |
| Clipper2 | Boost Software License 1.0 | https://github.com/AngusJohnson/Clipper2 |
| libc++ and libunwind (`libc++.dll`, `libunwind.dll`) — the C++ runtime the Windows geometry worker and its OCCT libraries share, from llvm-mingw; licence text [`licenses/Apache-2.0-with-LLVM-exceptions.txt`](licenses/Apache-2.0-with-LLVM-exceptions.txt) | Apache-2.0 WITH LLVM-exception | https://github.com/mstorsjo/llvm-mingw |
| Markdig | BSD-2-Clause | https://github.com/xoofx/markdig |
| Svg (svg-net) | Microsoft Public License (MS-PL) | https://github.com/svg-net/SVG |
| xunit, Microsoft.NET.Test.Sdk, coverlet.collector | MIT / Apache-2.0 | *(test-time only; not shipped)* |

`naga` (MIT OR Apache-2.0, https://github.com/gfx-rs/wgpu) is used by `tools/ShaderGen` at build time
only, to cross-compile the 3D view's one WGSL shader to MSL, HLSL and SPIR-V; the generated files
are committed and naga itself does not ship.

`Svg` (MS-PL) and `Svg.Skia` are used by `tools/IconGen`, which rasterises the committed brand SVGs
into the `.icns`/`.ico`/`.png` containers at packaging time. `IconGen` is not part of
`circuitRF.slnx` and neither package ships inside the application.

### Fonts

| Font | Licence | Licence text in repo |
|---|---|---|
| IBM Plex Sans — © 2017 IBM Corp., reserved font name "Plex" | SIL Open Font License 1.1 | [`src/Ui/Assets/Fonts/IBM_Plex_Sans/OFL.txt`](src/Ui/Assets/Fonts/IBM_Plex_Sans/OFL.txt), [`docs/user/assets/fonts/OFL.txt`](docs/user/assets/fonts/OFL.txt) |
| Inter — © The Inter Project Authors | SIL Open Font License 1.1 | [`docs/user/assets/fonts/OFL.txt`](docs/user/assets/fonts/OFL.txt) |
| DejaVu Sans — derived from Bitstream Vera, © 2003 Bitstream Inc.; Arev glyphs © Tavmjong Bah | Bitstream Vera Fonts License | [`src/Ui/Assets/Fonts/DejaVu Fonts License.txt`](src/Ui/Assets/Fonts/DejaVu%20Fonts%20License.txt), [`docs/user/assets/fonts/DejaVu Fonts License.txt`](docs/user/assets/fonts/DejaVu%20Fonts%20License.txt) |

Both the OFL and the Bitstream Vera licence carry a **reserved font name** clause: a modified version
of any of these faces must be distributed under a different name. circuitRF embeds them unmodified.

---

## 5. Build-time downloads (not redistributed)

`tools/macos-vmimage/build-image.sh` downloads pinned Alpine Linux and Ubuntu base images to
construct the Linux guest that runs Linux-only device workers on macOS. Those images are fetched by
checksum at build time on the user's own machine and are **not** contained in this repository or in
any circuitRF installer, so no redistribution obligation attaches to their contents. The pinned
versions and hashes are in [`tools/macos-vmimage/sources.lock`](tools/macos-vmimage/sources.lock).

The same is true of the cross-compiler images `tools/senior-worker/build.sh` and
`tools/netlist-worker/Dockerfile` pull on demand.

---

## 6. Test data

`tests/RfCore.Tests/testdata/2SC5226A.s2p` is manufacturer-published small-signal S-parameter data
for a commercially available transistor, used as a Touchstone parser fixture.

The loadpull and contour fixtures under `testdata/spl_test_data/` and `testdata/lpwave_test_data/`
are third-party measured data that **cannot be redistributed** and have never been committed to this
repository. Tests that read them report as *Skipped* with a reason; a fresh clone is fully green
without them.

---

*If you believe a component is listed incorrectly or is missing, please open an issue — corrections
to this file are always in scope.*
