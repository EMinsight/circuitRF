# UiSnapshot

Hosts circuitRF's **real** Avalonia views in a headless window and writes a PNG of what they paint. It exists
because the GUI cannot be started from a shell with no window-server session (Avalonia.Native fails at its
render timer), so without it a UI change can be compiled and unit-tested but never looked at.

It is not `circuitrf render`. That verb draws a document through the Skia renderers below the UI firewall —
the picture an export makes. This tool draws the **Avalonia** views: the trace cards, the marker info boxes as
controls, the label strips, the inspector's own layout. Use `render` for what a plot draws and this for what a
window shows.

## Use

```
dotnet build tools/UiSnapshot
dotnet run --project tools/UiSnapshot --no-build -- <file.cdd> -o out.png [--view canvas|inspector|display] \
    [--plot n] [--size WxH] [--results <dir>] [--click x,y] [--drag x0,y0:x1,y1] [--key <Key>] [--shot <png>] ...
```

| Option | Means |
|---|---|
| `--view canvas` | The active tab's plot canvas (`PlotCanvasView`): plots, legends, label strips, marker info boxes. The default. |
| `--view inspector` | Plot *n*'s Plot Inspector (`PlotInspectorView`): the trace cards. |
| `--view display` | The whole Data Display document view (`DataDisplayView`): toolbar, canvas and tab strip, and its **key bindings** — the view to use for `--key`. |
| `--plot n` | Which plot, 1-based, for `--view inspector`. Default 1. |
| `--size WxH` | The headless window, in DIPs. Default 1000x700. The inspector scrolls inside its own region, so a taller window does not show more cards. |
| `--results <dir>` | Where a relative trace source resolves — a workspace's `results/` folder in the application. Default: the `.cdd`'s own folder, which is where `circuitrf plot --write-cdd` puts a document beside its result. |

**The Signal Designer** is the other view it opens. It takes no `.cdd`:

```
dotnet run --project tools/UiSnapshot --no-build -- --signal "FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0" \
    -o out.png [--signal "<another carrier>"] [--tab 1] [--fc <expr>] [--size WxH] [steps]
```

| Option | Means |
|---|---|
| `--signal "<carrier>"` | One `Carrier[i]` of the MOD_Source the Designer opens on, in the netlist form. Repeat for several. |
| `--tab n` | The preview tab, 0-based: 1 is EVM, Pulse or Frequency. Default 0. |
| `--fc <expr>` | The source's Fc, in GHz. Default 2. |
| `--standalone` | Open it as **Tools ▸ Signal Designer** does, on no MOD_Source, instead of `--signal`. |
| `--iq <file>` | With `--standalone`: read an I/Q file through **Load custom**. |

The window is the real `SignalDesignerWindow`, opened on its view model's `SetTarget` as the inspector's
**Design signal…** opens it, after its preview has finished. Without `--size` it takes the window's own size
(1199x800), and the carrier cards may then be below the fold. Use `--size 1199x1060` to see them.

**The Tuning window** opens on a schematic's ticked Tune rows, the way the shell hosts it:

```
dotnet run --project tools/UiSnapshot --no-build -- --tuning <file.csch> -o out.png [--size WxH] [steps]
```

After every step it prints which control holds the keyboard and each row's value, so what a key did is read
rather than guessed from pixels. Default size 420x500.

**Input steps run in the order given**, as real pointer and key events through the window, before the final
capture. Coordinates are window DIPs — take them from a first capture.

| Step | Does |
|---|---|
| `--click x,y` | Left press and release. |
| `--drag x0,y0:x1,y1` | Left press, ten moves (so it crosses a click threshold), release. |
| `--key K` | Press and release a key: `Back`, `Delete`, `Escape`, or with modifiers, `Meta+Z`, `Ctrl+Shift+A`. |
| `--shot out.png` | Capture an intermediate frame. |

Set `CRF_STATE_DIR` to a scratch folder so the run neither reads nor writes your own preferences.

A quick document to look at: `circuitrf plot run.npy -o x.png --trace … --write-cdd x.cdd`, then snapshot `x.cdd`.

## How it is faithful

- **The display is opened by `DisplayWindowViewModel.LoadAllAsync`** — the GUI's own File ▸ Open — so the plots,
  traces, markers and inspectors are built by the code the application builds them with.
- **`SnapshotApp` merges the application's own styles, resources and view templates** (taken from a constructed
  `App`, whose `Initialize` loads XAML and starts nothing). Unlike `tools/DocGen`'s `DocsApp` it merges no
  docs-only paint remap, so the colours are the application's.

## Traps

- **`UseHeadlessDrawing = false` is required.** The default stubs drawing out and every capture is blank, with no
  error.
- **Never block on a `Task` on the UI thread** (`GetResult()`, `.Wait()`): its continuations are posted to the
  same dispatcher and the process hangs. Pump `Dispatcher.UIThread.RunJobs()` until it completes — `Await`
  in `Program.cs`.
- **A frame needs pumping**: `RunJobs()` + `AvaloniaHeadlessPlatform.ForceRenderTimerTick()` + `UpdateLayout()`,
  a few times, before `CaptureRenderedFrame()`.
- **A relative source with no results root loads no traces, silently** — an empty plot, not an error. That is
  what `--results` is for.
- **Two clicks on one spot in quick succession are a double-click** — a `--click` followed by a `--drag` from the
  same point opens a plot's inspector. Start the drag first, or click somewhere else in between.
- **A key binding fires only while focus is inside the view that declares it.** Deleting the box that had focus
  leaves it nowhere, so a following `--key Meta+Z` does nothing here; in the application the shell's Edit menu
  serves that key. Click the toolbar's undo button instead.

## Adding a view

Build the view model the way the application does (its own load or factory, never a hand-assembled copy),
put the real view in the `Window`, pump, capture. To drive an edit, raise the real event on the real control —
`new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box }` commits a text box
through its own handler — and capture again.
