// Will a planar (MoM) run fit in this machine's memory — asked once the run's mesh is final and before anything is
// filled, through PlanarSolveSettings.Admit.
//
// The answer goes out on the 3D solvers' own verdict type so it reaches the same question at Simulate (the panel's
// dialog) and the same --force on `circuitrf em`. The thresholds are NOT the 3D solvers': their estimate errs high by
// construction, so they confirm only past 150 %. The dense estimate here is the measured accounting of one frequency
// point (PlanarSystem.ResidentBytes, P1/P7), and de-embedding adds its calibration standards on top, so it errs LOW —
// past this machine's memory the run is asked about rather than allowed to swap.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>The planar solver's memory check: a warning past <see cref="WarnFraction"/> of physical memory, and a
/// question past <see cref="ConfirmFraction"/>.</summary>
public static class PlanarMemoryCheck
{
    /// <summary>What the memory limit is typed in: DECIMAL gigabytes, as every memory figure the EM
    /// panels show (<see cref="MachineMemory.Format"/>).</summary>
    public const double BytesPerGb = 1e9;

    /// <summary>Above this share of physical memory a run is warned about.</summary>
    public const double WarnFraction = 0.75;

    /// <summary>Above this share a run must be confirmed (the panel asks; the CLI needs --force).</summary>
    public const double ConfirmFraction = 1.0;

    /// <summary>The verdict for <paramref name="size"/> on a machine with <paramref name="physicalBytes"/> — or, when
    /// the machine's own memory limit is set (Settings ▸ Solvers), that limit, which every solver is held to. An
    /// accelerated run has no closed-form estimate and always fits.</summary>
    public static Em3dMemoryVerdict Evaluate(PlanarRunSize size, long physicalBytes, bool machineLimit = false)
    {
        string owner = machineLimit ? "this machine's memory limit of" : "this machine's";
        if (size.DenseResidentBytes is not { } e || physicalBytes <= 0)
            return new(Em3dMemoryLevel.Fits, size.DenseResidentBytes, physicalBytes, null);
        double share = (double)e / physicalBytes;
        if (share <= WarnFraction) return new(Em3dMemoryLevel.Fits, e, physicalBytes, null);

        bool severe = share > ConfirmFraction;
        string pct = (share * 100).ToString("0", CultureInfo.InvariantCulture);
        string text =
            $"This run's {size.Unknowns:N0} unknowns need about {MachineMemory.Format(e)} at each frequency point, " +
            $"{pct} % of {owner} {MachineMemory.Format(physicalBytes)}, before de-embedding adds its " +
            "calibration standards. " +
            (severe ? "At that size it will very likely swap heavily or be stopped by the operating system. "
                    : "A run this size may slow the machine or swap. ") +
            "The accelerated solve (Solver options) needs far less.";
        return new(severe ? Em3dMemoryLevel.Severe : Em3dMemoryLevel.Warning, e, physicalBytes, text);
    }

    /// <summary>
    /// The <see cref="PlanarSolveSettings.Admit"/> a run is given: a warning is added to <paramref name="warnings"/>,
    /// and a run past <see cref="ConfirmFraction"/> is asked about through <paramref name="confirm"/> — declined, or
    /// with no one to ask, it is not started.
    /// </summary>
    public static Func<PlanarRunSize, string?> Admitter(Func<Em3dMemoryVerdict, bool>? confirm, List<string> warnings,
                                                        long physicalBytes, bool machineLimit = false)
        => size =>
        {
            var verdict = Evaluate(size, physicalBytes, machineLimit);
            if (verdict.Text is not { } text) return null;
            if (verdict.Level != Em3dMemoryLevel.Severe) { warnings.Add(text); return null; }
            if (confirm?.Invoke(verdict) != true)
                return text + " It was not started. To start it anyway, confirm it when Simulate asks, or pass " +
                       "--force to `circuitrf em`.";
            warnings.Add(text + " It was started anyway, as confirmed.");
            return null;
        };
}
