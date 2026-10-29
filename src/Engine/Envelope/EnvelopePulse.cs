// ================================================================
//  EnvelopePulse.cs  —  what an envelope run on a pulsed-CW source
//  measures inside its pulses (brief ENV-PCW §4)
//
//  Cubes, on the last probe (the one Pout_avg_dBm is read at):
//    Pout_t_dBm [time]               output power, floored at -100 dBm
//    PulseTop_dBm, PulseDroop_dB,
//    PulsePhaseDroop_deg, PulseRise_s,
//    PulseFall_s [pulse]             per whole pulse inside Stop
//    PulseToPulse_dB / _deg          peak-to-peak over the pulses
//  The droop window is the flat top from MeasureStart to MeasureEnd
//  (fractions of the top). Droop is start - end, so a gain falling
//  across the top is positive, and the phase droop is signed the same
//  way: the output phase (relative to the input) at the window start
//  minus at its end.
// ================================================================

using System.Globalization;
using System.Numerics;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

internal static class EnvelopePulse
{
    /// <summary>Pout_t_dBm's floor: the off-time carries no harmonic-1 output at all.</summary>
    internal const double FloorDbm = -100;

    /// <summary>The pulse of a run whose only source plays one pCW carrier; null for every other run.</summary>
    internal static PulseShape? Of(EnvelopeRunResult r)
        => r.Sources.Length == 1 && r.Sources[0].Signal.Carriers.Count == 1 &&
           r.Sources[0].Signal.Carriers[0].Spec.Standard == CarrierStandard.Pcw
            ? PulsedCw.Resolve(r.Sources[0].Signal.Carriers[0].Spec)
            : null;

    /// <summary>The whole pulses inside the run: the run time each rising edge leaves 0. Run time t is record time
    /// <paramref name="startS"/> + t.</summary>
    internal static List<double> Starts(PulseShape s, double startS, double lastT)
    {
        var list = new List<double>();
        double dur = s.End - s.Delay;
        for (long n = (long)Math.Floor((startS - s.Delay) / s.Pri); ; n++)
        {
            double t = s.Delay + n * s.Pri - startS;
            if (t + dur > lastT + 1e-12) break;
            if (t >= -1e-12) list.Add(t);
        }
        return list;
    }

    /// <summary>Adds the pulse cubes; returns the sentence the Messages pane shows.</summary>
    internal static string Add(DataSet ds, EnvelopeRunResult r, PulseShape s, double startS)
    {
        int nt = r.Time.Length, np = r.ProbeNames.Length;
        double h = r.StepS;
        var y = new Complex[nt];
        for (int t = 0; t < nt; t++) y[t] = r.Probe[t][np - 1][1];
        var d = r.Drive[0];
        double Dbm(Complex v) => Math.Max(FloorDbm, 10 * Math.Log10(Math.Max((v.Real * v.Real + v.Imaginary * v.Imaginary) / (2 * EnvelopeDataSet.MeasureOhms), 1e-300) * 1e3));
        ds.Add("Pout_t_dBm", new DataCube([new Axis("time", r.Time, "s")], [.. y.Select(Dbm)]));

        Complex At(Complex[] x, double t)
        {
            double u = Math.Clamp(t / h, 0, nt - 1);
            int i = Math.Min((int)Math.Floor(u), nt - 2);
            double a = u - i;
            return x[i] * (1 - a) + x[i + 1] * a;
        }
        double Phase(double t) => (At(y, t) / At(d, t)).Phase * 180 / Math.PI;
        static double Wrap(double deg) => deg - 360 * Math.Round(deg / 360);

        double top = s.TopEnd - s.TopStart;
        var starts = Starts(s, startS, r.Time[^1]);
        int n = starts.Count;
        string window = string.Format(CultureInfo.InvariantCulture, "droop window {0:0.##}-{1:0.##} % of the top",
                                      100 * s.MeasureStart, 100 * s.MeasureEnd);
        if (n == 0)
            return $"Pulsed CW: no whole pulse inside Stop, so nothing was measured ({PulsedCw.FormatTime(s.End - s.Delay)} from the rising edge to the end of the fall)";

        var pTop = new double[n]; var droop = new double[n]; var phDroop = new double[n];
        var rise = new double[n]; var fall = new double[n]; var phStart = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t0 = starts[i];
            double ws = t0 + (s.TopStart - s.Delay) + s.MeasureStart * top;
            double we = t0 + (s.TopStart - s.Delay) + s.MeasureEnd * top;
            pTop[i] = Dbm(At(y, ws));
            droop[i] = pTop[i] - Dbm(At(y, we));
            phStart[i] = Phase(ws);
            phDroop[i] = Wrap(phStart[i] - Phase(we));
            double next = i + 1 < n ? starts[i + 1] : r.Time[^1];
            rise[i] = Crossing(y, h, t0, ws, At(y, ws).Magnitude, rising: true);
            fall[i] = Crossing(y, h, we, next, At(y, we).Magnitude, rising: false);
        }
        var pulseAxis = new Axis("pulse", [.. Enumerable.Range(1, n).Select(i => (double)i)]);
        ds.Add("PulseTop_dBm", new DataCube([pulseAxis], pTop));
        ds.Add("PulseDroop_dB", new DataCube([pulseAxis], droop));
        ds.Add("PulsePhaseDroop_deg", new DataCube([pulseAxis], phDroop));
        ds.Add("PulseRise_s", new DataCube([pulseAxis], rise));
        ds.Add("PulseFall_s", new DataCube([pulseAxis], fall));
        ds.Add("PulseToPulse_dB", DataCube.Scalar(pTop.Max() - pTop.Min()));
        var rel = phStart.Select(p => Wrap(p - phStart[0])).ToArray();
        ds.Add("PulseToPulse_deg", DataCube.Scalar(rel.Max() - rel.Min()));
        return $"Pulsed CW: {n} pulse{(n == 1 ? "" : "s")} measured, {window}";
    }

    /// <summary>The 10-90 % time of |<paramref name="y"/>| between run times <paramref name="from"/> and
    /// <paramref name="to"/>, against the reference amplitude <paramref name="a"/>: the first upward (or downward) crossing
    /// of each level, interpolated between samples. NaN when either level is never crossed.</summary>
    private static double Crossing(Complex[] y, double h, double from, double to, double a, bool rising)
    {
        int i0 = Math.Max(0, (int)Math.Floor(from / h)), i1 = Math.Min(y.Length - 1, (int)Math.Ceiling(to / h));
        double First(double level)
        {
            for (int i = i0; i < i1; i++)
            {
                double m0 = y[i].Magnitude, m1 = y[i + 1].Magnitude;
                bool hit = rising ? m0 < level && m1 >= level : m0 > level && m1 <= level;
                if (hit) return (i + (level - m0) / (m1 - m0)) * h;
            }
            return double.NaN;
        }
        double t10 = First(0.1 * a), t90 = First(0.9 * a);
        return rising ? t90 - t10 : t10 - t90;
    }
}
