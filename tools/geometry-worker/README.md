# geometry-worker

circuitRF's geometry kernel: **Open CASCADE Technology** (OCCT) behind a small program that circuitRF
starts and speaks to over stdin/stdout. Booleans, fillets and chamfers, and STEP import and export run
here — never in circuitRF's own process. Brief 62 (`docs/sonnet-briefs/brief-em3d-62-geometry-worker-and-shipping.md`)
built the program and its shipping; brief 63 built the client, discovery and the protocol proper.

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

## The protocol (brief 63)

**Frames**, one per request on stdin and one per reply on stdout; diagnostics on stderr:

```
[uint32 jsonLen][uint32 binLen][jsonLen bytes of UTF-8 JSON][binLen bytes]        little-endian
```

The device worker's layout on purpose, so a hex dump of either reads the same way — except that the binary
part is **bytes**, declared by the JSON's `blobs` array in order, because a reply carries doubles, 32-bit
integers and opaque B-rep/STEP bytes: `"blobs":[{"name":"vertices","type":"f64","count":3012}, …]`, with
`type` one of `f64`, `u32`, `bytes`. The worker flushes after every reply and reads a request until all of it
has arrived. The managed half is `src/Design/ThreeD/Occ/` (`GeometryKernel` and its codec,
`GeometryKernelFrame`); nothing else in circuitRF starts this program.

**A refusal is an ordinary reply**, and the worker keeps running:
`{"ok":false,"code":"build.failed","object":"lid","detail":"<OCCT's own text>"}`. The client words `code` in
circuitRF's voice and appends `detail` verbatim; an unknown `code` is reported as unrecognised. A fillet's or
chamfer's refusal also says which edges (brief 67): `edges` with `width_um` (the one that does not fit, and the
narrower of the two faces beside it), or `edges` with `"corner":true` (those meeting where the kernel cannot blend);
and `edge.missing` — a listed name that resolves to nothing — carries `edges` (that name) and `missing` (its faces
the target no longer has).

| request | in | out |
|---|---|---|
| `hello` | `protocol` | `worker`, `occt` (the version it LOADED), `protocol`, `rid` (the RID it was COMPILED for), `modules`, `test_ops` |
| `build` | `shape` (the handle to hold it under), `tree` (below), `options` (`fuzzy`, `keepTools`) | `shape`, `object`, `valid`, `solids`, `faces`, `volume_um3`, `notes[]`; blob `brep` — format version 1, no triangles, written straight from the result |
| `tessellate` | `shape`, `linear_um`, `angular_rad` | blobs `vertices` f64 (3 per node), `tris` u32 (3 per triangle), `face` u32 (per triangle, an index into `faces`' list) |
| `faces` | `shape` | per face, in `TopExp::MapShapes` order: `name`, `kind` (`plane`, `cylinder`, `cone`, `sphere`, `torus`, `bspline`, `other`), `box` (tight, 6 numbers), `area`, `min_radius` (0 for a plane), `centroid`, `normal` (outward, at the face's point nearest its centroid; zeros where undefined) — the fingerprint Reload from Source matches by (brief 68) |
| `edges` | `shape`, `deflection_um` | per feature edge, sorted by name: `name`, `faces` (its two), `kind`, `length`, `min_radius` (0 for a line), `points`, and (brief 67) `closed`, `ends` (6 numbers, the polyline's way), `tangents` (the unit tangents there, 6 numbers), `mid` (halfway along the curve; absent when closed), `centre` and `radius` (a circle or an arc); blob `polylines` f64 |
| `export` | `shapes[]`, `format` (`brep`, `step`, `ply`, `stl`), `units` (`um`, `mm`, `mil`, `in`, `m`), `names[]`, `colours[]`, `linear_um`, `schema` (`ap242`, else AP214); STEP only: `assembly` (true: one assembly whose components are the shapes; a handle listed twice is one part instanced twice) and `locations[]` (per shape, 12 numbers, µm, or null) | blob `data` |
| `import-step` | `shape` (a handle prefix), blob `file` or `path`, `hold` (false: hold nothing), `display_rel` (count display triangles at that fraction of each part's diagonal) | `units[]` and `unit_um[]` (the file's length units, each resolved exactly as the transfer resolves it — an unresolvable one, or none, is the refusal `import.units`), `pmi` (dimensions, tolerances and datums, none imported), `parts[]` (`shape` = prefix`/n` or empty, `name`, `path` — the occurrence, composed through every assembly level, `1/2` — `colour` or null, `solids`, `faces`, `valid`, `closed`, `why`, `healing`, `triangles`), `healing[]` |
| `release` | `shapes[]` | `released`, `held` |
| `shutdown` (or `quit`) | — | `ok`, then exit 0 |
| `box`, `selftest` | brief 62's skeleton checks, kept for `tools/CliSmoke` | |

**Units are micrometres** throughout — OCCT's absolute tolerances sit nine orders of magnitude below a 25 µm
feature in µm, and two in metres (the reason `GmshGeoWriter` writes µm too).

### The tree

A `build`'s tree is **resolved numbers only** — no expression, no unit, no variable; the worker never sees a
`.c3d` (`GeometryKernelTree` writes it, canonically, so the same tree is the same bytes and hash everywhere):

```json
{"tree":1,"root":{"kind":"box","name":"lid","faces":["xmin","xmax","ymin","ymax","zmin","zmax"],
                  "transform":[m00,m01,m02,tx, m10,m11,m12,ty, m20,m21,m22,tz],"min":[x,y,z],"size":[x,y,z]}}
```

Each primitive is built **in its own frame**, its faces named, and then carried by `transform` (a rigid
3 × 4 matrix, rows; a mirror is allowed). Brief 64 adds operation nodes whose operands are nodes like these.

### Face names

Every node lists its face names in the primitive's own order — `C3dObject.FaceNames()` — and the worker
attaches each to the OCCT face that IS that face, by geometry, never by OCCT's list order:

| kind | fields | names, in order | which OCCT face |
|---|---|---|---|
| `box` | `min`, `size` | `xmin xmax ymin ymax zmin zmax` | the face's plane: its normal picks the axis, its side of the middle picks min or max |
| `cylinder` | `base`, `axis`, `length` (negative runs backwards), `radius` | `bottom top side` | `side` is the cylindrical face; `bottom` is the cap at `base` |
| `prism` | `outline`, `holes` (points in the object's frame), `extrude` (one vector: height along the plane's normal plus the shear) | `bottom top side0… hole0.side0…` | `bottom` is the outline's face, `top` its translate; side *k* is the face `BRepPrimAPI_MakePrism` generated from outline edge *k* → *k*+1 |
| `polyhedron` | `vertices`, `loops` (`outer`, `holes`: vertex indices) | one per loop | the face made from that loop, followed through sewing |

**Edges** are named by the two faces they separate, sorted and joined by `|` (`xmax|zmax`); where one pair
bounds more than one edge a third field numbers them in geometric order (`side|zmax|2`). A seam (a face meeting
itself, as a cylinder's side does) and a degenerate edge are not feature edges and are not listed.

**The geometric order** (brief 64 §2d) numbers split pieces and repeated edges by CENTROID in the root object's
own frame — before its `transform`, so moving or rotating the object renumbers nothing — compared x, then y, then
z, each rounded to 1 nm.

### Operation nodes (brief 64)

An operation lists no `faces`: it names its result from its operands' through OCCT's history (`Modified`,
`IsDeleted`, and `Generated` for a fillet's or chamfer's new faces). Its operands are built in its own frame, then
the result is carried by its `transform`.

| kind | fields | the result's face names |
|---|---|---|
| `boolean` | `op` (`subtract`, `unite`, `intersect`), `blank` (a node whose `name` is `""`), `tools` (named nodes) | the blank's bare; a tool's `<tool>:<face>` (a nested tool's `<tool>:<inner>:<face>`) |
| `fillet` | `radius`, `edges` (names, as above), `target` | the target's; each rounded edge's new face `fillet(<edge>)` |
| `chamfer` | `distance`, optional `distance2` (on the edge's second face), `edges`, `target` | the target's; `chamfer(<edge>)` |
| `step` | `file` (a path), `hash`, `part` (the occurrence path `import-step` reports, `1/2`) | `face<n>` in the part's own order, as `import-step` names them; a part that is not a closed solid is refused, and healing is a build note |

A fillet's or chamfer's `edges` are resolved on its target numbered in the TARGET's own frame — the frame `edges`
numbers it in when it is a root. A name resolves to the edge of that name; failing that, a two-field name resolves
to EVERY edge between the pieces of its two faces — `zmax#k` and a managed fold's `zmax.k` alike — so `xmax|zmax`
follows a split `zmax` onto both new edges (brief 67). A numbered name, or one naming a piece, resolves exactly or
not at all. When the operation fails the worker builds each listed name alone, to say which does not fit, and only
then the corner; that costs nothing unless it fails.

A face of the result keeps the first name that claims it, the blank's before a tool's. A name held by more than
one face of the result — a split face, including one an operand had already split — is numbered `#1…#n` in the
geometric order, so a reference to `zmax` means every `zmax#k`. A fillet's and chamfer's faces are named from
EVERY edge of each contour OCCT built (`Edge(contour, i)`), not only the edges listed: a tangent chain
propagates. An empty result is refused as `build.empty` (brief 66: the editor words it in the operation's own terms —
*"'lid' and 'pin' share nothing"*), and a fillet OCCT cannot build as `build.failed`, each naming the object. A
`step` node reads its file once per (path, hash) for the life of the worker.

### Test nodes

With `CRF_GEOMETRY_WORKER_TEST=1` in its environment the worker also builds a node of kind `crash` (it exits at
once with status 70, answering nothing) and `sleep` (`seconds`, then builds its `then` node) — what the client's
crash and cancellation gates are tested against. Without it both are refused like any unknown kind. They are
nodes of a `build`, so the client's cache, failed-tree record and restart are what those gates exercise.
With the same switch, `CRF_GEOMETRY_WORKER_TEST_IMPORT_SECONDS=<s>` makes every `import-step` wait that long before
reading — what cancelling the Import STEP dialog mid-read is tested against (brief 68). Without the switch it is
ignored.

`geometry-worker --version` prints `geometry-worker <VERSION>` and `occt <version>` and exits 0, so a
person and `tools/CliSmoke` can ask without speaking the protocol.

**The OCCT it loaded, not the one it was built against.** The worker compares the version compiled into
it (`OCC_VERSION_COMPLETE`) with the loaded `TKernel`'s own (`OCCT_Version_String_Complete()`). On a
mismatch `--version` says so and exits 1, and every request but `shutdown` is refused (`kernel.mismatch`)
with a sentence naming both — never a silent run against the wrong library.

Nothing but a frame ever reaches stdout: at start-up the worker keeps a private copy of the stdout
descriptor for the protocol and points descriptor 1 at stderr, so anything OCCT prints lands there.

## Failure

Written from brief 61 Q9 (`docs/design/em-3d-f4b-spike-findings.md`, "Crash posture"):

- **A C++ exception** — `Standard_Failure` (which derives from `std::exception` in OCCT 8.0) or any
  `std::exception` — is caught at the request boundary, answered with `ok:false`, and the worker carries on.
- **A fault inside the kernel** is turned into an exception: the worker installs `OSD::SetSignal(false)`
  and wraps each request in `OCC_CATCH_SIGNALS`, which is what Q9 found to work — after a caught SIGSEGV
  the spike's process ran a boolean and a fillet correctly. **The worker still exits after one** (status 3,
  after answering the request with the refusal `kernel.fault`): a handler cannot vouch for a heap after a wild write, and a
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
