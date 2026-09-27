# brief-em3d-61 -- the spike's probe harness. SPIKE MATERIAL, NOT PRODUCT CODE.
# Configure against an OCCT built by the recipe in docs/design/em-3d-f4b-spike-findings.md:
#   cmake -S testdata/em3d/f4b/harness -B <build>/probe -DCMAKE_BUILD_TYPE=Release \
#         -DOpenCASCADE_DIR=<occt-install>/lib/cmake/opencascade
#   cmake --build <build>/probe
# (Windows: -DOpenCASCADE_DIR=<occt-install>/cmake)
cmake_minimum_required(VERSION 3.16)
project(occt_probe CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(OpenCASCADE REQUIRED CONFIG)

add_executable(occt_probe occt_probe.cpp)
target_include_directories(occt_probe PRIVATE ${OpenCASCADE_INCLUDE_DIR})
# OCC_CATCH_SIGNALS converts a signal to an exception only where this is defined (Q9); OCCT's own
# build defines it for every non-MSVC toolkit.
if(NOT MSVC)
  target_compile_definitions(occt_probe PRIVATE OCC_CONVERT_SIGNALS)
endif()
if(MSVC)
  target_compile_options(occt_probe PRIVATE /EHa)
endif()
target_link_libraries(occt_probe PRIVATE
  TKDESTEP TKXCAF TKXSBase TKDE TKCAF TKLCAF TKCDF
  TKFillet TKBool TKBO TKMesh TKShHealing TKPrim TKTopAlgo TKGeomAlgo TKBRep TKGeomBase TKG3d TKG2d TKMath TKernel)
# find the OCCT libraries next to the build without an environment variable
set_target_properties(occt_probe PROPERTIES BUILD_RPATH "${OpenCASCADE_LIBRARY_DIR}")
