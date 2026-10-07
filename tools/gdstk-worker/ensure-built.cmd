@echo off
rem Windows counterpart of ensure-built.sh -- same contract, same guarantees.
rem
rem     ensure-built.cmd [--dest <dir>] [--rid win-x64|win-arm64|win-x86] [--strict]
rem
rem THIS SCRIPT MUST NEVER FAIL A BUILD, except under --strict (the packaging script passes it).
rem
rem IT READS THE CACHE AND NOTHING ELSE (brief-oasis-gdstk.md 4c). It never downloads, unpacks or builds
rem gdstk's dependencies, and with an empty cache it needs no C++ toolchain: it prints one warning and
rem succeeds. Only tools\gdstk-worker\build.cmd fetches anything, and only when someone runs it.
rem
rem   cache has the RID   compile the worker if its source is newer than it, check it imports only
rem                       Windows' own DLLs, stage it in build\<rid>\gdstk-kernel\, and copy that folder to
rem                       <dest>\gdstk-kernel
rem   cache does not      one warning, and success
rem
rem ONE FILE (D3): gdstk, qhull, zlib and llvm-mingw's libc++ are linked into gdstk-worker.exe statically,
rem so, unlike the geometry kernel, nothing is copied beside it.
setlocal EnableDelayedExpansion
set "here=%~dp0"
set "recipe=%here%recipe.env"
set "findtc=%here%..\geometry-worker\find-toolchain.cmd"
set "dest="
set "rid="
set "strict=0"

:args
if "%~1"=="" goto after
if /I "%~1"=="--dest" (
    set "dest=%~2"
    shift
    shift
    goto args
)
if /I "%~1"=="--rid" (
    set "rid=%~2"
    shift
    shift
    goto args
)
if /I "%~1"=="--strict" (
    set "strict=1"
    shift
    goto args
)
shift
goto args
:after

set "version="
set "srcdir="
for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%recipe%") do (
    if "%%A"=="GDSTK_VERSION" set "version=%%B"
    if "%%A"=="GDSTK_SOURCE_DIR" set "srcdir=%%B"
)
if not defined version (
    set "why=no GDSTK_VERSION in %recipe%; the gdstk worker is not built."
    goto skip
)

if not defined rid (
    set "rid=win-x64"
    if /I "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "rid=win-arm64"
    if /I "%PROCESSOR_ARCHITEW6432%"=="ARM64" set "rid=win-arm64"
    if /I "%PROCESSOR_ARCHITECTURE%"=="x86" if not defined PROCESSOR_ARCHITEW6432 set "rid=win-x86"
)
set "known="
if /I "%rid%"=="win-x64"   set "known=1"
if /I "%rid%"=="win-arm64" set "known=1"
if /I "%rid%"=="win-x86"   set "known=1"
if not defined known (
    set "why=%rid% is not built by this script; the gdstk worker is not built."
    goto skip
)

set "cacheroot=%LOCALAPPDATA%\circuitRF-build\gdstk"
if defined CRF_GDSTK_CACHE set "cacheroot=%CRF_GDSTK_CACHE%"
set "cache=%cacheroot%\%version%\%rid%"
set "gdstksrc=%cacheroot%\%version%\source\%srcdir%"

if not exist "%cache%\install.json" goto notbuilt
if not exist "%gdstksrc%\.unpacked" goto notbuilt

set "work=%here%build\%rid%"
set "stage=%work%\gdstk-kernel"
set "worker=%stage%\gdstk-worker.exe"
set "log=%work%\worker-build.log"

rem Staleness: the source, the build description, the manifest, this script, the VERSION file the worker
rem reports, and install.json (which changes when the dependencies under it are rebuilt).
set "stale=1"
if exist "%worker%" (
    set "stale=0"
    for %%F in ("%here%gdstk_worker.cpp" "%here%CMakeLists.txt" "%here%ensure-built.cmd" "%here%windows\gdstk-worker.manifest" "%here%windows\gdstk-worker.rc" "%here%..\..\VERSION" "%cache%\install.json") do (
        for /f %%R in ('powershell -NoProfile -Command ^
            "if ((Get-Item '%%~fF').LastWriteTime -gt (Get-Item '%worker%').LastWriteTime) {1} else {0}" 2^>nul') do (
            if "%%R"=="1" set "stale=1"
        )
    )
)
if "%stale%"=="0" goto publish

call "%findtc%" %rid%
if errorlevel 1 (
    set "why=find-toolchain.cmd refused %rid%; the gdstk worker is not built."
    goto skip
)
if defined crf_missing (
    set "why=gdstk's dependencies are in the cache but !crf_missing! was not found, so the worker cannot be compiled; the gdstk worker is not built. Install with: winget install -e --id !crf_winget!"
    goto skip
)

echo gdstk-worker: building the gdstk worker for %rid%
if not exist "%work%" mkdir "%work%" >nul 2>&1
rem Configured afresh every time: a tree another generator configured is refused.
if exist "%work%\cmake" rmdir /s /q "%work%\cmake"
rem Forward slashes: CMake writes these into files of its own, where a backslash is an escape.
set "depsf=%cache:\=/%/deps"
set "srcf=%gdstksrc:\=/%"
cmake -S "%here%." -B "%work%\cmake" %crf_cmake_args% "-DGDSTK_SOURCE_DIR=%srcf%" "-DCMAKE_PREFIX_PATH=%depsf%" "-DZLIB_ROOT=%depsf%" "-DQHULL_ROOT=%depsf%" -DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF -DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF >"%log%" 2>&1
if errorlevel 1 goto compilefailed
cmake --build "%work%\cmake" --target gdstk-worker >>"%log%" 2>&1
if errorlevel 1 goto compilefailed

set "built=%work%\cmake\gdstk-worker.exe"
if not exist "%built%" goto compilefailed

if exist "%stage%" rmdir /s /q "%stage%"
mkdir "%stage%" >nul 2>&1
copy /Y "%built%" "%worker%" >nul

rem D3: the worker imports Windows' own DLLs only -- KERNEL32 and the UCRT (part of Windows 10 and later).
rem A toolchain that dropped -static would leave it asking for libc++.dll, which nothing installs.
rem objdump writes to a file and findstr reads it: a for /f command that BEGINS with a quote has its first and
rem last quote stripped by cmd /c, which turned '"...\llvm-objdump.exe" -p "..." ^| findstr /C:"DLL Name:"' into
rem a path that does not exist -- "The system cannot find the path specified.", no lines, and the check passed
rem having read nothing (G1 Windows session 2, every RID). No import at all is therefore a failure: every
rem Windows program imports KERNEL32.
set "imports=%work%\imports.txt"
"%crf_llvm_mingw%\bin\llvm-objdump.exe" -p "%worker%" > "%imports%" 2>nul
set "foreign="
set "nimports=0"
for /f "tokens=3" %%N in ('findstr /C:"DLL Name:" "%imports%"') do (
    set /a nimports+=1
    echo %%N | findstr /I /B /C:"KERNEL32.dll" /C:"api-ms-win-crt-" >nul
    if errorlevel 1 set "foreign=!foreign! %%N"
)
if "!nimports!"=="0" (
    rmdir /s /q "%stage%"
    set "why=llvm-objdump read no imports from the worker (%imports%), so it could not be checked for DLLs that are not Windows' own (D3). The gdstk worker is not built."
    goto skip
)
if defined foreign (
    rmdir /s /q "%stage%"
    set "why=the worker imports DLLs that are not Windows' own (!foreign!); it must be one static file (D3). The gdstk worker is not built."
    goto skip
)
echo gdstk-worker: imports checked: !nimports! DLLs, all Windows' own

rem Answering is the proof it runs. Skipped for an architecture this machine cannot run: ARM64 Windows runs
rem all three; x64 Windows runs x64 and x86.
set "canrun=1"
if /I "%rid%"=="win-arm64" if /I not "%PROCESSOR_ARCHITECTURE%"=="ARM64" if /I not "%PROCESSOR_ARCHITEW6432%"=="ARM64" set "canrun=0"
if "%canrun%"=="1" (
    "%worker%" --version >nul 2>&1
    if errorlevel 1 (
        rmdir /s /q "%stage%"
        set "why=the worker compiled but does not run; the gdstk worker is not built."
        goto skip
    )
    for /f "delims=" %%V in ('"%worker%" --version') do echo gdstk-worker: %%V
) else (
    echo gdstk-worker: this machine cannot execute a %rid% worker, so it was not run here.
)
goto publish

:notbuilt
set "why=the gdstk worker is not built for %rid%, so OASIS and the (gdstk) GDSII route are disabled. Run tools\gdstk-worker\build.cmd once (well under a minute) to build it."
goto skip

:compilefailed
set "why=the worker did not compile (the log is %log%); the gdstk worker is not built."
goto skip

:publish
if "%dest%"=="" exit /b 0
if not exist "%worker%" exit /b 0
if exist "%dest%\gdstk-kernel" rmdir /s /q "%dest%\gdstk-kernel"
xcopy /E /I /Q /Y "%stage%" "%dest%\gdstk-kernel" >nul
exit /b 0

:skip
rem The form MSBuild reads as a WARNING, so the build summary counts it.
echo gdstk-worker : warning GD001 : !why!
if "%strict%"=="1" exit /b 1
exit /b 0
