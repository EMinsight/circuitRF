# The G0 spike's worker and probe (brief-oasis-gdstk.md §3). Built by build.sh beside this file:
#
#   cmake -S tools/gdstk-worker/spike -B <cache>/<rid>/spike-build -DCMAKE_BUILD_TYPE=Release \
#         -DGDSTK_SOURCE_DIR=<cache>/source/gdstk-1.0.1 -DCMAKE_PREFIX_PATH=<cache>/<rid>/deps
#
# gdstk is added as a SUBDIRECTORY from the cache's unpacked source, not installed and found: as a
# sub-project its CMakeLists builds the library alone (as the top-level project it forces its examples
# and CTest on, with a CACHE FORCE no -D can undo). Its own src/CMakeLists.txt still finds zlib and qhull
# with find_package, from the static builds under CMAKE_PREFIX_PATH. Nothing of gdstk's is modified.
#
# ONE STATICALLY LINKED EXECUTABLE (D3): gdstk, qhull and zlib are static archives; Linux adds
# -static-libstdc++ -static-libgcc (glibc stays dynamic), Windows (llvm-mingw) links -static, so neither
# libc++.dll nor libunwind.dll is needed beside it. macOS links only the system's libc++ and libSystem.
cmake_minimum_required(VERSION 3.26)
project(gdstk_worker_spike C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT GDSTK_SOURCE_DIR)
  message(FATAL_ERROR "set GDSTK_SOURCE_DIR to the unpacked gdstk source (build.sh does)")
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

file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/../../../VERSION" CRF_VERSION LIMIT_COUNT 1)
string(STRIP "${CRF_VERSION}" CRF_VERSION)

function(crf_static_exe target)
  if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    target_link_options(${target} PRIVATE -static-libstdc++ -static-libgcc)
  elseif(WIN32)
    target_link_options(${target} PRIVATE -static)
  endif()
endfunction()

add_executable(gdstk-worker gdstk_worker.cpp)
target_compile_definitions(gdstk-worker PRIVATE CRF_WORKER_VERSION="${CRF_VERSION}-spike")
target_link_libraries(gdstk-worker PRIVATE gdstk::gdstk QHULL::QHULL)
crf_static_exe(gdstk-worker)

# Windows only: the same worker with an application manifest (UTF-8 code page, long paths) -- Q3's question is
# which of the two reads a non-ASCII and a long path on real Windows.
if(WIN32)
  enable_language(RC)
  add_executable(gdstk-worker-utf8 gdstk_worker.cpp windows/utf8.rc)
  target_compile_definitions(gdstk-worker-utf8 PRIVATE CRF_WORKER_VERSION="${CRF_VERSION}-spike-utf8")
  target_link_libraries(gdstk-worker-utf8 PRIVATE gdstk::gdstk QHULL::QHULL)
  crf_static_exe(gdstk-worker-utf8)
endif()

add_executable(gdstk-probe probe.cpp)
target_link_libraries(gdstk-probe PRIVATE gdstk::gdstk QHULL::QHULL)
crf_static_exe(gdstk-probe)
