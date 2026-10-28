namespace CircuitRF.Ui.Optimization;

/// <summary>
/// What the Properties Inspector asks the Optimizer panel: is this value optimized, and add or remove it. The keys are
/// the Tuning surface's (<see cref="Tuning.ITuningSurface.KeysFor"/>) — one catalog, one entry per value — so the
/// Inspector's optimize toggle and the Optimizer's own ＋ and ☐ set the same flag in the same single undo step, as
/// <see cref="Tuning.ITuningSurface"/> does for Tuning and <see cref="Yield.IToleranceSurface"/> for Yield.
/// </summary>
public interface IOptimizeSurface
{
    /// <summary>Whether the entry for <paramref name="key"/> has its opt flag set.</summary>
    bool IsOptimized(string key);

    /// <summary>Sets or clears the opt flag — one undo step in the schematic.</summary>
    void SetOptimized(string key, bool on);
}

/// <summary>The <see cref="IOptimizeSurface"/> every schematic session is given: it forwards to whichever Optimizer
/// panel the workspace currently has, as <see cref="Tuning.TuningSurfaceRelay"/> does for Tuning.</summary>
public sealed class OptimizeSurfaceRelay(System.Func<OptimizerPanelViewModel?> current) : IOptimizeSurface
{
    public bool IsOptimized(string key) => current()?.IsOptimized(key) == true;

    public void SetOptimized(string key, bool on) => current()?.SetOptimized([key], on);
}
