// The DBPSK carrier: differential BPSK with raised-cosine reversals, a UNB uplink.
//
//   D1  The netlist form parses, writes back to itself, survives the .cmod JSON and labels; refusals name the field.
//   D2  The record: a 0 reverses the phase and a 1 keeps it, the envelope dips to zero only at a reversal, unit mean
//       power, the data twice over when one period would end reversed, and 99 % of the power within +-0.75 Rb.
//   D3  It is the source's only carrier and is not clipped; the envelope steps at its own few kHz, not at the
//       1.92 MHz the LTE / NR ladder starts from.

using System;
using System.Linq;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class DbpskTests
{
    private const string Text = "DBPSK;Rb=100bps;Data=PN9;Off=0;P=0";

    [Fact]
    public void D1_ParsesRoundTripsLabelsAndRefuses()
    {
        var c = SignalSpec.ParseCarrier(Text);
        Assert.Equal((CarrierStandard.Dbpsk, 100.0, "PN9"), (c.Standard, c.BitRateBps!.Value, c.Data));
        Assert.Equal(200, c.BandwidthHz);                                  // the pulse's main lobe, 2 Rb
        Assert.Equal(Text, SignalSpec.FormatCarrier(c));
        Assert.Equal(c, SignalSpec.FromJson(new SignalSpec { Carriers = [c] }.ToJson()).Carriers.Single());
        Assert.Equal("DBPSK 100 b/s PN9", c.Label);

        foreach (var (text, fragment) in new[]
        {
            ("DBPSK;Rb=0", "Rb must be a positive bit rate"),
            ("DBPSK;Data=PN7", "Data must be PN9, PN15 or a pattern"),
            ("DBPSK;Dev=1kHz", "DEV belongs to an FSK carrier"),
        })
            Assert.Contains(fragment, Assert.Throws<FormatException>(() => SignalSpec.ParseCarrier(text)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void D2_ZeroReverses_EnvelopeDipsOnlyThere_ClosesAndStaysNarrow()
    {
        const int sps = Dbpsk.SamplesPerBit;
        var s = Dbpsk.Resolve(SignalSpec.ParseCarrier("DBPSK;Rb=100bps;Data=PN9"));
        Assert.Equal(1022, s.Bits);                                          // PN9 has 255 0s: one period would end reversed
        Assert.Equal(4, Dbpsk.Resolve(SignalSpec.ParseCarrier("DBPSK;Data=1100")).Bits);

        var x = Dbpsk.Generate(s);
        Assert.Equal(1022 * sps, x.Length);
        Assert.Equal(1.0, x.Average(v => v.Magnitude * v.Magnitude), 12);
        Assert.All(x, v => Assert.Equal(0, v.Imaginary));
        double peak = x.Max(v => Math.Abs(v.Real));
        var bits = Fsk.DataBits("PN9");
        for (int k = 0; k < s.Bits; k++)
        {
            double centre = x[k * sps + sps / 2].Real, before = x[((k - 1 + s.Bits) % s.Bits) * sps + sps / 2].Real;
            Assert.Equal(bits[k % bits.Length] == 0, Math.Sign(centre) != Math.Sign(before));   // a 0 reverses
            Assert.Equal(peak, Math.Abs(centre), 12);                                           // full amplitude mid-bit
            double edge = Math.Abs(x[k * sps].Real);                                           // the bit's leading edge
            Assert.Equal(bits[k % bits.Length] == 0 ? 0 : peak, edge, 12);                     // zero only at a reversal
        }

        var spec = Fft.Forward(x);
        double total = spec.Sum(v => v.Magnitude * v.Magnitude), inBand = 0;
        for (int i = 0; i < spec.Length; i++)
        {
            double f = (i <= spec.Length / 2 ? i : i - spec.Length) * Dbpsk.NativeRateHz(s) / spec.Length;
            if (Math.Abs(f) <= 75) inBand += spec[i].Magnitude * spec[i].Magnitude;
        }
        Assert.True(inBand / total > 0.99, $"{100 * inBand / total:0.###} % within +-75 Hz");
    }

    [Fact]
    public void D3_OnlyCarrier_NotClipped_StepsAtItsOwnRate()
    {
        var c = SignalSpec.ParseCarrier(Text);
        Assert.Contains("only carrier", Assert.Throws<ArgumentException>(() =>
            SignalComposer.Generate(new SignalSpec { Carriers = [c, new CarrierSpec()] })).Message);
        Assert.Contains("not clipped", Assert.Throws<ArgumentException>(() =>
            SignalComposer.Generate(new SignalSpec { Carriers = [c], ClipPaprDb = 8 })).Message);

        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        Assert.Equal(10.22, sig.RecordSeconds, 12);
        Assert.Equal(3200, SignalRate.AtLeast(2 * 2.5 * 200, [sig]));         // its native rate, not the 1.92 MHz ladder
        Assert.Equal(3200, sig.SuggestedCompositeRateHz());
    }
}
