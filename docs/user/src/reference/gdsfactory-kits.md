---
title: Using a gdsfactory kit
slug: reference/gdsfactory-kits.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > gdsfactory kits
lede: Layout cells, ports and a technology from an open kit written as a gdsfactory package.
keywords: gdsfactory, open kit, open PDK, Python kit, layout cells, PCell, layer stack, technology, pip
---

Some open process kits are published as a **gdsfactory package**: a Python package that defines the
kit's layout cells, its layers and its layer stack. circuitRF runs those cells as its own
[PCells](pcells.html) and builds a technology from the stack.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#two-kinds">Two kinds of open kit</a></li>
<li><a href="#install">What to install</a></li>
<li><a href="#import">Importing the kit</a></li>
<li><a href="#consent">The consent prompt</a></li>
<li><a href="#get">What you get</a></li>
<li><a href="#not">What you do not get</a></li>
<li><a href="#headless">From the command line</a></li>
</ol>
</nav>

## Two kinds of open kit {#two-kinds}

An open kit can reach you in two forms, and they supply different halves of a design. Decide which
one you need first.

| | **The kit's own tree** | **A gdsfactory kit** |
|---|---|---|
| What it is | A folder downloaded from the kit's publisher | A Python package installed with `pip` into your own Python (3.12 or later). Installing it installs gdsfactory and its layout engine too |
| How it comes in | **File ▸ Import ▸ PDK… ▸ Kit folder…** — no Python needed | **File ▸ Import ▸ PDK… ▸ A Python package (gdsfactory)…** |
| What it gives | The **schematic side**: parts with symbols, their models and corners, and whatever layout cells circuitRF can run | The **layout side**: parametric layout cells, their ports (position, width and direction), and the kit's layer stack and materials as a technology |

**Which do I need?**

- **Schematic and simulation:** the kit's own tree. [PDK Integration](pdk-integration.html) covers it.
- **Layout artwork, or a technology for EM:** the gdsfactory kit.
- **Both:** import both. A layout cell that declares the same device model as a part merges onto that
  part's tile in the Library palette: symbol and model from the tree, artwork from the package. Either
  works without the other. Where the kit's own tree has Python layout cells too, a cell both forms
  offer under the same name is drawn by the gdsfactory package, and Messages names each such cell.

**What a gdsfactory kit adds over the kit's own tree:**

- layout cells, where the kit's own are written for another layout tool's macro system, which
  circuitRF cannot run;
- RF structures the kit's own tree does not have, such as lines, couplers and filters;
- kits that exist only as a gdsfactory package;
- ports with geometry, which become the cells' pins;
- the layer stack, with material properties where the kit states them.

**What it does not add:** schematic symbols, circuit models and corners. Those come only from the kit's
own tree. (A few kits ship their own drawing of each cell; circuitRF turns those into symbols, with pins
placed from the ports. A symbol still gives a cell no circuit model.)

**Nothing else to install.** The layout engine comes with gdsfactory when `pip` installs it; no separate
layout application is needed, and circuitRF installs nothing. A kit's own tree needs no Python at all
unless its layout cells are Python, and then they run under the same consent prompt.

**GDS export is circuitRF's.** A gdsfactory cell's geometry becomes an ordinary generated cell in your
layout. It exports through **File ▸ Export** and `circuitrf convert` like any other cell, and exporting
never needs Python.

## What to install {#install}

1. **Python 3.12 or later.** Some kits require a specific version; circuitRF reads the kit's own
   requirement and refuses an interpreter that does not meet it, naming its version.
2. **A Python environment for the kit**, such as a virtual environment, so the kit's own pinned
   gdsfactory version does not disturb anything else.
3. **The kit**, with `pip install` into that environment. This installs the gdsfactory version the kit
   asks for.

<div class="callout note">
<span class="label">A kit that lists a proprietary dependency</span>
<p>Some kits list an optional proprietary package as a dependency although none of their cells use it.
Install such a kit with <code>pip install --no-deps</code>, then install its open dependencies
(gdsfactory among them) yourself. circuitRF only imports gdsfactory and the kit. If a cell does need a
module that is missing, the import report names that module, exactly as the error states it.</p>
</div>

circuitRF never runs `pip` and never installs anything into your environment.

## Importing the kit {#import}

**File ▸ Import ▸ PDK… ▸ A Python package (gdsfactory)…**

1. **Pick the interpreter**: the Python whose environment holds the kit. Interpreters found on your
   `PATH` are offered, and **Choose…** finds any other.
2. **Pick the kit.** circuitRF finds kits by **reading files only**: each installed package's metadata
   that names gdsfactory as a requirement. No kit code runs to list them. A kit that needs a newer
   Python than the one you picked is listed, dimmed, with the reason.
3. **Answer the consent prompt** (below).
4. **Read the report.**

**Kit folder…** works too, on a folder holding a gdsfactory kit's source, and takes the same steps.

**The interpreter is yours, not the workspace's.** It is remembered per user, by the kit's module name,
and never written into the workspace. A colleague who opens the workspace is asked for their own once.
The workspace records only the module and the version last seen; a different installed version is a
note, never a refusal. To change environments, import the kit again and pick the other interpreter.

{{ui: gdsfactory-import-report}}

The report lists:

- **the cells offered**, with the device model each one declares;
- **the cells not offered, grouped by reason**: a cell with required arguments has no defaults to draw
  from; a cell gdsfactory itself cannot build is listed with gdsfactory's own message; a cell named like
  one of circuitRF's built-in cells is not reachable, because the built-in is found first;
- **parameters not offered**: a default that is not a number, a flag or text (a list, a function, a
  cross-section object) keeps the kit's own default and has no field to edit;
- **ports not made pins**: a `placement` port is an alignment mark. Every other port becomes a pin,
  whatever its type;
- **every model pairing** with a part from the kit's own tree, and every pairing refused, with the
  parameters that did not match.

**Also build the kit's technology** writes `tech/<module>.ctech` from the kit's layer stack, layer
colours and material cards. It becomes the workspace's default technology only if the workspace has
none. A stack lists only the layers that are drawn, so circuitRF fills what it does not state (the
substrate, the dielectric between metals) and **lists every value it had to fill**. A permittivity it had
nothing to go on for is a warning to set before running EM. Check the
[Stackup](stackup.html) before the first EM run.

## The consent prompt {#consent}

A gdsfactory kit is Python code, and nothing of it runs until you allow it.

{{ui: gdsfactory-trust}}

The prompt names the package, its version, where it is installed and the interpreter that would run
it. Your answer is remembered on this machine, keyed by the kit's installed directory, so the same kit
in a different environment asks again. It never travels with a workspace. **Don't Allow** leaves the
kit's cells as placeholders and changes nothing else. Settings ▸ General ▸ Generated Artwork asks again.

## What you get {#get}

- **Layout cells in the Library palette**, under the kit's own heading. A cell that declares the same
  model as a part from the kit's own tree merges onto that part's tile; any other cell gets a
  layout-only tile of its own.
- **Placed cells regenerate** like any other PCell: change a parameter and the kit draws the cell again
  through gdsfactory. A cell's pins are its ports.
- **A technology** built from the kit's stack, with every filled value listed.
- **Export** through File ▸ Export or `circuitrf convert`, with no Python involved.

## What you do not get {#not}

- **Circuit models.** A gdsfactory package has none circuitRF can use. Import the kit's own tree for
  those (above).
- **Drag handles** on gdsfactory cells. Handles need parameters circuitRF knows to be lengths, and
  circuitRF does not guess which of a kit's numbers are lengths.
- **Units on parameters.** Numbers are passed to the kit exactly as you enter them, **in the kit's own
  units: µm for dimensions**. Not every number is a dimension (an inductance or a frequency may be a
  parameter too), so none is converted. The parameter's tooltip says so.

## From the command line {#headless}

```
circuitrf import pdk --module <module> --python <interpreter> --into <workspace> --allow-code --technology
```

`--allow-code` allows the kit for that one command and records nothing. Without it the kit is declared
but not run. Every command that regenerates cells (`render`, `convert`, `check` and the rest) takes
`--python` for the interpreter and `--trust-kit <kit directory>` for consent; on a machine where you have
already allowed the kit, neither is needed. [The command line](cli.html) has the details.
