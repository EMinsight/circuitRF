# Tuning and optimization

**Status:** TO-1 … TO-6 built (model, file format, catalog, in-memory overrides, `check`/`explain`/`reference`;
evaluation service; live session and Data Display; the Tuning panel; presets; the optimizer core).
TO-7 … TO-12 briefed (`docs/sonnet-briefs/brief-tuneopt-*.md`). The overview brief
(`brief-tuneopt-0-overview.md`) holds the decisions D1–D17 in full and is binding; this note is the
standing reference for what is built, and restates the decisions only as far as the code depends on them.

---

## 1. What exists

| Piece | Where | What it is |
|---|---|---|
| The model | `src/Core/Design/TuningSetup.cs` | `TuningSetup` = `TunableEntry` list + `TuningPreset` list + `OptimizationGoal` list + `OptimizerSettings`. `TunableKey` splits a key. `OptimizerAlgorithms.Ids` fixes the algorithm ids. |
| `.cnl` grammar | `src/Core/Netlist/AnalysisDirectiveSchema.Tuning.cs` | The four directives, their keys and bare words — the table the reader checks keys against and the `reference` topics print. |
| `.cnl` read/write | `src/Core/Netlist/TuningDirectiveText.cs` | Reader and writer of `tune`, `preset`, `goal`, `optimize`. Called by `CnlReader` and `CnlWriter`. |
| `.csch` block | `SchematicPersistence` (`CschTuning`) | The `"Tuning"` object; see `project-file-formats.md`. |
| Extraction | `NetExtractor.Extract` / `NetlistSchematic.Build` | Copy the setup across in both directions, so every run verb's `.cnl` carries it with no extra code. |
| Tunable catalog | `src/Design/Optimization/TunableCatalog.cs` | Every tunable of a schematic at any depth, and the setup keys that name nothing. |
| In-memory overrides | `src/Design/Optimization/TunableOverrides.cs` | The design to elaborate with tuned values substituted. Writes nothing. |
| Rules | `src/Design/Optimization/TuningValidator.cs` + `TuningDiagnostics.cs` | What `check` reports and the windows refuse on. |
| CLI | `check`, `explain --tunables`, `reference tuning`, `reference goals` | See `cli.md`. |
| Optimizer algorithms | `src/Engine/Optimization/` | Ask/tell state machines over the unit box: `RandomSearch`, `NelderMead`, `LevenbergMarquardt`, `BfgsB` (§10). |
| Optimizer run | `src/Design/Optimization/OptimizationRun.cs` + `OptimizationVariables`, `GoalResiduals`, `OptimizationDiagnostics` | Decode, goals → residuals, cache, parallel batches, stopping, pause, progress, history (§10). |

**Why the model is in `src/Core` and not `src/Design/Optimization`.** The brief placed the model classes
in `src/Design/Optimization`. A `TestBench` carries the setup and the `.cnl` reader and writer live in
`src/Core`, which cannot reference `src/Design` — so the plain data sits beside `Analysis` and
`Measurement`, and everything that needs a schematic, a resolver or a workspace is in
`src/Design/Optimization`, as the overview's D12 has it.

## 2. What is tunable (D1, D2)

A tunable is a **component parameter** whose stored value is a plain number with an optional unit
(`47`, `47 pF`, `47pF`, `1.2e-9`), or a **VAR row** whose expression is one. Anything else — an
expression, a reference to a cell parameter, a string, an enum, a bool — is **not offered at all**. The
way to tune an expression is to tune the variable it reads.

- **A sub-circuit instance's declared cell parameters are offered per instance** (`X1.Rbias`,
  `X2.Rbias`), whether the instance overrides them or inherits the default. An inherited one is reported
  `IsDefault = true` at the cell's default, so Push knows to add an override.
- **A value inside a cell is identified by the cell DEFINITION** (`DUT:R3.R`): tuning it moves it in every
  instance, and the catalog reports how many (`DUT · ×2`).
- **Only values the drawing holds.** Extraction adds parameters a drawing never stated — a microstrip's
  substrate (`H`, `T`, `Er`, …) comes from the workspace technology. Those are not values of the design and
  Push has no row to write them into, so the catalog offers an instance parameter only when the component
  in the drawing holds it. A `.cnl` holds every value it states.
- **Integer parameters** (`m`, `Nf`, `NumFingers`, `Fingers`) are offered and flagged `IsInteger`.
  Identity numbers (`Num`, `NumPorts`, `NumFreqs`) are not offered.
- A VAR that an **enabled parametric sweep** sweeps is offered with `DisabledReason = "swept by SW1"`.
- **Read-only** (`ReadOnlyReason`): a cell from a kit's netlist rather than a schematic, a cell outside
  the workspace, or a cell whose schematic file is read-only. Such a value can still be tuned and
  optimized; Push skips it.

**The catalog walks the extracted netlist, not the drawing.** `TunableCatalog.Discover(schematic,
resolver, workspaceRoot)` extracts once with the caller's resolver — the one Simulate uses — so cell names
in keys are spelled exactly as the `.cnl` spells instance types (a second `Amp` from another workspace is
`Amp_2` in both), and there is no second resolver to disagree with the first.
`NetExtractor.ExtractionResult.CellKeys` maps each library name back to its cell folder for the read-only
test.

**Default range on first activation (D4):** a positive value v → [v/2, 2v]; a negative one → [1.5v, 0.5v];
zero → [0, 1] in the parameter's unit, flagged `RangeGuessed`. `auto` scale is log when min > 0 and
max/min ≥ 10.

## 2a. Complex values (D18)

A value written as a **complex literal** — numbers only: `40+15j`, `50-j10`, `-3j`, `complex(40,15)` or
`polar(42.7,20.6)`, with an optional unit after the whole — is tuned and optimized **by its parts**,
never whole. Anything that reads a name (`4+j*X`) is an expression and is not offered; tune `X`. A plain
real number stays an ordinary tunable; to tune the imaginary part of `50`, write it `50+0j`.
`ComplexValue.TryParse` (in `src/Design/Optimization/ComplexValue.cs`) is the one test.

- **Four parts, four keys:** `real(K)`, `imag(K)`, `mag(K)`, `phase(K)`, where `K` is the value's own D3
  key. The words are the expression engine's own functions; the phase is in **degrees** (`deg`). Each part
  is an entry of its own — its own `tune`/`opt` flags and range — and **any combination is allowed**:
  real with imaginary, magnitude with phase, or real with magnitude of the same value.
- **One value, four views.** The session holds one complex number per value; every part row shows its
  own view of it. **Moving a part holds its partner in the same coordinate system** — real holds
  imaginary, imaginary holds real, magnitude holds phase, phase holds magnitude — so each slider is one
  straight path in the plane whatever else of the value is tuned (`ComplexValue.With`).
- **The ranges of all of a value's parts always hold, together** (`ComplexRegion`). Every entry of a
  value constrains it, whatever its flags and in either window: a range belongs to the entry, not to a
  window. A part's move that would leave the region **stops at the first edge it meets**. A range edit
  — typed, re-centred, reset, or a first activation's default — that leaves **no** complex value inside
  every range of that value is **refused**, with a sentence naming the other ranges, and `check` reports
  the same condition in a hand-written file as an error (`tuning.range.complex-disjoint`). The emptiness
  test is exact: the plane is cut into ≤ 90° wedges; in each, the real/imaginary box and the wedge make a
  convex polygon over which the magnitude spans [nearest, farthest], which either meets the magnitude
  range or does not.
- **Default ranges:** real, imaginary and magnitude follow D4 (to six significant figures); a phase gets
  [φ − 90°, φ + 90°] and is **linear** (an angle's range is not a ratio; `auto` reads as `lin` on a phase).
  A phase range wider than 360° is an error.
- **A complex value is written whole.** The session request, a preset and Push all carry the WHOLE
  value under its own key `K` (`Zsrc=45+10j Ohm`), **in the form the schematic wrote it** — rectangular
  stays rectangular, `complex(…)` and `polar(…)` stay calls — so a pushed value reads as a typed one.
  `TunableOverrides.Apply` also accepts part keys for a headless caller and composes them with the
  design's value by `ComplexValue.Compose`, the optimizer's own rule (below).
- **Optimizing (TO-6):** each opt-enabled part is one coordinate. One part: its partner is held at the
  start. Two parts of one system set the value directly; a **mixed pair** (real with magnitude, say) is
  solved geometrically, the free sign taken from the start (`Compose`). A decoded point no complex value
  satisfies, or one outside any range of the value, is **infeasible**: not simulated, ranked worse than
  every feasible point. More than two opt-enabled parts of one value is a refusal at Run — a complex
  value has two degrees of freedom — while the others' ranges still limit it.

## 3. Keys (D3)

| Key | Names |
|---|---|
| `R1.R` | parameter `R` of top-level instance `R1` |
| `Wline` | top-level VAR `Wline` (by name — it survives moving the row to another VAR block) |
| `X1.Rbias` | declared cell parameter `Rbias` of top-level sub-circuit instance `X1` |
| `DUT:R3.R` | parameter `R` of `R3` inside cell `DUT` — every instance of `DUT` |
| `DUT:Wline` | VAR `Wline` inside cell `DUT` |
| `DUT:X5.Wf` | declared parameter `Wf` of instance `X5` inside cell `DUT` |
| `mag(Zsrc)` | the magnitude of the complex VAR `Zsrc` — likewise `real(…)`, `imag(…)`, `phase(…)` (D18) |
| `phase(DUT:X5.ZL)` | the phase, in degrees, of a complex parameter inside cell `DUT` |

The cell part is the cell **as the `.cnl` spells its instance type** — `CellScope.NameFor` in
`NetExtractor`: the cell folder's leaf name, or `name_2`, `name_3` … when two cells with one leaf name
meet in one design. **A key that names nothing is never an error**: a preset skips it, `check` warns, a
run notes it.

## 4. The `.cnl` grammar

The authoritative table is `AnalysisDirectiveSchema.TuningDirectives`; `circuitrf reference tuning` and
`reference goals` print it.

```
tune <key> [min=<v> [unit]] [max=<v> [unit]] [scale=auto|lin|log] [step=<v> [unit]]
           [discrete=none|integer|preferred] [tune=1] [opt=1]
preset "<name>" [created=<yyyy-MM-ddTHH:mm:ssZ>] [lasttuned=1] [cost=<c>] <key>=<v> [unit] ...
goal <Name> = <expression> [analysis=<A>] [over=<axis> lo=<v> [unit] hi=<v> [unit]]
     (le|ge|eq) <limit> [unit] [to <limit> [unit]]  |  (in|out) <a> [unit] <b> [unit]
     [weight=<w>] [scale=<v> [unit]] [enabled=false]
optimize [algorithm=<id>] [maxiter=<n>] [maxevals=<n>] [timelimit=<v> s|ms|min|h]
         [cost=lsq|minimax] [analyses=goals|all] [seed=<n>] [parallel=<n>] [alg.<option>=<v>] ...
```

Settled details:
- **Units** follow a value after a space, as `measure` and instance lines write them. A value that would
  not read back as itself is written quoted.
- **A goal's expression** runs from after `=` to the first token, at bracket depth zero, that the grammar
  owns (a `key=` or a goal type), searched from the second token. The writer quotes the expression when
  that rule would end it early (`goal X = "Lout - out" ge 0`). Its own spacing is kept verbatim.
- **Sloped limits** are `le -15 to -10`: the limit at `lo`, `to`, the limit at `hi`. Only `le`/`ge`/`eq`
  slope; `in`/`out` take a band of two limits.
- **Inside a goal line a length in inches is spelled `inch`**, because `in` is a goal type.
- **`timelimit`** takes `s`, `ms`, `min` or `h`. These are local to this grammar: the shared unit table
  has no time units, and adding a bare `s` there would change what a bare word after an instance-line value
  means.
- **`created`/`lasttuned`/`cost` come before a preset's values**; once a value key is seen, every later
  `key=value` is a value. (So a preset whose FIRST value is a VAR named `created`, `lasttuned` or `cost`
  does not round-trip — the same limit `created` always had.)
- **Booleans** read `1/0/true/false/yes/no`; the writer writes `1` and omits defaults.
- **Unknown keys** are a warning naming the key (`cnl.tuning.unknown-key`) and are **kept** in the
  record's `Extra` map and written back — the room yield's tolerances will need (overview §5).
- **A malformed line is refused** with a `cnl.tuning.*` diagnostic (`TuningDirectiveDiagnostics`) and its
  line number.
- **Byte stability:** `CnlWriter → CnlReader → CnlWriter` and `.csch → .cnl → .csch → .cnl` reproduce the
  tuning lines exactly. `TuningPreset.Created` is held to the whole second, the precision the `.cnl`
  spelling carries.

## 5. Applying values in memory

`TunableOverrides.Apply(tb, lib, values, setVariables)` returns copies of the testbench and library with
each value substituted — a top-level instance parameter or VAR in the testbench, a cell's in its library
definition, an inherited cell parameter as an added override. Objects are replaced, never mutated, so the
input keeps its own. A value written without a unit keeps the row's existing unit (typing `47` into a row
whose unit column says pF means 47 pF). **The gate is equality with typing**: elaborating the result equals
elaborating a design in which the same text had been typed.

`--set` applies first; a tuned value naming a variable `--set` also sets is a refusal naming both.

## 6. What `check` reports

Errors: min ≥ max; `scale=log` with min ≤ 0; a bound, limit or range end that is not a number; a goal
whose analysis is not declared; a goal range that holds no point of the analysis's grid (computed for
`freq` of an S-parameter sweep, also through a wrapping parametric sweep, and for a sweep's own variable;
other axes are checked at run time); an expression that does not parse; an `in`/`out` goal without two
limits or with an inverted band; a sloped limit with no range; a key naming a value D1 does not offer
(a complex value named whole, or a part of a value that is not complex, included); a phase range wider
than 360°; ranges of one complex value's parts that leave no value inside all of them (D18);
an unknown algorithm id; a `timelimit` that is not a duration. **Warning:** a key — in a `tune` line or a
preset — that names nothing. An unknown function name in a goal expression parses, exactly as in a
`measure` line; it is reported where `measure` reports one.

## 7. Room left for yield

A variable entry, a goal and the optimizer settings each carry an `Extra` map, and both serializations
keep unknown keys, so a tolerance can be added to a `tune` line without a format break.

## 8. The Tuning panel (TO-4)

| Piece | Where |
|---|---|
| Panel state | `src/Ui/Tuning/TuningPanelViewModel.cs` (+ `TuningRowViewModel`, `TuningAddViewModel`, `TuningScopeViewModel`) |
| Dock + view | `TuningTool`, `DockPanelIds.Tuning`, `src/Ui/Views/Tuning/TuningToolView.axaml` |
| Shell wiring | `WorkspaceViewModel.Tuning.cs` — routing, session creation, sub-cell sessions, reveal |
| Slider maths | `TuningSliderMapping` (log/lin position, integer and step snap, keyboard nudges) |
| Entry edits | `src/Design/Optimization/TuningSetupEdits.cs` (pure) → `SetTuningSetupCommand` (one undo step) |
| Push | `TuningPush` → one `CommandBatch` per owning document |
| Canvas colour | `SchematicViewModel.TunedLabels` → `SchematicOverlay.TunedLabels` → `SchematicRenderTheme.TunedText` |

- **The tuned schematic is the focused tab's TOP frame**, so pushing into a sub-cell keeps tuning the
  bench. A non-schematic document clears the panel and stops the session — except a Data Display while a
  session runs, because that is where its results are watched.
- **One key mapping.** `TunableCatalog.KeysFor(drawing, component, parameter)` is what the Inspector toggle,
  the canvas's right-click ▸ Tune and the panel agree on — one key for a plain number, four part keys for
  a complex value, which the Inspector and the canvas offer as a menu of checkable parts; `TunableCatalog.Drawings` gives each scope's
  drawing — the open session's own model in the GUI, so Push edits what is on screen.
- **Discovery is lazy.** A schematic with nothing tuned never extracts on an edit; the catalog is computed
  when a row, the Add… list or the Inspector asks.
- **The session evaluates what Simulate would**: the same `NetExtractor.Extract` with the workspace
  resolver and corner bindings, written by `CnlWriter` and read back in memory (`PreparedCircuit.FromText`)
  rather than through `netlist.cnl`, which Simulate owns.
- **Push** writes `G15` numbers with the row's own unit spelling where it means the same unit (a complex
  value whole, in the form the schematic wrote it, D18); an
  instance inheriting a cell default gains the override (`AddParameterCommand`). A sub-cell session with no
  tab is opened with `CircuitRfDockFactory.OpenDocumentInBackground`.
- **A layout saved before TO-4** gains the panel beside wherever it put Analyses
  (`DockLayoutDefaults.WithMissingPanelsFilled`'s `TabbedBehind` rule), not at the default column.

## 9. Presets (TO-5)

| Piece | Where |
|---|---|
| Recall | `src/Design/Optimization/PresetRecall.cs` — `Apply(preset, catalog, setup)` → values to load, a per-key report, and the setup with recalled keys tune-enabled and ranges widened |
| Edits | `src/Design/Optimization/TuningPresets.cs` — `LockIn`, `WithLastTuned`, `Rename`/`Duplicate`/`Delete` (by INDEX: a hand-written file may repeat a name), `Compare`, `CnlText` |
| `.cnl` line | `TuningDirectiveText.WritePreset` / `ReadPresetLine` — "Copy as .cnl" and its inverse |
| Panel | `TuningPanelViewModel.Presets.cs` + `TuningPresetItemViewModel`; the drop-down and Compare table in `TuningToolView.axaml` |
| Last tuned | `TuningPanelViewModel.StoreLastTuned`, called by `WorkspaceViewModel.StoreLastTuned` from every schematic save and close path |

- **Recall never refuses.** Each key is *applied*, *widened* (outside its entry's range: applied, and the
  bound moved to the value), *missing* (names nothing), *no longer tunable* (now an expression, a value
  that is not complex any more, or swept), or *no longer applicable* (not a number; complex parts no value
  has). The report is one sentence (`Applied 11 of 12 · R7 no longer exists`) plus one line per key.
- **Recall loads; it does not push.** Values go into the panel (and the running session). The only document
  edit is the setup's: a recalled key that was not tune-enabled is turned on — a complex value with no tuned
  part gets real + imaginary, or magnitude + phase when written `polar(…)` — and widened ranges. One undo
  step. Widening only grows a complex value's region, so it never makes a D18 conflict.
- **Lock in stores every tune-enabled row**, moved or not — the schematic's own text for an unmoved one, a
  complex value once and whole. `TuningPreset.Cost` is the Optimizer's (TO-10), null for a person's.
- **Last tuned** is written only when some row differs from the schematic, and not again when it already
  holds the same values (so a second save does not dirty the document). Writing it is an ordinary undoable
  setup edit, so closing a clean schematic with unpushed values asks to save. Renaming "Last tuned" makes it
  an ordinary preset; the next save writes a new one.
- **The drop-down** orders Last tuned first, then by `Created`, newest first. Compare takes the schematic,
  or the preset earlier in the file, as the first side; the difference is second − first, per tuned part for a complex
  value (all four when none is tuned).

## 10. The optimizer core (TO-6)

**Shape.** Every algorithm is an ask/tell state machine over the unit box [0, 1]ⁿ
(`IOptimizerAlgorithm`): `Ask` hands out a batch, `Tell` gives back a cost, a residual vector and a
failed flag per point. No algorithm calls the simulator. Each is written as ONE iterator whose locals
hold all of its state (`AskTellAlgorithm`), so `Capture` is the list of batches told and `Restore` is a
replay of them into a fresh instance, checked point for point — plain numbers a checkpoint can hold.
`SplitMix64` is the seeded generator: its output is fixed by the seed on every platform.

**Variables (`OptimizationVariables`).** Each opt-enabled entry is one coordinate: linear, or
logarithmic per its effective `Scale` (a phase is always linear). Integers and steps are applied when a
point is DECODED, so the cache and the history see the value simulated. A decoded value is written as
the parameter's value text (`G15`, its own unit); a complex value is composed WHOLE by
`ComplexValue.Compose` from its start and written in the schematic's form. An entry with no range uses
the catalog's default and says so. A start outside its range is moved to the nearest bound and
reported; a complex start outside its region is moved to the nearest point of the start's own
coordinate paths (`ComplexRegion.Move` from the feasible grid point nearest the start), reported.

**Goal residuals (`GoalResiduals`) — the rule users read.**
- *Violation per grid point*: `le` max(0, x − L); `ge` max(0, L − x); `eq` |x − L|; `in` [a, b] the
  distance to the interval; `out` [a, b] the distance to the nearer edge when inside, else 0. A sloped
  limit interpolates L linearly along the range axis.
- *Scale*: the goal's `scale=` when given; otherwise the band's width b − a for `in`/`out`, and
  **max(|L|, 1) in the limit's own unit** for the others (the larger end of a sloped limit). So −15 dB
  is worth 15 dB, a limit of 0.5 is worth 1, and 2 pF is worth 2 pF rather than 1 F.
- *Residual*: violation ÷ scale × weight ÷ √(points), so a 201-point goal and a scalar goal weigh alike.
- *Cost*: least squares Σ r² (default) or minimax max r over the concatenated residual vector.
- *Met*: a violation within 1e-9 of the goal's scale counts as zero. Least squares approaches a one-sided
  limit from the violating side and lands on it only to rounding (|S11| = 1e-4 + 5e-15 against
  `le 1e-4`), so without it "all goals met" would never fire on an inequality.
- A complex result, a range axis the value does not have, a range holding no grid point, a non-finite
  value: errors (`opt.goal.*`). A goal that cannot be scored at the START point is a refusal (exit 1),
  not a hundred failed evaluations.
- A goal with no analysis reads variables only and costs no simulation; when no goal names an analysis
  nothing is simulated at all.

**Failures and infeasible points.** A failed evaluation (status not Success, an analysis reporting
`Converged = false`, a goal error) costs **10·(1 + the largest successful cost seen)**, assigned after
the batch in batch order so concurrency cannot change it. An infeasible complex point is not simulated
and costs that plus its normalized distance to the region. Both reach the algorithm as `Failed`:
Levenberg–Marquardt and BFGS-B shrink the step; Nelder–Mead and Random rank it.

**Counters.** `Evaluations` = simulations run; `CacheHits` = points the cache (keyed by the decoded
value vector) or an earlier point of the same batch answered; `Infeasible`; `Failures`.

**Parallel.** A batch's uncached points run concurrently up to `parallel=` (default cores − 1, capped by
the batch), only when `PreparedCircuit.IsReentrant`; otherwise one at a time, noted once
(`opt.parallel.serial`).

**Stopping (first that applies).** Every enabled goal met (best cost 0); the algorithm's own finish;
`maxiter` (default **100**); `maxevals` (simulations); `timelimit`; **stall** — the best cost improved by
less than `alg.stall_tol` (default 1e-9) of itself over `alg.stall_iters` (default 25) iterations; Stop.
Cancellation abandons the run (exit 130) and reports no best.

**Pause (D16).** `Pause` holds after the batch in flight; `Resume` continues. The run is identical,
evaluation for evaluation, to an unpaused one with the same seed.

**Outcome (D15).** `GoalsMet` 0 · `GoalsUnmet` 3 (each goal reported with its worst violation, where on
its axis, and the value there) · `Refused` 1 · `NoConvergence` 2 (no evaluation succeeded) ·
`Cancelled` 130.

**Progress (R-to6-9)** — `OptimizationProgress`, after every iteration: iteration, evaluations,
failures, infeasible, cache hits, elapsed, the iteration's best cost, the best cost, the best values,
per-goal worst violation and met at the best point, railed values, and the best point's `DataSet`.

**Railed (D17).** A coordinate within 0.5 % of the box of a bound; a complex value held within 0.5 %
of ANOTHER part's range is reported against that part (`RailedVariable.Against`).

**History (R-to6-10)** — group `opt`: over `eval` (every point told, in order) `cost`, `failed`,
`infeasible`, `cached` and one cube per value key in base SI (complex for a complex value); over
`iter` `best_cost` and `worst_<goal>`. The caller decides whether to write it.

**Algorithms and options** (`alg.<name>=` on the `optimize` line):

| Id | Batch | Options |
|---|---|---|
| `random` | `batch` points per iteration (default max(8, 2n)), the first carrying the start | `batch`, `lhs` (1 = Latin hypercube, default; 0 = uniform) |
| `simplex` | 1 (n+1 to build, n to shrink) | `step` (0.1), `xtol` (1e-6), `restarts` (1) — Gao–Han adaptive coefficients, projection onto the box |
| `lm` | n for the forward-difference Jacobian, then 1 per trial | `fdstep` (1e-6), `lambda` (1e-3) |
| `bfgsb` | n for the gradient, then 1 per line-search point | `fdstep` (1e-6), `memory` (5) |

- **Levenberg–Marquardt uses Levenberg's damping (JᵀJ + λ·s·I), not Marquardt's diag(JᵀJ).** The unit
  box already puts the coordinates on one scale; Marquardt's scaling lengthens the step along the
  coordinate the residuals barely feel, and with one goal over two values it stalled 7 % short of a
  reachable limit. Bounds: a coordinate on a bound whose gradient points outward is held and left out
  of the solve. A failed difference point is retried on the other side. `cost=minimax` is refused for
  `lm` (`opt.algorithm.lsq-only`).
- **BFGS-B** is limited-memory BFGS on the free coordinates with a projected backtracking (Armijo)
  line search; a direction that is not downhill resets the memory to steepest descent.
- **The difference step is 1e-6 of the box.** 1e-4 left the gradient error larger than the gradient
  itself near a Rosenbrock optimum and BFGS-B stopped at cost 0.01. A discrete or stepped coordinate
  decodes both sides of a difference to one value (a cache hit) and so has no gradient — a known limit
  of the gradient methods until TO-8's re-polish.
- **Auto** runs Levenberg–Marquardt (Nelder–Mead under `cost=minimax`) and says so (`opt.algorithm.auto`)
  until TO-8 adds its global stage. Algorithms not built yet are refused naming those that are.
