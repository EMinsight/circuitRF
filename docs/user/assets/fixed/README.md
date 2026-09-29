# Fixed pictures

The pictures in this folder are **fixed**, and the only raster images the documentation may hold
(`DocsFactoryTests.NoDocumentationImageIsABitmap` exempts this folder alone; a smooth field drawn as vector paths is
megabytes): DocGen links to them and never draws, writes or deletes them, and they do
not change when the example workspaces change. Every other figure under `assets/` is generated.

| File | What it is | Made from |
|---|---|---|
| `eight-fingers-temperature-top.png` | *Eight Fingers*' steady temperature, an XY cut 1 µm under the surface | the `Array` run of `examples/Thermal Channel vs Surface`, 2026-09-29 (brief-em3d-86) |
| `eight-fingers-temperature-section.png` | the same run, an XZ cut across the fingers at y = 1 µm | as above |
| `output-wires-dc-along-wire.png` | *Thermal Output Wires* at 14 A DC, an XZ cut along wire 4 (y = 75 µm) | the `DcSweep` run of `examples/Thermal Output Wires`, point 3, 2026-09-29 (brief-em3d-85) |
| `output-wires-dc-across.png` | the same run, a YZ cut across the six wires at x = 820 µm | as above |
| `output-wires-rf-across.png` | the `RfHarmonics` run at 8 A peak, 2 GHz, the same YZ cut: the edge wires hottest | the `RfHarmonics` run, point 3, 2026-09-29 (brief-em3d-85) |
| `output-wires-rf-plan.png` | the same run, an XY cut through the wires' loops at z = 330 µm | as above |

The two Eight Fingers pictures were drawn by circuitRF's own section renderer (`Em3dSectionRenderer`, the one `render --field` uses), with a
temperature clip-plane plot of that run and the colour range set to the slice's true minimum and maximum, as the 3D
view sets a temperature range. `render --field` itself refuses a temperature plot (brief-em3d-84, sections of EM
fields only), so reproducing them takes that same one-off setup, not a command.

The four *Output Wires* pictures were drawn the same way, with three more things the one-off setup did that `render` does
not (brief-em3d-88 is the brief that makes them its own): each bond wire painted from its own solved T(s) on the section's
range (a wire is a one-dimensional element and is not in the 3D field), the metals outlined instead of filled over the field,
and each solid labelled by its material, the mould compound as the overmold. Each was then cropped to the section, the legend
inset in its corner, and the flange below the die trimmed to a strip.
