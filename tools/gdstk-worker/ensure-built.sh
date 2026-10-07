#!/bin/sh
# Keeps the gdstk worker in step with an ordinary `dotnet build` / `dotnet run`.
#
#     ensure-built.sh [--dest <dir>] [--rid <rid>] [--strict]
#
# THIS SCRIPT MUST NEVER FAIL A BUILD, except under --strict (the packaging scripts pass it).
# ----------------------------------------------------------------------------------------------------
# The gdstk worker is optional to BUILDING circuitRF: without it the (gdstk) import and export commands and
# `convert`'s oasis format are disabled and everything else works (D6). So every failure here is reported
# and swallowed.
#
# IT READS THE CACHE AND NOTHING ELSE (brief-oasis-gdstk.md §4c). It never downloads, unpacks or builds
# gdstk's dependencies, and with an empty cache it needs no C++ toolchain at all: it prints one warning and
# succeeds. Only tools/gdstk-worker/build.sh (build.cmd) fetches anything, and only when someone runs it.
#
#   cache has the RID   compile the worker if its source is newer than it (seconds), check it links only
#                       system libraries, stage it in build/<rid>/gdstk-kernel/, and copy that folder to
#                       <dest>/gdstk-kernel/
#   cache does not      one warning, and success
#
# Skip it from a build with -p:CrfSkipGdstkWorker=true.
set -u

here=$(cd "$(dirname "$0")" && pwd)
recipe="$here/recipe.env"

dest=""
rid=""
strict=0
while [ $# -gt 0 ]; do
    case "$1" in
        --dest)   dest=${2:-}; shift 2 ;;
        --rid)    rid=${2:-}; shift 2 ;;
        --strict) strict=1; shift ;;
        *) shift ;;
    esac
done

say()  { echo "gdstk-worker: $*"; }
# The form MSBuild reads as a WARNING rather than as a line of output, so the build summary counts it.
warn() { echo "gdstk-worker : warning GD001 : $*"; }
skip() { warn "$*"; [ "$strict" = 1 ] && exit 1; exit 0; }

value() {
    while IFS='=' read -r k v; do
        [ "$k" = "$1" ] && { printf '%s' "$v"; return 0; }
    done < "$recipe"
    return 1
}
version=$(value GDSTK_VERSION) || skip "no GDSTK_VERSION in $recipe; the gdstk worker is not built."
source_dir=$(value GDSTK_SOURCE_DIR) || skip "no GDSTK_SOURCE_DIR in $recipe; the gdstk worker is not built."
osx_target=$(value OSX_DEPLOYMENT_TARGET) || osx_target=13.0

case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)   host_rid=osx-arm64 ;;
    Darwin-x86_64)  host_rid=osx-x64 ;;
    Linux-x86_64)   host_rid=linux-x64 ;;
    Linux-aarch64)  host_rid=linux-arm64 ;;
    *)              host_rid="" ;;
esac
[ -n "$rid" ] || rid=$host_rid
[ -n "$rid" ] || skip "not a platform this script builds for; the gdstk worker is not built."

exe=""
osx_arch=""
case "$rid" in
    osx-arm64) osx_arch=arm64 ;;
    osx-x64)   osx_arch=x86_64 ;;
    linux-*)   ;;
    win-*)     exe=".exe" ;;
    *) skip "$rid is not built by this script; the gdstk worker is not built." ;;
esac

vroot="${CRF_GDSTK_CACHE:-$HOME/.circuitRF-build/gdstk}/$version"
cache="$vroot/$rid"
gdstk_src="$vroot/source/$source_dir"
toolchain="$vroot/toolchain-$rid.cmake"

if [ ! -f "$cache/install.json" ] || [ ! -f "$gdstk_src/.unpacked" ]; then
    skip "the gdstk worker is not built for $rid, so OASIS and the (gdstk) GDSII route are disabled. Run tools/gdstk-worker/build.sh once (well under a minute) to build it."
fi
case "$rid" in
    win-*) [ -f "$toolchain" ] || skip "$rid needs the toolchain file tools/gdstk-worker/build.sh --rid $rid writes; the gdstk worker is not built." ;;
esac

work="$here/build/$rid"
stage="$work/gdstk-kernel"
worker="$stage/gdstk-worker$exe"
log="$work/worker-build.log"

# "Newer than the output" is the whole staleness rule, as for the geometry worker -- plus the VERSION file,
# which the worker reports, and install.json, which changes when the dependencies under it are rebuilt.
stale=0
[ -f "$worker" ] || stale=1
for f in "$here/gdstk_worker.cpp" "$here/CMakeLists.txt" "$here/ensure-built.sh" "$here/windows/gdstk-worker.manifest" \
         "$here/windows/gdstk-worker.rc" "$here/../../VERSION" "$cache/install.json"; do
    [ "$stale" = 1 ] && break
    [ "$f" -nt "$worker" ] && stale=1
done

if [ "$stale" = 1 ]; then
    command -v cmake >/dev/null 2>&1 || skip "gdstk's dependencies are in the cache but cmake is not on PATH, so the worker cannot be compiled; the gdstk worker is not built."

    say "building the gdstk worker for $rid"
    mkdir -p "$work"
    deps="$cache/deps"
    set -- -S "$here" -B "$work/cmake" -DCMAKE_BUILD_TYPE=Release "-DGDSTK_SOURCE_DIR=$gdstk_src" \
        "-DCMAKE_PREFIX_PATH=$deps" "-DZLIB_ROOT=$deps" "-DQHULL_ROOT=$deps" \
        -DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF -DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF
    [ -n "$osx_arch" ] && set -- "$@" "-DCMAKE_OSX_ARCHITECTURES=$osx_arch" "-DCMAKE_OSX_DEPLOYMENT_TARGET=$osx_target"
    [ -f "$toolchain" ] && [ "$rid" != "$host_rid" ] && set -- "$@" "-DCMAKE_TOOLCHAIN_FILE=$toolchain"
    if ! { cmake "$@" && cmake --build "$work/cmake" --config Release --target gdstk-worker; } >"$log" 2>&1; then
        tail -n 20 "$log"
        skip "the worker did not compile (the log is $log); the gdstk worker is not built."
    fi

    rm -rf "$stage"
    mkdir -p "$stage"
    cp "$work/cmake/gdstk-worker$exe" "$worker"

    # ── D3: one statically linked file, so it may name nothing but the system's own libraries ───────────
    # A build that picked up a shared zlib, or a toolchain that dropped -static, would run on this machine
    # and fail on a user's. Read from the binary itself; a check this machine has no tool for is skipped.
    foreign=""
    case "$rid" in
        osx-*)
            foreign=$(otool -L "$worker" | tail -n +2 | awk '{print $1}' | grep -v -e '^/usr/lib/' -e '^/System/') ;;
        linux-*)
            # The host's readelf reads either architecture's ELF.
            if command -v readelf >/dev/null 2>&1; then
                foreign=$(readelf -d "$worker" | sed -n 's/.*(NEEDED).*\[\(.*\)\]/\1/p' \
                          | grep -v -e '^libc\.so' -e '^libm\.so' -e '^ld-linux' -e '^libpthread\.so' -e '^libdl\.so')
            fi ;;
        win-*)
            objdump=$(sed -n 's/^set(CMAKE_CXX_COMPILER \(.*\)\/bin\/.*$/\1/p' "$toolchain")/bin/llvm-objdump
            if [ -x "$objdump" ]; then
                foreign=$("$objdump" -p "$worker" | sed -n 's/.*DLL Name: //p' \
                          | grep -v -i -e '^kernel32\.dll$' -e '^api-ms-win-crt-')
            fi ;;
    esac
    if [ -n "$foreign" ]; then
        rm -rf "$stage"
        skip "the worker links libraries that are not the system's own ($(echo $foreign)); it must be one static file (D3). The gdstk worker is not built."
    fi

    # Answering is the proof it runs; skipped for a binary this machine cannot execute.
    if [ "$rid" = "$host_rid" ]; then
        if "$worker" --version >/dev/null 2>&1; then
            say "$("$worker" --version | tr '\n' ' ')"
        else
            rm -rf "$stage"
            skip "the worker compiled but does not run; the gdstk worker is not built."
        fi
    else
        say "staged the $rid worker ($(wc -c < "$worker" | tr -d ' ') bytes); this $host_rid machine cannot execute it, so it was not run here."
    fi
fi

# ── Copy to <dest>: only when it differs, and never in place (the geometry worker's rule) ─────────────────
# A circuitRF already running from <dest> may start its worker from this folder at any moment, so an
# unchanged folder is left alone and a changed one is copied beside it and renamed into place.
if [ -n "$dest" ] && [ -f "$worker" ]; then
    mkdir -p "$dest"
    target="$dest/gdstk-kernel"
    if ! diff -rq "$stage" "$target" >/dev/null 2>&1; then
        fresh="$dest/.gdstk-kernel.new.$$"
        old="$dest/.gdstk-kernel.old.$$"
        rm -rf "$fresh" "$old"
        if cp -Rp "$stage" "$fresh"; then
            [ -e "$target" ] && mv "$target" "$old"
            mv "$fresh" "$target"
            rm -rf "$old"
        else
            rm -rf "$fresh"
            warn "the gdstk worker could not be copied to $target; the one there is left as it was."
        fi
    fi
fi

exit 0
