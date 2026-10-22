#!/usr/bin/env bash
# brief-emw-1: run one case under the watchdog, into runs/<name>/ with its log beside it.
#   ./run.sh <case> <name> '<json options>' [rss GB, default 5] [minutes, default 10]
# EMERGE_PY: the python of an EMerge environment (default: circuitRF's own install of 3.0.0b2).
set -euo pipefail
cd "$(dirname "$0")"
PY="${EMERGE_PY:-$HOME/.circuitRF/solvers/emerge/3.0.0b2/env/bin/python}"
mkdir -p runs
python3 watch.py --rss-gb "${4:-5}" --minutes "${5:-10}" -- "$PY" cases.py "$1" "$2" "${3:-{\}}" > "runs/$2.log" 2>&1 || true
echo "$2: $(grep -E 'Meshing complete' "runs/$2.log" | tail -1 | sed 's/.*complete! //') $(grep -E 'Port Z0 =' "runs/$2.log" | head -1 | sed 's/.*: //') | $(grep -E '^\{' "runs/$2.log" | tail -1) | $(tail -1 "runs/$2.log")"
