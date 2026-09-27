#!/bin/sh
# brief-em3d-61 Q9 -- failure behaviour, one case per process. SPIKE MATERIAL.
# Usage: q9/run.sh <path-to-occt_probe>. Writes q9/run-<rid>.txt: each case's stdout, stderr and exit status.
P="$1"; cd "$(dirname "$0")"
RID=$("$P" selftest | head -1 | awk '{print $NF}')
OUT="run-$RID.txt"; : > "$OUT"
for c in fillet-too-big self-intersecting zero-thickness tool-misses segv-no-handler segv-with-handler break-boolean break-fillet; do
  "$P" q9 "$c" > /tmp/q9.out 2> /tmp/q9.err
  code=$?
  { echo "== $c  exit=$code"; cat /tmp/q9.out; sed 's/^/stderr: /' /tmp/q9.err; echo; } >> "$OUT"
done
rm -f /tmp/q9.out /tmp/q9.err
cat "$OUT"
