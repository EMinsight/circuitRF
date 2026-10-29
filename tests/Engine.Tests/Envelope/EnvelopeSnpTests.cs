using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// brief-env-snp-rational: an S-parameter block in the circuit envelope, against a reference that never touches
/// the envelope engine. With a 50-ohm source and load on a 50-ohm file the path is linear and reflectionless, so
/// the output envelope is exactly the source's own I/Q record filtered by S21: y = IFFT(X(f) S21(f)) / 2 (the
/// available-power drive into a matched port is half the open-circuit voltage). Channel power and ACPR come from
/// that y through the same Welch / band definitions the engine's DataSet uses.
/// </summary>
public sealed class EnvelopeSnpTests(ITestOutputHelper output) : IDisposable
{
    private const double F0 = 1e9;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-envsnp-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _warnings = [];

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ── the fixtures ─────────────────────────────────────────────────────

    /// <summary>A 5th-order, 0.5 dB-ripple Chebyshev bandpass, ripple band F0 ± 11 MHz: lossless and symmetric, so
    /// S11 = S22 and S12 = S21. Its skirt falls through the NR 20 MHz adjacent channel (10.5-29.5 MHz off).</summary>
    private static (Complex s11, Complex s21) Chebyshev(double hz)
    {
        const int n = 5;
        double eps = Math.Sqrt(Math.Pow(10, 0.05) - 1), a = Math.Asinh(1 / eps) / n;
        double w0 = 2 * Math.PI * F0, bw = 2 * Math.PI * 22e6;
        var s = new Complex(0, 2 * Math.PI * hz);
        var p = (s * s + w0 * w0) / (s * bw);                    // lowpass -> bandpass
        Complex den = 1, den0 = 1, refl = 1;
        for (int k = 1; k <= n; k++)
        {
            double th = (2 * k - 1) * Math.PI / (2 * n);
            var pk = new Complex(-Math.Sinh(a) * Math.Sin(th), Math.Cosh(a) * Math.Cos(th));
            den *= p - pk; den0 *= -pk;
            refl *= p - new Complex(0, Math.Cos(th));             // zeros of T_n
        }
        var s21 = den0.Real / den;
        return (s21 * eps * Math.Pow(2, n - 1) * refl, s21);
    }

    /// <summary>Fixture (b): the same filter followed by an ideal 1 ns line on port 2 — not rational.</summary>
    private static Complex[,] Delayed(double hz)
    {
        var (s11, s21) = Chebyshev(hz);
        var d = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz * 1e-9);
        return new Complex[,] { { s11, s21 * d }, { s21 * d, s11 * d * d } };
    }

    private static Complex[,] Rational(double hz)
    {
        var (s11, s21) = Chebyshev(hz);
        return new Complex[,] { { s11, s21 }, { s21, s11 } };
    }

    /// <summary>A Touchstone v1 file of <paramref name="s"/>, 401 points over 0.8-1.2 GHz (1 MHz pitch).</summary>
    private static string WriteS2p(string dir, string name, Func<double, Complex[,]> s, double lo = 0.8e9, double hi = 1.2e9, int pts = 401)
    {
        Directory.CreateDirectory(dir);
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("# Hz S RI R 50\n");
        for (int i = 0; i < pts; i++)
        {
            double f = lo + (hi - lo) * i / (pts - 1);
            var m = s(f);
            sb.Append(f.ToString("R", inv));
            foreach (var (r, c) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })     // v1 2-port order: S11 S21 S12 S22
                sb.Append(' ').Append(m[r, c].Real.ToString("R", inv)).Append(' ').Append(m[r, c].Imaginary.ToString("R", inv));
            sb.Append('\n');
        }
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private const string Nr20 = "NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1";

    private static string Filter(string file, string carriers, string env) => $"""
        MOD_Source:S1 in 0 Fc=1e9 Pavg=0 Z=50 Record=0.5e-3 {carriers}
        SnP:F1 in out 0 NumPorts=2 File="{file}"
        IProbe:Iout out o
        R:RL o 0 R=50 Ohm
        analysis ENV1 type=env {env}
        """;

    // ── the run and the reference ────────────────────────────────────────

    private static DataSet Run(string cnl, out ElaboratedNetlist first)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var env = tb.Analyses.OfType<EnvelopeAnalysis>().Single();
        var nl = new Elaborator(lib).Elaborate(tb);
        first = nl;
        var plan = EnvelopeRunner.Resolve(env, nl.ResolvedGlobals);
        bool used = false;
        object gate = new();
        ElaboratedNetlist Elab() { lock (gate) { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; } }
        return EnvelopeRunner.Run(plan, Elab);
    }

    /// <summary>The output envelope at the run's step rate over record samples [0, <paramref name="count"/>): carriers
    /// <paramref name="idx"/> of the source, centred at <paramref name="centreHz"/>, filtered by S21. Overlap-save: the
    /// drive is taken from 4 us earlier and that lead is discarded, which is the filter's whole memory (its slowest pole
    /// decays in 130 ns, so the wrapped tail is below e^-30) — the same y the whole periodic record would give.</summary>
    private static Complex[] Reference(ModSourceModel src, double rate, double centreHz, IReadOnlyList<int>? idx, Func<double, Complex> s21, int count)
        => Reference(src, rate, centreHz, idx, f => [.. f.Select(s21)], count);

    /// <inheritdoc cref="Reference(ModSourceModel, double, double, IReadOnlyList{int}?, Func{double, Complex}, int)"/>
    private static Complex[] Reference(ModSourceModel src, double rate, double centreHz, IReadOnlyList<int>? idx, Func<double[], Complex[]> s21, int count)
    {
        int lead = (int)Math.Round(4e-6 * rate), n = lead + count;
        var x = src.Signal.CompositeWindow(rate, centreHz - src.FcHz, -lead, n, idx);
        var X = Fft.Forward(x);
        var h = s21([.. Enumerable.Range(0, n).Select(k => centreHz + (k < n / 2 ? k : k - n) * rate / n)]);
        for (int k = 0; k < n; k++) X[k] *= h[k] * src.VsPeak / 2;
        return Fft.Inverse(X)[lead..];
    }

    /// <summary>The engine's own Welch length for a run of <paramref name="nm"/> measured samples (EnvelopeDataSet).</summary>
    private static int Nseg(DataSet ds, ModSourceModel[] srcs, int nm)
    {
        double narrow = 1.92e6, need = EnvelopeEngineMt.Plan(srcs).Max(g => g.needRate);
        while (narrow < need) narrow *= 2;
        int nseg = 8192 * (int)Math.Max(1, Math.Round(ds["__EnvSignalRate_Hz"].RealValues[0] / narrow));
        while (nseg > nm / 4 && nseg > 256) nseg /= 2;
        return nseg;
    }

    private static double[] Aclr(Complex[] y, double fs, int nseg, CarrierSpec spec)
    {
        var (f, d) = SignalMeasure.Welch(y, fs, nseg);
        var (mb, sp) = SignalMeasure.AclrBands(spec);
        var a = SignalMeasure.Aclr(f, d, fs, 0, mb, sp);
        return [10 * Math.Log10(a.ChannelPower / (2 * 50) * 1e3), a.Acpr1L, a.Acpr1U, a.Acpr2L, a.Acpr2U];
    }

    private static Complex[] Envelope(DataSet ds, string node, int k)
    {
        var v = ds["V"];
        int nt = v.Axes[0].Length, np = v.Axes[1].Length, nk = v.Axes[2].Length;
        int p = Array.IndexOf(v.Axes[1].Labels!, node);
        var cv = v.ComplexValues;
        var y = new Complex[nt];
        for (int t = 0; t < nt; t++) y[t] = cv[(t * np + p) * nk + k];
        return y;
    }

    private static double[] DsAclr(DataSet ds, string node, int carrier)
    {
        var a = ds["ACLR"];
        int p = Array.IndexOf(a.Axes[0].Labels!, node), nc = a.Axes[1].Length;
        return [.. Enumerable.Range(0, 5).Select(m => a.RealValues[(p * nc + carrier) * 5 + m])];
    }

    private static double NmseDb(Complex[] y, Complex[] yRef)
    {
        double e = 0, r = 0;
        for (int t = 0; t < y.Length; t++) { e += (y[t] - yRef[t]).Magnitude * (y[t] - yRef[t]).Magnitude; r += yRef[t].Magnitude * yRef[t].Magnitude; }
        return 10 * Math.Log10(e / r);
    }

    private (double worstDb, double nmseDb, DataSet ds) SingleCarrier(string file, Func<double, Complex[,]> s, string accuracy = "fine", string stop = "100e-6")
    {
        var ds = Run(Filter(file, $"Carrier[1]=\"{Nr20}\"", $"Stop={stop} Accuracy={accuracy} MaxHarm=1 Probes=\"in,out\" Segments=1 Warmup=2e-6"), out var nl);
        var src = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single();
        double fs = 1 / ds["__EnvStep_s"].RealValues[0];
        var y = Envelope(ds, "out", 1);
        var yRef = Reference(src, fs, F0, null, f => s(f)[1, 0], y.Length);
        int nseg = Nseg(ds, [src], y.Length);
        var exact = Aclr(yRef, fs, nseg, src.Signal.Carriers[0].Spec);
        var got = DsAclr(ds, "Iout", 0);
        double worst = 0;
        for (int m = 0; m < 5; m++) worst = Math.Max(worst, Math.Abs(got[m] - exact[m]));
        double nmse = NmseDb(y, yRef);
        output.WriteLine($"exact  Pch {exact[0]:0.0000} dBm  ACPR1 {exact[1]:0.000}/{exact[2]:0.000}  ACPR2 {exact[3]:0.000}/{exact[4]:0.000} dBc");
        output.WriteLine($"engine Pch {got[0]:0.0000} dBm  ACPR1 {got[1]:0.000}/{got[2]:0.000}  ACPR2 {got[3]:0.000}/{got[4]:0.000} dBc");
        output.WriteLine($"worst |d| {worst:0.0000} dB   NMSE {nmse:0.00} dB   __EnvTangentError {ds["__EnvTangentError"].RealValues[0]:G4}");
        foreach (var n in nl.Notes) output.WriteLine("note: " + n);
        foreach (var w in nl.Warnings) output.WriteLine("warning: " + w);
        _warnings.AddRange(nl.Warnings);
        return (worst, nmse, ds);
    }

    // ── 1. fixture (a), single fundamental ───────────────────────────────

    // 16x oversampling: Gear-2's own frequency warping at the channel edge leaves 0.07-0.08 dB on ACPR1 at 8x ("fine")
    // and 0.003 dB at 16x, so the comparison is made where the step is not what is being measured.
    private const string Gate = "16", GateStop = "25e-6";

    [Fact]
    public void RationalFilter_MatchesTheExactReference()
    {
        var (worst, nmse, ds) = SingleCarrier(WriteS2p(_dir, "cheb.s2p", Rational), Rational, Gate, GateStop);
        Assert.True(worst < 0.05, $"channel power / ACPR off the exact reference by {worst:0.000} dB");
        Assert.True(nmse < -40, $"output NMSE {nmse:0.0} dB against y(t)");
        Assert.True(ds["__EnvTangentError"].RealValues[0] < 1e-10, "the realized fit is affine in jw: its tangent is exact");
        Assert.Empty(_warnings);
    }

    // ── 2. fixture (b): not rational ─────────────────────────────────────

    /// <summary>The fit's RMS error e is relative to the RMS of every entry (|S| ≤ 1 for a passive file), so S21's own
    /// absolute error is at most 2e over a 2-port, and in the passband (|S21| ≈ 1) that is the output's relative error.
    /// The bound is fixture (a)'s gate with that added: ±20·log10(1 + 2e) dB on the powers, (2e)² on the NMSE.</summary>
    [Fact]
    public void DelayedFilter_ErrorIsBoundedByTheFitError()
    {
        var (worst, nmse, ds) = SingleCarrier(WriteS2p(_dir, "chebdelay.s2p", Delayed), Delayed, Gate, GateStop);
        double e = ds["__EnvSnpFit"].RealValues[1];
        double dbBound = 0.05 + 20 * Math.Log10(1 + 2 * e), nmseBound = 10 * Math.Log10(1e-4 + 4 * e * e);
        output.WriteLine($"fit error {e:G3}: bounds {dbBound:0.0000} dB, NMSE {nmseBound:0.00} dB");
        Assert.True(e > 1e-8 && e <= EnvelopeSnpFit.Tolerance, $"a delay is not rational: the fit should work for its {e:G3}");
        Assert.True(worst < dbBound, $"channel power / ACPR off the exact reference by {worst:0.000} dB");
        Assert.True(nmse < nmseBound, $"output NMSE {nmse:0.0} dB against y(t)");
    }

    // ── 3. the start point and the stepping are one model ─────────────────

    [Fact]
    public void CwThroughAFileThatStopsBelowTheHarmonics_HoldsItsStartPoint()
    {
        string file = WriteS2p(_dir, "cheb.s2p", Rational);       // 0.8-1.2 GHz; 2 and 3 GHz are outside it
        var ds = Run($"""
            MOD_Source:S1 in 0 Fc=1e9 Pavg=10 Z=50 Record=0.5e-3 Carrier[1]="CW;Off=0;P=0"
            SnP:F1 in out 0 NumPorts=2 File="{file}"
            Diode:D1 out 0 Is=1e-12 N=1.0
            R:RL out 0 R=50 Ohm
            analysis ENV1 type=env Stop=5e-6 Accuracy=draft MaxHarm=3 Probes="out" Segments=1 Warmup=0
            """, out var nl);
        double drift = 0, peak = 0;
        for (int k = 0; k <= 3; k++)
        {
            var y = Envelope(ds, "out", k);
            foreach (var v in y) { drift = Math.Max(drift, (v - y[0]).Magnitude); peak = Math.Max(peak, v.Magnitude); }
        }
        output.WriteLine($"drift {drift:G3} V on a {peak:G3} V output; " + string.Join(" | ", nl.Notes));
        Assert.True(peak > 0.1, "the diode is driven");
        // Tol is Newton's current residual (1e-6 A); on this 50-ohm port that is 5e-5 V. A start point stamped through the
        // interpolator and a stepping stamped through the fit would part by the 0.06 the note reports, ~5e-2 V here.
        Assert.True(drift < 1e-6 * 50, $"the run drifted {drift:G3} V from its harmonic-balance start");
        Assert.Contains(nl.Notes, n => n.Contains("2 GHz") && n.Contains("3 GHz"));
    }

    // ── 4. two fundamentals ───────────────────────────────────────────────

    [Fact]
    public void Lattice_EachCarrierMatchesTheExactReference()
    {
        string file = WriteS2p(_dir, "cheb.s2p", Rational);
        // 16 MHz apart is more than 3x a 5 MHz carrier: each gets its own fundamental, both inside the ripple band.
        var ds = Run(Filter(file, "Carrier[1]=\"LTE;BW=5MHz;Mod=64QAM;Off=-8MHz;P=0;Seed=1\" Carrier[2]=\"LTE;BW=5MHz;Mod=64QAM;Off=8MHz;P=0;Seed=2\"",
                            $"Stop={GateStop} Accuracy={Gate} MaxMixOrder=1 Probes=\"in,out\" Segments=1 Warmup=2e-6"), out var nl);
        Assert.Equal(2.0, ds["__EnvFundamentals"].RealValues[0]);
        var src = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single();
        double fs = 1 / ds["__EnvStep_s"].RealValues[0];
        int nm = ds["V"].Axes[0].Length;
        int nseg = Nseg(ds, [src], nm);
        for (int c = 0; c < 2; c++)
        {
            double fc = F0 + src.Signal.Carriers[c].OffsetHz;
            var yRef = Reference(src, fs, fc, [c], f => Rational(f)[1, 0], nm);
            double exact = Aclr(yRef, fs, nseg, src.Signal.Carriers[c].Spec)[0], got = DsAclr(ds, "Iout", c)[0];
            output.WriteLine($"carrier {c}: Pch exact {exact:0.0000} dBm, engine {got:0.0000} dBm");
            Assert.True(Math.Abs(got - exact) < 0.05, $"carrier {c}: channel power {got:0.000} dBm against {exact:0.000} dBm");
        }
        Assert.True(ds["__EnvTangentError"].RealValues[0] < 1e-10);
    }

    // ── 5. nothing else moves ─────────────────────────────────────────────

    [Fact]
    public void LumpedCircuit_IsNotFitted_AndAnSnpIsRestoredAfterTheRun()
    {
        var lumped = Run("""
            MOD_Source:S1 in 0 Fc=1e9 Pavg=0 Z=50 Record=0.5e-3 Carrier[1]="CW;Off=0;P=0"
            C:C1 in mid C=2 pF
            L:L1 mid out L=5 nH
            IProbe:Iout out o
            R:RL o 0 R=50 Ohm
            analysis ENV1 type=env Stop=2e-6 Accuracy=draft MaxHarm=1 Segments=1
            """, out var lnl);
        Assert.False(lumped.Cubes.ContainsKey("__EnvSnpFit"));
        Assert.Empty(lnl.Notes);

        // The substitution is the run's alone: afterwards the netlist holds its own components and the real SnpModel,
        // so every other analysis on it stamps the file through its interpolator exactly as before.
        var (lib, tb) = new CnlReader().Read(Filter(WriteS2p(_dir, "cheb.s2p", Rational), $"Carrier[1]=\"CW;Off=0;P=0\"",
            "Stop=2e-6 Accuracy=draft MaxHarm=1 Probes=\"out\" Segments=1"));
        var nl = new Elaborator(lib).Elaborate(tb);
        var before = nl.Components.ToArray();
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        new EnvelopeEngine(nl).Run(plan.Params);
        Assert.Equal(before, nl.Components);
        Assert.IsType<SnpModel>(nl.Components.Single(c => c.InstancePath == "F1").Model);
    }

    // ── 6. what stays a tangent says so ───────────────────────────────────

    /// <summary>A coupled line keeps its value and slope at each harmonic (a TLIN is stepped as its delay since brief
    /// ENV-FIT, and is exact): a long mismatched one is named, in both engines.</summary>
    [Theory]
    [InlineData("Off=0")]                                   // one fundamental
    [InlineData("Off=-100MHz")]                             // with a second carrier at +100 MHz: two fundamentals
    public void LongMismatchedCoupledLine_WarnsAndNamesIt(string off)
    {
        string carriers = off == "Off=0"
            ? $"Carrier[1]=\"{Nr20}\""
            : "Carrier[1]=\"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=-100MHz;P=0;Seed=1\" Carrier[2]=\"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=100MHz;P=0;Seed=2\"";
        Run($"""
            MOD_Source:S1 in 0 Fc=1e9 Pavg=0 Z=50 Record=0.5e-3 {carriers}
            CLIN:TL1 in out c d Ze=30 Ohm Zo=20 Ohm E=2970 deg F=1 GHz
            R:RL out 0 R=50 Ohm
            R:Rc c 0 R=50 Ohm
            R:Rd d 0 R=50 Ohm
            analysis ENV1 type=env Stop=2e-6 Accuracy=draft MaxHarm=1 MaxMixOrder=1 Probes="out" Segments=1
            """, out var nl);
        output.WriteLine(string.Join("\n", nl.Warnings));
        Assert.Contains(nl.Warnings, w => w.Contains("'TL1'") && w.Contains("approximated to first order around each harmonic"));
    }

    // ── System blocks: the same fit, from their own stamp ─────────────────

    /// <summary>A System Filter or Duplexer stamps a rational S(ω), which the tangent loses exactly as it loses an SnP's
    /// (a passive filter read +7 dB of gain, a duplexer diverged). Fitted, it matches S21 from the ordinary S-parameter
    /// engine — a path that never touches the fit — through the same reference as the fixtures.</summary>
    [Theory]
    [InlineData("Filter:F1 in 0 out 0 Response=Chebyshev Form=Bandpass Order=5 F1=989 MHz F2=1011 MHz Ripple=0.5 dB Zin=50 Ohm Zout=50 Ohm", "")]
    [InlineData("Duplexer:D1 in 0 out 0 x 0 Zant=50 Ohm TxResponse=Chebyshev TxForm=Bandpass TxOrder=5 TxF1=989 MHz TxF2=1011 MHz TxRipple=0.5 " +
                "RxResponse=Chebyshev RxForm=Bandpass RxOrder=5 RxF1=1.1 GHz RxF2=1.2 GHz RxRipple=0.1", "R:RX x 0 R=50 Ohm")]
    public void SystemBlock_MatchesTheExactReference(string block, string extra)
    {
        var ds = Run($"""
            MOD_Source:S1 in 0 Fc=1e9 Pavg=0 Z=50 Record=0.5e-3 Carrier[1]="{Nr20}"
            {block}
            IProbe:Iout out o
            R:RL o 0 R=50 Ohm
            {extra}
            analysis ENV1 type=env Stop={GateStop} Accuracy={Gate} MaxHarm=1 Probes="in,out" Segments=1 Warmup=2e-6
            """, out var nl);
        var (lib, tb) = new CnlReader().Read($"""
            Port:P1 in 0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            {(extra == "" ? "" : "Port:P3 x 0 Num=3 Z=50 Ohm")}
            {block}
            """);
        var snl = new Elaborator(lib).Elaborate(tb);
        int np = extra == "" ? 2 : 3;
        Complex[] S21(double[] f)
        {
            var sv = SParameterEngine.Run(snl, f)["S"].ComplexValues;
            return [.. Enumerable.Range(0, f.Length).Select(k => sv[(k * np + 1) * np + 0])];
        }
        var src = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single();
        double fs = 1 / ds["__EnvStep_s"].RealValues[0];
        var y = Envelope(ds, "out", 1);
        var yRef = Reference(src, fs, F0, null, S21, y.Length);
        var exact = Aclr(yRef, fs, Nseg(ds, [src], y.Length), src.Signal.Carriers[0].Spec);
        var got = DsAclr(ds, "Iout", 0);
        double worst = Enumerable.Range(0, 5).Max(m => Math.Abs(got[m] - exact[m]));
        double nmse = NmseDb(y, yRef);
        output.WriteLine($"exact Pch {exact[0]:0.0000} ACPR1 {exact[1]:0.000}/{exact[2]:0.000}; engine Pch {got[0]:0.0000} ACPR1 {got[1]:0.000}/{got[2]:0.000}; worst {worst:0.0000} dB, NMSE {nmse:0.0} dB");
        output.WriteLine(string.Join(" | ", nl.Notes.Concat(nl.Warnings)));
        Assert.True(worst < 0.05, $"channel power / ACPR off the exact reference by {worst:0.000} dB");
        Assert.True(nmse < -40, $"output NMSE {nmse:0.0} dB against y(t)");
        Assert.Single(ds["__EnvSnpFit"].Axes[0].Labels!);
    }

    // ── the realization ───────────────────────────────────────────────────

    /// <summary>The realized model, swept by the ordinary S-parameter engine, is the fitted S to round-off — at DC,
    /// inside the file and far above it.</summary>
    [Fact]
    public void Realization_SweptAsAnSParameterBlock_IsTheFit()
    {
        string file = WriteS2p(_dir, "chebdelay.s2p", Delayed);
        var (lib, tb) = new CnlReader().Read($"""
            Port:P1 in 0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            SnP:F1 in out 0 NumPorts=2 File="{file}"
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        using var sub = EnvelopeSnpFit.Apply(nl, 3e9, [], 0);
        var model = Assert.IsType<RationalSnpModel>(nl.Components.Single(c => c.InstancePath == "F1").Model);
        Assert.True(model.Fit.Poles.Length > 0);
        double[] f = [0, 0.3e9, 0.95e9, 1e9, 1.011e9, 1.2e9, 3e9];
        var sCube = SParameterEngine.Run(nl, f)["S"];
        var sv = sCube.ComplexValues;
        double worst = 0;
        for (int k = 0; k < f.Length; k++)
        {
            var want = model.S(f[k]);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) worst = Math.Max(worst, (sv[(k * 2 + i) * 2 + j] - want[i, j]).Magnitude);
        }
        output.WriteLine($"worst |S_sweep - S_fit| {worst:G3}");
        Assert.True(worst < 1e-12, $"the realization departs from the fit by {worst:G3}");
    }
}
