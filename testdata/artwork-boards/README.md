# Field boards for Create Schematic from Artwork

This folder holds real boards that Create Schematic from Artwork is tested against. They are third-party artwork, so
**nothing in this folder is ever committed except this README**: `.gitignore` ignores every folder under it
(`/testdata/artwork-boards/*/`). On a fresh clone the field tests skip and say why.

## What goes here

One folder per board, under a **generic name** — `board-a`, `board-b`, … — never the board's product name, its maker
or its customer. Inside it, whatever the board is as circuitRF reads it — usually the workspace an import made (the
`.clay`, the `.ctech` beside it, and a `.cws` above them) — and an `expected.json` written by hand from the board.

A board folder without an `expected.json` is passed over.

## `expected.json`

`clay` and `ports` are required; every other key is optional, and a test checks only what the file states. `ports`,
`parts`, `series` and `shunt` are numbers, read exactly. Each line type's count is a range, `[min, max]`: a reading of
real copper is a best attempt.

| Key | Meaning | Read by |
|---|---|---|
| `clay` | The board's `.clay`, relative to this board's folder. | every field test |
| `ports` | Ports found. | `ArtworkRecognitionFieldTests`, `ArtworkFieldBoardTests` |
| `parts` | Two-terminal parts read. | `PartReadingFieldTests`, `ArtworkFieldBoardTests` |
| `series`, `shunt` | How many of those are series and how many shunt. | `PartReadingFieldTests`, `ArtworkFieldBoardTests` |
| `lines` | Line elements by type: `{ "MLIN": [8, 14], "MBEND": [2, 6], "MTEE": [1, 1], … }` — a type not named is not checked. | `LineRecognitionFieldTests`, `ArtworkFieldBoardTests` |
| `silkscreen` | Designators printed on the board: `[{ "refdes": "C3", "x": 12.4, "y": 30.1 }, …]`, the part's centre in mm. | `SilkscreenFieldTests` |
| `apart` | Pairs of designators the copper keeps apart — a line runs between them: `[["FB1", "FB2"]]`. Each pair must share no signal node. | `ArtworkFieldBoardTests` |
| `silkscreenFraction` | The share of `silkscreen` that must be read and placed, 0–1. | `SilkscreenFieldTests` |

`ArtworkFieldBoardTests` also requires that every board recognises with no refusal and that the `.cnl` it gives passes
`circuitrf check` with no error.

```json
{
  "clay": "board/layout/board.clay",
  "ports": 2,
  "parts": 10,
  "series": 4,
  "shunt": 6,
  "lines": { "MLIN": [10, 16], "MBEND": [2, 6], "MTEE": [0, 2] }
}
```
