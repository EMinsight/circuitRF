# Q12 — numbers brief 63 sizes itself by

**Measurements for sizing, not gates** (and never to become timing tests). Apple M4, 10 cores, 16 GB,
macOS 27.0; OCCT 8.0.1 Release, osx-arm64; the harness built Release. Median of 10.

| what | how measured | median |
|---|---|---|
| A bare process (`/usr/bin/true`), for scale | `process-start.py`'s method | 1.3 ms |
| **Process start + load of the 25 OCCT libraries** (`occt_probe noop`) | `process-start.py`, 10 runs after one warm-up (`process-start.txt`) | **39.6 ms** (38.8–40.1) |
| of which the AppKit/IOKit frameworks `TKService` links | a trivial program with and without them | ≈ 1 ms |
| Start + boolean + fillet + STEP write and read, in memory (`occt_probe selftest`) | the same | 48.1 ms |
| Box − cylinder, then a 50 µm fillet on the rim (in process) | `occt_probe q12` (`in-process.txt`) | **1.0 ms** |
| **Display tessellation** — relative deflection 0.001, 20° angular | the same | **6.0 ms** — 3,388 triangles; 1,904 per-face vertices (1,694 shared) |
| float32 xyz + uint32 index buffer for it | computed | **63,504 B** per-face / 60,984 B shared |
| **FDTD tessellation** — 1 µm absolute, 0.5 rad | the same | **1.8 ms** — 1,018 triangles; 658 per-face vertices (509 shared) |
| its buffer | computed | **20,112 B** per-face / 18,324 B shared |
| B-rep text write (v3, no triangles) | the same | 0.09 ms, 4,156 B |
| STEP write to memory (XCAF, one named, coloured part) | the same | 0.75 ms, 22,192 B |

What they say for brief 63:

- **Start the worker once per session and keep it.** 40 ms is invisible once, but it would be a visible
  hitch per operation. Start-up cost is OCCT's own library load and static initialisation, not AppKit.
- **One operation on a part this size is ~1 ms and its display mesh ~6 ms**, so a boolean preview on release
  is well under a frame even with the IPC. A 60 KB buffer per part crosses a pipe in microseconds.
- **The deflections are not interchangeable.** On this 1 mm part the display setting (relative 0.1 %, 20°) is
  *finer* than the 1 µm FDTD setting: a relative deflection scales with each edge's own size, so on the
  300 µm bore it is about 0.3 µm (a chord every ~7°) against the absolute 1 µm (~13°); neither angular limit
  binds there. A relative setting is right for display and wrong for FDTD, whose need is set by the grid,
  not by the feature. Brief 63 requests the two separately (overview §1j already says so).
- The worst cases are elsewhere: Q9's 576-hole boolean took 337 ms and its 576-edge fillet 1.04 s. Those are
  the sizes where the asynchronous preview and kill-to-cancel matter.
