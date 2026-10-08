using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Optimization;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>YA-1 R-ya1-3 (overview D10): a schematic corner's kit axis selection is resolved at extraction by
/// the function Simulate resolves the design's own selections with, and the <c>.cnl</c> corner line binds the
/// result; a selection that no longer resolves is reported in that function's words, not dropped.</summary>
public sealed class CornerExtractionTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-yc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IReadOnlyList<WorkspaceCornerAxis> _axes;

    public CornerExtractionTests()
    {
        string models = Path.Combine(_ws, "kit", "models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "capCorners.lib"), """
            .LIB cap_typ
            .param carea = 1.5E-15
            .param cpara = 1.0
            .ENDL cap_typ

            .LIB cap_wcs
            .param carea = 1.65E-15
            .param cpara = 1.1
            .ENDL cap_wcs
            """);
        _axes = WorkspaceCorners.From(_ws,
        [
            new CwsPdkRef
            {
                Path = "kit", Provider = "TestKit",
                Corners = [new CwsCornerAxis { AxisId = "models/capCorners.lib", DisplayName = "capCorners", Options = ["cap_typ", "cap_wcs"] }],
            },
        ]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best effort */ }
    }

    private SchematicEditModel Design(string section)
    {
        var m = new SchematicEditModel();
        m.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "50", "Ohm")));
        m.Tuning = new TuningSetup
        {
            Corners =
            [
                new CornerDefinition
                {
                    Name = "Wcs", Temp = "85",
                    AxisSelections = new() { [_axes[0].Key] = section },
                    Values = new() { ["R1.R"] = "47 Ohm" },
                },
            ],
        };
        return m;
    }

    private NetExtractor.ExtractionResult Extract(SchematicEditModel m)
        => NetExtractor.Extract(m, "tb", cornerBinder: (s, p) => WorkspaceCorners.BindingsFor(_axes, s, p));

    [Fact]
    public void AKitSelection_ExtractsToTheBindingsCornerSelectionsProduce()
    {
        var problems = new List<string>();
        var expected = WorkspaceCorners.BindingsFor(_axes, new Dictionary<string, string> { [_axes[0].Key] = "cap_wcs" }, problems)
            .Select(v => (v.Name, Value: string.IsNullOrEmpty(v.Unit) ? v.Expression : $"{v.Expression} {v.Unit}"))
            .Append(("R1.R", "47 Ohm"))
            .ToList();
        Assert.Empty(problems);
        Assert.Contains(expected, b => b.Item1 == "carea");

        var result = Extract(Design("cap_wcs"));
        var corner = Assert.Single(result.TestBench.Tuning!.Corners);
        Assert.Null(corner.AxisSelections);
        Assert.Equal(expected, corner.Values.Select(kv => (kv.Key, kv.Value)));

        // The .cnl line binds exactly that, and never names a kit file.
        string cnl = CnlWriter.Write(result.TestBench);
        Assert.DoesNotContain("capCorners", cnl);
        var (_, tb) = new CnlReader().Read(cnl);
        Assert.Equal(expected, tb.Tuning!.Corners.Single().Values.Select(kv => (kv.Key, kv.Value)));
    }

    [Fact]
    public void AStaleSelection_IsReportedInSimulatesWords_NotDropped()
    {
        var stale = new List<string>();
        WorkspaceCorners.BindingsFor(_axes, new Dictionary<string, string> { [_axes[0].Key] = "cap_gone" }, stale);

        var result = Extract(Design("cap_gone"));

        Assert.Contains($"Corner 'Wcs': {Assert.Single(stale)}", result.Conflicts);
        var corner = Assert.Single(result.TestBench.Tuning!.Corners);
        Assert.Equal("47 Ohm", corner.Values["R1.R"]);
    }
}
