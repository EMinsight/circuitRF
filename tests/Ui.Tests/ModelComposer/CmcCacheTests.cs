using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>brief-modelcomposer-3 R-mc3-4/6: the cache answers a reopen without reading a source, re-imports a
/// changed one, keeps working without a missing one, refuses without both, and survives a cancelled rebuild.</summary>
public sealed class CmcCacheTests
{
    private const string Iv = "Vg,Vd,Id\n-2,5,10 mA\n-2,10,12 mA\n-1,5,80 mA\n";

    private static (string Cmc, CmcDocument Doc) Fixture()
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc3-cache-").FullName;
        File.WriteAllText(Path.Combine(dir, "iv.csv"), Iv);
        var doc = new CmcDocument
        {
            Kind = CmcKind.Active,
            DataSets =
            [
                new CmcDataSet
                {
                    Name = "DC", Kind = DataSetKind.DcIv, Sources = ["iv.csv"],
                    Import = new ImportSettings
                    {
                        Format = ImportSettings.Csv,
                        Columns = [new ColumnMap("Vg", "vgs"), new ColumnMap("Vd", "vds"), new ColumnMap("Id", "id")],
                    },
                    Exclusions = [new PointExclusion("iv.csv:3", "repeat")],
                },
            ],
        };
        string cmc = Path.Combine(dir, "fet.cmc");
        CmcFile.Write(cmc, doc);
        return (cmc, doc);
    }

    [Fact]
    public void AReopen_ReadsNoSource()
    {
        var (cmc, doc) = Fixture();
        var first = new CmcCache(cmc);
        var opened = first.Open(doc);
        Assert.True(first.SourceReads > 0);
        Assert.Equal("DC: 3 point(s), Vgs -2 to -1 V, Vds 5 to 10 V.", Assert.Single(opened.Status));

        var again = new CmcCache(cmc);
        var reopened = again.Open(doc);
        Assert.Equal(0, again.SourceReads);
        Assert.Empty(reopened.Status);
        Assert.Equal(opened.Data["DC"].Points, reopened.Data["DC"].Points);
        Assert.Equal(0.012, reopened.Data["DC"].Points[1].Id, 15);
        Assert.True(reopened.Data["DC"].Points[1].Excluded);         // the user's exclusion, from the .cmc
    }

    /// <summary>A source copied or checked out keeps its bytes and takes a new timestamp, which rewrites the index — and the
    /// rewrite once carried only the intrinsic folder across, deleting the fitted arrays (MC-16).</summary>
    [Fact]
    public void ATouchedSource_KeepsEveryStepsFolder()
    {
        var (cmc, doc) = Fixture();
        var cache = new CmcCache(cmc);
        cache.Open(doc);
        string arrays = Path.Combine(cache.Folder, CurrentFit.CacheFolder, "fit.npy");
        Directory.CreateDirectory(Path.GetDirectoryName(arrays)!);
        File.WriteAllText(arrays, "weights");
        File.SetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(cmc)!, "iv.csv"), DateTime.UtcNow.AddHours(1));

        new CmcCache(cmc).Open(doc);

        Assert.Equal("weights", File.ReadAllText(arrays));
    }

    [Fact]
    public void AChangedSource_IsReimported()
    {
        var (cmc, doc) = Fixture();
        new CmcCache(cmc).Open(doc);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(cmc)!, "iv.csv"), Iv.Replace("80 mA", "95.5 mA"));

        var opened = new CmcCache(cmc).Open(doc);
        Assert.StartsWith("DC: iv.csv changed; re-imported:", Assert.Single(opened.Status));
        Assert.Equal(0.0955, opened.Data["DC"].Points[2].Id, 15);
    }

    [Fact]
    public void ADeletedSource_LoadsFromTheCacheWithAWarning_AndIsRefusedWithoutOne()
    {
        var (cmc, doc) = Fixture();
        new CmcCache(cmc).Open(doc);
        File.Delete(Path.Combine(Path.GetDirectoryName(cmc)!, "iv.csv"));

        var cache = new CmcCache(cmc);
        var opened = cache.Open(doc);
        Assert.Equal("DC: iv.csv is missing; using the cached data.", Assert.Single(opened.Warnings));
        Assert.Equal(3, opened.Data["DC"].Count);

        Directory.Delete(cache.Folder, recursive: true);
        var ex = Assert.Throws<DataImportException>(() => new CmcCache(cmc).Open(doc));
        Assert.Equal("iv.csv", ex.SourceName);
    }

    [Fact]
    public void ACancelledRebuild_LeavesTheOldCache()
    {
        var (cmc, doc) = Fixture();
        var cache = new CmcCache(cmc);
        cache.Open(doc);
        var before = Snapshot(cache.Folder);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(cmc)!, "iv.csv"), Iv.Replace("80 mA", "1 A"));

        // Cancelled once the re-import has begun — Cancel pressed while the progress bar shows it.
        using var cts = new CancellationTokenSource();
        var rc = new RunControl { Token = cts.Token, Progress = new Immediate(_ => cts.Cancel()) };
        Assert.ThrowsAny<OperationCanceledException>(() => new CmcCache(cmc).Open(doc, rc));

        Assert.Equal(before, Snapshot(cache.Folder));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(cmc)!));       // no half-written sibling left behind
    }

    private sealed class Immediate(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }

    private static Dictionary<string, byte[]> Snapshot(string folder) =>
        Directory.GetFiles(folder).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
}
