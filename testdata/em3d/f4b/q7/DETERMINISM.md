# Q7 — names survive, and the answer does not move

`occt_probe q7` runs nine operations (four subtracts, one unite, a fillet and a chamfer on a bore's rim, a
fillet on a box's four top edges, and a tangent-chain fillet), names every result face from OCCT's history,
names every edge by its two faces, and writes `history-<op>.txt` (the history map, the result faces with
their tight boxes, the edge names) and `hashes-<rid>.txt` (SHA-256 of each result's B-rep, v3 text, no
triangles).

## Determinism

| check | result |
|---|---|
| The whole run twice in one process: B-rep bytes and history tables | **identical**, all 9 |
| The booleans with `SetRunParallel(true)` against serial | **identical**, all 9 |
| A second process (`occt_probe q7 --out <elsewhere>`, then `cmp` of the hash file and every history file) | **identical**, all 9 |
| A second platform | **not done.** The osx-x64 build cannot run on this machine (no Rosetta, `q3/build-osx-x64.log`); Linux and Windows are owed by the owner. `occt_probe selftest` prints the B-rep hash of its filleted part (`53acdaae…89ae` on osx-arm64) precisely so the owner's per-RID runs answer this for free |

One thing that is **not** a pure function of the geometry: a B-rep's text includes each sub-shape's **flag
bits**. `BRepCheck_Analyzer` sets "checked"; a STEP export clears it (`selftest` prints *"STEP export left the
shape's B-rep CHANGED"* — only lines like `0111000` → `0101000` differ). Determinism holds for the **same
sequence of calls**, which is what a worker running one operation per request gives.

## Naming (overview §1g) — what the maps show

- **Blank faces keep bare names; Tool faces are `<tool>:<face>`.** Every result face in all nine operations was
  named, and **none received two names** (`faces unnamed 0, faces with >1 name 0` in every header).
- **A face split in two** (`subtract-slot`: `zmax` → `zmax#1`, `zmax#2`; `subtract-two`: four faces split)
  comes out of `Modified` as a list of pieces. **OCCT's list order is not geometric**: in `unite-pin`,
  `pin:side`'s two pieces come back in the opposite order to a sort by position. The order is stable run to
  run, but it is an implementation detail of one kernel version; the harness numbers pieces by a
  **geometric** order (tight box, lexicographic) and so should the worker.
- **Deleted faces** (`cavity:top`, `slot:zmax`) report `IsDeleted`; `Generated` on a face returns the new
  **edges** of the section, never faces, for a solid boolean.
- **Fillets and chamfers:** `Generated(edge)` returns exactly one face per filleted edge (`fillet(<edge>)`),
  `Modified(face)` the trimmed neighbours; no corner patch was generated from a vertex in any case here
  (the box's four top fillets meet in mitres). **In a tangent chain, OCCT propagates from the edge given to
  every tangent edge** (`fillet-chain`: one edge listed, 8 in the contour). Naming from the listed edge
  alone leaves **7 of 18 faces unnamed**; naming from every edge of the contour (`MakeFillet::Edge(c, i)`)
  names all 18. Nested names occur naturally: `fillet(fillet(xmax|ymin)|zmax)`.
- **Seam edges.** A cylinder's (or torus's) side is one face whose seam edge bounds it on both sides; by the
  two-faces rule its name is `side|side` (`cavity:side|cavity:side`). It is not a real edge of the part and
  cannot be filleted meaningfully.
- **One face pair bounding two edges** happens in ordinary shapes: the trench (`trench:side|zmax`, two
  lines), `subtract-two`, and in Q5's plain unions and the rotated subtract (`same-face-pair.txt` — one pair bounding
  two edges in each of the three Q5 shapes checked; none in the Q6 fillets and chamfers or in the Q4 part).
- **The `#n` suffixes collided.** When a split face also meets another face along two edges, the harness,
  following §1g as first written, wrote `cavity:side|zmax#2#1` and `cavity:side|zmax#2#2` (`subtract-two`).
  Worse than ugly: `x|zmax#2` means *the second edge between x and zmax* in one document and *the edge
  between x and piece 2 of zmax* in another, so a stored fillet edge can land on a **different** edge after an
  edit splits `zmax`, where §1g promises a refusal. `#` is allowed in names (`NameValidator` forbids only
  `< > : " / \ | ? *`). **§1g is corrected** (and brief 64 §2b with it): a repeated pair takes a third `|`
  field, and the history files here now show the corrected form — `cavity:side|zmax#2|1`,
  `trench:side|zmax|1`, `trench:side|zmax|2`.
