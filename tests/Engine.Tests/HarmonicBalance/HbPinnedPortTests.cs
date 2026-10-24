using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// An ideal source directly on a nonlinear device's port pins that interface node at every harmonic: its row and
/// column of Z_NN are zero. The extractor used to skip the singular row in the inversion, and the node then followed
/// the device current instead of the source (an ideal 0 V source on an SDD's thermal port let it reach 22.7 K). The
/// pinned port now takes InductanceRegR in series, so the node is the source's voltage to within R·I.
/// </summary>
public class HbPinnedPortTests(ITestOutputHelper output)
{
    [Fact]
    public void ASourceOnTheDevicePort_HoldsItsNode_AtEveryHarmonic()
    {
        var (lib, tb) = new CnlReader().Read(@"
V_1Tone:Vs  a 0  Vdc=0.5  Freq=1e9  V=0.1  Phase=0
SDD:D1      a 0  Ports=1  I[1,0]=1e-2*_v1^2
analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-10
");
        var netlist = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().First(), netlist.ResolvedGlobals);
        var v = ((DataSet)new HbEngine(netlist, tb).Run(p))["V"];
        int a = Array.IndexOf(v.Axes[0].Labels!, "a");
        Complex[] want = [0.5, 0.1, 0, 0];
        for (int k = 0; k < want.Length; k++)
        {
            var got = (Complex)v[a, k];
            output.WriteLine($"V(a, k={k}) = {got:G6}");
            // the device draws ~2.6 mA through the 1 µΩ the pinned port takes in series
            Assert.True((got - want[k]).Magnitude < 1e-8, $"k={k}: V(a) should be the source's {want[k]}, got {got}");
        }
    }
}
