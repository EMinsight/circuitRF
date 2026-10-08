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
| `yield.goal.nothing-varies` | warning | an enabled `use=yield` goal and no statistical entry (YA-3 adds: and no kit statistical section selected) |
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

## Later phases

Each phase appends its section below as it lands: YA-2 sampling, YA-3 kit statistics, YA-4 the run
service and result, YA-5 the CLI and MCP, YA-6/7 corners, YA-8/9 the display, YA-10 the panel, YA-11/12
centering.
