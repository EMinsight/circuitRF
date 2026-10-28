using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// GSGPAD (brief-review-6 follow-up) through the engine, the path a user's circuit takes: it IS MLIN — half the
/// signal pad, then the line — so it matches the two MLINs in series, wired by hand.
/// </summary>
public class GsgPadSParamTests
{
    private static Complex[,] SAt(string cnl, double freqHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [freqHz])["S"];
        return new[,] { { (Complex)s[0, 0, 0], (Complex)s[0, 0, 1] }, { (Complex)s[0, 1, 0], (Complex)s[0, 1, 1] } };
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(5e9)]
    [InlineData(40e9)]
    public void AGsgPad_IsHalfItsPadThenItsLine_AsTwoMlins(double f)
    {
        const double w = 72e-6, l = 300e-6, padW = 80e-6, padL = 90e-6;
        const string sub = "H=100e-6 T=3e-6 Er=12.9 Sigma=4.1e7 TanD=0.0005";

        string gsg = $"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            GSGPAD:G1 a b W={F(w)} L={F(l)} PadW={F(padW)} PadL={F(padL)} Pitch=150e-6 NVia=1 {sub}
            Port:P2 b 0 Num=2 Z=50 Ohm
            """;
        string hand = $"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            MLIN:Pad a m W={F(padW)} L={F(padL / 2)} {sub}
            MLIN:Line m b W={F(w)} L={F(l)} {sub}
            Port:P2 b 0 Num=2 Z=50 Ohm
            """;

        var a = SAt(gsg, f);
        var b = SAt(hand, f);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }
}
