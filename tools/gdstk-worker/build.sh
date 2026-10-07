#!/usr/bin/env bash
# Fetches and builds gdstk's dependencies into the per-user cache (if the cache lacks them), then the gdstk worker.
#
#     tools/gdstk-worker/build.sh                     this machine's own RID
#     tools/gdstk-worker/build.sh --rid osx-x64       another RID this machine can build for
#     tools/gdstk-worker/build.sh --rid win-x64       a Windows RID, cross-built with llvm-mingw (CRF_LLVM_MINGW)
#     tools/gdstk-worker/build.sh --arch x64          the same, by architecture (this OS)
#     tools/gdstk-worker/build.sh --strict            fail rather than warn (the packaging scripts)
#
# THIS IS THE ONLY THING IN circuitRF THAT FETCHES gdstk, qhull OR zlib, and it is only ever run deliberately
# (brief-oasis-gdstk.md §4b-c). `dotnet build` runs ensure-built.sh, which reads the cache and nothing else; a
# machine that never runs this script builds, tests and runs circuitRF with the gdstk worker reported absent.
# Nothing of theirs is ever written into the repository: the archives, the source trees, the dependency builds
# and the toolchain files all live in the cache (D2).
#
#   cache    ~/.circuitRF-build/gdstk/<version>/              the verified archives and the unpacked sources
#            ~/.circuitRF-build/gdstk/<version>/<rid>/        deps/ (static zlib and qhull) and install.json --
#                                                             written LAST, so an interrupted build is never
#                                                             taken for a finished one
#            ~/.circuitRF-build/gdstk/<version>/toolchain-<rid>.cmake   a cross build's toolchain file
#   override CRF_GDSTK_CACHE=/another/root                    (no space in it)
#
# A first build of a RID takes well under a minute (G0 Q1: 9-23 s); every later run finds install.json and
# goes straight to the worker, which takes seconds.
#
# Cross-building: macOS builds either Mac architecture (CMAKE_OSX_ARCHITECTURES) and every Windows RID with
# llvm-mingw's macOS release (the route tools/geometry-worker/RESOLVED.md records; CRF_LLVM_MINGW names its
# root). Linux builds the other architecture with the distribution's cross g++ (crossbuild-essential-amd64 /
# -arm64). On Windows, use build.cmd.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
recipe="$here/recipe.env"

say() { echo "gdstk-worker: $*"; }
die() { echo "gdstk-worker: ERROR: $*" >&2; exit 1; }

# The recipe is READ, not sourced (recipe.env says why).
recipe_value() {
  local key="$1" k v
  while IFS='=' read -r k v; do
    [ "$k" = "$key" ] && { printf '%s' "$v"; return 0; }
  done < "$recipe"
  return 1
}

GDSTK_VERSION=$(recipe_value GDSTK_VERSION) || die "no GDSTK_VERSION in $recipe"
KERNEL_RIDS=$(recipe_value KERNEL_RIDS || true)

rid=""
arch=""
strict=""
dest=""
while [ $# -gt 0 ]; do
  case "$1" in
    --rid)    rid="${2:-}"; shift 2 ;;
    --arch)   arch="${2:-}"; shift 2 ;;
    --strict) strict="--strict"; shift ;;
    --dest)   dest="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,29p' "$0"; exit 0 ;;
    *) die "unknown argument '$1' (see --help)" ;;
  esac
done

case "$(uname -s)" in
  Darwin) host_os=osx ;;
  Linux)  host_os=linux ;;
  *) die "this script builds on macOS and Linux; on Windows run tools\\gdstk-worker\\build.cmd" ;;
esac
case "$(uname -m)" in
  arm64|aarch64) host_arch=arm64 ;;
  x86_64|amd64)  host_arch=x64 ;;
  *) die "unsupported machine architecture $(uname -m)" ;;
esac

if [ -z "$rid" ]; then
  case "${arch:-$host_arch}" in
    arm64|aarch64)    rid="$host_os-arm64" ;;
    x64|x86_64|amd64) rid="$host_os-x64" ;;
    *) die "unsupported --arch '$arch' (arm64 or x64)" ;;
  esac
fi

case "$rid" in
  osx-arm64|osx-x64) [ "$host_os" = osx ] || die "$rid is built on macOS" ;;
  linux-arm64|linux-x64) [ "$host_os" = linux ] || die "$rid is built on Linux" ;;
  win-x64|win-x86|win-arm64) ;;
  *) die "unknown RID '$rid'" ;;
esac

case " $KERNEL_RIDS " in
  *" $rid "*) ;;
  *) say "note: $rid is not in recipe.env's KERNEL_RIDS, so no installer ships what this builds." ;;
esac

cache_root="${CRF_GDSTK_CACHE:-$HOME/.circuitRF-build/gdstk}"
case "$cache_root" in
  *" "*) die "the cache path '$cache_root' has a space in it; set CRF_GDSTK_CACHE to a path without one" ;;
esac
vroot="$cache_root/$GDSTK_VERSION"
rdir="$vroot/$rid"
mkdir -p "$vroot/source"

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}

# fetch NAME: the archive the recipe names, verified against its SHA-256 before anything is unpacked. An archive
# already in the cache is re-verified, never trusted by its name; a mismatch names both hashes.
fetch() {
  local name="$1" url sha file dir got archive
  url=$(recipe_value "${name}_URL")
  sha=$(recipe_value "${name}_SHA256")
  file=$(recipe_value "${name}_ARCHIVE")
  dir=$(recipe_value "${name}_SOURCE_DIR")
  archive="$vroot/$file"
  if [ -f "$archive" ] && [ "$(sha256_of "$archive")" != "$sha" ]; then
    say "the cached $file does not match the recipe; fetching it again"
    rm -f "$archive"
  fi
  if [ ! -f "$archive" ]; then
    say "fetching $url"
    if command -v curl >/dev/null 2>&1; then
      curl -fsSL -o "$archive.part" "$url" || { rm -f "$archive.part"; die "the download of $url failed"; }
    elif command -v wget >/dev/null 2>&1; then
      wget -q -O "$archive.part" "$url" || { rm -f "$archive.part"; die "the download of $url failed"; }
    else
      die "neither curl nor wget is on PATH"
    fi
    got="$(sha256_of "$archive.part")"
    if [ "$got" != "$sha" ]; then
      rm -f "$archive.part"
      die "$file's SHA-256 is $got, but the recipe says $sha. Nothing was built from it."
    fi
    mv "$archive.part" "$archive"
  fi
  # Unpacked once per version and shared by every RID; the marker is written last, like install.json.
  if [ ! -f "$vroot/source/$dir/.unpacked" ]; then
    rm -rf "${vroot:?}/source/$dir"
    tar -xzf "$archive" -C "$vroot/source"
    [ -d "$vroot/source/$dir" ] || die "$file did not unpack to $dir"
    : > "$vroot/source/$dir/.unpacked"
  fi
}

# ---- the toolchain: a cross build's file is written into the cache, never the repository ----------------
# ensure-built.sh finds it there by name, so `dotnet publish -r <rid>` compiles the worker with the same one.
toolchain=()
case "$rid" in
  win-*)
    case "$rid" in
      win-x64)   triple=x86_64-w64-mingw32;  proc=AMD64 ;;
      win-x86)   triple=i686-w64-mingw32;    proc=X86 ;;
      win-arm64) triple=aarch64-w64-mingw32; proc=ARM64 ;;
    esac
    mingw_version=$(recipe_value LLVM_MINGW_VERSION)
    mingw="${CRF_LLVM_MINGW:-$HOME/.circuitRF-build/toolchains/llvm-mingw-$mingw_version-ucrt-macos-universal}"
    [ -x "$mingw/bin/$triple-clang++" ] \
      || die "$rid is cross-built with llvm-mingw, and there is none at $mingw. Unpack llvm-mingw $mingw_version's release for this OS there, or name its root with CRF_LLVM_MINGW (on Windows, use build.cmd)."
    tc="$vroot/toolchain-$rid.cmake"
    cat > "$tc" <<EOF
# Written by tools/gdstk-worker/build.sh for $rid (llvm-mingw at $mingw).
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR $proc)
set(CMAKE_C_COMPILER $mingw/bin/$triple-clang)
set(CMAKE_CXX_COMPILER $mingw/bin/$triple-clang++)
set(CMAKE_RC_COMPILER $mingw/bin/$triple-windres)
set(CMAKE_FIND_ROOT_PATH $mingw/$triple)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
# Libraries, headers and packages are left at BOTH: ONLY would re-root CMAKE_PREFIX_PATH (the static zlib and
# qhull) under the sysroot and lose them, as tools/geometry-worker/RESOLVED.md found for OCCT. ZLIB_ROOT and
# QHULL_ROOT point at the prefix explicitly.
EOF
    toolchain=("-DCMAKE_TOOLCHAIN_FILE=$tc") ;;
  linux-*)
    if [ "${rid#linux-}" != "$host_arch" ]; then
      case "$rid" in
        linux-x64)   triple=x86_64-linux-gnu;  proc=x86_64;  pkg=crossbuild-essential-amd64 ;;
        linux-arm64) triple=aarch64-linux-gnu; proc=aarch64; pkg=crossbuild-essential-arm64 ;;
      esac
      command -v "$triple-g++" >/dev/null 2>&1 \
        || die "$rid is not this machine's architecture ($host_arch), and there is no $triple-g++ to cross-build it with. Install it (Debian/Ubuntu: sudo apt-get install $pkg)."
      tc="$vroot/toolchain-$rid.cmake"
      printf '# Written by tools/gdstk-worker/build.sh for %s.\nset(CMAKE_SYSTEM_NAME Linux)\nset(CMAKE_SYSTEM_PROCESSOR %s)\nset(CMAKE_C_COMPILER %s-gcc)\nset(CMAKE_CXX_COMPILER %s-g++)\n' \
        "$rid" "$proc" "$triple" "$triple" > "$tc"
      toolchain=("-DCMAKE_TOOLCHAIN_FILE=$tc")
    else
      rm -f "$vroot/toolchain-$rid.cmake"
    fi ;;
  osx-*) rm -f "$vroot/toolchain-$rid.cmake" ;;
esac

# gdstk's own source is compiled with the worker (ensure-built.sh), so it is checked on every run: a cache
# whose source tree was deleted is repaired here rather than reported missing by every build.
command -v tar >/dev/null 2>&1 || die "'tar' is not on PATH"
fetch GDSTK

# ---- the dependencies: zlib and qhull, static, once per RID --------------------------------------------
if [ -f "$rdir/install.json" ]; then
  say "gdstk $GDSTK_VERSION's dependencies for $rid are already in the cache ($rdir); not rebuilding them"
else
  for tool in cmake tar; do
    command -v "$tool" >/dev/null 2>&1 || die "'$tool' is not on PATH; building the gdstk worker needs it (see tools/gdstk-worker/README.md)"
  done
  fetch QHULL
  fetch ZLIB
  src_qhull="$vroot/source/$(recipe_value QHULL_SOURCE_DIR)"
  src_zlib="$vroot/source/$(recipe_value ZLIB_SOURCE_DIR)"

  per_rid=()
  case "$rid" in
    osx-arm64) per_rid=(-DCMAKE_OSX_ARCHITECTURES=arm64 "-DCMAKE_OSX_DEPLOYMENT_TARGET=$(recipe_value OSX_DEPLOYMENT_TARGET)") ;;
    osx-x64)   per_rid=(-DCMAKE_OSX_ARCHITECTURES=x86_64 "-DCMAKE_OSX_DEPLOYMENT_TARGET=$(recipe_value OSX_DEPLOYMENT_TARGET)") ;;
  esac
  if [ "$host_os" = osx ]; then jobs=$(sysctl -n hw.ncpu); else jobs=$(nproc 2>/dev/null || echo 4); fi

  # Only what this script owns is replaced: the cache folder may hold other things (the G0 spike's
  # binaries live in <rid>/spike/).
  rm -rf "$rdir/deps" "$rdir/build-zlib" "$rdir/build-qhull"
  mkdir -p "$rdir"
  deps="$rdir/deps"
  log="$rdir/build.log"
  : > "$log"
  started=$(date +%s)
  run() { echo "+ $*" >>"$log"; "$@" >>"$log" 2>&1 || { tail -n 40 "$log" >&2; die "a step failed; the whole log is $log"; }; }

  # Find nothing outside the prefix handed in: a system zlib or qhull must never be linked.
  hermetic=(-DCMAKE_BUILD_TYPE=Release -DCMAKE_POSITION_INDEPENDENT_CODE=ON "-DCMAKE_PREFIX_PATH=$deps"
            -DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF -DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF)

  say "building zlib $(recipe_value ZLIB_VERSION) for $rid"
  # shellcheck disable=SC2046
  run cmake -S "$src_zlib" -B "$rdir/build-zlib" "${hermetic[@]}" "-DCMAKE_INSTALL_PREFIX=$deps" \
      $(recipe_value ZLIB_CMAKE_OPTIONS) ${per_rid[@]+"${per_rid[@]}"} ${toolchain[@]+"${toolchain[@]}"}
  run cmake --build "$rdir/build-zlib" -j "$jobs"
  run cmake --install "$rdir/build-zlib"

  say "building qhull $(recipe_value QHULL_VERSION) for $rid"
  # shellcheck disable=SC2046
  run cmake -S "$src_qhull" -B "$rdir/build-qhull" "${hermetic[@]}" "-DCMAKE_INSTALL_PREFIX=$deps" \
      $(recipe_value QHULL_CMAKE_OPTIONS) ${per_rid[@]+"${per_rid[@]}"} ${toolchain[@]+"${toolchain[@]}"}
  run cmake --build "$rdir/build-qhull" -j "$jobs" --target qhullstatic_r
  # Only what the static reentrant library needs: its headers and archive. qhull's own install wants every
  # application built too, which the worker never uses.
  mkdir -p "$deps/include/libqhull_r" "$deps/lib"
  cp "$src_qhull"/src/libqhull_r/*.h "$deps/include/libqhull_r/"
  cp "$rdir"/build-qhull/libqhullstatic_r.a "$deps/lib/"
  rm -rf "$rdir/build-zlib" "$rdir/build-qhull"

  elapsed=$(( $(date +%s) - started ))
  cat > "$rdir/install.json" <<JSON
{
  "gdstk": "$GDSTK_VERSION",
  "qhull": "$(recipe_value QHULL_VERSION)",
  "zlib": "$(recipe_value ZLIB_VERSION)",
  "rid": "$rid",
  "gdstk_sha256": "$(recipe_value GDSTK_SHA256)",
  "qhull_sha256": "$(recipe_value QHULL_SHA256)",
  "zlib_sha256": "$(recipe_value ZLIB_SHA256)",
  "built": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "build_seconds": $elapsed,
  "recipe": "tools/gdstk-worker/recipe.env"
}
JSON
  say "gdstk's dependencies for $rid built in ${elapsed} s"
fi

# The worker itself -- compiled and staged by the same script `dotnet build` runs, so there is one worker
# build and not two that can disagree.
args=(--rid "$rid")
[ -n "$strict" ] && args+=("$strict")
[ -n "$dest" ]   && args+=(--dest "$dest")
exec "$here/ensure-built.sh" "${args[@]}"
