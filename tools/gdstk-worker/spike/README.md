# gdstk worker — the G0 spike

The throwaway harness of `docs/sonnet-briefs/brief-oasis-gdstk.md` §3 (R-oas-0): it decides whether gdstk can be
built, shipped and trusted, and it changes no product code. The findings, the numbers and the go/no-go are in
`docs/design/oasis-gdstk-findings.md`. This file says what each piece is and which question it answers.

Nothing of gdstk's, qhull's or zlib's is in this directory or anywhere in the repository (D2). `build.sh` fetches
the pinned archives into the per-user cache, checks them against `recipe.env`, and builds there. `runs/` holds every
measurement and is git-ignored.

## Building

| What | How |
|---|---|
| a macOS or Windows RID | `tools/gdstk-worker/spike/build.sh <rid>` on a Mac: `osx-arm64`, `osx-x64`, `win-x64`, `win-x86`, `win-arm64` (Windows with llvm-mingw; `CRF_LLVM_MINGW` names its root) |
| the Linux RIDs | `docker/run-linux.sh` (Docker Desktop, `--context desktop-linux`; image `crf-gdstk-linux` from `docker/Dockerfile.linux`) |
| GdsiiDump | `dotnet build tools/gdstk-worker/spike/GdsiiDump -c Release -o ~/.circuitRF-build/gdstk/1.0.1/gdsiidump` |

The output goes to `${CRF_GDSTK_CACHE:-~/.circuitRF-build/gdstk}/1.0.1/<rid>/spike/`, which holds
`gdstk-worker[.exe]`, `gdstk-probe[.exe]` and, on Windows, `gdstk-worker-utf8.exe`. The build log is
`<rid>/build.log`; its `+ cmake` lines are Q1's exact CMake lines.

## Files

| File | What it is | Question |
|---|---|---|
| `recipe.env` | gdstk, qhull and zlib: version, URL and SHA-256 (computed by the spike, because upstream publishes none). It is read, never sourced. G1 moves it to `tools/gdstk-worker/`. | Q1 |
| `build.sh`, `CMakeLists.txt` | the fetch, verify and static build for one RID. gdstk is added as a CMake sub-project, with no patch. | Q1 |
| `gdstk_worker.cpp` | the spike's worker: §5's frames, with `hello`, `open`, `cell`, `close`, `begin-write`, `add-cell`, `finish-write`, `shutdown` and `selftest`. **G1 grows this file.** The section "Worker changes the spike forced" in the findings lists what it must keep. | all |
| `probe.cpp` | `gdstk-probe` links gdstk in-process, which the product never does, to ask it directly: `q6`, `q7gen`, `q7read`, `errptr` (read_oas with and without an error pointer), `unit` (the START unit write_oas writes) | Q5, Q6, Q7 |
| `windows/utf8.manifest`, `utf8.rc` | the manifest for `gdstk-worker-utf8.exe`: UTF-8 active code page and `longPathAware`. It is one of the candidate fixes for Windows paths. | Q3 |
| `GdsiiDump/` | drives circuitRF's own `GdsiiReader` and `GdsiiWriter` into and out of the canonical JSON. Anything the model cannot hold is listed as `lost`. It is not in `circuitrf.slnx`. | Q4, Q8 |
| `docker/Dockerfile.linux`, `run-linux.sh` | Debian 12 arm64 with a cross g++ for linux-x64, used to build the Linux RIDs | Q1 |
| `docker/Dockerfile.wine`, `run-check.sh` | `run-check.sh <rid> "<cmd>"` runs a harness command against one RID: linux-arm64 natively, and linux-x64, win-x64 and win-x86 in an amd64 Debian 12 image with Wine 8.0, under emulation. **Wine is a first pass, never the confirmation.** | Q1, Q3, Q6 |
| `windows-session/` | the owner's real-Windows session: `run-session.ps1` (pure ASCII; needs only Windows PowerShell), `README.txt` and `RESULTS-TEMPLATE.md`. `harness/windows_bundle.py` copies them into the bundle. | Q1, Q3 |

### harness/ (Python 3, standard library only)

| Script | Answers |
|---|---|
| `gdstk_client.py` | the client for the frames. It reads a library into the **canonical form** every comparison uses (integer database units, asserted and counted) and writes one back. `CRF_GDSTK_WORKER` (a command line, such as `wine …/gdstk-worker.exe`) picks the worker. `CRF_GDSTK_WINE=1` hands it paths as `Z:\…`. |
| `canon.py` | the semantic equality of §8b. `diff()` lists every difference, not only the first. |
| `oasis_hand.py` | an independent OASIS encoder, byte by byte, for the encodings gdstk's writer never emits. It writes `hand-*.oas` beside `hand-*.expected.json`. |
| `q3_fuzz.py` | Q3: malformed input. Each of three fixtures gets 50 truncations, 50 byte flips and 5 random files, read in a fresh worker with a 10 s limit and with and without the OASIS guards. Each case is classed as refused, equal, wrong-silent, wrong-messaged, crash or hang. |
| `q3_paths.py` | Q3: paths containing a space, `é`, `Ω`, CJK, and a 352-character path, read and written as both GDSII and OASIS. On Windows this check runs only in the owner's session. |
| `q4.py` | Q4: each §8a fixture is written by both writers and read by both readers. It also writes the corpus files (`*.circuitrf.gds`, `*.gdstk.gds`, `*.gdstk.oas`, `box-record.gds`). |
| `q5_gdstk.py` | Q5: each OASIS feature as gdstk's `write_oas` writes it, read back |
| `q5_hand.py` | Q5: each `hand-*.oas` read through the worker and compared with its expectation |
| `q6.py` | Q6: 8 cases of 1,000,012 values each (read and write, GDSII and OASIS, 1 nm and 0.25 nm), checked for exactness against a reader and writer written here |
| `q8.py` | Q8: the §8a corpus as `GdsiiReader` sees it, written as OASIS twice (to check determinism), validated and read back |
| `platform_check.py` | Q1/Q8: every corpus file read (canonical hash) and written back (byte hash) on one RID. `compare` diffs two RIDs. |
| `windows_bundle.py` | builds the owner's session bundle: the binaries, the corpus, **transcripts** of macOS requests with the bundle root written as `@ROOT@`, the SHA-256 of every reply and written file, and the Q3 cases with the macOS class and reply hash for each |
| `replay_session.py` | replays a bundle the way `run-session.ps1` does (steps 1, 2 and 4), so the recorded hashes are checked off macOS before the owner runs the session |
| `reply_dump.py` | decodes the replies of chosen jobs at full f64 precision, to explain a job whose hash differs on another RID |
| `corpus.py` | assembles `testdata/interchange/gdstk/` from `runs/` **once**. Tests never regenerate it. |

## Re-running

Every script takes its output directory as the first argument, and its worker from `CRF_GDSTK_WORKER` or from this
machine's spike build. The runs the findings quote:

```
python3 harness/q4.py runs/q4                         # also writes the §8a corpus
python3 harness/oasis_hand.py runs/q5 && python3 harness/q5_hand.py runs/q5 && python3 harness/q5_gdstk.py runs/q5
python3 harness/q6.py runs/q6
python3 harness/q8.py runs/q4 runs/q8
python3 harness/q3_fuzz.py runs/q3-osx-arm64
docker/run-check.sh win-x64 "python3 harness/q3_fuzz.py runs/q3-win-x64"
python3 harness/windows_bundle.py runs/windows-session $(cat runs/corpus-list.txt)
docker/run-check.sh win-x64 "python3 harness/replay_session.py runs/windows-session win-x64"
```

Q7 runs through the probe: `gdstk-probe q7gen runs/q7`, then `/usr/bin/time -l gdstk-probe q7read runs/q7/q7a.oas oas`.
