#!/usr/bin/env bash
# Runs one harness command against one RID's spike worker in a container, with the repository and the cache
# mounted at their own paths (so every path in the corpus is the same on every RID).
# (No --platform: Docker Desktop files the locally built amd64 image under an arm64 index entry, and asking
# for linux/amd64 makes it try to PULL one. The image runs as amd64 under emulation regardless.)
#   docker/run-check.sh <rid> <command...>        linux-arm64: crf-gdstk-linux; linux-x64, win-x64, win-x86:
#                                                 crf-gdstk-wine (amd64; the Windows ones through wine)
set -euo pipefail
rid="$1"; shift
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo="$(cd "$here/../../.." && pwd)"
cache="${CRF_GDSTK_CACHE:-$HOME/.circuitRF-build/gdstk}"
case "$rid" in
  linux-arm64) image=crf-gdstk-linux; plat=linux/arm64; worker="$cache/1.0.1/$rid/spike/gdstk-worker"; wine=0 ;;
  linux-x64)   image=crf-gdstk-wine;  plat=linux/amd64; worker="$cache/1.0.1/$rid/spike/gdstk-worker"; wine=0 ;;
  win-x64|win-x86) image=crf-gdstk-wine; plat=linux/amd64; worker="wine $cache/1.0.1/$rid/spike/gdstk-worker.exe"; wine=1 ;;
  *) echo "no container runs $rid" >&2; exit 2 ;;
esac
docker --context desktop-linux run --rm -v "$repo:$repo" -v "$cache:$cache" -w "$here" \
  -e CRF_GDSTK_WORKER="$worker" -e CRF_GDSTK_WINE="$wine" -e WINEDEBUG=-all "$image" bash -c "$*"
