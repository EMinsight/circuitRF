// brief-em3d-106 R-em3d106-1 — the realistic view's toggle: VIEW STATE, never saved in the document, and toggling
// it marks nothing dirty. Turning it on asks the session for the shade stream and the environment's textures ONCE (R-em3d106-1e);
// it re-tessellates and re-elaborates nothing. The environment is prefiltered off the UI thread the first time a studio or a file
// is asked for, and cached after that, so a second toggle is immediate; until it is ready the view draws as the default view does
// and the status line says so. It opens OFF unless the per-user Realistic3DPreference says otherwise — the workspace applies
// that after the editor starts, exactly as the toolbar button would (overview D5 as amended 2026-10-04).

using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D.Look;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    /// <summary>The host's Look and the <c>.c3d</c> path a <c>.hdr</c> is relative to (the 3D editor's document); null for none —
    /// the defaults then.</summary>
    public Func<(C3dLook? Look, string? DocumentPath)>? LookSource { get; set; }

    /// <summary>How an environment is made: <see cref="EnvironmentPrefilter.For"/>, on a worker thread. Tests may answer at once.</summary>
    public Func<C3dStudio, string?, PrefilteredEnvironment> EnvironmentFor { get; set; } = EnvironmentPrefilter.For;

    /// <summary>R-em3d106-1a — the toolbar's toggle and 3D ▸ View ▸ Realistic View.</summary>
    [ObservableProperty] private bool _isRealistic;

    /// <summary>R-em3d106-1d — the status line while it is on: <c>Realistic · Studio · 0 EV</c>, and what fell back.</summary>
    [ObservableProperty] private string _realisticText = "";

    private int _lookRequest;

    /// <summary>brief-em3d-108 — how many times the Look reached the view: each one a uniform write on the next frame, nothing more
    /// unless the environment itself changed.</summary>
    public int LookWrites { get; private set; }

    partial void OnIsRealisticChanged(bool value)
    {
        View.Realistic = value;
        Session.ShadeStream = value;
        if (value) RefreshLook();
        else
        {
            _lookRequest++;
            View.Environment = null;
            Session.Environment = null;
            RealisticText = "";
        }
        FrameRequested?.Invoke();
    }

    /// <summary>The host's Look changed (an undo, a reload, brief 108's panel): read it again. Nothing happens while the view is off.</summary>
    public void LookChanged()
    {
        if (IsRealistic) RefreshLook();
    }

    private void RefreshLook()
    {
        var (look, documentPath) = LookSource?.Invoke() ?? (null, null);
        var parsed = RealisticLook.From(look);
        View.Look = parsed;
        LookWrites++;
        string? hdr = parsed.HdrPath is { } p ? C3dLook.ResolvePath(p, documentPath) : null;
        var current = View.Environment;
        string label = (look ?? new C3dLook()).EnvironmentLabel;
        int request = ++_lookRequest;
        // A rotation, exposure or intensity edit keeps the environment: only the uniforms change, and nothing uploads.
        if (current is not null && current.Key == KeyOf(parsed.Studio, hdr, current))
        {
            UpdateRealisticText(current);
            FrameRequested?.Invoke();
            return;
        }
        RealisticText = $"Realistic · preparing {label}…";
        var make = EnvironmentFor;
        Task.Run(() => make(parsed.Studio, hdr)).ContinueWith(t => _post(() =>
        {
            if (_disposed || request != _lookRequest || !IsRealistic) return;
            if (t.Exception is { } e)
            {
                RealisticText = "Realistic view: the environment could not be made — " + e.GetBaseException().Message;
                return;
            }
            View.Environment = t.Result;
            Session.Environment = t.Result;
            UpdateRealisticText(t.Result);
            FrameRequested?.Invoke();
        }), TaskScheduler.Default);
    }

    /// <summary>Whether <paramref name="current"/> already is what the Look names: a studio's key, or (for a file) the same file — a
    /// fallen-back file's key is <c>hdr:&lt;path&gt;|unread</c>, so it matches too and an exposure edit does not re-make it.</summary>
    private static string KeyOf(C3dStudio studio, string? hdr, PrefilteredEnvironment current)
        => hdr is null ? "studio:" + studio : current.Key.StartsWith("hdr:" + Path.GetFullPath(hdr) + "|", StringComparison.Ordinal) ? current.Key : "";

    private void UpdateRealisticText(PrefilteredEnvironment env)
    {
        var look = View.Look;
        string text = $"Realistic · {env.Label} · {C3dLook.FormatEv(look.ExposureEv)} EV";
        if (env.Fallback is { } why) text += " · " + why;
        if (Scene.AppearanceFallbacks > 0)
            text += $" · {Scene.AppearanceFallbacks:N0} object{(Scene.AppearanceFallbacks == 1 ? "" : "s")} use a default look " +
                    $"(more than {Pbr.TableRows} appearances)";
        RealisticText = text;
    }

    // ── brief-em3d-108 R-em3d108-3d — the picture camera (overview D17) ─────────────────────────────────────────────────

    /// <summary>
    /// The live camera as a Look's <c>Camera</c>: the direction from the target toward the eye, the target and the distance in DBU
    /// (<paramref name="dbuPerMicron"/> — every <c>.c3d</c> coordinate is DBU), the vertical field of view in degrees and the
    /// projection. Numbers are written exactly (round-trip), so <see cref="GoToPictureCamera"/> puts the same camera back.
    /// </summary>
    public C3dLookCamera PictureCamera(int dbuPerMicron)
    {
        var c = View.Camera;
        double perMetre = 1e6 * dbuPerMicron;
        var (x, y, z) = Scene.ToWorld(c.Target);
        var back = c.Back;
        return new C3dLookCamera
        {
            Direction = [back.X, back.Y, back.Z],
            Target = [x * perMetre, y * perMetre, z * perMetre],
            Distance = c.Distance * perMetre,
            FovY = c.FovY * 180.0 / Math.PI,
            Projection = c.Projection == CircuitRF.Render.Scene3D.Projection3D.Orthographic ? C3dLookCamera.Orthographic : C3dLookCamera.Perspective,
        };
    }

    /// <summary>Go to Camera View: the live view's camera set to <paramref name="camera"/>. False (nothing moved) for one a picture
    /// cannot be taken from.</summary>
    public bool GoToPictureCamera(C3dLookCamera camera, int dbuPerMicron)
    {
        // brief-em3d-110 — Camera3D.SetPictureCamera, which `render --look realistic` frames its picture with too
        if (!View.Camera.SetPictureCamera(camera, dbuPerMicron, Scene.ToLocal)) return false;
        IsPerspective = !camera.IsOrthographic;
        View.Camera.Projection = camera.IsOrthographic ? CircuitRF.Render.Scene3D.Projection3D.Orthographic : CircuitRF.Render.Scene3D.Projection3D.Perspective;
        FrameRequested?.Invoke();
        return true;
    }

    /// <summary>After a new scene: its appearance fallbacks may differ.</summary>
    private void RealisticSceneAdopted()
    {
        if (IsRealistic && View.Environment is { } env) UpdateRealisticText(env);
    }
}
