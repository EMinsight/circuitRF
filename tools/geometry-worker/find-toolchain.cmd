@echo off
rem Finds what build.cmd and ensure-built.cmd compile with on Windows: llvm-mingw (clang + lld + libc++),
rem CMake and Ninja. No Visual Studio. CALLED, never run on its own -- it has no setlocal, so what it
rem sets lands in the caller's environment:
rem
rem     call find-toolchain.cmd win-x64|win-arm64|win-x86 [--report]
rem
rem   crf_missing      the tools not found, space-separated (cmake ninja llvm-mingw), or undefined
rem   crf_winget       the winget ids that install exactly those, or undefined
rem   crf_llvm_mingw   llvm-mingw's root (bin\ and <triple>\bin\ below it)
rem   crf_triple       x86_64-w64-mingw32 | aarch64-w64-mingw32 | i686-w64-mingw32
rem   crf_cmake_args   generator, build type, target system and the three compilers, quoted
rem   PATH             llvm-mingw's bin, and CMake's and winget's links folders when they are not on it
rem
rem --report also prints `missing=` and `winget=` lines, which is how build-windows.ps1 asks -- one
rem place looks for the tools, not two that could disagree.
rem
rem WHY LLVM-MINGW. One portable toolchain targets x64, ARM64 and x86 from any Windows machine, native on
rem Windows on ARM, at a tenth of Visual Studio's install. Its lld honours --export-all-symbols, which is
rem how OCCT's own MinGW build exports the C++ vtables a DLL of it needs from another; zig cc was tried
rem first and silently drops that flag (tools/geometry-worker/RESOLVED.md).
rem
rem A shell opened before winget installed a tool does not have it on PATH yet, so the known install
rem folders are looked in too. CRF_LLVM_MINGW names llvm-mingw's root explicitly. It reads this machine
rem and nothing else.
set "crf_missing="
set "crf_winget="
set "crf_llvm_mingw="
set "crf_triple="
set "crf_sysproc="
set "crf_cmake_args="
if /I "%~1"=="win-x64"   set "crf_triple=x86_64-w64-mingw32"
if /I "%~1"=="win-x64"   set "crf_sysproc=AMD64"
if /I "%~1"=="win-arm64" set "crf_triple=aarch64-w64-mingw32"
if /I "%~1"=="win-arm64" set "crf_sysproc=ARM64"
if /I "%~1"=="win-x86"   set "crf_triple=i686-w64-mingw32"
if /I "%~1"=="win-x86"   set "crf_sysproc=x86"
if not defined crf_triple (
    echo geometry-worker: ERROR: find-toolchain.cmd was given '%~1', not a Windows RID
    exit /b 1
)

where cmake >nul 2>&1
if errorlevel 1 if exist "%ProgramFiles%\CMake\bin\cmake.exe" set "PATH=%ProgramFiles%\CMake\bin;%PATH%"
where cmake >nul 2>&1
if errorlevel 1 call :missing cmake Kitware.CMake

where ninja >nul 2>&1
if errorlevel 1 if exist "%LOCALAPPDATA%\Microsoft\WinGet\Links\ninja.exe" set "PATH=%LOCALAPPDATA%\Microsoft\WinGet\Links;%PATH%"
where ninja >nul 2>&1
if errorlevel 1 call :missing ninja Ninja-build.Ninja

if defined CRF_LLVM_MINGW call :try "%CRF_LLVM_MINGW%"
if not defined crf_llvm_mingw for /f "delims=" %%C in ('where %crf_triple%-clang.exe 2^>nul') do if not defined crf_llvm_mingw call :try "%%~dpC.."
if not defined crf_llvm_mingw for /d %%P in ("%LOCALAPPDATA%\Microsoft\WinGet\Packages\MartinStorsjo.LLVM-MinGW.*") do for /d %%V in ("%%P\llvm-mingw-*") do if not defined crf_llvm_mingw call :try "%%V"
if not defined crf_llvm_mingw for /d %%P in ("%ProgramFiles%\WinGet\Packages\MartinStorsjo.LLVM-MinGW.*") do for /d %%V in ("%%P\llvm-mingw-*") do if not defined crf_llvm_mingw call :try "%%V"
if not defined crf_llvm_mingw (
    call :missing llvm-mingw MartinStorsjo.LLVM-MinGW.UCRT
    goto report
)
set "PATH=%crf_llvm_mingw%\bin;%PATH%"

rem Forward slashes: CMake writes these into files of its own, where a backslash is an escape.
set "crf_bin=%crf_llvm_mingw:\=/%/bin"
set crf_cmake_args=-G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR=%crf_sysproc% "-DCMAKE_C_COMPILER=%crf_bin%/%crf_triple%-clang.exe" "-DCMAKE_CXX_COMPILER=%crf_bin%/%crf_triple%-clang++.exe" "-DCMAKE_RC_COMPILER=%crf_bin%/%crf_triple%-windres.exe"

:report
if /I not "%~2"=="--report" exit /b 0
echo missing=%crf_missing%
echo winget=%crf_winget%
exit /b 0

rem A root counts only with the target's compiler AND the C++ runtime the worker ships beside it.
:try
if exist "%~f1\bin\%crf_triple%-clang.exe" if exist "%~f1\%crf_triple%\bin\libc++.dll" set "crf_llvm_mingw=%~f1"
exit /b 0

:missing
if defined crf_missing (set "crf_missing=%crf_missing% %~1") else (set "crf_missing=%~1")
if defined crf_winget (set "crf_winget=%crf_winget% %~2") else (set "crf_winget=%~2")
exit /b 0
