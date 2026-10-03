# `src/Ui/ThreeD` — resolved findings

## Review of brief-em3d-101 (2026-10-03)

- **Save As rewrites the image paths the UNDO HISTORY holds**, not only the document's (`C3dEditorViewModel.RebaseHistoryImages`,
  `IC3dObjectTextEntry`). An entry stores each object as the file spells it, and an image's path is spelled for the `.c3d`'s
  folder: undoing past a Save As into another folder put back a path that resolved against the NEW folder, i.e. nowhere. Each
  entry kind that holds objects (`C3dEdit`, `C3dListsEdit`, `C3dDocumentEdit`, `C3dGroupEdit`, `C3dFilesEdit`) rewrites its own
  text in place; the stack's saved marker is a reference, so it still names the same entry. Text with no image is left byte for
  byte. Other relative references in an object (a STEP part's `File`, an instance's `CellRef`) are NOT rebased by Save As at all —
  that is older than brief 101.
- **Locked was enforced in Object mode only.** A sheet's face is the whole sheet, so Face mode's Move Along Normal, Move and Align
  to Face moved a locked image sheet, and so did Set Coordinates on a vertex. All four refuse with `LockedRefusal` now.
- **The first image placed was `image2`**: `NextFreeName("image1")` increments the trailing number. Placement now takes the
  smallest free number, as `NextName` does.
- **Keep aspect follows the image's pixels** (R-em3d101-3b), in the Inspector and the corner resize; it followed the rectangle's
  present aspect, so a sheet once stretched with Keep aspect off stayed stretched.
- **A numeric Width/Height on an image sheet unbinds the expression that size held**, as the sheet's own Size rows do; an
  expression typed there binds through `SetFieldText` (with Keep aspect off — the other side cannot follow a formula).
- **Delete on a hidden face image's tree row** removes it: a hidden one has no scene object, so the scene selection never had it.
- **Ticking Hidden in a face image's Inspector section deselected it.** Two causes, and the first fix found only the second:
  - **The real one:** when the edited scene is adopted, `RebuildRecordsTree` removes every face-image (and EM boundary) row
    and makes it again. The tree VIEW drops a removed row from its selection and raises an empty one, which cleared the
    selected row, and the Inspector went with it. It now finds the rows selected before by name afterwards, as `RebuildTree`
    does. Any Inspector edit of a selected face image hit it; Hidden is where it showed. A test that reads only the view
    model's `SelectedTreeItem` passes without the fix, because the stale row object is still held there and there is no tree
    view to drop it. The gate asserts the selected row IS the tree's current row object.
  - `TreeRowStillSelected` keeps a row with nothing of its own in the scene selected when the scene selects nothing:
    symmetry planes and thermal boundaries already, now a hidden face image.
- **A face image's Width/Height box left empty is that size unstated**: it follows the other at the image's aspect, and both empty
  is the default, fitted to the face (the offset stays; Fit to Face clears it). An empty box was an error before.
- **The face image's Transparency box stretched to the slider's row height** — the same trap the object's Transparency row
  already notes: a TextBox in a Grid row with a Slider needs `VerticalAlignment="Center"`.
