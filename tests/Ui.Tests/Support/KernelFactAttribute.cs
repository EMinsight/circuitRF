using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-em3d-63 R-em3d63-9a — like <see cref="FactAttribute"/>, but SKIPS with a reason when the geometry kernel
/// is not available: <i>"No geometry kernel — &lt;Reason&gt; &lt;Action&gt;"</i>. A fresh clone and a CI job that did not
/// build the worker are green, and say what they did not test. <see cref="FixtureFactAttribute"/>'s shape.
/// </summary>
public sealed class KernelFactAttribute : FactAttribute
{
    public KernelFactAttribute()
    {
        if (KernelForTests.SkipReason is { } reason) Skip = reason;
    }
}

/// <summary>The <see cref="TheoryAttribute"/> twin of <see cref="KernelFactAttribute"/>.</summary>
public sealed class KernelTheoryAttribute : TheoryAttribute
{
    public KernelTheoryAttribute()
    {
        if (KernelForTests.SkipReason is { } reason) Skip = reason;
    }
}

/// <summary>The capability the attributes read, probed once per test process by discovery itself — and kernels of the
/// tests' own, each with its own cache, so no test shares counters or a disk cache with another.</summary>
internal static class KernelForTests
{
    private static readonly Lazy<GeometryKernelCapability> Probed = new(() =>
    {
        using var k = new GeometryKernel(new GeometryKernelOptions { DiskCache = false });
        return k.Probe();
    });

    public static GeometryKernelCapability Capability => Probed.Value;

    public static string? SkipReason => Capability.Available ? null : $"No geometry kernel — {Capability.Reason} {Capability.Action}";

    /// <summary>The environment switch that lets the worker answer the test-only <c>crash</c> and <c>sleep</c> requests.</summary>
    public static readonly IReadOnlyDictionary<string, string> TestOps = new Dictionary<string, string> { ["CRF_GEOMETRY_WORKER_TEST"] = "1" };

    /// <summary>A kernel of this test's own. <paramref name="cacheDir"/> null keeps the cache in memory only.</summary>
    public static GeometryKernel New(string? cacheDir = null, bool testOps = false, GeometryKernelDeadlines? deadlines = null) =>
        new(new GeometryKernelOptions
        {
            DiskCache = cacheDir is not null,
            CacheDirectory = cacheDir,
            Start = path => new GeometryKernelProcessWorker(path, testOps ? TestOps : null),
            Deadlines = deadlines ?? new GeometryKernelDeadlines(),
            LogPath = Path.Combine(Path.GetTempPath(), "crf-geometry-kernel-tests.log"),
        });
}
