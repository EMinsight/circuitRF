# Q9 — failure behaviour

Each case ran in its own process (`run.sh <occt_probe>` → `run-<rid>.txt`, verbatim stdout, stderr and exit
status). osx-arm64, OCCT 8.0.1, Release.

| case | what happened | kind |
|---|---|---|
| **Fillet radius larger than the adjacent face** (r = 500 µm on a 200 µm-thick box's top edge) | `Build()` returns; `IsDone() == false`; no exception | **`IsDone() == false`** |
| **Boolean of a self-intersecting polyhedron** (a figure-of-eight outline extruded; the input is invalid, `BRepCheck_Analyzer` says so, and its "volume" is −3e-8 µm³) | `Cut` reports `IsDone = 1`, `HasErrors = 0`, `HasWarnings = 1`, and returns **an invalid shape** (2 solids, 3 shells) | **silent garbage** — not a failure by any flag the operation raises |
| **Zero-thickness box** (`MakeBox` with dz = 0) | throws at construction: `Standard_DomainError` (empty message) | **`Standard_Failure`** |
| **Tool that misses the blank** | `IsDone = 1`, no warning; the blank comes back, valid, same volume | success |
| **A real segmentation fault**, no handler | the process dies: exit 139 (SIGSEGV) | **signal** |
| **The same fault with `OSD::SetSignal(false)` and `OCC_CATCH_SIGNALS`** | caught as `OSD_SIGSEGV: SIGSEGV 'segmentation violation' detected. Address 0.`; **the process then ran a boolean and a fillet and both were valid** | signal → exception, process usable |
| **User break on a long boolean** (a 7.2 × 7.2 mm block minus 576 cylinders, one `Cut`) | unbroken: 337 ms, `UserBreak()` polled 86,590 times. With a break at 50 ms: stopped at **50.4 ms**, `IsDone = 0`, `BOPAlgo_AlertUserBreak` set | **honoured, promptly** |
| **User break on a long fillet** (576 bore rims, r = 20 µm, one `MakeFillet`) | unbroken: 1,044 ms. With a break at 50 ms: ran to completion in 1,041 ms, `IsDone = 1`. **`UserBreak()` was polled zero times** in either run | **not honoured at all** — `BRepFilletAPI_MakeFillet::Build(range)` takes a progress range and never asks it |

## What this means for brief 63's restart rule

1. **`IsDone()` is necessary and not sufficient.** The self-intersecting case "succeeded" with an invalid
   result. The worker must run `BRepCheck_Analyzer` on **every input and every result** and refuse an invalid
   one with a sentence; it costs a millisecond on shapes this size.
2. **Construction throws.** Every primitive constructor and every operation sits inside
   `try { OCC_CATCH_SIGNALS … } catch (const Standard_Failure&)`, and a caught exception is a refusal of that
   one request, not a crash.
3. **Install `OSD::SetSignal(false)` at worker start.** A fault inside a kernel algorithm then surfaces as an
   exception and the worker survives it — but a signal handler cannot promise the heap is intact after a
   wild write. The safe rule: **after a caught signal, answer the request with a refusal, then exit and let
   the client restart the worker** (process start is 40 ms, Q12). A plain C++ exception does not need a
   restart.
4. **Cancel by kill, always.** A boolean honours a progress-indicator break within milliseconds, but a fillet
   ignores it entirely. The client's cancel is therefore *kill the worker process* (overview §1c), and the
   progress indicator is only a courtesy for booleans. Brief 63 must not promise cooperative cancellation.
