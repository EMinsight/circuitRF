@echo off
rem Windows counterpart of ensure-built.sh -- same contract, same guarantees.
rem
rem     ensure-built.cmd [--dest <dir>] [--rid win-x64|win-arm64|win-x86] [--strict]
rem
rem THIS SCRIPT MUST NEVER FAIL A BUILD, except under --strict (the packaging script passes it).
rem
rem IT READS THE CACHE AND NOTHING ELSE (brief-em3d-62 R-em3d62-3f). It never downloads, unpacks or
rem configures OpenCASCADE, and with an empty cache it needs no C++ toolchain: it prints one warning and
rem succeeds. Only tools\geometry-worker\build.cmd fetches OCCT, and only when someone runs it.
rem
rem   cache has OCCT for the RID   compile the worker if its source is newer than it, stage it with the
rem                                OCCT DLLs and the MSVC runtime it was built with in
rem                                build\<rid>\geometry-kernel\, and copy that folder to <dest>
rem   cache does not               one warning, and success
rem
rem The DLLs sit beside geometry-worker.exe: the loader searches an executable's own directory first.
setlocal EnableDelayedExpansion
set "here=%~dp0"
set "recipe=%here%occt\recipe.env"
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
for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%recipe%") do (
    if "%%A"=="OCCT_VERSION" set "version=%%B"
)
if not defined version (
    set "why=no OCCT_VERSION in %recipe%; the geometry kernel is not built."
    goto skip
)

if not defined rid (
    set "rid=win-x64"
    if /I "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "rid=win-arm64"
    if /I "%PROCESSOR_ARCHITEW6432%"=="ARM64" set "rid=win-arm64"
    if /I "%PROCESSOR_ARCHITECTURE%"=="x86" if not defined PROCESSOR_ARCHITEW6432 set "rid=win-x86"
)
set "platform="
if /I "%rid%"=="win-x64"   set "platform=x64"
if /I "%rid%"=="win-arm64" set "platform=ARM64"
if /I "%rid%"=="win-x86"   set "platform=Win32"
if not defined platform (
    set "why=%rid% is not built by this script; the geometry kernel is not built."
    goto skip
)

set "cacheroot=%LOCALAPPDATA%\circuitRF-build\occt"
if defined CRF_OCCT_CACHE set "cacheroot=%CRF_OCCT_CACHE%"
set "cache=%cacheroot%\%version%\%rid%"
set "install=%cache%\install"

if not exist "%cache%\install.json" (
    set "why=the geometry kernel is not built for %rid%, so booleans, fillets and STEP are disabled. Run tools\geometry-worker\build.cmd once (about 5 minutes on 10 cores) to build it."
    goto skip
)

set "work=%here%build\%rid%"
set "stage=%work%\geometry-kernel"
set "worker=%stage%\geometry-worker.exe"
set "log=%work%\worker-build.log"

rem Staleness: the source, the build description, this script, the VERSION file the worker reports, and
rem install.json (which changes when the kernel under it is rebuilt) -- any newer than the worker.
set "stale=1"
if exist "%worker%" (
    set "stale=0"
    for %%F in ("%here%geometry_worker.cpp" "%here%CMakeLists.txt" "%here%ensure-built.cmd" "%here%..\..\VERSION" "%cache%\install.json") do (
        for /f %%R in ('powershell -NoProfile -Command ^
            "if ((Get-Item '%%~fF').LastWriteTime -gt (Get-Item '%worker%').LastWriteTime) {1} else {0}" 2^>nul') do (
            if "%%R"=="1" set "stale=1"
        )
    )
)
if "%stale%"=="0" goto publish

where cmake >nul 2>&1
if errorlevel 1 (
    set "why=OCCT is in the cache but cmake is not on PATH, so the worker cannot be compiled; the geometry kernel is not built."
    goto skip
)

echo geometry-worker: building the worker for %rid%
if not exist "%work%" mkdir "%work%" >nul 2>&1
rem A generator named in the environment (CMAKE_GENERATOR) is honoured; otherwise CMake's default on
rem Windows is Visual Studio, which takes the platform through -A.
set "platformflag=-A %platform%"
if defined CMAKE_GENERATOR set "platformflag="
cmake -S "%here%." -B "%work%\cmake" %platformflag% "-DCMAKE_PREFIX_PATH=%install%" >"%log%" 2>&1
if errorlevel 1 goto compilefailed
cmake --build "%work%\cmake" --config Release >>"%log%" 2>&1
if errorlevel 1 goto compilefailed

set "built=%work%\cmake\Release\geometry-worker.exe"
if not exist "%built%" set "built=%work%\cmake\geometry-worker.exe"
if not exist "%built%" goto compilefailed

rem -- Stage: the worker, the OCCT DLLs, and the MSVC runtime app-local -------------------------------
rem
rem Every TK*.dll the install holds: the recipe builds exactly the worker's closure (25 toolkits, brief
rem 61 Q2), so there is nothing extra to leave out. The MSVC runtime the build used ships beside them
rem rather than being assumed present -- a machine with no Visual C++ redistributable installed is
rem common, and the failure would be the worker not starting at all.
if exist "%stage%" rmdir /s /q "%stage%"
mkdir "%stage%" >nul 2>&1
copy /Y "%built%" "%worker%" >nul
for /r "%install%" %%D in (TK*.dll) do copy /Y "%%D" "%stage%\" >nul

set "crtarch=%platform%"
if /I "%platform%"=="Win32" set "crtarch=x86"
if /I "%platform%"=="ARM64" set "crtarch=arm64"
set "vswhere=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "crt="
if exist "%vswhere%" (
    for /f "usebackq delims=" %%C in (`"%vswhere%" -latest -products * -find "VC\Redist\MSVC\*\%crtarch%\Microsoft.VC*.CRT"`) do set "crt=%%C"
)
if defined crt (
    for %%N in (msvcp140.dll msvcp140_1.dll msvcp140_2.dll vcruntime140.dll vcruntime140_1.dll concrt140.dll) do (
        if exist "!crt!\%%N" copy /Y "!crt!\%%N" "%stage%\" >nul
    )
) else (
    echo geometry-worker: the MSVC runtime redistributable was not found ^(vswhere^); the worker will need
    echo geometry-worker: the Visual C++ runtime installed on any machine it runs on.
)

rem Answering proves the DLLs beside it are enough. Skipped for an architecture this machine cannot run.
set "canrun=1"
if /I "%rid%"=="win-arm64" if /I not "%PROCESSOR_ARCHITECTURE%"=="ARM64" if /I not "%PROCESSOR_ARCHITEW6432%"=="ARM64" set "canrun=0"
if "%canrun%"=="1" (
    "%worker%" --version >nul 2>&1
    if errorlevel 1 (
        rmdir /s /q "%stage%"
        set "why=the worker compiled but does not run from its own folder; the geometry kernel is not built."
        goto skip
    )
    for /f "delims=" %%V in ('"%worker%" --version') do echo geometry-worker: %%V
) else (
    echo geometry-worker: this machine cannot execute a %rid% worker, so it was not run here.
)
goto publish

:compilefailed
set "why=the worker did not compile (the log is %log%); the geometry kernel is not built."
goto skip

:publish
if "%dest%"=="" exit /b 0
if not exist "%worker%" exit /b 0
if exist "%dest%\geometry-kernel" rmdir /s /q "%dest%\geometry-kernel"
xcopy /E /I /Q /Y "%stage%" "%dest%\geometry-kernel" >nul
exit /b 0

:skip
rem The form MSBuild reads as a WARNING, so the build summary counts it.
echo geometry-worker : warning GK001 : !why!
if "%strict%"=="1" exit /b 1
exit /b 0
