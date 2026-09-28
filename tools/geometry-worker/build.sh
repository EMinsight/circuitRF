#!/usr/bin/env bash
# Builds OpenCASCADE Technology into the per-user cache (if the cache lacks it), then the geometry worker.
#
#     tools/geometry-worker/build.sh                     this machine's own RID
#     tools/geometry-worker/build.sh --rid osx-x64       another RID this machine can build for
#     tools/geometry-worker/build.sh --arch x64          the same, by architecture (this OS)
#     tools/geometry-worker/build.sh --strict            fail rather than warn (the packaging scripts)
#
# THIS IS THE ONLY THING IN circuitRF THAT FETCHES OCCT, and it is only ever run deliberately
# (brief-em3d-62 R-em3d62-3f). `dotnet build` runs ensure-built.sh, which reads the cache and nothing
# else; a machine that never runs this script builds, tests and runs circuitRF with the geometry kernel
# reported absent. Nothing of OCCT's is ever written into the repository: the archive, the source tree,
# the build tree and the install all live in the cache (R-em3d62-1b).
#
#   cache    ~/.circuitRF-build/occt/<version>/            the verified archive and the unpacked source
#            ~/.circuitRF-build/occt/<version>/<rid>/      build/, install/, and install.json -- written
#                                                          LAST, so an interrupted build is never taken
#                                                          for a finished one
#   override CRF_OCCT_CACHE=/another/root                  (no space in it: see below)
#
# The first build of a RID takes about 5 minutes on 10 cores (brief 61 Q3, Apple M4: 4 m 54 s) and
# ~1.5 GB of build tree; every later run finds install.json and goes straight to the worker, which
# takes seconds.
#
# Cross-building: macOS builds either architecture from either Mac (CMAKE_OSX_ARCHITECTURES). Linux
# builds the other architecture with the distribution's cross g++ (crossbuild-essential-amd64/-arm64),
# or with a CMake toolchain file named by CRF_OCCT_TOOLCHAIN_FILE (used for OCCT and the worker alike).
# Windows uses build.cmd.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
recipe="$here/occt/recipe.env"

say() { echo "geometry-worker: $*"; }
die() { echo "geometry-worker: ERROR: $*" >&2; exit 1; }

# The recipe is READ, not sourced (recipe.env explains why).
recipe_value() {
  local key="$1" k v
  while IFS='=' read -r k v; do
    [ "$k" = "$key" ] && { printf '%s' "$v"; return 0; }
  done < "$recipe"
  return 1
}

OCCT_VERSION=$(recipe_value OCCT_VERSION)       || die "no OCCT_VERSION in $recipe"
OCCT_TAG=$(recipe_value OCCT_TAG)               || die "no OCCT_TAG in $recipe"
OCCT_URL=$(recipe_value OCCT_URL)               || die "no OCCT_URL in $recipe"
OCCT_SHA256=$(recipe_value OCCT_SHA256)         || die "no OCCT_SHA256 in $recipe"
OCCT_SOURCE_DIR=$(recipe_value OCCT_SOURCE_DIR) || die "no OCCT_SOURCE_DIR in $recipe"
OCCT_CMAKE_OPTIONS=$(recipe_value OCCT_CMAKE_OPTIONS) || die "no OCCT_CMAKE_OPTIONS in $recipe"
OCCT_CMAKE_OPTIONS_OSX=$(recipe_value OCCT_CMAKE_OPTIONS_OSX || true)
OCCT_CMAKE_OPTIONS_LINUX=$(recipe_value OCCT_CMAKE_OPTIONS_LINUX || true)
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
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) die "unknown argument '$1' (see --help)" ;;
  esac
done

case "$(uname -s)" in
  Darwin) host_os=osx ;;
  Linux)  host_os=linux ;;
  *) die "this script builds for macOS and Linux; on Windows run tools\\geometry-worker\\build.cmd" ;;
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
  osx-arm64)   target_os=osx;   osx_arch=arm64 ;;
  osx-x64)     target_os=osx;   osx_arch=x86_64 ;;
  linux-arm64) target_os=linux ;;
  linux-x64)   target_os=linux ;;
  win-*) die "$rid is built on Windows, by tools\\geometry-worker\\build.cmd" ;;
  *) die "unknown RID '$rid'" ;;
esac
[ "$target_os" = "$host_os" ] || die "$rid cannot be built on this $(uname -s) machine; build it on its own platform"

case " $KERNEL_RIDS " in
  *" $rid "*) ;;
  *) say "note: $rid is not in recipe.env's KERNEL_RIDS, so no installer ships what this builds." ;;
esac

cache_root="${CRF_OCCT_CACHE:-$HOME/.circuitRF-build/occt}"
# NO SPACE IN THE PATH: autotools and some CMake paths refuse one (em-3d.md 7.2 records it for the
# solver installs), and a build that fails 4 minutes in over a path is worse than refusing here.
case "$cache_root" in
  *" "*) die "the cache path '$cache_root' has a space in it; set CRF_OCCT_CACHE to a path without one" ;;
esac
vroot="$cache_root/$OCCT_VERSION"
rdir="$vroot/$rid"

# The other Linux architecture. A toolchain file named by CRF_OCCT_TOOLCHAIN_FILE wins; otherwise the
# Debian/Ubuntu cross compiler for the target (crossbuild-essential-amd64 / -arm64, which
# packaging/linux/build-linux.sh offers to install) is found and a toolchain file is written for it --
# into the cache, never the repository. Exported, so ensure-built.sh compiles the worker with it too.
# The cross g++ carries its own sysroot, so the file names only the system and the compilers.
toolchain=()
if [ "$target_os" = linux ] && [ "${rid#linux-}" != "$host_arch" ]; then
  if [ -z "${CRF_OCCT_TOOLCHAIN_FILE:-}" ]; then
    case "$rid" in
      linux-x64)   triple=x86_64-linux-gnu;  processor=x86_64;  cross_pkg=crossbuild-essential-amd64 ;;
      linux-arm64) triple=aarch64-linux-gnu; processor=aarch64; cross_pkg=crossbuild-essential-arm64 ;;
    esac
    command -v "$triple-g++" >/dev/null 2>&1 \
      || die "$rid is not this machine's architecture ($host_arch), and there is no $triple-g++ to cross-build it with. Install it (Debian/Ubuntu: sudo apt-get install $cross_pkg), or name a CMake toolchain file with CRF_OCCT_TOOLCHAIN_FILE."
    mkdir -p "$vroot"
    CRF_OCCT_TOOLCHAIN_FILE="$vroot/toolchain-$rid.cmake"
    cat > "$CRF_OCCT_TOOLCHAIN_FILE" <<EOF
# Written by tools/geometry-worker/build.sh for $rid on an $host_arch machine.
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR $processor)
set(CMAKE_C_COMPILER $triple-gcc)
set(CMAKE_CXX_COMPILER $triple-g++)
EOF
    export CRF_OCCT_TOOLCHAIN_FILE
    say "cross-building $rid with $triple-g++"
  fi
  toolchain=("-DCMAKE_TOOLCHAIN_FILE=$CRF_OCCT_TOOLCHAIN_FILE")
fi

if [ -f "$rdir/install.json" ]; then
  say "OCCT $OCCT_VERSION for $rid is already in the cache ($rdir); not rebuilding it"
else
  for tool in cmake make tar; do
    command -v "$tool" >/dev/null 2>&1 || die "'$tool' is not on PATH; building OCCT needs it (see tools/geometry-worker/README.md)"
  done
  command -v c++ >/dev/null 2>&1 || command -v g++ >/dev/null 2>&1 || command -v clang++ >/dev/null 2>&1 \
    || die "no C++ compiler on PATH; building OCCT needs one (see tools/geometry-worker/README.md)"

  sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
    else shasum -a 256 "$1" | awk '{print $1}'; fi
  }

  mkdir -p "$vroot"
  archive="$vroot/OCCT-$OCCT_TAG.tar.gz"

  # An archive already in the cache is re-verified, never trusted by its name.
  if [ -f "$archive" ] && [ "$(sha256_of "$archive")" != "$OCCT_SHA256" ]; then
    say "the cached archive does not match the recipe; fetching it again"
    rm -f "$archive"
  fi

  if [ ! -f "$archive" ]; then
    say "fetching $OCCT_URL"
    if command -v curl >/dev/null 2>&1; then
      curl -fsSL -o "$archive.part" "$OCCT_URL" || { rm -f "$archive.part"; die "the download failed"; }
    elif command -v wget >/dev/null 2>&1; then
      wget -q -O "$archive.part" "$OCCT_URL" || { rm -f "$archive.part"; die "the download failed"; }
    else
      die "neither curl nor wget is on PATH"
    fi
    got="$(sha256_of "$archive.part")"
    if [ "$got" != "$OCCT_SHA256" ]; then
      rm -f "$archive.part"
      die "the downloaded archive's SHA-256 is $got, but the recipe says $OCCT_SHA256. Nothing was built from it."
    fi
    mv "$archive.part" "$archive"
  fi
  say "archive verified: SHA-256 $OCCT_SHA256"

  # Unpacked once per version and shared by every RID; the marker is written last, like install.json.
  src="$vroot/source/$OCCT_SOURCE_DIR"
  if [ ! -f "$vroot/source/.unpacked" ]; then
    rm -rf "$vroot/source"
    mkdir -p "$vroot/source"
    tar -xzf "$archive" -C "$vroot/source"
    [ -f "$src/CMakeLists.txt" ] || die "the archive did not unpack to $OCCT_SOURCE_DIR"
    : > "$vroot/source/.unpacked"
  fi

  if [ "$host_os" = osx ]; then jobs=$(sysctl -n hw.ncpu); else jobs=$(nproc 2>/dev/null || echo 4); fi

  per_rid=()
  if [ "$target_os" = osx ]; then
    per_rid=("-DCMAKE_OSX_ARCHITECTURES=$osx_arch")
    # shellcheck disable=SC2206
    per_rid+=($OCCT_CMAKE_OPTIONS_OSX)
  else
    # shellcheck disable=SC2206
    per_rid+=($OCCT_CMAKE_OPTIONS_LINUX)
  fi

  say "building OCCT $OCCT_VERSION for $rid, once: about 5 minutes on 10 cores, ~1.5 GB in $rdir"
  rm -rf "$rdir"
  mkdir -p "$rdir"
  log="$rdir/build.log"
  started=$(date +%s)

  # The options are split on spaces on purpose: recipe.env holds them as one line.
  # shellcheck disable=SC2086
  if ! { cmake -S "$src" -B "$rdir/build" -G "Unix Makefiles" \
           -DCMAKE_BUILD_TYPE=Release \
           "-DCMAKE_INSTALL_PREFIX=$rdir/install" \
           $OCCT_CMAKE_OPTIONS "${per_rid[@]}" ${toolchain[@]+"${toolchain[@]}"} \
         && cmake --build "$rdir/build" -j "$jobs" \
         && cmake --install "$rdir/build"; } >"$log" 2>&1; then
    tail -n 30 "$log" >&2
    die "the OCCT build failed; the whole log is $log"
  fi

  elapsed=$(( $(date +%s) - started ))
  cat > "$rdir/install.json" <<JSON
{
  "occt": "$OCCT_VERSION",
  "rid": "$rid",
  "sha256": "$OCCT_SHA256",
  "built": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "build_seconds": $elapsed,
  "recipe": "tools/geometry-worker/occt/recipe.env"
}
JSON
  say "OCCT $OCCT_VERSION for $rid built in $((elapsed / 60)) m $((elapsed % 60)) s; the build tree ($rdir/build) may be deleted"
fi

# The worker itself -- compiled and staged by the same script `dotnet build` runs, so there is one
# worker build and not two that can disagree.
args=(--rid "$rid")
[ -n "$strict" ] && args+=("$strict")
[ -n "$dest" ]   && args+=(--dest "$dest")
exec "$here/ensure-built.sh" "${args[@]}"
