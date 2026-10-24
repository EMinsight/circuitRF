// The pulsed-CW carrier (brief ENV-PCW §1, gates 1 and 2).
//
//   P1  The netlist form parses, writes back to itself, survives the .cmod JSON, and labels as width / PRI; every
//       time unit means the same thing.
//   P2  One refusal per rule, each a sentence naming the field.
//   P3  The record: exactly 0 off, exactly 1 on the top, 10-90 % edges of Rise / Fall and a 50 %-50 % width of PW
//       to one sample, the analytic power duty, one PRI long, and a bandwidth taken from the edges.

using System;
using System.Linq;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class PulsedCwTests
{
    private const string Text = "PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0";

    [Fact]
    public void P1_ParsesRoundTripsAndLabels()
    {
        var c = SignalSpec.ParseCarrier(Text);
        Assert.Equal(CarrierStandard.Pcw, c.Standard);
        Assert.Equal((100e-6, 50e-6, 0.5e-6, 0.5e-6, 10e-6),
                     (c.PriSeconds!.Value, c.PulseWidthSeconds!.Value, c.RiseSeconds!.Value, c.FallSeconds!.Value, c.DelaySeconds!.Value));
        Assert.Equal(Text, SignalSpec.FormatCarrier(c));
        Assert.Equal(c, SignalSpec.ParseCarrier(SignalSpec.FormatCarrier(c)));
        var spec = new SignalSpec { Carriers = [c] };
        Assert.Equal(c, SignalSpec.FromJson(spec.ToJson()).Carriers.Single());
        Assert.Equal("pCW 50 µs / 100 µs", c.Label);
        Assert.Equal("pCW 50 µs / 100 µs", spec.Summary);

        foreach (var pw in new[] { "50e-6", "5e-5", "0.05ms", "50000ns", "50µs" })
            Assert.Equal(50e-6, SignalSpec.ParseCarrier($"PCW;PRI=100us;PW={pw}").PulseWidthSeconds);

        // an unstated Delay stays unstated, and centres the pulse
        var centred = SignalSpec.ParseCarrier("PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us");
        Assert.Null(centred.DelaySeconds);
        Assert.DoesNotContain("Delay", SignalSpec.FormatCarrier(centred));
        var s = PulsedCw.Resolve(centred);
        Assert.Equal(s.Delay, s.Pri - s.End, 12);
    }

    [Theory]
    [InlineData("PCW;PRI=100us;PW=100us", "PW (100 µs) must be shorter than PRI")]
    [InlineData("PCW;PRI=100us;PW=10us;Rise=6us", "Rise (6 µs) is longer than half of PW")]
    [InlineData("PCW;PRI=100us;PW=10us;Fall=-1ns", "Fall must be a positive time")]
    [InlineData("PCW;PRI=100us;PW=50us;Delay=60us", "past the PRI")]
    [InlineData("PCW;PRI=100us;PW=50us;Delay=0", "Delay must be greater than 0")]
    [InlineData("PCW;PRI=100.5us;PW=50us", "not a whole number of samples")]
    [InlineData("PCW;PRI=100us;PW=50us;MeasureStart=0.9;MeasureEnd=0.1", "MeasureStart and MeasureEnd")]
    [InlineData("CW;PRI=100us", "PRI belongs to a PCW carrier")]
    [InlineData("PCW;PW=5 sec", "is not a time")]
    public void P2_RefusesWithTheField(string text, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => SignalSpec.ParseCarrier(text));
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void P2_RefusesAPulseBesideAnotherCarrier_OrClipped()
    {
        var pcw = SignalSpec.ParseCarrier(Text);
        var ex = Assert.Throws<ArgumentException>(() => SignalComposer.Generate(new SignalSpec { Carriers = [pcw, new CarrierSpec { Standard = CarrierStandard.Cw }] }));
        Assert.Contains("only carrier", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SignalComposer.Generate(new SignalSpec { Carriers = [pcw], ClipPaprDb = 8 }));
    }

    [Fact]
    public void P3_TheRecordIsTheStatedPulse()
    {
        var c = SignalSpec.ParseCarrier(Text);
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        var rec = sig.Carriers.Single();
        var s = PulsedCw.Resolve(c);
        double fs = rec.NativeRateHz, dt = 1 / fs;
        Assert.Equal(30.72e6, fs);                                   // >= 10 samples in a 0.5 µs edge
        Assert.Equal(100e-6, sig.RecordSeconds);
        Assert.Equal((int)Math.Round(100e-6 * fs), rec.Native.Length);
        Assert.Equal(PulsedCw.BandwidthHz(s), rec.Spec.BandwidthHz);
        Assert.Equal(1.0, rec.RelativePower);

        var a = rec.Native.Select(v => v.Real).ToArray();
        Assert.All(rec.Native, v => Assert.Equal(0.0, v.Imaginary));
        for (int i = 0; i < a.Length; i++)
        {
            double t = i * dt;
            if (t <= s.Delay || t >= s.End) Assert.Equal(0.0, a[i]);
            else if (t >= s.TopStart && t <= s.TopEnd) Assert.Equal(1.0, a[i]);
        }
        double Cross(double level, bool up, int from = 0)
        {
            for (int i = from; i + 1 < a.Length; i++)
                if (up ? a[i] < level && a[i + 1] >= level : a[i] > level && a[i + 1] <= level)
                    return (i + (level - a[i]) / (a[i + 1] - a[i])) * dt;
            return double.NaN;
        }
        int mid = (int)(0.5 * (s.TopStart + s.TopEnd) * fs);
        Assert.Equal(s.Rise, Cross(0.9, true) - Cross(0.1, true), dt);
        Assert.Equal(s.Fall, Cross(0.1, false, mid) - Cross(0.9, false, mid), dt);
        Assert.Equal(s.Width, Cross(0.5, false, mid) - Cross(0.5, true), dt);
        Assert.Equal(s.PowerDuty, a.Average(x => x * x), 1e-6);
    }
}
