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
