// EVM of a generated LTE / NR carrier (Signal Designer's EVM tab).
//
//   E1  An unclipped, unshaped record demodulates back to its own ideal symbols: EVM ~0 for LTE (DC unused)
//       and NR. The default -80 dBc spectral shaping trims each symbol's skirts and leaks a little back
//       in-band, so a shaped record reads a small but non-zero EVM (well under 0.5 %).
//   E2  Clipping to 8 dB PAPR adds in-band noise: EVM well above zero, below the 64QAM limit, and
//       the received points sit around the ideal ones (the gain fit takes out the record's scale).
//   E3  The ideal symbols are the generator's: one row per OFDM symbol, one point per subcarrier,
//       unit mean power, and only the modulation's own levels.
//   E4  A record of the wrong length is refused rather than demodulated on the wrong grid.
//   E5  PAPR reduction is peak cancellation: at 8 dB (peak, 8x) NR 20 64QAM reads <= 2.2 % EVM, where the earlier
//       clip-and-filter read 2.44 %, and ACLR stays at the unclipped signal's level (every correction is in-band).

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class SignalEvmTests
{
    private static ModulatedSignal Make(CarrierSpec c, double? clip = null, double? shaping = -80) => SignalComposer.Generate(new SignalSpec
    {
        Carriers = [c], RecordSeconds = 0.5e-3, ClipPaprDb = clip, ShapingFloorDbc = shaping,
    });

    [Theory]
    [InlineData(CarrierStandard.Lte, 20e6, 15e3)]
    [InlineData(CarrierStandard.Nr, 20e6, 30e3)]
    public void E1_AnUnclippedRecord_ReadsZeroEvm(CarrierStandard std, double bw, double scs)
    {
        var c = new CarrierSpec { Standard = std, BandwidthHz = bw, ScsHz = scs, Modulation = QamOrder.Qam256 };
        var sig = Make(c, shaping: null);
        var r = SignalEvm.Measure(c, sig.Carriers[0].Native, sig.RecordSeconds);
        Assert.True(r.RmsPercent < 1e-6, $"EVM {r.RmsPercent} %");
        Assert.True(r.Passes);
        Assert.Equal(3.5, r.LimitPercent);

        var shaped = Make(c);
        Assert.InRange(SignalEvm.Measure(c, shaped.Carriers[0].Native, shaped.RecordSeconds).RmsPercent, 0, 0.5);
    }

    [Fact]
    public void E2_ClippingAddsInBandNoise_ThatEvmReads()
    {
        var c = new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, Modulation = QamOrder.Qam64 };
        var sig = Make(c, clip: 8);
        var r = SignalEvm.Measure(c, sig.Carriers[0].Native, sig.RecordSeconds);
        Assert.InRange(r.RmsPercent, 0.5, 8.0);
        Assert.True(r.PeakPercent > r.RmsPercent);
        Assert.InRange(r.RmsDb, -46, -22);
        double meanRx = r.Received.Average(z => z.Magnitude * z.Magnitude);
        Assert.InRange(meanRx, 0.95, 1.05);       // scaled back onto the ideal grid
    }

    [Fact]
    public void E3_TheIdealSymbols_AreTheGeneratorsGrid()
    {
        var c = new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, Modulation = QamOrder.Qam16 };
        var nm = OfdmGenerator.Numerology(c);
        var rows = OfdmGenerator.IdealSymbols(c, 1);
        Assert.Equal(nm.SymbolsPerHalfMs, rows.Length);
        Assert.All(rows, row => Assert.Equal(nm.NSc, row.Length));
        var all = rows.SelectMany(x => x).ToArray();
        Assert.InRange(all.Average(z => z.Magnitude * z.Magnitude), 0.95, 1.05);
        Assert.Equal(16, all.Select(z => (Math.Round(z.Real, 9), Math.Round(z.Imaginary, 9))).Distinct().Count());
    }

    [Fact]
    public void E5_PeakCancellation_HoldsEvmDown_AndLeavesAclrAlone()
    {
        var c = new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, Modulation = QamOrder.Qam64 };
        var clipped = Make(c, clip: 8);
        var r = SignalEvm.Measure(c, clipped.Carriers[0].Native, clipped.RecordSeconds);
        Assert.InRange(r.RmsPercent, 1.0, 2.2);
        var nm = OfdmGenerator.Numerology(c);
        double fs4 = nm.SampleRateHz * OfdmGenerator.PaprOversample;
        var x4 = Fft.ResamplePeriodic(clipped.Carriers[0].Native, clipped.Carriers[0].Native.Length * OfdmGenerator.PaprOversample);
        Assert.InRange(SignalMeasure.PaprDb(x4, 0.0), 7.95, 8.001);
        var clean = Make(c);
        var c4 = Fft.ResamplePeriodic(clean.Carriers[0].Native, clean.Carriers[0].Native.Length * OfdmGenerator.PaprOversample);
        double a1 = SignalMeasure.Aclr(x4, fs4, c, 0).WorstAcpr1, a0 = SignalMeasure.Aclr(c4, fs4, c, 0).WorstAcpr1;
        Assert.True(System.Math.Abs(a1 - a0) < 0.5, $"ACPR1 {a1:0.0} vs unclipped {a0:0.0}");
    }

    [Fact]
    public void E4_AWrongLengthRecord_IsRefused()
    {
        var c = new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 20e6 };
        var sig = Make(c);
        Assert.Throws<ArgumentException>(() => SignalEvm.Measure(c, sig.Carriers[0].Native.AsSpan(1), sig.RecordSeconds));
    }
}
