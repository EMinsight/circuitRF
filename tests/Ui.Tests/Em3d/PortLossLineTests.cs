using System.Numerics;
using CircuitRF.Design.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-dfr32-4 R-dfr32-4-2 — the run summary's per-port lost power, from a synthetic S-matrix (no solver).
public sealed class PortLossLineTests
{
    [Fact]
    public void EachPortsLoss_AtTheBandEnds_IsOneMinusItsColumnsPower_AndTheOutlierIsFlagged()
    {
        // Three ports. Column j's power: port 1 keeps 99 %, port 2 keeps 70 % (30 % lost), port 3 keeps 99.5 %.
        static Complex[,] S(double through2)
        {
            var s = new Complex[3, 3];
            s[1, 0] = Math.Sqrt(0.99);                        // 1 → 2
            s[0, 1] = Math.Sqrt(through2);                    // 2 → 1
            s[2, 2] = new Complex(0, Math.Sqrt(0.995));       // 3 reflects
            return s;
        }
        string? line = Em3dPortLoss.Line([3e9, 0.5e9, 1e9], [S(0.70), S(0.65), S(0.68)], [1, 2, 3]);
        Assert.NotNull(line);
        // The band's ends are the lowest and highest frequency, whatever their order.
        Assert.Contains("at 0.5 GHz port 1 1.0 %, port 2 35.0 %, port 3 0.5 %", line);
        Assert.Contains("at 3 GHz port 1 1.0 %, port 2 30.0 %, port 3 0.5 %", line);
        Assert.DoesNotContain("at 1 GHz", line);
        Assert.Contains("Port 2 loses several times the median port's", line);
    }
}
