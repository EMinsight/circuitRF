# osdi-worker — resolved findings

## No Verilog-A model had ever loaded on Linux (GitHub issue #5, 2026-09-29)

A Linux user reported that every Verilog-A model — a `.va` compiled by circuitRF and a `.osdi` they
compiled themselves — failed with `osdi-worker: dlopen failed: <model>.osdi: undefined symbol: log`.
Two independent defects, both Linux-only, both invisible on the macOS machine everything had been
verified on. The second sat directly behind the first, so fixing only the reported one would have
turned a clear refusal into a crash on the very next request.

**1. libm was never in the worker.** OpenVAF's output calls `exp`/`log`/`log10`/… as undefined
symbols and lists only `libc.so.6` as NEEDED — it relies on the host already having libm in its
global scope, as a simulator linked against it does. `build.sh` passes `-lm`, but the worker uses no
libm function (`isfinite` is a macro), so an as-needed link drops it: the zig-built release workers
list `libc.so.6 libdl.so.2` and nothing else. A plain Debian `gcc` link does NOT use as-needed and
keeps libm, which is why a quick container build can make the bug look absent — link with
`-Wl,--as-needed` to see it. Fix: `dl_open` loads `libm.so.6` with `RTLD_GLOBAL` once before the
first model, so the outcome no longer depends on how the worker was linked.

**2. `OSDI_DESCRIPTOR_SIZE` was read as a `size_t`.** The compiler exports it as a 4-byte `uint32_t`
(`readelf --dyn-syms` on an OpenVAF-built model: size 4, beside the other uint32 exports), and the
test model already declared it so. The worker read 8 bytes. On macOS the neighbouring 4 bytes
happened to be zero; on Linux they were not, the descriptor stride became enormous, and `describe`
segfaulted (SIGSEGV, empty stderr — "the worker closed its output"). AddressSanitizer names it
immediately as a global-buffer-overflow at the read.

**How it was verified without a Linux machine.** Docker Desktop's `gcc:13` image, both
architectures (`--platform linux/amd64` runs under emulation): `verify.py` end to end against the
zig-built release worker, the same under ASan, and a real OpenVAF model using `exp`/`log`. The macOS
`openvaf-r` cannot LINK a Linux target (no linker for it) but leaves its `.o` files behind after the
failure; `gcc -shared` over those in the container gives a genuine Linux `.osdi`. The pre-fix worker
reproduces the reported `undefined symbol` message exactly against it.

**Not covered by any automated test.** The repository's tests run on macOS only, where neither
defect can appear. A Linux CI leg running `verify.py` against an as-needed-linked worker would hold
both shut.

## Any `$warning` or `$fatal` in a model killed the worker (brief-modelcomposer-26, 2026-10-21)

A compiled model sends every display task (`$display`, `$strobe`, `$warning`, `$error`, `$fatal`) through one function
pointer, `osdi_log`. The library exports the pointer and the HOST must fill it in. The worker never did, so the first
message a model printed called through null. The worker then died with an empty stderr, which circuitRF reports as a
worker that closed its output. Models whose own text never prints anything were unaffected, which is why nothing caught
it. The Verilog-A export writes a `$warning` for a model used outside its temperature and a `$fatal` for a constituent
that is not finite, and both crashed the worker.

**Fix.** `load_library` installs `crf_osdi_log` when the library exports the pointer. It writes
`osdi-worker: model <level>: <message>` to stderr. circuitRF already attaches stderr to a refused point, so a `$fatal`
reaches the user as the model's own sentence. Three details:

- **Each distinct message is written once.** The worker evaluates every point as a DC operating point, so a model's
  `@(initial_step)` block runs on every evaluation. A harmonic-balance solve is thousands of evaluations, and writing
  each one would fill the pipe and bury the line that matters. A `$fatal` is always written, because its point is
  refused.
- **The message is the compiler's `malloc`'d string, and the worker frees it**, except under `LOG_FMT_ERR`, where the
  compiler passes the format string itself because formatting failed.
- **The level is `lvl & 7`.** `osdi.h`'s `LOG_LVL_MASK` is 8, which masks nothing, and the header is the ABI contract and
  is not edited.

`EVAL_RET_FLAG_FATAL` was already read: the point's status is 0 and its block is left empty. Gate:
`tests/Ui.Tests/ModelComposer/VerilogA/CompiledVerilogATests.ANonFiniteConstituent_IsRefusedNamingItAndThePoint`.
