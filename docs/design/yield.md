# Monte Carlo, yield, corners and design centering

**Series:** `docs/sonnet-briefs/brief-yield-0-overview.md` (decisions D1–D14) · **Builds on:**
`docs/design/tuning-optimization.md` (its §19 lists the seams this series takes) and the PDK corner work
(`src/Core/Pdk/PdkCorners.cs`, `src/Ui/Schematic/WorkspaceCorners.cs`, `docs/design/spice-models.md` §8.11).

This note records the model and grammar as built, then one section per phase as it lands.

---

## 1. YA-1 — the model

There is **one** setup block per testbench, the tuning setup (`TuningSetup`, `src/Core/Design`), and the
statistical content lives in it (D1): a tolerance is part of the variable entry, a yield spec is a goal.

| Type | What it holds |
|---|---|
| `TunableEntry` | gains `Stat` (bool), `Distribution` (`None\|Gauss\|Unif\|LogNorm\|Discrete`) and `Spread` (`StatSpread`: `Sd`, `Tol`, `Sigmas`, `Lo`, `Hi`, `By`, `Trunc`, each the TEXT written). `IsStatistical` = `Stat` and a distribution. |
| `OptimizationGoal` | gains `Use` (`Both\|Opt\|Yield`, default `Both`); `ForOptimizer` / `ForYield`. |
| `StatisticsSettings` | the `statistics` line: trials, seed, sampling, target, confidence, autostop, nonconverged, save, process, mismatch, sigmascale, parallel, analyses, corners. Every default is held as null (or the enum's first member), so neither serialization ever writes one. |
| `StatCorrelation` | two keys and ρ. |
| `CornerDefinition` | name, enabled, kit `AxisSelections` (`.csch` only), `Temp`, `Values` (global names and tunable keys → value text), and a statistical origin (`Trial`, `Seed`, `Sampling`, `Trials`). |
| `TuningSetup` | gains `Correlations`, `Statistics`, `Corners`. |

**Why the spread is text.** The form a value was written in is part of what it means (D2): a percent
follows the nominal when centering moves it, an absolute value does not. Keeping the text keeps the form
and makes the writer's output byte-stable for free, the same reason a tuning bound is text.

**Defaults** (where the overview named none, this phase chose): `trials` **100**, `seed` 1, `sampling`
random, `confidence` 95 %, `nonconverged` fail, `save` auto, `process`/`mismatch` on, `sigmascale` 1,
`analyses` goals, `corners` none.

### 1.1 The spread in numbers (`ResolvedSpread`, `src/Design/Statistics`)

A spread is resolved against the tunable's nominal into base SI:

- **A width** (`sd`, `tol`, `by`) written as a percent is that percent of |nominal|; otherwise a value
  with its unit, a bare number being in the parameter's own unit.
- **An end** (`lo`, `hi`) written as a percent is that percent **of** the nominal — `lo=90% hi=110%` is
  0.9 to 1.1 times it, not an offset.
- `gauss`/`lognorm`: σ = `sd`, or `tol`/`sigmas`. For `lognorm`, σ/nominal is the log's σ.
- `unif`: [nominal − tol, nominal + tol] or [lo, hi]. `discrete`: lo, lo+by, … ≤ hi.

**The non-physical probability** (D2's warning) is P(draw ≤ 0) for a value that must be positive — a
resistance, capacitance, inductance, conductance or length (by `Units.BaseUnit`) or a magnitude part, with a
positive nominal. A truncated normal is renormalized over its ±k σ window: P = (Φ(−μ/σ) − Φ(−k)) / (1 − 2Φ(−k))
when μ/σ < k, else 0. Above `1e-9` it is a `check` warning naming `trunc=` and `lognorm`; that threshold puts
an untruncated Gaussian resistance's limit at about 16.7 % (6 σ).

## 2. YA-1 — the `.cnl` grammar

`TuningDirectiveText` (`src/Core/Netlist`) reads and writes the four tuning directives and three more; the
schema is `AnalysisDirectiveSchema.TuningDirectives`, which is also what `reference` prints. The spelling
rules are the tuning grammar's: a unit after a space, `%` glued to its number, booleans `1/0/true/false/
yes/no`, an unknown key a warning that is kept, a default never written.

```
tune R1.R   min=10 Ohm max=200 Ohm opt=1 dist=gauss sd=2%
tune C1.C   dist=unif tol=0.1 pF
tune L1.L   dist=gauss tol=5% sigmas=3 trunc=3
tune mag(ZL) dist=unif lo=45 Ohm hi=55 Ohm stat=0
tune X1.Nf  dist=discrete lo=4 hi=8 by=2
correlate R1.R R2.R rho=0.9
statistics trials=500 seed=7 sampling=lhs target=95%
goal S21 = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge 14 use=yield
corner SS_hot temp=85 Vdd=3.0 V R1.R=47 Ohm
corner Worst_S21 trial=417 seed=7 sampling=lhs trials=500
```

- **`tune`** — the statistical keys come after the tuning ones in the order `dist`, `sd`, `tol`, `sigmas`,
  `lo`, `hi`, `by`, `trunc`, then `stat=0` only when it says something `dist` does not imply. `stat`
  defaults to 1 when `dist` is present wherever on the line `stat=` sits.
- **`correlate <key> <key> rho=<v>`** — `rho` required.
- **`statistics`** — at most one line (`cnl.statistics.repeated`). `target`/`confidence` are percents;
  `save` is `auto|scalars|all|<n>`; `corners` is `none|all|<name>,<name>`.
- **`corner <Name>`** — a one-word name. `enabled`, `trial`, `seed`, `sampling`, `trials` and `temp` are
  the line's own words **wherever they sit**; every other key is a binding and must be a variable's name or
  a tunable key, bound once. A global variable named like one of those words cannot be bound by a corner
  (`temp` is that global already). The writer writes the words first, then the bindings.
- **`goal … use=opt|yield`** — written after `enabled=`, absent at `both`.

A malformed line is refused with its line number: `cnl.statistics.malformed`, `cnl.statistics.value-invalid`
(also for a statistical key on a `tune` line), `cnl.corner.malformed`, `cnl.corner.value-invalid`.

**Byte stability.** `CnlWriter → CnlReader → CnlWriter` reproduces every line, and so does
`.csch → .cnl → .csch → .cnl` (`StatisticsSetupRoundTripTests`).

## 3. YA-1 — `.csch` and extraction

`CschTuning` carries `Correlations`, `Statistics` and `Corners` beside the tuning parts; the entry's and
goal's new fields are omitted at their defaults. A schematic with no statistical content writes the bytes it
always did. **No `format_version` bump**: the file's own rule (`project-file-formats.md`) is that an
optional, null-by-default field is additive.

**Kit corners (D10).** A `.csch` corner may select kit corner sections, keyed as the design's own
`CornerSelections` are. `NetExtractor.Extract` takes a `cornerBinder` — the GUI passes
`WorkspaceCorners.BindingsFor` over the workspace's axes, the function Simulate already binds the design's
selections with — and for each corner binds the corner's selections **overlaid on the design's own**. Every
bound constant goes into the corner's `Values` (a value the corner states itself wins), and the selections
are dropped, so the `.cnl` corner line binds values and never names a kit file. A selection that does not
resolve is reported as `Corner '<name>': ` followed by that function's own sentence — only what the corner's
selections add, since the design's own are reported where they are applied. With no binder (a schematic belonging
to no workspace, or one whose workspace offers no kit corner axes) a corner's kit selections are reported as not
applied. The CLI binds them too since YA-6 (§10.1).

## 4. YA-1 — `check` (`StatisticsValidator`)

`TuningValidator.Validate` calls `StatisticsValidator.Validate`, so `check`, the Tuning and Optimizer windows
and every later run read one rule set (a setup `check` passes is not refused later for a reason `check` could
have seen).

| Code | Severity | When |
|---|---|---|
| `yield.dist.not-tunable` | error | a distribution on a key the catalog does not offer |
| `yield.spread.missing` / `.extra` | error | the spread keys are not exactly the distribution's (`gauss` with `lo`; `tol` without `sigmas`) |
| `yield.spread.not-a-number` | error | a spread value that resolves to no number |
| `yield.spread.not-positive` | error | `sd`, `tol`, `sigmas`, `trunc` or `by` ≤ 0 |
| `yield.spread.inverted` | error | `lo` ≥ `hi` |
| `yield.spread.step` | error | `by` does not step from `lo` to `hi` a whole number of times (to 1e-6) |
| `yield.dist.lognorm-nonpositive` | error | `lognorm` on a value ≤ 0 |
| `yield.dist.integer` | error | `gauss`/`lognorm` on a whole-number value |
| `yield.complex.mixed` / `.too-many` | error | tolerances on a mixed pair of a complex value's parts, or on more than two |
| `yield.correlate.not-stat` / `.same-key` / `.rho` | error | a non-statistical key; a key with itself; ρ outside (−1, 1) |
| `yield.statistics.lhs-autostop` | error | `sampling=lhs` with `autostop=1` |
| `yield.statistics.autostop-target` | error | `autostop=1` with no `target` |
| `yield.corner.unknown-key` | error | a binding that is neither a global variable nor a key the catalog offers |
| `yield.corner.temp` | error | `temp` not a number |
| `yield.corner.trial-incomplete` | error | `trial=` without `seed`, `sampling` and `trials` |
| `yield.dist.nonphysical` | warning | §1.1 |
| `yield.dist.off` | warning | a distribution with `stat=0` |
| `yield.goal.nothing-varies` | warning | an enabled `use=yield` goal, no statistical entry and no distribution call (§7) |
| `yield.correlate.repair` | warning | the correlations are not positive definite; reports the largest change |

**The correlation matrix** (`StatisticsValidator.CorrelationOf`) is over the keys the `correlate` lines name,
in setup order; a non-positive-definite one is repaired by `NearestCorrelation.Repair`
(`src/Engine/Statistics`, Higham's alternating projections with Dykstra's correction, eigenvalues floored at
1e-8 so the result factors). YA-2 samples with the same matrix.

**The optimizer and `use=`.** `OptimizationRun` aims only for enabled goals with `ForOptimizer` — a
`use=yield` goal is a yield spec only (D4). The goal editor carries `Use` through an edit.

## 5. YA-1 — `explain` and `reference`

- `explain --tunables` adds a line per toleranced entry (`gauss σ = 1 Ohm (2 %)`, `unif 1.9 pF .. 2.1 pF
  (±5 %)`, `, truncated at ±3σ`, ` — off (stat=0)`) and, in JSON, a `stat` object: the spread in base SI
  (`sigma`, `lo`, `hi`, `step`, `trunc`, with the base `unit`) and `written`, the keys as written.
- `explain --analysis` adds a `statistics` object whenever the setup has statistical content: the effective
  settings, the statistical entries, every goal with its `use`, the corners, the correlation matrix a run
  would use (and whether it was repaired), and **what the trial count can resolve**: the expected
  half-width of the Clopper–Pearson yield interval at a yield of 90 % and the fewest trials that bring it
  under ±2 % (500 trials: ±2.73 %; 912 trials reach ±2 %). `ClopperPearson` (`src/Engine/Statistics`)
  takes the Beta quantiles through a continued-fraction incomplete beta inverted by bisection;
  `StatisticsExplainTests` checks it against the interval found from binomial tail sums directly.
- `reference statistics` is generated from the schema: the tune line's statistical keys, the distribution
  table (`AnalysisDirectiveSchema.Distributions`), the percent-vs-absolute rule, truncation, the three
  directives with every key and default, statistical and kit corners, goal `use=`, pass/fail (D4) and how
  yield and its interval are reported (D7, D8), with a worked example that `check`s clean. `reference goals`
  lists `use=`.

**On the interval with `lhs` and `sobol`.** The Clopper–Pearson interval assumes independent trials. Latin
hypercube and low-discrepancy samples are not independent and typically estimate a yield more tightly than
random sampling does, so the binomial interval is conservative for them. That is said here, once, and not
on screen (D8).

## 6. YA-2 — sampling and batch evaluation

A trial is produced in three steps, each a pure function, and evaluated through the optimizer's own
evaluator.

**1. Streams and the plan** (`src/Engine/Statistics`, pure numerics). Each statistical entry is one
*stream*, identified by `StatStreams.Id` — FNV-1a over the key text's UTF-8, then `SplitMix64.Mix` (the
optimizer's generator's output mix, made public for this). A draw is `Hash(seed, trial, stream, k)`: each
input folded in through its own mix, the top 53 bits offset by half a step so the uniform lies in the open
interval (0, 1) and Φ⁻¹ of it is finite. No generator state exists, so trial N drawn alone equals trial N
inside a parallel batch, and a new entry moves no other entry's `random` draws (D5). `SamplingPlan` turns
trial t into one **independent** standard normal per stream:

| `sampling=` | Trial t (1-based), stream s |
|---|---|
| `random` | Φ⁻¹(U(seed, t, s)) |
| `lhs` | Φ⁻¹((π_s(t−1) + v)/N): π_s a Fisher–Yates permutation of 0…N−1 whose draws sit at trial −N (no real trial), v = U(seed, t, s). One draw per stratum per stream. |
| `sobol` | Φ⁻¹ of point t−1 of a scrambled Sobol sequence, Gray-code order, computed directly from the index. Streams take dimensions in ascending order of their ids, so the mapping does not depend on discovery order. |

**Sobol.** Direction numbers are S. Joe and F. Y. Kuo's `new-joe-kuo-6.21201`, its first 1111 dimensions
(the range over which the set satisfies Property A), embedded unmodified as
`src/Engine/Statistics/SobolDirections.txt` with its BSD-style licence (`licenses/Joe-Kuo-Sobol.txt`,
`THIRD-PARTY-NOTICES.md` §4). Dimension 0 is van der Corput. 32-bit direction numbers: 2³² points. The
scramble is a **random linear matrix scramble** (per dimension a lower-triangular binary matrix with a unit
diagonal, each output digit mixing in the digits above it) followed by a **random digital shift**, both drawn
from the stream hash with the run seed and the dimension; both preserve the net structure. A stream past
dimension 1111 is drawn at `random` and the sampler reports `yield.sampling.sobol-dimensions`. The unscrambled
first ten points in three dimensions match those the table's authors publish for their reference generator.

**2. Correlation** (`GaussianCopula`). The plan's normals of the streams named by `correlate` lines pass through
z ← L·z, L the Cholesky factor of the matrix `StatisticsValidator.CorrelationOf` builds (§4) — restricted to the
keys that are statistical, a principal submatrix of a valid matrix being valid. Uncorrelated streams skip it.
A matrix that is not positive definite is repaired (Higham, §4) and the sampler's notes carry
`yield.correlate.repair` with the largest change. The result is a `StatisticalSample`: the trial number and
its correlated z per stream key — the stored form of a trial (D5).

**3. Values** (`SampleValues.Apply`, `src/Design/Statistics`). Each entry's spread is resolved
(`ResolvedSpread`, §1.1) against the nominal it is **given** — `SampleValues.Nominals(catalog, moved)` yields
the catalog's tunables at a moved design (a real value by its key, a complex value whole) — and its marginal
(`ResolvedSpread.Marginal`) evaluated at z:

| `dist=` | Value at z | Mean, variance |
|---|---|---|
| `gauss` | μ + σ·q(z) | μ; σ²·(1 − 2kφ(k)/(1 − 2Φ(−k))) when truncated at k |
| `lognorm` | m·exp(s·q(z)), s = σ/m: the **median** is the nominal | m·M(s); m²·(M(2s) − M(s)²), M(t) = E[e^{tY}] of the (truncated) standard normal |
| `unif` | lo + (hi − lo)·Φ(z) | (lo + hi)/2; (hi − lo)²/12 |
| `discrete` | rung ⌊Φ(z)·n⌋ of lo, lo + by, … | lo + by(n − 1)/2; by²(n² − 1)/12 |

q(z) is z, or with `trunc=k` the truncated normal sampled **exactly**: Φ⁻¹(Φ(−k) + Φ(z)·(1 − 2Φ(−k))),
computed from the lower tail by symmetry. Φ⁻¹ is Acklam's approximation plus one Halley step against
`SpecialFunctions.NormalCdf` (§5), the upper half by Φ⁻¹(p) = −Φ⁻¹(1 − p); `DistributionTests` holds it to
1e-12 against tabulated quantiles.

- **`sigmascale`** multiplies σ (`gauss`, `lognorm`) and a uniform's half-width about its centre. A
  `discrete` list is not scaled — its values are the legal ones.
- **Whole numbers** are rounded half away from zero.
- **A complex value's parts** are drawn in their own units (a phase in degrees) and composed with the
  nominal whole by `ComplexValue.Compose`, written back by `ComplexValue.Format` in the form the schematic
  wrote it (`polar(52,29) Ohm`).
- **A non-physical draw** — ≤ 0 for a value that must be positive (§1.1's rule) — refuses the **trial**
  (`yield.trial.nonphysical`); it is never clamped. `SampledValues.Draws` still holds the draw. So do a key
  that names nothing, a missing draw, a spread that does not resolve at the given nominal (a log-normal of a
  nominal ≤ 0) and a pair of parts no complex value has (`yield.trial.*`).

**Evaluation** (`OptimizationRun.EvaluateValues`). `OptimizationRun.ForEvaluation(circuit, options, goals)`
validates the setup as a run does and scores its enabled goals for `GoalUse.Yield` (the default), `Opt` or
`Both`; it needs no optimized variable and no algorithm. Its analyses are the goals', or every runnable one
under `statistics analyses=all` or with no goal (a Monte Carlo of spreads alone). `EvaluateValues` takes value
maps (key → value text) and returns per map a `PointEvaluation`: status (`Evaluated` / `DidNotEvaluate` with
its reason), every goal's `GoalScore` (worst violation, met, margin), `Pass`, the cost, and the `DataSet`
unless `keepData: false` drops it after scoring. It is **the** evaluator: the optimizer's unit-box batches are
now decoded into value maps and handed to the same private method, so the cache (keyed by
`OptimizationRun.CacheKeyOf` — the map's `key=value` lines in its own order, the form a decoded point's key
always had), the parallelism, the `NotReentrantReason` serial rule with its note, and the cancellation token
are shared. A cache hit returns its scores and no `DataSet` (the cache keeps scores, not results).
`BatchEvaluateTests` holds a map's results bit-identical to Simulate with the same values typed.

**Nothing nominal changes** (R-ya2-7): with no statistical entry nothing in this section runs during
Simulate, Tuning or Optimization; the optimizer's own classes pass unchanged.

## 7. YA-3 — kit statistics and distribution functions

**The functions** (`Evaluator.Statistical.cs`, `src/Core/Expressions`). `agauss(nom, dev[, k])`,
`gauss(nom, rel[, k])`, `aunif(nom, dev)`, `unif(nom, rel)` and `limit(nom, dev)` are circuitRF built-ins with
the SPICE dialect's meaning; `limit(x, lo, hi)` is the clamp (`expressions.md` §7 has the table). Outside a trial
each is its **first argument, evaluated alone** — the spread is never read — and the unit rules read the same
nominal view (`Evaluator.NominalView`), so a nominal result is the one the importer's old reduction gave, bit for
bit. Inside a trial the evaluator holds an `IStatisticalDraws` (`Evaluator.Statistics`, set through
`Elaborator.Statistics` — per elaboration, never global) and computes:

| Call | Value at the stream's draw (z, u = Φ(z), s = `sigmascale`) |
|---|---|
| `agauss(nom, dev, k)` | nom + (s·dev/k)·z |
| `gauss(nom, rel, k)` | nom·(1 + (s·rel/k)·z) |
| `aunif(nom, dev)` | nom + s·dev·(2u − 1) |
| `unif(nom, rel)` | nom·(1 + s·rel·(2u − 1)) |
| `limit(nom, dev)` | nom − s·dev if u < ½, else nom + s·dev |

k defaults to 1; **k = 0 is no spread** — a kit writes `(mm_ok != 1 ? 0 : 1)` as k to switch its mismatch off,
and that is the only reading that does not divide by zero; k < 0 is refused. A draw on a complex argument, a wrong
arity or a spread that does not evaluate throws, and is also recorded in `StatisticalProblems` (on the evaluator and
on `ElaboratedNetlist`) — some parameter paths fall back to verbatim text when evaluation throws, so YA-4 treats a
non-empty list as a trial that did not evaluate (D7; `yield.trial.draw-failed`).

**Process and mismatch** (D6) are the scope's. `Scope.InstancePath` is null for the testbench's global scope and the
instance's full path for a cell scope (inherited by a user function's call frame). The evaluator keeps a stack of
SITES: resolving a named binding pushes (owner's instance path, name); an instance parameter's override is evaluated
through `Evaluator.EvalParameter` with site `<instance>.<param>` (`Elaborator.EvalOverride`, every per-device
resolver). A call's kind is its site's scope — no path is **process**, a path is **mismatch** — and its stream is
`<path>.<name>`, or `<name>` alone in the global scope, with `#2`, `#3` … for later calls in one site:

| Written | Stream | Kind |
|---|---|---|
| global `Rnom = agauss(100, 10, 1)` | `Rnom` | process — memoised, so one draw per trial shared by every instance |
| cell `sub` default `R=agauss(Rnom,5,1)`, instance `X1` | `X1.R` | mismatch |
| inside `X1`, `R1 a b R='agauss(…)'` | `X1.R1.R` | mismatch |
| top-level `R1 in 0 R=agauss(…)` | `R1.R` | process (testbench scope; one instance, so one draw either way) |

`ElaboratedNetlist.StatisticalCalls` lists every call reached, once per stream, nominal or not.

**The draws** (`ExpressionDraws`, `src/Design/Statistics`). A stream's z is `StatStreams.Normal(seed, trial, Id(stream))`
at **slot k = 1** — slot 0 belongs to the setup's statistical entries, so a stream that happens to be spelled like an
entry's key never shares its draw. `process=0` / `mismatch=0` make that kind evaluate at its nominal; `sigmascale`
is passed with every draw. Streams are discovered during elaboration, after any plan exists, so they draw at
`random`; a caller that elaborates nominally first can plan them (`StatisticalCalls` names them) and pass their z in
`planned`, which win — that is how YA-4 reaches `lhs`/`sobol` for them. `Drawn` records each stream's kind and z, the
per-stream record YA-4's result keeps.

**The SPICE reader** (`SpiceDistributions`, `spice-models.md` §8.4 has the per-path table). The extraction's read
and a corner section's read keep the call LIVE, in circuitRF's spelling; the import gestures, which write files a
user keeps, reduce it to the NOMINAL; a placed part's parameter rows are seeded BLANK where the default holds a
distribution, so the file's own default — and its per-instance draw — stands. A `.if` condition and a controlled
source's equation are always nominal. `SpiceExpression.ReduceDistributions` is the nominal form of any text.

**Kit sections** (`spice-models.md` §8.11). A kit's statistical section binds process globals whose values are
distributions — they reach the run as corner bindings. Its mismatch section includes a VARIANT model library whose
subcircuits carry the per-instance draws; `PdkCorners.SectionFor` returns the section's definitions with its
bindings, `WorkspaceCorners.Bind` returns both for the design's selections, and `KitCornerVariants.Apply` (called as
the extraction loads a part's SPICE netlist) puts the variant's subcircuits in place of the part library's — only
when the section does not include the part library itself (by content), so a nominal section moves no byte. A
SPICE model placed straight on a schematic runs the file it names; its distributions are live, but no section
substitutes its definitions.

**Not tunable** (R-ya3-4): a value holding a distribution is an expression, and `TunableCatalog.WhyNotOffered`
says so in its own words ("its value '…' is a distribution, which varies in a Monte Carlo trial and is not tuned —
write its nominal as a plain value with a tolerance (dist=) to tune and vary it").

**Discovery and refusal** (`KitStatistics`, R-ya3-5). `KitStatistics.Report(calls, selected sections, axes)` gives
the process and mismatch counts, the selected sections that brought statistics (`r_stat (rCorners.lib)`), and an
Info note per kit axis whose statistical section is not selected (`yield.kit.section-not-selected`, naming the
axis and the section). Which sections are statistical is a TEXT scan (`PdkCorners.StatisticalSections`): a
distribution call outside a comment, in the section's own lines or a file it includes or requests — reading every
section would read its model library each time. `KitStatistics.NothingVaries(setup, calls)` is the run's refusal
(`yield.run.nothing-varies`): an enabled yield goal, no tolerance and no distribution call. `check`'s
`yield.goal.nothing-varies` warning counts distribution calls too (the elaborated calls when `check` has them, the
testbench's own text otherwise). `explain --analysis` adds a `distributions` object — counts and every stream. The
CLI's yield report names no sections; YA-4's run report takes them from `WorkspaceCorners.Bind`.

**Not covered.** A distribution inside a FREQUENCY-DEPENDENT expression is evaluated at stamp time by the model's
own evaluator, which has no trial context, so it stays at its nominal. The other simulator dialect's
`statistics { process {…} mismatch {…} }` blocks are not read (`spice-models.md` §8.11 names the seam).

**Nominal identity** (R-ya3-6, `KitStatisticsNominalIdentityTests`): every example extracts a netlist with no
distribution in it; a kit-shaped design runs bit-identically live and reduced, and a nominal section moves no byte;
over a real kit's corner files (a git-ignored `testdata/kit-statistics`, skipped with a reason when absent) every
section read live is the nominal read modulo the distributions' spelling, and every global and subcircuit
expression evaluates to the same bits.

## 8. YA-4 — the run service

`StatisticalRun` (`src/Design/Statistics`) is the one Monte Carlo and yield implementation: the GUI, the CLI and MCP
call it, and it evaluates through the optimizer's evaluator (D11).

| Piece | Where |
|---|---|
| The run, pause/resume/stop, re-run one trial, contributions, worst trials | `StatisticalRun.cs` |
| Options, progress, the trial record, the result | `StatisticalTypes.cs` |
| The result `DataSet` (D9) | `StatisticalDataSet.cs`; names in `results-dataset-layout.md` §"Monte Carlo and yield" |
| Contributions | `StatisticalContributions.cs` over `Regression` (`src/Engine/Statistics`) |
| Yield, interval, auto-stop | `YieldEstimate` over `ClopperPearson` (`src/Engine/Statistics`) |
| The statistics functions | `Evaluator.Reductions.cs`, `SampleStatistics` (`src/Core/Expressions`); `measurements.md` "Reductions over an axis" |

### 8.1 Creating a run

`StatisticalRun.Create(circuit, options)` builds `OptimizationRun.ForEvaluation` with `GoalUse.Yield` (mode `Yield`:
the `use=yield|both` goals) or `GoalUse.Both` (mode `MonteCarlo`: every enabled goal) — so the setup is validated by
the rule set `check` runs and a refusal is `check`'s sentence. It then refuses `yield.run.no-yield-goal` (a yield run
with no yield goal) and `yield.run.nothing-varies` (no statistical entry and no distribution call — in either mode).
`analyses=goals|all` is the evaluator's (§6). The design is elaborated once, nominally, to list its distribution
calls (`KitCalls`).

### 8.2 Trials

The nominal is evaluated first (tag `nominal`); a nominal that does not evaluate is the refusal
`yield.run.nominal-failed`. Trial t is then drawn and evaluated in three steps, each a pure function of t:
`StatisticalSampler.Sample(t)` → `SampleValues.Apply` (entries, at `sigmascale`) → `ExpressionDraws.For(settings, t,
planned)` (distribution calls). A trial whose sample is refused (`yield.trial.*`) did not evaluate and costs no
simulation.

- **Kit streams in the plan.** Under `random` a distribution call draws natively from its own stream (slot 1).
  Under `lhs`/`sobol` the nominal's calls join the plan as `kit:<stream>` — one plan across every stream, or two
  plans' Sobol dimensions would coincide — and their z is handed to `ExpressionDraws` as `planned`. A call a trial
  reaches that the nominal did not draws natively.
- **Draws reach every elaboration.** `CircuitEvaluationRequest.Statistics` puts the trial's draws on the evaluation's
  own bench copy (`TestBench.StatisticalDraws`, run-time only, never written), and `Elaborator.Elaborate` takes them
  from there when it has none of its own — so a parametric sweep's per-point elaboration and a parallel S-parameter
  run's per-worker elaboration draw the trial's values too, not the nominal. A draw that could not be made fails the
  evaluation with `yield.trial.draw-failed`.
- **The cache.** A `ValuePoint` carries a tag (`trial <n>`) appended to its cache key: two trials whose values happen
  to coincide (a `discrete` draw, or kit draws alone with an empty value map) are still two simulations, and a cached
  answer would carry no results. `EvaluateTrial(n)` tags `trial <n> (re-run)` and so always simulates afresh.
- **Batches.** `parallel=` trials at a time by default (`StatisticalOptions.BatchSize` overrides), through
  `OptimizationRun.EvaluateValues`, numbered in the order drawn and slotted by number, so the result does not depend
  on parallelism (`YieldDeterminismTests`). A circuit that is not re-entrant runs one trial at a time with the
  optimizer's note.
- **Did not evaluate (D7).** The trial's reason is kept; `nonconverged=fail` counts it in the denominator as a fail,
  `warn` leaves it out. Either way the result notes `yield.run.did-not-evaluate` with the count. No trial evaluating at
  all is outcome `NoneEvaluated` (exit 2).

### 8.3 Yield, interval, auto-stop (D8)

`YieldEstimate.Of(passes, counted, confidence)` — the Clopper–Pearson interval of §5 — overall and per goal. A trial
passes when it evaluated and every scored goal is met. With no goal (a Monte Carlo of the spread alone) there is no
yield: the estimate is NaN and the `pass` cube is not written.

**Auto-stop** (`autostop=1`, yield mode, a target) is decided after each batch **at every trial count the batch
reached, in trial order**: the run stops at the first count n ≥ 50 counted trials whose lower bound is at or above the
target (`Above`) or whose upper bound is below it (`Below`), and the trials of that batch past n are discarded. So the
count it stops at is a function of the draws alone — the same for any batch size (`AutoStopTests`: a design at 100 %
yield against 95 % stops at ⌈ln 0.025 / ln 0.95⌉ = 72). Outcome: `BelowTarget` (exit 3) when auto-stop said `Below`,
or, without an auto-stop verdict, when the yield is below the target; otherwise `Finished` (exit 0). A Monte Carlo run
has no target.

### 8.4 Save policy and writing (R-ya4-5, R-ya4-6)

The nominal's cubes give the size of one trial's analysis results (real 8 bytes, complex 16; scalar measurements and
`__` metadata excluded — the scalars are kept for every trial anyway). `save=scalars` keeps none, `all` every trial's,
`<n>` the first n, `auto` all when they fit in 256 MB and otherwise the largest leading count that does. The result
notes `yield.run.saved` with the sentence saying which. `ResultPathFor(source)` is `<design>.yield.npy` beside the
source; the run writes there when `StatisticalOptions.ResultPath` is set and it finishes or is stopped, never when
cancelled. `StatisticalOptions.Publish` receives the result so far at most once per `PublishInterval` (250 ms) after a
batch and once at the end — the in-memory Data Display source's feed (D14), which the panel (YA-10) wires.

### 8.5 Re-running one trial (R-ya4-7)

`EvaluateTrial(n)` draws trial n exactly as the run does and evaluates it with its results kept, whatever the save
policy — equal, value for value, to trial n inside a run (`YieldDeterminismTests`). Under `lhs` a trial past the plan's
count is `yield.trial.out-of-range`.

### 8.6 Contributions and worst trials (R-ya4-9, R-ya4-10)

`Contributions(name)` regresses a goal's value at its tightest point (or a scalar measurement) over the evaluated trials
on every stream's z — entries' correlated z, and the kit streams' (0 where a trial did not draw one) — standardized.
Ordinary least squares with at least 5 trials per stream; otherwise ridge with a penalty of 0.01 per trial on the
standardized normal equations, flagged `Underdetermined`. Each contributor reports its standardized coefficient, its
**share** of the explained variance by Pratt's measure β·r/R² (the shares sum to 1; with independent streams it is
β²/Σβ²), and its Spearman rank correlation with the scalar. A kit process stream is one contributor; **an instance's
mismatch streams are one contributor**, named by the instance (the stream's site less its parameter and `#n`), with
the root sum square of its coefficients, its streams' summed share and the rank correlation of its fitted linear
part. Never run unasked. `WorstTrials(goal, k = 10)` lists the evaluated trials with the smallest margin, with their
values.

### 8.7 Not covered here

The statistics functions reduce whatever cube they are given. A Data Display TRACE expression that calls one is
evaluated once over the cubes it names rather than point by point (`TraceExpression`, §9.3), so `plot` and the trace
card take them; the drawing styles a histogram wants are YA-8's. The measurement
scope reads the trial's drawn globals (`ResolvedGlobals` come from the trial's elaboration); a stamp-time,
frequency-dependent expression stays nominal (§7).

## 9. The `yield` verb and MCP (brief-yield-5)

### 9.1 One verb, three nouns

`circuitrf yield mc|estimate|trial <path>` (`docs/design/cli.md` §25) runs `StatisticalRun` and nothing else:
`mc` in `StatisticalMode.MonteCarlo`, `estimate` in `StatisticalMode.Yield`, and `trial` (or `--trial n` on either)
`EvaluateTrial(n)`. Its flags override the `statistics` line for one run, and the setup reaches the run only when a
flag changed it, so a flag-less run's `.yield.npy` is byte for byte the in-process run's. Over MCP it is `run
analysis=montecarlo|yield`, with a progress notification per batch.

### 9.2 The two writes

`--save-preset <name> --trial n` locks a trial's values in as a preset (`TuningPresets.LockIn`); `--save-corner <name>
--trial n` adds `corner <name> trial=n seed=s sampling=m trials=N` — the run's EFFECTIVE seed, sampling and trial
count, flags included, since those three identify the trial (§3). A `.csch` only, after a history checkpoint (D12).

### 9.3 Statistics functions in a trace

`TraceExpression` binds each reference of an expression that calls one of `Evaluator.AxisFunctions` as the cube its
slice leaves and evaluates once; the result's single remaining axis is the X. `check` asks `StatisticalRun.Create`
for the refusal a run would give, and `explain --analysis` reports the chains a yield run evaluates under each
`analyses=` scope and its cost in nominal evaluations.

## 10. Corners (brief-yield-6)

| Piece | Where |
|---|---|
| One evaluation per corner, a Monte Carlo at each | `CornerRun.cs` |
| The corner `DataSet` and the stacker | `CornerDataSet.cs`; names in `results-dataset-layout.md` §"Corners" |
| Statistical corners: proposing, replaying | `StatisticalCorner.cs`, `StatisticalRun.Replay`, `RecordedTrial` |
| The cross product | `CornerGenerator.cs` |
| Kit corner binding | `WorkspaceCorners` — moved to `src/Design/Workspace` |

### 10.1 A corner is a value map

A `.cnl` corner line binds values and a temperature (D10): a `.csch` corner's kit axis selections were resolved at
extraction (§3) into the values the line binds, the schematic's other axes as the schematic has them, since the
binder overlays the corner's selections on the design's. So `CornerRun` evaluates a corner as the value map
`CornerRun.BindingsOf` gives — its values, then `temp` as the ambient global — through `TunableOverrides.Apply`, the
tuned-value door: a VAR value replaces the variable, a tunable key the instance's assignment. **`temp` on a design that
never wrote it adds the global**, as typing it would (`Elaborator` reads the ambient by name either way); every other
key naming nothing is skipped with the note it always had. A corner naming a variable `--set` also sets is the refusal
`yield.corner.set-conflict`, checked when the run is created rather than per point.

**The headless extraction binds kit corners now.** `WorkspaceCorners` (axes, `Bind`, `BindingsFor`) was in `src/Ui`
only because nothing below the firewall had needed it; it moved to `src/Design/Workspace` unchanged except that it
resolves a kit path itself (rooted as stored, otherwise under the workspace root — `WorkspaceRefs.Resolve`'s rule,
which stays in `src/Ui`). `WorkspaceCorners.ForDocument(path)` reads the axes of the workspace a document belongs to,
and `SchematicCircuit.ExtractInWorkspace` binds the design's selections and passes the corner binder exactly as
`WorkspaceViewModel.WriteNetlist` does. Every path-based extraction — the run verbs' `.csch` input, `netlist`, `check`
— goes through it, so a schematic in a kit workspace runs headlessly at the corners Simulate runs it at. A workspace
offering no axes extracts exactly as before, byte for byte.

### 10.2 One run, every corner (R-ya6-1, R-ya6-2)

The nominal (tagged `nominal`) and every enabled corner (or `--corners` names; an unknown name is
`yield.corner.unknown`, none at all `yield.corner.none`) are ONE batch of `OptimizationRun.EvaluateValues` — in
parallel, each tagged `corner <name>` so two corners whose maps coincide are still two simulations. They are scored
against the **yield specs** (`GoalUse.Yield`): a corner analysis verifies the specs, and an opt-only goal is a design
target the yield goals are deliberately looser than (D4). A corner that does not evaluate is a fail under
`nonconverged=fail` (D7) and is reported with its reason either way.

Outcome: `Finished` (exit 0) when every goal is met at every corner, `BelowTarget` (exit 3) when one is not,
`NoneEvaluated` (exit 2) when nothing evaluated, the nominal included. The result carries each corner's evaluation and,
per goal, its **worst corner** — the smallest margin. The `DataSet` (§"Corners" of the layout note) has an outer
`corner` axis labelled with the names, the nominal first, and is written to `<design>.corners.npy`
(`CornerRun.ResultPathFor`) — never `run.npy` or the Monte Carlo file.

### 10.3 A Monte Carlo at each corner (R-ya6-3)

`CornerOptions.MonteCarlo` runs a `StatisticalRun` per corner, the nominal first, each with the corner's bindings as
`StatisticalOptions.Bindings`: applied to the nominal and every trial, and the nominal each spread is drawn around
(`SampleValues.Nominals` at the moved values), so a percent spread on a value the corner moves follows it. **At a
corner the kit's process draws are off** — the corner is the process answer; drawing process too would count one
question twice — while mismatch draws as configured; the nominal row is the ordinary run, process included. `check`
warns `yield.corner.process-double-counted` when the statistics line says `corners=` with `process=1` written. A
statistical corner is one trial and has no Monte Carlo of its own; it is left out with a note.

The per-corner results stack under the `corner` axis (`CornerDataSet.Stack`): every cube with an outer `trial` axis is
padded with NaN to the longest corner's trial count (auto-stop ends corners at different counts), a cube whose shape
differs any other way is left out, and the file is `<design>.yield.npy`. The outcome is `BelowTarget` when any corner's
yield is below its target; the result names the corner with the lowest yield. `statistics corners=all|<names>` (or
`--corners`) makes `yield mc|estimate` this run.

### 10.4 Statistical corners (R-ya6-4)

`corner <Name> trial=n seed=s sampling=m trials=N` replays trial n against the CURRENT nominal.
`StatisticalCorner.Replay` builds a `StatisticalRun` at the corner's seed, sampling and trial count (and the setup's
current process, mismatch and σ scale; auto-stop off) with the corner's own bindings, and asks it for trial n:

- **With the run's record** — `RecordedTrial`, from a run in memory (`RecordedTrial.Of`) or from the `.yield.npy`
  beside the design when its seed and sampling match and it holds the trial (`RecordedTrial.FromDataSet`; a rank-1
  `z:` cube per stream) — the recorded z-vector is used AS IT STANDS. An entry or distribution call the recording has
  no draw for takes its nominal (`ExpressionDraws`'s `unplannedAtNominal`), and a recorded stream the design no longer
  has is named in the warning `yield.corner.streams-gone`.
- **Without one**, the trial is drawn afresh from (seed, trial, stream) — the same draws for every stream the run had
  under `random` and `lhs`, whose streams are independent — and the note `yield.corner.not-recorded` says a renamed or
  added variable cannot be told apart. Under `sobol` a changed stream set moves the dimension mapping, which only the
  record avoids.

Because the stored form is z (D5), the same vector over a moved nominal is the same relative deviation for a percent
spread and the same absolute one for an absolute spread; correlated entries keep their correlation, the vector being
the one drawn after it. `StatisticalCorner.FromRun(result, goal, k)` proposes the k worst trials of a goal as corner
definitions named `<goal>_t<trial>`, with the run's effective seed, sampling and trial count
(`StatisticalResult.Settings`) — what "save as corner" uses.

### 10.5 The generator (R-ya6-5)

`CornerGenerator.CrossProduct(axes, temps, values)` crosses kit axes × options, temperatures and variable values into
explicit `CornerDefinition`s, named from their parts joined by `_`: a kit option as written, a temperature as its
number (`-` → `m`, `.` → `p`; `t25`, `tm40` when it starts the name), a variable or key as its letters and digits with
its value's number (`Vdd3p0`, `R2R1100`). Duplicates (the same bindings) are dropped and a name collision gets `_2`.
More than 256 corners is the refusal `yield.corner.generate-too-many` naming the count. `CornerGenerator.Parse` reads
the CLI's `axis=a,b;temp=-40,25;Vdd=3.0,3.6`: a name is a kit axis when one of the workspace's axes answers to it (its
label, file stem or key), `temp` the ambient, anything else a variable or tunable key — checked by `check` with the
rest of the corner lines. It writes lines; it is not a grammar.

### 10.6 `explain` (R-ya6-7)

`explain --analysis` lists each corner's bindings in base SI with the base unit (`temp` in °C; a bare number takes the
unit of what it binds) and, for a `.csch` in a kit workspace, each kit axis's section at that corner and whether the
corner sets it or inherits the schematic's selection (the axis's first section when the schematic chose none, as
`WorkspaceCorners.Bind` binds it).

## 11. Tune and optimize across corners (brief-yield-7)

### 11.1 A corner, evaluable at any point

`CornerPoint` (`src/Design/Statistics`) is one corner made ready to be evaluated at ANY point of the design — the
optimizer's candidates, the Tuning panel's sliders. `CornerPoint.Prepare(circuit, setup, names, nominal, recorded,
sets)` makes the list: the nominal first unless `nominal=0`, then each named ENABLED corner in the order named (null:
every enabled one, in setup order). A name that is no enabled corner (`yield.corner.unknown`, listing them), a corner
binding a `--set` name, and a statistical corner that cannot be replayed (`yield.corner.replay-refused`) are refusals.
A setup that is not the netlist's own (the Optimizer panel's, the schematic's) takes the EXTRACTED corner of the same
name, since only extraction resolves a kit axis selection into values (§10.1).

`At(values)` is the map a point evaluates at that corner. **A value corner is its bindings OVER the point's values —
the corner is the condition, so a value it binds wins over the optimizer's or the slider's.** A statistical corner is
prepared once (its `StatisticalRun`, its recorded vector) and replays its trial **around the point's values**
(R-ya7-3): `StatisticalRun.Replay(trial, recorded, at)` computes the nominals at the moved design
(`SampleValues.Nominals` over the point's values with the corner's bindings over them), so a percent spread moves with
the candidate. A draw that is not physical at a point refuses that point at that corner, not the run.

### 11.2 The optimizer across corners (R-ya7-1, R-ya7-2, R-ya7-5)

`optimize corners=none|all|<names> [nominal=0]` — default `none`, which is today's run byte for byte (the corner path
is a separate branch of `EvaluateBatch`). With corners, every candidate is evaluated at every point of the list
through the batch door: the batch × corners is ONE call to the evaluator, so it runs in parallel, and **the cache keys
by (values, corner)** — `<values key>\n#corner <name>`. Per candidate the outcomes merge:

- **The residual vector is every corner's residuals in turn**, so least squares and minimax keep their meaning;
  minimax then minimizes the worst violation over the corners — ordinary worst-case design.
- **A goal is met only when met at every corner.** Its `GoalReport` is the BINDING corner's — the largest violation,
  or when met everywhere the smallest margin — with `Corner` naming it and `PerCorner` the goal at each corner.
- **A corner that fails fails the candidate**, its reason prefixed `at corner <name>:`; the penalty for a failed point
  is computed from the largest MERGED cost, so it still ranks below every real point.
- `Evaluations` counts simulations: a point costs `EvaluationsPerPoint` = 1 + corners. Auto's global-stage budget is
  50(n + 1) POINTS, so it is multiplied by that; the user's own `maxevals` stays in simulations. Sensitivity (TO-8)
  goes through the same batch door, so it is over the same corner set. The results kept per point (and published) are
  the first corner's — the nominal's, when it is evaluated. Railing is unchanged.

`OptimizationRun.EvaluationPointsOf(setup)` is the list a run would evaluate at, without preparing it — what
`explain --analysis` prints (`at nominal, hot — 2 evaluations per point`) and the Optimizer panel's status shows
(`Iter 4 · 26 evals (2 per point)`).

### 11.3 Tuning at a corner (R-ya7-4)

The Tuning panel's toolbar has an **Evaluate at** picker — Nominal, or any enabled corner of the schematic — shown
only when there is a corner. `TuneSession.EvaluateAt(name)` resolves it through the session's prepared circuit
(`CornerPoint.For`), cancels the evaluation in flight and evaluates the sliders' values again there; each later
request is evaluated at the corner. **Push still writes the sliders' values alone.** The Data Display chip reads
`Tuning · <corner>` (`ITuneResultSink.AtCorner`), and the canvas's tuned labels end `@ <corner>`. A statistical corner
replays the run beside the schematic (`<design>.yield.npy`) when it is there.

### 11.4 Headless (R-ya7-6)

`opt --corners all|none|a,b` overrides the file; the binding corner is the goals table's `binding` column, and
`--json` carries `corner` and `perCorner` per goal plus the run's `corners` and `evaluationsPerPoint`. A statistical
corner replays the `<design>.yield.npy` beside the design when its seed and sampling match. MCP:
`run analysis=optimize corners=…`.

Gates: `tests/Ui.Tests/Statistics/CornerOptimizationTests.cs` (`CornerOptimizationTests`,
`CornerOptimizationCacheTests`, `TuneAtCornerTests`) and `OptCliVerbTests.Corners_ReportsTheBindingCorner`.

## 12. The Data Display's statistics (brief-yield-8)

**The numbers are the expression engine's; the Data Display adds drawing.** Every statistical picture is an ordinary
cube trace whose expression calls one of §8's functions, so the card, `plot`, a `measure` line and `render` compute it
with one piece of code. No new plot type: these are Rect plots (and a Table), so markers, export, Plot Versus and the
trace card keep working.

| Piece | Where |
|---|---|
| Draw style `Line|Bars|Step` (`TraceProperties.DrawStyle`, `.cdd` `Properties.DrawStyle`, not written when Line) | `src/Render/DataDisplay/Models/Misc.cs` |
| Bars, steps, normal fit, spec lines | `Renderers/StatisticsRenderer.cs`, called from `PlotRenderer` on Rect plots |
| The menu as a function — `Build`, `Apply`, `BackToCurves`, `CompanionOf` | `TraceStatistics.cs` |
| Goals → lines | `SpecLineResolve.cs`; the reverse match is `TraceGoalReader.GoalExpressionOf` + `TraceToGoal.SameQuantity` |
| The statistics table | the run's `statistics` group (`StatisticalDataSet`), drawn by `StatisticsTablePreset` |
| `normq`, histogram `"percent"`, Φ⁻¹ | `src/Core/Expressions` (`NormalDistribution`; `Engine.Statistics.SpecialFunctions` forwards to it) |

### 12.1 Bars (R-ya8-1)

A bar is one point, centred on its X, as wide as the expression's `width` companion (`TraceExpression.TryEvaluate`
now hands companions back, and the resolve stores the width on the trace) or, with none, the smallest X spacing. Fill
is the trace colour at `RenderTheme.FillOpacity`, outline the line colour. A family's members split each bin evenly
and stand side by side. **Step** is the bars' outline when there is a width and the post-step through the points when
there is not (an empirical CDF). Autoscale frames a bar trace down to zero and half a bin past each end.

### 12.2 The Statistics menu (R-ya8-2)

Over the `trial` axis when the trace has one — otherwise one submenu per axis, the chosen axis kept and every other
axis pinned (a kept one at its first sample, which the expression then shows):

| Entry | Expression | Style |
|---|---|---|
| Histogram | `histogram(<operand>, <n>)`, n by Freedman–Diaconis (`SampleStatistics.FreedmanDiaconisBins`) | Bars |
| Histogram (Percent) | `histogram(<operand>, <n>, "percent")` | Bars |
| CDF | `cdf(<operand>)` | Step |
| Normal Quantile | `normq(<operand>)` — values against Φ⁻¹ of Blom's positions; Gaussian is straight | Line |
| Yield Sensitivity vs ▸ `<key>` | `100*yield_sens(trials.pass, trials.stat:<key>, <n>)`, plus a second series `histogram(trials.stat:<key> + 0*trials.pass, <n>)` — the trials per bin, on the right axis, as a step at half opacity | Bars |
| Normal Fit (a histogram) | `mean_over`/`std_over` of the operand, scaled to the bars' area | — |
| Back to Curves | restores the trace exactly | — |

The operand is the card's own reading of the trace (`PickerBody` with its transform), so `mag(…)` stays `mag(…)`.
The FIRST rewrite records what the trace was (`TraceStatisticsOrigin`: expression, cube, slice, transform, style;
`.cdd` `StatisticsOrigin`), so Histogram then CDF still reads the original data and Back to Curves goes all the way
back. The companion's `+ 0*trials.pass` makes a trial with no pass/fail a NaN, which the histogram skips exactly as
`yield_sens` does — so the two share their bins. The added series carries an EMPTY origin, the mark Back to Curves
removes it by.

### 12.3 Spec lines (R-ya8-3)

A run records each scored goal's own line as `yield.goal:<g>:spec` (results-dataset-layout.md), so the limits come
from the result: `plot` has nothing else. A trace draws a goal when it is a histogram/CDF/normal plot of
`goal:<g>:worst`, or of a quantity a range-less goal reads (vertical lines at the values — a sloped limit at its
TIGHTER end, `in`/`out` at both edges); or when its "Add as goal…" translation reads the same quantity as the goal
(`TraceToGoal.Canonical`: whitespace, `dB20`→`dB`, a `nominal.` prefix, `~`, the KEPT axes of a slice and an
accessor's parentheses do not change the quantity) and its X is the goal's range axis — then the limit is drawn
across the range, sloped where it slopes. Dashed in `RenderTheme.LimitColor`, as segments (Skia's SVG device drops a
path effect), labelled `<goal> ≥ <limit>`. `Plot.ShowSpecLines` is null by default, which is ON: a line exists only
where a source records goals. The inspector shows the toggle only where one exists.

### 12.4 Normal fit (R-ya8-4), the table (R-ya8-5), live (R-ya8-7)

The fit is `A·φ((x − μ)/σ)/σ` with μ, σ from `mean_over`/`std_over` of the histogram's operand and A the bars' area
(Σ height × width), off by default (`.cdd` `NormalFit`). The table is the run's `statistics` group — one row per goal's
worst value and scalar measure on a labelled `quantity` axis, columns mean, σ, min, max, median, P1, P99, skew,
kurtosis, Cpk, σ-to-limit and the goal's yield with its interval, by `SampleStatistics` against
`GoalResiduals.ValueLimits` — drawn as one Table trace per column (**Add Statistics Table**, the Σ toolbar button).
While a source is published in memory (`IPlotDataSources.IsLive`), an unranged histogram holds its bin range from the
first frame with ≥ 30 values (`TraceStatistics.HoldRange`, through `pctl_over`) and is evaluated with that range
written in; once the source is a file again the range is re-derived. The table follows each frame for free, being
part of the published DataSet.

### 12.5 Headless (R-ya8-6)

`plot --trace …,stat=histogram|cdf|quantile|yieldsens[,over=<axis>][,bins=n][,percent=1][,param=<key>][,fit=normal]`
and `style=line|bars|step`; `--spec-lines`/`--no-spec-lines` (absent is the default, on). A statistic is
`TraceStatistics.Build` applied to the trace the spec built, so the `.cdd` written is the one the menu writes, origin
included. `--spec-lines` on a result recording no goal is refused (`plot.spec-lines.no-goals`).

Gates: `tests/Ui.Tests/Statistics/DisplayStatisticsTests.cs` — `BarsRenderTests`, `StatisticsMenuTests`,
`SpecLineTests`, `YieldSensitivityTests`, `HistogramPlotParityTests` (the CLI's SVG against the in-process
composer's, byte for byte, a histogram with spec lines and a fit).

## 13. The Data Display's trials (brief-yield-9)

**Which trials fail, and why.** §12 summarises the trials; this section draws them one by one — every trial's response
coloured by pass/fail with the nominal on top, an envelope where a thousand curves no longer read, scatters coloured
the same way, a contribution Pareto, and one selected trial highlighted in every plot of every display. As in §12,
nothing new is a statistic: categories are the result's own cubes, bands are §8's functions, a fit is
`Engine.Statistics.Regression` and a ranking is R-ya4-9's.

| Piece | Where |
|---|---|
| Authored views — `ColorBy`, `ShowNominal`, `Envelope`, `ShowCurves`, `ShowFitLine` (`.cdd` `ColorBy`, `Nominal`, `Envelope`, `Curves`, `FitLine`, each written only off its default) | `src/Render/DataDisplay/Models/Trace.Trials.cs` |
| Resolved — categories, counts, the nominal's points, the band, the fit, which trial each curve/point/bar is | `TrialResolve.cs`, called at the end of `TraceResolve.SetCubeDataFrom` |
| Drawing — the band, then `FamilyPlan` (passes, others, fails, nominal, selection) | `Renderers/TrialRenderer.cs`; bars dim in `StatisticsRenderer.DrawBars` |
| The menu as functions — colour-by choices, envelope refusal, Scatter vs | `TrialViews.cs` |
| A click → trials; a trial's values | `TrialPick.cs` |
| Contributions from a saved result; the Pareto | `src/Design/Statistics/ResultContributions.cs`; `ContributionParetoPreset.cs` |
| Re-run a trial; a trial as a corner | `src/Design/Statistics/TrialReplay.cs` |
| The shared selection, the trial actions, the chip | `src/Ui/DataDisplay/TrialSelection.cs`, `TrialActions.cs`, `DisplayWindowViewModel.TrialChip` |

### 13.1 Colour by and the nominal (R-ya9-1, R-ya9-2)

`ColorBy` names a per-member cube on the family axis: `pass` (the source's `trials.pass`), any address such as
`trials.goal:S21:pass`, or `corner` (a family over `corner`, each member its own wheel colour). A member reads 1 →
pass, 0 → fail, NaN → did not evaluate; and a member whose `trials.status` is not 0 did not evaluate WHATEVER its pass
reads — under `nonconverged=fail` such a trial's pass is 0, and drawing it as a fail would show a curve that was never
computed. Passes draw in the trace's colour at `TrialRenderer.PassOpacity` (0.35), fails in `RenderTheme.FailColor`
(the limit colour), did-not-evaluate members not at all. The legend (the Y-axis label) reads `S21 — 471 pass · 29
fail` (`· n not evaluated` when there are any), counted over the WHOLE axis. The draw order is `FamilyPlan`: passes,
then members with no pass/fail, then fails, then the nominal — the trace re-read from `nominal.<cube>` with the trial
axis dropped from its slice — in the trace's full colour and width (**Show Nominal**, on by default).

A trial family is capped at `Trace.MaxTrialFamilyCurves` (2,000) rather than the general 101: at 101 a failing trial
300 would never be drawn while the picture claimed to be every trial.

### 13.2 Envelopes (R-ya9-3)

`Envelope` is `minmax`, `p:<p>` (the Pp–P(100−p) band) or `sigma:<k>`, spelled the same in the `.cdd` and on
`plot --trace`. Per X point, over the family axis (`TrialResolve.EnvelopeExpressions`):

| Envelope | Lower | Upper | Line |
|---|---|---|---|
| `minmax` | `pctl_over(op, 0, "trial")` | `pctl_over(op, 100, "trial")` | `median_over` |
| `p:<p>` | `pctl_over(op, p, …)` | `pctl_over(op, 100 − p, …)` | `median_over` |
| `sigma:<k>` | `mean_over − k·std_over` | `mean_over + k·std_over` | `mean_over` |

`op` is the family read with both its family axis and its X kept (`TrialResolve.FamilyOperand`, the card's own
`PickerBody` and transform). **Min–max is percentiles 0 and 100, not `min_over`/`max_over`:** those two propagate a
NaN by design (a bad grid point in a band must not be hidden), and a trial that did not evaluate is a NaN on every X —
one such trial would blank the whole band. The band fills behind the members at the theme's fill opacity; **Curves:
off** draws the band and its line alone, which is one evaluation of three reductions however many trials there are.
On a Smith or Polar plot the envelope is refused — a pointwise band of complex values is not a region — with
`TrialResolve.ComplexPlaneRefusal` as the greyed menu item's tooltip and on `Trace.EnvelopeRefusal`.

### 13.3 Scatter (R-ya9-4)

A trace whose X is the `trial` axis offers **Scatter vs ▸** each `trials.stat:<key>`, each goal's
`trials.goal:<g>:worst` and each scalar measure. `TrialViews.ApplyScatter` writes an ordinary Plot Versus spec
(`XSpec`), turns the line off and the markers on, and colours by `pass` where the source scores goals. Each point is a
trial through the sample index the versus replaced (`Trace.SampleAxis`), so colour-by and selection apply point by
point (fails drawn after passes). **Fit Line** draws the least-squares line, `Regression.Fit` on the drawn points with
its standardized slope taken back to the plot's units, and puts `R² = …` in the legend.

### 13.4 Contributions (R-ya9-5)

Never computed unasked. **Statistics ▸ Contributions ▸ `<goal or measure>`** ranks it once through
`ResultContributions.Of` — which rebuilds the trials from the result's `z:` cubes, `goal:<g>:worst`, scalar measures
and `status`, and hands them to `StatisticalContributions.Of`, the run's own ranking — stores it as
`yield.contrib:<name>` (shares, largest first) and `yield.contrib:<name>:cumulative` over a labelled `contributor`
axis, writes the source back so a saved display redraws without recomputing, and adds a plot: the shares as bars in
percent, the running total as a line on the right axis. A Rect plot whose bars all stand on a labelled 1…N axis labels
its X ticks with the names (`Plot.XCategoryLabels`).

### 13.5 Linked trial selection (R-ya9-6)

A click on a family member, a scatter point or a histogram bar (every trial in its bin — the value on a shared edge
belongs to the bar on its right, as the histogram counts it) selects trials in that SOURCE (`TrialPick.At`, within 6 px).
The selection is `TrialSelection.Shared`, keyed by the source's full path, above every display — each display has a
library of its own, and a selection is a source's. Plots copy it onto their traces (`Trace.SelectedTrials`, never
persisted); selected elements draw last and highlighted, the rest at `TrialRenderer.DimOpacity`. The toolbar chip reads
`Trial 417` or `23 trials`; Esc clears. The click is not consumed — the plot is still selected and dragged as before.

With exactly one trial selected, the plot's context menu offers **Trial n ▸**: **Send Trial to Tuning** (the trial's
drawn values, `TrialPick.ValuesOf`, through `TuningPanelViewModel.LoadValues` on the schematic the result came from),
**Re-run Trial** (`TrialReplay.Run` — the design beside the result, `StatisticalRun.EvaluateTrial` — shown as the
snapshot ghost of every bound trace in every display, the result stacked on a one-long `trial` axis so the traces'
slices resolve unchanged), **Copy Values** and **Save as Corner…** (`TrialReplay.CornerOf`: the result's seed,
sampling and trial count, as `yield --save-corner` writes, added as one undo step). The Yield panel's trial table
(YA-10) calls the same `TrialActions`.

### 13.6 Headless (R-ya9-7)

`plot --trace …,colorby=pass|corner|<cube>,envelope=minmax|p:1|sigma:3,curves=0|1,nominal=0|1[,fitline=1]` set the
fields the window writes; selection is interactive only. Gates: `tests/Ui.Tests/Statistics/DisplayTrialsTests.cs` —
`FamilyColourByTests`, `EnvelopeTests`, `ScatterTests`, `TrialSelectionTests`, `TrialPlotParityTests` (the CLI's SVG
against the in-process composer's for a pass/fail family with a P1–P99 envelope), `ContributionParetoTests`. Both
parity tests mask Skia's `clipPath` ids, which come from a counter in the process (see `RailZMapTests.WithoutSkiaIds`).

## 14. The Yield panel (brief-yield-10)

`src/Ui/Yield/` — `YieldPanelViewModel` (lists and edits; `.Run.cs` the run, the readouts, the trial table and the
trial actions; `.Corners.cs` Corners mode), tabbed behind the Optimizer (`DockPanelIds.Yield = "Yield"`, a file
format; `DockLayoutDefaults.TabbedBehind` gives a layout saved before it the panel beside the Optimizer). It follows
the focused schematic on Tuning's rule.

### 14.1 Edits

Every edit is a pure function in `TuningSetupEdits` (`WithStat`, `WithoutTolerance`, `WithDistribution`,
`WithGoalUse`, `WithStatistics`, `WithCorrelations`, `WithCorners`) wrapped in one `SetTuningSetupCommand`. Before it
lands, `StatisticsValidator.EditRefusal(before, after, catalog)` returns the first ERROR the edit adds — the setup-only
half of `check` (`ValidateSetup`: entries, complex parts, correlations, settings) — so the refusal is `check`'s own
sentence and a problem the setup already had is not this edit's to refuse. A tolerance on a key with no entry creates
one with `tune`/`opt` off and NO range (the first tune or opt gives it the D4 default range); an entry carrying a
tolerance is never dropped as "default-shaped" when its tune and opt flags clear. The compact spread editor is
`ToleranceText` (`src/Design/Statistics`): `± 2 % at 3σ`, `σ 1 Ω`, `45 … 55 Ω`, `4 … 8 by 2`, `, trunc 3σ` to and from
the `tune` line's keys — it moves text only; validity is the validator's. The Inspector's toggle and the canvas's
right-click ▸ Tolerance… go through `IToleranceSurface`, keyed by the Tuning surface's keys.

### 14.2 The run (R-ya10-10)

`Run` creates `StatisticalRun` exactly as `yield` does — the bench prepared as Simulate netlists it, `Setup = null`
so the extracted tuning block is what runs, `ResultPath = StatisticalRun.ResultPathFor(<schematic>)` — on a background
thread; progress and frames are posted to the UI thread. The trial table is read from the frames
(`YieldTrialRowViewModel.From`: `trials.pass`, `trials.status` + `reasons`, each `goal:<g>:margin`), so it is the same
function of the same cubes during and after the run. Corners mode (and a `statistics corners=` line) drives
`CornerRun`; a corner sweep has no pause, and Stop cancels it. Gates: `tests/Ui.Tests/Statistics/YieldPanelTests.cs` —
the panel's file is the verb's byte for byte; paused and resumed it is an uninterrupted run's; stopped it keeps
exactly the trials an uninterrupted run of that many draws produces.

### 14.3 The yield display (R-ya10-8)

`YieldDisplayPreset` (`src/Render/DataDisplay`) composes, never draws: per goal a pass/fail family over the `trial`
axis, a histogram of `trials.goal:<g>:worst` (`TraceStatistics.Build`), a yield sensitivity over the first goal's top
`entry` contributor, and `StatisticsTablePreset`. The family's cube is FOUND, not guessed: every slice of every
trial-stacked cube in the goal's analysis group is tried (the goal's range axis kept as X, ports and labels pinned
in turn, each transform a goal can name) until `TraceGoalReader.GoalExpressionOf` reads the goal's quantity — the same
reverse translation the spec lines use, so the family chosen is a curve the goal's spec lines land on. A goal with
no swept axis (one number per trial) is drawn as its worst value against the trial number. The workspace writes the
document as `<design>.yield.cdd` beside the result and opens it; a display that exists is focused instead.

## Later phases

Each phase appends its section above this one as it lands: YA-11/12 centering.
