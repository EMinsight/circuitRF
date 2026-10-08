using System;

namespace CircuitRF.Ui.Yield;

/// <summary>
/// What the Properties Inspector and the canvas ask the Yield panel (brief-yield-10 R-ya10-3): does this value carry a
/// tolerance, and add or switch one off. The keys are the Tuning surface's (<see cref="Tuning.ITuningSurface.KeysFor"/>)
/// — one catalog, one entry per value (yield overview D1) — so all three ways of adding a tolerance set the same flag
/// in the same single undo step.
/// </summary>
public interface IToleranceSurface
{
    /// <summary>Whether the entry for <paramref name="key"/> carries a tolerance that is on.</summary>
    bool IsToleranced(string key);

    /// <summary>Adds (with the default spread) or switches off the tolerance — one undo step in the schematic.</summary>
    void SetToleranced(string key, bool on);

    /// <summary>Brings the Yield panel forward on the value — what right-click ▸ Tolerance… ends with.</summary>
    void Reveal(string key);

    /// <summary>Raised when what is toleranced may have changed.</summary>
    event EventHandler? Changed;
}

/// <summary>The <see cref="IToleranceSurface"/> every schematic session is given: it forwards to whichever Yield panel
/// the workspace currently has, as <see cref="Tuning.TuningSurfaceRelay"/> does for Tuning.</summary>
public sealed class ToleranceSurfaceRelay(Func<YieldPanelViewModel?> current, Action? reveal = null) : IToleranceSurface
{
    public void Reveal(string key) => reveal?.Invoke();

    public bool IsToleranced(string key) => current()?.IsToleranced(key) == true;

    public void SetToleranced(string key, bool on) => current()?.SetToleranced(key, on);

    public event EventHandler? Changed
    {
        add    { if (current() is { } p) p.Changed += value; }
        remove { if (current() is { } p) p.Changed -= value; }
    }
}
