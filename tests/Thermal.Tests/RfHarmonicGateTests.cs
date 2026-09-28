// brief-em3d-78 §5 — RF heat in a wire, at the solver: gate 1 (W3, against brief 72's externally generated reference), gate 5
// (harmonics heat as the sum of each alone) and gate 6 (the analytic dq′_RF/dT, and the assembled Jacobian it lands in). The
// design-side gates (2–4, 7, 8: the share, Peak/RMS, the arrays) are tests/Ui.Tests/ThreeD/ThermalRfCurrentsTests.cs.

using System.Globalization;
using System.Text.Json;
using CircuitRF.Thermal.Electrothermal;
using CircuitRF.Thermal.Nonlinear;
using CircuitRF.WBond;
using Xunit.Abstractions;

namespace CircuitRF.Thermal.Tests;

public sealed class RfHarmonicGateTests(ITestOutputHelper output)
{
    private static readonly ThermalSolveOptions Direct = new() { Solver = ThermalSolverKind.Direct };
    private const double D = 2.54e-5, L = 1e-3;

    /// <summary>The W1 bench (1 mm, 25 °C ends) with no DC current, its wire carrying <paramref name="h"/>.</summary>
    private static ElectrothermalProblem Bench(ElectricalConductivity sigma, double k, params WireHarmonic[] h)
    {
        var p = ElectrothermalGateTests.Bench(D, L, 25, 25, 0, ThermalConductivity.Constant(k), sigma);
        var w = p.Wires[0];
        var rf = new ThermalWire
        {
            Name = w.Name, Points = w.Points, S = w.S, Area = w.Area, Diameter = w.Diameter, K = w.K, Sigma = w.Sigma, Contacts = w.Contacts,
            OnPad = w.OnPad, Harmonics = h, AcResistance = (f, s) => InternalImpedance.ResistanceWithSigmaSlope(f, D / 2, s),
        };
        return new ElectrothermalProblem { Thermal = p.Thermal, Wires = [rf], Sigma = p.Sigma, Currents = [] };
    }

    private static JsonElement Gold => JsonDocument.Parse(File.ReadAllText(TestMeshes.Testdata("wire-rf", "wire-rf.json"))).RootElement
                                                   .GetProperty("metals").GetProperty("gold");

    private static ElectricalConductivity GoldSigma(out double k)
    {
        var p = Gold.GetProperty("parameters");
        k = p.GetProperty("k_W_mK").GetDouble();
        return ElectricalConductivity.LinearResistivity(p.GetProperty("rho0_Ohm_m").GetDouble(), p.GetProperty("alpha_per_K").GetDouble());
    }

    // ── gate 1: W3 ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// W3, gold 1 mil, 25 °C ends: T along the span to 1e-5 of the rise for one harmonic at 1 GHz and for 2, 4 and 6 GHz together
    /// (both entered as the reference's peak phasors); the RF heat per length at the solved temperatures equals ½|I|² × wBond's
    /// Bessel R′_ac at σ(T); and that R′_ac matches the reference's own table (SciPy's Bessel functions, independent of circuitRF's)
    /// at every tabulated frequency and temperature.
    /// </summary>
    [Theory]
    [InlineData("1GHz-rms0.25")]
    [InlineData("2GHz-h123")]
    public void Gate1_W3_RfHeat_AlongTheWire(string excitation)
    {
        var sigma = GoldSigma(out double k);
        var profile = Gold.GetProperty("profiles").EnumerateArray().Single(p => p.GetProperty("d_m").GetDouble() == D && p.GetProperty("excitation").GetString() == excitation);
        var h = profile.GetProperty("harmonics").EnumerateArray()
                       .Select((x, i) => new WireHarmonic($"h{i + 1}", x.GetProperty("f_Hz").GetDouble(), x.GetProperty("I_peak_A").GetDouble())).ToArray();
        var p = Bench(sigma, k, h);
        var s = ConductiveBalance.Solve(p, Direct with { KOfT = false });
        Assert.True(s.Converged, s.Failure);

        var rows = File.ReadAllLines(TestMeshes.Testdata("wire-rf", "gold.csv")).Where(l => l.Length > 0 && l[0] != '#').Skip(1)
                       .Select(l => l.Split(',')).Where(c => c[0] == "2.54e-05" && c[1] == excitation)
                       .Select(c => (S: double.Parse(c[2], CultureInfo.InvariantCulture), T: double.Parse(c[3], CultureInfo.InvariantCulture))).ToList();
        Assert.Equal(101, rows.Count);
        var t = s.WireTemperature[0];
        double rise = rows.Max(r => r.T) - 25, worst = 0;
        foreach (var (sr, tr) in rows)
        {
            int node = 2 + (int)Math.Round(sr / L * 100);
            worst = Math.Max(worst, Math.Abs(t[node] - tr));
        }
        output.WriteLine($"W3 gold {excitation}: {s.Iterations} Newton step(s); worst |ΔT| {worst:G3} K on a {rise:G6} K rise; RF heat {s.RfW:G6} W; " +
                         $"balance {s.Thermal.BalanceRelative:G3}; centre {t[52]:F4} °C (reference {profile.GetProperty("T_centre_degC").GetDouble():F4})");
        Assert.True(worst <= 1e-5 * rise, $"worst {worst:G3} K on a {rise:G6} K rise");
        Assert.True(s.Thermal.BalanceRelative <= 1e-6, $"balance {s.Thermal.BalanceRelative:G3}");

        // q′ at the solved temperature is ½|I|²·R′_ac(σ(T)), per harmonic
        var w = p.Wires[0];
        foreach (int node in new[] { 2, 27, 52 })
        {
            double tc = t[node], sg = sigma.At(tc, true).Sigma;
            double expected = h.Sum(x => 0.5 * x.PeakA * x.PeakA * InternalImpedance.PerMetre(x.FrequencyHz, D / 2, sg).ResistancePerMetre);
            Assert.Equal(expected, w.RfHeat(tc, true).Q, 1e-12 * expected);
        }
        Assert.Equal(s.RfW, s.RfByHarmonic[0].Sum(), 1e-12 * s.RfW);

        // wBond's R′_ac against the reference's independent table
        var p0 = Gold.GetProperty("parameters");
        double rho0 = p0.GetProperty("rho0_Ohm_m").GetDouble(), alpha = p0.GetProperty("alpha_per_K").GetDouble();
        double table = 0;
        foreach (var c in File.ReadAllLines(TestMeshes.Testdata("wire-rf", "rac-gold.csv")).Where(l => l.Length > 0 && l[0] != '#').Skip(1).Select(l => l.Split(',')))
        {
            double d = double.Parse(c[0], CultureInfo.InvariantCulture), tc = double.Parse(c[1], CultureInfo.InvariantCulture);
            double f = double.Parse(c[2], CultureInfo.InvariantCulture), r = double.Parse(c[3], CultureInfo.InvariantCulture);
            double ours = InternalImpedance.PerMetre(f, d / 2, 1 / (rho0 * (1 + alpha * (tc - 20)))).ResistancePerMetre;
            table = Math.Max(table, Math.Abs(ours - r) / r);
        }
        output.WriteLine($"R′_ac against the reference table: worst relative {table:G3}");
        Assert.True(table <= 1e-6, $"R′_ac differs from the reference table by {table:G3}");
    }

    // ── gate 5: orthogonality ───────────────────────────────────────────────────────────────────────

    /// <summary>With σ(T) and k(T) off the problem is linear in heat, so harmonics at F0 and 2F0 heat as the sum of each alone:
    /// the temperature rise at every node, and the RF power, to 1e-12.</summary>
    [Fact]
    public void Gate5_Harmonics_HeatAsTheSumOfEachAlone()
    {
        var sigma = GoldSigma(out double k);
        var h1 = new WireHarmonic("h1", 2e9, 0.9);
        var h2 = new WireHarmonic("h2", 4e9, 0.5);
        var o = Direct with { KOfT = false, SigmaOfT = false };
        var both = ConductiveBalance.Solve(Bench(sigma, k, h1, h2), o);
        var one = ConductiveBalance.Solve(Bench(sigma, k, h1), o);
        var two = ConductiveBalance.Solve(Bench(sigma, k, h2), o);
        Assert.True(both.Converged && one.Converged && two.Converged);
        var tb = both.WireTemperature[0];
        double top = tb.Max() - 25, worst = 0;
        for (int i = 0; i < tb.Length; i++) worst = Math.Max(worst, Math.Abs(tb[i] - 25 - (one.WireTemperature[0][i] - 25) - (two.WireTemperature[0][i] - 25)));
        output.WriteLine($"rise {top:G6} K; worst |ΔT₁₂ − ΔT₁ − ΔT₂| {worst:G3} K ({worst / top:G3}); RF {both.RfW:G10} = {one.RfW:G10} + {two.RfW:G10}");
        Assert.True(worst <= 1e-12 * top, $"superposition off by {worst:G3} K on {top:G6} K");
        Assert.Equal(one.RfW + two.RfW, both.RfW, 1e-12 * both.RfW);
    }

    // ── gate 6: the Jacobian ────────────────────────────────────────────────────────────────────────

    /// <summary>dq′_RF/dT, analytic (through dσ/dT and the skin depth), against a central difference of q′(T), at 5 and 50 skin
    /// depths — the continued-fraction and asymptotic regimes of the Bessel ratio — to 1e-6; and the assembled Jacobian's wire rows
    /// against a central difference of the residual, so the slope is in the matrix Newton uses.</summary>
    [Theory]
    [InlineData(5.0)]
    [InlineData(50.0)]
    public void Gate6_TheRfHeatsTemperatureSlope_IsItsDerivative(double skinDepths)
    {
        var sigma = GoldSigma(out double k);
        double s25 = sigma.At(25, true).Sigma, a = D / 2, delta = a / skinDepths;
        double f = 1 / (Math.PI * InternalImpedance.Mu0 * s25 * delta * delta);
        var p = Bench(sigma, k, new WireHarmonic("h1", f, 1.0));
        var w = p.Wires[0];
        Assert.Equal(skinDepths, InternalImpedance.QParameter(f, a, s25), 1e-9);
        foreach (double tc in new[] { 25.0, 150.0, 400.0 })
        {
            const double dt = 1e-3;
            double fd = (w.RfHeat(tc + dt, true).Q - w.RfHeat(tc - dt, true).Q) / (2 * dt);
            double an = w.RfHeat(tc, true).Slope;
            output.WriteLine($"a/δ {skinDepths} at {tc} °C: analytic {an:G12}, central {fd:G12}, relative {Math.Abs(an - fd) / Math.Abs(fd):G3}");
            Assert.True(Math.Abs(an - fd) <= 1e-6 * Math.Abs(fd), $"{an} vs {fd}");
        }
        Assert.Equal(0, w.RfHeat(150, false).Slope);

        // the assembled tangent: with k(T) off the secant does not move with T, so (J − S)·v is exactly −(dL/dx)·v — the RF heat's
        // slope, compared on its own (the contacts' penalty conductances would drown it in J·v) with a central difference of L
        var sys = new ElectrothermalSystem(p);
        var x = new double[sys.Size];
        var rng = new Random(78);
        for (int i = 0; i < sys.TUnknowns; i++) x[i] = 25 + 200 * rng.NextDouble();
        var v = new double[sys.Size];
        for (int i = 0; i < sys.TUnknowns; i++) v[i] = rng.NextDouble() - 0.5;
        const double eps = 1e-4;
        var lp = sys.Assemble(p, [.. x.Select((xi, i) => xi + eps * v[i])], kOfT: false, sigmaOfT: true).Load;
        var lm = sys.Assemble(p, [.. x.Select((xi, i) => xi - eps * v[i])], kOfT: false, sigmaOfT: true).Load;
        var at = sys.Assemble(p, x, false, true);
        var jv = new double[sys.Size];
        var sv = new double[sys.Size];
        sys.Matrix(at.Tangent).Multiply(v, jv);
        sys.Matrix(at.Secant).Multiply(v, sv);
        int o = sys.WireOffset[0];
        double num = 0, den = 0;
        for (int i = o; i < o + w.NodeCount; i++)
        {
            double fdv = -(lp[i] - lm[i]) / (2 * eps);
            num = Math.Max(num, Math.Abs(jv[i] - sv[i] - fdv));
            den = Math.Max(den, Math.Abs(fdv));
        }
        output.WriteLine($"assembled wire rows: |(J − S)·v + ΔL/2ε| {num:G3} of {den:G3}");
        Assert.True(den > 0);
        Assert.True(num <= 1e-6 * den, $"{num:G3} of {den:G3}");
    }
}
