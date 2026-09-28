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
