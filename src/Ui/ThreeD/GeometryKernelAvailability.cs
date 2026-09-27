using Avalonia.Threading;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Ui.ThreeD;

/// <summary>
/// brief-em3d-63 R-em3d63-3c — the GUI's binding to <see cref="GeometryKernel.Capability"/>: probed ONCE, in the
/// background, after the main window is shown (a process start is tens of milliseconds and is kept off the launch
/// path), and re-probed by the Settings row's <i>Check again</i>. Until the probe answers, a kernel command is
/// disabled with <see cref="GeometryKernel.Checking"/>; after it, with <see cref="GeometryKernel.NeedsKernel(string, GeometryKernelCapability)"/>.
///
/// <para>Holds no wording of its own: every sentence is <see cref="GeometryKernel"/>'s, the one place the absence is
/// worded. Briefs 66–69 bind their commands' enabled state and tooltip to <see cref="DisabledReason"/> and refresh
/// on <see cref="Changed"/>.</para>
/// </summary>
public static class GeometryKernelAvailability
{
    private static int _probed;

    static GeometryKernelAvailability()
    {
        GeometryKernel.Shared.CapabilityChanged += _ => Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// <summary>Raised on the UI thread whenever the capability is settled or changes.</summary>
    public static event Action? Changed;

    /// <summary>What the probe found, or null while it has not answered.</summary>
    public static GeometryKernelCapability? Current => GeometryKernel.Shared.Known;

    /// <summary>True when kernel commands can run.</summary>
    public static bool IsAvailable => Current?.Available == true;

    /// <summary>Starts the one background probe. Called when the first workspace window has opened; later calls do nothing.</summary>
    public static void ProbeOnceInBackground()
    {
        if (Interlocked.Exchange(ref _probed, 1) == 0) _ = GeometryKernel.Shared.ProbeAsync();
    }

    /// <summary>The tooltip for a disabled kernel command, or null when it is enabled — <paramref name="what"/> is the
    /// command, capitalised as the sentence's subject: <c>"Boolean"</c>, <c>"Fillet"</c>, <c>"Import STEP"</c>.</summary>
    public static string? DisabledReason(string what) => GeometryKernel.DisabledReason(what, Current);
}
