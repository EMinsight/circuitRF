// brief-em3d-28 R-em3d28-1b / R-em3d28-1d — what OUTLIVES the pane: the backend (device, pipelines,
// uploaded buffers) and which scene generation and overlay versions those buffers hold.
//
// A Dock float or re-dock detaches the pane from the visual tree and attaches a new one; the session
// stays with the document, so the new pane re-imports three images and uploads NO geometry. The
// frame's upload step compares numbers — generation, overlay versions — and uploads only what moved,
// which is what makes gate 3 ("100 camera changes upload 0 bytes") a property of the design rather
// than of care in the caller.

using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Render.Scene3D.Look;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DSession(Func<Viewer3DBackend> create) : IDisposable
{
    private long _uploadedGeneration = -1;
    private readonly long[] _overlayVersions = [-1, -1, -1];
    private long _fieldVersion = -1;
    private Scene3DModel? _uploadedScene;
    private Scene3DModel? _shadeScene;
    private PrefilteredEnvironment? _environment;
    private CircuitRF.Design.ThreeD.Appearance.AppearanceValues[]? _appearances;
    private bool _disposed;

    /// <summary>Serialises the render thread against a backend teardown.</summary>
    public object RenderLock { get; } = new();

    /// <summary>The backend, created on first use and kept until the document closes.</summary>
    public Viewer3DBackend? Backend { get; private set; }

    /// <summary>How many times a backend has been created — a re-dock must not add one.</summary>
    public int BackendsCreated { get; private set; }

    /// <summary>How many new scenes were uploaded as a patch rather than whole.</summary>
    public int PatchesApplied { get; private set; }

    /// <summary>
    /// brief-em3d-104 R-em3d104-3b/c — whether the backend holds the shade stream (the realistic view, brief 106, sets it). Off,
    /// a scene uploads exactly the bytes it always did; turned on, the next frame uploads the current stream once, and later
    /// generations and patches keep it in step; turned off, the next frame releases it.
    /// </summary>
    public bool ShadeStream { get; set; }

    /// <summary>
    /// brief-em3d-106 R-em3d106-1e — the environment the realistic view lights with (the view model sets it with
    /// <see cref="ShadeStream"/>). While the shade stream is on, the next frame uploads it once, and the scene's appearance table
    /// whenever its rows change; with the shade stream off, the next frame releases both. A rotation, an exposure or an intensity
    /// is a uniform and uploads nothing.
    /// </summary>
    public PrefilteredEnvironment? Environment { get; set; }

    /// <summary>How many times the environment, and the appearance table, have been uploaded (gate 7).</summary>
    public int EnvironmentUploads { get; private set; }
    public int AppearanceUploads { get; private set; }

    /// <summary>Counters on the UI lane: the pane's per-frame share on the UI thread.</summary>
    public FrameCounters Ui { get; } = new("ui");

    public Viewer3DBackend EnsureBackend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Backend is null)
        {
            Backend = create();
            BackendsCreated++;
        }
        return Backend;
    }

    /// <summary>
    /// Render thread: uploads whatever the frame needs that the backend does not already hold, then
    /// draws <paramref name="plan"/> into <paramref name="image"/>. Counted on the backend's lane —
    /// a frame whose camera alone changed adds 0 upload bytes. A session disposed under the render
    /// thread (the tab closed mid-frame) draws nothing and creates nothing: false.
    /// </summary>
    public bool Frame(int image, Scene3DFramePlan plan, ulong frame, Scene3DModel scene,
                      Scene3DOverlay mesh, Scene3DOverlay section, Scene3DOverlay grid, bool orbiting,
                      Scene3DFieldGeometry? field = null)
    {
        lock (RenderLock)
        {
            if (Backend is not { } b) return false;
            b.Counters.BeginFrame(orbiting);
            Upload(b, scene, mesh, section, grid, field);
            b.Render(image, plan, frame);
            b.Counters.EndFrame();
            return true;
        }
    }

    /// <summary>Whatever the frame needs that the backend does not already hold — compared by number, so a
    /// frame whose camera or phase alone changed uploads nothing.</summary>
    private void Upload(Viewer3DBackend b, Scene3DModel scene, Scene3DOverlay mesh, Scene3DOverlay section, Scene3DOverlay grid,
                        Scene3DFieldGeometry? field)
    {
        Scene3DModel? previous = null;
        Scene3DPatch? patch = null;
        if (!ReferenceEquals(scene, _uploadedScene) || scene.Generation != _uploadedGeneration)
        {
            // brief-em3d-43 gate 6: a scene with the last one's layout rewrites only what changed.
            previous = _uploadedScene;
            if (previous is not null && (patch = Scene3DPatch.Between(previous, scene)) is not null)
            {
                PatchesApplied++;
                b.PatchScene(scene, patch);
            }
            else b.UploadScene(scene);
            _uploadedScene = scene;
            _uploadedGeneration = scene.Generation;
        }
        // brief-em3d-104 — the shade stream, only while it is wanted: patched by the same comparison when the backend held the
        // previous scene's, else uploaded whole.
        if (!ShadeStream)
        {
            if (_shadeScene is not null) { b.ReleaseShade(); _shadeScene = null; }
            if (_environment is not null || _appearances is not null)
            {
                b.ReleaseEnvironment();
                (_environment, _appearances) = (null, null);
            }
        }
        else
        {
            if (!ReferenceEquals(_shadeScene, scene))
            {
                if (patch is { ShadeWhole: false } && _shadeScene is not null && ReferenceEquals(_shadeScene, previous)) b.PatchShade(scene, patch);
                else b.UploadShade(scene);
                _shadeScene = scene;
            }
            // brief-em3d-106 — the appearance table when its ROWS changed (a rebuilt scene with the same looks uploads nothing), and the
            // environment when it is a different one.
            if (_appearances is null || !_appearances.AsSpan().SequenceEqual(scene.Appearances))
            {
                b.UploadAppearances(Pbr.Table(scene.Appearances));
                _appearances = scene.Appearances;
                AppearanceUploads++;
            }
            if (Environment is { } env && !ReferenceEquals(env, _environment))
            {
                b.UploadEnvironment(env);
                _environment = env;
                EnvironmentUploads++;
            }
        }
        Sync(b, Scene3DBuffer.Overlay0, 0, mesh);
        Sync(b, Scene3DBuffer.Overlay1, 1, section);
        Sync(b, Scene3DBuffer.Overlay2, 2, grid);
        // brief-em3d-29 — the field's geometry, by version: a phase step changes a uniform and uploads
        // nothing (gate 5).
        field ??= Scene3DFieldGeometry.None;
        if (_fieldVersion != field.Version)
        {
            b.UploadField(field.Vertices);
            _fieldVersion = field.Version;
        }
    }

    /// <summary>
    /// brief-em3d-29 R-em3d29-5 — <paramref name="plan"/> (planned at the export's size) drawn offscreen by
    /// the same backend, with the same buffers, and read back as RGBA8 rows, top first. Holds the render
    /// lock, so the render thread is never mid-frame on the device meanwhile. Null when the session is gone.
    /// </summary>
    public byte[]? RenderPixels(Scene3DFramePlan plan, Scene3DModel scene, Scene3DOverlay mesh, Scene3DOverlay section,
                                Scene3DOverlay grid, Scene3DFieldGeometry? field)
    {
        lock (RenderLock)
        {
            if (_disposed || Backend is not { } b) return null;
            Upload(b, scene, mesh, section, grid, field);
            return b.RenderPixels(plan);
        }
    }

    private void Sync(Viewer3DBackend b, Scene3DBuffer slot, int i, Scene3DOverlay o)
    {
        if (_overlayVersions[i] == o.Version) return;
        b.UploadOverlay(slot, o.Lines);
        _overlayVersions[i] = o.Version;
    }

    public void Dispose()
    {
        lock (RenderLock)
        {
            _disposed = true;
            Backend?.Dispose();
            Backend = null;
        }
    }
}
