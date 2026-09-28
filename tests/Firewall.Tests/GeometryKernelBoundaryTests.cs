using System.Text.RegularExpressions;

namespace CircuitRF.Firewall.Tests;

/// <summary>
/// brief-em3d-63 gates 3 and 9 — the managed client's boundaries, beside <see cref="OcctBoundaryTests"/>.
/// <list type="number">
/// <item>No assembly P/Invokes the geometry worker (the OCCT libraries are <see cref="OcctBoundaryTests"/>' already):
/// it is a separate process, spoken to over a pipe.</item>
/// <item><c>src/Design/ThreeD/Occ/</c> does not reference the device-worker namespace, so nobody later "reuses" the kit
/// transport and inherits a consent prompt for circuitRF's own binary (R-em3d63-1c, D15).</item>
/// <item>The words <i>geometry kernel</i> appear in no string in <c>src/Ui</c> or <c>src/Cli</c>: the absence is worded
/// in ONE place, <c>GeometryKernel</c> (R-em3d63-3b) — enforced, not hoped for.</item>
/// </list>
/// Each has a planted-violation twin: the same scanner pointed at something that breaks the rule.
/// </summary>
public sealed class GeometryKernelBoundaryTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-kernelwall-" + Guid.NewGuid().ToString("N")[..10]);

    public GeometryKernelBoundaryTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_tmp, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static readonly Regex Worker = new(@"geometry[-_]?worker", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── 1. the worker is a process, never a library ──────────────────────────────────────────

    [Fact]
    public void NoAssemblyOrSourceFileImportsTheWorker()
    {
        var found = Boundary.AssemblyViolations(SolverBoundaryTests.CircuitRfAssemblies(), Worker).ToList();
        found.AddRange(Boundary.InteropViolations(SolverBoundaryTests.SourceFiles(Path.Combine(SolverBoundaryTests.RepoRoot(), "src"), "*.cs"), Worker));
        Assert.True(found.Count == 0, "The geometry worker is imported as a library:\n" + string.Join("\n", found));
    }

    [Fact]
    public void Planted_AnInteropDeclarationNamingTheWorkerIsCaught()
        => Assert.NotEmpty(Boundary.InteropViolations([Write("Planted.cs", """[DllImport("geometry-worker")] static extern int Build();""")], Worker));

    // ── 2. the client is not the kit transport ───────────────────────────────────────────────

    private static readonly Regex KitTransport = new(@"CircuitRF\.Core\.Devices\.External|\bDeviceWorker\w*", RegexOptions.Compiled);

    private static IReadOnlyList<string> KitTransportUses(IEnumerable<string> files) =>
        [.. files.SelectMany(f => KitTransport.Matches(StripCode(File.ReadAllText(f))).Select(m => $"{f}: {m.Value}"))];

    [Fact]
    public void TheClientDoesNotReferenceTheDeviceWorkerNamespace()
    {
        string occ = Path.Combine(SolverBoundaryTests.RepoRoot(), "src", "Design", "ThreeD", "Occ");
        var files = Directory.GetFiles(occ, "*.cs");
        Assert.NotEmpty(files);
        var found = KitTransportUses(files);
        Assert.True(found.Count == 0, "The geometry client uses the kit device-worker transport:\n" + string.Join("\n", found));
    }

    [Fact]
    public void Planted_AClientUsingTheKitTransportIsCaught()
        => Assert.NotEmpty(KitTransportUses([Write("Planted.cs", "using CircuitRF.Core.Devices.External;\nvar t = ProcessDeviceWorkerTransport.Start(p);")]));

    // ── 3. one place words the absence ───────────────────────────────────────────────────────

    private static readonly Regex Words = new(@"geometry\s+kernel", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IReadOnlyList<string> Wording(IEnumerable<string> files) =>
        [.. files.SelectMany(f =>
        {
            string text = File.ReadAllText(f);
            text = f.EndsWith(".axaml", StringComparison.Ordinal) ? Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline) : StripCode(text);
            return Words.Matches(text).Select(m => $"{f}: {m.Value}");
        })];

    [Fact]
    public void NoFileInUiOrCliWordsTheKernelItself()
    {
        string root = SolverBoundaryTests.RepoRoot();
        var files = new[] { "Ui", "Cli" }.SelectMany(p => new[] { "*.cs", "*.axaml" }
            .SelectMany(pattern => SolverBoundaryTests.SourceFiles(Path.Combine(root, "src", p), pattern)));
        var found = Wording(files);
        Assert.True(found.Count == 0,
            "These words the geometry kernel's absence (or its name) outside GeometryKernel; take the sentence from " +
            "GeometryKernel.NeedsKernel / SettingsStatus instead:\n" + string.Join("\n", found));
    }

    [Theory]
    [InlineData("Planted.cs", """ToolTip = "Boolean needs the Geometry Kernel, which is missing.";""")]
    [InlineData("Planted.axaml", """<TextBlock Text="Geometry kernel: not found"/>""")]
    public void Planted_AnotherSentenceNamingTheKernelIsCaught(string name, string text)
        => Assert.NotEmpty(Wording([Write(name, text)]));

    /// <summary>C# with its comments removed, so a scan cannot be satisfied — or tripped — by prose.</summary>
    private static string StripCode(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"(?m)^\s*///?.*$|(?<=[;{})\s])//[^\n]*", "");
}
