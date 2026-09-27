#!/bin/sh
# brief-em3d-61 Q4 -- the Gmsh half. SPIKE MATERIAL. Run after `occt_probe q4 --out ..`.
# Each .geo imports one OCCT 8.0.1-written file, fragments it with an air box, recovers the fillet and
# the top face by a tight box query, and meshes second order. The mesh itself is not kept.
cd "$(dirname "$0")"
gmsh -info 2>&1 | grep -E '^(Version|OCC version)' > gmsh-info.txt
for g in brep-v1 brep-v2 brep-v3 step-um-target-um step-um-default-unit; do
  start=$(python3 -c 'import time; print(time.time())')
  gmsh "$g.geo" -3 -o "/tmp/q4-$g.msh" > "gmsh-$g.log" 2>&1
  code=$?
  end=$(python3 -c 'import time; print(time.time())')
  els=$(grep -Eo 'Info *: [0-9]+ nodes [0-9]+ elements' "gmsh-$g.log" | tail -1)
  printf '%-22s exit=%s  %.2fs  %s\n' "$g" "$code" "$(python3 -c "print($end-$start)")" "$els" >> gmsh-summary.txt
  rm -f "/tmp/q4-$g.msh"
done
