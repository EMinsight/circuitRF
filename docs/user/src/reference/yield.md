---
title: Yield
slug: reference/yield.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Yield
lede: Give values a tolerance, mark which goals are specs, and find out how many builds of the design meet them — and which ones do not.
keywords: yield, design centering, centering, surrogate, monte carlo, tolerance, tolerances, distribution, gaussian, uniform, lognormal, discrete, truncation, spread, correlation, specs, trials, seed, sampling, latin hypercube, sobol, auto-stop, confidence, interval, corners, statistical corner, contributions, sensitivity, histogram, kit statistics, process, mismatch
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What the Yield panel does</a></li>
<li><a href="#modes">The four modes</a></li>
<li><a href="#tolerances">Tolerances</a></li>
<li><a href="#spread">Writing a spread</a></li>
<li><a href="#correlations">Correlations</a></li>
<li><a href="#kit">Kit statistics</a></li>
<li><a href="#specs">Specs</a></li>
<li><a href="#settings">Run settings</a></li>
<li><a href="#running">Running, pausing and reading the yield</a></li>
<li><a href="#trials">The trial table</a></li>
<li><a href="#display">The yield display</a></li>
<li><a href="#corners">Corners</a></li>
<li><a href="#centering">Centering</a></li>
<li><a href="#headless">From the command line, and from an agent</a></li>
</ol>
</nav>

## What the Yield panel does {#what}

The **Yield** panel (View ▸ Panels ▸ Yield, tabbed behind the [Optimizer](optimization.html)) runs the
focused schematic many times — once per **trial** — with each toleranced value drawn afresh from its
distribution, and counts how many trials meet the specs. It follows the focused schematic the same way
[Tuning](tuning.html) and the Optimizer do.

Everything you set here — tolerances, correlations, which goals are specs, the run settings, the
corners — is saved with the schematic, marks it changed and can be undone, one step per change. A run
and its trial table are not: they belong to the session. A run never changes the schematic's values.

## The four modes {#modes}

The four buttons at the top choose what **▶** does:

| Mode | What a run reports |
|---|---|
| **Monte Carlo** | The spread of every measurement and goal value over the trials. No pass or fail. |
| **Yield** | The fraction of trials meeting every spec, with its confidence interval, overall and per spec. |
| **Corners** | Every enabled corner evaluated once — or, with **MC at each corner**, a Monte Carlo at each. |
| **Centering** | The designable values moved to where the most trials meet the specs, then checked on fresh trials. |

## Tolerances {#tolerances}

A tolerance lives on **the same entry** as a value's tuning range and its optimizer flag: one row per
value, in all three panels. A tolerance can be added three ways, and all three do the same thing:

- **＋** in the panel's Variables header, through the same search Tuning uses;
- the **bell-curve toggle** beside a value in the Properties Inspector;
- right-click a value on the canvas ▸ **Tolerance…**.

A new tolerance is a Gaussian at ± 5 % for 3σ (a uniform ± 5 % on a whole-number value), with tuning
and optimizing left off. Each row shows:

| Column | |
|---|---|
| **Check** | Draw this value in every trial. Unticked, the tolerance is kept but the value stays at its nominal. |
| **Distribution** | Gaussian, Uniform, Lognormal or Discrete. A whole-number value takes Uniform or Discrete only. |
| **Spread** | How wide — see [Writing a spread](#spread). |
| **Nominal** | The schematic's value, in its unit. |
| **Share** | After **Contributions**: this value's share of the spread of the selected spec. |

**⋮ ▸ Remove tolerance** deletes the distribution and any correlation naming the value.

An edit the design cannot run is refused on the spot, with the same sentence
`circuitrf check` prints for it — a lognormal on a value that is not positive, a Gaussian on a whole
number, tolerances on both a rectangular and a polar part of one complex value. A complex value takes
tolerances on its real and imaginary parts, or on its magnitude and phase.

## Writing a spread {#spread}

Type into the Spread box and press Enter:

| You type | Means |
|---|---|
| `± 2 %` | Within 2 % of the nominal. On a Gaussian, at 3σ unless you say otherwise. |
| `± 2 % at 2σ` | A Gaussian whose ± 2 % is two standard deviations. |
| `σ 1 Ω` | A Gaussian or lognormal with a standard deviation of 1 Ω. |
| `45 … 55 Ω` | Uniform between 45 Ω and 55 Ω (`...` works too). |
| `4 … 8 by 2` | Discrete: 4, 6 or 8, equally likely. |
| `σ 2 %, trunc 3σ` | A Gaussian cut off at ± 3σ — drawn from the truncated distribution, never clipped. |

A percentage follows the nominal when the nominal moves; an absolute value does not.

## Correlations {#correlations}

**Correlations…** (the link button) opens a grid of every toleranced value. Type a correlation
coefficient ρ, between −1 and 1, above the diagonal; the cell below mirrors it. A set of correlations
that cannot all hold at once is shown with the nearest set that can — which is what a run uses.

## Kit statistics {#kit}

When the design's kits carry statistical model sections, a **Kit statistics** row appears: **Process**
(one draw per trial, shared by every device) and **Mismatch** (a draw per device per trial) can be
switched off independently. When a kit offers a statistical section that is not selected, **Choose a
corner…** opens the corner picker in the Analyses panel.

## Specs {#specs}

A spec is a [goal](optimization.html#goals). Every goal is listed, with a **Use** choice:

| Use | |
|---|---|
| **Opt** | The optimizer meets it; the yield ignores it. |
| **Yield** | The yield counts it; the optimizer ignores it. |
| **Both** | Both — the default. |

A common way to work: centre the design with the optimizer against tight goals, then loosen copies of
them, marked **Yield**, for the yield analysis. Goals are written in the Optimizer; the pencil
(**Edit goals…**) takes you there with the selected goal. After a run each spec shows its own yield
and interval.

## Run settings {#settings}

Collapsed to one line (`500 trials · seed 1 · random · target 95 %`); the chevron opens them.

| Setting | |
|---|---|
| **Trials** | How many. With auto-stop, the most it will run. Default 100. |
| **Seed** | The same seed draws the same trials, on any machine. The dice picks a new one. |
| **Sampling** | `random`; `lhs` (Latin hypercube — needs the trial count up front, so not with auto-stop); `sobol` (low-discrepancy). |
| **Target %** | The yield the design should reach. |
| **Confidence %** | Of the interval around the yield. Default 95. |
| **Auto-stop** | Stop once the interval lies wholly above or below the target — after 50 trials at least. |
| **Did not evaluate** | A trial that did not converge: **Fail** counts it as a fail; **Warn** leaves it out of the yield. Either way it is listed. |
| **Save** | Which trials keep their full sweeps: `auto`, `scalars`, `all`, or the first N. |
| **Parallel** | How many trials run at once. |

## Running, pausing and reading the yield {#running}

**▶** starts the run; **⏸** finishes the trials in flight and holds, and pressing it again carries on
exactly as if it had never paused; **■** stops and keeps every trial already finished. The header
shows trials done, elapsed time and an estimate of the time left. In Yield mode the readout shows the
yield, its interval as a bar with the target marked on it, and how many trials did not evaluate. Every
open Data Display holding the result follows the run live, with a **Yield** chip.

The result is written beside the schematic as `<name>.yield.npy` — the same file
`circuitrf yield` writes for the same schematic and seed, byte for byte.

## The trial table {#trials}

During and after a run, one row per trial: its number, pass or fail, the spec it is tightest on and
its margin there, or why it did not evaluate. Sort by trial or by margin (tightest first); the funnel
shows fails only. Selecting a row highlights that trial in every plot of the result, and picking a
trial on a plot selects its row. Right-click a row for:

- **Send trial to Tuning** — the trial's values onto the Tuning sliders, to see why it failed;
- **Re-run trial** — simulate it again in full and show it on every plot of the result;
- **Copy values**;
- **Save as corner…** — a statistical corner that replays this trial's draws around the current design.

**Contributions** (the bar-chart button) ranks, once, which toleranced values drive the selected spec,
and fills each variable's **Share** bar.

## The yield display {#display}

**Open yield display** (the chart button, also offered once at the end of the first run) creates a
Data Display beside the result, `<name>.yield.cdd`, or brings it forward if it exists. It holds, for
each spec, every trial's curve coloured pass and fail with the nominal and the spec's limits; a
histogram of each spec's worst value with its limits; a yield sensitivity over the value that drives
the first spec most; and the statistics table. Every plot is an ordinary one: change it as you would
any other — see [the Data Display](data-display.html).

## Corners {#corners}

In Corners mode the panel lists the schematic's corners — name, enabled, the kit sections, the
temperature and the values each binds; a statistical corner shows the trial it replays.
**Generate…** builds corners from every combination of the kit sections you tick, the temperatures
and the values you type, and shows how many before it writes them. **▶** evaluates every enabled corner
and shows a grid of corner × spec: the margin in each cell, failing cells red, each spec's worst corner
in bold. With **MC at each corner**, the grid shows each spec's yield at each corner instead.

## Centering {#centering}

Centering moves the values you let the [Optimizer](optimization.html) move — those with **Opt** ticked,
within their ranges — to wherever the most trials meet the specs, while their tolerances move with them.
The list shows each such value with its range and its tolerance beside it, and the toleranced values
that stay put.

Every position the search tries is judged on the same set of trials, so two positions differ by the
design and not by luck. The chart beside the status line is the best yield found at each iteration. At
the end, the starting values and the centred ones are each run on the same set of fresh trials, and the
panel shows both: **start yield [interval] → centred yield [interval]**. That pair — never the search's
own figure — is the result. If the two intervals overlap, the gain is not resolved at that many trials;
raise **Verify** to settle it.

The chevron opens the centering settings: the search method, the trials each position is judged on,
the trials of the final check, the iteration and simulation limits, and **Surrogate**. With
**quadratic**, each position costs a dozen simulations or so instead of one per trial: circuitRF fits
each spec to a smooth curve through a few runs and counts the yield on the curve. It is much faster
with a handful of toleranced values and specs that change gently, and it never decides the answer —
the final check is still simulated. When the curve fits a spec poorly for three iterations running,
the search goes back to simulating every trial and says so.

When it finishes, **Lock in** keeps the centred values as a preset, **Push** writes them into the
schematic (one undo step), and **Send to Tuning** loads them into the Tuning sliders.

## From the command line, and from an agent {#headless}

Everything the panel runs, `circuitrf yield` runs too — `yield mc`, `yield estimate`,
`yield corners`, `yield center` (with `--surrogate quadratic`) and `yield trial` — and an agent runs
the same through MCP. See
[the command line](cli.html) and [AI agents](ai-agents.html#yield).
