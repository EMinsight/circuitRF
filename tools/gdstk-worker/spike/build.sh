#!/usr/bin/env bash
# The G0 spike's build (brief-oasis-gdstk.md §3, Q1): zlib, qhull and gdstk from pinned, verified
# sources into the per-user cache, then the spike's worker and probe, statically linked.
#
#     tools/gdstk-worker/spike/build.sh <rid>
#
#   rid   osx-arm64 | osx-x64                       on a Mac (x64 cross-built with CMAKE_OSX_ARCHITECTURES)
#         win-x64 | win-x86 | win-arm64             on a Mac, with llvm-mingw (CRF_LLVM_MINGW names its root)
#         linux-x64 | linux-arm64                   on Linux (run-linux.sh runs this in a container); the
#                                                   other architecture with the distribution's cross g++
#
#   cache  ${CRF_GDSTK_CACHE:-~/.circuitRF-build/gdstk}/1.0.1/
#            <archives>, source/                    verified once, unpacked once, shared by every RID
#            toolchain-<rid>.cmake                  written here, never in the repository
#            <rid>/deps/                            static zlib and qhull (install prefix)
#            <rid>/spike/                           gdstk-worker[.exe], gdstk-probe[.exe], build.log
#
# Nothing of gdstk's, qhull's or zlib's is written into the repository. This is spike code: G1's
# build.sh / ensure-built.sh replace it (brief §4).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
recipe="$here/recipe.env"
say() { echo "gdstk-spike: $*"; }
die() { echo "gdstk-spike: ERROR: $*" >&2; exit 1; }

recipe_value() {
  local key="$1" k v
  while IFS='=' read -r k v; do
    [ "$k" = "$key" ] && { printf '%s' "$v"; return 0; }
  done < "$recipe"
  return 1
}

rid="${1:-}"
[ -n "$rid" ] || die "name a RID (see the header of this file)"

GDSTK_VERSION=$(recipe_value GDSTK_VERSION)
cache_root="${CRF_GDSTK_CACHE:-$HOME/.circuitRF-build/gdstk}"
case "$cache_root" in *" "*) die "the cache path '$cache_root' has a space in it" ;; esac
vroot="$cache_root/$GDSTK_VERSION"
rdir="$vroot/$rid"
mkdir -p "$vroot/source" "$rdir"

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}

# fetch NAME: the archive the recipe names, verified against its SHA-256 before anything is unpacked.
fetch() {
  local name="$1" url sha file dir got
  url=$(recipe_value "${name}_URL")
  sha=$(recipe_value "${name}_SHA256")
  file=$(recipe_value "${name}_ARCHIVE")
  dir=$(recipe_value "${name}_SOURCE_DIR")
  local archive="$vroot/$file"
  if [ -f "$archive" ] && [ "$(sha256_of "$archive")" != "$sha" ]; then
    say "the cached $file does not match the recipe; fetching it again"
    rm -f "$archive"
  fi
  if [ ! -f "$archive" ]; then
    say "fetching $url"
    curl -fsSL -o "$archive.part" "$url" || { rm -f "$archive.part"; die "the download of $url failed"; }
    got="$(sha256_of "$archive.part")"
    if [ "$got" != "$sha" ]; then
      rm -f "$archive.part"
      die "$file's SHA-256 is $got, but the recipe says $sha. Nothing was built from it."
    fi
    mv "$archive.part" "$archive"
  fi
  if [ ! -f "$vroot/source/$dir/.unpacked" ]; then
    rm -rf "${vroot:?}/source/$dir"
    tar -xzf "$archive" -C "$vroot/source"
    [ -d "$vroot/source/$dir" ] || die "$file did not unpack to $dir"
    : > "$vroot/source/$dir/.unpacked"
  fi
}

fetch GDSTK
fetch QHULL
fetch ZLIB
src_gdstk="$vroot/source/$(recipe_value GDSTK_SOURCE_DIR)"
src_qhull="$vroot/source/$(recipe_value QHULL_SOURCE_DIR)"
src_zlib="$vroot/source/$(recipe_value ZLIB_SOURCE_DIR)"

# ---- the per-RID configuration ----------------------------------------------------------------
per_rid=()
exe=""
case "$rid" in
  osx-arm64) per_rid=(-DCMAKE_OSX_ARCHITECTURES=arm64 "-DCMAKE_OSX_DEPLOYMENT_TARGET=$(recipe_value OSX_DEPLOYMENT_TARGET)") ;;
  osx-x64)   per_rid=(-DCMAKE_OSX_ARCHITECTURES=x86_64 "-DCMAKE_OSX_DEPLOYMENT_TARGET=$(recipe_value OSX_DEPLOYMENT_TARGET)") ;;
  win-x64|win-x86|win-arm64)
    case "$rid" in
      win-x64)   triple=x86_64-w64-mingw32;  proc=AMD64 ;;
      win-x86)   triple=i686-w64-mingw32;    proc=X86 ;;
      win-arm64) triple=aarch64-w64-mingw32; proc=ARM64 ;;
    esac
    mingw="${CRF_LLVM_MINGW:-$HOME/.circuitRF-build/toolchains/llvm-mingw-$(recipe_value LLVM_MINGW_VERSION)-ucrt-macos-universal}"
    [ -x "$mingw/bin/$triple-clang++" ] || die "no llvm-mingw at $mingw (set CRF_LLVM_MINGW)"
    tc="$vroot/toolchain-$rid.cmake"
    cat > "$tc" <<EOF
# Written by tools/gdstk-worker/spike/build.sh for $rid (llvm-mingw $(recipe_value LLVM_MINGW_VERSION)).
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR $proc)
set(CMAKE_C_COMPILER $mingw/bin/$triple-clang)
set(CMAKE_CXX_COMPILER $mingw/bin/$triple-clang++)
set(CMAKE_RC_COMPILER $mingw/bin/$triple-windres)
set(CMAKE_FIND_ROOT_PATH $mingw/$triple)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
# Libraries, headers and packages are left at BOTH: ONLY would re-root CMAKE_PREFIX_PATH (the static
# zlib and qhull) under the sysroot and lose them, as tools/geometry-worker/RESOLVED.md found for OCCT.
# ZLIB_ROOT and QHULL_ROOT point at the prefix explicitly, and the link would fail on a host library.
EOF
    per_rid=("-DCMAKE_TOOLCHAIN_FILE=$tc")
    exe=".exe" ;;
  linux-x64|linux-arm64)
    [ "$(uname -s)" = Linux ] || die "$rid is built on Linux (run-linux.sh runs this script in a container)"
    case "$(uname -m)" in aarch64|arm64) host=linux-arm64 ;; *) host=linux-x64 ;; esac
    if [ "$rid" != "$host" ]; then
      case "$rid" in
        linux-x64)   triple=x86_64-linux-gnu;  proc=x86_64 ;;
        linux-arm64) triple=aarch64-linux-gnu; proc=aarch64 ;;
      esac
      command -v "$triple-g++" >/dev/null || die "no $triple-g++ to cross-build $rid"
      tc="$vroot/toolchain-$rid.cmake"
      printf 'set(CMAKE_SYSTEM_NAME Linux)\nset(CMAKE_SYSTEM_PROCESSOR %s)\nset(CMAKE_C_COMPILER %s-gcc)\nset(CMAKE_CXX_COMPILER %s-g++)\n' \
        "$proc" "$triple" "$triple" > "$tc"
      per_rid=("-DCMAKE_TOOLCHAIN_FILE=$tc")
    fi ;;
  *) die "unknown RID '$rid'" ;;
esac

if [ "$(uname -s)" = Darwin ]; then jobs=$(sysctl -n hw.ncpu); else jobs=$(nproc 2>/dev/null || echo 4); fi
deps="$rdir/deps"
log="$rdir/build.log"
: > "$log"
started=$(date +%s)

run() { echo "+ $*" >>"$log"; "$@" >>"$log" 2>&1 || { tail -n 40 "$log" >&2; die "a step failed; the whole log is $log"; }; }

# Find nothing outside the prefix we hand in: a system zlib or qhull must never be linked.
hermetic=(-DCMAKE_BUILD_TYPE=Release -DCMAKE_POSITION_INDEPENDENT_CODE=ON "-DCMAKE_PREFIX_PATH=$deps"
          -DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF -DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF)

if [ ! -f "$deps/.built" ]; then
  rm -rf "$rdir/build-zlib" "$rdir/build-qhull" "$deps"
  say "building zlib $(recipe_value ZLIB_VERSION) for $rid"
  # shellcheck disable=SC2046
  run cmake -S "$src_zlib" -B "$rdir/build-zlib" "${hermetic[@]}" "-DCMAKE_INSTALL_PREFIX=$deps" \
      $(recipe_value ZLIB_CMAKE_OPTIONS) "${per_rid[@]}"
  run cmake --build "$rdir/build-zlib" -j "$jobs"
  run cmake --install "$rdir/build-zlib"
  say "building qhull $(recipe_value QHULL_VERSION) for $rid"
  # shellcheck disable=SC2046
  run cmake -S "$src_qhull" -B "$rdir/build-qhull" "${hermetic[@]}" "-DCMAKE_INSTALL_PREFIX=$deps" \
      $(recipe_value QHULL_CMAKE_OPTIONS) "${per_rid[@]}"
  run cmake --build "$rdir/build-qhull" -j "$jobs" --target qhullstatic_r
  # Install only what the static reentrant library needs: its headers and archive. qhull's own install
  # wants every application built too, which the worker never uses.
  mkdir -p "$deps/include/libqhull_r" "$deps/lib"
  cp "$src_qhull"/src/libqhull_r/*.h "$deps/include/libqhull_r/"
  cp "$rdir"/build-qhull/libqhullstatic_r.a "$deps/lib/"
  : > "$deps/.built"
fi

say "building gdstk $GDSTK_VERSION and the spike worker for $rid"
rm -rf "$rdir/spike-build"
run cmake -S "$here" -B "$rdir/spike-build" "${hermetic[@]}" "-DGDSTK_SOURCE_DIR=$src_gdstk" \
    "-DQHULL_ROOT=$deps" "-DZLIB_ROOT=$deps" "${per_rid[@]}"
run cmake --build "$rdir/spike-build" -j "$jobs"
mkdir -p "$rdir/spike"
cp "$rdir/spike-build/gdstk-worker$exe" "$rdir/spike-build/gdstk-probe$exe" "$rdir/spike/"
[ -n "$exe" ] && cp "$rdir/spike-build/gdstk-worker-utf8$exe" "$rdir/spike/"
elapsed=$(( $(date +%s) - started ))
size=$(wc -c < "$rdir/spike/gdstk-worker$exe" | tr -d ' ')
say "$rid built in ${elapsed} s; gdstk-worker$exe is $size bytes ($rdir/spike)"
echo "{\"rid\":\"$rid\",\"seconds\":$elapsed,\"worker_bytes\":$size}" > "$rdir/spike/build.json"
