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
