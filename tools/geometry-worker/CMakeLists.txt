# The geometry worker (brief-em3d-62). Built by ensure-built.sh / ensure-built.cmd, against the OCCT
# build.sh / build.cmd put in the per-user cache:
#
#   cmake -S tools/geometry-worker -B <build> -DCMAKE_BUILD_TYPE=Release \
#         -DCMAKE_PREFIX_PATH=~/.circuitRF-build/occt/<version>/<rid>/install
#
# THE WORKER FINDS OCCT BESIDE ITSELF, NEVER IN THE CACHE (R-em3d62-4a). The run path is the worker's
# own directory on every platform, and it is set on the BUILD-tree binary too (BUILD_WITH_INSTALL_RPATH):
# a worker that found its libraries only because the cache happened to be on the machine would pass
# every developer's run and fail every user's. ensure-built stages the worker with its library closure
# in one folder, geometry-kernel/, and that folder is what ships.
#
#   macOS    LC_RPATH  @loader_path     (OCCT's install names are @rpath/libTK*.8.0.dylib)
#   Linux    RUNPATH   $ORIGIN          (OCCT's own libraries carry $ORIGIN too: recipe.env)
#   Windows  none      the loader searches the executable's own directory first
cmake_minimum_required(VERSION 3.16)
project(geometry_worker CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(OpenCASCADE REQUIRED CONFIG)

# The worker reports circuitRF's version: the repo-root VERSION file, the one place it is written.
file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/../../VERSION" CRF_VERSION LIMIT_COUNT 1)
string(STRIP "${CRF_VERSION}" CRF_VERSION)

add_executable(geometry-worker geometry_worker.cpp)
target_include_directories(geometry-worker PRIVATE ${OpenCASCADE_INCLUDE_DIR})
target_compile_definitions(geometry-worker PRIVATE CRF_WORKER_VERSION="${CRF_VERSION}")

# OCC_CATCH_SIGNALS turns a signal into an exception only where this is defined (brief 61 Q9); OCCT's
# own build defines it for every non-MSVC toolkit. MSVC needs /EHa for the same, since a structured
# exception is what a fault is there.
if(MSVC)
  target_compile_options(geometry-worker PRIVATE /EHa)
else()
  target_compile_definitions(geometry-worker PRIVATE OCC_CONVERT_SIGNALS)
endif()

target_link_libraries(geometry-worker PRIVATE
  TKDESTEP TKXCAF TKXSBase TKDE TKCAF TKLCAF TKCDF
  TKFillet TKBool TKBO TKMesh TKShHealing TKPrim TKTopAlgo TKGeomAlgo TKBRep TKGeomBase TKG3d TKG2d TKMath TKernel)

if(APPLE)
  set_target_properties(geometry-worker PROPERTIES
    BUILD_WITH_INSTALL_RPATH ON
    INSTALL_RPATH "@loader_path")
elseif(UNIX)
  set_target_properties(geometry-worker PROPERTIES
    BUILD_WITH_INSTALL_RPATH ON
    INSTALL_RPATH "$ORIGIN")
  # RUNPATH, not the older RPATH: the linker's default differs between distributions.
  target_link_options(geometry-worker PRIVATE "LINKER:--enable-new-dtags")
endif()
