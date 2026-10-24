using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-7 §4: on the synthetic device's DC I–V (one temperature, no pulses), seen through the GaN network,
/// the default ANN currents reproduce the SDD truth at held-out intrinsic points — bias points between the training
/// grid's lines, never trained on — evaluated through the <c>.cmodel</c> the record writes, as the device runs it.
/// </summary>
public sealed class CurrentFitAnnTests(ITestOutputHelper output)
{
    private static readonly Lazy<(CmcDocument Doc, CurrentFitResult Fit)> Fitted = new(() =>
    {
        var (doc, data, intrinsic) = SyntheticFetData.IvDocument(SyntheticFetData.Iv(SyntheticFetData.IvVgs, SyntheticFetData.IvVds));
        // One start per network, and 2000 iterations for the gate current, keep this inside the routine tier; the defaults
        // (three starts, 6000 iterations) only fit closer. The tolerances below are for these settings.
        var settings = CurrentFitSettings.Default with { Restarts = 1, Ig = new CurrentConstituentSettings { MaxIterations = 2000 } };
        return (doc, CurrentFit.Run(doc, data, intrinsic, settings));
    });

    /// <summary>The held-out points' intrinsic voltages, from the truth's own internal nodes.</summary>
    internal static IReadOnlyList<(double Vgs, double Vds)> HeldOut() =>
        [.. SyntheticFetData.Iv(SyntheticFetData.HeldVgs, SyntheticFetData.HeldVds).Points.Select(p => (p.VgsIntrinsic, p.VdsIntrinsic))];

    [Fact]
    public void AnnCurrents_ReproduceTheTruth_AtHeldOutIntrinsicPoints()
    {
        var fit = Fitted.Value.Fit;
        Assert.Equal(Constituent.AnnMethod, fit.Record.Id.Method);
        Assert.Equal(Constituent.AnnMethod, fit.Record.Ig.Method);
        foreach (var (k, e) in fit.Record.Errors)
            output.WriteLine($"{k}: train {e.TrainRelative:P3} held-out {e.HeldOutRelative:P3} ({e.HeldOutCount} points)");
        foreach (string line in fit.Record.Report) output.WriteLine(line);
        // R-mc29-4: the extrapolation line names the law and each current's conductance at its lowest and highest vgs edge.
        string edges = Assert.Single(fit.Record.Report, l => l.StartsWith("Extrapolation (local", StringComparison.Ordinal));
        foreach (string key in (string[])["Id", "IgGs", "IgGd"])
            Assert.Matches($@"\b{key} \S+ S at \S+ V, \S+ S at \S+ V", edges);

        var model = SyntheticFetData.Core(fit);
        var truth = SyntheticFetData.TruthCore();
        var held = HeldOut();
        var pairs = held.Select(p => (Model: model.Evaluate(new PortVoltages([p.Vgs, p.Vds])),
                                      Truth: truth.Evaluate(new PortVoltages([p.Vgs, p.Vds])))).ToList();

        // Id: the drain terminal current, relative to its own value or the weighting floor — what relative weighting
        // with a floor promises — worst over the held-out points.
        double idRange = pairs.Max(x => Math.Abs(x.Truth.I[1]));
        double idFloor = CurrentFitSettings.DefaultIdFloor * idRange;
        double Rel((NonlinearResult Model, NonlinearResult Truth) x) =>
            Math.Abs(x.Model.I[1] - x.Truth.I[1]) / Math.Max(Math.Abs(x.Truth.I[1]), idFloor);
        double idWorst = pairs.Max(Rel);
        foreach (var k in Enumerable.Range(0, held.Count).OrderByDescending(k => Rel(pairs[k])).Take(3))
            output.WriteLine($"  Id at ({held[k].Vgs:F3}, {held[k].Vds:F3}): {pairs[k].Model.I[1]:E4} vs {pairs[k].Truth.I[1]:E4}");

        // Ig: the gate terminal current in decades, wherever the truth conducts above the fit's own floor I0.
        double floor = CurrentFitSettings.DefaultIgFloor * pairs.Max(x => Math.Abs(x.Truth.I[0]));
        var conducting = pairs.Where(x => Math.Abs(x.Truth.I[0]) > floor).ToList();
        double igWorst = conducting.Max(x => Math.Abs(Math.Log10(Math.Abs(x.Model.I[0]) / Math.Abs(x.Truth.I[0]))));
        double idOfRange = pairs.Max(x => Math.Abs(x.Model.I[1] - x.Truth.I[1])) / idRange;
        output.WriteLine($"Id worst {idOfRange:P3} of {idRange:E3} A, {idWorst:P3} of its own value; Ig worst {igWorst:F3} decades over {conducting.Count} points");

        // Measured: 1.5 % of the range, 4.1 % of its own value, 0.13 decades over 4 points.
        Assert.True(idOfRange < 0.02, $"Id {idOfRange:P3} of the range");
        Assert.True(idWorst < 0.05, $"Id {idWorst:P3} of its own value");
        Assert.True(conducting.Count >= 4, "not vacuous: held-out points where the gate conducts");
        Assert.True(igWorst < 0.2, $"Ig {igWorst:F3} decades");
    }

    [Fact]
    public void TheRecord_RoundTripsThroughTheDocument()
    {
        var (doc, fit) = Fitted.Value;
        var back = CmcFile.Parse(System.Text.Encoding.UTF8.GetString(CmcFile.ToBytes(CurrentFit.Apply(doc, fit))));
        var record = CurrentFit.Record(back)!;
        Assert.Equal(CmodelFile.ConstituentJson(fit.Record.Id), CmodelFile.ConstituentJson(record.Id));
        Assert.Equal(CmodelFile.ConstituentJson(fit.Record.Ig), CmodelFile.ConstituentJson(record.Ig));
        Assert.Equal(fit.Record.Gx, record.Gx);
        Assert.Equal(fit.Record.Arrays, record.Arrays);
        Assert.Equal(NetworkFit.StaleSteps(back), []);
    }
}
