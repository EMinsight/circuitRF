using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Core.Devices.Planar;

/// <summary>
/// The width a CPWG or SLIN needs for a Z₀ — bisection on ln W over the model ITSELF, so the inverse can
/// never disagree with the forward direction (the rule <see cref="HammerstadJensen.SynthesizeWidth"/>
/// states for MLIN). Z₀ falls monotonically as the strip widens with everything else held, which is all
/// a bisection needs. The line calculator, the parameter editor's Z0 field and placement defaults call
/// these, so all three agree on the width a Z₀ needs.
/// </summary>
public static class PlanarLineSynthesis
{
    /// <summary>The search spans W from this fraction of the substrate height…</summary>
    public const double MinWOverH = 1e-3;
    /// <summary>…to this multiple of it.</summary>
    public const double MaxWOverH = 100.0;

    /// <summary>
    /// W for <paramref name="targetZ0"/> on a line whose Z₀ at a width is <paramref name="z0Of"/>. A Z₀
    /// no width in the span reaches is reported on <paramref name="reporter"/> (and the nearer bound
    /// returned), never a silently clamped answer — a caller that must not write a wrong width checks
    /// the reporter.
    /// </summary>
    public static double SynthesizeWidth(Func<double, double> z0Of, double targetZ0, double hMeters,
        string modelName, MicrostripValidityReporter reporter, int iterations = 80)
    {
        double lo = Math.Log(MinWOverH * hMeters), hi = Math.Log(MaxWOverH * hMeters);
        double zLo = z0Of(Math.Exp(lo)), zHi = z0Of(Math.Exp(hi));
        if (targetZ0 > zLo || targetZ0 < zHi)
            reporter.CheckRange($"{modelName} (synthesis)", "targetZ0", targetZ0, zHi, zLo, "ohm");
        for (int i = 0; i < iterations; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (z0Of(Math.Exp(mid)) > targetZ0) lo = mid; else hi = mid;
        }
        return Math.Exp(0.5 * (lo + hi));
    }

    /// <summary>The CPWG width for a static Z₀, the gap held.</summary>
    public static double CpwgWidth(double targetZ0, double g, double h, double t, double er, MicrostripValidityReporter reporter)
    {
        var quiet = new MicrostripValidityReporter("(CPWG synthesis search, not reported)");
        return SynthesizeWidth(w => GroundedCoplanar.Static(w, g, h, t, er, quiet).Z0, targetZ0, h,
                               GroundedCoplanar.ModelName, reporter);
    }

    /// <summary>The SLIN width for a Z₀ (TEM, so static is every frequency).</summary>
    public static double SlinWidth(double targetZ0, double h1, double h2, double t, double er, MicrostripValidityReporter reporter)
        => SynthesizeWidth(w => Stripline.Z0Air(w, h1, h2, t) / Math.Sqrt(er), targetZ0, Math.Min(h1, h2),
                           Stripline.ModelName, reporter);
}
