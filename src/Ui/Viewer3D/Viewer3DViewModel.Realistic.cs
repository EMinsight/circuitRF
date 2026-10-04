// brief-em3d-106 R-em3d106-1 — the realistic view's toggle: VIEW STATE, never saved, off on every open (overview D5), and toggling
// it marks nothing dirty. Turning it on asks the session for the shade stream and the environment's textures ONCE (R-em3d106-1e);
// it re-tessellates and re-elaborates nothing. The environment is prefiltered off the UI thread the first time a studio or a file
// is asked for, and cached after that, so a second toggle is immediate; until it is ready the view draws as the default view does
// and the status line says so.

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
    /// fallen-back file keeps Studio's key, so it is compared on the file's own path in its label.</summary>
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

    /// <summary>After a new scene: its appearance fallbacks may differ.</summary>
    private void RealisticSceneAdopted()
    {
        if (IsRealistic && View.Environment is { } env) UpdateRealisticText(env);
    }
}
