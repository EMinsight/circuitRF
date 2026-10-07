using System;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tuning;

/// <summary>
/// The <see cref="ITuningSurface"/> every schematic session is given: it forwards to whichever Tuning
/// panel the workspace currently has. A layout rebuild (a workspace switch) makes a new panel, and a
/// session built before it must not go on talking to the old one.
/// </summary>
public sealed class TuningSurfaceRelay(Func<TuningPanelViewModel?> current) : ITuningSurface
{
    public string? KeyFor(SchematicEditModel drawing, EditableComponent component, EditableParameter parameter)
        => current()?.KeyFor(drawing, component, parameter);

    public bool IsTuned(string key) => current()?.IsTuned(key) == true;

    public void SetTuned(string key, bool on) => current()?.SetTuned(key, on);

    public event EventHandler? Changed
    {
        add    { if (current() is { } p) p.Changed += value; }
        remove { if (current() is { } p) p.Changed -= value; }
    }
}
