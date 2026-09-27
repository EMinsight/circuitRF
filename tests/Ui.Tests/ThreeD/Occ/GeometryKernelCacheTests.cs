using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Ui.Tests.ThreeD.Occ;

/// <summary>
/// brief-em3d-63 R-em3d63-6, -7, -8 against a stand-in worker (R-em3d63-9b): an unchanged tree makes no call, a
/// failing tree is remembered as failing, a crash costs one request, and only the newest preview is delivered.
/// Every assertion is a counter or a recorded request, never a timing.
/// </summary>
public sealed class GeometryKernelCacheTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "crf-kernel-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cache, recursive: true); } catch (IOException) { }
    }

    private static GeometryKernelTree Box(string name, long size = 100_000) =>
        GeometryKernelTree.From(new C3dBox { Name = name, Size = new C3dPoint3(size, size, size) }, 1000);

    [Fact]
    public void TheSameTreeTwice_SendsOneBuild_AndAfterAMemoryClear_TheDiskServesIt()
    {
        var fake = new FakeKernel();
        using var kernel = fake.Create(_cache);

        var first = kernel.Build(Box("lid"));
        var again = kernel.Build(Box("lid"));
        Assert.Single(fake.Sent("build"));
        Assert.Equal(1L, kernel.MemoryHits);
        Assert.Same(first, again);

        long sent = kernel.RequestsSent;
        kernel.ClearMemoryCache();
        var fromDisk = kernel.Build(Box("lid"));
        Assert.Equal(1L, kernel.DiskHits);
        Assert.Equal(sent, kernel.RequestsSent);
        Assert.Equal(first.Brep, fromDisk.Brep);
    }

    [Fact]
    public void AnotherKernelIdentity_NeverServesAnothersResult()
    {
        var a = new FakeKernel();
        using (var k = a.Create(_cache)) k.Build(Box("lid"));

        var b = new FakeKernel { WorkerVersion = "1.0.1-test" };
        using var other = b.Create(_cache);
        other.Build(Box("lid"));
        Assert.Equal(0L, other.DiskHits);
        Assert.Single(b.Sent("build"));
    }

    [Fact]
    public void ADiskFileThatDoesNotDecode_IsDeletedAndRebuilt()
    {
        var fake = new FakeKernel();
        using (var k = fake.Create(_cache)) k.Build(Box("lid"));
        string file = Assert.Single(Directory.GetFiles(_cache, "*.build"));
        File.WriteAllBytes(file, [1, 2, 3]);

        using var again = fake.Create(_cache);
        again.Build(Box("lid"));
        Assert.Equal(0L, again.DiskHits);
        Assert.Equal(2, fake.Sent("build").Count);
        Assert.True(new FileInfo(file).Length > 3);
    }

    [Theory]
    [InlineData("build.failed", "The geometry kernel could not build 'lid': the kernel's own text")]
    [InlineData("fillet.radius-unheard-of", "The geometry kernel could not build 'lid': the kernel's own text (an unrecognised refusal, 'fillet.radius-unheard-of')")]
    public void ARefusal_IsWordedInCircuitRfsVoice_AndRememberedWithoutAnotherCall(string code, string sentence)
    {
        var fake = new FakeKernel { RefuseBuild = code };
        using var kernel = fake.Create();

        var e = Assert.Throws<GeometryKernelException>(() => kernel.Build(Box("lid")));
        Assert.Equal(sentence, e.Message);
        Assert.Equal(GeometryKernelFailure.Refused, e.Failure);

        long sent = kernel.RequestsSent;
        Assert.Equal(sentence, Assert.Throws<GeometryKernelException>(() => kernel.Build(Box("lid"))).Message);
        Assert.Equal(sent, kernel.RequestsSent);
    }

    /// <summary>R-em3d63-7a and -6c: a crash fails that request, the next starts a fresh worker, and the crashing tree is
    /// refused again from the failed cache with no call — no crash loop.</summary>
    [Fact]
    public void ACrash_CostsOneRequest_AndTheSameTreeIsNotSentAgain()
    {
        var fake = new FakeKernel { CrashOn = r => r.Text("op") == "build" && r.Json["tree"]!["root"]!["name"]!.GetValue<string>() == "bad" };
        using var kernel = fake.Create();

        var e = Assert.Throws<GeometryKernelException>(() => kernel.Build(Box("bad")));
        Assert.Equal(GeometryKernelFailure.Crashed, e.Failure);
        Assert.Equal("The geometry kernel stopped while building 'bad' (exit code 70). Nothing was changed; it restarts for the next operation.", e.Message);

        kernel.Build(Box("good"));
        Assert.Equal(1L, kernel.WorkerRestarts);

        long sent = kernel.RequestsSent;
        Assert.Equal(e.Message, Assert.Throws<GeometryKernelException>(() => kernel.Build(Box("bad"))).Message);
        Assert.Equal(sent, kernel.RequestsSent);
    }

    /// <summary>Gate 8 (R-em3d63-8b): three rapid previews — the first is in flight, the second is dropped unsent, the
    /// third supersedes both; only the third is delivered.</summary>
    [Fact]
    public async Task ThreeRapidPreviews_OnlyTheNewestIsDelivered()
    {
        var fake = new FakeKernel();
        using var kernel = fake.Create();
        kernel.Probe();
        fake.Hold = new SemaphoreSlim(0);

        var first = kernel.RequestPreview(Box("p1"), 5);
        await WaitFor(() => fake.Sent("build").Count == 1);
        var second = kernel.RequestPreview(Box("p2"), 5);
        var third = kernel.RequestPreview(Box("p3"), 5);
        Assert.Null(await second);

        fake.Hold.Release(100);
        Assert.NotNull(await third);
        Assert.Null(await first);
        Assert.Equal(1L, kernel.PreviewsDiscarded);
        Assert.Equal([Box("p1").Hash, Box("p3").Hash], fake.Sent("build").Select(r => r["shape"]!.GetValue<string>()));
    }

    /// <summary>R-em3d63-8a/-8b: cancelling a preview kills the PREVIEW session's worker only; a model build still runs,
    /// and the cancelled tree is not remembered as failing.</summary>
    [Fact]
    public async Task CancellingAPreview_LeavesTheModelSessionAlone()
    {
        var fake = new FakeKernel();
        using var kernel = fake.Create();
        kernel.Probe();
        fake.Hold = new SemaphoreSlim(0);

        var preview = kernel.RequestPreview(Box("p1"), 5);
        await WaitFor(() => fake.Sent("build").Count == 1);
        kernel.CancelPreview();
        Assert.Null(await preview);

        fake.Hold = null;
        kernel.Build(Box("model"));
        Assert.NotNull(await kernel.RequestPreview(Box("p1"), 5));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
