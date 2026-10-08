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
selections add, since the design's own are reported where they are applied. With no binder (the CLI, which
has no workspace corner axes at hand today) a corner's kit selections are reported as not applied.

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
CLI has no workspace corner axes, so it names no sections; YA-4's run report takes them from `WorkspaceCorners.Bind`.

**Not covered.** A distribution inside a FREQUENCY-DEPENDENT expression is evaluated at stamp time by the model's
own evaluator, which has no trial context, so it stays at its nominal. The other simulator dialect's
`statistics { process {…} mismatch {…} }` blocks are not read (`spice-models.md` §8.11 names the seam).

**Nominal identity** (R-ya3-6, `KitStatisticsNominalIdentityTests`): every example extracts a netlist with no
distribution in it; a kit-shaped design runs bit-identically live and reduced, and a nominal section moves no byte;
over a real kit's corner files (a git-ignored `testdata/kit-statistics`, skipped with a reason when absent) every
section read live is the nominal read modulo the distributions' spelling, and every global and subcircuit
expression evaluates to the same bits.

## Later phases

Each phase appends its section above this one as it lands: YA-4 the run service and
result, YA-5 the CLI and MCP, YA-6/7 corners, YA-8/9 the display, YA-10 the panel, YA-11/12 centering.
