// ================================================================
//  YieldExampleTests.cs — the shipped Yield example (brief-yield-13 R-ya13-4): a bandpass filter's yield
//  against loosened specs, an amplifier failing one generated corner and then optimized across all of
//  them, and a divider centred from ~70 % to above 95 %.
//
//  Every run is the CLI as a process on the shipped .csch at the example's own settings and seed, so
//  what is asserted is what the README says a reader gets. Results go to a temporary folder — never
//  beside the shipped design.
// ================================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Statistics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Tests.Optimization;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class YieldExampleTests
{
    /// <summary>The bandpass at the Optimization example's equiripple point, ±2 % parts: 86.4 % of 500 trials
    /// meet the loosened specs, every failure is the passband's, and the 80 % target is met. The saved display
    /// is the one-click yield display over the result the panel writes beside the schematic.</summary>
    [Fact]
    public void Bandpass_YieldIsInsideTheStatedInterval_AndTheSavedDisplayIsTheOneClickOne()
    {
        var y = Run(0, "yield", "estimate", Csch("BandpassYield"), "-o", Out("bp.yield.npy"), "--json")["yield"]!;
        var overall = y["yield"]!;
        Assert.Equal(500, overall["counted"]!.GetValue<int>());
        Assert.InRange(overall["yield"]!.GetValue<double>(), 0.83, 0.90);
        Assert.True(y["targetMet"]!.GetValue<bool>());

        var goals = y["goals"]!.AsArray().ToDictionary(g => g!["name"]!.GetValue<string>(), g => g!["yield"]!["yield"]!.GetValue<double>());
        Assert.Equal(["PassbandSpec", "StopHighSpec"], goals.Keys);             // the use=opt goals are not specs
        Assert.Equal(overall["yield"]!.GetValue<double>(), goals["PassbandSpec"]);
        Assert.Equal(1.0, goals["StopHighSpec"]);

        string cdd = Path.Combine(Path.GetDirectoryName(Csch("BandpassYield"))!, "BandpassYield.yield.cdd");
        Assert.Equal(StatisticalRun.ResultPathFor(Csch("BandpassYield")), Path.ChangeExtension(cdd, ".npy"));
        var ds = StatisticalRun.Create(PreparedCircuit.FromSchematic(Csch("BandpassYield"))).Run().Data!;
        const string source = "../BandpassYield/schematic/BandpassYield.yield.npy";
        var expected = YieldDisplayPreset.Build(ds, source);
        var shipped = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd), DataDisplayJson.Options)!;
        Assert.Equal(source, shipped.SelectedDataSource);
        Assert.Equal(expected.Tabs.Single().Plots.Select(p => p.CustomTitle), shipped.Tabs.Single().Plots.Select(p => p.CustomTitle));
        Assert.All(shipped.Tabs.Single().Plots.SelectMany(p => p.Traces), t => Assert.Equal(source, t.SourcePath));
    }

    /// <summary>The amplifier: of the six generated corners only hot at low supply fails, on gain; optimized
    /// across every corner (and the statistical one saved from the Monte Carlo) both goals are met, with that
    /// corner binding.</summary>
    [Fact]
    public void Amplifier_FailsOnlyHotAtLowSupply_AndTheOptimizerAcrossCornersMeetsEveryGoal()
    {
        var c = Run(3, "yield", "corners", Csch("AmplifierCorners"), "-o", Out("amp.corners.npy"), "--json")["corners"]!;
        var corners = c["corners"]!.AsArray();
        Assert.Equal(["nominal", "tm40_Vdd3p0", "tm40_Vdd3p6", "t25_Vdd3p0", "t25_Vdd3p6", "t85_Vdd3p0", "t85_Vdd3p6", "WorstGain"],
                     corners.Select(k => k!["name"]!.GetValue<string>()));
        Assert.True(corners.Single(k => k!["name"]!.GetValue<string>() == "WorstGain")!["statistical"]!.GetValue<bool>());
        var failing = Assert.Single(corners, k => !k!["pass"]!.GetValue<bool>())!;
        Assert.Equal("t85_Vdd3p0", failing["name"]!.GetValue<string>());
        Assert.Equal(["Gain"], failing["goals"]!.AsArray().Where(g => !g!["met"]!.GetValue<bool>()).Select(g => g!["name"]!.GetValue<string>()));

        var (exit, stdout, stderr) = OptCli.Run("opt", Csch("AmplifierCorners"), "--json");
        Assert.True(exit == 0, stderr);
        var r = OptCli.Report(stdout);
        Assert.Equal("goalsMet", r["outcome"]!.GetValue<string>());
        var gain = r["goals"]!.AsArray().Single(g => g!["name"]!.GetValue<string>() == "Gain")!;
        Assert.Equal("t85_Vdd3p0", gain["corner"]!.GetValue<string>());
        Assert.Equal(8, gain["perCorner"]!.AsArray().Count);
    }

    /// <summary>The divider: centering R1 takes the verified yield from ~70 % to above 95 % on the same 1000 fresh
    /// trials, with the two intervals apart.</summary>
    [Fact]
    public void Divider_CenteringRaisesTheVerifiedYieldAboveTheTarget()
    {
        var c = Run(0, "yield", "center", Csch("DividerCentering"), "-o", Out("div.yield.npy"), "--json")["center"]!;
        var v = c["verification"]!;
        Assert.Equal(1000, v["trials"]!.GetValue<int>());
        Assert.InRange(v["start"]!["yield"]!.GetValue<double>(), 0.65, 0.78);
        Assert.True(v["best"]!["yield"]!.GetValue<double>() >= 0.95, v["sentence"]!.GetValue<string>());
        Assert.False(v["withinOverlap"]!.GetValue<bool>());
        Assert.InRange(double.Parse(c["bestValues"]!["R1.R"]!.GetValue<string>().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture), 1000, 1070);
    }

    private readonly string _out = OptCli.Dir();

    private string Out(string name) => Path.Combine(_out, name);

    private static JsonNode Run(int expectedExit, params string[] args)
    {
        var (exit, stdout, stderr) = OptCli.Run(args);
        Assert.True(exit == expectedExit, $"exit {exit}: {stderr}");
        return JsonNode.Parse(stdout)!["result"]!;
    }

    internal static string Csch(string cell) => Path.Combine(Root(), cell, "schematic", cell + ".csch");

    private static string Root()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return Path.Combine(dir, "examples", "Yield");
        throw new InvalidOperationException("repo root not found");
    }
}
