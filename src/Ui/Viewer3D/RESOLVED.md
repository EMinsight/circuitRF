# `src/Ui/Viewer3D` — resolved findings

Findings from work on the 3D viewer's view model. The field model itself (readers, surfaces, samplers) is recorded in
`src/Render/RESOLVED.md`.

## brief-em3d-82 — Plot Field on a face (2026-09-28)

- **The EM path now counts its builds.** `FieldGeometryBuilds` was temperature's alone; the EM branch of
  `ScheduleFieldGeometry` now increments it too, which is what gate 6 holds (100 phase steps with a painted face: no build,
  the same `FieldGeometry.Version`). A phase step was already uniforms-only; nothing about that changed.
- **Painted faces are resolved against the scene on the UI thread** (`FieldFaceTargets`: triangles, normal, role, tolerance)
  and painted off it. The painter keeps each region boundary it cuts for the next face of the same region within a build.
- **The list clears on a new run** (`RefreshFields`' not-the-same branch) **and on a solution whose volume mesh has a
  different node or cell count** (the other solver's, a re-meshed run) — the brief's "the run or the solution changes shape".
  Faces are scene faces, so a new solution on the same mesh keeps them.
- **A refused face never changes the quantity** (owner, Q2: keep the quantity). The sentence is appended to `FieldText`, after
  the triangle count, one per face.
- **The hover readout re-reads where the paint was read**: a boundary quantity through the boundary sampler, openEMS through
  `FieldFaces.SampleOffFace` (half a cell off), Palace a hair (1e-6 of the scene's diagonal) off the face along
  `FieldFacePaint.ReadToward` — without the hair, a point ON a sheet is located in whichever tetrahedron the bucket lists
  first, which is either side.
- **Plot Field is not refused on a STALE EM result**, unlike Plot Temperature: an EM field from an older run is still drawn
  (R-em3d49-5b — the banner says so and the picture is the solver's own geometry), so the item follows the field, not the
  banner.
- **Test cross-talk, not a regression:** `OpenEmsBackendTests.Gate10` counts the pids its `OpenEmsRun.ProcessStarted` handler
  sees. The event is static, so when another class's openEMS run (`FieldTests.Gate9`) runs concurrently in the same process
  the list holds three pids and `Assert.Single` fails. It passes alone.

## Reference images: the texture path on each backend (brief-em3d-101, 2026-10-03)

- **The 3D view had no texture path at all**; this brief added one, on all three APIs, for image sheets and images on faces.
  An image draw is its own pipeline (`Scene3DPipeline.Image` / `ImageTranslucent`), its own vertex stream
  (`Scene3DImageVertex`, 32 bytes: position, uv, id, face, colour — `Scene3DVertex`'s 24-byte stride is untouched) and its own
  shader pair (`vs_image` / `fs_image`). Picking never samples: the ID pass draws the object's ordinary triangles with
  `fs_pick`, so a fully transparent texel still picks (the brief's `fs_pick_image` was not needed and was not written).
- **UNORM, never sRGB.** Every backend creates `RGBA8 UNORM` (Metal 70, `R8G8B8A8_UNorm`, `VK_FORMAT_R8G8B8A8_UNORM`), so a
  texel reaches the framebuffer with the value a vertex colour of that value would — brief 69's sRGB trap. Levels are
  unpremultiplied RGBA, rows top first, built ONCE on the CPU (`Scene3DTextures.Reduce`, SkiaSharp Mitchell): no backend
  generates mips, so the three pictures stay alike. The long edge is capped at `C3dImages.MaxTexturePixels` (4096) first.
- **Uploads are keyed by identity, never per frame or per re-elaboration** (`Scene3DTextureResidency<T>`, `src/Render`).
  `Scene3DTextures.Get(path)` hands out one object per path until `Refresh(path)` (Refresh Image, Resolve Path…, Browse…),
  so a scene rebuilt for a move, a hide, a transparency or a Model toggle holds the SAME object and uploads nothing; the
  residency releases what no scene holds. `Uploads` is the counter gate 6 asserts. Every broken file is the one
  `Scene3DTextures.Placeholder` object, so they share one upload.
- **Metal**: `texture2DDescriptorWithPixelFormat:…mipmapped:` with `mipmapLevelCount` set to the CPU chain's length,
  `replaceRegion:mipmapLevel:withBytes:bytesPerRow:` per level (the `MTLRegion` struct goes by value through
  `objc_msgSend`), one `MTLSamplerState` (linear/linear, mip linear, clamp to edge), `setFragmentTexture:atIndex:0` and
  `setFragmentSamplerState:atIndex:0` before each image draw. Verified on this Mac (the gates' Metal read-backs).
- **D3D11**: an `Immutable` `ID3D11Texture2D` created with every level as `SubresourceData` (each level's array pinned for the
  call), a shader-resource view, one `SamplerDescription(MinMagMipLinear, Clamp)` at s0, `PSSetShaderResource(0, …)` per image
  draw. **The rasterizer states are indexed by `tie − Scene3DFramePlan.TieMin`** now that `Underlay` (−2) and `FaceImage`
  exist — the old `+ 1` would have read off the front of the array. Compiled here; NOT run on Windows.
- **Vulkan — the per-texture descriptors**: set 1 is its own layout (sampled image at binding 0, sampler at binding 1; set 0
  is unchanged and still written once). **Each texture owns a descriptor pool of exactly one set**, created in
  `UploadTexture` beside its image, view and memory, and destroyed in `ReleaseTexture` with them — destroying the pool frees
  the set. There is no shared pool to size, so a re-elaboration (or the hundredth) cannot exhaust one; release waits for
  the device idle first (a frame in flight may still sample it), which happens only when a scene drops an image, never per
  frame. The upload stages every level in one buffer and copies level by level between two layout transitions (undefined →
  transfer destination → shader read-only). Compiled here; NOT run on Linux.
- **HLSL samplers.** naga writes samplers through a D3D12 sampler heap that D3D11 cannot compile; ShaderGen rewrites it to a
  plain `SamplerState : register(s0)` and refuses to generate if the spelling changes (`tools/ShaderGen/README.md`).
- **A face image is clipped in the SHADER, not by a border sampler.** The brief proposed clamp-to-border with transparent
  black on each API; `fs_image` instead discards outside uv [0, 1], which every backend runs identically, needs no border
  colour (Metal's `clampToZero` is the odd one out) and spends no texture memory either. The one sampler is clamp-to-edge.
- **No nudges exist in the 3D editor**, so Locked has none to refuse; it refuses Move, Rotate, the quarter turns, Mirror,
  Align (as a mover — a locked image may be the reference), the gizmo and a vertex move. Duplicate is allowed (the copy moves).

## brief-em3d-101 follow-up: Vulkan texture upload on failure (2026-10-03)

- **`UploadTexture` released nothing if it failed part-way.** On out of device memory with a large photo, the staging buffer, the
  image and its memory were left behind. The staging buffer is now freed in a `finally`, and an upload that does not complete
  releases what it made through `ReleaseTexture`. The image's memory is recorded before it is bound, so a failed bind frees it.
  `NewBuffer` frees its buffer when the memory allocation fails, and `OneShot` frees its command buffer on any failure. Compiled
  only: no Vulkan device has run it.

## The shade stream: upload and binding (brief-em3d-104, 2026-10-04)

- **Lazy, by `Viewer3DSession.ShadeStream`** (brief 106 sets it). Off, nothing is uploaded or held: a scene costs exactly
  vertices + indices + lines, as before. On, the next frame uploads the whole stream once (16 bytes a vertex); a later scene is
  PATCHED when the backend held the previous scene's stream and the scene itself was patched, else uploaded whole. Off again,
  the next frame releases the buffer (Metal also drops any staged patch aimed at it).
- **Shade ranges are listed apart** (`Scene3DPatch.ShadeRanges`), compared object by object as the vertices are, so the default
  view's patch is byte-for-byte what it was. A recolour or a translation lists none (normals unchanged); a tilt lists the object.
- **Buffer mapping** (also at the top of `scene.wgsl` and in `tools/ShaderGen/README.md`): attributes `@location(4)` normal and
  `@location(5)` slot; Metal vertex buffer index **3** (0 geometry, 1 uniforms, 2 per-draw transform share one argument table),
  D3D11 input slot **1** (semantics LOC4, LOC5), Vulkan vertex binding **1**. No pipeline declares it yet; 106's do. The WGSL
  edit is comment-only: the regenerated shaders differ only in their hash line.
- **Gate 11 ran on Metal**: the CaseA scene drawn with and without the duplicates reads back identical bytes. D3D11 and Vulkan's
  upload/patch/release are compiled only.

## The realistic view: bindings, formats, what ran (brief-em3d-106, 2026-10-04)

- **Binding slots, per backend** (also in `scene.wgsl`'s realistic section and `tools/ShaderGen/README.md`):

  | What | WGSL | Metal | D3D11 | Vulkan |
  |---|---|---|---|---|
  | scene vertex | `@location(0..3)` | vertex buffer 0 | input slot 0 | vertex binding 0 |
  | shade stream (brief 104) | `@location(4)` normal, `@location(5)` slot | vertex buffer **3** | input slot **1** | vertex binding **1** |
  | uniforms `U` (now 2,304 B) | group 0 binding 0 | `[[buffer(1)]]`, inline bytes | `b0` | set 0 binding 0, dynamic |
  | per-draw transform | group 0 binding 1 | `[[buffer(2)]]` | `b1` | set 0 binding 1, dynamic |
  | appearance table (12,288 B) | group 0 binding 2 | fragment `[[buffer(4)]]`, an `MTLBuffer` (over the 4 KB inline limit) | `b2` | set 0 binding **2**, a plain uniform |
  | environment map / sampler / split-sum table | group 2 bindings 0/1/2 | `[[texture(1)]]`, `[[sampler(1)]]`, `[[texture(2)]]` | `t1`, `s1`, `t2` | set **2**, bindings 0/1/2 |

  The environment's sampler is the image sampler brief 101 made (linear, linear between mips, clamp), bound a second time
  at index 1. Vulkan's pipeline layout grew to three set layouts; binding set 0 again for a transform leaves set 2 bound,
  so the realistic draws bind set 2 once a frame. Vulkan keeps the 12 KB appearance buffer for the device's life (set 0
  names it, so its descriptor is always valid); Metal and D3D11 release theirs with the environment.
- **Texture format: RGBA16F on all three** (`MTLPixelFormatRGBA16Float` 115, `R16G16B16A16_FLOAT`, `R16G16B16A16_SFLOAT`),
  so the RGBE-in-RGBA8 fallback the brief allowed was not needed: every Metal device, D3D11 at feature level 10+ and every
  Vulkan device (a required format) samples it with linear filtering. The upload path took a format argument: Metal's
  `NewSampledTexture`, D3D11's `NewSampledTexture`, Vulkan's `NewSampledImage` (brief 101's images now go through it with
  RGBA8).
- **PbrTranslucent blends PREMULTIPLIED** (RGB one, 1 − src-alpha; alpha one, 1 − src-alpha): a reflection on glass then
  adds on top of what shows through instead of being capped at the coverage. The swap image's alpha still ends at 1.
- **The session decides the uploads** (`Viewer3DSession`): with `ShadeStream` on, the appearance table when its ROWS change
  (a rebuilt scene with the same looks uploads nothing) and the environment when it is a different object; with it off,
  `ReleaseEnvironment` and `ReleaseShade`. A rotation, exposure or intensity is the uniform look block and uploads nothing
  (gate 7 counts it).
- **Which backends ran**: **Metal** — the gate's offscreen frames (a mirror sphere against the CPU reference, within 3/255;
  the theme background; alpha 255 everywhere; a rough dielectric with no peak) and three studio pictures looked at by eye.
  **D3D11 and Vulkan were compiled only; their runtime is unverified on this machine**, as briefs 62 and 101 recorded.
  The HLSL is naga's (validated) and was not put through `d3dcompiler_47` here.
- **The mirror gate had to look at a smooth patch of the studio.** Behind the default camera, the unrotated Studio puts the
  fill softbox's soft edge exactly where a mirror sphere's centre reflects: 1° of normal moves the colour ~15/255, and the
  GPU's interpolated normal is a fraction of a degree off the exact one, so the first run read 238 against 226. A uniform
  environment matched to 0.1/255. The gate now turns the studio 90° and asserts the environment varies under 2/255 within
  ~1° of the reflected direction before comparing.

## Shadows, contact shading, the ground, the realistic picture (brief-em3d-107, 2026-10-04)

- **The shadow map's invalidation rule.** The map lives on the BACKEND between frames and is re-rendered only when
  `Scene3DFramePlan.ShadowKey` moves. The plan lists the casters every realistic frame (cheap: no GPU work) and hashes what
  the map depends on: the scene object and its generation (a new generation or a patch is a new scene), the light's matrix
  (the key's direction — so the Look's rotation — and the casters' bounds, so visibility), the map's size, and every caster
  draw with the contents of any per-frame transform slot it is drawn under (a drag's preview). Nothing about the camera is
  in it, so an orbit renders no shadow pass. **The session decides** (`Viewer3DSession.DecideShadowPass`, and its
  `ShadowPasses` counter): the pass runs when the key differs from the key the map was last rendered with; releasing the
  realistic view's lighting resets it. A picture plans a 4096² map (live is 2048²), so the next live frame after an export
  renders the map once more at its own size. Element casters are never LOD-boxed — the boxing depends on the eye.
- **The transparent picture's read-back is PREMULTIPLIED.** Only `RenderPixels` with `Scene3DFramePlan.Transparent` clears to
  (0, 0, 0, 0) and skips the backdrop; with the blend every pipeline already uses (colour src-alpha or one / 1 − src-alpha,
  alpha one / 1 − src-alpha) the pixels come back premultiplied with real alpha. `FieldPictureShot.Transparent` carries that
  through Compose (which paints legends on a premultiplied bitmap, so it stays right) and `Png()` straightens it in C#
  (`PictureResample.Unpremultiply`, rounded) and encodes it as UNPREMUL so Skia converts nothing. The live image is never
  cleared to 0: its alpha stays 1 (brief 27's rule). Copy never asks for transparency.
- **Supersampling** happens in `CapturePicture`: the plan is drawn at k × the output (k from 1, 2, 4, halved until the drawn
  side fits `FieldPicture.MaxSide`) and brought down by `PictureResample.Downsample` before the legends are painted, so the
  legend, caption and overlay layer are composed at the output size exactly as before.
- **Bindings** (also in `scene.wgsl` and `tools/ShaderGen/README.md`): group 3 — the shadow map, its
  comparison sampler, the blurred occlusion, the prepass's depths, the raw occlusion — is Metal fragment textures 3, 4, 5, 6
  and sampler 2; D3D11 t3, s2, t4, t5, t6; Vulkan set 3, bindings 0–4. The HLSL comparison sampler comes out of naga's
  COMPARISON sampler heap, which `plain_hlsl_samplers` now rewrites too (`SamplerComparisonState smap_s : register(s2)`).
- **Per backend.** Metal: three extra encoders (the shadow pass, the prepass into R32Float with the main depth texture
  reused, then the horizon pass and blur into two R8 targets), 1 × 1 stand-ins for an absent map or occlusion. D3D11: the
  map is R32_TYPELESS (a D32 view to draw, an R32 float view to read); t3..t6 are unbound before every pass that draws into
  one of them, since the runtime would otherwise unbind the input itself and the next pass would read nothing. Vulkan: three
  render passes that leave their targets SHADER_READ_ONLY_OPTIMAL, the prepass with its own depth buffer (no per-image
  framebuffer), set 3 always naming valid images (stand-ins made with the device and transitioned at once), and every map or
  target made, and set 3 re-pointed, BEFORE the command buffer is begun (a set may not change under a recording that binds
  it). The comparison sampler is linear only where the device filters D32 linearly (`_depthLinear`), else nearest.
- **What ran.** Metal: every brief-107 pixel gate (a box's shadow on its plate 30/255 darker than its mirror, gone with
  `Shadows: false`; glass at 0.9 casts nothing and 0.2 casts 30/255; an inside corner 21/255 darker, equal without occlusion;
  a field pixel in shadow and in a corner exactly (51, 102, 153); a transparent picture 0 / 40 / 255 alpha; a 50 % slab's
  straightened colour composited over black and white within 1/255). **D3D11 and Vulkan were compiled only; their runtime
  is unverified on this machine**, as briefs 62, 101 and 106 recorded.
