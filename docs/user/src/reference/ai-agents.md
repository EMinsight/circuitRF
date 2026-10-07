---
title: Designing with an AI agent
slug: reference/ai-agents.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Designing with an AI agent
lede: Connect an AI agent to circuitRF's MCP server, tell it what to build, and know where it will need your help. What to set up, what to say, which reference topics it should read, and the design flow that works.
keywords: AI agent, assistant, MCP, Model Context Protocol, serve, print-config, Claude Code, automation, headless design, reference topics, design flow, prompt
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What an agent can do in circuitRF</a></li>
<li><a href="#setup">Connecting the agent</a></li>
<li><a href="#brief">What to tell it</a></li>
<li><a href="#topics">The reference topics it should read</a></li>
<li><a href="#flow">The design flow that works</a></li>
<li><a href="#em">EM runs: check the cost first</a></li>
<li><a href="#traps">Where agents go wrong</a></li>
<li><a href="#safety">Keeping its work reversible</a></li>
</ol>
</nav>

## What an agent can do in circuitRF {#what}

circuitRF runs as an **MCP server**. An agent connected to it can create workspaces and cells,
write designs, check them, simulate them, render them and read the results back. It can also
export artwork and import parts. All of this is available without the window, and each tool returns
the same JSON document as the matching [command-line verb](cli.html).

**The agent works by writing files.** There are no tools for placing one component or setting
one parameter. The agent writes a netlist, a layout or an EM setup in full, using its own file
tools, and then asks circuitRF whether it is sound (`check`), what circuitRF made of it
(`explain`), and what it does (`run`). The file formats are the interface, and the
[reference topics](#topics) describe every one of them, so the agent does not need circuitRF's
source code.

A capable agent, given a filter specification and the GaAs technology that ships with circuitRF,
produces the following in about a quarter of an hour:

- a synthesised prototype;
- a version built from elements that technology can realise, simulated against the spec and its
  tolerance corners;
- a drawn schematic;
- a layout that passes DRC;
- a checked EM setup;
- a GDSII file;
- a report saying what is still unverified.

It will also tell you where it had to work things out for itself. [Where agents go wrong](#traps)
lists the places that happen today.

## Connecting the agent {#setup}

The server is the circuitRF application itself, started with `serve`. The agent's client starts it,
so you only need to tell the client how to.

**1. Choose a root folder.** The server reads and writes only inside it, and refuses any path that
leads out of it. Use the folder your designs live in, not your home folder.

**2. Print the configuration.** circuitRF writes it for you, with the full path to the executable
filled in:

<pre><code class="cmd"><span class="prompt">$ </span>/Applications/circuitRF.app/Contents/MacOS/circuitRF serve --root ~/designs --print-config</code></pre>

On Windows the executable is `%LOCALAPPDATA%\Programs\circuitRF\circuitRF.exe`; on Linux,
`~/.local/bin/circuitrf`. The JSON it prints goes into your client's MCP settings. For Claude Code,
run the `claude mcp add …` line it prints underneath.

**3. Start a new session.** Most clients load MCP servers only when a session starts.

**4. Check that it answers.** Ask the agent to call circuitRF's `reference` tool with no topic. It
should list the reference topics with their sizes. If it cannot see a circuitRF tool, the client
did not start the server: run the printed command yourself and read what it says on stderr.

The [Command Line chapter](cli.html#serve) describes the server in full, including what it refuses
and how to run it from a source build.

## What to tell it {#brief}

Give the agent the same things you would give a colleague: the specification, the process, what
you want back, and how you will judge it. A brief that has worked well:

```text
Design a 50 ohm two-port low-pass filter as an MMIC on the GaAs technology that ships with
circuitRF. Insertion loss <= 1 dB from 0.1 to 3 GHz, return loss >= 10 dB at both ports over the
same band, rejection >= 20 dB from 6 to 12 GHz.

Work in a circuitRF workspace named lc_filter. Deliver: a circuit-level design simulated against
the spec, a schematic, a layout that passes check, an EM setup that passes check, a GDSII export,
and REPORT.md with the performance against each spec line and everything that is unverified.

Read circuitRF's reference topics before writing a file of a kind you have not written yet. Run
check after every file you write. Ask me before starting any EM run, and tell me its unknown count
first.
```

Five points in that brief do most of the work:

- **Name the technology**, or say that it is one that ships with circuitRF. The `technologies`
  reference topic lists every shipped technology with its stackup.
- **Ask for a report of what is unverified.** An agent that has no EM result will say so, and
  you need to know which numbers come from a circuit model and which from a field solve.
- **Tell it to `check` after every write.** It costs nothing, and it catches most mistakes before
  they reach a simulation.
- **Make it ask before EM.** An EM run can take minutes or hours. [The next section but one](#em)
  explains how to estimate the cost first.
- **Ask for a friction log** if you are evaluating circuitRF or the agent. "Note every time you
  guessed a syntax, were refused, or computed something yourself" produces a list worth reading.

## The reference topics it should read {#topics}

`reference` with no topic lists every topic and its size. The ones an agent needs, in the order it
usually needs them:

| Topic | When | What it gives |
|---|---|---|
| `technologies` | before creating the workspace | every shipped technology: its id, stackup and materials |
| `netlist` | before the first `.cnl` | the instance line, analysis and measurement syntax |
| `analyses` | before declaring any analysis | every `type=` and every key it accepts, with defaults |
| `tuning`, `goals` | before writing a tune, preset, goal or optimize line | which values can vary and how they are named; the goal grammar; each with a complete worked example |
| `component-index` | when choosing parts | every component type in one line each: nets, category, name |
| `components <TYPE>` | before using a component type | its nets, terminals and parameters |
| `units`, `expressions` | when writing values or measurements | SI suffixes, functions, conditionals |
| `measurements` | when checking against a spec | named quantities computed from a run |
| `shipped-materials` | before choosing or defining a material | every material that ships, with its properties |
| `technology`, `materials` | before reading or writing a `.ctech` / `.cmat` | the stackup, layers, DRC rules and material records |
| `schematic` | before the first `.csch` | components, wires, variables and analyses, with a worked example |
| `layout` | before the first `.clay` | shapes, layers, nets, ports |
| `em-setup` | before the first `.cem` | solver, mesh, ports and sweep |

**Start from `component-index`, then ask for one type at a time.** `reference components` with no
type is the whole catalogue, around 100 kB, which is more than many agents can take in one tool
result. The index is a few kilobytes, and `components <TYPE>` is a few hundred bytes.

The same topics are offered as MCP *resources* at `circuitrf://reference/<topic>`, which cost less
for clients that support them.

## The design flow that works {#flow}

The order below keeps every expensive step behind a cheap check.

1. **Create the workspace on the technology.** Use `create` with `what=workspace` and a `tech`.
   Every later document in the workspace resolves that technology.
2. **Synthesise an ideal prototype** as a `.cnl` of ideal L and C, and `run` it. This proves that
   the topology and values can meet the spec before anything physical is drawn.
3. **Rebuild it from realisable elements**: microstrip, vias and capacitors the process can make.
   Run it again, including tolerance corners. Each spec line becomes one `measure`: `max_over` and
   `min_over` give the worst value of a quantity over a frequency band, for example
   `measure IL_worst = max_over(-dB(SP1.S(2,1)), 0.1GHz, 3GHz)`. The agent reads them back with
   `read`. See [Measurements](measurements.html).
4. **Draw the layout** (`.clay`). Run `check` for DRC, `render` it to look at, and run `impedance`
   to compare each drawn line's Z0 with what the circuit model assumed.
5. **Write the EM setup** (`.cem`) and run `check` on it. Its notes report what the solver will mesh
   and how large the problem is. See the next section.
6. **Run EM** once the size is acceptable, then `read` the Touchstone and compare it with step 3.
7. **Export** the artwork with `convert`, for example a `.clay` in and a `.gds` out.

## EM runs: check the cost first {#em}

An EM run is the one step that can take hours. Before starting one, the agent should run `check` on
the `.cem` and read its mesh budget: the number of unknowns, the solver's ceiling, and the mesher's
verdict. The budget is in the document's `em` entry, so it is there even when `summary` collapses the
notes. A mesh setting that `Auto` overrides is reported as a warning naming it. Coarsen the mesh, or narrow what is meshed, until the number is
acceptable, and only then ask circuitRF to run it. [EM Setup](em-setup.html) describes each mesh
setting. [EM Solvers](em-solvers.html) explains what each solver costs.

## Where agents go wrong {#traps}

These are the places an agent working only from the reference topics is likely to stumble.

**Keep netlists inside the workspace.** A microstrip or via in a `.cnl` takes its substrate from
the workspace technology, exactly as one in a schematic does, and `check` says which numbers it
took. Outside any workspace there is no technology, so a line that names a layer is refused and a
line that names none uses a default board substrate.

**Read `check`'s warnings, not just its error count.** A line the netlist reader does not recognise
is skipped and reported as a warning, and warnings do not change the exit code. An agent that
invents a statement will see it reported there, and nowhere else.

**A drawn schematic takes more care than a netlist.** The `schematic` topic gives a complete working
example, and `components <TYPE>` gives each symbol's pin positions, but every wire has to end exactly
on a pin. Have the agent run `check` and `netlist` on the `.csch`: `check` reports a component it
cannot identify, and `netlist` shows what the drawing actually connects. Simpler still, have it
write the netlist and let `netlist` with `toSchematic` draw it
([`--to-schematic`](cli.html#netlist-to-schematic)). The drawing extracts back to the same circuit,
and every net carries its netlist name.

## Keeping its work reversible {#safety}

Nothing the server does deletes or overwrites a workspace. An agent's own file edits are another
matter, because it writes files with its own tools. If the workspace has [history](history.html)
turned on, the agent can take a restore point before a change (`history` with `checkpoint`), and
`batch` groups several calls under one restore point. [History and AI
assistants](history.html#assistants) describes how that works.
