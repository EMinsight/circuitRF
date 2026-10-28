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

**The Model Composer** opens on a `.cmc` as File ▸ Open does, or on a new document of a chosen kind:

```
dotnet run --project tools/UiSnapshot --no-build -- --cmc <file.cmc|new:Passive|new:Active> -o out.png \
    [--step <name>] [--run <key>] [--marker]... [--import <data>] [--size WxH] [steps]
```

| Option | Means |
|---|---|
| `--step <name>` | Selects a step (`Topology`, `Fit`, ...). A step that is not enabled yet stays unselected. |
| `--run <key>` | Runs one of the step's tasks (`propose`, `analyse`, `fit`, ...) to the end, then prints the status line, any proposal note and the proposals. |
| `--marker` | Adds a marker on the S plot's first trace, its info box placed as a new marker's is. Repeat for several. |
| `--import <data>` | Photographs the Import Data dialog instead, with `<data>` chosen as its source. On a `new:` document the importer works against its provisional `.cmc` beside the data, and prints it. |

Without `--size` it takes the window's own size; `--size 1400x866` shows the whole window.

**The EM Setup editor** opens on a `.cem`, as the workspace opens one, and prints every visible named check box's state,
position and tooltip, so a setting is read as well as seen:

```
dotnet run --project tools/UiSnapshot --no-build -- --cem <file.cem> -o out.png [--size WxH] [steps]
```

Default size 900x1200.

**The 3D editor** draws a `.c3d` on the platform's own GPU backend (Metal, D3D11 or Vulkan — what the application
picks), through the editor's view model and Export Picture's own `CapturePicture`. No window is opened, so this mode
starts no Avalonia platform at all:

```
dotnet run --project tools/UiSnapshot --no-build -- --c3d <file.c3d> -o out.png [--size WxH] [--realistic] \
    [--fit] [--ortho|--perspective] [--yaw deg] [--pitch deg] [--distance mm] [--target x,y,z] \
    [--only a+b] [--hide a+b] [--list]
```

| Option | Means |
|---|---|
| (none) | The camera the workspace's `.cwsuser` stored for this view — the one its owner left it at, projection included. |
| `--fit` | The editor's opening fit instead of the stored camera. |
| `--yaw`, `--pitch` | Degrees, as the camera holds them (pitch > 0 is above the target). Applied after the stored camera or the fit. |
| `--distance` | Millimetres from the target; in orthographic it is also the zoom (half the view's height is distance × tan(0.35)). |
| `--target x,y,z` | Millimetres, in the scene-local frame `--list` prints. |
| `--realistic` | The realistic view, once its environment is made. |
| `--only`, `--hide` | Objects by substring of their name, `+` between several — what unticking them in the tree does. |
| `--list` | Every object's name, kind and scene-local bounds (mm). Alone, it writes no picture. |

Every run prints the camera it drew from, so a picture can be reproduced and nudged. The default size is 1200x800.

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

## The layout editor

```
dotnet run --project tools/UiSnapshot --no-build -- --clay <file.clay> -o out.png [--hide <layer>] [--lock <layer>] \
    [--size WxH] [steps]
```

The real `LayoutEditorView` on a `LayoutDocument`, the technology resolved from the document's `TechRef`, fitted.
Messages print as they are posted. Besides the steps below:

| Option | |
|---|---|
| `--hide`, `--lock` | Untick a layer's row in the layer list (its own `Visible`/`Selectable`). Repeat for several. |
| `@x,y` | Any step's point in the layout's micrometres instead of window DIPs, through the canvas's viewport. |
| `--rclick x,y` | Right press and release: the canvas's context menu opens. It is drawn in the window's own frame. |
| `--menu <header>` | Clicks the open context menu's item whose header starts with this (submenus searched), then closes it. A miss lists the headers. |
| `--type <text>` | Types into the open dialog's first text box, replacing what is there. `--type Name=text` sets the text box of that `x:Name` instead (an inline box such as `SizeInput`). |
| `--button <label>` | A real click on the open dialog's visible button with this label. A miss lists the buttons. |
| `--impedance <freq\|none>` | Runs the editor's own Impedance Analysis with that highest frequency (`6GHz`, or `none`) before the steps, and prints each trace's findings. The canvas shows its overlay. |
| `--finding <kind>` | With `--impedance`: selects the first finding of that kind (`pad-shunt`, `return-broken`, …) as the panel's row does, and zooms to it. The selection status prints. |
| `--impedance-layer <name>` | With `--impedance`: run on this copper layer only. Repeat for several. |
| `--impedance-panel` | With `--impedance`: the real Impedance panel on the editor, waited until its export page counts are in. The export choice and its note print, and `<out>.impedance.png` is the panel scrolled to **Export PDF…**. |

A capture while a dialog is open also writes `<out>.dialog.png`, the dialog's own window. A commanded "open" of
another document (Crop to New Cell's new cell) prints `opens <path>`; there is no workspace to open it in.

A drag that STARTS on a shape moves it, as in the editor: begin a marquee off the board.

## The picture dialog

```
dotnet run --project tools/UiSnapshot --no-build -- --image <picture> -o out.png [--tech <file.ctech>] [--digitise] \
    [--make-schematic] [--size WxH] [steps]
```

The real `CreateSchematicFromArtworkDialog` on an `ImageSourceViewModel`, as **Create Layout from Image…** opens it
(**Create Schematic from Image…** with `--make-schematic`; **Tools ▸ Digitise Image…** with `--digitise`, which opens
empty and is handed the picture as a drop is). The workspace is the picture's folder and `--tech` its one technology —
copy the technology's material library (`.cmat`) beside it, or the technology does not load and the dialog says there
is none. Each step waits for the reading to settle, then prints one line: the title, the kind read, what Make makes,
the scale, the armed tool and the canvas prompt. `--type`, `--button`, `--click` and `--drag` act on the dialog itself.

## Vendor libraries

```
dotnet run --project tools/UiSnapshot --no-build -- --palette <workspace> [--category <name>] [--search <q>] -o out.png [steps]
dotnet run --project tools/UiSnapshot --no-build -- --schematic <file.csch> [--select <instance>] -o out.png [steps]
dotnet run --project tools/UiSnapshot --no-build -- --archive <workspace> -o out.png
dotnet run --project tools/UiSnapshot --no-build -- --manage-libraries <workspace> -o out.png
dotnet run --project tools/UiSnapshot --no-build -- --add-vendor-library <folder> -o out.png
```

Each first indexes the workspace's referenced vendor libraries, as a workspace open starts them, so captions, counts
and lists are filled in. `--palette` is the Library palette with a placement service (so a row arms), and prints the
focus and the armed row after every step. `--schematic` is the schematic view beside the Parameter Editor on
`--select`'s instance; `--type VendorPartBox=<text>` types into the Part field. `--manage-libraries` and
`--add-vendor-library` open their dialog over an empty window (`<out>.dialog.png`). A list drop-down opened by a
popup is drawn in the window's own frame, and so is a button's menu flyout opened by a real `--click` on the button
(the palette's Type menu and its sub-menus). `--palette` prints the Type menu's state, item count and the selection
after every step — a menu that opens with no items shows up as a near-zero presenter.

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
