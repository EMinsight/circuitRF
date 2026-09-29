// ================================================================
//  ThermalExamplesTests.cs — brief-em3d-81's gates for the two shipped thermal examples, Thermal Die to Heatsink and Thermal
//  Channel vs Surface (the third, Output Wires, is brief 85's).
//
//  Brief 70's discipline: each example's `expected-numbers.json` is the ONE source of every number its README and the Thermal
//  user page print — each value's Readme text must appear verbatim in both — and the Benchmark-tier gate re-runs the shipped
//  setups and holds their recorded values. `check` must be clean as a process; the vendor scan covers every file and the page.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ThermalExamplesTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-th81-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    private const string Heatsink = "Thermal Die to Heatsink", Channel = "Thermal Channel vs Surface";
    private const string Page = "docs/user/src/reference/thermal.md";

    // ── gate 1: check is clean ──────────────────────────────────────────────────────────────────────

    /// <summary><c>check --json</c> on each workspace, as a process: no error and no warning.</summary>
    [Theory]
    [InlineData(Heatsink)]
    [InlineData(Channel)]
    public void Gate1_CheckIsClean(string example)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(ThermalExamplesTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in new[] { "check", Root(example), "--json" }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        using var doc = JsonDocument.Parse(outTask.GetAwaiter().GetResult());
        var found = doc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() is "error" or "warning")
            .Select(d => d.GetProperty("message").GetString()!).ToList();
        Assert.Empty(found);
    }

    // ── gate 2: one source ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Heatsink, "README.md")]
    [InlineData(Heatsink, Page)]
    [InlineData(Channel, "README.md")]
    [InlineData(Channel, Page)]
    public void Gate2_TheReadmeAndThePageQuoteEveryNumberInTheFile(string example, string page)
    {
        string path = page == Page ? Path.Combine(PalaceBackendTests.RepoRoot(), Page) : Path.Combine(Root(example), page);
        string text = File.ReadAllText(path);
        var quoted = Readmes(JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(example), "expected-numbers.json"))).RootElement).ToList();
        Assert.NotEmpty(quoted);
        Assert.All(quoted, q => Assert.Contains(q, text));
    }

    /// <summary>Every string under a <c>Readme</c> key, at any depth.</summary>
    private static IEnumerable<string> Readmes(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == "Readme")
                {
                    if (p.Value.ValueKind == JsonValueKind.String) yield return p.Value.GetString()!;
                    else foreach (var s in p.Value.EnumerateArray()) yield return s.GetString()!;
                }
                else foreach (string s in Readmes(p.Value)) yield return s;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) foreach (string s in Readmes(x)) yield return s;
    }

    /// <summary>The external check the page states: the interface material in one dimension.</summary>
    [Fact]
    public void TheInterfaceMaterialsClosedForm_IsTheRecordedValue()
    {
        double t = 85 + 11 * 100e-6 / (3 * 10e-3 * 8e-3);
        var n = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Heatsink), "expected-numbers.json"))).RootElement;
        Assert.Equal(t, n.GetProperty("External")[0].GetProperty("Expected").GetDouble(), 5);
        Assert.Equal(t, n.GetProperty("Shipped").GetProperty("Recorded")[2].GetProperty("T_interface").GetDouble(), 4);
    }

    // ── gate 3: no vendor names ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_NoVendorNameIsInTheExamplesOrThePage()
    {
        var files = new[] { Heatsink, Channel }.SelectMany(x => Directory.EnumerateFiles(Root(x), "*", SearchOption.AllDirectories))
            .Append(Path.Combine(PalaceBackendTests.RepoRoot(), Page));
        foreach (string file in files)
        {
            var words = Regex.Matches(File.ReadAllText(file).ToLowerInvariant(), @"[a-z][a-z0-9]*").Select(w => w.Value).ToList();
            for (int i = 0; i < words.Count; i++)
            {
                Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i]), Docs.WsProbeDocsTests.BannedDigests);
                if (i + 1 < words.Count)
                    Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i] + " " + words[i + 1]), Docs.WsProbeDocsTests.BannedDigests);
            }
        }
    }

    // ── the runs: a regression test of the recorded numbers ─────────────────────────────────────────

    /// <summary>
    /// The shipped setups re-run in process — Die to Heatsink's three power points (about a minute), One Finger (minutes, order 2)
    /// and Eight Fingers — each holding its recorded values to the file's tolerance (K, or K/W). A deliberate change re-records
    /// the file and the pages quoting it.
    /// </summary>
    [GmshFact]
    [Trait("Category", "Benchmark")]
    public void TheShippedSetups_ReproduceTheirRecordedNumbers()
    {
        var moved = new List<string>();
        void Hold(string what, double got, double want, double tol)
        {
            output.WriteLine($"{what}: {got:F4} (recorded {want:F4})");
            if (Math.Abs(got - want) > tol) moved.Add($"{what}: {got:F4}, recorded {want:F4} (± {tol})");
        }

        var hs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Heatsink), "expected-numbers.json"))).RootElement.GetProperty("Shipped");
        var r = Run(Heatsink, "Die to Heatsink", "Steady");
        double tol = hs.GetProperty("Tolerance").GetDouble();
        int k = 0;
        foreach (var p in hs.GetProperty("Recorded").EnumerateArray())
        {
            Hold($"die top at {p.GetProperty("Pdiss")} W", r.Data!["thermal.T:die_top:max"].RealValues[k], p.GetProperty("DieTop").GetDouble(), tol);
            Hold($"Rth_jc at {p.GetProperty("Pdiss")} W", r.Data["Rth_jc"].RealValues[k], p.GetProperty("Rth_jc").GetDouble(), tol / 10);
            Hold($"Rth_ja at {p.GetProperty("Pdiss")} W", r.Data["Rth_ja"].RealValues[k], p.GetProperty("Rth_ja").GetDouble(), tol / 10);
            k++;
        }

        var ch = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Channel), "expected-numbers.json"))).RootElement;
        var one = ch.GetProperty("OneFinger");
        r = Run(Channel, "One Finger", "Steady");
        Hold("channel", r.Data!["thermal.T:channel_T:max"].RealValues[0], one.GetProperty("Recorded").GetProperty("ChannelMax").GetDouble(), one.GetProperty("Tolerance").GetDouble());
        Hold("offset", r.Data["dT_ch_ir"].RealValues[0], one.GetProperty("Recorded").GetProperty("dT_ch_ir").GetDouble(), one.GetProperty("Tolerance").GetDouble());
        var eight = ch.GetProperty("EightFingers");
        r = Run(Channel, "Eight Fingers", "Array");
        Hold("centre finger", r.Data!["T_centre"].RealValues[0], eight.GetProperty("Recorded").GetProperty("T_centre").GetDouble(), eight.GetProperty("Tolerance").GetDouble());
        Hold("edge finger", r.Data["T_edge"].RealValues[0], eight.GetProperty("Recorded").GetProperty("T_edge").GetDouble(), eight.GetProperty("Tolerance").GetDouble());

        Assert.True(moved.Count == 0, "thermal results moved from the recorded ones:\n" + string.Join("\n", moved));
    }

    private EmRunResult Run(string example, string cell, string setup)
    {
        string path = Path.Combine(Root(example), cell, "3d", cell + ".c3d");
        var doc = C3dPersistence.LoadFromFile(path);
        var run = EmRunService.RunThreeDView(C3dSetups.ForRun(C3dSetups.Select(doc, setup).Setup!, path), doc, path,
                                             Path.Combine(Root(example), ".cws"), Path.Combine(_tmp, example));
        Assert.True(run.Status == EmRunStatus.Ok, $"{cell} {setup}: {run.Error}");
        return run;
    }

    private static string Root(string example)
        => Path.Combine(ExampleWorkspaces.ResolveRoot(PalaceBackendTests.RepoRoot()) ?? throw new InvalidOperationException("no examples/"), example);
}
