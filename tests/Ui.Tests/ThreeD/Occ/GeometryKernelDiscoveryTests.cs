using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Ui.Tests.ThreeD.Occ;

/// <summary>
/// brief-em3d-63 gate 1 — discovery over fake file trees (R-em3d63-1). The order is environment variable, the
/// shipped folder beside the application, then the source tree's own build; the first CANDIDATE stops the walk, and
/// a named worker that does not work is reported, never replaced.
/// </summary>
public sealed class GeometryKernelDiscoveryTests : IDisposable
{
    private const string Rid = "osx-arm64";
    private static readonly string[] Shipped = ["osx-arm64", "linux-x64"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-kernel-discovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>A tree: an application folder, maybe inside a source checkout, maybe with a <c>geometry-kernel/</c>.</summary>
    private (string BaseDir, string? Env) Tree(string env, string folder, string source)
    {
        string checkout = Path.Combine(_root, "checkout");
        string baseDir = source == "none" ? Path.Combine(_root, "installed", "app") : Path.Combine(checkout, "src", "Ui", "bin");
        Directory.CreateDirectory(baseDir);
        if (source != "none") Touch(Path.Combine(checkout, "circuitRF.slnx"));
        if (source == "built") Touch(Path.Combine(checkout, "tools", "geometry-worker", "build", Rid, "geometry-kernel", "geometry-worker"));
        if (folder != "none") Directory.CreateDirectory(Path.Combine(baseDir, "geometry-kernel"));
        if (folder == "worker") Touch(Path.Combine(baseDir, "geometry-kernel", "geometry-worker"));
        string? e = env switch
        {
            "present" => Touch(Path.Combine(_root, "dev", "geometry-worker")),
            "missing" => Path.Combine(_root, "dev", "not-there"),
            _ => null,
        };
        return (baseDir, e);
    }

    [Theory]
    // The environment variable is the only candidate once set — over a shipped folder, and even when it names nothing.
    [InlineData("present", "worker", "built", true,  "EnvironmentVariable")]
    [InlineData("missing", "worker", "built", true,  "Missing")]
    // The shipped folder, when it exists, is the answer — working or not; the source tree is never consulted.
    [InlineData("none",    "worker", "built", true,  "BesideTheApplication")]
    [InlineData("none",    "empty",  "built", true,  "Missing")]
    // No folder: a source tree's own build, or NotBuilt; outside a source tree, the install is damaged.
    [InlineData("none",    "none",   "built", true,  "SourceTree")]
    [InlineData("none",    "none",   "unbuilt", true, "NotBuilt")]
    [InlineData("none",    "none",   "none",  true,  "Missing")]
    // A RID that does not ship it never looks beside the application; a developer's own build still works.
    [InlineData("none",    "worker", "none",  false, "NotShippedOnThisPlatform")]
    [InlineData("none",    "worker", "built", false, "SourceTree")]
    public void Discovery_FollowsTheOrder_AndStopsAtTheFirstCandidate(string env, string folder, string source, bool shipped, string expected)
    {
        var (baseDir, envValue) = Tree(env, folder, source);
        var loc = GeometryKernelLocator.Locate(envValue, baseDir, Rid, shipped ? Shipped : ["linux-x64"], windows: false);

        string actual = loc.Found ? loc.Route!.Value.ToString() : loc.Absence!.Value.ToString();
        Assert.Equal(expected, actual);
        Assert.Equal(loc.Found, loc.Reason.Length == 0);
        if (env == "missing") Assert.Contains(GeometryKernel.EnvironmentVariable, loc.Action, StringComparison.Ordinal);
    }

    /// <summary>R-em3d63-1b: an environment-named worker that fails its start is Broken — and the shipped copy beside
    /// the application is never started in its place.</summary>
    [Fact]
    public void ABrokenNamedWorker_IsReported_NeverReplaced()
    {
        var (baseDir, envValue) = Tree("present", "worker", "built");
        var started = new List<string>();
        using var kernel = new GeometryKernel(new GeometryKernelOptions
        {
            Locate = () => GeometryKernelLocator.Locate(envValue, baseDir, Rid, Shipped, windows: false),
            Start = path =>
            {
                started.Add(path);
                throw new System.ComponentModel.Win32Exception("exec format error");
            },
            DiskCache = false,
            LogPath = Path.Combine(_root, "start.log"),
        });

        var cap = kernel.Probe();

        Assert.False(cap.Available);
        Assert.Equal(GeometryKernelAbsence.Broken, cap.Absence);
        Assert.Equal([Path.GetFullPath(envValue!)], started);
        Assert.Contains($"Unset {GeometryKernel.EnvironmentVariable}", cap.Action, StringComparison.Ordinal);
    }
}
