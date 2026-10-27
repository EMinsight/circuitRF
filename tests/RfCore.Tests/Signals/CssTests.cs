// The CSS carrier: a chirp-spread-spectrum packet.
//
//   C1  The netlist form parses (BW defaults to 125 kHz), writes back to itself, survives the .cmod JSON and labels;
//       refusals name the field.
//   C2  The packet: 44.25 symbols of constant envelope; the preamble sweeps up from -BW/2; and the payload reads back
//       as the PN9 symbols it was made from, by a receiver's dechirp: a symbol s starts s / 2^SF of the way up the
//       sweep (a chirp shifted the other way reads back as 2^SF - s).
//   C3  It is the source's only carrier; the envelope steps at its native 8 BW, below the ladder's base.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class CssTests
{
    private const string Text = "CSS;SF=7;BW=125kHz;Off=0;P=0";

    [Fact]
    public void C1_ParsesRoundTripsLabelsAndRefuses()
    {
        var c = SignalSpec.ParseCarrier(Text);
        Assert.Equal((CarrierStandard.Css, 7, 125e3), (c.Standard, c.SpreadingFactor!.Value, c.BandwidthHz));
        Assert.Equal(Text, SignalSpec.FormatCarrier(c));
        Assert.Equal(c, SignalSpec.FromJson(new SignalSpec { Carriers = [c] }.ToJson()).Carriers.Single());
        Assert.Equal("CSS SF7 125 kHz", c.Label);
        Assert.Equal(125e3, SignalSpec.ParseCarrier("CSS").BandwidthHz);

        foreach (var (text, fragment) in new[]
        {
            ("CSS;SF=13", "SF must be 5 to 12"),
            ("CSS;BW=5MHz", "BW must be a chirp bandwidth"),
            ("LTE;SF=7", "SF belongs to a CSS carrier"),
        })
            Assert.Contains(fragment, Assert.Throws<FormatException>(() => SignalSpec.ParseCarrier(text)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void C2_APacketOfChirps_WhosePayloadDechirpsBack()
    {
        var s = Css.Resolve(SignalSpec.ParseCarrier(Text));
        var (x, f) = Css.Generate(s);
        int ns = s.Chips * Css.SamplesPerChip;
        Assert.Equal(44.25 * ns, x.Length);
        Assert.Equal(45.312e-3, s.RecordSeconds, 12);
        Assert.All(x, v => Assert.Equal(1, v.Magnitude, 12));
        Assert.Equal(-62.5e3, f[0]);                                           // the preamble starts at the bottom
        Assert.Equal(62.5e3 - 125e3 / ns, f[ns - 1], 6);                        // and sweeps to the top

        // the receiver: one sample per chip, times the conjugate base up-chirp, then the FFT peak is the symbol
        var up = Css.Generate(s).Samples.Take(ns).Where((_, i) => i % Css.SamplesPerChip == 0).ToArray();
        int start = (int)(12.25 * ns);
        var payload = Css.Payload(7);
        for (int k = 0; k < payload.Length; k++)
        {
            var seg = Enumerable.Range(0, s.Chips).Select(m => x[start + k * ns + m * Css.SamplesPerChip] * Complex.Conjugate(up[m])).ToArray();
            var spec = Fft.Forward(seg);
            int peak = Enumerable.Range(0, spec.Length).MaxBy(i => spec[i].Magnitude);
            Assert.Equal(payload[k], peak);
        }
    }

    [Fact]
    public void C3_OnlyCarrier_StepsAtItsOwnRate()
    {
        var c = SignalSpec.ParseCarrier(Text);
        Assert.Contains("only carrier", Assert.Throws<ArgumentException>(() =>
            SignalComposer.Generate(new SignalSpec { Carriers = [c, new CarrierSpec()] })).Message);
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        Assert.Equal(1e6, SignalRate.AtLeast(2 * 2.5 * 125e3, [sig]));
    }
}
