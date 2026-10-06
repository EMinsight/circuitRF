#!/bin/zsh
# run.sh <name> '<gen.py json>' '<cfg.py json>' — mesh, config, Palace on 8 ranks, into runs/<name>/.
# Needs `palace` (the spack wrapper) and its `mpirun` on PATH, and Gmsh's Python module importable
# (Homebrew's gmsh puts gmsh.py in /opt/homebrew/lib: export PYTHONPATH=/opt/homebrew/lib).
# An optional env.sh beside this script is sourced first, for a machine's own PATH; it is never committed.
S=${0:A:h}; [[ -f $S/env.sh ]] && source $S/env.sh
D=$S/runs/$1; mkdir -p $D; cd $D
python3 $S/gen.py mesh.msh "$2" > gen.txt || exit 1
python3 $S/cfg.py config.json "$3" || exit 1
/usr/bin/time -l palace -np 8 config.json > palace.log 2> time.txt
tail -3 gen.txt; grep "ND (p" palace.log | head -1; grep "^Total  " palace.log | head -1
