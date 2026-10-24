using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine;
using RfCore.Data;
using CircuitRF.Design.Modeling;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// One synthetic device's truth (brief ENV-PCW §5): every constant <see cref="SyntheticGan"/> simulates, and the grids its
/// raw files are measured on. <see cref="SyntheticGan.Gan4x125"/> is the Model Composer example's half-watt device;
/// <see cref="SyntheticGan.Radar8x250"/> the pulsed-radar example's.
/// </summary>
public sealed record GanTruth
{
    public required int N0 { get; init; }
    public required string UnitWidth { get; init; }
    /// <summary>The core's constants (the <see cref="ComposedTestModels"/> equations' names), as the SDD's globals.</summary>
    public required (string Name, double Value)[] CoreParams { get; init; }
    /// <summary>The charges, in the <see cref="ComposedTestModels"/> form unless stated.</summary>
    public string QgEq { get; init; } = ComposedTestModels.QgEq;
    public string QdEq { get; init; } = ComposedTestModels.QdEq;
    /// <summary>Total thermal resistance at N0 (K/W); one pole of <see cref="TauTh"/>, or a Foster ladder of
    /// (fraction of Rth, τ) poles when <see cref="Ladder"/> is not empty.</summary>
    public required double Rth { get; init; }
    public double TauTh { get; init; }
    public (double Fraction, double Tau)[] Ladder { get; init; } = [];
    public required double Kt { get; init; }
    public required double At { get; init; }
    public required double Ct { get; init; }
    public double TauCapt { get; init; } = 1e-8;
    public double TauEmit { get; init; } = 1e-3;
    public double Xref { get; init; } = 1;
    public double Vcap { get; init; } = 0.05;
    public double Cn { get; init; } = 1e-3;
    public double Cpad { get; init; } = 30e-15;
    public double Lpad { get; init; } = 15e-12;
    public required double Lg { get; init; }
    public required double Ld { get; init; }
    public required double Ls { get; init; }
    /// <summary>The network's other values at N0, the stock GaN two-shell network's unless stated: the access resistances
    /// and the inner capacitances, which scale with the periphery, and the outer (pad-level) capacitances, which do not.</summary>
    public double Rg { get; init; } = 5.2;
    public double Rd { get; init; } = 9.2;
    public double Rs { get; init; } = 6.6;
    public double Cpgi { get; init; } = 4.9e-15;
    public double Cpdgi { get; init; } = 1e-15;
    public double Cpdi { get; init; } = 21e-15;
    public double PulseWidth { get; init; } = 0.5e-6;
    public double SampleDelay { get; init; } = 0.4e-6;
    public double Period { get; init; } = 1e-3;
    public required double PreVds { get; init; }
    public double PreWidth { get; init; } = 50e-6;
    public double PreGap { get; init; } = 1e-6;
    public required double QuiescentVgs { get; init; }
    public required string Note { get; init; }
    public required double[] DcVgs { get; init; }
    public required double[] DcVds { get; init; }
    public required double[] PulsedVgs { get; init; }
    public required double[] PulsedVds { get; init; }
    public required double[] SVgs { get; init; }
    public required double[] SVds { get; init; }
    public required double[] Freqs { get; init; }
    public required (string Name, double AmbientC, double Vq, bool DoublePulse)[] PulsedSets { get; init; }
    public required double[] STemperatures { get; init; }
    /// <summary>The most a self-heated (DC or S) bias may dissipate, W at N0: a DC measurement stops at its compliance.
    /// Null measures the whole grid.</summary>
    public double? MaxDcPowerW { get; init; }
}

/// <summary>
/// The series acceptance truth (brief-modelcomposer-16 R-mc16-1, R-mc16-4; overview §4): one synthetic GaN device written
/// as an SDD — independent of the composer's code — with everything the composer models: <see cref="ComposedTestModels"/>'
/// currents and non-separable charges, a drain current that falls with the junction temperature, self-heating through
/// <see cref="Rth"/> ∥ Cth, and a drain-lag trap state whose capture and emission follow the CaptureEmission form at its
/// defaults. It sits inside the GaN two-shell network (its inductances away from the stock values, so the network fit has
/// something to find) and behind a probe pad on each side (shunt C, series L).
///
/// <para>Every "measurement" is circuitRF simulating that SDD: DC at the dynamic state (the trap at <c>vds</c>, the
/// junction heated), a pulse at the chuck temperature with the trap state the pulse leaves (emission and capture
/// integrated over the measurement's own timing), and S at the DC state, where the thermal and trap nodes are frozen.</para>
/// </summary>
public static class SyntheticGan
{
    // ── The truth ─────────────────────────────────────────────────────────────

    /// <summary>The measured device's finger count; its unit width is 125 µm.</summary>
    public const int N0 = 4;
    /// <summary>Thermal resistance (K/W at N0), thermal time constant (s), and the drain current's fall per kelvin.</summary>
    public const double Rth = 25, TauTh = 10e-6, Kt = 0.004;
    /// <summary>Drain lag: the threshold shifts by <c>At·tanh((phi2 − vds)/Ct)</c>.</summary>
    public const double At = 0.3, Ct = 5;
    /// <summary>
    /// The CaptureEmission defaults, which the truth's trap follows. Capture slows as the gap closes, so a pulse from a 0 V
    /// quiescent drain leaves the state about half a volt short of the pulse's drain voltage: such points lie beyond their
    /// trap-setting bias and are validation only.
    /// </summary>
    public const double TauCapt = 1e-8, TauEmit = 1e-3, Xref = 1, Vcap = 0.05;
    /// <summary>The trap node's charge per volt: slow enough states without stalling harmonic balance.</summary>
    public const double Cn = 1e-3;
    /// <summary>The probe pads: shunt capacitance and series inductance per side.</summary>
    public const double Cpad = 30e-15, Lpad = 15e-12;
    /// <summary>The network's inductances, away from the stock 39.5, 46.4 and 5.25 pH.</summary>
    public const double Lg = 45e-12, Ld = 52e-12, Ls = 6.5e-12;

    /// <summary>The pulse timing every pulsed set states, seconds.</summary>
    public const double PulseWidth = 0.5e-6, SampleDelay = 0.4e-6, Period = 1e-3;
    /// <summary>The double pulse's pre-pulse: to 20 V at pinch-off for 50 µs, 1 µs before the measuring pulse.</summary>
    public const double PreVds = 20, PreWidth = 50e-6, PreGap = 1e-6;
    /// <summary>Every pulsed set holds its quiescent point at this pinched-off gate voltage, where it dissipates nothing.</summary>
    public const double QuiescentVgs = -6;

    public const string Note = "Synthetic: simulated by circuitRF from a known device (an SDD with self-heating and drain lag), not measured.";

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Sdd(string eq) => Regex.Replace(Regex.Replace(eq, @"\bvgs\b", "_v1"), @"\bvds\b", "_v2");

    /// <summary>The truth's constants as globals, with the ambient <c>temp</c> unless a bench states its own.</summary>
    public static string Globals(double? ambientC, GanTruth? truth = null)
    {
        var g = truth ?? Gan4x125;
        return string.Concat(g.CoreParams.Select(p => $"{p.Name} = {p.Value:R}\n")) +
            $"kT = {R(g.Kt)}\nAt = {R(g.At)}\nCt = {R(g.Ct)}\nTauC = {R(g.TauCapt)}\nTauE = {R(g.TauEmit)}\nXref = {R(g.Xref)}\nVcap = {R(g.Vcap)}\n" +
            (ambientC is { } t ? $"temp = {R(t)}\n" : "");
    }

    /// <summary>
    /// The intrinsic core on (g d s), the thermal node <c>t</c> (its voltage the rise in kelvin) and the trap node <c>p</c>
    /// (its voltage phi2), its currents and charges times <paramref name="k"/> = N/N0. <paramref name="held"/> leaves the
    /// trap node's dynamics out, for a pulse that holds it: a capture rate across a held gap is a current of no meaning, and
    /// across a gap of volts it is large enough to stall the DC solve.
    /// </summary>
    private static string Core(string name, double k, bool held, GanTruth g)
    {
        string vg = $"(_v1-At*tanh((_v4-_v2)/Ct))";
        string id = $"beta*(1-kT*(temp+_v3-25))*(ln(1+exp(({vg}-Vto)/Vs))*Vs)^2*(1+lambda*_v2)*tanh(alpha*_v2)";
        string igs = Sdd(ComposedTestModels.IgGsEq), igd = Sdd(ComposedTestModels.IgGdEq);
        string ig = $"({igs})+({igd})", idt = $"({id})-({igd})";
        string u = "((_v2-_v4-Xref)/Vcap)";
        string rate = $"(_v2-_v4)/TauE+(Xref/TauC)*(if({u}<20,exp({u}),exp(20)*({u}-19))-exp(-Xref/Vcap))";
        string kk = R(k);
        return $"define {name} (g d s t p)\n" +
               $"SDD:core g s d s t 0 p 0 I[1,0]={kk}*({ig}) I[2,0]={kk}*({idt}) I[3,0]=-{kk}*(({ig})*_v1+({idt})*_v2) " +
               (held ? "I[4,0]=0 " : $"I[4,0]=-{R(g.Cn)}*({rate}) ") + $"I[1,1]={kk}*({Sdd(g.QgEq)}) I[2,1]={kk}*({Sdd(g.QdEq)}) " +
               $"I[4,1]={R(g.Cn)}*_v4\nend\n";
    }

    /// <summary>The GaN two-shell network at <paramref name="n"/> fingers, the default scaling rules applied.</summary>
    private static string Network(string name, int n, GanTruth g)
    {
        double s = (double)n / g.N0;
        return $"define {name} (P1 P2 P3 P4 P5 P6)\n" +
               $"C:C1 P1 P3 C=7e-15\nC:C4 P1 P2 C=0.5e-15\nC:C6 P2 P3 C=7e-15\n" +
               $"L:L1 P1 n1 L={R(g.Lg)}\nL:L2 P2 n3 L={R(g.Ld)}\nL:L3 P3 n2 L={R(g.Ls)}\n" +
               $"R:rg n1 P4 R={R(g.Rg / s)}\nR:rd n3 P5 R={R(g.Rd / s)}\nR:rs n2 P6 R={R(g.Rs / s)}\n" +
               $"C:C2 n1 n2 C={R(g.Cpgi * s)}\nC:C3 n1 n3 C={R(g.Cpdgi * s)}\nC:C5 n3 n2 C={R(g.Cpdi * s)}\n" +
               "Mutual:M1 Inductor1=\"L1\" Inductor2=\"L2\" M=1e-12\nend\n";
    }

    /// <summary>
    /// The device as a cell <c>gan (G D S)</c> at <paramref name="n"/> fingers: the network, the core, and either the thermal
    /// network with the trap node free (<paramref name="pulsePhi"/> null: DC, S and harmonic balance), or the junction held
    /// at the chuck temperature and the trap state held at <paramref name="pulsePhi"/> (a pulse).
    /// </summary>
    public static string Device(int n = N0, double? pulsePhi = null, GanTruth? truth = null)
    {
        var g = truth ?? Gan4x125;
        double s = (double)n / g.N0;
        var sb = new StringBuilder(Core("gancore", s, pulsePhi is not null, g)).Append(Network("gannet", n, g));
        sb.Append("define gan (G D S)\ngannet:NW G D S gi di si\ngancore:CORE gi di si t p\n");
        if (pulsePhi is { } phi) sb.Append($"Vdc:VT t 0 V=0\nVdc:VP p 0 V={R(phi)}\n");
        else if (g.Ladder.Length == 0) sb.Append($"R:RTH t 0 R={R(g.Rth / s)}\nC:CTH t 0 C={R(g.TauTh / g.Rth * s)}\n");
        else
        {
            // a Foster ladder: pole i is Ri ∥ Ci in series from the junction to the ambient, Ri = fraction × Rth, Ci = τi / Ri
            for (int i = 0; i < g.Ladder.Length; i++)
            {
                var (fraction, tau) = g.Ladder[i];
                string a = i == 0 ? "t" : $"tl{i}", b = i == g.Ladder.Length - 1 ? "0" : $"tl{i + 1}";
                double ri = fraction * g.Rth / s;
                sb.Append($"R:RTH{i + 1} {a} {b} R={R(ri)}\nC:CTH{i + 1} {a} {b} C={R(tau / ri)}\n");
            }
        }
        return sb.Append("end\n").ToString();
    }

    /// <summary>The probe pads between probe nets <c>pg</c>/<c>pd</c> and the device's <c>g</c>/<c>d</c>.</summary>
    private static string Pads(GanTruth g) =>
        $"C:CPG pg 0 C={R(g.Cpad)}\nL:LPG pg g L={R(g.Lpad)}\nC:CPD pd 0 C={R(g.Cpad)}\nL:LPD pd d L={R(g.Lpad)}\n";

    // ── The trap state a pulse leaves ─────────────────────────────────────────

    /// <summary><c>dx/dt = −g(x)</c> for the gap <c>x = vds − phi2</c>: emission and the capture term, tangent-continued as
    /// the SDD continues it.</summary>
    private static double Rate(double x, GanTruth g)
    {
        double u = (x - g.Xref) / g.Vcap;
        double e = u < 20 ? Math.Exp(u) : Math.Exp(20) * (u - 19);
        return x / g.TauEmit + g.Xref / g.TauCapt * (e - Math.Exp(-g.Xref / g.Vcap));
    }

    /// <summary>phi2 after <paramref name="duration"/> at a fixed <paramref name="vds"/>, from <paramref name="phi"/>: implicit
    /// Euler on log-spaced steps, each solved by bisection (g is increasing with g(0) = 0).</summary>
    public static double Evolve(double phi, double vds, double duration, GanTruth? truth = null)
    {
        var g = truth ?? Gan4x125;
        double x = vds - phi, t = 0, h = 1e-20;
        while (t < duration)
        {
            h = Math.Min(h, duration - t);
            double lo = Math.Min(0, x), hi = Math.Max(0, x), x0 = x;
            for (int k = 0; k < 200; k++)
            {
                double mid = 0.5 * (lo + hi);
                if (mid + h * Rate(mid, g) > x0) hi = mid; else lo = mid;
            }
            x = 0.5 * (lo + hi);
            t += h;
            h *= 1.05;
        }
        return vds - x;
    }

    /// <summary>The trap state a pulsed set's sample sees: set by its quiescent point (held long, so phi2 sits at it) or by a
    /// pre-pulse, then moved over the gap and the sample delay at the point's own intrinsic drain voltage.</summary>
    private static double PulsePhi(double settingVds, bool doublePulse, double vdsIntrinsic, GanTruth g)
    {
        double phi = settingVds;
        if (doublePulse)
        {
            phi = Evolve(0, g.PreVds, g.PreWidth, g);    // the quiescent drain is 0 V
            phi = Evolve(phi, 0, g.PreGap, g);
        }
        return Evolve(phi, vdsIntrinsic, g.SampleDelay, g);
    }

    // ── Solving the truth ─────────────────────────────────────────────────────

    private sealed record Solved(double Ig, double Id, double Vgsi, double Vdsi, double Tj);

    private static Solved SolveDc(string cnl, double ambientC)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged, $"the truth's DC solve did not converge: {dc.Iterations} it, residual {dc.FinalResidual}\n{cnl}");
        var (rows, names) = nl.Nodes.ResultRows(excludeInternal: false);
        double V(string node)
        {
            int k = Array.IndexOf(names, node);
            return k < 0 ? 0 : dc.NodeVoltages[rows[k] - 1];
        }
        return new Solved(dc.ProbeCurrents["IPG"], dc.ProbeCurrents["IPD"],
                          V("X1.gi") - V("X1.si"), V("X1.di") - V("X1.si"), ambientC + V("X1.t"));
    }

    private static string DcBench(int n, double? phi, double ambientC, double vgs, double vds, GanTruth g) =>
        Globals(ambientC, g) + Device(n, phi, g) +
        $"Vdc:VG vg 0 V={R(vgs)}\nIProbe:IPG vg g\ngan:X1 g d 0\nIProbe:IPD vd d\nVdc:VD vd 0 V={R(vds)}\n";

    /// <summary>One measured point and its intrinsic truth.</summary>
    public sealed record Point(double Vgs, double Vds, double Ig, double Id, double Vgsi, double Vdsi, double Phi, double Tj);

    /// <summary>DC I–V at the dynamic state: phi2 = vds and the junction heated.</summary>
    public static IReadOnlyList<Point> Dc(double[] vgsList, double[] vdsList, double ambientC, int n = N0, GanTruth? truth = null)
    {
        var t = truth ?? Gan4x125;
        return [.. from g in vgsList from d in vdsList let s = SolveDc(DcBench(n, null, ambientC, g, d, t), ambientC)
                   where !(s.Id * d > t.MaxDcPowerW * n / t.N0)
                   select new Point(g, d, s.Ig, s.Id, s.Vgsi, s.Vdsi, s.Vdsi, s.Tj)];
    }

    /// <summary>
    /// Pulsed I–V at the chuck temperature from quiescent drain voltage <paramref name="quiescentVds"/>, or from a pre-pulse
    /// to <see cref="PreVds"/>: the trap state the pulse leaves found by iterating it with the point's intrinsic drain
    /// voltage.
    /// </summary>
    public static IReadOnlyList<Point> Pulsed(double[] vgsList, double[] vdsList, double ambientC, double quiescentVds, bool doublePulse,
                                              GanTruth? truth = null)
    {
        var t = truth ?? Gan4x125;
        var points = new List<Point>();
        foreach (double g in vgsList)
            foreach (double d in vdsList)
            {
                double phi = doublePulse ? t.PreVds : quiescentVds;
                Solved s = SolveDc(DcBench(t.N0, phi, ambientC, g, d, t), ambientC);
                for (int it = 0; it < 4; it++)
                {
                    double next = PulsePhi(quiescentVds, doublePulse, s.Vdsi, t);
                    if (Math.Abs(next - phi) < 1e-9) break;
                    phi = next;
                    s = SolveDc(DcBench(t.N0, phi, ambientC, g, d, t), ambientC);
                }
                points.Add(new Point(g, d, s.Ig, s.Id, s.Vgsi, s.Vdsi, phi, ambientC));
            }
        return points;
    }

    /// <summary>S (50 Ω) at the probe plane — pads included — at each DC bias, through ideal tees.</summary>
    public static (IReadOnlyList<Point> Points, Complex[][,] S) SParams(double[] vgsList, double[] vdsList, double ambientC, double[] freqs,
                                                                        GanTruth? truth = null)
    {
        var t = truth ?? Gan4x125;
        var points = new List<Point>();
        var s = new List<Complex[,]>();
        foreach (double g in vgsList)
            foreach (double d in vdsList)
            {
                string cnl = Globals(ambientC, t) + Device(t.N0, null, t) + Pads(t) + "gan:X1 g d 0\n" +
                    $"Vdc:VG vg 0 V={R(g)}\nIProbe:IPG vg lg\nL:LG lg pg L=1\nC:CG pa pg C=1\nPort:P1 pa 0 Num=1 Z=50 Ohm\n" +
                    $"Vdc:VD vd 0 V={R(d)}\nIProbe:IPD vd ld\nL:LD ld pd L=1\nC:CD pb pd C=1\nPort:P2 pb 0 Num=2 Z=50 Ohm\n";
                var dc = SolveDc(cnl, ambientC);
                if (dc.Id * d > t.MaxDcPowerW) continue;        // past the compliance a DC bias is never held
                points.Add(new Point(g, d, dc.Ig, dc.Id, dc.Vgsi, dc.Vdsi, dc.Vdsi, dc.Tj));
                var (lib, tb) = new CnlReader().Read(cnl);
                using var nl = new Elaborator(lib).Elaborate(tb);
                var sp = SParameterEngine.Run(nl, freqs)["S"];
                var m = new Complex[freqs.Length, 4];
                for (int k = 0; k < freqs.Length; k++)
                    for (int i = 0; i < 2; i++)
                        for (int j = 0; j < 2; j++)
                            m[k, 2 * i + j] = (Complex)sp[k, i, j];
                s.Add(m);
            }
        return (points, [.. s]);
    }

    /// <summary>The open and short dummies: the pads alone, and the pads with their device ends grounded.</summary>
    public static (Complex[,] Open, Complex[,] Short) PadDummies(double[] freqs, GanTruth? truth = null)
    {
        var t = truth ?? Gan4x125;
        Complex[,] Run(string body)
        {
            var (lib, tb) = new CnlReader().Read(body + "Port:P1 pg 0 Num=1 Z=50 Ohm\nPort:P2 pd 0 Num=2 Z=50 Ohm\n");
            using var nl = new Elaborator(lib).Elaborate(tb);
            var sp = SParameterEngine.Run(nl, freqs)["S"];
            var m = new Complex[freqs.Length, 4];
            for (int k = 0; k < freqs.Length; k++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                        m[k, 2 * i + j] = (Complex)sp[k, i, j];
            return m;
        }
        return (Run(Pads(t) + "R:RG g 0 R=1e12\nR:RD d 0 R=1e12\n"), Run(Pads(t) + "R:RG g 0 R=1e-6\nR:RD d 0 R=1e-6\n"));
    }

    // ── The raw files ─────────────────────────────────────────────────────────

    public static readonly double[] DcVgs = [-5.0, -4.0, -3.0, -2.0, -1.6, -1.2, -0.9, -0.6, -0.3, 0.0, 0.3, 0.6, 0.8, 1.0, 1.4];
    public static readonly double[] DcVds = [0, 0.5, 1, 2, 3, 5, 8, 12, 16, 20];
    public static readonly double[] PulsedVgs = [-5.0, -4.0, -3.0, -2.0, -1.6, -1.2, -0.8, -0.4, 0.0, 0.4, 0.8, 1.0, 1.4];
    public static readonly double[] PulsedVds = [0, 1, 2, 4, 6, 8, 10, 14, 20];
    public static readonly double[] SVgs = [-2.0, -1.6, -1.2, -0.8, -0.4, 0.0, 0.3];
    public static readonly double[] SVds = [0.5, 1, 2, 4, 7, 10, 15];
    public static readonly double[] Freqs = [0.5e9, 1e9, 2e9, 3e9, 4e9, 6e9, 8e9, 10e9, 12e9, 15e9, 18e9, 20e9];

    /// <summary>The pulsed sets: name, chuck temperature, quiescent drain voltage, double pulse.</summary>
    public static readonly (string Name, double AmbientC, double Vq, bool DoublePulse)[] PulsedSets =
    [
        ("pulsed_q0_25C", 25, 0, false), ("pulsed_q0_125C", 125, 0, false),
        ("pulsed_q10_25C", 25, 10, false), ("pulsed_q20_25C", 25, 20, false), ("pulsed_dp20_25C", 25, 0, true),
    ];

    public static readonly double[] STemperatures = [25, 85];

    /// <summary>The Model Composer example's device: the constants above, measured on the grids above. Declared after the
    /// grids, which a static initializer must find already set.</summary>
    /// <summary>
    /// The pulsed-radar example's device (brief ENV-PCW §5): an 8 × 250 µm cell (2 mm) of GaN at radar power density, built
    /// on the same equations as <see cref="Gan4x125"/> — ~1 A/mm, knee ~5 V, pinch-off near −3 V, data to 100 V — with a
    /// drain current falling <see cref="GanTruth.Kt"/> per kelvin, a three-pole Foster ladder (channel ~1 µs, die ~20 µs,
    /// attach and flange ~0.5 ms; 9 K/W at the cell, 1.5 K/W at the example's 12 mm) and the first device's drain lag
    /// stretched over the supply (Ct 20 V). DC and S stop at 10 W of dissipation, a DC instrument's compliance: at 16 W an S
    /// bias at the 85 °C chuck heated the junction past 200 °C, beyond every I–V set, and the fit extrapolated there.
    /// </summary>
    public static readonly GanTruth Radar8x250 = new()
    {
        N0 = 8, UnitWidth = "250 um",
        CoreParams =
        [
            ("beta", 0.17), ("Vto", -3.0), ("Vs", 0.25), ("lambda", 0.002), ("alpha", 0.45),
            ("Isg", 2e-12), ("Vtg", 0.035),
            ("C0", 1.8e-12), ("C1", 0.15e-12), ("Cx", -0.4e-12), ("Cd", 0.5e-12), ("Cy", -0.3e-12), ("Vq", 15),
        ],
        // The first device's charges saturate in vds by ~3 V, so over a radar part's 5-100 V its capacitances would be flat:
        // a 15 V scale keeps them moving across the supply.
        QgEq = ComposedTestModels.QgEq.Replace("tanh(vds)", "tanh(vds/Vq)", StringComparison.Ordinal),
        QdEq = ComposedTestModels.QdEq.Replace("tanh(vds)", "tanh(vds/Vq)", StringComparison.Ordinal),
        Rth = 9, Ladder = [(0.3, 1e-6), (0.4, 20e-6), (0.3, 0.5e-3)],
        Kt = 0.003, At = 0.4, Ct = 20,
        Lg = Lg, Ld = Ld, Ls = Ls,
        // a 2 mm cell's access resistances (~1 Ω·mm source and drain, a gate fed from both ends) and its inner capacitances
        // at four times the half-millimetre stock values
        Rg = 0.8, Rd = 0.5, Rs = 0.35, Cpgi = 19.6e-15, Cpdgi = 4e-15, Cpdi = 84e-15,
        // Trap-setting levels to the drain's peak under drive (~95 V at 48 V): at 60 V the fit extrapolated the current
        // collapse in saturation and read 0.7-1.5 dB high there.
        PreVds = 100, QuiescentVgs = -6,
        Note = "Synthetic: simulated by circuitRF from a known radar-class GaN device (an SDD with a three-pole thermal ladder and drain lag), not measured.",
        // Gate to -10 V: at 48 V and full drive the intrinsic gate swings to about -8 V. With the grids stopping at -6 V
        // the fitted gate current ran away below them (about 1 A of reverse current at -7 V), clamped the negative swing
        // and expanded the gain from 30.5 dBm of drive, so the model saturated about 1 dB of drive early.
        DcVgs = [-10.0, -8.0, -6.0, -5.0, -4.0, -3.5, -3.0, -2.75, -2.5, -2.25, -2.0, -1.5, -1.0, 0.0, 1.0],
        DcVds = [0, 0.5, 1, 2, 3, 5, 8, 12, 16, 24, 32, 48, 64, 80, 100],
        PulsedVgs = [-10.0, -8.0, -6.0, -5.0, -4.0, -3.5, -3.0, -2.75, -2.5, -2.25, -2.0, -1.5, -1.0, 0.0, 1.0],
        PulsedVds = [0, 1, 2, 4, 6, 8, 12, 16, 24, 32, 48, 64, 80, 100],
        SVgs = [-2.9, -2.7, -2.5, -2.25, -2.0, -1.6],
        SVds = [5, 12, 24, 36, 48, 64, 80, 100],
        Freqs = Freqs,
        PulsedSets =
        [
            ("pulsed_q0_25C", 25, 0, false), ("pulsed_q0_125C", 125, 0, false),
            ("pulsed_q48_25C", 25, 48, false), ("pulsed_q96_25C", 25, 96, false), ("pulsed_dp100_25C", 25, 0, true),
            // the trapped states hot as well: at radar duty the junction runs at 70-100 °C, and with every trapped set at
            // 25 °C the fit extrapolated the trap state's temperature dependence and read 0.7-1 dB low at 70 °C
            ("pulsed_q48_125C", 125, 48, false), ("pulsed_q96_125C", 125, 96, false),
        ],
        STemperatures = [25, 85],
        MaxDcPowerW = 10,
    };

    public static readonly GanTruth Gan4x125 = new()
    {
        N0 = N0, UnitWidth = "125 um",
        CoreParams = [.. ComposedTestModels.IdParams.Concat(ComposedTestModels.IgParams).Concat(ComposedTestModels.QgParams)
                                            .Concat(ComposedTestModels.QdParams).DistinctBy(p => p.Name)],
        Rth = Rth, TauTh = TauTh, Kt = Kt, At = At, Ct = Ct, TauCapt = TauCapt, TauEmit = TauEmit, Xref = Xref, Vcap = Vcap,
        Cn = Cn, Cpad = Cpad, Lpad = Lpad, Lg = Lg, Ld = Ld, Ls = Ls,
        PulseWidth = PulseWidth, SampleDelay = SampleDelay, Period = Period, PreVds = PreVds, PreWidth = PreWidth, PreGap = PreGap,
        QuiescentVgs = QuiescentVgs, Note = Note,
        DcVgs = DcVgs, DcVds = DcVds, PulsedVgs = PulsedVgs, PulsedVds = PulsedVds, SVgs = SVgs, SVds = SVds, Freqs = Freqs,
        PulsedSets = PulsedSets, STemperatures = STemperatures,
    };

    /// <summary>
    /// Writes the raw files of <c>synthetic-gan</c> into <paramref name="dir"/>: <c>pads/open.s2p</c> and <c>short.s2p</c>,
    /// <c>dc/dc_25C.csv</c>, <c>pulsed/*.csv</c>, and one Touchstone file per bias in <c>s_25C/</c> and <c>s_85C/</c>, named
    /// by bias and drain current. The same bytes every call.
    /// </summary>
    public static void WriteRaw(string dir, GanTruth? truth = null)
    {
        var g = truth ?? Gan4x125;
        Directory.CreateDirectory(Path.Combine(dir, "pads"));
        var (open, shrt) = PadDummies(g.Freqs, g);
        File.WriteAllText(Path.Combine(dir, "pads", "open.s2p"), Touchstone(g.Freqs, open, "Open dummy: the probe pads alone (synthetic)."));
        File.WriteAllText(Path.Combine(dir, "pads", "short.s2p"), Touchstone(g.Freqs, shrt, "Short dummy: the probe pads, device ends grounded (synthetic)."));

        Directory.CreateDirectory(Path.Combine(dir, "dc"));
        File.WriteAllText(Path.Combine(dir, "dc", "dc_25C.csv"), Csv(Dc(g.DcVgs, g.DcVds, 25, g.N0, g)));

        Directory.CreateDirectory(Path.Combine(dir, "pulsed"));
        foreach (var (name, t, vq, dp) in g.PulsedSets)
            File.WriteAllText(Path.Combine(dir, "pulsed", name + ".csv"), Csv(Pulsed(g.PulsedVgs, g.PulsedVds, t, vq, dp, g)));

        foreach (double t in g.STemperatures)
        {
            string sub = Path.Combine(dir, $"s_{R(t)}C");
            Directory.CreateDirectory(sub);
            var (points, s) = SParams(g.SVgs, g.SVds, t, g.Freqs, g);
            for (int p = 0; p < points.Count; p++)
            {
                var b = points[p];
                string name = $"Vgs{F(b.Vgs)}_Vds{F(b.Vds)}_Id{(1e3 * b.Id).ToString("F6", CultureInfo.InvariantCulture)}.s2p";
                File.WriteAllText(Path.Combine(sub, name), Touchstone(g.Freqs, s[p], null));
            }
        }
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Csv(IEnumerable<Point> points)
    {
        var sb = new StringBuilder("Vgs,Vds,Ig,Id\n");
        foreach (var p in points)
            sb.Append(string.Join(',', new[] { p.Vgs, p.Vds, p.Ig, p.Id }.Select(v => v.ToString("G10", CultureInfo.InvariantCulture)))).Append('\n');
        return sb.ToString();
    }

    private static string Touchstone(double[] f, Complex[,] s, string? comment)
    {
        var sb = new StringBuilder();
        if (comment is not null) sb.Append("! ").Append(comment).Append('\n');
        sb.Append("# Hz S RI R 50\n");
        for (int k = 0; k < f.Length; k++)
        {
            sb.Append(f[k].ToString("R", CultureInfo.InvariantCulture));
            foreach (int ij in new[] { 0, 2, 1, 3 })     // S11 S21 S12 S22
                sb.Append(' ').Append(s[k, ij].Real.ToString("G12", CultureInfo.InvariantCulture))
                  .Append(' ').Append(s[k, ij].Imaginary.ToString("G12", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ── simulated-tables ──────────────────────────────────────────────────────

    public static readonly double[] TableVgs = [-2.0, -1.6, -1.2, -0.8, -0.4, 0.0, 0.4];
    public static readonly double[] TableVds = [0, 0.5, 1, 2, 4, 6, 8, 10];
    /// <summary>The low frequency the tables' Y is at.</summary>
    public const double TableFrequency = 1e9;

    /// <summary>
    /// The tables another simulator writes for <see cref="ComposedTestModels"/>' device inside the GaN network at its stock
    /// values: per bias the terminal voltages and currents, the intrinsic voltages its internal nodes carry, and Y at
    /// <see cref="TableFrequency"/> — one circuitRF grouped <c>.npy</c>.
    /// </summary>
    public static void WriteTables(string path)
    {
        int n = TableVgs.Length * TableVds.Length;
        var s = SyntheticFetData.BiasedS(SyntheticFetData.NetworkDevice, TableVgs, TableVds, [TableFrequency], ("X1.gi", "X1.di", "X1.si"));
        var y = new Complex[n * 4];
        for (int p = 0; p < n; p++)
        {
            Complex s11 = s.SAt(p, 0, 0, 0), s12 = s.SAt(p, 0, 0, 1), s21 = s.SAt(p, 0, 1, 0), s22 = s.SAt(p, 0, 1, 1);
            Complex den = (1 + s11) * (1 + s22) - s12 * s21;
            y[4 * p] = ((1 - s11) * (1 + s22) + s12 * s21) / den / 50;
            y[4 * p + 1] = -2 * s12 / den / 50;
            y[4 * p + 2] = -2 * s21 / den / 50;
            y[4 * p + 3] = ((1 + s11) * (1 - s22) + s12 * s21) / den / 50;
        }
        var point = new Axis("point", [.. Enumerable.Range(0, n).Select(i => (double)i)]);
        var ds = new DataSet();
        foreach (var (name, get) in new (string, Func<BiasPoint, double>)[]
                 {
                     ("vgs", p => p.Vgs), ("vds", p => p.Vds), ("ig", p => p.Ig), ("id", p => p.Id),
                     ("vgsi", p => p.VgsIntrinsic), ("vdsi", p => p.VdsIntrinsic),
                 })
            ds.Add(name, new DataCube([point], [.. s.Points.Select(get)]));
        var two = new Axis("port", [1, 2]);
        ds.Add("Y", new DataCube([point, new Axis("freq", [TableFrequency], "Hz"), two, two], y));
        ds.Add("freq", new DataCube([new Axis("index", [0])], [TableFrequency]));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        DataSetExporter.ExportNpy(ds, stream);
    }
}
