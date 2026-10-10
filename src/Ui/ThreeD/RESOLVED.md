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

## brief-em3d-101 follow-ups (2026-10-03)

- **A face image now follows its object's drag** (`C3dEditorViewModel.FaceImagesFollow`). A face image is a scene record of its
  own (`image:object/face`), so the preview's moving set — built from the dragged objects' own scene objects — left it where the
  object was until the drop. It is matched by scene name, the object half split at the last `/` (`C3dModelled.ObjectOfFace`), so
  a placed cell's own face images follow the instance too; the image draw follows because `AddImage` keys its preview on the
  record's id. **Face-boundary tints still do not follow**: a tint is named after its boundary, not its object, and one boundary
  can span faces of several objects, so there is no name to match without carrying the owners on the tint.
- **An image placed while the pane has no usable size** (under `MinPlacementPixels` on a side: not laid out yet, or squeezed to a
  sliver) is sized and centred as the default 800 × 500 view would place it. A sliver's aspect collapsed the visible width and
  the sheet came out at the snap minimum. `Viewer3DViewModel.Resized` also no longer takes an aspect of 0 from a pane with no
  width, which a later Fit would have framed against.
- **"Drop to place…" is taken back when a file drag leaves without dropping** (`EndFileDrag`), restoring what the status line
  said before the drag, unless something else has written to it since.

## 3D editor keys only worked while the canvas had focus (2026-10-03)

**Symptom.** Pressing 1 (Isometric), or any toolbar key in the tooltips, did nothing in the 3D editor.

**Cause.** Every key is `Viewer3DViewModel.HandleKey`, reached only from `Viewer3DPane.OnKeyDown`, and the pane took focus only
on a pointer press inside it (and once, on the view's first attach). A toolbar click focuses the BUTTON, so the next key went
to it and was dropped; returning to an already-attached 3D tab focused nothing, because `C3dEditorView` never subscribed to
`ActivationFocusRequested` as the layout editor does. The same thing made image sheets seem hard to move: G after the
toolbar's Insert Image… went to that button.

**Fix.** Three parts, in `C3dEditorView`: a toolbar `Button.ClickEvent` hands focus back to the pane (the layout editor's
`OnToolButtonClick`; a button with a flyout keeps it); `ActivationFocusRequested` focuses the pane; and a bubbling KeyDown nobody
in the view handled is forwarded through `Viewer3DPane.ForwardKey` — never from a TextBox/ComboBox/NumericUpDown/AutoCompleteBox,
and never Delete/Backspace, so a key pressed on another control cannot delete geometry. No window menu was intercepting:
the macOS `NativeMenu` 3D items carry no `Gesture`, and the in-window `InputGesture`s are display-only.

## Make Port ▸ Wave on a face that is not a rectangle — brief-em3d-121 (2026-10-06)

**The coax face was never greyed out for the reason brief 121 gives.** Brief 121 expected *Make Port ▸ Wave* on the
Launch's bore face to be refused by the port's overlap rule. The editor refused it earlier: `PortFromFace` requires a face
whose area equals its bounding rectangle's, and the bore's end is a disc ("Face bottom is not a rectangle"). A wave port's
rectangle is only a region of the box's face, so a wave port now takes any flat, axis-normal face as the rectangle round it
(`WaveRegion`): a cylinder's cap exactly, from the elaborated primitive's bounds (its tessellated polygon falls short of the
circle on one axis or the other), and any other face from its tessellation's corners. A lumped port still needs a
rectangular face.

**That square is not the shipped Launch's port**, measured once through brief 117's gate 4: on the 1.34 mm square, openEMS's
default grid left the current probe round the pin 106 µm from the housing where it needs a cell (153 µm), and the run was
refused; Palace ran but its Draft |S| moved by up to 1.9 dB (S22 at 14 GHz), from the different Gmsh geometry. The 2 × 2 mm
rectangle's grid lines at ±1 mm are what give the probe its room. The owner chose to ship P1 as the Port tool draws it,
2 × 2 mm round the axis, stating no ends and no path; it lowers to the problem the runs were recorded with.

**The status line names why Make Port chose the reference.** `AddPort` resolved the port it had just written, whose
`Reference` is then stated, so the reason read "the reference is stated". `MakePortFromFace` passes `TerminalsFor`'s reason
through (`AddPort(port, referenceWhy)`).

## Import STEP: a header row per product of several solids, and the group (2026-10-09, brief-em3d-128)

The dialog draws `Table` — each header row (`StepImportProductRow`) followed by its solids, which are ordinary
`StepImportRow`s indented — while `Rows` keeps every importable row for the logic. The header's check box is three-state
(every closed solid, none, some) and its combo is blank when its solids disagree; both act through `StepImport`'s
`SetProductImport`/`SetProductMaterial`, so the CLI and the gate reach the same rule. The group field shows only when the
file has more than one row, and a reload's offer of new parts imports them loose: the file's group is already in the view.

The commit needed no new selection code: selecting every object the import made is, by `C3dGroups.Units`, the group taken
whole. `StepImportGroupUxTests` proves that, a view pick on a piece taking the package, the group's Corner moving every
piece by one step, and a member's tree-row Material changing that member alone, each one undo entry.
