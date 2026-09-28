@echo off
rem Windows counterpart of build.sh: OpenCASCADE into the per-user cache (if it lacks it), then the worker.
rem
rem     tools\geometry-worker\build.cmd                    this machine's own RID
rem     tools\geometry-worker\build.cmd --rid win-arm64    another Windows RID (llvm-mingw targets all three)
rem     tools\geometry-worker\build.cmd --arch x64         the same, by architecture
rem     tools\geometry-worker\build.cmd --strict           fail rather than warn (the packaging script)
rem
rem THIS IS THE ONLY THING ON WINDOWS THAT FETCHES OCCT, and it is only ever run deliberately
rem (brief-em3d-62 R-em3d62-3f). `dotnet build` runs ensure-built.cmd, which reads the cache and nothing
rem else. Nothing of OCCT's is written into the repository: archive, source, build tree and install all
rem live in the cache,
rem
rem     %LOCALAPPDATA%\circuitRF-build\occt\<version>\          the verified archive and the source
rem     %LOCALAPPDATA%\circuitRF-build\occt\<version>\<rid>\    build\, install\, install.json (LAST)
rem
rem or under CRF_OCCT_CACHE. Needs llvm-mingw, CMake and Ninja -- no Visual Studio; find-toolchain.cmd
rem finds them and says which winget ids install what is missing. llvm-mingw targets all three RIDs from
rem any Windows machine. curl.exe, tar.exe and certutil are part of Windows.
setlocal EnableDelayedExpansion
set "here=%~dp0"
set "recipe=%here%occt\recipe.env"
set "rid="
set "arch="
set "strict="
set "dest="

:args
if "%~1"=="" goto after
if /I "%~1"=="--rid" (
    set "rid=%~2"
    shift
    shift
    goto args
)
if /I "%~1"=="--arch" (
    set "arch=%~2"
    shift
    shift
    goto args
)
if /I "%~1"=="--dest" (
    set "dest=%~2"
    shift
    shift
    goto args
)
if /I "%~1"=="--strict" (
    set "strict=--strict"
    shift
    goto args
)
echo geometry-worker: ERROR: unknown argument %1
exit /b 1
:after

rem The recipe is READ, never `call`ed: its values hold spaces and semicolons.
for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%recipe%") do set "%%A=%%B"
if not defined OCCT_VERSION (
    echo geometry-worker: ERROR: %recipe% names no OCCT_VERSION
    exit /b 1
)

if not defined rid (
    if not defined arch (
        set "arch=x64"
        if /I "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "arch=arm64"
        if /I "%PROCESSOR_ARCHITEW6432%"=="ARM64" set "arch=arm64"
    )
    if /I "!arch!"=="x64"     set "rid=win-x64"
    if /I "!arch!"=="x86_64"  set "rid=win-x64"
    if /I "!arch!"=="amd64"   set "rid=win-x64"
    if /I "!arch!"=="arm64"   set "rid=win-arm64"
    if /I "!arch!"=="aarch64" set "rid=win-arm64"
    if /I "!arch!"=="x86"     set "rid=win-x86"
)
set "platform="
if /I "%rid%"=="win-x64"   set "platform=x64"
if /I "%rid%"=="win-arm64" set "platform=ARM64"
if /I "%rid%"=="win-x86"   set "platform=Win32"
if not defined platform (
    echo geometry-worker: ERROR: '%rid%' is not a Windows RID; macOS and Linux use build.sh
    exit /b 1
)

echo  %KERNEL_RIDS% | findstr /C:" %rid%" >nul
if errorlevel 1 echo geometry-worker: note: %rid% is not in recipe.env's KERNEL_RIDS, so no installer ships what this builds.

set "cacheroot=%LOCALAPPDATA%\circuitRF-build\occt"
if defined CRF_OCCT_CACHE set "cacheroot=%CRF_OCCT_CACHE%"
rem NO SPACE IN THE PATH: some CMake and autotools paths refuse one (em-3d.md 7.2).
if not "%cacheroot: =%"=="%cacheroot%" (
    echo geometry-worker: ERROR: the cache path "%cacheroot%" has a space in it; set CRF_OCCT_CACHE to a path without one
    exit /b 1
)
set "vroot=%cacheroot%\%OCCT_VERSION%"
set "rdir=%vroot%\%rid%"

if exist "%rdir%\install.json" (
    echo geometry-worker: OCCT %OCCT_VERSION% for %rid% is already in the cache ^(%rdir%^)
    goto worker
)

call "%here%find-toolchain.cmd" %rid%
if errorlevel 1 exit /b 1
if defined crf_missing goto notools
for %%T in (curl tar certutil) do (
    where %%T >nul 2>&1
    if errorlevel 1 (
        echo geometry-worker: ERROR: '%%T' is not on PATH; building OCCT needs it ^(see tools\geometry-worker\README.md^)
        exit /b 1
    )
)

if not exist "%vroot%" mkdir "%vroot%"
set "archive=%vroot%\OCCT-%OCCT_TAG%.tar.gz"

rem An archive already in the cache is re-verified, never trusted by its name.
if exist "%archive%" (
    call :sha256 "%archive%"
    if /I not "!sha!"=="%OCCT_SHA256%" (
        echo geometry-worker: the cached archive does not match the recipe; fetching it again
        del /f /q "%archive%"
    )
)
if not exist "%archive%" (
    echo geometry-worker: fetching %OCCT_URL%
    curl.exe -fsSL -o "%archive%.part" "%OCCT_URL%"
    if errorlevel 1 (
        del /f /q "%archive%.part" >nul 2>&1
        echo geometry-worker: ERROR: the download failed
        exit /b 1
    )
    call :sha256 "%archive%.part"
    if /I not "!sha!"=="%OCCT_SHA256%" (
        del /f /q "%archive%.part"
        echo geometry-worker: ERROR: the downloaded archive's SHA-256 is !sha!, but the recipe says %OCCT_SHA256%. Nothing was built from it.
        exit /b 1
    )
    move /y "%archive%.part" "%archive%" >nul
)
echo geometry-worker: archive verified: SHA-256 %OCCT_SHA256%

set "src=%vroot%\source\%OCCT_SOURCE_DIR%"
if not exist "%vroot%\source\.unpacked" (
    if exist "%vroot%\source" rmdir /s /q "%vroot%\source"
    mkdir "%vroot%\source"
    tar -xzf "%archive%" -C "%vroot%\source"
    if not exist "%src%\CMakeLists.txt" (
        echo geometry-worker: ERROR: the archive did not unpack to %OCCT_SOURCE_DIR%
        exit /b 1
    )
    type nul > "%vroot%\source\.unpacked"
)

echo geometry-worker: building OCCT %OCCT_VERSION% for %rid%, once: about 5 minutes on 10 cores ^(measured on macOS; Windows is not yet measured^)
if exist "%rdir%" rmdir /s /q "%rdir%"
mkdir "%rdir%"
set "log=%rdir%\build.log"

rem Two declarations OCCT assumes and a MinGW toolchain lacks, force-included (the header says which).
rem Copied into the cache because the repository's path may hold a space and a compiler flag may not.
copy /Y "%here%occt\mingw-compat.h" "%rdir%\mingw-compat.h" >nul
set "compat=%rdir:\=/%/mingw-compat.h"

cmake -S "%src%" -B "%rdir%\build" %crf_cmake_args% "-DCMAKE_INSTALL_PREFIX=%rdir%\install" "-DCMAKE_CXX_FLAGS=-include %compat%" %OCCT_CMAKE_OPTIONS% %OCCT_CMAKE_OPTIONS_WINDOWS% >"%log%" 2>&1
if errorlevel 1 goto occtfailed
cmake --build "%rdir%\build" --parallel >>"%log%" 2>&1
if errorlevel 1 goto occtfailed
cmake --install "%rdir%\build" >>"%log%" 2>&1
if errorlevel 1 goto occtfailed

rem install.json LAST: its presence is what says the RID is built.
> "%rdir%\install.json" (
    echo {
    echo   "occt": "%OCCT_VERSION%",
    echo   "rid": "%rid%",
    echo   "sha256": "%OCCT_SHA256%",
    echo   "toolchain": "llvm-mingw %crf_triple%",
    echo   "built": "%DATE% %TIME%",
    echo   "recipe": "tools/geometry-worker/occt/recipe.env"
    echo }
)
echo geometry-worker: OCCT %OCCT_VERSION% for %rid% built; the build tree ^(%rdir%\build^) may be deleted

:worker
set "passon=--rid %rid% %strict%"
if defined dest set "passon=%passon% --dest "%dest%""
call "%here%ensure-built.cmd" %passon%
exit /b %errorlevel%

:notools
echo geometry-worker: ERROR: not found: %crf_missing%. Building OCCT needs llvm-mingw, CMake and Ninja
echo geometry-worker: ^(no Visual Studio^). Install what is missing, then open a new terminal:
for %%W in (%crf_winget%) do echo     winget install -e --id %%W
echo geometry-worker: or run packaging\windows\build-windows.ps1, which offers to install them.
exit /b 1

:occtfailed
powershell -NoProfile -Command "Get-Content -Tail 30 '%log%'"
echo geometry-worker: ERROR: the OCCT build failed; the whole log is %log%
exit /b 1

rem certutil prints a header line, the hash (spaced into byte pairs on older Windows), and a footer.
:sha256
set "sha="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%~1" SHA256') do (
    if not defined sha set "sha=%%H"
)
set "sha=%sha: =%"
exit /b 0
