#!/usr/bin/env bash
# Builds linux-arm64 (native) and linux-x64 (cross g++) in the crf-gdstk-linux container (Dockerfile.linux),
# with the spike directory and the per-user cache mounted. Docker Desktop only (desktop-linux context).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo="$(cd "$here/../../.." && pwd)"
cache="${CRF_GDSTK_CACHE:-$HOME/.circuitRF-build/gdstk}"
docker --context desktop-linux run --rm -v "$repo:/repo" -v "$cache:/cache" -e CRF_GDSTK_CACHE=/cache-linux \
  crf-gdstk-linux bash -c '
    mkdir -p /cache-linux/1.0.1 && cp /cache/1.0.1/*.tar.gz /cache-linux/1.0.1/
    for rid in '"${*:-linux-arm64 linux-x64}"'; do
      /repo/tools/gdstk-worker/spike/build.sh $rid || exit 1
      mkdir -p /cache/1.0.1/$rid && rm -rf /cache/1.0.1/$rid/spike && cp -r /cache-linux/1.0.1/$rid/spike /cache/1.0.1/$rid/
      cp /cache-linux/1.0.1/$rid/build.log /cache/1.0.1/$rid/build.log
      f=/cache/1.0.1/$rid/spike/gdstk-worker
      file $f
      if [ $rid = linux-x64 ]; then x86_64-linux-gnu-readelf -d $f | grep NEEDED; else readelf -d $f | grep NEEDED; fi
      objdump -T $f 2>/dev/null | grep -o "GLIBC_[0-9.]*" | sort -uV | tail -1 || true
      x86_64-linux-gnu-objdump -T $f 2>/dev/null | grep -o "GLIBC_[0-9.]*" | sort -uV | tail -1 || true
    done'
