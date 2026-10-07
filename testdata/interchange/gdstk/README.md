# gdstk comparison corpus (OASIS, and GDSII from two writers)

This is the fixed data of the gdstk G0 spike (`docs/sonnet-briefs/brief-oasis-gdstk.md` §3 and §8a). The findings
are in `docs/design/oasis-gdstk-findings.md`. **Every file here was written once, by the program named below, and
tests never regenerate it.** Every file is synthetic: no real layout, no fab data, nothing from a PDK, and nothing
from a tool other than gdstk or circuitRF. Each file is under 50 KB.

Assembled by `tools/gdstk-worker/spike/harness/corpus.py` from the spike's runs on 2026-10-06, on osx-arm64.

**Units.** Coordinates in every JSON are integer database units. `grid_per_um` is database units per µm (1000 =
1 nm, 4000 = 0.25 nm). Rotations are in degrees. A `.json` "source" is the spike's canonical form: per cell,
`polygons` (`layer`, `datatype`, flat `xy`), `paths` (`width`, `end` = `flush`/`round`/`halfwidth`/`extended`,
`ext` = [start, end] when extended), `labels` (`texttype`, `text`, `x`, `y`, `anchor`, `rotation`,
`magnification`, `mirror`), and `refs` (`cell`, `x`, `y`, `rotation`, `magnification`, `mirror`, `rep`). A `rep`
is a repetition as gdstk holds it: `rectangular` (columns, rows, spacing), `regular` (v1, v2), `explicit` (offsets),
or `explicit_x`/`explicit_y` (coords).

## `8a/` — the §8a GDSII corpus, from both writers

The `*.circuitrf.gds` files were written by circuitRF's writer **before** `brief-gdsii-native-fixes.md`, so they hold
its old `UNITS` value (the user unit in metres as the first real) and its old `TEXTTYPE 1` port convention. Tests of
the current reader use the `*.gdstk.gds` files.

For each fixture `<name>`:

| File | Written by | From |
|---|---|---|
| `<name>.circuitrf.gds` | circuitRF's own `GdsiiWriter` (`src/Design/Layout/Interchange/`), driven by the spike's `GdsiiDump write` | the source in `<name>.json`, as `InterchangeStructure`s |
| `<name>.gdstk.gds` | gdstk 1.0.1 `Library::write_gds`, through the spike's worker; timestamp fixed at 2026-01-01 | the same source |
| `<name>.gdstk.oas` | gdstk 1.0.1 `Library::write_oas`, through the spike's worker: compression level 6, rectangles and trapezoids detected, CRC32, no standard properties | the same source |
| `<name>.json` | `corpus.py` | the source; each file's writer, size and SHA-256; and **what each reader made of each file** (`q4`: `circuitrf->gdstk` = our file read by gdstk, compared with the source; an empty list = equal under §8b) |

**The simple cases** (§7c, expected to agree): `rectangle`, `polygon7`, `path-ends` (flush, round, half-width),
`labels` (0°, 90°, mirror, magnification 2), `sref-transform` (90°, mirror, magnification 2), `aref-3x2`,
`hierarchy` (a cell referenced twice), `layers-datatypes` (2 × 2), `dbu-1nm`, `dbu-0p25nm` (grid 4000) and
`empty-cell`. All agree in every direction except `labels`. circuitRF's model holds no label mirror or
magnification, so `labels.circuitrf.gds` carries neither, and our reader drops them from gdstk's file.

**The probes** (each one asks a question; the answers are in the findings, § Q4):

| Fixture | What it holds | What happens |
|---|---|---|
| `aref-rotated` | a 3 × 2 AREF at 90° | gdstk writes the column vector along y (legal); **circuitRF's reader places all 6 at the origin** |
| `aref-mirrored` | a 2 × 2 AREF, mirrored | agrees |
| `path-extensions` | `PATHTYPE 4`, [50, 50] and [20, 70] | gdstk keeps [20, 70]; **circuitRF's writer writes [50, 50] and circuitRF's reader turns gdstk's [20, 70] into [50, 50]** (our model extends by width/2 only) |
| `path-1dbu-segment` | a spine with a 1-DBU first segment | agrees (read tolerance 0.5 DBU) |
| `path-odd-width` | width 3 | agrees in GDSII (OASIS write makes it 4, see `oasis-gdstk/`) |
| `label-texttype` | text types 1 and 5 | 5 is lost (becomes 0) in circuitRF's model |
| `label-45deg`, `sref-30deg` | non-orthogonal angles | agree |
| `layer-40000`, `datatype-40000` | values above 32767 | **circuitRF's reader wraps them to −25536**; gdstk reads 40000 |
| `big-polygon-8000` | one 8000-vertex polygon | agrees. **Only the `.gdstk.oas` (489 B) is here**: the two `.gds` files are 64,138 B, over the limit. The vertex formula is in the `.json`. |
| `box-record.gds` | a GDSII `BOX` element beside a `BOUNDARY` | **written byte by byte by the spike's harness** (`q4.py gds_box_file`), because neither writer emits BOX. gdstk reads it as a polygon; **circuitRF's reader drops it silently**. |

## `oasis-gdstk/` — one OASIS feature per file, from gdstk's writer

`q5-<feature>.oas` was written by gdstk 1.0.1 `write_oas` through the spike's worker. Unless the `.json` says
otherwise, the options are compression level 6, rectangles and trapezoids detected, CRC32, and no standard
properties. `q5-<feature>.json` holds the `source` given to the writer, the `options`, what gdstk **reads back**
(`reads_back_as`, read at a tolerance of 0.5 DBU) and the `differences`. For a G4 test, "imports to its stated
content" means `reads_back_as`.

| File | Feature | Read back |
|---|---|---|
| `q5-rectangles` | RECTANGLE, square and not | equal |
| `q5-polygon7` | POLYGON | equal |
| `q5-trapezoids` | TRAPEZOID/CTRAPEZOID (horizontal, vertical, triangle) | equal (checked by gdstk against itself only) |
| `q5-paths` | PATH: flush, half-width, extended [20, 30], round, width 3, a 1-DBU segment | **round → flush, width 3 → 4**; the rest equal |
| `q5-labels` | TEXT with text type 3, rotation 90, magnification 2 + mirror | **rotation, magnification and mirror dropped** (OASIS TEXT has none) |
| `q5-placements` | PLACEMENT at 0/90/180/270°, mirror, magnification 2, 30° | equal |
| `q5-ref-repetitions` | placement repetitions: rectangular (including a negative pitch), regular (non-orthogonal), explicit, explicit_x, explicit_y | equal. **Written without the gdstk#247 row** (a negative explicit offset, which gdstk writes as about 2⁶⁴ and which then reads back differently by libm). The spike's `runs/q5` keeps the original. |
| `q5-shape-repetition` | a RECTANGLE carrying a 3 × 2 repetition | equal, held as a repetition, not expanded |
| `q5-hierarchy` and `-deflate0`, `-deflate9`, `-checksum32`, `-stdprops` | a 3-level hierarchy and an empty cell, with CBLOCK at 0/6/9, checksum32, standard properties on | equal |

## `oasis-hand/` — OASIS encoded byte by byte (the independent half)

`hand-<feature>.oas` was written by `tools/gdstk-worker/spike/harness/oasis_hand.py`, circuitRF's own OASIS encoder
in the spike's harness. It is **not** gdstk, so these files hold encodings gdstk's writer never emits. Beside each
is `hand-<feature>.expected.json`, the content an ideal reader produces. gdstk 1.0.1 reads all of them exactly:

`hand-repetitions` (repetition types 0–11), `hand-shape-repetitions`, `hand-paths` (extension schemes, point lists
0–5), `hand-placements` (PLACEMENT_TRANSFORM, 30°, magnification), `hand-modal` (implicit modal variables,
XYRELATIVE), `hand-names` (reference-number names, strict tables), `hand-layernames` (LAYERNAME, every interval
type), `hand-cblock`, `hand-xrecords` (XNAME, XELEMENT, XGEOMETRY, PROPERTY: read to the end, and carried by
nothing), and `hand-grid4000` (0.25 nm, a coordinate near 2³¹).

`hand-circle.oas` holds two CIRCLE records, r = 50 µm at the origin and r = 0.5 µm at (200 µm, 0). Its
`.expected.json` states the records and what gdstk makes of them at a tolerance of 0.5 DBU: polygons of 703 and 71
vertices, with off-grid vertices.
