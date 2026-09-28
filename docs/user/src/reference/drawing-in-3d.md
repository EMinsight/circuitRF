---
title: The 3D Editor
slug: reference/drawing-in-3d.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > The 3D editor
lede: Drawing a cell in three dimensions — boxes, cylinders, faces, booleans, fillets, STEP parts, placed cells, bond wires and ports — in circuitRF's own 3D editor, which needs no solver to draw anything. Taught through two examples, an MMIC in a lidded package and a coaxial connector launching onto a board.
keywords: 3D editor, boolean, subtract, unite, intersect, keep tools, fillet, chamfer, edge mode, STEP, STEP import, STEP export, connector, connector launch, coaxial, OpenCASCADE, 3D view, c3d, drawing in 3D, solid model, CAD, box, cylinder, sheet, polygon, extrude, push pull, move along normal, face, vertex, snap, drawing plane, grid, typed dimensions, expressions, parameters, variables, array, align, gizmo, hierarchy, place cell, die attach, swap view, push in, flatten, group into cell, bond wire, wire, ball bond, wedge bond, loop height, port, lumped port, wave port, air box, boundary, package, lid, cavity, cavity resonance, eigenmode, MMIC, Palace, openEMS, no solver needed
---

The **3D editor** is where a cell's **3D view** is drawn: solids, sheets, bond wires and ports in three
dimensions, with the cells they are built from placed inside them. It is **part of circuitRF**. It is not
a front end to Palace or openEMS, and **it needs no solver installed**. Everything on this page up to
[Simulating](#simulate) — drawing, editing, placing cells, wires, ports, setups, `check`, `explain` and
section pictures — works on a machine with no 3D solver at all. A solver is needed for one thing: pressing
**Run**. When you do, circuitRF offers to install the one the setup names (see
[3D EM ▸ Getting the solvers](em-3d.html#getting)), and [EM Solvers](em-solvers.html) says which one suits
which problem.

This page teaches the editor through the **3D Package** example — an MMIC die in a ceramic package with a
lid — which was drawn with exactly the gestures below. Open it with **Tools ▸ Examples ▸ 3D Package**. The
**3D Connector** example teaches the operations the geometry kernel adds — booleans, fillets and STEP
parts — through a coaxial connector launching onto a board ([below](#connector)).

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#view">The 3D view of a cell</a></li>
<li><a href="#example">The example: an MMIC in a package</a></li>
<li><a href="#connector">The second example: a connector launch</a></li>
<li><a href="#selecting">Selecting</a></li>
<li><a href="#snapping">Snapping</a></li>
<li><a href="#drawing">Drawing</a></li>
<li><a href="#editing">Moving and editing</a></li>
<li><a href="#expressions">Dimensions as expressions</a></li>
<li><a href="#operations">Booleans, fillets, chamfers and STEP parts</a></li>
<li><a href="#step">STEP: importing and exporting</a></li>
<li><a href="#hierarchy">Hierarchy: placing cells</a></li>
<li><a href="#wires">Bond wires</a></li>
<li><a href="#simulate">Simulating</a></li>
<li><a href="#lid">The lid's resonance</a></li>
<li><a href="#headless">Headless</a></li>
<li><a href="#keys">Keys at a glance</a></li>
<li><a href="#reference">Every control, in detail</a></li>
</ol>
</nav>

## The 3D view of a cell {#view}

A cell can have up to four views: a **schematic** (what it does), a **symbol** (how it is placed in a
schematic), a **layout** (its artwork, layer by layer) and a **3D view** (its solid model). The 3D view is
a file of its own, `<cell>/3d/<name>.c3d`, a small JSON document you can read and diff, and the Project
panel lists it under the cell like the others. **File ▸ New 3D Design…** makes a cell with an empty one,
**New 3D View** on a cell's menu in the Project panel adds one to an existing cell, and **New 3D View from Layout** makes one
holding the cell's own layout, ready to extend — the quickest way to take a layout you have already set up
for EM into 3D.

- **What it holds.** Boxes, prisms, cylinders, sheets, polyhedra and bond wires, each made of a named
  material; instances of other cells' layouts or 3D views; ports; boundaries on faces; VARs; and any number
  of EM setups. Everything a 3D solve needs is in the one file.
- **Units.** Every coordinate is stored as a whole number of **database units** (DBU; 1,000 per µm by
  default), exactly as a layout's are, so nothing drifts when you save and reopen. The **display unit** —
  the unit you read and type in — is chosen with the **Unit** box on the toolbar. It starts as the cell
  layout's unit, or the technology's. Changing it moves nothing. The example is drawn in **mil**; its die's
  layout is in **µm**. Both are exact.
- **Materials** come from the cell's technology (**Assign Material…** and the toolbar's material box list
  them; a technology's material libraries, `.cmat` files, are where new ones are kept — see
  [File formats](file-formats.html)). A placed cell brings its **own**
  technology's materials with it — see [Hierarchy](#hierarchy).
- **The editor and the viewer are one pane.** The 3D view a solver result opens in, **Show 3D** on a setup,
  and this editor share the camera, the clip plane, the mesh and field displays, and every key described in
  [3D EM ▸ The 3D view](em-3d.html#view). The editor adds the drawing.

The camera is the viewer's: drag to orbit; right-, middle-, Alt- or Shift-drag to pan; scroll to zoom;
**Home** fits; **1** is isometric, **2**–**7** the six orthographic views, **P** toggles perspective; **C**
turns on the clip plane. A new 3D view opens orthographic. The line along the bottom of the view is the
**status line**: what the armed tool wants next, or what the last edit did, or why it was refused.

## The example: an MMIC in a package {#example}

**Tools ▸ Examples ▸ 3D Package** copies a workspace with two cells to a folder you choose, and opens its
`README.md`, which carries the numbers quoted here.

- **Thru die** — an MMIC's **layout**, drawn in **µm** on a GaAs technology (`tech/mmic-gaas.ctech`: 100 µm
  of GaAs, two gold metals, a gold back side). A 1000 × 600 µm die with a 75 µm line between two 100 µm pads.
- **Package** — a **3D view**, drawn in **mil** on a ceramic-package technology
  (`tech/ceramic-package.ctech`: alumina, gold and a lid alloy). A gold floor; 5 mil of alumina on it; a
  gold die-attach pad on four gold vias; two 5 mil gold leads; the **Thru die** layout placed on the attach
  pad as instance **U1**; a ball–wedge gold wire from each die pad to its lead; a port at each lead's outer
  end; four gold walls; and a lid.

{{ui: em3d-package-3d-open}}

{{ui: em3d-package-3d-closed}}

{{ui: em3d-package-3d-section}}

The cavity is **400 × 320 mil and 40 mil high** — three **cell parameters**, `cav_w`, `cav_l` and `cav_h`,
that the floor, the base, the walls and the lid are all written in terms of, so changing one redraws the
package (see [Dimensions as expressions](#expressions)). The die, its pad, the leads, the wires and the
ports do not depend on them: a bigger cavity leaves the die where it is and moves the walls out.

## The second example: a connector launch {#connector}

**Tools ▸ Examples ▸ 3D Connector** is a coaxial connector launching onto a 50 Ω microstrip at a board's edge —
the 3D problem an RF board meets most — and it is built from the operations the geometry kernel adds:

- **Board** — a layout in **mil**: 20 mil of PTFE-glass laminate over a ground plane, with a 62 mil line.
- **Flange** — a 3D view in **mm**: a plate with a hole, a **Subtract** of a cylinder from a box. It is the source
  of the STEP file below, standing in for the model a connector's maker would send.
- **Launch** — a 3D view in **mm**: the board placed from its layout; a housing with its **bore subtracted and
  kept** as the PTFE fill (*Keep tools*); the flange **imported from STEP** (`flange.step`, exported from Flange)
  and **united** with the housing; a centre pin whose tip is **filleted** — a fillet on a curved edge; and two
  ports and two setups, one for **Palace** and one for **openEMS**.

{{ui: em3d-connector-3d}}

{{ui: em3d-connector-section}}

Its `README.md` lists the gestures that drew it, in order — Box, Cylinder, *Boolean ▸ Subtract…* with the Tool
selected first and *Keep tools* ticked, *Export STEP…* and *Import STEP…*, *Boolean ▸ Unite…*, **E** and
*Fillet…* — and what each run measured. The experiment it exists for is one click: untick **Enabled** on the pin's
fillet row in the object tree and Simulate again. Palace meshes the fillet as a curved surface; openEMS, whose grid
cell at the tip is wider than the fillet's radius, says before the run that it will not represent it. What each
makes of it is in [EM Solvers ▸ What each solver sees of curved geometry](em-solvers.html#curved).

## Selecting {#selecting}

**O**, **F**, **E** and **V** choose what a click selects — a whole **object**, one **face**, one **edge**, or
one **vertex** — and the toolbar has a button for each. What is under the cursor is highlighted; a click selects
it, **Shift**-click adds or removes, **Esc** clears.

- **Edge mode** (**E**) picks the edge nearest the cursor that you can see. An edge is a whole run between two
  faces — a cylinder's rim is one edge, not a ring of segments — named by those two faces: `xmax|zmax`,
  `bottom|side`, `bore:side|zmax`. Where one pair of faces meets in more than one place the name takes a number,
  `side|zmax|1`, `side|zmax|2`, counted in the object's own frame, so moving or turning it renames nothing.
  **Double-click** an edge to take its **tangent chain** — every edge that carries on smoothly from it (within a
  degree), the loop round a rounded top — and **Shift**-double-click to add it; a chain that meets a fork stops
  there and says how many ways it could go. Properties shows an edge's faces, kind and length, and a circle's or
  an arc's radius and centre; several edges, their count and total length. Edge mode needs no geometry kernel.

- **B** selects the next thing **behind** the selection along the line of sight through the cursor, and
  **Shift+B** steps back toward you; the status line says where you are (`Face zmin · "base" · 2 of 5`).
  In the example: select the lid in Object mode, press **B**, and the selection passes through the lid to
  the wire, the die and the floor in turn — the way to reach something inside a closed package without
  hiding anything.
- **Right-click** opens the menu for what is selected: *Hide*, *Isolate*, *Show All*, *Select Owning
  Object*, and the commands this page describes. **Hide** the lid and the walls to work inside the package;
  **Show All** brings them back. Hiding changes only the picture — the solver still sees everything.
- The **Objects** tree lists everything by material or by type — and your [groups](#groups) first; a click there selects in the view, and the
  **Properties** panel shows the selection's name, material, dimensions and position, every one of them
  editable.

## Snapping {#snapping}

The cursor snaps to a **vertex** (a square marker), an edge's **midpoint** (a triangle), the nearest point on
an **edge** (an ×), a face's **centre** (a circle), and otherwise to the **grid** (a small +) on the
drawing plane. A vertex wins over anything else within reach. Snapping reaches **into placed cells**: the
corner of a pad on the die, inside U1, is a target in the package. Only what you can see is a target,
except through a translucent object or with the clip plane on.

The magnet button turns snapping off and on, and the buttons beside it turn each kind on and off; these are
your settings, not the document's. Hold **Alt** (**Option** on a Mac) to suspend geometry snapping while
drawing; the grid still applies. The **Snap** box beside **Unit** sets the grid step (the example uses
0.5 mil); changing it moves nothing already drawn. A **≈** before a coordinate means the point is not
exactly on the database-unit grid (a corner of something rotated by an odd angle).

## Drawing {#drawing}

**The drawing plane.** Shapes are drawn on a plane — **XY**, **YZ** or **XZ** at an offset along its
normal — chosen from the toolbar's plane box and offset field (a bare number is the display unit; `25um`
or `10mil` says its own), from *3D ▸ Drawing Plane*, or from geometry: with a tool armed, **Ctrl**-click
(**Cmd**-click on a Mac) a face to draw on it, or **Ctrl/Cmd+Shift**-click a snapped point to move the
offset there. A faint grid lies on the plane; moving the camera never moves it.

**The tools** are on the toolbar, under *3D ▸ Draw*, and at the cursor: press **Shift+A**, then one letter.

| Letter | Tool | Clicks |
|---|---|---|
| **B** | Box | a corner, the opposite corner, then the height |
| **S** | Sheet | two corners (a conducting rectangle with a thickness) |
| **G** | Polygon | a click per vertex; click the first, press Enter or double-click to close |
| **L** | Polyline | construction lines, never solved; Enter or a double-click ends one |
| **Y** | Cylinder | the centre, the radius, then the height |
| **P** | Port | two corners on the plane (see [Simulating](#simulate)) |
| **W** | Wire | two pads, then the loop height (see [Bond wires](#wires)) |
| **H** | Heat source | two corners on the plane, then its name and power (see [Temperature](#thermal)) |
| **T** | Probe | a click on a face: the temperature there (see [Temperature](#thermal)) |
| **M** | Mesh region | a box, then the element size inside it (see [Temperature](#thermal)) |

**Typing instead of clicking.** While a shape is in progress, type a digit and a box opens at the cursor
showing what the next click would set — width and depth, then height (or radius, then height). **Tab**
moves between the fields, **Enter** accepts, **Esc** goes back to the mouse. A value can carry its own unit
(`127um` while drawing in mil) and can be an expression (see [Dimensions as expressions](#expressions)).
Each new object takes the toolbar's material and a name you can change (`box1`, `cyl1`, …), and each is one
undo step. **Esc** cancels a shape; pressed again it puts the tool down.

**How the example's package was drawn.** With the material box on *Gold* and the XY plane at 0, **Shift+A
B**, one click anywhere, then typed: `cav_w + 2*wall` **Tab** `cav_l + 2*wall` **Enter**, `floor_t`
**Enter**. That is the floor's size; its corner was typed in Properties (`-cav_w/2 - wall`,
`-cav_l/2 - wall`, `-floor_t`), and its name (`floor`) there too. The alumina base, the attach pad, the
leads, the walls and the lid are drawn the same way. The four vias are one **cylinder** (radius `2`, height
`5`) and *Array…* (right-click): 2 × 2 at 36 × 20 mil. The walls and the lid were drawn **last**: drawn
first, they would hide the inside while you work on it.

**Extrude** (a sheet's menu, *3D ▸ Modify*, or a polyline selected in the tree) pulls a closed polyline, a
polygon or a rectangle into a prism: move to set the distance and click, or type it. **K** keeps the
source.

## Moving and editing {#editing}

**Whole objects** (Object mode; right-click, or *3D ▸ Modify*). *3D ▸ Modify* is greyed out until something is
selected, and each item in it is enabled only when it can act on the selection — the same rule the right-click menu
follows, where a greyed-out item's tooltip says why:

- **G** moves the selection: click a base point, then a target point, both snapped — two clicks put one
  corner exactly on another. **X**, **Y** or **Z** holds the move to that axis, **Shift+X/Y/Z** to the plane
  across it; type a digit for the distance.
- **R** rotates about the drawing plane's normal in 15° steps (**Shift** for free, or type the angle);
  *Rotate 90°* and *Mirror* act at once. A rotated box is still a box.
- **Ctrl+D** (**Cmd+D**) duplicates and moves the copies; *Array…* makes copies along up to three axes at a
  pitch, previewed as you type; *Align* lines objects up on an axis.
- The **gizmo** at the selection's centre: drag an arrow to move along it, a square to move in its plane.
- *Order*: where two solids overlap, the later one in the list wins the shared volume — except that **metal
  always wins over a dielectric**, whatever the order. That is why the example's vias can simply be drawn
  through the alumina base: they are gold, so they win.

**Faces and vertices** (Face mode, **F**; Vertex mode, **V**):

- **N** (*Move Along Normal*) pushes or pulls a face. Its neighbours keep their planes and stretch to meet
  it, so a pushed box is still a box. It stops before a neighbour would vanish, and says which.
- **G** moves a face freely (its neighbours tilt to follow), or a vertex in Vertex mode; Properties' *Set
  coordinates* types where a vertex goes.
- **Shift+E** grows a new solid out of a face along its normal — a bump on a pad, a wall on a floor. *Align to
  Face…* moves an object until two faces meet (**T** switches between *Touching* and *Flush*). *Copy as
  Sheet* makes a sheet on a face. *Measure* reports a face's area, perimeter and normal.

**Why a solid never opens.** Every edit keeps a solid **closed**. No face is ever deleted, so there is no
Delete in Face mode, and a face that can no longer be flat is split into flat triangles named after it
(`top.0`, `top.1`). An edit that would make a solid pass through itself is drawn in red and refused, naming
the faces. A closed solid is what a mesher needs; an open one would be a hole in the problem that nothing
reported. A shape keeps its kind as long as its own dimensions can say the result — otherwise it becomes a
**polyhedron** with the same face names, and the status line says so.

**Measure** (**M**): two snapped clicks give both points, their differences and the distance, in the display
unit; every number copies, and pastes back into a dimension box exactly.

### Groups {#groups}

A **group** gathers objects and placed cells under one name, so the whole of a part — a stage, a lid and its
walls, a row of pads — is picked, moved and changed as one thing.

- **Group Objects** (**Ctrl+G**, **Cmd+G** on a Mac; the right-click menu, in the view or the tree; *3D ▸ Modify*)
  makes one group of the selection, named `Group1`, `Group2` and so on. Select the members first — two or more
  objects, instances or groups — in the view or with **Shift**/**Ctrl**-click in the tree; with one thing selected
  (a single object, or a group on its own) it is greyed out, and its tooltip says why. Placed cells can be members too, and so can other groups: a group
  inside a group stays a group of its own.
- **A click in the view on any member selects the whole group** — the outermost one it is in — and every
  Object-mode command acts on all of it: Move, Rotate, Mirror, Duplicate, Array, Align (which lines the group up by
  its own box and moves it as one), Order, Hide, Isolate, Delete, a material, and a boolean — whose panel lists
  every member, so the Blank is chosen there as usual; the result stays in its Blank's group. To pick one member out
  of a group, click its row in the tree, or press **B** in the view.
- **In the object tree** groups are listed first, under **Groups**; open one to see the groups and then the objects
  and instances inside it. A member is listed there and nowhere else. The tick beside a group shows or hides all of it.
- **Properties** shows a group as one thing: its **Name** (rename it there), one **Material** and one **Role** for
  every member — the box reads **Various** while they differ, and a choice applies to all of them — and its
  **Corner**: type one and every member moves by the same step. The **Size** is the members' box, for reading; a
  group has no placement or size of its own, and each member keeps its own.
- **Ungroup** (**Ctrl+Shift+G**, **Cmd+Shift+G**; *Ungroup Group1* on the right-click menu) takes apart only the
  outermost group: the groups that were inside it stay groups, to be ungrouped in turn if you want.
- **Duplicate** or **Array** a whole group and each copy is a new group (`Group2`, and each group inside it
  renamed the same way); a member copied on its own stays in its group.
- Grouping, ungrouping and renaming are each **one undo step**, and the groups are saved in the `.c3d`: each member
  names its group — `"Group": "stage1/match"` — so a group can never be left empty or point at nothing.
  **Flatten** puts a placed cell's contents in that instance's group, and **Group into Cell** gives the new instance
  the group its contents were in. Groups are only organisation: the solver never sees them.

## Dimensions as expressions {#expressions}

Wherever a dimension is typed — the box at the cursor, Properties, the Array panel — you may type a name or
an expression: `cav_w`, `2*wall`, `cav_l/2 + wall`. Names are **VARs** of the 3D view or the cell's
**parameters**, and the **Variables** panel (the *x* button) lists both. An unknown name opens a **Define**
strip under the box, so a name can be made on the spot.

The example's names:

| Name | Value | What it is |
|---|---|---|
| `cav_w`, `cav_l`, `cav_h` | 400, 320, 40 mil | **cell parameters** of *Package* — each a VAR promoted with *Promote to Cell Parameter* |
| `wall` | 20 mil | a VAR: the wall thickness |
| `floor_t` | 2 mil | a VAR: the floor's thickness |
| `lid_t` | 5 mil | a VAR: the lid's thickness |

**Changing one redraws everything that uses it**: set `cav_w` to `480` in the Variables panel and the
floor, the base, the east and west walls and the lid all move; the die and the leads stay where they are.
Dragging a face whose size is an expression (**N**) writes the **name**, not the field, so every other
object using that name follows too.

<div class="callout">
<span class="label">Units in an expression</span>
<p>A number may carry its own unit, written straight after it with no space: <code>cav_w + 40mil</code>,
<code>10um + 1mil</code>, <code>2*wall + 0.5mm</code>. A number with no unit that is <b>added to</b>, subtracted from
or compared with a name that has a unit takes the <b>box's</b> unit: <code>cav_w + 40</code> typed in mil is
<code>cav_w</code> plus forty mil. A number that <b>multiplies</b> or divides stays a plain number:
<code>2*wall</code> is twice the wall.</p>
<p>A lone <code>m</code> is <b>milli</b>, not the metre, exactly as in a netlist: <code>2m</code> is 2 mm. Write
<code>mm</code> to say so, or <code>metre</code> for metres; the box refuses a bare <code>2m</code> and
<code>check</code> warns about an <code>m</code> inside a length. <code>check</code> also warns about any dimension
over 1 m, the mark a unit slip leaves.</p>
<p>A VAR follows the same rule: its unit scales its expression only when the expression names nothing with a unit of
its own. <code>w2 = 2*w</code> in mil, with <code>w</code> = 10 mil, is 20 mil, and <code>w3 = w + 5</code> in mil is
15 mil. A VAR written <code>w = 10mil</code> needs no unit of its own.</p>
</div>

## Booleans, fillets, chamfers and STEP parts {#operations}

Four kinds of object are built by **OpenCASCADE**, the geometry kernel that ships inside circuitRF, rather
than by the editor itself: a **Boolean** (subtract, unite or intersect), a **Fillet**, a **Chamfer** and a
**Step** part (one solid of an imported STEP file). **Settings ▸ 3D EM** says whether this installation has
the kernel. **All four are made in the editor** — booleans and fillets below, STEP parts in
[STEP](#step) — and a `.c3d` holding any of them is drawn, checked, explained and elaborated. The kernel is
Open CASCADE Technology; **Help ▸ About** and its third-party notices say which version and under what
licence.

A boolean owns its operands. This one subtracts a bore from a lid:

```json
{
	"$type": "Boolean",
	"Name": "lid",
	"Op": "Subtract",
	"Blank": {
		"$type": "Box",
		"Material": "Lid alloy",
		"Min": [0, 0, 500000],
		"Size": [4000000, 3000000, 250000]
	},
	"Tools": [
		{
			"$type": "Cylinder",
			"Name": "bore",
			"Base": [2000000, 1500000, 400000],
			"Axis": "Z",
			"Length": 500000,
			"Radius": 300000
		}
	]
}
```

- **The boolean takes its Blank's name.** The box inside it has no `Name`, and the result is `lid` — so a
  port or a boundary that was on `lid` before anything was subtracted from it is still on `lid`. A Fillet or
  Chamfer takes its Target's name the same way. The result's **material and role are the Blank's**; a Unite
  of different materials takes the Blank's and says which it replaced.
- **Faces keep their names.** The Blank's faces are still `zmax`, `xmin` …; a Tool's are `bore:side`; a face
  the operation cut in pieces is `zmax#1`, `zmax#2`, and a boundary on `zmax` lands on every piece. Edges are
  named by the two faces they separate, `bore:side|zmax`, and that is how a Fillet names what it rounds:
  `"Radius": 50000, "Edges": ["bore:side|zmax"]`. A fillet's new face is `fillet(bore:side|zmax)`. An edge name
  follows its faces through an edit: when `zmax` is split into `zmax.0` and `zmax.1`, a fillet on `xmax|zmax`
  rounds the edges between `xmax` and both pieces. One whose face has gone is refused by name
  (*"Edge 'xmax|zmax' of 'lid' no longer exists: its face 'zmax' was removed"*), never moved to a nearby edge.
- **`"Enabled": false`** makes the operation as if it were not there: the Blank is drawn and solved under the
  boolean's name and each Tool under its own — the bore standing in the lid. A boundary on `bore:side` of `lid`
  then lands on the cylinder's `side`. Nothing in the file changes; switching it back is the A/B test.
- **`"KeepTools": true`** on a Subtract also keeps each Tool as its own solid, right after the result, with its
  own material — how a dielectric fill in a bore is stated.
- **An operation the kernel cannot build** — a fillet larger than the faces beside it, a subtraction that
  leaves nothing — is refused by name, and the rest of the document still elaborates. The edit stays; fix it or
  undo it.

### Making a boolean {#booleans}

Select two or more solids in **Object** mode — **the first one you select is a Tool, the last is the Blank** —
then right-click ▸ **Boolean ▸ Subtract…**, **Unite…** or **Intersect…** (also under **3D ▸ Boolean**). A panel
opens at the view's top right, as *Array…*'s does, and the camera stays free:

- **One row per selected solid**, in the order you selected them. Its radio makes that row the **Blank**; every
  other row is a **Tool**. With two rows, **Swap** exchanges them.
- **The operation** can be changed in the panel's combo. **Keep tools** (Subtract only, off until you tick it,
  remembered while the editor is open) keeps each Tool as a solid of its own beside the result — subtract a
  dielectric slug from a lid and keep the slug as the fill.
- **One line says what the result keeps**: its name and material, and — for a Unite — every material it
  replaces, by name (*"'pin' (Copper) becomes Gold"*).
- **The preview is live.** The result is drawn as it will be, the operands as translucent ghosts (a subtraction's
  Tools in red); a small busy mark shows while the kernel works, and **OK** waits for it. Changing anything asks
  again, and only the newest answer is drawn. A result in several pieces is one object and the panel says so; one
  that leaves nothing, or that the kernel cannot build, is said in red and OK stays disabled.
- **OK** (or **Enter**) is one undo entry — *Subtract bore from lid* — and costs nothing more: the preview already
  built exactly what is committed. **Cancel** (or **Esc**) changes nothing.

Sheets, polylines, bond wires and placed cells are not operands, and the disabled menu item says why for each
(a placed cell's solids belong to its own cell: push into it, or flatten it).

**In the object tree** a boolean is a node with its operation's icon; its **Blank** and then its **Tools** are
beneath it, and a boolean inside a boolean is a node inside a node. By material, a boolean is listed under its
Blank's material and its operands only under it; by type, booleans have a group of their own. Right-click a
boolean for *Edit Operands*, *Dissolve Boolean* (its operands become top-level objects again, the Blank under the
boolean's name) and *Enabled*; right-click a Tool for *Make Blank* and *Remove from Boolean*.

**In Properties** a selected boolean shows its **Operation**, **Enabled**, **Keep tools** and **Blank** (and
**Swap** for two operands), and its material — the Blank's, changed on the Blank. Each change is one undo entry
and is built at once; a change the kernel cannot build is **kept**, and the node is flagged with the reason, so
undo is one keystroke away rather than the combo snapping back.

**Editing an operand.** Double-click a boolean's result (or select it and press **Ctrl/Cmd+]**) to enter it: the
result is drawn as a ghost and its operands solid, and the status line says *Editing operands of 'lid' — Esc to
leave*. An operand then moves, rotates, mirrors, duplicates (the copy is a top-level object), takes a material,
and has its faces and vertices edited, exactly as any object does; a drag moves only its drawing, and the boolean
is built again once, on the release. **Esc** or **Ctrl/Cmd+[** leaves. Clicking an operand's row in the tree
enters its boolean too.

**A result's faces are for reading**: a port, a boundary, Measure, snapping and *Drawing Plane from Face* all use
them, but moving or extruding one is refused — *"'lid' is made by a boolean: edit its operands (double-click it)
or the operation in Properties."*

### Rounding edges: Fillet and Chamfer {#fillets}

Select edges of one solid in **Edge** mode, then right-click ▸ **Fillet…** or **Chamfer…** (also under
**3D ▸ Modify ▸ Edge**). A box, prism, cylinder, polyhedron, boolean result or STEP part can be rounded, and so can a
solid that is already; a sheet has no volume to round, a bond wire is made from its points, and a placed cell's
solids belong to its own cell — each disabled item says which. Without the kernel both are disabled with the
reason, and Edge mode still works.

- **The panel** is the Boolean panel's: **Radius** for a fillet; for a chamfer **Equal distances** or **Two
  distances**, where **Flip** chooses which face the first distance is measured on and a line says which. A size
  is a number or an expression (`r_edge`, `2*t`); a name nothing defines is offered as a VAR, with its value.
- **The edge list** counts the edges and names each, with a **×** to leave one out. The panel does not hold the
  view: click, **Shift**-click or double-click edges while it is open to change the list. The object is drawn
  translucent over the preview so its edges can still be picked; an edge of another object is refused.
- **The preview is live**, as a boolean's is, and **OK** waits for it. A size that does not fit is said in the
  edge's own terms — *"A 50 µm radius does not fit edge 'xmax|zmax': the faces beside it are 30 µm wide. Try less
  than 30 µm."* — and a corner the kernel cannot blend names the edges that meet there.
- **OK** is one undo entry (*Fillet 4 edges of lid*). The fillet **wraps** the solid and **takes its name and its
  place**: every port, boundary and wire on the solid's faces still lands, the faces keep their names, and each new
  face is `fillet(<edge>)` or `chamfer(<edge>)`. A second Fillet… or Chamfer… on the same solid is added beside the
  first: a solid carries any number of them, **one enabled at a time** — the new one is enabled and the others are kept,
  switched off, and switching one on switches the rest off, each in the same undo entry.

**In the object tree** a rounded solid is **one node**, listed as the solid it rounds, with its fillets and
chamfers as rows beneath it, innermost first (*Fillet 50 µm — 4 edges*). **In Properties** a row shows its
**Radius** (or **Distance** and **Distance 2**, and **Flip**), **Enabled**, and its **Edges** — **Show** selects
them in the view and **Edit…** reopens the panel on them — and **Remove** unwraps it: the solid returns unrounded,
in its place and under its name. Each is one undo entry; a change the kernel cannot build is kept and the row
flagged. **Enabled** off draws and solves the solid unrounded, and a boundary on one of the fillet's own faces then
says it *"exists only while its fillet is enabled"*.

**A rounded solid's faces are for reading** too: *"'lid' is rounded by a fillet: edit the box (disable the fillet,
or change it in Properties)."* With every fillet and chamfer on it disabled, its faces and vertices are edited as
the solid's own, and switching them back on rounds the edited solid — an edge still there is rounded again at the
new size.

**Snapping to curves.** On a solid the kernel built, a snap reaches the true vertices only (a curved edge's
drawing points are not vertices), an open curve's midpoint along the curve, and the **centre of a circle or an
arc** — shown as the face-centre marker with a dot in it — which is how a pin is put exactly on a bore's axis. A
point found along a curved edge is on its drawn chord, so the status line marks it **≈**.

<div class="callout warn">
<span class="label">Without the geometry kernel</span>
<p>A development build, or an installation missing its <code>geometry-kernel</code> folder, cannot build
these objects. A 3D view that holds <b>any</b> of them — even a disabled one — is <b>not opened</b>: a dialog
names the objects, says why, and offers <b>Open Settings ▸ 3D EM</b>, where the kernel's row says how to
restore it. A 3D view with none of them opens exactly as before. <code>circuitrf check</code> reports one
error per such object and exits 1, and <code>em</code> refuses before writing anything. A document that places
a <i>child</i> using one still opens; that one instance is refused and Simulate says which.</p>
</div>

Both solvers take a kernel solid, and they do not treat its curves alike:

- **Palace** reads the kernel's own shape, so a fillet or a bore is meshed as the curved surface it is (second-order
  elements, twelve per full turn). Two things are said before the run: a **warning** where a conductor's curved
  face is under ten skin depths in radius at the bottom of the band — Palace models a conductor's loss as a flat
  surface's, which reads low there — and a **note** where a small radius sets the mesh's size; the operation's
  **Enabled** box is the way to see whether that feature matters.
- **openEMS** reads a tessellation fitted to its grid and **staircases** every curved or sloping face to the grid's
  cells. Each kernel object gets one line about its worst rounded feature: *will not represent* (the cell is wider
  than the radius, so the edge is solved as sharp), *staircases … with about N cells* (a warning below four), or a
  note that refining the grid converges it. A boundary on a curved face is refused for openEMS; Palace takes it.

Those lines appear under each setup in *Simulate ▸ Setup Analyses…*, in `circuitrf check` (at their own severity;
they never change the exit code), and in the run's messages. None of them stops a run. `explain` (which reports the
kernel, each operation's operands, its face and edge counts and its smallest radius) and `render` work on kernel
objects too.

## STEP: importing and exporting {#step}

**STEP** (`.step`, `.stp`) is how a solid model travels between mechanical tools: a connector's maker, a
housing's designer and a CAD package all read and write it. circuitRF reads its solids **into a 3D view**
and writes a 3D view — or a layout — **out as one**. The geometry kernel does both, so without it both
commands are disabled with the reason.

**Import STEP…** (*File ▸ Import ▸ STEP…*) reads the file, off the editor's thread
and cancellable, and opens a table of its parts:

- **Units.** The first line says what the file is in and that it is imported exactly — *File is in
  inches; imported exactly* — because a STEP file states its own unit and every coordinate is converted,
  not guessed.
- **One row per part**: its name, its colour, whether it is a closed solid, the **material** it will take,
  how that was chosen (**Match**: by name, by colour, or chosen), and its path in the file's assembly. A part
  whose name is a material of the technology takes it; failing that, a part whose colour is a material's
  takes that one. The **⇉** button on a row — **Map all of this colour** — gives every part of the row's
  colour the row's material: a connector's brass parts in one gesture. **(no material)** imports a part
  that is drawn and ignored by the solver, and `check` names it.
- **What is not imported, and why.** Only a closed solid can be solved, so a surface, a wire body or an
  open shell is listed, unchecked and disabled, with its reason on its row. Dimensions, tolerances and
  datums (product manufacturing information) are counted in the notes and not imported: they describe
  how to make a part, not what it is.
- **Import** copies the file **into the 3D view's own folder** — so the workspace carries it, and a copy
  that holds the same bytes is reused rather than duplicated — and adds one **Step** object per checked
  part, selected, ready to Move. It is one undo entry, and undoing it removes the copy at the next save.
  The part lands exactly where its file puts it: the file's assembly transform is applied, the object's
  own placement starts at nothing.

A Step object is a solid like any other: it takes a material, moves, and is a boolean operand or a fillet's
target. Its faces are `face1`, `face2` … as the file's shape numbers them, and they mean something only for
those bytes — which is why the object records the file's **Hash**. When the source file changes (a new
revision from the maker), **Reload from Source** on the object's menu reads it again and re-matches every
port, boundary and fillet on it **by geometry**, then says what moved; a part the new file no longer has is
said by name. A part the new file adds is offered in the import table, unchecked.

**Export STEP…** (*File ▸ Export ▸ STEP…*) writes the active 3D view or layout:

- **Structure**: **Flattened** writes one product per solid; **As assembly** writes each placed cell as a
  sub-assembly, once, however many times it is placed.
- **Overlaps**: **Precedence applied** writes disjoint solids, cut exactly as the solver gets them — metal
  over dielectric, then construction order, so a via is a hole through the substrate with the via in it.
  **As drawn** writes every solid whole, overlapping where it was drawn.
- **Thicken sheets** gives each sheet its stated thickness, for a receiving tool that drops surfaces;
  **Include air box** adds the first 3D setup's air box as one uncoloured solid, for checking clearance to
  a housing. **Schema**: AP214 (the widest read) or AP242.
- **Units** follow the design: millimetres for a design in nm, µm or mm; inches for one in mil or inch.
  Each product carries its object's name and its material's colour, and the header names circuitRF and its
  version. A summary line says what will be written before anything is.

The **3D Connector** example's flange was made this way: drawn in its own cell, exported, and imported into
the launch — see [the second example](#connector).

## Pictures and drawings {#drawings}

Right-click the view (on anything, or on nothing) for five ways to take it out of circuitRF:

- **Copy** puts a picture of the view on the clipboard, drawn by the graphics card at a multiple of the
  window's size. **Export Picture…** saves the same picture as a PNG.
- **Copy as Vector** puts the view on the clipboard as **lines**: every silhouette and sharp edge, seen
  along the camera's direction, with the edges behind a surface removed — what the shaded view shows, drawn
  as a line drawing. It pastes into a slide or a document as a picture that stays sharp at any size: on
  Windows as a metafile, on macOS and Linux as PDF and SVG, with a PNG beside them for anything that takes
  neither. **Export as Vector…** saves the same drawing as an SVG or PDF file.
- **Export Drawing…** (also *File ▸ Export ▸ Drawing…* while a 3D view is the active document) lays several
  views out on one sheet, as SVG or PDF.

A vector picture is always **orthographic**, even when the view is in perspective, and it leaves out what you
have hidden. Conductors keep the colours the view draws them in; dielectrics are outlined in grey.

### Export Drawing… {#export-drawing}

The dialog chooses what goes on the sheet:

- **Views**: *Isometric*, *Top*, *Front*, *Right*, *Left*, *Back*, *Bottom* — each an outline looking along
  the direction the matching *Standard View* looks, so *Top* on paper is *Top* on screen. Top comes first, so
  on two columns it sits above Front with Right beside it.
- **Sections**: **Add Section** adds a cut through the model — **XY** at a height *z*, **XZ** at a *y*,
  **YZ** at an *x* — starting at the model's centre. Type the position **with its unit** (`1.2mm`, `350um`,
  `40mil`); a bare number is refused, as `render --section` refuses one, because nanometres, micrometres and
  millimetres are all plausible. A section is drawn filled, each material in its colour, and lettered
  *Section A–A*, *B–B* … in order.
- **Hidden edges**: **Removed** (the default) draws only what faces you; **Dashed** draws the edges behind a
  surface as thin dashed lines, the drafting convention; **Shown** draws every edge, a wire-frame. Sections
  show the inside of a closed part better than dashed lines do, which is why Removed is the default.
- **Material legend**, and **Leave out what the 3D view hides** (on by default).
- **Text as outlines** (on by default): every label is drawn as the shapes of its letters, so the file looks
  the same on every machine and in every program. Turned off, the labels are real text you can select and
  edit — but they name the font *IBM Plex Sans*, and a reader without it installed sees another font in its
  place.
- **Page** (A4, Letter, A3, Tabloid, portrait or landscape) and **Format** (PDF or SVG).

**Every view is at one scale**, a round one — *5:1*, *2:1*, *1:10* — chosen as the largest that fits every
view, and stated in the title block with the document's name. A length measured on one view can be compared
with another, and with a ruler on the printed page. The dialog remembers its choices (not its sections) for
next time.

Working out which edges are hidden takes a moment on a large model; above 400,000 triangles it is skipped,
the edges are all drawn, and the message that reports the export says so.

## Hierarchy: placing cells {#hierarchy}

**Design ▸ Place Cell Instance…** with a 3D view active offers every cell with a **3D view** or a
**layout**, and a **View** choice between them. The example places the *Thru die* cell's **layout**: a
layout comes in through its own technology's stackup, bottom up — back-side metal, 100 µm of GaAs, the
metal on top — bounded by its board outline, the die edge. Its objects are named `U1/…` (`U1/GaAs`,
`U1/Metal1/1`), and its conductors are pads a wire can land on.

- **The placement point** is the child's origin. Hold **Ctrl** (**Cmd**) while placing to use its
  **bottom-centre** instead — the **die-attach handle**: the die is dropped with its back side flat on
  whatever you click, which is how U1 sits on the attach pad.
- **Why a different technology is allowed here, and not in a layout.** A layout instance must share its
  parent's technology because layouts match layers **by number**: layer 1 in a GaAs process and layer 1 in
  a board process are different things, and mixing them would be silently wrong. A 3D view matches nothing
  by layer. Every instance arrives as solids in **metres** made of **named materials**, so a GaAs die in an
  alumina package is exactly what it says. Two technologies' materials with the same name merge when they
  are equal (the example's *Gold* is one material) and are told apart as `name@technology` when they are
  not.
- **Swap View** (right-click an instance) switches it between the child's layout and its 3D view, keeping
  its placement. *Thru die* has no 3D view, so in the example the item is disabled and its tooltip says
  why. A port or boundary that named a conductor inside the instance is reported if the other view has no
  object of that name.
- **Array…** on one instance sets that instance's own array — one child, drawn many times.
- **Push Into Cell** (**Ctrl/Cmd+]**) opens a 3D child in this editor, in context — the parent drawn dimmed
  around it — or a layout child in the layout editor. **Pop Out** (**Ctrl/Cmd+[**) returns.
- **Flatten** replaces an instance with its objects (a layout's become solids; it asks first, stating how
  many). **Group into Cell…** does the reverse for selected objects, without moving them.

### A placed layout's dielectric {#layout-dielectric}

A placed layout's dielectrics are drawn, and they are **part of the problem** Palace and openEMS solve. A
stackup gives a dielectric no outline, so each slab is cut to one of these, the first that exists:

- The layout's **board outline**, as with the die edge above.
- The **outline of the copper directly above and below it**, with gaps closed, holes filled and a margin of
  2 dielectric stack heights past the copper. This keeps the substrate in a coplanar line's gaps and beside
  every trace, where its fringing field runs.
- The bounding box of everything drawn, if there is no copper above or below it.

The Messages note for the build says which one each slab took. Draw a board outline when the board's real
edge matters. The whole rule, and how it differs from a planar setup's laterally infinite dielectric, is
in [3D EM ▸ How far the dielectric reaches](em-3d.html#dielectrics).

## Bond wires {#wires}

**Shift+A W** (or *3D ▸ Draw ▸ Wire*). A wire runs between two metal objects: click the one it starts on
(any face — it lands on that object's **top**, above the point clicked), then the one it ends on, then move
to set the **loop height** and click, or type it. The toolbar sets the diameter, the metal, each end's bond
(**ball** or **wedge**) and the section. Across hierarchy is the usual case: the example's wires start on
the die's pads, inside U1, and end on the package's leads, which a `.wBond` cannot do because it belongs to
one layout.

- **The loop height you type is the one the wire measures**, the assembly way: from the top of the lower
  pad to the top of the wire at its highest point. The example's are **8 mil**, typed.
- In Vertex mode a wire's centre-line points are handles; **G** moves one, and the feet are put back on the
  pads when you let go. Properties lists every point, editable.
- Properties also takes a wire's **loop height** and **span**, as a wBond or Layout wire does: a loop height
  scales the wire's rise above its feet and keeps every x and y; a span moves the end foot along the wire's
  direction, the start staying put, and the end must land on a pad. Its **diameter** is in the display unit,
  like every other length there.
- **A wire does not follow its pad.** Re-routing would change its inductance without telling you, so a
  wire whose pad has moved is drawn red, flagged in the tree, and refused by a run. *Re-Seat Wire Ends*
  puts each end back on the pad now under it.
- Clicking a pad **through the lid** lands on the lid, which is metal too. Hide the lid first, or draw the
  wires before it — the example did both in that order.

## Simulating {#simulate}

**Setups.** A 3D view carries its own EM setups. *Simulate ▸ Setup Analyses…* (or the toolbar's tune
button) lists them as cards: *Add*, *Duplicate*, *Rename*, *Remove*, *Make Active*; a double-click opens
the same panel a `.cem` opens, less the layout row. The example has two: **Driven**, a Palace S-parameter
sweep from 2 to 30 GHz, and **Lid modes**, a Palace eigenmode solve. The **active** setup is the one
**Run** runs and whose air box is drawn.

**Ports.** **Shift+A P** draws one on the drawing plane, two corners; or right-click a flat rectangular
face, *Make Port ▸ Lumped* or *Wave*. **Which kind a face can take depends on where it lies, not on what it
was drawn as**: a wave port is a region of an air-box face (a sheet drawn there is the usual way to state one),
and a lumped port bridges two conductors on opposite edges (a face of a small gap block does exactly that). The
menu offers only the kind that would resolve, and the other's tip says why. **Which way round a lumped port is comes from what it touches**: each
edge is tested against every conductor, one opposite pair must touch one conductor each, and the one that
is ground — or failing that, the larger — is **−**. The port is drawn with its number and an arrow from −
to +, and while it is drawn the arrow (or the reason there cannot be one) follows the cursor. The example's
two ports are 5 × 5 mil rectangles on the YZ plane at each lead's outer end, from the floor up to the lead:
each runs **from `floor` (−) up to its lead (+)**. *Flip* on its menu turns it round; a wrong arrow turns
every transmission term by 180°, so look before a run.

**The air box** is the region solved in: each face a distance beyond the geometry, and a boundary — PEC,
PMC, symmetry or absorbing — chosen by right-clicking the face. The example's package **is** its own
shield: both setups set every face to PEC with **no padding**, so the box lies flush against the floor,
the walls and the lid, and nothing outside the metal is meshed. **Boundaries on faces** (right-click a
dielectric's face: *Perfect Conductor*, *Conductive Surface*) put a wall inside the model without drawing
metal.

**Run.** *Simulate ▸ Run* (or the workspace toolbar's Run). Palace or openEMS is started with the same
progress, **Cancel** and messages as any EM run, and the result opens in the Data Display. Afterwards the
**Field** bar shows the saved fields on the geometry that was solved. On a machine without the solver, Run
is refused and offers to install it — and that is the only gesture on this page that needed one.

**What the example gives.** Every figure was measured once, on an Apple M4 with 10 cores and 16 GB,
running macOS 27.0, Palace 0.18.1 and Gmsh 4.15.2; yours will differ.

- **Driven** (2–30 GHz, 1,121 points): **4 min 46 s**, peak **6.4 GB**. |S21| is **−0.41 dB** at the band
  centre, 16 GHz, and falls to **−5.35 dB** in a notch about 50 MHz wide at 22.15 GHz — the lid's resonance,
  below, seen from outside.
- Both setups run at **Draft with element order 2**, with *At metal and ports* at **1** and the **Direct** linear
  solver, chosen so each finishes in under five minutes. Every wall here is metal, so the default refinement at
  metal would fill the whole cavity with fine elements; order 2 is what resolves the notch, and *Direct* is what
  makes it fast. The example's `README.md` gives what each alternative measured.

## The lid's resonance {#lid}

A lid turns a package into a **cavity**, and a cavity has resonant modes. The lowest, for a cavity much
wider than it is tall, has its electric field pointing from the floor up to the lid, strongest in the
middle — exactly where the die is. If that mode lands inside the band the circuit works in, the package
couples the output back to the input through it: the classic package problem. The **Lid modes** setup
finds it directly, with an eigenmode solve.

**Lid modes** (the one mode above 20 GHz): **35 s** of Palace, peak **4.0 GB**. The mode is at **22.16 GHz**
with a Q of **318** — the gold, the lid alloy and the ports' 50 Ω all load it — and it is the driven sweep's notch.

**The external check.** The closed form for a rectangular cavity 400 × 320 × 40 mil with a 5 mil alumina slab on
its floor puts the lowest mode at **22.23 GHz**. That is the one number in this section that is not circuitRF's
own output. The drawn package is 0.3 % lower: the die, its pad, the leads and the wires are what the closed form
leaves out.

**Move the mode.** Set `cav_w` to `480` in the Variables panel: the walls, floor, base and lid move out and the
die stays put. The closed form then says 20.87 GHz — a mode below 22 GHz, walking toward the band — and **Run**
on *Lid modes* finds where the drawn package puts it.

## Temperature {#thermal}

The same model solves for heat. A **thermal setup** (*Simulate ▸ Setup Analyses…*, the thermometer button) is solved by
circuitRF's own steady-conduction solver after Gmsh meshes every solid — air is not meshed. What it needs is drawn in the
view, and what it is worth is typed in the setup, so one model carries several thermal setups beside its EM ones.

**The places.** These are drawn in the view but are not objects: they are never solved as metal, never snapped to, and an
EM setup ignores them. Each has a group in the object tree (*Heat sources*, *Probes*, *Mesh regions*) whose rows select,
rename, hide and delete, and a switch under *3D ▸ View*. Selecting a row shows its fields in Properties, dimensions
included, each a number or an expression.

- **Heat source** (**Shift+A H**, or *3D ▸ Draw ▸ Heat Source ▸ Rectangle / Polygon*): drawn as a port's sheet is — put the
  drawing plane on the face first (**Ctrl/Cmd**-click it) — then named and given a default power in W. Right-click a solid,
  *Heat Source from Solid*, spreads the heat through its whole volume instead. Drawn hatched in orange.
- **Probe** (**Shift+A T**, or *3D ▸ Draw ▸ Probe ▸ Point / Spot / Line*): a point on a face; a spot (its centre, then its
  diameter — what an IR microscope averages); or a line (two points: temperature along it). Right-click a face (Face mode)
  or a solid or a wire (Object mode), *Add Probe*, for a probe over the whole face, solid or wire. A probe may state a limit
  in °C; the results flag where it is crossed.
- **Mesh region** (**Shift+A M**, or *3D ▸ Draw ▸ Mesh Region*): a box, drawn as a Box is, with the element size inside it —
  fine mesh where you put it. Drawn dashed.

**Boundaries.** Every face nothing names is insulated. In Face mode, right-click a face, *Thermal ▸ Fixed Temperature…*,
*Convection…*, *Insulated* or *Clear*: each writes the **active** thermal setup (greyed, saying why, when the active setup
is not a thermal one). A conditioned face is tinted — **blue** a fixed temperature, **green** convection — and listed under
*Thermal boundaries* in the tree. With two touching objects selected, *Thermal ▸ Contact Resistance…* overrides the
technology's interface resistance for that contact, prefilled with the technology's value when it states one.

**The setup's Thermal page**, top to bottom: **Sources** (each heat source's default, and this setup's override),
**Boundaries** (the list the menu writes, and *All exposed faces: convection* at an h and an ambient), **Sweep** (up to two
variables), **Measures** (one per line — `Rth = (Tmax(die) - Tavg(flange)) / Pdiss` — checked as you type), **Mesh** (order,
elements across a source and through the thinnest solid, grading, the solver, the convergence check), and **Balance** (k(T)
on or off). Under Mesh, the size of the run is estimated before anything is meshed.

**Plot Temperature.** After a run of the active thermal setup, right-click a face, *Plot Temperature*, to paint that face
(again to take it off; several faces accumulate); *3D ▸ View ▸ Temperature ▸ All Faces* paints every exposed face, and
*On Clip Plane* the clip plane's section — the way to see a channel under a field plate. The items are greyed, with the
reason, when there is no thermal result or the model has changed since it ran. The colour range is the **true** minimum
and maximum of what is painted — the peak is the answer, so it is never clipped — and the legend reads °C; *Fix Range
Across Sweep* makes it the minimum and maximum over every sweep point, so stepping the sweep never rescales the colours.
The slider on the Field bar steps the sweep. Hover a painted face for the temperature there (interpolated, not the nearest
node); a ring marks the **hot spot** with its temperature and its object. A bond wire whose run tabulated its temperature
is coloured along its length, and hovering it names the wire and the distance along it.

**Along a line.** *Along…* on the Field bar (or *3D ▸ View ▸ Temperature ▸ Temperature Along…*) takes two points and plots
the temperature between them, with both end temperatures and their difference — Rth by hand, with nothing added to the
document. A line probe's row in the tree plots its own, *Plot T(s)*. **Probes** on the Field bar lists every probe
statistic and every measure at the sweep point shown, a column per point on request, with any probe past its limit in
orange.

## Headless {#headless}

Every step above has a command-line spelling, and none of them needs a solver except `em`:

- **The format.** `circuitrf reference 3d-view` prints the `.c3d` format, field by field, with a whole
  worked file. A 3D view can be written by hand, or by a script, as well as drawn.
- **`circuitrf check <path>`** — on the `.c3d`, the cell or the workspace: every object, name, expression,
  instance, port and setup, with each port's inferred polarity reported. The example checks clean.
- **`circuitrf explain <path.c3d>`** — how it elaborates: where each name's value came from, which
  technology each instance resolved through, every solid with its material and extent in metres, each
  port's contacts edge by edge, the air box, and an estimate of the problem's size.
- **`circuitrf render <path.c3d> --section xz@y=0mil -o cut.svg`** — a section through it; `--iso` for an
  isometric view. The pictures on this page are drawn that way.
- **`circuitrf em <path.c3d> --setup "Lid modes"`** — runs one setup, exactly as Run does, and writes the
  same files.
- **`circuitrf convert part.step -o Cell/3d/Cell.c3d`** — Import STEP into a new 3D view, each part's material by
  name or colour, or `--material <part>=<material>`; **`circuitrf convert x.c3d -o x.step`** — Export STEP, with the
  dialog's choices as flags. See [convert ▸ STEP, both ways](cli.html#convert-step).
- **Booleans, fillets, chamfers and Step parts by hand.** The `Boolean`, `Fillet`, `Chamfer` and `Step` objects are
  ordinary JSON in the `.c3d`, and `circuitrf reference 3d-view` prints every field; the rules for their names, faces,
  edges and `Hash` are in [File formats ▸ The 3D view's operations](file-formats.html#c3d-operations).

## Keys at a glance {#keys}

| Key | What it does |
|---|---|
| **O**, **E**, **F**, **V** | Select objects, edges (Edge mode, for Fillet… and Chamfer…), faces, vertices |
| **B**, **Shift+B** | The next thing behind the selection; back toward you |
| **Ctrl/Cmd+A** | Select every shown object (*3D ▸ Select All Objects*); hidden ones are left out |
| **Shift+A** then a letter | Box, Sheet, polyGon, polyLine, cYlinder, Port, Wire, Heat source, Temperature probe, Mesh region |
| digits, **Tab**, **Enter** | Type a dimension instead of clicking |
| **Esc** | Back one step: the typed box, the shape, the tool, the selection |
| **G**, **R**, **X/Y/Z** | Move, rotate; hold to an axis |
| **Ctrl/Cmd+D** | Duplicate and move |
| **Ctrl/Cmd+G**, **Ctrl/Cmd+Shift+G** | Group the selection; ungroup the outermost selected group |
| **N**, **Shift+E**, **T** | Push/pull a face; extrude a face into a new solid; Touching/Flush while aligning |
| double-click an edge | Its tangent chain (**Shift** adds it) |
| **M** | Measure |
| **Alt** (**Option**) | Suspend geometry snapping while held |
| **Ctrl/Cmd**-click a face | Draw on that face's plane |
| **Ctrl/Cmd** while placing | The bottom-centre (die-attach) handle |
| **Ctrl/Cmd+]**, **Ctrl/Cmd+[** | Push into a placed cell, or enter a boolean to edit its operands; pop out, or leave it |
| **Home**, **1**–**7**, **P**, **C** | Fit; standard views; perspective; clip plane |
| In the object tree: **Shift**-click, **Ctrl**-click (**Cmd** too on a Mac) | Select every row between; add or remove one row |

## Every control, in detail {#reference}

The sections above teach the editor through the example. This one is the reference: every control, in the
order a pane presents them, with the rules each one follows.

- **The line on the view**. One line at the bottom of the view is the editor's status line: what
  the armed tool wants next, or the last message — a refusal and its reason, or what an edit did. Where this chapter
  says *the status line* in the 3D editor, it means that line. The clip plane cuts the model, never the drawing grid.
- **Snapping**. The cursor snaps to a **vertex** (a square marker), an edge's **midpoint**
  (a triangle), the nearest point on an **edge** (an ×), a face's **centre** (a circle) and, when nothing is
  within reach, the **grid** (a small +) — the document's snap step on the drawing plane. A marker is drawn in
  the colour of the material it snaps to, and in amber on the grid or on an object with no material. A vertex wins over
  anything else in reach, even a nearer midpoint. Snapping reaches into placed cells, so a die's pad corner
  is a target in its package. Only what you can see is a target: a corner hidden behind a surface is not,
  except inside a translucent object or with the clip plane on. The snap distance is the layout editor's,
  in screen pixels, so it feels the same at every zoom. The magnet on the toolbar, and *3D ▸ Snap*, turn
  snapping on and off, and the buttons beside it turn each kind on and off; these are your settings, not the
  document's. Hold **Alt** (**Option** on a Mac) to suspend geometry snapping while it is held; the grid
  still applies. The marker's shape says what the cursor snapped to; the point itself is read with Measure (**M**).
  A **≈** before a point means it is not exactly a point of the document's database-unit grid: a corner of
  an object rotated by an angle that is not a multiple of 90°, or of a cell drawn at another scale.
- **Drawing**. Shapes are drawn on the **drawing plane** — XY, YZ or XZ, at an offset along
  its normal — chosen with the **XY / YZ / XZ** buttons and the offset box on the toolbar (a bare number is the
  document's unit; `25um` or `10mil` says its own), from *3D ▸ Drawing Plane*, from a face (*Drawing Plane
  from Face* on a face's menu in Face mode, or **Ctrl**-click a face — **Cmd**-click on a Mac — with a tool
  armed), or through a snapped point (**Ctrl/Cmd+Shift**-click moves the offset there and keeps the plane).
  Only axis-aligned faces make a plane; a tilted face is refused, naming its normal. Moving the camera never
  moves the plane, and a plane seen edge-on is refused rather than drawn on. The plane is remembered for the
  document in the workspace's window state, not in the `.c3d`. A faint **grid** lies on it, behind the
  objects, in steps of the document's unit — major lines every 10 minor ones, every 5 in mil and inch —
  spaced to stay readable at every zoom and fading away from the view's centre and when the plane is seen
  at a grazing angle; the lines through the origin are in the axis colours. The snap step is the **Snap** box beside **Unit** on
  the toolbar — the layout editor's control, with the same steps off the technology's default: pick one or type
  a length (`2.5mil`). It is saved in the document, and changing it moves nothing already drawn. The tools are on the toolbar, under *3D ▸ Draw*, and at the cursor with
  **Shift+A** — then one letter: **B**ox (corner, opposite corner, then its height), **S**heet (a rectangle,
  two clicks), Poly**g**on (a closed sheet, a click per vertex; click the first vertex, press Enter or
  double-click to close — an outline that crosses itself is refused and the crossing shown), Poly**l**ine
  (construction geometry; Enter or a double-click ends it, a click on its first vertex closes it) and
  C**y**linder (centre, radius, height). A box or cylinder rises along the plane's normal; near a
  neighbour's feature its height snaps to that feature, so a box rises to exactly the top of a pad. Every
  click snaps. While a shape is in progress, type a digit to enter a dimension instead of clicking — the
  box that opens at the cursor shows the width, depth, height or radius the next click would set; **Tab**
  moves between them, **Enter** accepts, **Esc** goes back to the mouse. An entry that is not a length stays
  in the box, in red, and nothing changes. **Esc** cancels a shape in progress; pressed again it puts the
  tool down. Each new object takes the material chosen on the toolbar and a name you can change
  (`box1`, `sheet1`, …); each is one undo step. **Extrude** (a sheet's menu, *3D ▸ Modify*, or a polyline
  selected in the tree) pulls a closed polyline, a polygon or a rectangle into a prism — move to set the
  distance and click, or type it — and consumes the source unless **K** says keep it. An open polyline
  extrudes to a flat ribbon when it is a straight line along an axis of its plane.
- **Moving and copying** (Object mode). The selection — objects, placed cells, or both — is
  moved, rotated and copied from its right-click menu or *3D ▸ Modify*. **G** moves it: click a **base
  point**, then a **target point**, both snapped, and the selection moves by the difference — two clicks
  put a pad's corner exactly on a trace's corner. Pressed with the cursor over the selection, **G** takes the
  snapped point under the cursor as the base at once. While moving, **X**, **Y** or **Z** holds the move to
  that axis (a snap still decides how far) and **Shift+X/Y/Z** to the plane across it; press the same key to
  let go. Type a digit to enter the distance (`dx`, `dy`, `dz`, or one distance along a held axis). **R**
  rotates about the drawing plane's normal (X, Y or Z changes the axis) through the selection's centre, or
  through a snapped point Ctrl/Cmd-clicked first, in 15° steps — hold **Shift** to turn freely, or type the
  angle. *Rotate 90°* and *Mirror* (across XY, YZ or XZ through the centre) act at once. **Ctrl+D**
  (**Cmd+D**) duplicates the selection and moves the copies; **Esc** cancels both. *Array…* makes copies
  along up to three axes at a pitch, shown as you type. *Align* lines the others up with the last one
  selected, by their minimum, centre or maximum on an axis. *Order* moves objects in the construction
  order — where two solids overlap, the later one wins, except that **metal always wins over a dielectric**
  whatever the order (an air object drawn after a metal still cuts a hole in it) — and the tree and the
  Properties panel show each object's place in it. The **gizmo** at the selection's centre moves it too: drag an arrow to move along
  that axis, or a square to move in that plane. While any of these runs nothing is changed yet; the
  selection moves on screen, and the document changes once, as one undo step, when you click or release. A
  rotation keeps an object what it was — a rotated box is still a box — and the file stores it as at most
  three turns. A result that is not a whole number of database units is rounded and the status line says
  **≈**.
- **Editing faces and vertices** (Face and Vertex modes). Right-click a face, or use *3D ▸
  Modify ▸ Face*. **N** (*Move Along Normal*) pushes or pulls the face: its plane moves and the faces around
  it keep theirs, stretching to meet it — push a box's top up and it is still a box, only taller. It stops
  just before a neighbour would shrink to nothing, and the status line names that neighbour (`'side2' would
  vanish at 1.2 mil`); a typed distance past that point is refused rather than shortened. **G** moves the face
  freely, base point to target point, with **X/Y/Z** and typed distances as for objects; the faces around it
  tilt to follow. **Shift+E** (*Extrude to New Solid*) grows a new solid from the face along its normal — a bump
  from a pad, a wall from a floor — in the toolbar's material, or the source's with **M**; the source is
  unchanged. *Align to Face…* then a click on another face moves the object until the two are in one plane:
  *Touching* (facing each other, a die on its substrate) or *Flush* (the same way), **T** switches, and the
  result is shown before the click; faces that are not parallel are refused (rotate first). *Copy as Sheet*
  makes a sheet exactly on the face. *Measure* puts the face's area, perimeter and outward normal in
  Properties; Shift-click a second, parallel face for the distance between them. In Vertex mode **G** moves the
  selected vertex, Properties' *Set coordinates* types where it goes, and *Measure From* starts a measurement
  there. **Every edit keeps the solid closed**: no face is ever removed, so there is no Delete in Face mode. A
  face that can no longer be flat is split into flat triangles, named after it (`top.0`, `top.1`), and anything
  attached to the face goes to every piece. An edit that would make the solid pass through itself is drawn in
  red and refused, naming the faces (`'top' would pass through 'bottom'`); nothing changes. A shape keeps its
  kind for as long as it can say the result with its own dimensions — a pushed box stays a box, a prism's top
  moved sideways is a slanted prism — and otherwise becomes a **polyhedron** with the same face names; the
  status line says so, and Undo takes it back. A cylinder's ends move only along its axis and its side only
  in radius, and in Vertex mode it offers just its two cap centres, which measure but do not move: *Convert to
  Polyhedron* (Object mode; for a cylinder, choose how many flat sides) makes every vertex editable. While a
  face or vertex is dragged only that object is redrawn, and the document changes once, as one undo step,
  when you click.
- **Dimensions as expressions**. Wherever a dimension is typed — the box at the cursor while
  drawing, Properties, the Array panel — you may type a name or an expression instead of a number: `w`,
  `2*w`, `h_sub + t_met`, optionally followed by a unit (`2*w mil`); with none, the display unit is the
  expression's unit, and it is stored with the expression, so changing the display unit later moves nothing.
  The box shows the value as you type (`= 0.254 mm`), or `unknown: w`. Press Enter with an unknown name and a
  **Define** strip opens under the box, one row per name: a value (for a bare `w`, the size the rubber band
  shows now), a unit, and whether it becomes a **VAR of this 3D view** or a **cell parameter**; Enter defines
  them and the shape carries on, and the definitions and the new object are one undo step. A width, depth,
  height, radius or extrude distance keeps its expression; a polygon's points and a move's distance are
  evaluated once and stored as numbers, and the box says so. A number may carry its own unit (`2*w + 0.5mm`);
  one without that is added to a name with a unit takes the box's unit, so `2*w + 5` with `w` in mil is 25 mil,
  and a multiplier stays a number. A lone `m` is milli (`2mm` or `2metre`, never `2m`); `check` warns about any
  dimension over 1 m.
  The **Variables** panel (the *x* button) lists each VAR with its expression, unit, value and how many fields
  use it, then the cell's parameters: *Set*, *Rename* (every use is rewritten; `ww` is not touched by renaming
  `w`), *Delete* (refused while anything uses it — *Inline* writes the current numbers into those fields
  first), and *Promote to Cell Parameter*. A VAR with the same name as a cell parameter is **linked** to it:
  it takes the parameter's value — an instance's override, else the cell's default — and editing it edits the
  parameter, so the cell has one default for the name. *Unlink* makes it keep its own value, which then hides
  the parameter from this 3D view (`check` warns). Placing a cell's 3D view, an instance can override its
  parameters in the file (`"Params"`); two instances with the same values are elaborated once.
  **Dragging** a face whose size is an expression changes the *name*, never the field: a bare VAR is given the
  new value, a parameter (or linked VAR) gets a new default, and an expression linear in one name (`2*w + gap`)
  solves for that name — and every other object using it moves with it, in the preview too. A drag of
  anything else (`2*w*l`) is refused, with *Replace with Number* offered. A move or rotation is refused when
  it would change a name another object uses, because only the selection moves while you drag.
- **Setups** (*Simulate ▸ Setup Analyses…* with a 3D view active, or the editor's tune button). A 3D view
  carries its own EM setups, in the same form a `.cem` has, and the dialog lists them as a schematic's
  analyses are listed — one card each, with its kind (SP, ES, MS or EIG) and its solver and sweep: *Add*,
  *Edit*, *Duplicate*, *Rename*, *Remove* and *Make Active*, also on each card's right-click menu with *Run*.
  The **active** setup (the filled radio mark) is the one *Simulate ▸ Run* runs and whose air box is drawn;
  which one is active is remembered per user, not saved in the `.c3d`. **Double-click** a card to edit that
  setup in the same panel a `.cem` opens, less the layout row (the 3D view is the geometry) and the planar
  analyses (a 3D view is solved by Palace or openEMS). Every change is an edit of the 3D view: Undo takes it
  back and Save writes it. A static setup's terminals name their conductors by **object** (`top`, or `U1/pad3` inside a
  placed cell), because a drawn object has no net. *Show 3D* on a `.cem` whose geometry is a `.c3d` opens this
  editor, with that `.cem` listed read-only and active, so its ports and boundaries can be seen.
- **Ports** (**P** in the Shift+A popup, or *3D ▸ Draw ▸ Port*). A port is drawn like a sheet —
  two corners on the drawing plane — or made from a face: right-click a flat, rectangular face and choose
  *Make Port ▸ Lumped* or *Wave*. It takes the next free number and the last port's Z0. **Which way round it
  is comes from what it touches**: each edge of the rectangle is tested against every conductor, and exactly
  one pair of opposite edges must each touch exactly one conductor. The end that is ground — the setup's
  Ground net, a placed layout's ground plane, or a PEC face of the air box — is **−**; otherwise the larger
  conductor is. The port is drawn with its number and an arrow from − to +, and while it is being drawn the
  arrow, or the reason it cannot be one, follows the cursor. Right-click a port for *Flip*, its kind, *Z0…*
  and *Delete*. A wrong polarity turns every transmission term by 180°, so check the arrow before a run.
  A **wave** port must lie on a face of the active setup's air box. Every setup uses every port, and a
  placed cell's own ports are never used: only the parent says where a signal enters.
- **Bond wires** (**W** in the Shift+A popup, or *3D ▸ Draw ▸ Wire*). A wire runs between two metal
  objects or sheets — a die's pad inside a placed cell to a lead in the package is the usual case, which a `.wBond`
  cannot do because it belongs to one layout. Click any face of the object the wire starts on: each end attaches to
  its object's **top**, above the point you clicked (a side face lands just inside the top's edge; a snapped corner or
  face centre on a top is taken exactly). An object with no material, or an insulator, is refused with the reason.
  Then click the object it ends on, then move to set the **loop height** and click, or type it (`8mil`). The
  loop height is the assembly one: from the top of the lower pad to the top of the wire at its highest point.
  The status line shows it and the wire's centre-line loop height side by side, and **the height you type is
  the height the wire measures**. Before the first click the toolbar sets the diameter, the metal, how each end
  is bonded (a **wedge** lays a foot on the pad, a **ball** sits on it) and the section (a flat-bottomed
  hexagon, or round); they start from the last wire drawn. A wire's points are its whole shape: in Vertex mode
  its centre-line points are shown and **G** moves one, and its feet are put back on the pads when you let go
  — an end moved to where there is no pad is refused. When the pad under a wire moves, the wire does **not**
  follow, because re-routing it would change its inductance: the wire is flagged in the tree, drawn in red,
  and a run says which end (`w3's start is no longer on a pad`). *Re-Seat Wire Ends* (right-click the wire, or
  select it in the tree and use *3D ▸ Modify*) moves each end up or down onto the pad now under it; a pad that moved sideways has to be reconnected by
  hand. *Duplicate* and *Array…* copy wires at a pitch, and a copy whose end misses a pad is still made and
  flagged, so a pitch error shows at once. Foot length and ball size come from the workspace's assembly rules
  (`.wasm`) unless the wire's end states a foot length, and the run says when a built-in first guess was used.
- **The air box** (the box button). The active setup's air box, its faces tinted by boundary: **PEC** grey,
  **PMC** orange, **symmetry** hatched, and **absorbing** clear. It is shown until you hide it. In Face mode its faces can be picked, but only where no solid is under the cursor (**B** reaches
  one behind a solid). Right-click one for *Boundary ▸ Absorbing / PEC / PMC / Symmetry* and *Padding…*:
  these change the **setup**, not the geometry, and the status line says which setup they wrote.
- **Boundaries on faces** (Face mode). Right-click a face of a dielectric or air object for
  *Boundary ▸ Perfect Conductor*, *Conductive Surface* (choose the metal) or *None*. The face is drawn tinted
  and listed in the tree under its object. A boundary stays with its face through every edit, and a face
  that is split into pieces hands it to each. A conductor's face takes none — it is already metal — and
  absorbing, PMC and symmetry boundaries belong to the air box, because both solvers can state them only on
  the outside of the problem. Palace finds a boundary's surfaces by the face's extent and counts them, so a
  neighbour's face lying in the same plane and overlapping it is refused rather than guessed.
- **Simulate** (*Simulate ▸ Run* with the 3D view active, or the toolbar's Run button — the workspace toolbar's). Runs the active setup with the
  same progress, Cancel and messages as a `.cem`, and opens its results in the Data Display. Afterwards the
  **Field** bar above the view shows the run's fields, drawn on the geometry the run solved; if the 3D view has
  changed since, a line says so (`Fields are from the run at 14:02; the model has changed since`) and they are
  still shown. An object with **no material** yet — drawn as a wireframe — does not stop the run: the solver
  ignores it and the run's messages say so. One whose material the technology does not define does stop it.
