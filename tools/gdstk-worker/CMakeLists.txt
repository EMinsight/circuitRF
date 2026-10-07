# The gdstk worker (brief-oasis-gdstk.md §4, R-oas-1). Configured by ensure-built.sh / ensure-built.cmd:
#
#   cmake -S tools/gdstk-worker -B <out> -DCMAKE_BUILD_TYPE=Release
#         -DGDSTK_SOURCE_DIR=<cache>/source/gdstk-<v> -DCMAKE_PREFIX_PATH=<cache>/<rid>/deps
#         -DZLIB_ROOT=<cache>/<rid>/deps -DQHULL_ROOT=<cache>/<rid>/deps [per-RID toolchain or OSX options]
#
# gdstk is added as a SUBDIRECTORY from the cache's unpacked source, not installed and found: as a
# sub-project its CMakeLists builds the library alone (as the top-level project it forces its examples
# and CTest on, with a CACHE FORCE no -D can undo). Its own src/CMakeLists.txt finds zlib and qhull with
# find_package, from the static builds under CMAKE_PREFIX_PATH. Nothing of gdstk's is modified (G0 Q1).
#
# ONE STATICALLY LINKED EXECUTABLE (D3): gdstk, qhull and zlib are static archives; Linux adds
# -static-libstdc++ -static-libgcc (glibc stays dynamic), Windows (llvm-mingw) links -static, so neither
# libc++.dll nor libunwind.dll is needed beside it. macOS links only the system's libc++ and libSystem.
cmake_minimum_required(VERSION 3.26)
project(gdstk_worker C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT GDSTK_SOURCE_DIR)
  message(FATAL_ERROR "set GDSTK_SOURCE_DIR to the unpacked gdstk source (ensure-built does)")
endif()

# Static archives only, and only ours: never a system zlib or qhull.
set(ZLIB_USE_STATIC_LIBS ON)
set(QHULL_REQUIRED_TYPE STATIC)

# Found here first, with gdstk's own FindQhull.cmake, so the targets are visible in this directory too
# (gdstk's FindQhull returns early when QHULL::QHULL already exists).
list(APPEND CMAKE_MODULE_PATH "${GDSTK_SOURCE_DIR}/cmake_modules")
find_package(ZLIB 1.2.7 REQUIRED)
find_package(Qhull 8 REQUIRED)
message(STATUS "zlib: ${ZLIB_LIBRARIES} (${ZLIB_VERSION})")
get_target_property(_qh QHULL::QHULL INTERFACE_LINK_LIBRARIES)
message(STATUS "qhull: ${_qh}")

add_subdirectory(${GDSTK_SOURCE_DIR} gdstk EXCLUDE_FROM_ALL)

# The worker reports circuitRF's own version (the repository's one VERSION file).
file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/../../VERSION" CRF_VERSION LIMIT_COUNT 1)
string(STRIP "${CRF_VERSION}" CRF_VERSION)

set(_sources gdstk_worker.cpp)
if(WIN32)
  # The application manifest: the process's ANSI code page is UTF-8, so gdstk's fopen(const char*) reads
  # the UTF-8 path the protocol carries on a machine of any system code page (G0 Q3, the owner's Windows
  # session). Long paths are the worker's own job (NativePath adds \\?\), because longPathAware is
  # honoured only where the machine's LongPathsEnabled policy is on.
  enable_language(RC)
  list(APPEND _sources windows/gdstk-worker.rc)
endif()

add_executable(gdstk-worker ${_sources})
target_compile_definitions(gdstk-worker PRIVATE CRF_WORKER_VERSION="${CRF_VERSION}")
target_link_libraries(gdstk-worker PRIVATE gdstk::gdstk QHULL::QHULL)
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(gdstk-worker PRIVATE -static-libstdc++ -static-libgcc)
elseif(WIN32)
  target_link_options(gdstk-worker PRIVATE -static)
endif()
