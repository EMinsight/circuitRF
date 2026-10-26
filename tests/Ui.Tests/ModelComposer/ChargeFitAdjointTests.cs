using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-8 §4: on the synthetic device's multi-bias S, seen through the GaN network, the default charges —
/// ANNs trained by the adjoint method on the intrinsic capacitances — reproduce the SDD truth at held-out intrinsic
/// points: <c>∂Q/∂v</c> against the truth's <c>Cij</c>, and <c>Q</c> against the truth's charge up to the anchor.
/// </summary>
public sealed class ChargeFitAdjointTests(ITestOutputHelper output)
{
    [Fact]
    public void AdjointCharges_ReproduceTheTruth_AtHeldOutIntrinsicPoints()
    {
        var s = SyntheticFetData.BiasedS(SyntheticFetData.NetworkDevice, SyntheticFetData.CvVgs, SyntheticFetData.CvVds,
                                         SyntheticFetData.CvFreqs, ("X1.gi", "X1.di", "X1.si"));
        var (doc, data, intrinsic) = SyntheticFetData.SDocument(s, network: true);
        // One start keeps this inside the routine tier; the default three only fit closer.
        var fit = ChargeFit.Run(doc, data, intrinsic, ChargeFitSettings.Default with { Restarts = 1 });
        foreach (string line in fit.Record.Report) output.WriteLine(line);
        Assert.Equal(Constituent.AnnMethod, fit.Record.Qg.Method);
        Assert.Equal(Constituent.AnnMethod, fit.Record.Qd.Method);

        var model = ComposedCoreProgram.Compile(ChargeFit.Model(fit, null), "fit");
        var truth = SyntheticFetData.TruthCore();
        var held = SyntheticFetData.Iv(SyntheticFetData.CvHeldVgs, SyntheticFetData.CvHeldVds).Points
                                   .Select(p => (p.VgsIntrinsic, p.VdsIntrinsic)).ToList();
        var anchor = fit.Record.Anchor;
        var q0 = truth.Evaluate(new PortVoltages([anchor.Vgs, anchor.Vds])).Q;

        var dc = new List<double[]>();
        var dq = new List<double[]>();
        var cTruth = new List<double[]>();
        var qTruth = new List<double[]>();
        Span<double> gg = stackalloc double[2], gd = stackalloc double[2];
        foreach (var (vgs, vds) in held)
        {
            var t = truth.Evaluate(new PortVoltages([vgs, vds]));
            var (c11, c12, c21, c22) = ChargeFit.Capacitances(model, vgs, vds);
            double[] tc = [t.Dc[0, 0], t.Dc[0, 1], t.Dc[1, 0], t.Dc[1, 1]];
            dc.Add([c11 - tc[0], c12 - tc[1], c21 - tc[2], c22 - tc[3]]);
            cTruth.Add(tc);
            var (qg, qd) = model.Charges([vgs, vds], gg, gd);
            double[] tq = [t.Q[0] - q0[0], t.Q[1] - q0[1]];
            dq.Add([qg - tq[0], qd - tq[1]]);
            qTruth.Add(tq);
        }

        // Each capacitance's worst error over the held-out points, relative to the largest truth capacitance of its row —
        // C12 and C21 are small and cross zero, so their own range would magnify noise into a failure. Measured: 0.49 %
        // at worst (C22); the charges 0.06 % of their range.
        for (int i = 0; i < 4; i++)
        {
            double scale = cTruth.Max(c => Math.Max(Math.Abs(c[i < 2 ? 0 : 3]), Math.Abs(c[i])));
            double worst = dc.Max(e => Math.Abs(e[i])) / scale;
            output.WriteLine($"{ChargeFitRecord.Capacitances[i]}: worst held-out {worst:P3} of {scale:E3} F");
            Assert.True(worst < 0.02, $"{ChargeFitRecord.Capacitances[i]} {worst:P3}");
        }
        for (int i = 0; i < 2; i++)
        {
            double range = qTruth.Max(q => q[i]) - qTruth.Min(q => q[i]);
            double worst = dq.Max(e => Math.Abs(e[i])) / range;
            output.WriteLine($"{(i == 0 ? "Qg" : "Qd")}: worst held-out {worst:P3} of its range {range:E3} C");
            Assert.True(worst < 0.01, $"Q{i} {worst:P3}");
        }
    }
}
