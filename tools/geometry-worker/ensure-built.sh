#!/bin/sh
# Keeps the geometry worker in step with an ordinary `dotnet build` / `dotnet run`.
#
#     ensure-built.sh [--dest <dir>] [--rid <rid>] [--strict]
#
# THIS SCRIPT MUST NEVER FAIL A BUILD, except under --strict (the packaging scripts pass it).
# ----------------------------------------------------------------------------------------------------
# The geometry kernel is optional to BUILDING circuitRF: without it booleans, fillets and STEP are
# disabled and everything else works. So every failure here is reported and swallowed.
#
# IT READS THE CACHE AND NOTHING ELSE (brief-em3d-62 R-em3d62-3f). It never downloads, unpacks or
# configures OpenCASCADE, and with an empty cache it needs no C++ toolchain at all: it prints one warning
# and succeeds. Only tools/geometry-worker/build.sh fetches OCCT, and only when someone runs it.
#
#   cache has OCCT for the RID  compile the worker if its source is newer than it (seconds), stage it
#                               with its library closure in build/<rid>/geometry-kernel/, and copy that
#                               folder to <dest>/geometry-kernel/
#   cache does not              one warning, and success
#
# Skip it from a build with -p:CrfSkipGeometryWorker=true.
set -u

here=$(cd "$(dirname "$0")" && pwd)
recipe="$here/occt/recipe.env"

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

say()  { echo "geometry-worker: $*"; }
# The form MSBuild reads as a WARNING rather than as a line of output, so the build summary counts it.
warn() { echo "geometry-worker : warning GK001 : $*"; }
skip() { warn "$*"; [ "$strict" = 1 ] && exit 1; exit 0; }

version=""
while IFS='=' read -r k v; do
    [ "$k" = OCCT_VERSION ] && version=$v
done < "$recipe"
[ -n "$version" ] || skip "no OCCT_VERSION in $recipe; the geometry kernel is not built."

if [ -z "$rid" ]; then
    case "$(uname -s)" in Darwin) os=osx ;; Linux) os=linux ;; *) os="" ;; esac
    case "$(uname -m)" in arm64|aarch64) arch=arm64 ;; x86_64|amd64) arch=x64 ;; *) arch="" ;; esac
    [ -n "$os" ] && [ -n "$arch" ] || skip "not a platform this script builds for; the geometry kernel is not built."
    rid=$os-$arch
fi

case "$rid" in
    osx-arm64) osx_arch=arm64 ;;
    osx-x64)   osx_arch=x86_64 ;;
    linux-*)   osx_arch="" ;;
    *) skip "$rid is not built by this script; the geometry kernel is not built." ;;
esac

cache="${CRF_OCCT_CACHE:-$HOME/.circuitRF-build/occt}/$version/$rid"
install="$cache/install"

if [ ! -f "$cache/install.json" ]; then
    skip "the geometry kernel is not built for $rid, so booleans, fillets and STEP are disabled. Run tools/geometry-worker/build.sh once (about 5 minutes on 10 cores) to build it."
fi

work="$here/build/$rid"
stage="$work/geometry-kernel"
worker="$stage/geometry-worker"
log="$work/worker-build.log"

# "Newer than the output" is the whole staleness rule, as for senior-worker -- plus the VERSION file,
# which the worker reports, and install.json, which changes when the kernel under it is rebuilt.
stale=0
[ -f "$worker" ] || stale=1
for f in "$here/geometry_worker.cpp" "$here/CMakeLists.txt" "$here/ensure-built.sh" "$here/../../VERSION" "$cache/install.json"; do
    [ "$stale" = 1 ] && break
    [ "$f" -nt "$worker" ] && stale=1
done

if [ "$stale" = 1 ]; then
    command -v cmake >/dev/null 2>&1 || skip "OCCT is in the cache but cmake is not on PATH, so the worker cannot be compiled; the geometry kernel is not built."

    say "building the worker for $rid"
    mkdir -p "$work"
    set -- -S "$here" -B "$work/cmake" -DCMAKE_BUILD_TYPE=Release "-DCMAKE_PREFIX_PATH=$install"
    [ -n "$osx_arch" ] && set -- "$@" "-DCMAKE_OSX_ARCHITECTURES=$osx_arch" -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0
    [ -n "${CRF_OCCT_TOOLCHAIN_FILE:-}" ] && set -- "$@" "-DCMAKE_TOOLCHAIN_FILE=$CRF_OCCT_TOOLCHAIN_FILE"
    if ! { cmake "$@" && cmake --build "$work/cmake" --config Release; } >"$log" 2>&1; then
        tail -n 20 "$log"
        skip "the worker did not compile (the log is $log); the geometry kernel is not built."
    fi

    # ── Stage: the worker and EXACTLY its library closure (R-em3d62-4) ──────────────────────────────
    #
    # The closure is read from the binaries themselves, recursively, rather than listed: every library
    # a staged file names that the OCCT install holds is copied, under THE NAME IT IS ASKED FOR
    # (libTKernel.8.0.dylib, libTKernel.so.8.0), dereferenced -- so the folder holds no symlinks for a
    # .deb or a tarball to lose, and nothing the worker does not load.
    rm -rf "$stage"
    mkdir -p "$stage"
    cp "$work/cmake/geometry-worker" "$worker"

    deps() {
        if [ -n "$osx_arch" ]; then
            otool -L "$1" | tail -n +2 | awk '{print $1}' | sed -n 's|^@rpath/||p'
        elif command -v readelf >/dev/null 2>&1; then
            readelf -d "$1" | sed -n 's/.*(NEEDED).*\[\(.*\)\]/\1/p'
        else
            objdump -p "$1" | awk '$1 == "NEEDED" {print $2}'
        fi
    }

    queue="$worker"
    while [ -n "$queue" ]; do
        set -- $queue
        file=$1; shift; queue="$*"
        for name in $(deps "$file"); do
            [ -f "$stage/$name" ] && continue
            src=$(find "$install" -name "$name" \( -type f -o -type l \) 2>/dev/null | head -n 1)
            [ -n "$src" ] || continue          # a system library: not ours to ship
            cp -L "$src" "$stage/$name"
            chmod 644 "$stage/$name"
            queue="$queue $stage/$name"
        done
    done
    count=$(ls "$stage" | grep -c '^libTK')
    say "staged the worker and $count OCCT libraries in $stage"

    # Answering is the proof the run path works; the cache is never on the search path, so a pass here
    # is a pass from the staged folder alone. Skipped for a binary this machine cannot execute.
    if "$worker" --version >/dev/null 2>&1; then
        say "$("$worker" --version | tr '\n' ' ')"
    else
        case "$(uname -s)-$(uname -m)" in
            Darwin-arm64)   host_rid=osx-arm64 ;;
            Darwin-x86_64)  host_rid=osx-x64 ;;
            Linux-x86_64)   host_rid=linux-x64 ;;
            Linux-aarch64)  host_rid=linux-arm64 ;;
            *)              host_rid="" ;;
        esac
        if [ "$rid" = "$host_rid" ]; then
            rm -rf "$stage"
            skip "the worker compiled but does not run from its own folder; the geometry kernel is not built."
        fi
        say "this $host_rid machine cannot execute a $rid worker, so it was not run here."
    fi
fi

if [ -n "$dest" ] && [ -f "$worker" ]; then
    mkdir -p "$dest"
    rm -rf "$dest/geometry-kernel"
    cp -Rp "$stage" "$dest/geometry-kernel"
fi

exit 0
