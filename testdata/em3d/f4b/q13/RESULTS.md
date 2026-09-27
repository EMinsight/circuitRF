# Q13 — Windows x86 (decides D2), and win-arm64

**Not answered — owed by the owner.** Nothing on the spike's machine can build with MSVC or run a Windows
binary (brief 61 §1).

What the owner runs, per RID (`win-x86` first, since it decides D2):

1. The recipe in `docs/design/em-3d-f4b-spike-findings.md` with `-G "Visual Studio 17 2022" -A Win32`
   (`-A ARM64` for win-arm64), then `cmake --build build --config Release -j` and `cmake --install build
   --config Release`.
2. `cmake -S testdata/em3d/f4b/harness -B probe -A Win32 -DOpenCASCADE_DIR=<install>/cmake`, build Release.
3. `occt_probe selftest` and `q9/run.sh`'s cases (run each `occt_probe q9 <case>` by hand on Windows), with
   the OCCT `bin` folder on `PATH`.
4. **x86 only:** the largest box − cylinder array that tessellates before the 32-bit address space refuses.
   The probe has no sub-command for it yet; `occt_probe q9 break-boolean` (576 cylinders, one `Cut`) is the
   first rung — if it passes, double the array side in `HoleArray` until it fails and record the last size
   that passed and the failing line verbatim.
5. Fill the table below and `q3/build-<rid>.log` in brief 61 §4's format.

| RID | builds | selftest | largest array tessellated | notes |
|---|---|---|---|---|
| win-x86 | *not done* | *not done* | *not done* | |
| win-arm64 | *not done* | *not done* | — | |

**Until this is filled, D2's recommendation for win-x86 is provisional** (findings note §D2).
