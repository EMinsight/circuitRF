// brief-idle-power — CRF_DIRTY_RECTS=1 makes every window flash each region Avalonia's compositor redraws, so a still window
// that keeps flashing names what keeps it awake at a glance. Unset (every normal and packaged run) it subscribes nothing.
// A MODULE INITIALIZER for the reason UiVerilogACacheInstaller gives: three Main methods, one rule.

using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Rendering;

namespace CircuitRF.Ui.Diagnostics;

internal static class DirtyRectsOverlay
{
    [ModuleInitializer]
    internal static void InstallIfRequested()
    {
        string? v = Environment.GetEnvironmentVariable("CRF_DIRTY_RECTS");
        if (string.IsNullOrWhiteSpace(v) || v == "0") return;
        Control.LoadedEvent.AddClassHandler<TopLevel>((top, _) =>
        {
            try { top.RendererDiagnostics.DebugOverlays |= RendererDebugOverlays.DirtyRects; }
            catch { /* a diagnostic is never a reason for a window to fail */ }
        }, RoutingStrategies.Direct);
    }
}
