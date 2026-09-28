// brief-em3d-80 R-em3d80-4 — a rectangular pulse train through Foster networks, in closed form. No transient solver (PRD):
// each stage R ∥ C driven by P for t_on of every period T rises and decays exponentially, so its periodic steady state is
//
//     b = P·R·(1 − e^{−t_on/τ}) / (1 − e^{−T/τ})          (the end of the pulse: the peak)
//     a = b·e^{−(T − t_on)/τ}                              (the start of the pulse: the minimum)
//     x(t) = P·R + (a − P·R)·e^{−t/τ}   for 0 ≤ t ≤ t_on,   b·e^{−(t − t_on)/τ} after.
//
// Every stage peaks at t_on, so the network's peak is the sum of the stages' — and several sources driven by the same
// waveform add the same way. brief 72 Z2 found the Fourier route converging like 1/N (lumped) and N^−½ (distributed), which
// is why nothing here sums harmonics.

namespace CircuitRF.Thermal.Frequency;

/// <summary>A source's contribution to one probe: its power during the pulse, W, and the probe's Foster network per watt in it.</summary>
public readonly record struct PulseDrive(double PeakPowerW, FosterNetwork Network);

public static class PulseTrain
{
    /// <summary>The periodic steady-state peak rise: Σⱼ Pⱼ Σᵢ Rᵢⱼ(1 − e^{−t_on/τᵢⱼ}) / (1 − e^{−T/τᵢⱼ}).</summary>
    public static double Peak(IReadOnlyList<PulseDrive> drives, double period, double tOn)
    {
        Check(period, tOn);
        double s = 0;
        foreach (var d in drives)
            foreach (var t in d.Network.Terms) s += d.PeakPowerW * t.R * OneMinusExpNeg(tOn / t.Tau) / OneMinusExpNeg(period / t.Tau);
        return s;
    }

    /// <summary>The rise at the end of ONE pulse from rest: Σⱼ Pⱼ Σᵢ Rᵢⱼ(1 − e^{−t_on/τᵢⱼ}).</summary>
    public static double Single(IReadOnlyList<PulseDrive> drives, double tOn)
    {
        Check(double.PositiveInfinity, tOn);
        double s = 0;
        foreach (var d in drives)
            foreach (var t in d.Network.Terms) s += d.PeakPowerW * t.R * OneMinusExpNeg(tOn / t.Tau);
        return s;
    }

    /// <summary>The average rise: duty × Σⱼ Pⱼ R_th,j.</summary>
    public static double Average(IReadOnlyList<PulseDrive> drives, double period, double tOn)
    {
        Check(period, tOn);
        return tOn / period * drives.Sum(d => d.PeakPowerW * d.Network.Rth);
    }

    /// <summary>The periodic steady-state rise at each of <paramref name="times"/> (seconds from the pulse's start, within one
    /// period).</summary>
    public static double[] Waveform(IReadOnlyList<PulseDrive> drives, double period, double tOn, IReadOnlyList<double> times)
    {
        Check(period, tOn);
        var y = new double[times.Count];
        foreach (var d in drives)
            foreach (var term in d.Network.Terms)
            {
                double pr = d.PeakPowerW * term.R, tau = term.Tau;
                double b = pr * OneMinusExpNeg(tOn / tau) / OneMinusExpNeg(period / tau);
                double a = b * Math.Exp(-(period - tOn) / tau);
                for (int k = 0; k < times.Count; k++)
                {
                    double t = times[k];
                    y[k] += t <= tOn ? pr + (a - pr) * Math.Exp(-t / tau) : b * Math.Exp(-(t - tOn) / tau);
                }
            }
        return y;
    }

    /// <summary>1 − e^{−x}, accurate for small x.</summary>
    public static double OneMinusExpNeg(double x)
    {
        if (double.IsPositiveInfinity(x)) return 1;
        if (Math.Abs(x) < 1e-4) return x * (1 - x / 2 * (1 - x / 3 * (1 - x / 4)));
        return -(Math.Exp(-x) - 1);
    }

    private static void Check(double period, double tOn)
    {
        if (!(period > 0)) throw new ArgumentOutOfRangeException(nameof(period), "the period must be positive");
        if (!(tOn >= 0) || tOn > period) throw new ArgumentOutOfRangeException(nameof(tOn), "the on-time must lie within the period");
    }
}
