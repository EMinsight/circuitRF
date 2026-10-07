circuitRF -- gdstk worker, real-Windows confirmation session (G1: the worker that ships)
=====================================================================================

What this is: the gdstk worker's Windows programs (x64, x86, ARM64). This folder checks, on a real Windows
machine, that all three COMPILE there (the installers are built on Windows, so on an ARM64 machine x64 and x86
are cross-built), that they run, give the same answers as the Mac build, handle file names with spaces,
accented / Greek / Chinese characters and paths longer than 260 characters, and cope with damaged files.
Nothing is installed. The only thing written outside this folder is the build's dependency cache,
%LOCALAPPDATA%\circuitRF-build\gdstk -- the same one the packaging script uses.

Before you start: the build needs internet access (it downloads gdstk, qhull and zlib, checked by SHA-256) and
llvm-mingw, CMake and Ninja -- the same three tools the geometry kernel's Windows build uses. If one is missing
the build says which and prints the winget line that installs it; the rest of the session then tests the Mac
builds instead. To skip the build on purpose:  ... -File run-session.ps1 -NoBuild

1. Copy the zip to the Windows machine's own disk first (not a shared or network folder), and unzip it
   somewhere whose path has NO non-ASCII characters (for example C:\gdstk-session).
2. Open PowerShell in that folder and run:

       powershell -ExecutionPolicy Bypass -File run-session.ps1

   It takes a few minutes (the first build a minute or two more). On x64 Windows it runs win-x64 and
   win-x86; on ARM64 Windows, win-arm64, win-x64 and win-x86.
   To run one RID only:  ... -File run-session.ps1 -Rids win-x64
3. Fill in RESULTS-TEMPLATE.md (machine, and the few lines it asks for).
4. Zip the "results" folder that appears here and send it back with the template.

If Windows Defender or SmartScreen blocks gdstk-worker.exe (the binaries are not signed yet), note it in the
template; "More info -> Run anyway" or an exclusion for this folder lets the session continue.

What should happen:
  - "==== build win-..." says ok for all three RIDs, followed by a line "imports checked: N DLLs, all Windows'
    own". "The system cannot find the path specified." should no longer appear. A FAILED build is the most
    important thing to send back.
  - the long paths (366-374 characters) read and write in BOTH forms, plain and \\?\-prefixed.
  - results\ no longer keeps the long test folders, so Explorer copies it without complaint.

Differences that are already expected (they showed up under Wine too, and are explained in the findings):
  - jobs: 277 of 279 identical. The 2 that differ are the two 081-write-q5-ref-repetitions jobs: that file holds
    a repetition offset of about 2^64 (a known gdstk defect circuitRF never writes), and how a value that large
    is turned into an integer differs between processors. Any other job marked DIFFERENT is new.
  - malformed files: up to about 5 named rich-cblock-truncate-.. or rich-cblock-flip-.. (read with the checks
    off) may read, be refused or crash differently here: damaged compressed blocks whose content gdstk leaves
    undefined, so the outcome depends on memory layout, even on the folder they are in. Last session: 3-5 per
    RID. One malformed file (a damaged block size) makes the worker use a lot of memory for up to 10 seconds
    before it is stopped; that is expected. On win-x86, rich-plain-flip-48 hung last time where macOS
    crashes; both are a refusal to the application, which stops a worker that does not answer.
  - each worker's "hello" line shows its code page. 65001 (UTF-8) is what the worker is built to use.
Anything else marked DIFFERENT is new information.

Every line is written to results\summary.txt as it happens, and the whole console to results\transcript.txt,
so if the run stops part-way, send back the results folder anyway.
