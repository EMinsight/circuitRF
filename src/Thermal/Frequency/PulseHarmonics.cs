// brief-em3d-86 R-em3d86-1 — a periodic pulse train from the EXACT Z_th at the harmonics of 1/Period, not from a Foster fit.
//
//     T(t) − T₀ = Σⱼ P̄ⱼ Zⱼ(0) + 2 Re Σ_{m≥1} Σⱼ Pₘⱼ Zⱼ(m f₀) e^{j2πm f₀ t},   f₀ = 1/Period,
//     Pₘⱼ = Pⱼ sin(πmD)/(πm) · e^{−jπmD}                    (a rectangular pulse of height Pⱼ and duty D),
//
// summed over every source j that drives the place. A transfer Z_th (heat that has to travel) has a negative real part at
// high frequency, which no Foster network can represent, so a fit of one is no basis for a pulse (brief 80's mutual fits missed
// by 99.8 % of their DC value). The sum above needs no fit.
//
// WHY THE SELF TERM KEEPS ITS FIT. The plain sum converges like N^(−½) on a distributed structure (testdata/thermal/pulse,
// Z2c): 0.06 % at the peak needs ~10⁶ harmonics. A source's OWN Z_th is a driving-point impedance and its Foster fit is good,
// so the fit carries the whole answer in closed form (PulseTrain) and the harmonics carry only the RESIDUAL Z − Z_fit, which
// is small everywhere in the band and falls fast: Z2c's slab meets 1e-4 in a few tens of harmonics. Above the last harmonic
// the self term is the fit's own (its asymptote); a transfer term, which has no fit to lean on, is zero there — and heat that
// has to travel is exponentially small at high frequency, which is why a transfer sum converges quickly anyway.
//
// THE SINGLE PULSE is the train's first pulse from rest: y(t) = Σⱼ Pⱼ (sⱼ(t) − sⱼ(t − t_on)), sⱼ the step response. The self
// part's is the fit's closed form; the residual's (a transfer's whole) is the inverse transform of the solved samples,
//
//     s(t) = (2/π) ∫₀^∞ Re Z(ω) sin(ωt)/ω dω          (a causal real response),
//
// with Re Z a natural cubic spline in ln f through the samples (the Z_th sweep's and the harmonics'), sampled 40 per decade and
// integrated EXACTLY between those points as a piecewise-linear function of ω — α·[Si(bt) − Si(at)] + β·[cos(at) − cos(bt)]/t.

using System.Numerics;

namespace CircuitRF.Thermal.Frequency;

/// <summary>A pulse's drive at one point: its duty and each source's power during the pulse, W.</summary>
public sealed record PulseLoad(double Duty, IReadOnlyList<double> PowersW);

/// <summary>
/// Z_th of every place per watt in every source, at the samples a pulse is evaluated from: the Z_th sweep (DC first) and the
/// harmonics m/<see cref="Period"/>, m = 1 … <see cref="HarmonicCount"/>, each <c>[place, source]</c>; and each pair's tail —
/// the network that carries it above the last harmonic (a source's own fit), or null for none.
/// </summary>
public sealed class PulseResponse
{
    public PulseResponse(double period, IReadOnlyList<double> sweepHz, IReadOnlyList<Complex[,]> sweep, FosterNetwork?[,] tails)
    {
        ArgumentNullException.ThrowIfNull(sweepHz);
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(tails);
        if (!(period > 0)) throw new ArgumentOutOfRangeException(nameof(period), "the period must be positive");
        if (sweepHz.Count != sweep.Count || sweepHz.Count == 0 || sweepHz[0] != 0)
            throw new ArgumentException("one value per sweep frequency, DC first", nameof(sweep));
        Period = period;
        SweepHz = sweepHz;
        Sweep = sweep;
        Tails = tails;
        Places = tails.GetLength(0);
        Sources = tails.GetLength(1);
    }

    public double Period { get; }
    public IReadOnlyList<double> SweepHz { get; }
    public IReadOnlyList<Complex[,]> Sweep { get; }
    public FosterNetwork?[,] Tails { get; }
    public int Places { get; }
    public int Sources { get; }

    /// <summary>Z at harmonic m is <c>Harmonics[m − 1]</c>.</summary>
    public List<Complex[,]> Harmonics { get; } = [];

    public int HarmonicCount => Harmonics.Count;

    /// <summary>The DC value of a pair (real).</summary>
    public double Dc(int place, int source) => Sweep[0][place, source].Real;

    /// <summary>What the harmonics carry for a pair at harmonic m: Z less its tail's own value there.</summary>
    public Complex Residual(int place, int source, int m)
        => Harmonics[m - 1][place, source] - (Tails[place, source]?.Z(m / Period) ?? Complex.Zero);
}

public static class PulseHarmonics
{
    /// <summary>A harmonic is small enough when its largest possible term is below this fraction of the place's DC term.</summary>
    public const double Tolerance = 1e-4;

    /// <summary>How many consecutive harmonics must be small before the sum stops.</summary>
    public const int ConsecutiveSmall = 3;

    /// <summary>Samples of the step response's integrand per decade of frequency.</summary>
    public const int SplinePerDecade = 40;

    /// <summary>Time samples a peak is first searched on (the harmonic sum's), before a golden-section refinement.</summary>
    public const int PeakGridMin = 2048;

    /// <summary>Time samples a single pulse's peak is first searched on.</summary>
    public const int SingleGrid = 256;

    /// <summary>A rectangular pulse's Fourier coefficient: P sin(πmD)/(πm) e^{−jπmD}.</summary>
    public static Complex Coefficient(double powerW, double duty, int m)
    {
        double a = Math.PI * m * duty;
        return powerW * Math.Sin(a) / (Math.PI * m) * Complex.FromPolarCoordinates(1, -a);
    }

    /// <summary>
    /// True when harmonic <paramref name="m"/> is small at <paramref name="place"/> under every load: its ENVELOPE —
    /// Σⱼ Pⱼ/(πm) · |Zⱼ(m f₀) − tailⱼ(m f₀)|, the sine's zeros ignored so a lucky harmonic cannot stop the sum — below
    /// <see cref="Tolerance"/> of the place's DC term. A place with no DC term (no power) is always small.
    /// </summary>
    public static bool Small(PulseResponse r, IReadOnlyList<PulseLoad> loads, int place, int m)
    {
        foreach (var load in loads)
        {
            double dc = 0, env = 0;
            for (int j = 0; j < r.Sources; j++)
            {
                dc += load.Duty * load.PowersW[j] * r.Dc(place, j);
                env += Math.Abs(load.PowersW[j]) / (Math.PI * m) * r.Residual(place, j, m).Magnitude;
            }
            if (Math.Abs(dc) > 0 && env > Tolerance * Math.Abs(dc)) return false;
        }
        return true;
    }

    /// <summary>The places whose last <see cref="ConsecutiveSmall"/> harmonics are not all <see cref="Small"/>.</summary>
    public static List<int> Unconverged(PulseResponse r, IReadOnlyList<PulseLoad> loads)
    {
        int n = r.HarmonicCount;
        var open = new List<int>();
        for (int o = 0; o < r.Places; o++)
            if (n < ConsecutiveSmall || Enumerable.Range(n - ConsecutiveSmall + 1, ConsecutiveSmall).Any(m => !Small(r, loads, o, m))) open.Add(o);
        return open;
    }

    /// <summary>True when every place's last <see cref="ConsecutiveSmall"/> harmonics are all <see cref="Small"/>.</summary>
    public static bool Converged(PulseResponse r, IReadOnlyList<PulseLoad> loads) => Unconverged(r, loads).Count == 0;

    /// <summary>The average rise: Σⱼ D·Pⱼ·Zⱼ(0).</summary>
    public static double Average(PulseResponse r, int place, PulseLoad load)
    {
        double s = 0;
        for (int j = 0; j < r.Sources; j++) s += load.Duty * load.PowersW[j] * r.Dc(place, j);
        return s;
    }

    /// <summary>The periodic steady-state rise at each of <paramref name="times"/> (seconds from the pulse's start).</summary>
    public static double[] Waveform(PulseResponse r, int place, PulseLoad load, IReadOnlyList<double> times)
    {
        var e = new Evaluator(r, place, load);
        return [.. times.Select(e.At)];
    }

    /// <summary>The periodic steady-state peak rise and when in the period it falls.</summary>
    public static (double Rise, double AtS) Peak(PulseResponse r, int place, PulseLoad load)
    {
        var e = new Evaluator(r, place, load);
        int n = Math.Max(PeakGridMin, 16 * r.HarmonicCount);
        return Maximum(e.At, r.Period, load.Duty * r.Period, n);
    }

    /// <summary>The single pulse from rest: its highest rise before the train's second pulse starts, and when.</summary>
    public static (double Rise, double AtS) Single(PulseResponse r, int place, PulseLoad load)
    {
        double tOn = load.Duty * r.Period;
        var step = new StepResponse(r, place, load);
        double Y(double t) => step.At(t) - (t > tOn ? step.At(t - tOn) : 0);
        return Maximum(Y, r.Period, tOn, SingleGrid);
    }

    /// <summary>The largest value of <paramref name="f"/> over [0, period]: a uniform grid plus <paramref name="tOn"/>, then a
    /// golden-section search either side of the best point.</summary>
    private static (double, double) Maximum(Func<double, double> f, double period, double tOn, int n)
    {
        double bestT = tOn, best = f(tOn);
        int bestK = -1;
        for (int k = 0; k <= n; k++)
        {
            double t = period * k / n, v = f(t);
            if (v > best) { best = v; bestT = t; bestK = k; }
        }
        if (bestK < 0) return (best, bestT);    // the end of the pulse: a kink the grid holds exactly
        double lo = period * Math.Max(0, bestK - 1) / n, hi = period * Math.Min(n, bestK + 1) / n;
        const double g = 0.6180339887498949;
        double a = hi - g * (hi - lo), b = lo + g * (hi - lo), fa = f(a), fb = f(b);
        for (int it = 0; it < 60 && hi - lo > 1e-12 * period; it++)
        {
            if (fa > fb) { hi = b; b = a; fb = fa; a = hi - g * (hi - lo); fa = f(a); }
            else { lo = a; a = b; fa = fb; b = lo + g * (hi - lo); fb = f(b); }
        }
        foreach (var (t, v) in new[] { (a, fa), (b, fb) }) if (v > best) { best = v; bestT = t; }
        return (best, bestT);
    }

    /// <summary>One place's periodic waveform: the tails' closed form, the DC term, and the harmonics' residual sum.</summary>
    private sealed class Evaluator
    {
        private readonly double _period, _tOn, _dc;
        private readonly List<(double Pr, double Tau, double A, double B)> _stages = [];   // each tail stage's P·R, τ, minimum, peak
        private readonly Complex[] _c;          // Σⱼ Pₘⱼ (Zⱼ − tailⱼ) at harmonic m, index m − 1

        public Evaluator(PulseResponse r, int place, PulseLoad load)
        {
            _period = r.Period;
            _tOn = load.Duty * r.Period;
            _dc = Average(r, place, load);
            for (int j = 0; j < r.Sources; j++)
                if (r.Tails[place, j] is { } tail)
                    foreach (var term in tail.Terms)
                    {
                        // PulseTrain's per-stage periodic state, less the stage's own mean (the DC term is Z(0), not ΣR)
                        double pr = load.PowersW[j] * term.R;
                        double b = pr * PulseTrain.OneMinusExpNeg(_tOn / term.Tau) / PulseTrain.OneMinusExpNeg(_period / term.Tau);
                        _stages.Add((pr, term.Tau, b * Math.Exp(-(_period - _tOn) / term.Tau), b));
                        _dc -= load.Duty * pr;
                    }
            _c = new Complex[r.HarmonicCount];
            for (int m = 1; m <= _c.Length; m++)
                for (int j = 0; j < r.Sources; j++)
                    _c[m - 1] += Coefficient(load.PowersW[j], load.Duty, m) * r.Residual(place, j, m);
        }

        public double At(double t)
        {
            double y = _dc;
            foreach (var (pr, tau, a, b) in _stages)
                y += t <= _tOn ? pr + (a - pr) * Math.Exp(-t / tau) : b * Math.Exp(-(t - _tOn) / tau);
            var step = Complex.FromPolarCoordinates(1, 2 * Math.PI * t / _period);
            var e = step;
            double s = 0;
            for (int m = 0; m < _c.Length; m++)
            {
                s += _c[m].Real * e.Real - _c[m].Imaginary * e.Imaginary;
                e *= step;
            }
            return y + 2 * s;
        }
    }

    /// <summary>One place's step response to its sources' pulse powers: Σⱼ Pⱼ sⱼ(t).</summary>
    private sealed class StepResponse
    {
        private readonly List<(double P, FosterNetwork Tail)> _tails = [];
        private readonly double[] _w, _alpha, _beta;      // segments [w_i, w_i+1], g = α + βω on each

        public StepResponse(PulseResponse r, int place, PulseLoad load)
        {
            for (int j = 0; j < r.Sources; j++) if (r.Tails[place, j] is { } tail) _tails.Add((load.PowersW[j], tail));
            // the residual's real part at every sample, sweep and harmonics together, one value per frequency
            var samples = new SortedDictionary<double, double>();
            double G(Complex[,] z, double f)
            {
                double g = 0;
                for (int j = 0; j < r.Sources; j++)
                    g += load.PowersW[j] * (z[place, j] - (r.Tails[place, j]?.Z(f) ?? Complex.Zero)).Real;
                return g;
            }
            for (int k = 0; k < r.SweepHz.Count; k++) samples[r.SweepHz[k]] = G(r.Sweep[k], r.SweepHz[k]);
            // a harmonic on a sweep frequency (1 kHz against 10^(12/4) Hz, apart by rounding) is one sample: two would give the
            // spline an interval of no width
            var sweepKeys = samples.Keys.ToArray();
            for (int m = 1; m <= r.HarmonicCount; m++)
            {
                double f = m / r.Period;
                int at = Array.BinarySearch(sweepKeys, f);
                if (at < 0) at = ~at;
                bool near = (at < sweepKeys.Length && Math.Abs(sweepKeys[at] - f) <= 1e-9 * f) || (at > 0 && Math.Abs(sweepKeys[at - 1] - f) <= 1e-9 * f);
                if (!near) samples[f] = G(r.Harmonics[m - 1], f);
            }
            double g0 = samples[0];
            var pos = samples.Where(kv => kv.Key > 0).ToList();
            var w = new List<double> { 0 };
            var g = new List<double> { g0 };
            if (pos.Count == 1) { w.Add(2 * Math.PI * pos[0].Key); g.Add(pos[0].Value); }
            else if (pos.Count > 1)
            {
                var spline = new NaturalSpline([.. pos.Select(kv => Math.Log(kv.Key))], [.. pos.Select(kv => kv.Value)]);
                double du = Math.Log(10) / SplinePerDecade;
                for (int i = 0; i < pos.Count; i++)
                {
                    if (i > 0)
                    {
                        double u0 = Math.Log(pos[i - 1].Key), u1 = Math.Log(pos[i].Key);
                        int sub = Math.Max(1, (int)Math.Ceiling((u1 - u0) / du));
                        for (int s = 1; s < sub; s++)
                        {
                            double u = u0 + (u1 - u0) * s / sub;
                            w.Add(2 * Math.PI * Math.Exp(u));
                            g.Add(spline.At(u));
                        }
                    }
                    w.Add(2 * Math.PI * pos[i].Key);
                    g.Add(pos[i].Value);
                }
            }
            // above the last sample the residual (a transfer's whole Z) goes to nothing, linearly, by twice that frequency
            if (w.Count > 1) { w.Add(2 * w[^1]); g.Add(0); }
            int n = w.Count - 1;
            _w = [.. w];
            _alpha = new double[n];
            _beta = new double[n];
            for (int i = 0; i < n; i++)
            {
                _beta[i] = (g[i + 1] - g[i]) / (w[i + 1] - w[i]);
                _alpha[i] = g[i] - _beta[i] * w[i];
            }
        }

        public double At(double t)
        {
            if (!(t > 0)) return 0;
            double y = 0;
            foreach (var (p, tail) in _tails)
                foreach (var term in tail.Terms) y += p * term.R * PulseTrain.OneMinusExpNeg(t / term.Tau);
            double s = 0, siA = SineIntegral.Si(_w[0] * t), cosA = Math.Cos(_w[0] * t);
            for (int i = 0; i < _alpha.Length; i++)
            {
                double siB = SineIntegral.Si(_w[i + 1] * t), cosB = Math.Cos(_w[i + 1] * t);
                s += _alpha[i] * (siB - siA) + _beta[i] * (cosA - cosB) / t;
                siA = siB; cosA = cosB;
            }
            return y + 2 / Math.PI * s;
        }
    }

    /// <summary>A natural cubic spline through (x, y), x increasing.</summary>
    private sealed class NaturalSpline
    {
        private readonly double[] _x, _y, _m;     // _m: second derivatives

        public NaturalSpline(double[] x, double[] y)
        {
            _x = x; _y = y;
            int n = x.Length;
            _m = new double[n];
            if (n < 3) return;
            var c = new double[n];
            var d = new double[n];
            for (int i = 1; i < n - 1; i++)
            {
                double h0 = x[i] - x[i - 1], h1 = x[i + 1] - x[i];
                double a = h0 / 6, b = (h0 + h1) / 3, cc = h1 / 6;
                double rhs = (y[i + 1] - y[i]) / h1 - (y[i] - y[i - 1]) / h0;
                double den = b - a * c[i - 1];
                c[i] = cc / den;
                d[i] = (rhs - a * d[i - 1]) / den;
            }
            for (int i = n - 2; i >= 1; i--) _m[i] = d[i] - c[i] * _m[i + 1];
        }

        public double At(double u)
        {
            int i = Array.BinarySearch(_x, u);
            if (i >= 0) return _y[i];
            i = Math.Clamp(~i - 1, 0, _x.Length - 2);
            double h = _x[i + 1] - _x[i], a = (_x[i + 1] - u) / h, b = (u - _x[i]) / h;
            return a * _y[i] + b * _y[i + 1] + ((a * a * a - a) * _m[i] + (b * b * b - b) * _m[i + 1]) * h * h / 6;
        }
    }
}

/// <summary>The sine integral Si(x) = ∫₀ˣ sin(t)/t dt.</summary>
public static class SineIntegral
{
    public static double Si(double x)
    {
        if (x < 0) return -Si(-x);
        if (x <= 4)
        {
            // Σ (−1)ⁿ x^(2n+1) / ((2n+1)·(2n+1)!)
            double term = x, sum = x, x2 = x * x;
            for (int n = 1; n < 40; n++)
            {
                term *= -x2 / ((2 * n) * (2 * n + 1));
                double add = term / (2 * n + 1);
                sum += add;
                if (Math.Abs(add) < 1e-17 * Math.Abs(sum)) break;
            }
            return sum;
        }
        // Si(x) = π/2 + Im E₁(ix); E₁(z) = e^{−z} / (z + 1 − 1²/(z + 3 − 2²/(z + 5 − …))), by modified Lentz
        var z = new Complex(0, x);
        var b = z + 1;
        Complex tiny = 1e-300, c = 1 / tiny, d = 1 / b, f = d;
        for (int i = 1; i < 1000; i++)
        {
            double an = -(double)i * i;
            b += 2;
            d = an * d + b;
            if (d == Complex.Zero) d = tiny;
            c = b + an / c;
            if (c == Complex.Zero) c = tiny;
            d = 1 / d;
            var delta = c * d;
            f *= delta;
            if ((delta - 1).Magnitude < 1e-16) break;
        }
        var e1 = f * Complex.Exp(-z);
        return Math.PI / 2 + e1.Imaginary;
    }
}
