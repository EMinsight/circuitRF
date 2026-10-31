using System.Globalization;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// Brief MC-30: a Table on gridded intrinsic data is fitted on the data's own grid (D1) — exact at the samples, where the
/// re-grid is not — and a grid with one excluded point falls back and says which site broke it; an RBF re-grid above its
/// point limit is refused before anything is allocated, and one under it cancels inside the factor (D2).
/// </summary>
public sealed class TableOnDataGridTests(ITestOutputHelper output)
{
    private static readonly double[] Vgs = [.. Enumerable.Range(0, 11).Select(i => -2.0 + 0.25 * i)];
    private static readonly double[] Vds = [0, 0.25, 0.5, 1, 1.5, 2, 3, 4, 5, 6, 8, 10];

    /// <summary>The generating function: smooth, nonlinear in both inputs, so no spline through a re-grid reproduces it.</summary>
    private static double Truth(double vgs, double vds) => 0.05 * (1 + Math.Tanh(1.7 * (vgs + 1))) * Math.Tanh(0.9 * vds);

    private static (CmcDocument, Dictionary<string, NormalisedData>, IntrinsicResult) Gridded(bool dropOne)
    {
        var points = new List<BiasPoint>();
        foreach (double g in Vgs)
            foreach (double d in Vds)
            {
                if (dropOne && g == Vgs[4] && d == Vds[5]) continue;   // what an excluded point leaves
                points.Add(new BiasPoint(string.Create(CultureInfo.InvariantCulture, $"{g}_{d}"), g, d, double.NaN, Truth(g, d)));
            }
        var iv = new NormalisedData { Points = points };
        var set = new CmcDataSet
        {
            Name = "sim", Kind = DataSetKind.SimulatedTables, Sources = ["sim.npy"],
            Conditions = new DataConditions { ReferencePlane = ReferencePlane.Intrinsic },
            Import = new ImportSettings { Format = ImportSettings.Npy, Arrays = new Dictionary<string, string> { ["vgs"] = "vgs", ["vds"] = "vds", ["id"] = "id" } },
        };
        var ds = IntrinsicExtraction.Compute(set, iv, PadDeembedding.Load(PadsSettings.None, "."), null, null, null, []);
        return (new CmcDocument { Kind = CmcKind.Active, DataSets = [set] },
                new Dictionary<string, NormalisedData> { ["sim"] = iv },
                new IntrinsicResult(new Dictionary<string, RfCore.Data.DataSet> { ["sim"] = ds }, []));
    }

    private static readonly CurrentFitSettings Table = CurrentFitSettings.Default with
    {
        Id = new CurrentConstituentSettings { Method = CurrentMethod.Table, Regrid = RegridSource.RBF },
    };

    [Fact]
    public void AGrid_IsTheTable_ExactAtItsSamples_WhereTheRegridIsNot()
    {
        var (doc, data, intrinsic) = Gridded(dropOne: false);
        var direct = CurrentFit.Run(doc, data, intrinsic, Table).Record;
        foreach (string line in direct.Report) output.WriteLine(line);
        Assert.Contains(direct.Report, l => l.StartsWith("Id: Table on the data's own 11 × 12 grid", StringComparison.Ordinal));
        double exact = direct.Errors["Id"].TrainRelative;

        var (doc2, data2, intrinsic2) = Gridded(dropOne: true);
        var regrid = CurrentFit.Run(doc2, data2, intrinsic2, Table).Record;
        foreach (string line in regrid.Report) output.WriteLine(line);
        double sampled = regrid.Errors["Id"].TrainRelative;
        output.WriteLine($"train RMS / range: direct {exact:E2}, re-grid {sampled:E2}");

        Assert.True(exact < 1e-12, $"{exact:E2}");
        Assert.True(sampled > 1e-6, $"{sampled:E2}");
        Assert.Contains("Id: the data is a 11 × 12 grid missing 1 site(s) — (vgs -1, vds 2) — so it is re-gridded.", regrid.Report);
    }

    [Fact]
    public void RbfAboveTheLimit_IsRefusedBeforeAnyAllocation()
    {
        int n = Regrid.RbfPointLimit + 1;
        var points = Enumerable.Range(0, n).Select(k => ((double)k, (double)(k % 7))).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<RbfTooLargeException>(() => Regrid.RbfWithinLimit(points, new double[n]));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{ex.Message} ({allocated:N0} bytes allocated)");
        Assert.Contains("Re-grid through the ANN.", ex.Message);
        Assert.True(allocated < Regrid.RbfBytes(n) / 1000, $"{allocated:N0} bytes");
    }

    [Fact]
    public void ACancelledFactor_ReturnsNothing()
    {
        var points = Enumerable.Range(0, 2000).Select(k => (k % 50 / 50.0, k / 50 / 40.0)).ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<double, double, double>? f = null;
        Assert.ThrowsAny<OperationCanceledException>(
            () => f = Regrid.RbfWithinLimit(points, [.. points.Select(p => p.Item1 * p.Item2)], new RunControl { Token = cts.Token }));
        Assert.Null(f);
    }
}
