// brief-em3d-86 R-em3d86-1 — the pulse train from the exact Z_th at the harmonics of 1/Period: against the slab's closed form
// (testdata/thermal/pulse Z2c, the silicon slab of zth-slab/), and on a transfer impedance, where the old Foster path was wrong,
// against a brute-force sum over 10⁴ solved harmonics.

using System.Numerics;
using System.Text.Json;
using CircuitRF.Thermal.Frequency;
using Xunit.Abstractions;

namespace CircuitRF.Thermal.Tests;

public sealed class PulseHarmonicsTests(ITestOutputHelper output)
{
    private const double K = 148, Rho = 2329, Cp = 705;          // silicon, as zth-slab/ and Z2c state it

    private static List<double> Band(double start, double stop, int perDecade)
    {
        int steps = (int)Math.Round(Math.Log10(stop / start) * perDecade);
        return [0, .. Enumerable.Range(0, steps + 1).Select(i => start * Math.Pow(stop / start, (double)i / steps))];
    }

    [Fact]
    public void Slab_TheHarmonicSumMeetsTheClosedFormPeak_AndSingle_To1e4()
    {
        var c = JsonDocument.Parse(File.ReadAllText(TestMeshes.Testdata("pulse", "pulse.json"))).RootElement.GetProperty("cases").GetProperty("Z2c");
        var slab = c.GetProperty("slab");
        double l = slab.GetProperty("thickness_m").GetDouble(), area = slab.GetProperty("heated_area_m2").GetDouble();
        double p = c.GetProperty("P_W").GetDouble(), period = c.GetProperty("period_s").GetDouble(), tOn = c.GetProperty("t_on_s").GetDouble();
        double reference = c.GetProperty("dT_peak_closed_form_K").GetDouble();
        Complex Z(double f)
        {
            if (f == 0) return l / (K * area);
            var g = Complex.Sqrt(new Complex(0, 2 * Math.PI * f * Rho * Cp / K));
            return Complex.Tanh(g * l) / (K * g) / area;
        }

        // the run's shape: a sweep with its fit (the example's band), then harmonics until the sum has converged
        var sweepHz = Band(1, 1e6, 5);
        var fit = FosterFit.Fit(sweepHz, [.. sweepHz.Select(Z)]);
        var r = new PulseResponse(period, sweepHz, [.. sweepHz.Select(f => new Complex[,] { { Z(f) } })], new FosterNetwork?[,] { { fit } });
        var load = new PulseLoad(tOn / period, [p]);
        for (int m = 1; m <= 1000 && !PulseHarmonics.Converged(r, [load]); m++) r.Harmonics.Add(new Complex[,] { { Z(m / period) } });
        var (peak, at) = PulseHarmonics.Peak(r, 0, load);

        // the single pulse from rest, as the modal sum: λₙ = (n + ½)π/L, Rₙ = 2/(kLλₙ²A), τₙ = ρc/(kλₙ²), with Z2c's tail
        double exactSingle = c.GetProperty("foster_tail_added_K").GetDouble();
        for (int n = 0; n < 200_000; n++)
        {
            double lam = (n + 0.5) * Math.PI / l;
            exactSingle += p * 2 / (K * l * lam * lam * area) * PulseTrain.OneMinusExpNeg(tOn / (Rho * Cp / (K * lam * lam)));
        }
        var (single, singleAt) = PulseHarmonics.Single(r, 0, load);
        double fitOnly = PulseTrain.Peak([new PulseDrive(p, fit)], period, tOn);
        output.WriteLine($"{r.HarmonicCount} harmonics; peak {peak:R} K at {at:G4} s against {reference:R} ({peak / reference - 1:G3}); " +
                         $"single {single:R} K at {singleAt:G4} s against {exactSingle:R} ({single / exactSingle - 1:G3}); the fit alone {fitOnly / reference - 1:G3}");
        Assert.True(PulseHarmonics.Converged(r, [load]));
        Assert.True(Math.Abs(peak / reference - 1) <= 1e-4, $"peak {peak} against {reference}");
        Assert.True(Math.Abs(single / exactSingle - 1) <= 1e-4, $"single {single} against {exactSingle}");
        Assert.Equal(tOn, at, 1e-9);
        Assert.Equal(p * tOn / period * l / (K * area), PulseHarmonics.Average(r, 0, load), 1e-12);

        // the sine integral the single pulse is built on (Abramowitz & Stegun table 5.1)
        Assert.Equal(0.946083070367183, SineIntegral.Si(1), 1e-14);
        Assert.Equal(1.549931244944674, SineIntegral.Si(5), 1e-14);
        Assert.Equal(1.658347594218874, SineIntegral.Si(10), 1e-14);
    }

    /// <summary>
    /// A place heated by itself and by a second source whose heat arrives through 120 µm of SiC — half a diffusion length at
    /// 1/Period, the shape of *Eight Fingers*' neighbours. The slab's modal sum is exact for both terms (the transfer's Rₙ carry
    /// cos λₙx, some negative): the periodic peak and the single pulse from rest meet it, and the single is below the peak,
    /// as it must be (the periodic state is the single pulse plus what earlier pulses left).
    /// </summary>
    [Fact]
    public void SelfAndTransfer_PeakAndSinglePulse_MeetTheModalSum()
    {
        const double k = 370, rho = 3210, cp = 690, l = 500e-6, area = 1e-8, x = 120e-6, period = 1e-3, duty = 0.1, p = 0.125;
        double tOn = duty * period;
        Complex Z(double f, double depth)
        {
            if (f == 0) return (l - depth) / (k * area);
            var g = Complex.Sqrt(new Complex(0, 2 * Math.PI * f * rho * cp / k));
            return Complex.Sinh(g * (l - depth)) / (k * g * Complex.Cosh(g * l)) / area;
        }
        double Modal(double depth, Func<double, double, double> term)
        {
            double s = 0;
            for (int n = 0; n < 200_000; n++)
            {
                double lam = (n + 0.5) * Math.PI / l;
                s += term(2 * Math.Cos(lam * depth) / (k * l * lam * lam * area), rho * cp / (k * lam * lam));
            }
            return s;
        }
        double Periodic(double t, double depth) => Modal(depth, (r, tau) =>
        {
            double b = r * PulseTrain.OneMinusExpNeg(tOn / tau) / PulseTrain.OneMinusExpNeg(period / tau), a = b * Math.Exp(-(period - tOn) / tau);
            return t <= tOn ? r + (a - r) * Math.Exp(-t / tau) : b * Math.Exp(-(t - tOn) / tau);
        });
        double Step(double t, double depth) => t <= 0 ? 0 : Modal(depth, (r, tau) => r * PulseTrain.OneMinusExpNeg(t / tau));

        var sweepHz = Band(1, 1e6, 5);
        var fit = FosterFit.Fit(sweepHz, [.. sweepHz.Select(f => Z(f, 0))]);
        var r = new PulseResponse(period, sweepHz, [.. sweepHz.Select(f => new Complex[,] { { Z(f, 0), Z(f, x) } })], new FosterNetwork?[,] { { fit, null } });
        var load = new PulseLoad(duty, [p, p]);
        for (int m = 1; m <= 1000 && !PulseHarmonics.Converged(r, [load]); m++) r.Harmonics.Add(new Complex[,] { { Z(m / period, 0), Z(m / period, x) } });
        var (peak, _) = PulseHarmonics.Peak(r, 0, load);
        var (single, _) = PulseHarmonics.Single(r, 0, load);
        double exactPeak = double.MinValue, exactSingle = double.MinValue;
        for (int i = 0; i <= 100; i++)
        {
            double t = period * i / 100;
            exactPeak = Math.Max(exactPeak, p * (Periodic(t, 0) + Periodic(t, x)));
            exactSingle = Math.Max(exactSingle, p * (Step(t, 0) - Step(t - tOn, 0) + Step(t, x) - Step(t - tOn, x)));
        }
        output.WriteLine($"{r.HarmonicCount} harmonics: peak {peak:G8} K against {exactPeak:G8}; single {single:G8} K against {exactSingle:G8}");
        Assert.True(Math.Abs(peak / exactPeak - 1) <= 1e-3, $"peak {peak} against {exactPeak}");
        Assert.True(Math.Abs(single / exactSingle - 1) <= 1e-3, $"single {single} against {exactSingle}");
        Assert.True(single < peak);
    }

    /// <summary>A silicon column, heated at x = 0 and held at x = L, with a probe strip on its side at <paramref name="depth"/>:
    /// the strip is the second "source", so Z[1, 0] is the transfer impedance from the heated face to it.</summary>
    private static ThermalSmallSignal Column(double length, double depth, double cell)
    {
        const int strip = 20;
        var mesh = TestMeshes.Box(TestMeshes.Linspace(0, length, (int)Math.Round(length / cell)), [0, cell], [0, cell]);
        mesh = MeshEdits.Retag(mesh, TestMeshes.YMin, strip, (x, _, _) => x > depth && x < depth + cell);
        var problem = new ThermalProblem
        {
            Mesh = mesh, Conductivity = [ThermalConductivity.Constant(K)], Fixed = [new FixedTemperature(TestMeshes.XMax, 25)],
        };
        return new ThermalSmallSignal(problem, [new SmallSignalSource("heated", [TestMeshes.XMin], []), new SmallSignalSource("probe", [strip], [])],
                                      null, kOfT: false);
    }

    /// <summary>~13 s: the 10⁴ solves the brute force needs, at ~1.3 ms each on the smallest mesh that holds the case.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Transfer_ThreeDiffusionLengthsApart_MeetsTenThousandSolvedHarmonics_WhereTheFosterFitDidNot()
    {
        const double period = 1e-3, duty = 0.1, p = 1;
        double delta = Math.Sqrt(K / (Rho * Cp) / (Math.PI / period));          // the diffusion length at 1/Period
        var system = Column(2e-3, 3 * delta, 50e-6);
        var options = new ThermalSolveOptions { Solver = ThermalSolverKind.Direct };
        double[] rhoC = [Rho * Cp];
        var sweepHz = Band(1, 1e6, 5);
        var sweep = system.Zth(rhoC, sweepHz, options);
        Complex[,] Pick(Complex[,] z) => new Complex[,] { { z[0, 0] }, { z[1, 0] } };   // both places, per watt in the heated face
        var transfer = sweep.Z.Select(z => z[1, 0]).ToList();
        var fit = FosterFit.Fit(sweepHz, [.. sweep.Z.Select(z => z[0, 0])]);
        var tails = new FosterNetwork?[,] { { fit }, { null } };
        var load = new PulseLoad(duty, [p]);

        var r = new PulseResponse(period, sweepHz, [.. sweep.Z.Select(Pick)], tails);
        system.Zth(rhoC, [.. Enumerable.Range(1, 10_000).Select(m => m / period)], options, stopAfter: (_, z) =>
        {
            r.Harmonics.Add(Pick(z));
            return PulseHarmonics.Converged(r, [load]);
        });
        var brute = new PulseResponse(period, sweepHz, [.. sweep.Z.Select(Pick)], tails);
        brute.Harmonics.AddRange(system.Zth(rhoC, [.. Enumerable.Range(1, 10_000).Select(m => m / period)], options).Z.Select(Pick));

        var (mine, at) = PulseHarmonics.Peak(r, 1, load);
        var (reference, refAt) = PulseHarmonics.Peak(brute, 1, load);
        // the old path: the transfer's own Foster fit, through the closed form, its peak searched over the period
        var old = PulseTrain.Waveform([new PulseDrive(p, FosterFit.Fit(sweepHz, transfer, 1))], period, duty * period,
                                      [.. Enumerable.Range(0, 4001).Select(k => period * k / 4000)]).Max();
        // the ripple about the average is what a pulse adds, and what a fit can get wrong: the DC term is exact either way
        double avg = PulseHarmonics.Average(brute, 1, load);
        output.WriteLine($"δ {delta * 1e6:G4} µm; Re Z_transfer at least {transfer.Min(z => z.Real):G4} K/W (DC {transfer[0].Real:G4}); " +
                         $"{r.HarmonicCount} harmonics: peak {mine:R} K at {at:G4} s; 10⁴ harmonics: {reference:R} K at {refAt:G4} s; " +
                         $"ripple {mine - avg:G6} against {reference - avg:G6} K ({(mine - avg) / (reference - avg) - 1:G3}); " +
                         $"the Foster fit's ripple {old - avg:G6} K ({(old - avg) / (reference - avg) - 1:G3})");
        Assert.Contains(transfer, z => z.Real < 0);                            // a delay: no Foster network can represent it
        Assert.True(PulseHarmonics.Converged(r, [load]) && r.HarmonicCount < 1000, $"{r.HarmonicCount} harmonics");
        Assert.True(Math.Abs(mine / reference - 1) <= 1e-4, $"peak {mine} against {reference}");
        Assert.True(Math.Abs((mine - avg) / (reference - avg) - 1) <= 1e-3, $"ripple {mine - avg} against {reference - avg}");
        Assert.True(at > duty * period, "heat that has to travel peaks after the pulse ends");
        Assert.True(Math.Abs((old - avg) / (reference - avg) - 1) > 0.1, "the Foster path's ripple is close; the case no longer shows why it was replaced");
    }
}
