# Brief 3 — a kit's layer stack and materials become a `.ctech`

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf3-n` · **Phase:** technology
**Area:** `tools/pcell-python/gdsfactory_bridge/technology.py` (new), `src/Ui/Layout/TechImport/` → moved to
`src/Design/Layout/TechImport/` (`ProcessTechnologyBuilder`, `ProcessStackReader`, `LayerPropertiesReader`,
`RuleDeckReader`, `ProcessTechnologyImport`), new `src/Design/Layout/TechImport/GdsfactoryStackConverter.cs`,
`src/Ui/GlobalUsings.cs`, the Import PDK flow, `src/Cli/Authoring.cs`, `src/Design/RESOLVED.md`
**Depends on:** 2 · **Blocks:** 5
**Rule:** overview §0.

---

## 0. Why this is the most valuable part of the series

A gdsfactory kit states its process **as data a program can read**: z, thickness and material for
each layer, and in kit A, conductivity and permittivity for each material. That is what an on-chip EM
setup needs, and today a user has to type it in by hand. Measured (overview §2, finding 6):

| Kit | Patterned levels | Background dielectrics | Material properties |
|---|---|---|---|
| A: 130 nm SiGe BiCMOS | 32 | **none as levels** | σ and εr cards, including the oxide, the substrate, the passivation and air |
| B: superconducting quantum RF | 10 | substrate and vacuum **are** levels | names only |
| C: 130 nm CMOS | 18 | none | none (every metal is `metal`) |
| D: 180 nm CMOS | 25 | substrate level only | names only |

So a conversion has to fill gaps, and **every value it fills in must be reported**. A plausible
number nobody chose, sitting silently in a stack, is the defect this codebase has learned to fear
most.

## 1. `R-gdf3-1` — read it out (Python)

- **`R-gdf3-1a`** `python -I -m gdsfactory_bridge.technology <module>` prints one JSON document on
  stdout and exits. It is run through the same consent, interpreter and `KitRoot` as brief 2 (it runs
  kit code). The document holds:
  - `layers`: name → `[layer, datatype]`.
  - `layerViews`: written with the kit's own `layer_views.to_lyp(path)` into a temp file. The **path**
    is returned, and circuitRF reads it with the existing `LayerPropertiesReader`. Do not invent a
    second colour format.
  - `stack`: per level: `name`, `layer` (resolved to `[layer, datatype]` when the level names a
    single layer, else the expression as text), `zmin`, `thickness`, `material`, `sidewall_angle`,
    `info`.
  - `materials`: name → `{sigma, epsr, tand}`. Found **by type, never by module name**: any object at
    module level in the kit's package that exposes an RF regime with conductivity and permittivity
    (the open material-card schema kit A uses), plus gdsfactory's own material index if the `Pdk`
    carries one. Missing values are `null`, never 0.
  - `problems`: what could not be read.

## 2. `R-gdf3-2` — convert, then build with the ONE builder

- **`R-gdf3-2a`** Move `src/Ui/Layout/TechImport/*` to `src/Design/Layout/TechImport/`. These are
  framework-free already, and `Rgba` is in `src/Design`. List the namespace once in
  `src/Ui/GlobalUsings.cs`. This is the route `src/Design`'s interchange code already took. A
  headless `import pdk` needs it.
- **`R-gdf3-2b`** `GdsfactoryStackConverter` turns the JSON into a `ProcessStackDescription` plus a
  `ProcessLayerTable`, then calls **`ProcessTechnologyBuilder.Build`**. There is no second builder.
  Any rule the builder lacks is added to the builder, so a stack file and a gdsfactory kit can never
  disagree. Classification, in this order, each step stated in the notes:
  1. **Via:** a level whose z-span runs from the top of one conductor to the bottom of another. Its
     `SpanFrom`/`SpanTo` are those two conductors.
  2. **Conductor:** a material with σ ≥ 1e5 S/m. With no material property, a level that a via
     spans counts as a conductor, and the note says that is how it was decided.
  3. **Device layer:** wells, implants and anything at or below the top of the substrate that is
     neither via nor conductor. These map onto the builder's existing device-layer path (the role the
     stack format calls `LayerType`). They are never EM conductors.
  4. **Dielectric:** a level whose material has εr and σ below the conductor threshold.
- **`R-gdf3-2c`** **Gap filling.** Between the substrate top and the highest level, every z interval
  that no dielectric level covers becomes a dielectric slab. Its εr is chosen in this order:
  1. a dielectric material card the kit itself names as the background: the material occupying
     the most volume among stated dielectric levels, or a card whose name the kit's own material
     module marks as the inter-metal oxide. **Never match on a material's name inside circuitRF.**
  2. otherwise **εr = 1**, with a **WARNING** note: "εr unknown between z = … and … µm; set before
     running EM".

  The **substrate** works the same way. Its thickness comes from a substrate level when the kit
  states one (kit B, kit D). Otherwise it uses the builder's default, with a WARNING naming it.
- **`R-gdf3-2d`** Every filled value, and every value the kit stated but circuitRF could not use
  (a sidewall angle, a level the stack expresses as a layer expression), is a NOTE in the import
  report. The report counts them in its first line: "Technology built: 14 conductors, 13 vias, 9
  filled values (see notes)".
- **`R-gdf3-2e`** Levels that overlap in z (kit A's MIM dielectric and plate inside the via-to-top-metal
  gap) go through the builder's existing embedded-conductor rule. If that rule cannot place them, the
  builder reports it as it already does. **Do not flatten them silently.**

## 3. `R-gdf3-3` — flows

- **`R-gdf3-3a`** Importing a gdsfactory kit (brief 2 §5) offers **"Also build the kit's technology"**,
  checked by default when the kit has a stack.
  - The result is written as a `.ctech` beside the workspace's other technologies.
  - It is set as the default technology only if the workspace has none.
  - It is never written over an existing file with the same name: the import asks, as File ▸ Import ▸
    Technology does.
- **`R-gdf3-3b`** `circuitrf import pdk … --technology` writes it headless. Without the flag, nothing
  is written.

## 4. Tests

Run only the classes you touch.

**Tier A (routine, synthetic JSON, no Python):**

1. A synthetic stack (substrate, two metals, one via, one MIM pair, a well) classifies each level as
   specified. The via spans the right conductors.
2. Gap filling produces exactly the uncovered intervals. Every fill is a NOTE. With no dielectric card
   the fill is εr = 1 with the WARNING.
3. A level with no material properties is classified by the via-span rule, and the note says so.
4. The same stack expressed as a stack **file** and as gdsfactory JSON builds byte-identical `.ctech`
   files. **One builder.**
5. After the move, the existing technology-import tests pass unmodified. Only their namespaces change.

**Tier B (`[GdsfactoryFact]`):**

6. The synthetic kit's stack round-trips through the real `technology.py`.
7. Kit E's stack builds with zero refusals. Its note count is printed for the completion record.

## 5. Acceptance (tier C, owner-run, recorded by process)

- **Kit A — a measured reference, the one the series can hold itself to.**
  - Kit A's upstream tree publishes two-port **measurements** of a spiral inductor. Two measured dies
    give a differential L of about 1.67 nH at 1 GHz, extracted from `Z11 − Z12 − Z21 + Z22`.
  - Locate the measurement and the inductor's geometry. Build the same inductor from the kit's own
    inductor cell, with the technology from this brief.
  - Run circuitRF's planar EM at 1 GHz only: one short run. No sweep, and no mesh-convergence study.
  - Compare the differential L. The gate is **±10 %**.
  - Other public attempts at this structure came out about 2× low, so a pass here means something.
  - **A miss is reported, never tuned to.** If the measurement cannot be found, stop and report rather
    than substitute a simulated reference.
- **Kit B:** a straight CPW from the kit's own cell on the converted substrate. Its Z0 from
  circuitRF's EM, against the textbook conformal-mapping closed form for CPW on a substrate of the
  converted thickness and εr. The gate is **±3 %**. This is an independent formula, not circuitRF's
  own line model.
- **Kits C and D:** the technology builds, with the WARNING count recorded. No EM gate. Their stacks
  carry no material properties, and a number we invented could not be checked against anything.
