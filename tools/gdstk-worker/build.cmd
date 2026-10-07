@echo off
rem Windows counterpart of build.sh: gdstk's dependencies into the per-user cache (if it lacks them), then
rem the gdstk worker.
rem
rem     tools\gdstk-worker\build.cmd                    this machine's own RID
rem     tools\gdstk-worker\build.cmd --rid win-arm64    another Windows RID (llvm-mingw targets all three)
rem     tools\gdstk-worker\build.cmd --arch x64         the same, by architecture
rem     tools\gdstk-worker\build.cmd --strict           fail rather than warn (the packaging script)
rem
rem THIS IS THE ONLY THING ON WINDOWS THAT FETCHES gdstk, qhull OR zlib, and it is only ever run
rem deliberately (brief-oasis-gdstk.md 4b-c). `dotnet build` runs ensure-built.cmd, which reads the cache and
rem nothing else. Nothing of theirs is written into the repository: archives, sources and the static
rem dependency builds all live in the cache,
rem
rem     %LOCALAPPDATA%\circuitRF-build\gdstk\<version>\          the verified archives and the sources
rem     %LOCALAPPDATA%\circuitRF-build\gdstk\<version>\<rid>\    deps\ (static zlib, qhull), install.json (LAST)
rem
rem or under CRF_GDSTK_CACHE. Needs llvm-mingw, CMake and Ninja -- no Visual Studio. The geometry worker's
rem tools\geometry-worker\find-toolchain.cmd finds them (one place looks, not two) and names the winget ids
rem of whatever is missing. curl.exe, tar.exe and certutil are part of Windows. A first build takes well
rem under a minute.
setlocal EnableDelayedExpansion
set "here=%~dp0"
set "recipe=%here%recipe.env"
set "findtc=%here%..\geometry-worker\find-toolchain.cmd"
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
echo gdstk-worker: ERROR: unknown argument %1
exit /b 1
:after

rem The recipe is READ, never `call`ed: its values hold spaces.
for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%recipe%") do set "%%A=%%B"
if not defined GDSTK_VERSION (
    echo gdstk-worker: ERROR: %recipe% names no GDSTK_VERSION
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
set "known="
if /I "%rid%"=="win-x64"   set "known=1"
if /I "%rid%"=="win-arm64" set "known=1"
if /I "%rid%"=="win-x86"   set "known=1"
if not defined known (
    echo gdstk-worker: ERROR: '%rid%' is not a Windows RID; macOS and Linux use build.sh
    exit /b 1
)

echo  %KERNEL_RIDS% | findstr /C:" %rid%" >nul
if errorlevel 1 echo gdstk-worker: note: %rid% is not in recipe.env's KERNEL_RIDS, so no installer ships what this builds.

set "cacheroot=%LOCALAPPDATA%\circuitRF-build\gdstk"
if defined CRF_GDSTK_CACHE set "cacheroot=%CRF_GDSTK_CACHE%"
if not "%cacheroot: =%"=="%cacheroot%" (
    echo gdstk-worker: ERROR: the cache path "%cacheroot%" has a space in it; set CRF_GDSTK_CACHE to a path without one
    exit /b 1
)
set "vroot=%cacheroot%\%GDSTK_VERSION%"
set "rdir=%vroot%\%rid%"
if not exist "%vroot%\source" mkdir "%vroot%\source"

for %%T in (curl tar certutil) do (
    where %%T >nul 2>&1
    if errorlevel 1 (
        echo gdstk-worker: ERROR: '%%T' is not on PATH; building the gdstk worker needs it ^(see tools\gdstk-worker\README.md^)
        exit /b 1
    )
)

rem gdstk's own source is compiled with the worker (ensure-built.cmd), so it is checked on every run.
call :fetch GDSTK
if errorlevel 1 exit /b 1

if exist "%rdir%\install.json" (
    echo gdstk-worker: gdstk %GDSTK_VERSION%'s dependencies for %rid% are already in the cache ^(%rdir%^)
    goto worker
)

call "%findtc%" %rid%
if errorlevel 1 exit /b 1
if defined crf_missing goto notools

call :fetch QHULL
if errorlevel 1 exit /b 1
call :fetch ZLIB
if errorlevel 1 exit /b 1

if not exist "%rdir%" mkdir "%rdir%"
for %%D in (deps build-zlib build-qhull) do if exist "%rdir%\%%D" rmdir /s /q "%rdir%\%%D"
set "log=%rdir%\build.log"
set "deps=%rdir%\deps"
set "depsf=%deps:\=/%"
set "hermetic=-DCMAKE_POSITION_INDEPENDENT_CODE=ON "-DCMAKE_PREFIX_PATH=%depsf%" -DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF -DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF "-DCMAKE_INSTALL_PREFIX=%depsf%""

echo gdstk-worker: building zlib %ZLIB_VERSION% for %rid%
cmake -S "%vroot%\source\%ZLIB_SOURCE_DIR%" -B "%rdir%\build-zlib" %crf_cmake_args% %hermetic% %ZLIB_CMAKE_OPTIONS% >"%log%" 2>&1
if errorlevel 1 goto depsfailed
cmake --build "%rdir%\build-zlib" --parallel >>"%log%" 2>&1
if errorlevel 1 goto depsfailed
cmake --install "%rdir%\build-zlib" >>"%log%" 2>&1
if errorlevel 1 goto depsfailed

echo gdstk-worker: building qhull %QHULL_VERSION% for %rid%
cmake -S "%vroot%\source\%QHULL_SOURCE_DIR%" -B "%rdir%\build-qhull" %crf_cmake_args% %hermetic% %QHULL_CMAKE_OPTIONS% >>"%log%" 2>&1
if errorlevel 1 goto depsfailed
cmake --build "%rdir%\build-qhull" --parallel --target qhullstatic_r >>"%log%" 2>&1
if errorlevel 1 goto depsfailed
rem Only what the static reentrant library needs: its headers and archive (build.sh says why).
if not exist "%deps%\include\libqhull_r" mkdir "%deps%\include\libqhull_r"
if not exist "%deps%\lib" mkdir "%deps%\lib"
copy /Y "%vroot%\source\%QHULL_SOURCE_DIR%\src\libqhull_r\*.h" "%deps%\include\libqhull_r\" >nul
copy /Y "%rdir%\build-qhull\libqhullstatic_r.a" "%deps%\lib\" >nul
if errorlevel 1 goto depsfailed
rmdir /s /q "%rdir%\build-zlib"
rmdir /s /q "%rdir%\build-qhull"

rem install.json LAST: its presence is what says the RID is built.
> "%rdir%\install.json" (
    echo {
    echo   "gdstk": "%GDSTK_VERSION%",
    echo   "qhull": "%QHULL_VERSION%",
    echo   "zlib": "%ZLIB_VERSION%",
    echo   "rid": "%rid%",
    echo   "gdstk_sha256": "%GDSTK_SHA256%",
    echo   "qhull_sha256": "%QHULL_SHA256%",
    echo   "zlib_sha256": "%ZLIB_SHA256%",
    echo   "toolchain": "llvm-mingw %crf_triple%",
    echo   "built": "%DATE% %TIME%",
    echo   "recipe": "tools/gdstk-worker/recipe.env"
    echo }
)
echo gdstk-worker: gdstk's dependencies for %rid% built

:worker
set "passon=--rid %rid% %strict%"
if defined dest set "passon=%passon% --dest "%dest%""
call "%here%ensure-built.cmd" %passon%
exit /b %errorlevel%

:notools
echo gdstk-worker: ERROR: not found: %crf_missing%. Building the gdstk worker needs llvm-mingw, CMake and Ninja
echo gdstk-worker: ^(no Visual Studio^). Install what is missing, then open a new terminal:
for %%W in (%crf_winget%) do echo     winget install -e --id %%W
echo gdstk-worker: or run packaging\windows\build-windows.ps1, which offers to install them.
exit /b 1

:depsfailed
powershell -NoProfile -Command "Get-Content -Tail 30 '%log%'"
echo gdstk-worker: ERROR: a dependency build failed; the whole log is %log%
exit /b 1

rem fetch NAME: the archive the recipe names, verified against its SHA-256 before anything is unpacked. An
rem archive already in the cache is re-verified, never trusted by its name; a mismatch names both hashes.
:fetch
set "f_url=!%1_URL!"
set "f_sha=!%1_SHA256!"
set "f_file=!%1_ARCHIVE!"
set "f_dir=!%1_SOURCE_DIR!"
set "f_archive=%vroot%\!f_file!"
if exist "!f_archive!" (
    call :sha256 "!f_archive!"
    if /I not "!sha!"=="!f_sha!" (
        echo gdstk-worker: the cached !f_file! does not match the recipe; fetching it again
        del /f /q "!f_archive!"
    )
)
if not exist "!f_archive!" (
    echo gdstk-worker: fetching !f_url!
    curl.exe -fsSL -o "!f_archive!.part" "!f_url!"
    if errorlevel 1 (
        del /f /q "!f_archive!.part" >nul 2>&1
        echo gdstk-worker: ERROR: the download of !f_url! failed
        exit /b 1
    )
    call :sha256 "!f_archive!.part"
    if /I not "!sha!"=="!f_sha!" (
        del /f /q "!f_archive!.part"
        echo gdstk-worker: ERROR: !f_file!'s SHA-256 is !sha!, but the recipe says !f_sha!. Nothing was built from it.
        exit /b 1
    )
    move /y "!f_archive!.part" "!f_archive!" >nul
)
if not exist "%vroot%\source\!f_dir!\.unpacked" (
    if exist "%vroot%\source\!f_dir!" rmdir /s /q "%vroot%\source\!f_dir!"
    tar -xzf "!f_archive!" -C "%vroot%\source"
    if not exist "%vroot%\source\!f_dir!" (
        echo gdstk-worker: ERROR: !f_file! did not unpack to !f_dir!
        exit /b 1
    )
    type nul > "%vroot%\source\!f_dir!\.unpacked"
)
exit /b 0

rem certutil prints a header line, the hash (spaced into byte pairs on older Windows), and a footer.
:sha256
set "sha="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%~1" SHA256') do (
    if not defined sha set "sha=%%H"
)
set "sha=%sha: =%"
exit /b 0
