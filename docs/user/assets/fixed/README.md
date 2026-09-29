# Fixed pictures

The pictures in this folder are **fixed**, and the only raster images the documentation may hold
(`DocsFactoryTests.NoDocumentationImageIsABitmap` exempts this folder alone; a smooth field drawn as vector paths is
megabytes): DocGen links to them and never draws, writes or deletes them, and they do
not change when the example workspaces change. Every other figure under `assets/` is generated.

| File | What it is | Made from |
|---|---|---|
| `eight-fingers-temperature-top.png` | *Eight Fingers*' steady temperature, an XY cut 1 µm under the surface | the `Array` run of `examples/Thermal Channel vs Surface`, 2026-09-29 (brief-em3d-86) |
| `eight-fingers-temperature-section.png` | the same run, an XZ cut across the fingers at y = 1 µm | as above |

Both were drawn by circuitRF's own section renderer (`Em3dSectionRenderer`, the one `render --field` uses), with a
temperature clip-plane plot of that run and the colour range set to the slice's true minimum and maximum, as the 3D
view sets a temperature range. `render --field` itself refuses a temperature plot (brief-em3d-84, sections of EM
fields only), so reproducing them takes that same one-off setup, not a command.
